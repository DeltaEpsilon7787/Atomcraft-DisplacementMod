using HarmonyLib;

namespace AtomCraft_Displacement;

class Entry
{
    static void Initialize() {
        var harmony = new Harmony("DeltaEpsilon.displacement");
        harmony.PatchAll();
    }
}