using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
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
    //    Popping up to fire: while it aims or fires a CE ranged verb (warmup, cooldown, between a burst's shots) the crouch is
    //    raised as far as CE raises the shot. CE launches a pawn's shot from the verb's ShotHeight (CollisionVertical: a
    //    crouching pawn's height comes down to 0.45 of its body, but not below the highest partial cover in the 8 cells around
    //    it plus 0.15 of its body; the shot 0.15 of its body under that top; an instant shot, a laser, further raised by
    //    AdjustShotHeight over the cover in its line). That value is read as it is and placed on IsoRim's crouch the way CE
    //    places it on its own pawn: CE's lowest crouched shot (no cover: 0.45 of the body, + 0.01) is IsoRim's full crouch,
    //    CE's standing shot (0.85 of the body) standing, linearly between (IsoRim's crouch brings the shoulder, and the gun on
    //    it, down linearly with the amount). No cover: CE's lowest shot, the full crouch as before. Eased over game ticks (up
    //    or down in PopEaseTicks, kept up PopHoldTicks after the last aimed tick so a burst's gaps do not dip); IsoRim eases
    //    on top.
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

        // Popping up to fire (see above), per pawn (made the first time a CE verb aims).
        private sealed class PopBox
        {
            public float shown = 1f;     // the crouch the rule gives now (1: the full crouch)
            public float target = 1f;    // what CE's shot height asks for (last aimed)
            public int tick = -1;        // game tick of the last step
            public int aimTick = -99999; // last game tick aimed
            public int shotTick = -1;    // game tick CE's height was last read
            public float shotHeight, bottom, body;   // CE's values last read (harness)
        }

        private const int PopEaseTicks = 10;   // game ticks from the full crouch to standing (and back)
        private const int PopHoldTicks = 12;   // kept up this long after the last aimed tick
        private static readonly ConditionalWeakTable<Pawn, PopBox> pops = new ConditionalWeakTable<Pawn, PopBox>();
        private static readonly ConditionalWeakTable<Pawn, PopBox>.CreateValueCallback newPopBox = p => new PopBox();

        // CE's body height factor (CE_Utility.GetCollisionBodyFactors: a Unity Vector2, read through Harmony as the pack reads
        // CE's Unity-typed members).
        private static readonly Func<Pawn, Vector2> bodyFactors =
            AccessTools.MethodDelegate<Func<Pawn, Vector2>>(AccessTools.Method(typeof(CE_Utility), nameof(CE_Utility.GetCollisionBodyFactors)));

        // Harness counters.
        public static int Asked, Crouching, CrouchWalking, Hunkering, IdleStanding, ThreatAsks, Popping;
        public static bool PopOn = true;   // harness: off draws the full crouch while firing, as before

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
                Verb aim = AimVerb(pawn);
                if (job != JobDefOf.Wait_Combat || aim != null || Threat(pawn.MapHeld))
                {
                    Crouching++;
                    return Pop(pawn, aim);   // CE_Utility.IsCrouching: 1, raised while firing over cover
                }
            }
            if (!CrouchJob(job) && JustFired(pawn))
            {
                return Pop(pawn, null);   // a moment between jobs after firing (Wait_MaintainPosture): no flicker up
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

        // The ranged verb the pawn aims or fires now (the stance of a shot's warmup or cooldown, between a burst's shots), or
        // null.
        private static Verb AimVerb(Pawn pawn)
        {
            Stance_Busy b = pawn.stances?.curStance as Stance_Busy;
            return b != null && b.verb != null && !b.verb.IsMeleeAttack ? b.verb : null;
        }

        // The crouch while CE crouches the pawn: 1, raised toward standing while it aims or fires (see the header).
        private static float Pop(Pawn pawn, Verb aim)
        {
            Verb_LaunchProjectileCE ce = aim as Verb_LaunchProjectileCE;
            PopBox s;
            if (!PopOn)
            {
                return 1f;   // (harness: as before)
            }
            if (!pops.TryGetValue(pawn, out s))
            {
                if (ce == null)
                {
                    return 1f;   // never aimed a CE verb: the full crouch, nothing kept
                }
                s = pops.GetValue(pawn, newPopBox);
            }
            int now = Find.TickManager.TicksGame;
            if (ce != null)
            {
                s.aimTick = now;
                if (s.shotTick != now)
                {
                    s.shotTick = now;
                    s.target = PopTarget(pawn, ce, s);
                }
            }
            float want = ce != null || now >= s.aimTick && now - s.aimTick <= PopHoldTicks ? s.target : 1f;
            int dt = s.tick < 0 || now < s.tick ? 1000 : now - s.tick;
            s.tick = now;
            s.shown = Mathf.MoveTowards(s.shown, want, dt / (float)PopEaseTicks);
            if (s.shown < 1f)
            {
                Popping++;
            }
            return Mathf.Max(0.001f, s.shown);   // (over 0: still a crouch, never a pass)
        }

        // Aimed a CE verb within PopHoldTicks and standing still: when an attack job ends the game puts the pawn on a short
        // non-crouch job (Wait_MaintainPosture) for a tick or so before Wait_Combat; the crouch is kept over that gap.
        private static bool JustFired(Pawn pawn)
        {
            PopBox s;
            if (!PopOn || !pops.TryGetValue(pawn, out s) || pawn.pather != null && pawn.pather.MovingNow)
            {
                return false;
            }
            int now = Find.TickManager.TicksGame;
            return now >= s.aimTick && now - s.aimTick <= PopHoldTicks;
        }

        // CE's shot height for this verb now, placed on IsoRim's crouch: 1 at CE's lowest crouched shot, 0 at its standing one.
        private static float PopTarget(Pawn pawn, Verb_LaunchProjectileCE verb, PopBox s)
        {
            float shot = verb.ShotHeight;   // what CE launches a projectile from (Verb_LaunchProjectileCE.TryCastShot)
            ProjectilePropertiesCE props = verb.Projectile?.projectile as ProjectilePropertiesCE;
            LocalTargetInfo target = verb.CurrentTarget;
            if (props != null && props.isInstant && target.Thing != null)
            {
                verb.AdjustShotHeight(pawn, target, ref shot);   // an instant shot (a laser): CE raises it over the cover in its line
            }
            float bottom = new CollisionVertical(pawn).Min;   // standing on something (cover in its own cell) or flying: CE's base
            float body = bodyFactors(pawn).y;
            s.shotHeight = shot;
            s.bottom = bottom;
            s.body = body;
            float low = CollisionVertical.BodyRegionBottomHeight * body + 0.01f;   // CE's crouched shot without cover
            float high = CollisionVertical.BodyRegionMiddleHeight * body;          // CE's standing shot
            if (high - low < 0.01f)
            {
                return 1f;
            }
            float up = Mathf.Clamp01((shot - bottom - low) / (high - low));
            return up < 0.005f ? 1f : 1f - up;
        }

        // For tests: the pop-up state (CE's shot height read, the crouch it asks for, the crouch given now).
        public static string PopReport(Pawn pawn)
        {
            PopBox s;
            if (pawn == null || !pops.TryGetValue(pawn, out s))
            {
                return "no pop state";
            }
            return "CE shot " + s.shotHeight.ToString("0.000") + " (base " + s.bottom.ToString("0.00") + ", body " + s.body.ToString("0.00") + "), crouch wanted "
                + s.target.ToString("0.000") + ", given " + s.shown.ToString("0.000") + ", aimed " + (Find.TickManager.TicksGame - s.aimTick) + " ticks ago";
        }

        // For tests: CE's shot height last read for the pawn (CE units), -1 if never.
        public static float PopShotHeight(Pawn pawn)
        {
            PopBox s;
            return pawn != null && pops.TryGetValue(pawn, out s) ? s.shotHeight : -1f;
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
