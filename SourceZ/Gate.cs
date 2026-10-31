using System.Linq;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using Verse;

namespace IsoRimCEPackZ
{
    // The fail-soft refusal (plan §5): when a key patch failed (or a ProjectZ part the pack builds on is off), a shot
    // between floors is refused as CE refuses a shot without a line. Its own class, with no CE members resolved in a
    // type initializer and no other pack class but Layout, so it still works when the groups it stands in for failed.
    internal static class Gate
    {
        public static int Refused;   // harness

        public static void Patch(Harmony h)
        {
            MethodInfo m = typeof(Verb_LaunchProjectileCE).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == "CanHitTargetFrom" && x.GetParameters().Length == 3) ?? throw new System.MissingMethodException("Verb_LaunchProjectileCE.CanHitTargetFrom/3");
            PackZ.Prefix(h, m, typeof(Gate), nameof(Prefix));
        }

        public static bool Prefix(Verb_LaunchProjectileCE __instance, IntVec3 root, LocalTargetInfo targ, ref string report, ref bool __result)
        {
            if (PackZ.Healthy)
            {
                return true;
            }
            Layout L = Layout.Of(__instance.caster?.Map);
            if (L == null || L.FrameOf(root) == L.FrameOf(targ.Cell))
            {
                return true;
            }
            Refused++;
            report = "CE_NoLoS".Translate();
            __result = false;
            return false;
        }
    }
}
