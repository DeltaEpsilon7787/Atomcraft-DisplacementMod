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
    static readonly ConcurrentBag<(int, int)> ReactionOpportunities = [];
    const uint MaxDisplacementLength = 256;

    // 0 1 2   ↖ ↑ ↗
    // 3 4 5   ← X →
    // 6 7 8   ↙ ↓ ↘
    static (int, int) IndexToDirection(this byte indx) {
        return indx switch
        {
            0 => (-1, -1),
            1 => (+0, -1),
            2 => (+1, -1),
            3 => (-1, +0),
            4 => (+0, +0),
            5 => (+1, +0),
            6 => (-1, +1),
            7 => (+0, +1),
            8 => (+1, +1),
            _ => (+0, +0),
        };
    }

    static byte DirectionToIndex(this (int, int) dir) {
        return dir switch
        {
            (-1, -1) => 0,
            (+0, -1) => 1,
            (+1, -1) => 2,
            (-1, +0) => 3,
            (+0, +0) => 4,
            (+1, +0) => 5,
            (-1, +1) => 6,
            (+0, +1) => 7,
            (+1, +1) => 8,
            _ => 4,
        };
    }

    static (int, int) ShiftFrom(this byte dir, int x, int y) {
        var (dx, dy) = dir.IndexToDirection();
        return (x + dx, y + dy);
    }

    static short GetAt(this (int, int) coords, SimField field) {
        return field.Get(coords.Item1, coords.Item2);
    }

    static short Get(this SimField field, (int, int) coords) {
        return field.Get(coords.Item1, coords.Item2);
    }

    static byte DirectionLeftRotate(this byte dirIndx) {
        return dirIndx switch
        {
            0 => 3,
            1 => 0,
            2 => 1,
            3 => 6,
            4 => 4,
            5 => 2,
            6 => 7,
            7 => 8,
            8 => 5,
            _ => dirIndx,
        };
    }

    static byte DirectionRightRotate(this byte dirIndx) {
        return dirIndx switch
        {
            0 => 1,
            1 => 2,
            2 => 5,
            3 => 0,
            4 => 4,
            5 => 8,
            6 => 3,
            7 => 6,
            8 => 7,
            _ => dirIndx,
        };
    }

    [ThreadStatic] static byte[]? _pixelCounter;

    public static void RecordReactionOpportunity(int x, int y) {
        ReactionOpportunities.Add((x, y));
    }

    public static Reaction? TryFindReaction(BaseMaterial that, int cx, int cy, SimField field, int tick) {
        if (that.Reactions == null || that.Reactions.Length == 0)
        {
            return null;
        }

        _pixelCounter ??= new byte[short.MaxValue + 1];

        // 0 1 2   ↖ ↑ ↗
        // 3 4 5   ← X →
        // 6 7 8   ↙ ↓ ↘
        Span<short> matArray = stackalloc short[9];
        for (byte dir = 0; dir < 9; dir++)
        {
            matArray[dir] = dir.ShiftFrom(cx, cy).GetAt(field);
        }

        var is1 = Materials.IsStatic(matArray[1]);
        var is3 = Materials.IsStatic(matArray[3]);
        var is5 = Materials.IsStatic(matArray[5]);
        var is7 = Materials.IsStatic(matArray[7]);

        if (is1 && is3) matArray[0] = -2;
        if (is1 && is5) matArray[2] = -2;
        if (is3 && is7) matArray[6] = -2;
        if (is5 && is7) matArray[8] = -2;

        foreach (var mat in matArray)
        {
            if (mat >= 0) _pixelCounter[mat]++;
        }

        var tempHere = field.GetHeatmap(cx, cy);
        var rng = RNG.Roll(cx, cy, tick);

        Reaction? result = null;
        foreach (Reaction reaction in that.Reactions)
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

        foreach (var mat in matArray)
        {
            if (mat >= 0) _pixelCounter[mat] = 0;
        }

        return result;
    }

    static List<(int, int)[]>? TryFindDisplacements(
        int x, int y,
        Span<byte> displacementStarts,
        SimField field, int tick
    ) {
        List<(int, int)[]> paths = new(displacementStarts.Length);

        Span<(int, int)> path = stackalloc (int, int)[(int)MaxDisplacementLength];

        foreach (var dir in displacementStarts)
        {
            var (fx, fy) = dir.ShiftFrom(x, y);

            var (cx, cy) = (fx, fy);
            var heading = dir;

            var pathHead = 0;
            path[pathHead++] = (cx, cy);

            var r = 4;

            headingLoop:
            var isDiagonalShift = heading switch
            {
                0 => true,
                2 => true,
                6 => true,
                8 => true,
                _ => false,
            };

            if (isDiagonalShift)
            {
                if (heading switch
                    {
                        0 => Materials.IsStatic(((byte)(1)).ShiftFrom(cx, cy).GetAt(field)) &&
                             Materials.IsStatic(((byte)(3)).ShiftFrom(cx, cy).GetAt(field)),
                        2 => Materials.IsStatic(((byte)(1)).ShiftFrom(cx, cy).GetAt(field)) &&
                             Materials.IsStatic(((byte)(5)).ShiftFrom(cx, cy).GetAt(field)),
                        6 => Materials.IsStatic(((byte)(3)).ShiftFrom(cx, cy).GetAt(field)) &&
                             Materials.IsStatic(((byte)(7)).ShiftFrom(cx, cy).GetAt(field)),
                        8 => Materials.IsStatic(((byte)(5)).ShiftFrom(cx, cy).GetAt(field)) &&
                             Materials.IsStatic(((byte)(7)).ShiftFrom(cx, cy).GetAt(field)),
                        _ => false,
                    })
                {
                    // Attempted to displace into a diagonally sealed location
                    return null;
                }
            }

            (cx, cy) = heading.ShiftFrom(cx, cy);
            if (Math.Max(Math.Abs(cx - x), Math.Abs(cy - y)) <= 1)
            {
                // Displacement failed, we've intersected with protected area.
                return null;
            }

            var matId = field.Get(cx, cy);

            // Hit a wall
            if (Materials.IsStatic(matId)) return null;
            if (matId == -2) return null;

            path[pathHead++] = (cx, cy);
            if (pathHead >= MaxDisplacementLength)
            {
                return null;
            }

            if (matId == -1)
            {
                paths.Add(path[..pathHead].ToArray());

                if (paths.Count >= displacementStarts.Length)
                {
                    return paths;
                }

                continue;
            }

            var doesDirShift = RNG.Roll(cx, cy, tick) % r == 0;
            if (doesDirShift)
            {
                r++;
                var choice = RNG.Roll(cx, cy, tick + 1) % 2;
                heading = choice == 0 ? heading.DirectionLeftRotate() : heading.DirectionRightRotate();
            }

            goto headingLoop;
        }

        return paths;
    }

    static void InduceReactionAt(int cx, int cy, SimField? field, int tick) {
        if (field == null)
        {
            return;
        }

        var primaryMat = field.Get(cx, cy);
        Reaction? reaction = TryFindReaction(primaryMat.ToMaterial(), cx, cy, field, tick);
        if (reaction == null)
        {
            // Displacement or something else changed the neighborhood, so reaction is no longer possible
            return;
        }

        // 0 1 2   ↖ ↑ ↗
        // 3 4 5   ← X →
        // 6 7 8   ↙ ↓ ↘
        Span<short> matArray = stackalloc short[9];
        for (byte dir = 0; dir < 9; dir++)
        {
            matArray[dir] = dir.ShiftFrom(cx, cy).GetAt(field);
        }

        var is1 = Materials.IsStatic(matArray[1]);
        var is3 = Materials.IsStatic(matArray[3]);
        var is5 = Materials.IsStatic(matArray[5]);
        var is7 = Materials.IsStatic(matArray[7]);

        if (is1 && is3) matArray[0] = -2;
        if (is1 && is5) matArray[2] = -2;
        if (is3 && is7) matArray[6] = -2;
        if (is5 && is7) matArray[8] = -2;

        Span<(short type, int amt)> inputIdsAmts =
        [
            reaction.InputTypeCount > 0 ? (reaction.InputTypes[0], reaction.InputAmounts[0]) : ((short)0, 0),
            reaction.InputTypeCount > 1 ? (reaction.InputTypes[1], reaction.InputAmounts[1]) : ((short)0, 0),
            reaction.InputTypeCount > 2 ? (reaction.InputTypes[2], reaction.InputAmounts[2]) : ((short)0, 0),
            reaction.InputTypeCount > 3 ? (reaction.InputTypes[3], reaction.InputAmounts[3]) : ((short)0, 0),
        ];
        Span<(short type, int amt)> catalystIdsAmts =
        [
            reaction.CatalystTypeCount > 0 ? (reaction.CatalystTypes[0], reaction.CatalystAmounts[0]) : ((short)0, 0),
            reaction.CatalystTypeCount > 1 ? (reaction.CatalystTypes[1], reaction.CatalystAmounts[1]) : ((short)0, 0),
        ];

        // Indx 4 has special meaning since it is the central pixel
        //   so if always must be consumed first
        Span<byte> directionOrdering =
        [
            0, 1, 2,
            3, 4, 5,
            6, 7, 8,
        ];

        (directionOrdering[0], directionOrdering[4]) = (directionOrdering[4], directionOrdering[0]);
        (matArray[0], matArray[4]) = (matArray[4], matArray[0]);

        // We exploit Shuffle's property that for same length vectors and tick, the reordering will be the same 
        matArray[1..].Shuffle(tick);
        directionOrdering[1..].Shuffle(tick);

        Span<byte> inputSlots = stackalloc byte[9];
        var inputSlotsHead = 0;
        Span<byte> airSlots = stackalloc byte[9];
        var airSlotsHead = 0;
        Span<byte> displacementSlots = stackalloc byte[9];
        var displacementSlotsHead = 0;

        for (var i = 0; i < 9; i++)
        {
            var dir = directionOrdering[i];
            var matId = matArray[i];

            switch (matId)
            {
                case -1:
                    airSlots[airSlotsHead++] = dir;
                    continue;
                case >= 0:
                {
                    for (var j = 0; j < 4; j++)
                    {
                        if (matId != inputIdsAmts[j].type || inputIdsAmts[j].amt <= 0) continue;
                        inputIdsAmts[j].amt--;
                        inputSlots[inputSlotsHead++] = dir;
                        goto onFound;
                    }

                    for (var j = 0; j < 2; j++)
                    {
                        if (matId != catalystIdsAmts[j].type || catalystIdsAmts[j].amt <= 0) continue;
                        catalystIdsAmts[j].amt--;
                        break;
                    }

                    if (Materials.IsStatic(matId))
                    {
                        continue;
                    }

                    displacementSlots[displacementSlotsHead++] = dir;
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
            Span<byte> displacementStarts = displacementSlots[..extraAirNeeded].ToArray();
            List<(int, int)[]>? paths = null;
            for (var i = 0; i < Math.Clamp(reaction.Probability, 1, 100); i++)
            {
                paths = TryFindDisplacements(cx, cy, displacementStarts, field, tick);
                if (paths != null)
                {
                    break;
                }
            }

            if (paths == null)
            {
                // Reaction could occur, but we could not make space for it
                return;
            }

            foreach ((int, int)[] path in paths)
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
            foreach (var displacementIndx in displacementStarts)
            {
                airSlots[airSlotsHead++] = displacementIndx;
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
                var (x, y) = inputSlots[--inputSlotsHead].ShiftFrom(cx, cy);
                field.Set(x, y, nextOutput);
            } else if (airSlotsHead > 0)
            {
                var (x, y) = airSlots[--airSlotsHead].ShiftFrom(cx, cy);
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

        List<(int, int)> listCollected = new(ReactionOpportunities.Count);
        while (ReactionOpportunities.TryTake(out (int, int) pair))
        {
            listCollected.Add(pair);
        }

        listCollected.Sort((p1, p2) =>
        {
            var (x1, y1) = p1;
            var (x2, y2) = p2;

            return (x1, y1).CompareTo((x2, y2));
        });

        Span<(int, int)> collected = listCollected.ToArray();
        collected.Shuffle(state.Tick);

        foreach (var (x, y) in collected)
        {
            InduceReactionAt(x, y, state.Field, state.Tick);
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
            CodeMatch.Branches(),
        ];

        CodeMatch[] postReactionIL =
        [
            CodeMatch.IsLdarg(),
            Ldc_I4_4,
            And,
            Ldc_I4_0,
            Ceq,
            CodeMatch.IsStloc(),
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
            Call[AccessTools.Method(typeof(DisplacementSystem), nameof(DisplacementSystem.RecordReactionOpportunity))],
            Ldc_I4_1,
            Ret,
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