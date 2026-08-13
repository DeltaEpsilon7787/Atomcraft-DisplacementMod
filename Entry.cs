using Godot;
using HarmonyLib;

namespace DeltaEpsilon.Displacement;

public class Entry
{
    public static void Initialize() {
        var harmony = new Harmony("DeltaEpsilon.displacement");
        harmony.PatchAll();
        GD.Print("Displacement mod loaded");
    }
}