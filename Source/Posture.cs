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
    //    One exception, drawn only (CE's own IsCrouching, its gameplay, is untouched): drafted and idle (Wait_Combat) the pawn
    //    is drawn crouching only while its map has an active hostile threat (the game's GenHostility.AnyHostileActiveThreatToPlayer:
    //    awake hostiles and manhunters count; dormant mech clusters, hive insects at their hive, prisoners, downed enemies do
    //    not; asked once a second per map) or while it aims or fires from it (a ranged verb's warmup or cooldown: auto-attack).
    //    Otherwise it stands.
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

        private sealed class ThreatBox
        {
            public int tick;
            public bool asked, on;
        }

        private const int ThreatInterval = 60;   // game ticks between asks (about a second)
        private static readonly ConditionalWeakTable<Map, ThreatBox> threats = new ConditionalWeakTable<Map, ThreatBox>();
        private static readonly ConditionalWeakTable<Map, ThreatBox>.CreateValueCallback newThreatBox = m => new ThreatBox();

        // Harness counters.
        public static int Asked, Crouching, CrouchWalking, Hunkering, IdleStanding, ThreatAsks;

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
            if (CrouchJob(job) && (job != JobDefOf.Wait_Combat || Aiming(pawn) || Threat(pawn.MapHeld)))
            {
                Crouching++;
                return 1f;   // CE_Utility.IsCrouching
            }
            if (job == JobDefOf.Wait_Combat)
            {
                IdleStanding++;   // drafted idle, no threat on the map: stands (CE still counts it crouching)
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

        // Aiming or firing a ranged verb (the stance of a shot's warmup or cooldown).
        private static bool Aiming(Pawn pawn)
        {
            Stance_Busy b = pawn.stances?.curStance as Stance_Busy;
            return b != null && b.verb != null && !b.verb.IsMeleeAttack;
        }

        // The game's own "is there a fight on this map" test, cached per map and asked again every ThreatInterval game ticks.
        private static bool Threat(Map map)
        {
            if (map == null)
            {
                return false;
            }
            ThreatBox b = threats.GetValue(map, newThreatBox);
            int now = Find.TickManager.TicksGame;
            if (!b.asked || now - b.tick >= ThreatInterval || now < b.tick)
            {
                b.asked = true;
                b.tick = now;
                b.on = GenHostility.AnyHostileActiveThreatToPlayer(map);
                ThreatAsks++;
            }
            return b.on;
        }

        // For tests: the map's threat flag as the rule last read it (no new ask); false when never asked.
        public static bool CachedThreat(Map map)
        {
            ThreatBox b;
            return map != null && threats.TryGetValue(map, out b) && b.on;
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
