using System;
using System.Reflection;
using CombatExtended;
using CombatExtended.AI;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // CE's own target finders and AI decisions between floors (plan §2d, D7: IsoRimZ's target picking, sight and cast
    // positions stay; the pack fixes CE's own). All through the scope (Scope: distances 3D, points in the viewer's frame):
    //  - NonSnapAttackTargetFinder (non-snap turrets: flak): its range filters (InHorDistOf, the closest-target fallback),
    //    minimum ranges and score (distance, the angle the turret must turn, the target's cover).
    //  - CE's postfix on the game's shooting score (Harmony_AttackTargetFinder_GetShootingTargetScore: the edging-closer
    //    bonus and the shield terms by distance), which IsoRimZ's cross-floor pick uses too.
    //  - CE's AI tactics: the fire-mode choice by distance (CompFireSelection), weapon switching and flares
    //    (CompOpportunisticSwitch), AI_Utility's distance rules (EdgingCloser/Away, VisibilityGoodAt, the flare cluster),
    //    the hunt job's range (JobDriver_Hunt).
    //  - Mid-burst retargeting (Verb_LaunchProjectileCE.Retarget): CE looks for the target's mates near the raw shot
    //    segment; between floors that segment runs through other bands. Scope.PawnsNearSegment takes its ground line on the
    //    floors it passes; the turret top's turn to the new target goes through Aim's frame transpiler.
    internal static class Ai
    {
        public static bool On;   // every patch of the group landed

        // Scoped methods per type on the CE build the pack was tested on (Scope.Expect).
        private const int TestedNonSnap = 5, TestedFireSelection = 1, TestedSwitch = 2, TestedHunt = 1;   // (CE 2890901044, 2026-10-30)

        public static void Patch(Harmony h)
        {
            Scope.CheckSwaps();
            // non-snap turrets
            Type ns = typeof(NonSnapAttackTargetFinder);
            Scope.Around(h, Static(ns, "BestAttackTarget", 6), nameof(Scope.Searcher_Prefix));
            Scope.Expect("NonSnapAttackTargetFinder", Scope.ScopeAll(h, ns, true), TestedNonSnap);
            // CE's shooting score postfix
            Type outer = AccessTools.TypeByName("CombatExtended.HarmonyCE.Harmony_AttackTargetFinder") ?? throw new MissingMethodException("Harmony_AttackTargetFinder");
            Type score = AccessTools.Inner(outer, "Harmony_AttackTargetFinder_GetShootingTargetScore") ?? throw new MissingMethodException("Harmony_AttackTargetFinder_GetShootingTargetScore");
            Scope.Entry(h, AccessTools.Method(score, "Postfix") ?? throw new MissingMethodException("GetShootingTargetScore.Postfix"), nameof(Scope.Searcher_Prefix));
            // AI tactics
            Type ai = typeof(AI_Utility);
            Scope.Entry(h, Static(ai, "EdgingCloser", 2), nameof(Scope.Pawn_Prefix));
            Scope.Entry(h, Static(ai, "EdgingAway", 2), nameof(Scope.Pawn_Prefix));
            Scope.Entry(h, Static(ai, "VisibilityGoodAt", 4), nameof(Scope.Shooter_Prefix));
            Scope.Entry(h, Static(ai, "FindAttackedClusterCenter", 5), nameof(Scope.Attacker_Prefix));
            // (each type on its own: one type found nothing fails the group even when the others found more)
            Scope.Expect("CompFireSelection", Scope.ScopeAll(h, typeof(CompFireSelection), true), TestedFireSelection);
            Scope.Expect("CompOpportunisticSwitch", Scope.ScopeAll(h, typeof(CompOpportunisticSwitch), true), TestedSwitch);
            Scope.Expect("JobDriver_Hunt", Scope.ScopeAll(h, typeof(JobDriver_Hunt), true), TestedHunt);
            // mid-burst retargeting
            MethodInfo retarget = Aim.ByName(typeof(Verb_LaunchProjectileCE), "Retarget", 0);
            PackZ.Transpiler(h, retarget, typeof(Aim), nameof(Aim.Frame_Transpiler));
            Scope.Swap(h, retarget);
            On = true;
        }

        private static MethodInfo Static(Type t, string name, int args)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name == name && m.GetParameters().Length == args)
                {
                    return m;
                }
            }
            throw new MissingMethodException(t.Name + "." + name + "/" + args);
        }
    }
}
