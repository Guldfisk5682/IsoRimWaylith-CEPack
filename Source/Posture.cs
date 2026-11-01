using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CombatExtended;
using RimWorld;
using Verse;

namespace IsoRimCEPack
{
    // CE's postures on IsoRim's figures (IsoRim Api 45: posture rules; older IsoRim: nothing, figures stand as before).
    //
    // CE has three, all its own state read as it is (nothing of CE's is changed or called with side effects):
    //  - crouching: CE_Utility.IsCrouching (a humanlike, not downed, on a job whose JobDefExtensionCE.isCrouchJob is set:
    //    waiting in combat (drafted and standing still), shooting, manning a turret, reloading, tending...). Rule: 1, a full
    //    crouch; IsoRim decides how low from the cover drawn beside the pawn. CE switches at job changes; IsoRim eases.
    //  - crouch-walking under suppression: CompSuppressable.IsCrouchWalking while the pawn moves. Rule: 0.5, a mild crouch
    //    with IsoRim's walking step.
    //  - hunkering: the job CE_JobDefOf.HunkerDown. CE first waits a moment, then lays the pawn down
    //    (PawnPosture.LayingOnGroundNormal). Rule: a crouch while it waits, then lying face down (Api.PostureProne), the head
    //    toward CompSuppressable.SuppressorLoc, where the suppressing fire comes from. (CompSuppressable.IsHunkering is never
    //    read: its getter changes CE's state.)
    // Per pawn per frame: the job def's extension is cached per JobDef, the comp per pawn (no searches).
    internal static class Posture
    {
        private sealed class CompBox
        {
            public CompSuppressable comp;
        }

        private static readonly ConditionalWeakTable<Pawn, CompBox> comps = new ConditionalWeakTable<Pawn, CompBox>();
        private static readonly Dictionary<JobDef, bool> crouchJobs = new Dictionary<JobDef, bool>();

        // Harness counters.
        public static int Asked, Crouching, CrouchWalking, Hunkering;

        // Registers the rules (only called with IsoRim Api 45 or newer: the Api members are only touched in here).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register()
        {
            IsoRim.Api.AddPostureRule(new Func<Pawn, float>(Rule));
            IsoRim.Api.AddProneFacingRule(new Func<Pawn, IntVec3>(Facing));
        }

        private static CompSuppressable CompOf(Pawn pawn)
        {
            return comps.GetValue(pawn, p => new CompBox { comp = p.TryGetComp<CompSuppressable>() }).comp;
        }

        // CE's isCrouchJob for a job def, cached.
        private static bool CrouchJob(JobDef def)
        {
            bool yes;
            if (!crouchJobs.TryGetValue(def, out yes))
            {
                JobDefExtensionCE ext = def.GetModExtension<JobDefExtensionCE>();
                yes = ext != null && ext.isCrouchJob;
                crouchJobs[def] = yes;
            }
            return yes;
        }

        private static float Rule(Pawn pawn)
        {
            Asked++;
            if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.Downed || pawn.Dead)
            {
                return 0f;
            }
            JobDef job = pawn.CurJobDef;
            if (job == null)
            {
                return 0f;
            }
            if (job == CE_JobDefOf.HunkerDown)
            {
                Hunkering++;
                // down once CE has laid it down; crouching while CE waits first
                return pawn.GetPosture() != PawnPosture.Standing ? IsoRim.Api.PostureProne : 1f;
            }
            if (CrouchJob(job))
            {
                Crouching++;
                return 1f;   // CE_Utility.IsCrouching
            }
            if (pawn.pather != null && pawn.pather.MovingNow)
            {
                CompSuppressable c = CompOf(pawn);
                if (c != null && c.IsCrouchWalking)
                {
                    CrouchWalking++;
                    return 0.5f;
                }
            }
            return 0f;
        }

        private static IntVec3 Facing(Pawn pawn)
        {
            if (pawn == null || pawn.CurJobDef != CE_JobDefOf.HunkerDown)
            {
                return IntVec3.Invalid;
            }
            CompSuppressable c = CompOf(pawn);
            IntVec3 from = c != null ? c.SuppressorLoc : IntVec3.Invalid;
            return from.IsValid && from != pawn.Position ? from : IntVec3.Invalid;
        }
    }
}
