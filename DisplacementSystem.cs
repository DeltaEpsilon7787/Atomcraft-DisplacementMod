using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Atomcraft;
using Godot;
using HarmonyLib;
using static HarmonyLib.Code;
using Label = System.Reflection.Emit.Label;

namespace DeltaEpsilon.Displacement;

public static class DisplacementSystem
{
    static readonly ConcurrentBag<(int, int, Reaction)> ReactionOpportunities = [];
    const uint MaxDisplacementLength = 256;

    static readonly Dictionary<(int, int), (int, int)> LeftRemapping = new(8)
    {
        { (-1, -1), (-1, 0) },
        { (-1, 0), (-1, 1) },
        { (-1, 1), (0, 1) },
        { (0, 1), (1, 1) },
        { (1, 1), (1, 0) },
        { (1, 0), (1, -1) },
        { (1, -1), (0, -1) },
        { (0, -1), (-1, -1) }
    };

    static readonly Dictionary<(int, int), (int, int)> RightRemapping = new(8)
    {
        { (-1, -1), (0, -1) },
        { (0, -1), (1, -1) },
        { (1, -1), (1, 0) },
        { (1, 0), (1, 1) },
        { (1, 1), (0, 1) },
        { (0, 1), (-1, 1) },
        { (-1, 1), (-1, 0) },
        { (-1, 0), (-1, -1) },
    };

    [ThreadStatic] static byte[]? _pixelCounter;

    public static void RecordReactionOpportunity(int x, int y, Reaction reaction) {
        ReactionOpportunities.Add((x, y, reaction));
    }

    public static Reaction? TryFindReaction(BaseMaterial mat, int cx, int cy, SimField field, int tick) {
        if (mat.Reactions == null || mat.Reactions.Length == 0)
        {
            return null;
        }

        _pixelCounter ??= new byte[short.MaxValue + 1];

        var m0 = field.Get(cx - 1, cy - 1);
        var m1 = field.Get(cx - 1, cy + 0);
        var m2 = field.Get(cx - 1, cy + 1);
        var m3 = field.Get(cx + 0, cy - 1);
        var m4 = field.Get(cx + 0, cy + 0);
        var m5 = field.Get(cx + 0, cy + 1);
        var m6 = field.Get(cx + 1, cy - 1);
        var m7 = field.Get(cx + 1, cy + 0);
        var m8 = field.Get(cx + 1, cy + 1);

        var is1 = Materials.IsStatic(m1);
        var is3 = Materials.IsStatic(m3);
        var is5 = Materials.IsStatic(m5);
        var is7 = Materials.IsStatic(m7);

        if (is1 && is3) m0 = -2;
        if (is1 && is5) m2 = -2;
        if (is3 && is7) m6 = -2;
        if (is5 && is7) m8 = -2;

        Span<short> matArray = [m0, m1, m2, m3, m4, m5, m6, m7, m8];

        for (var i = 0; i < 9; i++)
        {
            var m = matArray[i];
            if (m >= 0) _pixelCounter[m]++;
        }

        var tempHere = field.GetHeatmap(cx, cy);
        var rng = RNG.Roll(cx, cy, tick);

        Reaction? result = null;
        foreach (Reaction reaction in mat.Reactions)
        {
            if (reaction.Blender ||
                reaction.Temperature.HasValue && tempHere < reaction.Temperature ||
                reaction.Probability != 0 && rng % reaction.Probability != 0 ||
                reaction.Electrolysis && !Simulation.IsElectrolysisAtPosition(cx, cy))
            {
                continue;
            }

            var isValid = (
                (reaction.InputTypeCount <= 0 || _pixelCounter[reaction.InputTypes[0]] >= reaction.InputAmounts[0]) &&
                (reaction.InputTypeCount <= 1 || _pixelCounter[reaction.InputTypes[1]] >= reaction.InputAmounts[1]) &&
                (reaction.InputTypeCount <= 2 || _pixelCounter[reaction.InputTypes[2]] >= reaction.InputAmounts[2]) &&
                (reaction.InputTypeCount <= 3 || _pixelCounter[reaction.InputTypes[3]] >= reaction.InputAmounts[3]) &&
                (reaction.CatalystTypeCount <= 0 || _pixelCounter[reaction.CatalystTypes[0]] >= reaction.CatalystAmounts[0]) &&
                (reaction.CatalystTypeCount <= 1 || _pixelCounter[reaction.CatalystTypes[1]] >= reaction.CatalystAmounts[1])
            );
            if (!isValid) continue;

            // Can also be made `yield return` if we want to support multiple reactions
            result = reaction;
            break;
        }

        for (var i = 0; i < 9; i++)
        {
            var m = matArray[i];
            if (m >= 0)
            {
                _pixelCounter[m] = 0;
            }
        }

        return result;
    }

    static List<(int, int)[]>? TryFindDisplacements(
        int x, int y,
        Span<(int, int)> displacementPositions,
        SimField field, int tick
    ) {
        List<(int, int)[]> paths = new(displacementPositions.Length);

        foreach (var (fx, fy) in displacementPositions)
        {
            var (dx, dy) = (fx - x, fy - y);
            var (cx, cy) = (fx, fy);

            Span<(int, int)> path = new (int, int)[MaxDisplacementLength];
            path[0] = (cx, cy);
            var pathHead = 1;

            var r = 4;
            do
            {
                (cx, cy) = (cx + dx, cy + dy);
                path[pathHead++] = (cx, cy);

                if (Math.Max(Math.Abs(cx - x), Math.Abs(cy - y)) <= 1)
                {
                    // Displacement failed, we've intersected with protected area.
                    return null;
                }

                var matId = field.Get(cx, cy);
                if (Materials.IsStatic(matId))
                {
                    // Hit a wall
                    return null;
                }

                switch (matId)
                {
                    case -1:
                        // Successful displacement
                        paths.Add(path[..pathHead].ToArray());
                        goto onSuccess;
                    case -2:
                        // Reached OoB
                        return null;
                }

                var doesDirShift = RNG.Roll(cx, cy, tick) % r == 0;
                if (!doesDirShift) continue;

                r++;
                var choice = RNG.Roll(cx, cy, tick + 1) % 2;
                (dx, dy) = choice == 0 ? LeftRemapping[(dx, dy)] : RightRemapping[(dx, dy)];
            } while (pathHead < MaxDisplacementLength);

            return null;

            onSuccess: ;
        }

        return paths;
    }

    static void PerformReaction(int cx, int cy, SimField? field, int tick, Reaction reaction) {
        if (field == null)
        {
            return;
        }

        var primaryMat = field.Get(cx + 0, cy + 0);
        Reaction? reactionTest = TryFindReaction(primaryMat.ToMaterial(), cx, cy, field, tick);
        if (reactionTest != reaction)
        {
            // Revalidation of the reaction failed
            return;
        }

        var m0 = field.Get(cx - 1, cy - 1);
        var m1 = field.Get(cx - 1, cy + 0);
        var m2 = field.Get(cx - 1, cy + 1);
        var m3 = field.Get(cx + 0, cy - 1);
        var m4 = field.Get(cx + 0, cy + 0);
        var m5 = field.Get(cx + 0, cy + 1);
        var m6 = field.Get(cx + 1, cy - 1);
        var m7 = field.Get(cx + 1, cy + 0);
        var m8 = field.Get(cx + 1, cy + 1);

        var is1 = Materials.IsStatic(m1);
        var is3 = Materials.IsStatic(m3);
        var is5 = Materials.IsStatic(m5);
        var is7 = Materials.IsStatic(m7);

        if (is1 && is3) m0 = -2;
        if (is1 && is5) m2 = -2;
        if (is3 && is7) m6 = -2;
        if (is5 && is7) m8 = -2;

        Span<short> matIds = [m0, m1, m2, m3, m4, m5, m6, m7, m8];

        Span<(short, int)> inputIdsAmts =
        [
            reaction.InputTypeCount > 0 ? (reaction.InputTypes[0], reaction.InputAmounts[0]) : ((short)0, 0),
            reaction.InputTypeCount > 1 ? (reaction.InputTypes[1], reaction.InputAmounts[1]) : ((short)0, 0),
            reaction.InputTypeCount > 2 ? (reaction.InputTypes[2], reaction.InputAmounts[2]) : ((short)0, 0),
            reaction.InputTypeCount > 3 ? (reaction.InputTypes[3], reaction.InputAmounts[3]) : ((short)0, 0),
        ];
        Span<(short, int)> catalystIdsAmts =
        [
            reaction.CatalystTypeCount > 0 ? (reaction.CatalystTypes[0], reaction.CatalystAmounts[0]) : ((short)0, 0),
            reaction.CatalystTypeCount > 1 ? (reaction.CatalystTypes[1], reaction.CatalystAmounts[1]) : ((short)0, 0),
        ];

        Span<(int, int)> neighbors =
        [
            (cx - 1, cy - 1),
            (cx - 1, cy + 0),
            (cx - 1, cy + 1),
            (cx + 0, cy - 1),
            (cx + 0, cy + 0),
            (cx + 0, cy + 1),
            (cx + 1, cy - 1),
            (cx + 1, cy + 0),
            (cx + 1, cy + 1),
        ];

        // We exploit Shuffle's property that for same length vectors and tick, the reordering will be the same 
        matIds.Shuffle(tick);
        neighbors.Shuffle(tick);

        Span<(int, int)> inputSlots = stackalloc (int, int)[9];
        var inputSlotsHead = 0;
        Span<(int, int)> airSlots = stackalloc (int, int)[9];
        var airSlotsHead = 0;
        Span<(int, int)> displacementSlots = stackalloc (int, int)[9];
        var displacementSlotsHead = 0;

        for (var i = 0; i < 9; i++)
        {
            var matId = matIds[i];
            var (x, y) = neighbors[i];

            switch (matId)
            {
                case -1:
                    airSlots[airSlotsHead++] = (x, y);
                    continue;
                case >= 0:
                {
                    for (var j = 0; j < 4; j++)
                    {
                        if (matId != inputIdsAmts[j].Item1 || inputIdsAmts[j].Item2 <= 0) continue;
                        inputIdsAmts[j].Item2--;
                        inputSlots[inputSlotsHead++] = (x, y);
                        goto onFound;
                    }

                    for (var j = 0; j < 2; j++)
                    {
                        if (matId != catalystIdsAmts[j].Item1 || catalystIdsAmts[j].Item2 <= 0) continue;
                        catalystIdsAmts[j].Item2--;
                        break;
                    }

                    if (Materials.IsStatic(matId))
                    {
                        continue;
                    }

                    displacementSlots[displacementSlotsHead++] = (x, y);
                    break;
                }
            }

            onFound: ;
        }

        var extraAirNeeded = (reaction.OutputCellCount - reaction.InputCellCount) - airSlotsHead;
        if (displacementSlotsHead < extraAirNeeded)
        {
            return;
        }

        if (extraAirNeeded > 0)
        {
            // Not enough space to do the reaction, try to perform displacement
            Span<(int, int)> displacementTargets = displacementSlots[..extraAirNeeded].ToArray();
            List<(int, int)[]>? displacementPaths = null;
            for (var i = 0; i < Math.Clamp(reaction.Probability, 1, 100); i++)
            {
                displacementPaths = TryFindDisplacements(cx, cy, displacementTargets, field, tick);
                if (displacementPaths != null)
                {
                    break;
                }
            }

            if (displacementPaths == null)
            {
                // Reaction could occur, but we could not make space for it
                return;
            }

            foreach ((int, int)[] path in displacementPaths)
            {
                for (var i = path.Length - 1; i >= 1; i--)
                {
                    var ((ax, ay), (bx, by)) = (path[i], path[i - 1]);

                    var i1 = field.Index(ax, ay);
                    var i2 = field.Index(bx, by);
                    field.SwapMaterialAndHeat(i1, i2);
                }
            }

            // Now, we have made space to drop stuff in
            foreach (var (ax, ay) in displacementTargets)
            {
                airSlots[airSlotsHead++] = (ax, ay);
            }
        }

        if (airSlotsHead + inputSlotsHead < reaction.OutputCellCount)
        {
            return;
        }

        for (var i = 0; i < reaction.OutputCellCount; i++)
        {
            var nextOutput = reaction.OutputsAsArray[i];
            if (inputSlotsHead > 0)
            {
                var (x, y) = inputSlots[--inputSlotsHead];
                field.Set(x, y, nextOutput);
                continue;
            }

            if (airSlotsHead > 0)
            {
                var (x, y) = airSlots[--airSlotsHead];
                field.Set(x, y, nextOutput);
            }
        }

        var cIndx = field.Index(cx, cy);
        var tempHere = field.GetHeatmap(cIndx);

        if (reaction.ChangeInTemperature.HasValue)
        {
            var newTemp = (short)(reaction.ChangeInTemperature.Value + tempHere);
            field.SetHeatmap(cIndx, newTemp);
        }
    }

    public static void PerformReactions(SimSnapshot state) {
        if (ReactionOpportunities.IsEmpty)
        {
            return;
        }

        List<(int, int, Reaction)> listCollected = new(ReactionOpportunities.Count);
        while (ReactionOpportunities.TryTake(out (int, int, Reaction) triplet))
        {
            listCollected.Add(triplet);
        }

        listCollected.Sort((p1, p2) =>
        {
            var (x1, y1, _) = p1;
            var (x2, y2, _) = p2;

            return (x1, y1).CompareTo((x2, y2));
        });

        Span<(int, int, Reaction)> collected = listCollected.ToArray();
        collected.Shuffle(state.Tick);

        foreach ((var x, var y, Reaction reaction) in collected)
        {
            PerformReaction(x, y, state.Field, state.Tick, reaction);
        }
    }
}

[HarmonyPatch(typeof(BaseMaterial))]
public static class BaseMaterialPatches
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(BaseMaterial.Step))]
    static IEnumerable<CodeInstruction> _0(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
        GD.Print("Patching IL of BaseMaterial.Step");

        CodeMatcher matcher = new(instructions, generator);

        matcher
            .DeclareLocal(typeof(Reaction), out LocalBuilder? reaction)
            .DefineLabel(out Label onFail);

        CodeMatch[] reactionSetIL =
        [
            CodeMatch.IsLdarg(0),
            CodeMatch.LoadsField(AccessTools.Field(typeof(BaseMaterial), nameof(BaseMaterial.Reactions))),
            Dup,
            CodeMatch.Branches()
        ];

        CodeMatch[] postReactionIL =
        [
            CodeMatch.IsLdarg(),
            Ldc_I4_4,
            And,
            Ldc_I4_0,
            Ceq,
            CodeMatch.IsStloc()
        ];

        CodeMatch[] injectedCodeIL =
        [
            Ldarg_0,
            Ldarg_1,
            Ldarg_2,
            Ldarg_3,
            Ldarg_S[4],
            Call[AccessTools.Method(typeof(DisplacementSystem), nameof(DisplacementSystem.TryFindReaction))],
            Stloc_S[reaction.LocalIndex],
            Ldloc_S[reaction.LocalIndex],
            Brfalse_S[onFail],
            Ldarg_1,
            Ldarg_2,
            Ldloc_S[reaction.LocalIndex],
            Call[AccessTools.Method(typeof(DisplacementSystem), nameof(DisplacementSystem.RecordReactionOpportunity))],
            Ldc_I4_1,
            Ret
        ];

        matcher
            .MatchStartForward(reactionSetIL)
            .Insert(injectedCodeIL)
            .Do(cm => cm.Instruction.MoveLabelsFrom(cm.Clone().MatchStartForward(reactionSetIL).Instruction))
            .MatchStartForward(postReactionIL)
            .AddLabels([onFail])
            .Advance(-1)
            .RemoveUntilBackward(injectedCodeIL);

        return matcher.Instructions();
    }
}

[HarmonyPatch(typeof(Simulation))]
public static class SimulationPatches
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(Simulation.Step))]
    static IEnumerable<CodeInstruction> _1(IEnumerable<CodeInstruction> instructions) {
        GD.Print("Patching IL of Simulation.Step");

        MethodInfo? targetMethod = AccessTools.Method(typeof(Simulation), "RefreshMaterialTypeIdentityCacheFromActiveChunks");
        var matcher = new CodeMatcher(instructions);

        matcher
            .MatchStartForward(CodeMatch.Calls(targetMethod))
            .ThrowIfInvalid("Could not find RefreshMaterial...")
            .Insert(
                Ldarg_0,
                Call[AccessTools.Method(typeof(DisplacementSystem), nameof(DisplacementSystem.PerformReactions))]
            );

        return matcher.Instructions();
    }
}