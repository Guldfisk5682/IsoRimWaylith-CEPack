using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Test harness only (mode "posture", -cepacktest=posture): CE's postures on IsoRim's figures (the pack's Posture.cs, IsoRim
    // Api 45), in the real game flow: drafted colonists, real CE jobs, real suppression from real fire (a shooter of no
    // faction firing past them at a held target), real hunkering, real ticks; each check its own fixture and seed. Renders
    // (the window's frames, as Capture17 takes them) go to the run's folder for the review sheet: each pose next to the same
    // scene with postures off (IsoRim Api.Dev.PosturesOn), from two camera turns, at play zoom and close up.
    public static partial class TestZ
    {
        private const string Rifle = "Gun_AssaultRifle";
        private const float FixedDrop = 0.20f;   // IsoRim's one crouch (Postures.Drop), cells for an adult (size 1)

        private static IEnumerable<IEnumerator<int>> PostureChecks()
        {
            // (-ceposture=1,5: only those checks; each keeps its own seed)
            Func<IEnumerator<int>>[] all = { PostureOpen, PostureSandbags, PostureEmbrasure, PostureFiring, PostureCrouchWalk, PostureHunker, PostureUpstairs, PostureSeated, PostureCards, PosturePerf, PostureRider };
            string[] only = GenCommandLine.TryGetCommandLineArg("ceposture", out string sel) && sel != null ? sel.Split(',') : null;
            FarThreat();
            for (int i = 0; i < all.Length; i++)
            {
                yield return only == null || only.Contains((i + 1).ToString()) ? all[i]() : Nothing();
            }
        }

        // The pack draws the drafted-idle crouch (Wait_Combat) only while the map has an active hostile threat (mode "drafted"
        // tests that rule). These checks are of the crouch itself, so a hostile is held awake in a far corner, out of every
        // fixture's range, for the whole mode (its own seed: the fixtures' pawns stay as before).
        private static void FarThreat()
        {
            IntVec3 c = new IntVec3(240, 0, 240);
            Wipe(236, 236, 244, 244);
            Pawn p;
            Rand.PushState(99001);
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, Enemy, forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false));
            }
            finally
            {
                Rand.PopState();
            }
            p.equipment?.DestroyAllEquipment();
            GenSpawn.Spawn(p, c, Map);
            held.Add((p, c));
            Out("far threat: " + p.LabelShort + " of " + p.Faction?.Name + " at " + c + ", map threat " + GenHostility.AnyHostileActiveThreatToPlayer(Map));
        }

        private static IEnumerator<int> Nothing()
        {
            yield break;
        }

        // ------------------------------------------------------------------ helpers

        private static string Posture(Pawn p) => IsoRim.Api.Dev.PostureReport(p);

        private static float Drawn(Pawn p, out float crouch, out float prone)
        {
            IsoRim.Api.DrawnPostureOf(p, out crouch, out prone, out float drop);
            return drop;
        }

        // Rendered frames with the game paused (the easing runs on real time): at least frames, and at least frames / 60 s.
        private static IEnumerable<int> Wait(int frames)
        {
            float t0 = Time.realtimeSinceStartup;
            for (int i = 0; i < frames || Time.realtimeSinceStartup - t0 < frames / 60f; i++)
            {
                yield return -1;
            }
        }

        private static bool hourSet;

        // A screenshot of the window (UI hidden unless ui) with the camera over cell c (a ground cell: an upper floor's cell's
        // column), size cells across, turned turn eighths.
        private static IEnumerable<int> Snap(string name, IntVec3 c, float size, int turn, bool ui = false, Pawn select = null, float lift = 0f)
        {
            if (!hourSet)
            {
                hourSet = true;
                TickManager tm = Find.TickManager;
                int baseTicks = tm.TicksGame - tm.TicksGame % GenDate.TicksPerHour + GenDate.TicksPerDay;
                for (int k = 0; k < 24; k++)
                {
                    tm.DebugSetTicksGame(baseTicks + k * GenDate.TicksPerHour + GenDate.TicksPerHour / 2);
                    if (GenLocalDate.HourOfDay(Map) == 11)
                    {
                        break;
                    }
                }
                Map.weatherManager.curWeather = WeatherDefOf.Clear;
                Map.weatherManager.lastWeather = WeatherDefOf.Clear;
                Map.weatherManager.curWeatherAge = 100000;
            }
            IsoRim.IsoRimSettings st = IsoRim.IsoRimMod.Settings;
            st.turn = turn;
            st.snap45 = true;
            st.isoPitch = 35.3f;
            st.cutHover = false;
            st.treesNearPointer = false;
            st.roofHover = false;
            st.buildSeeThrough = false;
            Find.UIRoot.screenshotMode.Active = !ui;
            Find.Selector.ClearSelection();
            if (select != null)
            {
                Find.Selector.Select(select, false, false);
            }
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
            Find.CameraDriver.config.sizeRange.min = 1f;   // (close-ups, as Capture17)
            // (lift: what is shown stands that high, an upper floor: the camera goes that far along its view, so it is in the middle)
            float yawRad = turn * 45f * Mathf.Deg2Rad;
            Vector3 along = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad)) * (lift / Mathf.Tan(35.3f * Mathf.Deg2Rad));
            Find.CameraDriver.SetRootPosAndSize(c.ToVector3Shifted() + along, size / 2f);
            foreach (int f in Wait(40))
            {
                yield return f;
            }
            string path = Path.Combine(outDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            for (int i = 0; i < 300 && !(File.Exists(path) && new FileInfo(path).Length > 0); i++)
            {
                yield return -1;
            }
            yield return -3;
            Out("  shot " + name);
        }

        // The same scene with postures off (standing, as before) and on, from two turns, at play zoom and close up.
        private static IEnumerable<int> Pair(string name, IntVec3 c, bool close = true, float lift = 0f)
        {
            foreach (int turn in new[] { 2, 3 })
            {
                foreach (bool on in new[] { false, true })
                {
                    IsoRim.Api.Dev.PosturesOn = on;
                    foreach (int w in Snap(name + "_t" + turn + "_" + (on ? "on" : "off") + "_play", c, 12f, turn, lift: lift))
                    {
                        yield return w;
                    }
                    if (close)
                    {
                        foreach (int w in Snap(name + "_t" + turn + "_" + (on ? "on" : "off") + "_close", c, 4.5f, turn, lift: lift))
                        {
                            yield return w;
                        }
                    }
                }
            }
            IsoRim.Api.Dev.PosturesOn = true;
        }

        // A drafted colonist with a rifle, facing north (toward the cover the fixtures put north of it).
        private static Pawn Soldier(IntVec3 c, string weapon = Rifle)
        {
            Pawn p = Colonist(c, weapon);
            p.Rotation = Rot4.North;
            return p;
        }

        // A shooter of no faction (CE suppresses pawns of another faction than the shooter's) with a gun.
        private static Pawn Shooter(IntVec3 c, string weapon)
        {
            Pawn p = null;
            for (int i = 0; i < 30; i++)
            {
                Rand.PushState(pawnSeed++);
                try
                {
                    p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, OtherFaction(), forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                        mustBeCapableOfViolence: true, allowAddictions: false));
                }
                finally
                {
                    Rand.PopState();
                }
                if (!p.WorkTagIsDisabled(WorkTags.Violent) && p.health.capacities.CapableOf(PawnCapacityDefOf.Sight))
                {
                    break;
                }
            }
            p.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
            p.equipment.DestroyAllEquipment();
            ThingDef wd = DefDatabase<ThingDef>.GetNamed(weapon);
            p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(wd, wd.MadeFromStuff ? GenStuff.DefaultStuffFor(wd) : null));
            GenSpawn.Spawn(p, c, Map);
            Out("  shooter " + p.LabelShort + " of " + (p.Faction?.Name ?? "no faction") + (p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer) ? " (hostile)" : ""));
            return p;
        }

        // A gun of another faction (an automatic mini-turret: no AI of its own to end its order), loaded, awake.
        private static Building_TurretGunCE Gun(IntVec3 c)
        {
            Building_TurretGunCE t = (Building_TurretGunCE)BuildAs("Turret_AutoMiniTurret", c, Faction.OfMechanoids);   // (a turret fires only at what its faction dislikes: mechs dislike the muffalo)
            Load(t);
            Out("  gun " + t.def.defName + " of " + (t.Faction?.Name ?? "no faction"));
            return t;
        }

        // (a turret not the player's cannot be given a forced target: CE drops it every tick; it picks its own, what its faction
        // dislikes: the colonist, or the muffalo past it)
        private static void KeepFiring(Building_TurretGunCE t, LocalTargetInfo target)
        {
            Load(t);
        }

        // A faction other than the player's, not hostile if there is one (CE suppresses pawns of another faction than the
        // shooter's; a pawn of no faction does not fire on an order).
        private static Faction OtherFaction() => Find.FactionManager.AllFactionsListForReading.Where(f => !f.IsPlayer && f.def.humanlikeFaction && !f.Hidden && !f.temporary)
            .OrderBy(f => f.HostileTo(Faction.OfPlayer) ? 1 : 0).ThenBy(f => f.loadID).FirstOrDefault();

        // Keeps a shooter on its order (its own AI would end the job between bursts).
        private static int keepLogged;

        private static void KeepFiring(Pawn shooter, LocalTargetInfo t)
        {
            if (shooter.Spawned && !shooter.Dead && !shooter.Downed && shooter.CurJobDef != JobDefOf.AttackStatic)
            {
                Order(shooter, t);
                if (keepLogged++ < 6)
                {
                    Out("  shooter order: job now " + shooter.CurJobDef?.defName + ", can hit " + CanHit(shooter, t) + ", faction " + (shooter.Faction?.Name ?? "none") + ", mental " + shooter.InMentalState);
                }
            }
        }

        // The fired-at colonist kept on its feet: marine armour (most of the turret's bullets glance off) and any wound taken off at
        // once (a test of postures, not of wounds).
        private static void Armour(Pawn p)
        {
            foreach (string a in new[] { "Apparel_PowerArmor", "Apparel_PowerArmorHelmet" })
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(a);
                if (d != null)
                {
                    p.apparel.Wear((Apparel)ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null), false);
                }
            }
        }

        private static void Unhurt(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(x => x is Hediff_Injury || x.def == HediffDefOf.BloodLoss).ToList())
            {
                p.health.RemoveHediff(h);
            }
        }

        private static void SetMood(Pawn p, float level)
        {
            if (p.needs?.mood != null)
            {
                p.needs.mood.CurLevel = level;
            }
        }

        private static CompSuppressable Supp(Pawn p) => p.TryGetComp<CompSuppressable>();

        private static string SuppText(Pawn p)
        {
            CompSuppressable c = Supp(p);
            return c == null ? "no comp" : "suppression " + c.CurrentSuppression.ToString("0") + "/" + c.SuppressionThreshold.ToString("0") + " suppressed " + c.isSuppressed
                + " crouch-walking " + c.IsCrouchWalking + " from " + c.SuppressorLoc;
        }

        // ------------------------------------------------------------------ checks

        // P1: drafted, standing still in the open: CE's Wait_Combat crouch; IsoRim's fixed crouch in the open.
        private static IEnumerator<int> PostureOpen()
        {
            Out("--- P1 drafted in the open");
            Wipe(10, 10, 30, 30);
            Pawn p = Soldier(new IntVec3(20, 0, 20));
            yield return 60;
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            IsoRim.Api.PostureOf(p, out float rc, out bool rp);
            float drop = Drawn(p, out float dc, out float dp);
            Out("  " + Posture(p));
            Check("P1 open: CE crouches the drafted pawn standing still (its crouch job: Wait_Combat or ReloadWeapon), IsoRim draws the open crouch", p.IsCrouching() && rc == 1f && !rp && dc == 1f && dp == 0f
                && Mathf.Abs(drop - FixedDrop) < 0.005f, "job " + p.CurJobDef?.defName + ", rule " + rc + (rp ? " prone" : "") + ", drawn " + dc + "/" + dp + ", drop " + drop.ToString("0.000"));
            foreach (int w in Pair("p1_open", p.Position))
            {
                yield return w;
            }
            // undrafted, held on the game's plain Wait job (not a CE crouch job): stands
            p.drafter.Drafted = false;
            held.Add((p, p.Position));
            yield return 30;
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            IsoRim.Api.PostureOf(p, out rc, out rp);
            Drawn(p, out dc, out dp);
            Check("P1b undrafted on a plain Wait job (CE: not crouching): standing", !p.IsCrouching() && rc == 0f && dc == 0f, "job " + p.CurJobDef?.defName + ", CE crouching " + p.IsCrouching() + ", rule " + rc + ", drawn " + dc);
            Gone(p);
        }

        // P2: behind sandbags: the same fixed crouch as in the open.
        private static IEnumerator<int> PostureSandbags()
        {
            Out("--- P2 behind sandbags");
            Wipe(40, 10, 60, 30);
            Thing[] bags = { Build("Sandbags", new IntVec3(49, 0, 21)), Build("Sandbags", new IntVec3(50, 0, 21)), Build("Sandbags", new IntVec3(51, 0, 21)) };
            Pawn p = Soldier(new IntVec3(50, 0, 20));
            yield return 60;
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            float drop = Drawn(p, out float dc, out _);
            Out("  " + Posture(p));
            Check("P2 sandbags: the fixed crouch", dc == 1f && Mathf.Abs(drop - FixedDrop) < 0.005f, "drop " + drop.ToString("0.000") + " (fixed " + FixedDrop.ToString("0.000") + ")");
            foreach (int w in Snap("p2_sandbags_t2_ui", p.Position, 8f, 2, true, p))   // selected: the brackets and the label follow
            {
                yield return w;
            }
            foreach (int w in Pair("p2_sandbags", p.Position))
            {
                yield return w;
            }
            Gone(p);
            Gone(bags);
        }

        // P3: behind a CE embrasure (a low wall): the same fixed crouch.
        private static IEnumerator<int> PostureEmbrasure()
        {
            Out("--- P3 behind an embrasure");
            Wipe(70, 10, 90, 30);
            Thing[] walls = { Build("CE_Embrasure", new IntVec3(79, 0, 21)), Build("CE_Embrasure", new IntVec3(80, 0, 21)), Build("CE_Embrasure", new IntVec3(81, 0, 21)) };
            Pawn p = Soldier(new IntVec3(80, 0, 20));
            yield return 60;
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            IsoRim.Api.PostureOf(p, out float rc, out _);
            float drop = Drawn(p, out float dc, out _);
            Out("  " + Posture(p));
            Check("P3 embrasure: CE crouches, the fixed crouch (gun through the wall as before)", rc == 1f && dc == 1f && Mathf.Abs(drop - FixedDrop) < 0.005f, "rule " + rc + ", drawn " + dc + ", drop " + drop.ToString("0.000"));
            foreach (int w in Pair("p3_embrasure", p.Position))
            {
                yield return w;
            }
            Gone(p);
            Gone(walls);
        }

        // P4: a burst fired crouched behind sandbags: the muzzle IsoRim gives (Api 44, the drawn bullet's start) comes down
        // with the crouch.
        private static IEnumerator<int> PostureFiring()
        {
            Out("--- P4 burst fire crouched");
            Wipe(10, 40, 30, 70);
            Thing[] bags = { Build("Sandbags", new IntVec3(19, 0, 51)), Build("Sandbags", new IntVec3(20, 0, 51)), Build("Sandbags", new IntVec3(21, 0, 51)) };
            Pawn target = Target(new IntVec3(20, 0, 64));
            Pawn p = Soldier(new IntVec3(20, 0, 50));
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            Order(p, target);
            int guard = 0;
            while (By(p, Launches, t0).Count == 0 && guard++ < 400)
            {
                yield return 1;
            }
            // mid-burst: the camera on the shooter, frames rendered with the game paused
            Find.CameraDriver.SetRootPosAndSize(p.Position.ToVector3Shifted(), 3f);
            foreach (int w in Wait(10))
            {
                yield return w;
            }
            IsoRim.Api.PostureOf(p, out float rc, out _);
            float drop = Drawn(p, out float dc, out _);
            bool onM = IsoRim.Api.TryGetMuzzle(p.equipment.Primary, p, out Vector3 mOn);
            Out("  firing: " + Posture(p) + ", launches " + By(p, Launches, t0).Count + ", muzzle " + mOn.ToString("F3"));
            foreach (int w in Snap("p4_fire_t2_on_close", p.Position, 4.5f, 2))
            {
                yield return w;
            }
            foreach (int w in Snap("p4_fire_t3_on_close", p.Position, 4.5f, 3))
            {
                yield return w;
            }
            IsoRim.Api.Dev.PosturesOn = false;
            foreach (int w in Wait(4))
            {
                yield return w;
            }
            bool offM = IsoRim.Api.TryGetMuzzle(p.equipment.Primary, p, out Vector3 mOff);
            foreach (int w in Snap("p4_fire_t2_off_close", p.Position, 4.5f, 2))
            {
                yield return w;
            }
            IsoRim.Api.Dev.PosturesOn = true;
            foreach (int w in Wait(4))
            {
                yield return w;
            }
            Out("  muzzle standing " + mOff.ToString("F3") + ", crouched " + mOn.ToString("F3") + ", difference " + (mOff.y - mOn.y).ToString("0.000") + ", drop " + drop.ToString("0.000"));
            Check("P4 firing: crouched (AttackStatic is CE's crouch job) and the muzzle comes down by about the shoulder drop (it may sit in the sandbags now: one fixed crouch)", rc == 1f && dc == 1f && onM && offM
                && Mathf.Abs((mOff.y - mOn.y) - drop) < 0.05f, "rule " + rc + ", drawn " + dc + ", muzzle y " + mOff.y.ToString("0.000") + " -> " + mOn.y.ToString("0.000") + ", drop " + drop.ToString("0.000"));
            yield return 300;
            List<Shot> hits = By(p, Impacts, t0);
            Out("  impacts " + hits.Count + ": " + Hits(hits));
            Check("P4 burst: shots fired and flew (CE unchanged)", By(p, Launches, t0).Count >= 3 && hits.Count > 0, "launches " + By(p, Launches, t0).Count + ", impacts " + hits.Count);
            Gone(p, target);
            Gone(bags);
            foreach (int w in Settle(100))
            {
                yield return w;
            }
        }

        // P5: crouch-walking under suppression: a drafted colonist (mood a little over its break: suppressed, not hunkering) fired at by a
        // mechanoid turret, then ordered to walk: CE's IsCrouchWalking while moving -> IsoRim's mild crouch.
        private static IEnumerator<int> PostureCrouchWalk()
        {
            Out("--- P5 crouch-walk under suppression");
            Wipe(40, 40, 70, 75);
            Pawn target = Target(new IntVec3(55, 0, 70));
            Pawn w = Soldier(new IntVec3(56, 0, 60));   // 1 cell off the path, well past the shooter's first 3 cells
            w.drafter.FireAtWill = false;   // (it does not fire back at the turret)
            float mid = w.mindState.mentalBreaker.BreakThresholdMajor + 0.05f;   // (suppressed by a burst, far from hunkering: ten times the threshold)
            SetMood(w, mid);
            Armour(w);
            Building_TurretGunCE shooter = Gun(new IntVec3(55, 0, 44));
            yield return 20;
            int t0 = Find.TickManager.TicksGame;
            bool walking = false, seen = false;
            float seenCrouch = 0f;
            string seenReport = "";
            for (int i = 0; i < 2400 && !seen; i++)
            {
                KeepFiring(shooter, target);
                if (i % 120 == 0) Out("  t" + i + ": shooter " + "forced " + shooter.ForcedTarget + " launches " + By(shooter, Launches, t0).Count + "; " + SuppText(w) + ", job " + w.CurJobDef?.defName);
                if (i % 120 == 0) Out("    tick " + Find.TickManager.TicksGame + " turret active " + shooter.Active + " current " + shooter.CurrentTarget + " verb " + shooter.AttackVerb?.state + " warm " + shooter.burstWarmupTicksLeft + " cool " + shooter.burstCooldownTicksLeft + " can hit " + shooter.AttackVerb?.CanHitTarget(target) + " power " + (shooter.TryGetComp<CompPowerTrader>()?.PowerOn.ToString() ?? "n/a") + " dormant awake " + (shooter.TryGetComp<CompCanBeDormant>()?.Awake.ToString() ?? "n/a") + " target at " + target.Position + " w at " + w.Position);
                SetMood(w, mid);
                Unhurt(w);
                CompSuppressable c = Supp(w);
                if (!walking && c != null && c.isSuppressed && Find.TickManager.TicksGame - t0 > 60)
                {
                    walking = true;
                    w.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(41, 0, 60)), JobTag.DraftedOrder);
                    Out("  suppressed at tick " + (Find.TickManager.TicksGame - t0) + ": " + SuppText(w) + "; ordered to walk west");
                }
                if (walking && !w.pather.MovingNow && w.CurJobDef != JobDefOf.Goto)
                {
                    // arrived before CE had it crouch-walking (the bursts come and go): walk back across the line of fire
                    w.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(w.Position.x < 50 ? 60 : 41, 0, 60)), JobTag.DraftedOrder);
                }
                if (walking && w.pather.MovingNow && c.IsCrouchWalking)
                {
                    foreach (int f in Wait(12))
                    {
                        yield return f;
                    }
                    Drawn(w, out seenCrouch, out _);
                    seenReport = Posture(w) + "; " + SuppText(w);
                    seen = true;
                    break;
                }
                yield return 1;
            }
            Out("  " + seenReport);
            Check("P5 crouch-walk: suppressed, moving, CE crouch-walking -> the half crouch", seen && Mathf.Abs(seenCrouch - 0.5f) < 0.01f, seen ? "drawn crouch " + seenCrouch : "never crouch-walking: " + SuppText(w) + ", job " + w.CurJobDef?.defName);
            if (seen)
            {
                foreach (int f in Snap("p5_walk_t2_on_play", w.Position, 12f, 2))
                {
                    yield return f;
                }
                foreach (int f in Snap("p5_walk_t2_on_close", w.Position, 4.5f, 2))
                {
                    yield return f;
                }
                IsoRim.Api.Dev.PosturesOn = false;
                foreach (int f in Snap("p5_walk_t2_off_close", w.Position, 4.5f, 2))
                {
                    yield return f;
                }
                IsoRim.Api.Dev.PosturesOn = true;
            }
            Gone(shooter, target, w);
            foreach (int f in Settle(100))
            {
                yield return f;
            }
        }

        // P6: hunkering: a drafted colonist at its mood's break threshold (CE's suppression threshold about 0, so any
        // suppression is ten times it: CE's HunkerDown). While CE first waits: the crouch; then face down toward
        // SuppressorLoc; after the fire stops and CE gets it up: easing back up, then the crouch again (drafted).
        private static IEnumerator<int> PostureHunker()
        {
            Out("--- P6 hunker");
            Wipe(80, 40, 110, 75);
            Pawn target = Target(new IntVec3(95, 0, 70));
            Pawn w = Soldier(new IntVec3(96, 0, 60));
            w.drafter.FireAtWill = false;
            Building_TurretGunCE shooter = Gun(new IntVec3(95, 0, 44));
            float low = w.mindState.mentalBreaker.BreakThresholdMajor + 0.002f;
            SetMood(w, low);
            Armour(w);
            Find.UIRoot.screenshotMode.Active = true;
            Find.CameraDriver.SetRootPosAndSize(w.Position.ToVector3Shifted(), 3f);   // (drawn: its facing is tracked as drawn)
            yield return 40;
            int t0 = Find.TickManager.TicksGame;
            bool sawWait = false, sawDown = false;
            string waitReport = "", downReport = "";
            float waitCrouch = -1f, waitProne = -1f;
            for (int i = 0; i < 900; i++)
            {
                KeepFiring(shooter, target);
                if (i % 120 == 0) Out("  t" + i + ": shooter " + "forced " + shooter.ForcedTarget + " launches " + By(shooter, Launches, t0).Count + "; " + SuppText(w) + ", job " + w.CurJobDef?.defName);
                SetMood(w, low);
                Unhurt(w);
                if (w.CurJobDef == CE_JobDefOf.HunkerDown)
                {
                    if (!sawWait && w.GetPosture() == PawnPosture.Standing)
                    {
                        sawWait = true;
                        foreach (int f in Wait(15))
                        {
                            yield return f;
                        }
                        Drawn(w, out waitCrouch, out waitProne);
                        waitReport = Posture(w);
                        foreach (int f in Snap("p6_hunker_prewait_t2_on_close", w.Position, 4.5f, 2))
                        {
                            yield return f;
                        }
                    }
                    if (w.GetPosture() != PawnPosture.Standing)
                    {
                        sawDown = true;
                        break;
                    }
                }
                yield return 1;
            }
            Out("  pre-wait: " + waitReport);
            Check("P6a hunker pre-wait (posture still standing): the crouch, not standing, not yet down", sawWait && waitCrouch == 1f && waitProne == 0f, "seen " + sawWait + ", crouch " + waitCrouch + ", prone " + waitProne);
            float downProne = -1f;
            float? yaw = null;
            float want = 0f;
            if (sawDown)
            {
                foreach (int f in Wait(30))
                {
                    yield return f;
                }
                Drawn(w, out _, out downProne);
                downReport = Posture(w);
                yaw = IsoRim.Api.Dev.DrawnYaw(w, out int age);
                IntVec3 from = Supp(w).SuppressorLoc;
                want = (from.ToVector3Shifted() - w.DrawPos).AngleFlat();
                Out("  down: " + downReport + "; drawn yaw " + (yaw.HasValue ? yaw.Value.ToString("0") : "-") + ", toward the suppressor " + want.ToString("0") + " (" + from + ")");
                foreach (int f in Pair("p6_hunker_down", w.Position))
                {
                    yield return f;
                }
                foreach (int f in Snap("p6_hunker_down_t2_ui", w.Position, 8f, 2, true, w))
                {
                    yield return f;
                }
            }
            Check("P6b hunkering: face down, head toward the suppressing fire", sawDown && downProne == 1f && yaw.HasValue && Mathf.Abs(Mathf.DeltaAngle(yaw.Value, want)) < 20f,
                "down " + sawDown + ", prone " + downProne + ", yaw " + (yaw.HasValue ? yaw.Value.ToString("0") : "-") + " want " + want.ToString("0"));
            // the fire stops; CE gets the pawn up when the suppression is gone
            Gone(shooter, target);
            SetMood(w, 1f);
            bool up = false;
            for (int i = 0; i < 1500 && !up; i++)
            {
                SetMood(w, 1f);
                Unhurt(w);
                up = w.CurJobDef != CE_JobDefOf.HunkerDown && w.GetPosture() == PawnPosture.Standing;
                if (!up)
                {
                    yield return 1;
                }
            }
            float midProne = -1f, endProne = -1f, endCrouch = -1f;
            if (up)
            {
                Find.UIRoot.screenshotMode.Active = true;   // (no UI on the get-up frame)
                Find.Selector.ClearSelection();
                Find.CameraDriver.SetRootPosAndSize(w.Position.ToVector3Shifted(), 2.25f);
                Drawn(w, out _, out _);
                float tUp = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tUp < 0.12f)
                {
                    yield return -1;
                }
                Drawn(w, out _, out midProne);
                string path = Path.Combine(outDir, "p6_hunker_getup_t2_on_close.png");
                ScreenCapture.CaptureScreenshot(path);
                for (int i = 0; i < 300 && !(File.Exists(path) && new FileInfo(path).Length > 0); i++)
                {
                    yield return -1;
                }
                foreach (int f in Wait(60))
                {
                    yield return f;
                }
                Drawn(w, out endCrouch, out endProne);
            }
            Out("  up: " + (up ? Posture(w) : "never got up: " + SuppText(w) + ", job " + w.CurJobDef?.defName) + "; prone a few frames after getting up " + midProne.ToString("0.00"));
            Check("P6c getting up: eased (still part way down a few frames after), then the drafted crouch", up && midProne > 0f && midProne < 1f && endProne == 0f && endCrouch == 1f,
                "up " + up + ", prone after a few frames " + midProne.ToString("0.00") + ", at the end prone " + endProne + " crouch " + endCrouch);
            Gone(w);
            foreach (int f in Settle(100))
            {
                yield return f;
            }
        }

        // P7: upstairs on an IsoRimZ floor: behind sandbags and behind an embrasure, the same fixed crouch.
        private static IEnumerator<int> PostureUpstairs()
        {
            Out("--- P7 upstairs");
            if (L == null || !L.Has(1))
            {
                Check("P7 upstairs", false, "needs a map with floors");
                yield break;
            }
            Wipe(120, 10, 150, 30);
            Slab(125, 14, 145, 26);
            Thing[] bags = { Build("Sandbags", On(129, 21, 1)), Build("Sandbags", On(130, 21, 1)), Build("Sandbags", On(131, 21, 1)) };
            Thing[] walls = { Build("CE_Embrasure", On(139, 21, 1)), Build("CE_Embrasure", On(140, 21, 1)), Build("CE_Embrasure", On(141, 21, 1)) };
            // a sandbag on the ground under them (kept from the cover-reading version: the fixture stays the same)
            Thing under = Build("Sandbags", new IntVec3(130, 0, 19));
            Pawn a = Soldier(On(130, 20, 1));
            Pawn b = Soldier(On(140, 20, 1));
            yield return 60;
            ProjectZ.Api.SetViewedFloor(1);
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            float da = Drawn(a, out float ca, out _), db = Drawn(b, out float cb, out _);
            Out("  behind sandbags: " + Posture(a));
            Out("  behind the embrasure: " + Posture(b));
            Check("P7 upstairs: the fixed crouch behind sandbags and behind the embrasure", ca == 1f && Mathf.Abs(da - FixedDrop) < 0.005f && cb == 1f && Mathf.Abs(db - FixedDrop) < 0.005f,
                "sandbags drop " + da.ToString("0.000") + ", embrasure drop " + db.ToString("0.000") + " (fixed " + FixedDrop.ToString("0.000") + ")");
            foreach (int w in Pair("p7_upstairs", new IntVec3(135, 0, 20), false, ProjectZ.Api.StoreyHeight))
            {
                yield return w;
            }
            foreach (int w in Pair("p7_upstairs_bags", new IntVec3(130, 0, 20), true, ProjectZ.Api.StoreyHeight))
            {
                yield return w;
            }
            ProjectZ.Api.SetViewedFloor(0);
            Gone(a, b, under);
            Gone(bags);
            Gone(walls);
        }

        // P8: a drafted colonist standing on a chair is seated (IsoRim's sitting pose); CE's crouch is not applied on top.
        private static IEnumerator<int> PostureSeated()
        {
            Out("--- P8 seated");
            Wipe(10, 80, 30, 100);
            Thing chair = Build("DiningChair", new IntVec3(20, 0, 90));
            Pawn p = Soldier(new IntVec3(20, 0, 90));
            yield return 60;
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            IsoRim.Api.PostureOf(p, out float rc, out _);
            Drawn(p, out float dc, out _);
            Out("  " + Posture(p));
            Check("P8 seated: CE says crouch, IsoRim keeps it seated (no posture drawn)", rc == 1f && dc == 0f, "rule " + rc + ", drawn " + dc);
            foreach (int w in Snap("p8_seated_t2_on_close", p.Position, 4.5f, 2))
            {
                yield return w;
            }
            Gone(p, chair);
        }

        // P9: 2D cards (3D figures off): no posture, drawn exactly as the game draws them.
        private static IEnumerator<int> PostureCards()
        {
            Out("--- P9 cards");
            Wipe(40, 80, 60, 100);
            Thing[] bags = { Build("Sandbags", new IntVec3(53, 0, 91)), Build("Sandbags", new IntVec3(54, 0, 91)) };
            Pawn a = Soldier(new IntVec3(47, 0, 90));
            Pawn b = Soldier(new IntVec3(53, 0, 90));
            yield return 60;
            IsoRim.IsoRimMod.Settings.pawns3D = false;
            foreach (int w in Wait(10))
            {
                yield return w;
            }
            float drop = Drawn(a, out float ca, out _);
            Out("  card in the open: " + Posture(a));
            Out("  card behind sandbags: " + Posture(b));
            Check("P9 cards: no posture on cards", ca == 0f && drop == 0f, "crouch " + ca + ", drop " + drop.ToString("0.000"));
            foreach (int w in Pair("p9_cards", new IntVec3(50, 0, 90), false))
            {
                yield return w;
            }
            IsoRim.IsoRimMod.Settings.pawns3D = true;
            Gone(a, b);
            Gone(bags);
        }

        // P10: 50 drafted colonists in view, behind sandbag rows: frame time with postures on and off, and IsoRim's own
        // posture work per pawn and frame.
        private static IEnumerator<int> PosturePerf()
        {
            Out("--- P10 50 pawns");
            Wipe(70, 80, 110, 110);
            List<Thing> all = new List<Thing>();
            for (int r = 0; r < 5; r++)
            {
                for (int k = 0; k < 10; k++)
                {
                    IntVec3 c = new IntVec3(80 + 2 * k, 0, 84 + 4 * r);
                    if ((k & 1) == 0)
                    {
                        all.Add(Build("Sandbags", c + IntVec3.North));
                    }
                    all.Add(Soldier(c));
                }
            }
            yield return 60;
            Find.UIRoot.screenshotMode.Active = true;
            Find.CameraDriver.SetRootPosAndSize(new Vector3(90f, 0f, 93f), 13f);
            foreach (int w in Wait(60))
            {
                yield return w;
            }
            double[] ft = new double[4];
            for (int round = 0; round < 4; round++)
            {
                bool on = (round & 1) == 0;
                IsoRim.Api.Dev.PosturesOn = on;
                foreach (int w in Wait(30))
                {
                    yield return w;
                }
                IsoRim.Api.Dev.ResetPostureCost();
                IsoRim.Api.Dev.PostureTiming = true;
                float t0 = Time.realtimeSinceStartup;
                foreach (int w in Wait(300))
                {
                    yield return w;
                }
                ft[round] = (Time.realtimeSinceStartup - t0) * 1000.0 / 300.0;
                IsoRim.Api.Dev.PostureTiming = false;
                IsoRim.Api.Dev.PostureCost(out int works, out double ms);
                Out("  round " + round + " postures " + (on ? "on" : "off") + ": frame " + ft[round].ToString("0.00") + " ms, posture works " + works + " (" + (works / 300.0).ToString("0.0")
                    + " per frame), " + ms.ToString("0.00") + " ms in all = " + (works > 0 ? (ms * 1000.0 / works).ToString("0.00") : "-") + " us per pawn and frame, "
                    + (ms / 300.0).ToString("0.000") + " ms per frame");
            }
            Pawn first = (Pawn)all.First(t => t is Pawn);
            Out("  sample: " + Posture(first) + "; pawns on the map " + Map.mapPawns.AllPawnsSpawnedCount);
            Check("P10 perf: 50 pawns, postures on vs off within frame noise", (ft[0] + ft[2]) * 0.5 - (ft[1] + ft[3]) * 0.5 < 2.0, "on " + ft[0].ToString("0.00") + "/" + ft[2].ToString("0.00") + " ms, off " + ft[1].ToString("0.00") + "/" + ft[3].ToString("0.00") + " ms");
            foreach (int w in Snap("p10_perf_t2_on_play", new IntVec3(90, 0, 93), 26f, 2))
            {
                yield return w;
            }
            IsoRim.Api.Dev.PosturesOn = true;
            Gone(all.ToArray());
        }

        // P11: a drafted colonist mounted on a horse (Giddy-Up, through its IsoRim pack: attached to its host, Api 13): CE may
        // report the rider crouching; IsoRim keeps the rider's mount pose (no posture drawn). Only with Giddy-Up loaded.
        private static IEnumerator<int> PostureRider()
        {
            Out("--- P11 rider");
            Type mu = AccessTools.TypeByName("GiddyUp.MountUtility");
            System.Reflection.MethodInfo go = mu != null ? AccessTools.Method(mu, "GoMount") : null;
            if (go == null)
            {
                Out("  Giddy-Up not loaded: skipped");
                yield break;
            }
            Wipe(120, 40, 140, 60);
            PawnKindDef horseKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Horse") ?? DefDatabase<PawnKindDef>.GetNamed("Muffalo");
            Pawn horse;
            Rand.PushState(pawnSeed++);
            try
            {
                horse = PawnGenerator.GeneratePawn(new PawnGenerationRequest(horseKind, Faction.OfPlayer, forceGenerateNewPawn: true));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(horse, new IntVec3(130, 0, 50), Map);
            horse.training?.Train(TrainableDefOf.Tameness, null, true);
            Pawn p = Soldier(new IntVec3(130, 0, 50));
            object instant = Enum.Parse(go.GetParameters()[2].ParameterType, "Instant");
            go.Invoke(null, new object[] { p, horse, instant, null, null });
            yield return 60;
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            IsoRim.Api.PostureOf(p, out float rc, out _);
            Drawn(p, out float dc, out _);
            bool attached = IsoRim.Api.AttachmentOf(p).HasValue;
            Out("  " + Posture(p) + "; attached " + attached + ", CE crouching " + p.IsCrouching());
            Check("P11 rider: attached to its mount, no posture drawn over the mount pose", attached && dc == 0f, "attached " + attached + ", rule " + rc + ", drawn " + dc);
            foreach (int w in Pair("p11_rider", p.Position))
            {
                yield return w;
            }
            Gone(p, horse);
        }
    }
}
