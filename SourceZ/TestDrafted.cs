using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace IsoRimCEPackZ
{
    // Test harness only (mode "drafted", -cepacktest=drafted): the drafted-idle rule of the pack's Posture.cs. Drafted and idle
    // (CE's crouch job Wait_Combat) a colonist is drawn crouching only while the map has an active hostile threat (the game's
    // GenHostility.AnyHostileActiveThreatToPlayer, asked once a second) or while it aims or fires; every other CE crouch is
    // unchanged; CE's own IsCrouching (its gameplay) stays as CE has it. Real flow: a real raid, real orders and jobs, real
    // ticks; each check its own fixture and seed (the runner's).
    public static partial class TestZ
    {
        private static IEnumerable<IEnumerator<int>> DraftedChecks()
        {
            Func<IEnumerator<int>>[] all = { DraftPeace, DraftRaid, DraftMechCluster, DraftHive, DraftNonThreats, DraftManhunter, DraftAutoAttack, DraftAttackOrder, DraftReload, DraftManTurret };
            string[] only = GenCommandLine.TryGetCommandLineArg("cedrafted", out string sel) && sel != null ? sel.Split(',') : null;
            for (int i = 0; i < all.Length; i++)
            {
                yield return only == null || only.Contains((i + 1).ToString()) ? all[i]() : Nothing();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static float RuleOf(Pawn p)
        {
            IsoRim.Api.PostureOf(p, out float c, out bool prone);
            return prone ? -1f : c;
        }

        private static bool Vanilla() => GenHostility.AnyHostileActiveThreatToPlayer(Map);

        private static System.Reflection.MethodInfo cachedThreat;
        private static System.Reflection.FieldInfo threatAsks;

        // The pack's cached threat flag (no new ask) and its ask counter (Posture.cs, through reflection: the Z DLL does not
        // reference the pack's).
        private static bool Cached() => (bool)(cachedThreat ??= AccessTools.Method(AccessTools.TypeByName("IsoRimCEPack.Posture"), "CachedThreat")).Invoke(null, new object[] { Map });

        private static int Asks() => (int)(threatAsks ??= AccessTools.Field(AccessTools.TypeByName("IsoRimCEPack.Posture"), "ThreatAsks")).GetValue(null);

        private static bool AimingNow(Pawn p) => p.stances.curStance is Stance_Busy b && b.verb != null && !b.verb.IsMeleeAttack;

        private static string State(Pawn p)
        {
            Drawn(p, out float dc, out float dp);
            return "job " + p.CurJobDef?.defName + ", stance " + p.stances.curStance?.GetType().Name + ", CE crouching " + p.IsCrouching() + ", rule " + RuleOf(p) + ", drawn " + dc.ToString("0.00")
                + (dp > 0f ? " prone " + dp.ToString("0.00") : "") + ", threat (vanilla " + Vanilla() + ", cached " + Cached() + ")";
        }

        // Ticks (one per frame, so the drawn posture follows) until the pawn is drafted idle (CE reloads a fresh gun first).
        private static IEnumerable<int> UntilIdle(params Pawn[] ps)
        {
            for (int i = 0; i < 900 && ps.Any(p => p.CurJobDef != JobDefOf.Wait_Combat); i++)
            {
                yield return 1;
            }
            foreach (int w in Wait(20))
            {
                yield return w;
            }
        }

        private static void Camera(IntVec3 c, float size = 6f)
        {
            Find.UIRoot.screenshotMode.Active = true;
            Find.CameraDriver.SetRootPosAndSize(c.ToVector3Shifted(), size);
        }

        // A hostile humanlike of the enemy faction, unarmed, awake, held where it is (on a Wait job).
        private static Pawn Hostile(IntVec3 c, string weapon = null)
        {
            Pawn p;
            Rand.PushState(pawnSeed++);
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
            return p;
        }

        // ------------------------------------------------------------------ checks

        // D1: a peaceful map, drafted and idle: stands (CE still counts it crouching: its gameplay unchanged).
        private static IEnumerator<int> DraftPeace()
        {
            Out("--- D1 peace");
            Wipe(10, 10, 30, 30);
            Pawn p = Soldier(new IntVec3(20, 0, 20));
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            Drawn(p, out float dc, out _);
            Out("  " + State(p));
            Check("D1 peace: drafted idle stands (CE's IsCrouching still true)", p.CurJobDef == JobDefOf.Wait_Combat && RuleOf(p) == 0f && dc == 0f && p.IsCrouching() && !Vanilla(), State(p));
            foreach (int w in Snap("d1_peace_t2_close", p.Position, 4.5f, 2))
            {
                yield return w;
            }
            Gone(p);
        }

        // D2: a real raid (the game's RaidEnemy incident, walking in at an edge, attacking at once): the drafted idle colonists
        // crouch within about a second; all raiders downed: they stand again within about a second; raiders gone: still stand.
        private static IEnumerator<int> DraftRaid()
        {
            Out("--- D2 raid");
            Wipe(115, 115, 135, 135);
            Pawn a = Soldier(new IntVec3(124, 0, 125)), b = Soldier(new IntVec3(126, 0, 125));
            Camera(a.Position);
            foreach (int w in UntilIdle(a, b))
            {
                yield return w;
            }
            Out("  before: " + State(a));
            Check("D2a before the raid: both stand", RuleOf(a) == 0f && RuleOf(b) == 0f && !Vanilla(), State(a) + " / " + State(b));
            foreach (int w in Snap("d2_peace_t2_close", new IntVec3(125, 0, 125), 4.5f, 2))
            {
                yield return w;
            }
            foreach (int w in Snap("d2_peace_t2_play", new IntVec3(125, 0, 125), 12f, 2))
            {
                yield return w;
            }
            Faction enemy = Enemy;
            HashSet<Pawn> before = new HashSet<Pawn>(Map.mapPawns.AllPawnsSpawned);
            IncidentParms parms = new IncidentParms
            {
                target = Map,
                faction = enemy,
                points = 300f,
                raidStrategy = RaidStrategyDefOf.ImmediateAttack,
                raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn,
                forced = true,
            };
            bool fired = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
            int t0 = Find.TickManager.TicksGame;
            List<Pawn> raiders = Map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p) && p.Faction == enemy).ToList();
            Out("  raid fired " + fired + " by " + enemy?.Name + ": " + raiders.Count + " raiders at " + string.Join(" ", raiders.Select(r => r.Position.ToString())) + ", nearest "
                + (raiders.Count > 0 ? raiders.Min(r => r.Position.DistanceTo(a.Position)).ToString("0") : "-") + " cells");
            Camera(a.Position);
            int vanillaAt = -1, crouchAt = -1;
            for (int i = 0; i < 300 && crouchAt < 0; i++)
            {
                if (vanillaAt < 0 && Vanilla())
                {
                    vanillaAt = Find.TickManager.TicksGame - t0;
                }
                if (RuleOf(a) == 1f && RuleOf(b) == 1f)
                {
                    crouchAt = Find.TickManager.TicksGame - t0;
                    break;
                }
                yield return 1;
            }
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            Drawn(a, out float dca, out _);
            Drawn(b, out float dcb, out _);
            Out("  raid: " + State(a) + "; vanilla threat after " + vanillaAt + " ticks, both crouch after " + crouchAt + " ticks, nearest raider "
                + raiders.Where(r => r.Spawned).Select(r => r.Position.DistanceTo(a.Position)).DefaultIfEmpty(-1f).Min().ToString("0") + " cells");
            Check("D2b raid: both drafted idle colonists crouch within about a second", fired && raiders.Count > 0 && crouchAt >= 0 && crouchAt <= 61 && dca == 1f && dcb == 1f
                && a.CurJobDef == JobDefOf.Wait_Combat && b.CurJobDef == JobDefOf.Wait_Combat, "crouch after " + crouchAt + " ticks, drawn " + dca + "/" + dcb + ", " + State(a));
            foreach (int w in Snap("d2_raid_t2_close", new IntVec3(125, 0, 125), 4.5f, 2))
            {
                yield return w;
            }
            foreach (int w in Snap("d2_raid_t2_play", new IntVec3(125, 0, 125), 12f, 2))
            {
                yield return w;
            }
            // every raider downed
            int t1 = Find.TickManager.TicksGame;
            foreach (Pawn r in raiders.Where(r => r.Spawned && !r.Downed && !r.Dead))
            {
                HealthUtility.DamageUntilDowned(r, false);
            }
            Out("  raiders downed " + raiders.Count(r => r.Downed) + ", dead " + raiders.Count(r => r.Dead) + " of " + raiders.Count);
            int standAt = -1;
            for (int i = 0; i < 300; i++)
            {
                if (RuleOf(a) == 0f && RuleOf(b) == 0f)
                {
                    standAt = Find.TickManager.TicksGame - t1;
                    break;
                }
                yield return 1;
            }
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            Drawn(a, out dca, out _);
            Drawn(b, out dcb, out _);
            Out("  downed: " + State(a) + "; stand after " + standAt + " ticks");
            Check("D2c all raiders downed: both stand again within about a second", standAt >= 0 && standAt <= 61 && dca == 0f && dcb == 0f && !Vanilla()
                && a.CurJobDef == JobDefOf.Wait_Combat, "stand after " + standAt + " ticks, drawn " + dca + "/" + dcb + ", " + State(a));
            foreach (int w in Snap("d2_after_t2_close", new IntVec3(125, 0, 125), 4.5f, 2))
            {
                yield return w;
            }
            // the raiders gone
            Gone(raiders.Where(r => !r.Destroyed).ToArray());
            yield return 90;
            Check("D2d raiders gone: still standing", RuleOf(a) == 0f && RuleOf(b) == 0f && !Vanilla(), State(a));
            Gone(a, b);
        }

        // D3: a dormant mech cluster on the map (the game's own cluster sketch and spawn, dormant, no pods): standing.
        private static IEnumerator<int> DraftMechCluster()
        {
            Out("--- D3 dormant mech cluster");
            if (!ModsConfig.RoyaltyActive)
            {
                Out("  Royalty not active: skipped");
                yield break;
            }
            Wipe(150, 150, 210, 210);
            Wipe(200, 225, 215, 240);
            Pawn p = Soldier(new IntVec3(208, 0, 232));
            MechClusterSketch sketch = null;
            for (int i = 0; i < 20 && (sketch == null || sketch.pawns == null || sketch.pawns.Count == 0); i++)   // (one with mechs: pawns are a chance)
            {
                sketch = MechClusterGenerator.GenerateClusterSketch(1500f, Map, startDormant: true, forceNoConditionCauser: true);
            }
            List<Thing> cluster = MechClusterUtility.SpawnCluster(new IntVec3(175, 0, 175), Map, sketch, dropInPods: false, canAssaultColony: false);
            List<Pawn> mechs = cluster.OfType<Pawn>().ToList();
            Out("  cluster: " + cluster.Count + " things, " + mechs.Count + " mechs, buildings " + string.Join(",", cluster.Where(t => !(t is Pawn)).Select(t => t.def.defName).Distinct()) + ", nearest "
                + cluster.Min(t => t.Position.DistanceTo(p.Position)).ToString("0") + " cells");
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            bool always = true;
            for (int i = 0; i < 240; i++)
            {
                always &= RuleOf(p) == 0f;
                yield return 1;
            }
            // (the cluster is there and hostile, only asleep: its mechs dormant, its turrets and buildings dormant)
            bool dormantHostile = GenHostility.AnyHostileActiveThreatToPlayer(Map, countDormantPawnsAsHostile: true)
                || cluster.Any(t => t.Spawned && t.HostileTo(Faction.OfPlayer) && t.TryGetComp<CompCanBeDormant>() is CompCanBeDormant d && !d.Awake);
            Out("  " + State(p) + ", dormant hostiles on the map " + dormantHostile + ", mechs awake " + mechs.Count(m => m.TryGetComp<CompCanBeDormant>()?.Awake ?? true));
            Check("D3 dormant mech cluster: standing", always && RuleOf(p) == 0f && !Vanilla() && dormantHostile && p.CurJobDef == JobDefOf.Wait_Combat, State(p) + ", dormant hostiles " + dormantHostile);
            Gone(cluster.Where(t => !t.Destroyed).ToArray());
            Gone(p);
        }

        // D4: an insect hive put to sleep (the game's HiveUtility.SpawnHive, dormant) with its insects (its own spawner, in the
        // hive's lord): standing.
        private static IEnumerator<int> DraftHive()
        {
            Out("--- D4 sleeping hive");
            Wipe(20, 150, 50, 180);
            Wipe(20, 220, 40, 240);
            Pawn p = Soldier(new IntVec3(30, 0, 232));
            Hive hive = HiveUtility.SpawnHive(new IntVec3(35, 0, 165), Map, WipeMode.Vanish, spawnInsectsImmediately: false, dormant: true, spawnSludge: false);
            hive.PawnSpawner.SpawnPawnsUntilPoints(300f);
            hive.CompDormant.ToSleep();
            List<Pawn> insects = Map.mapPawns.AllPawnsSpawned.Where(x => x.Faction == Faction.OfInsects).ToList();
            Out("  hive " + hive.Position + " dormant " + !hive.CompDormant.Awake + ", insects " + insects.Count + " (" + string.Join(",", insects.Select(x => x.kindDef.defName)) + "), lord "
                + insects.FirstOrDefault()?.GetLord()?.LordJob?.GetType().Name + ", nearest " + insects.Select(x => x.Position.DistanceTo(p.Position)).DefaultIfEmpty(-1f).Min().ToString("0") + " cells");
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            bool always = true;
            for (int i = 0; i < 240; i++)
            {
                always &= RuleOf(p) == 0f;
                yield return 1;
            }
            Out("  " + State(p));
            Check("D4 sleeping hive and its insects: standing", always && RuleOf(p) == 0f && !Vanilla() && insects.Count > 0 && p.CurJobDef == JobDefOf.Wait_Combat, State(p) + ", insects " + insects.Count);
            Gone(insects.Where(x => !x.Destroyed).Cast<Thing>().Append(hive).ToArray());
            Gone(p);
        }

        // D5: a wild animal, a prisoner and a downed enemy on the map: standing.
        private static IEnumerator<int> DraftNonThreats()
        {
            Out("--- D5 wild animal, prisoner, downed enemy");
            Wipe(60, 10, 100, 40);
            Pawn p = Soldier(new IntVec3(70, 0, 20));
            Pawn animal = Target(new IntVec3(80, 0, 30));
            Pawn prisoner = Hostile(new IntVec3(90, 0, 20));
            held.RemoveAll(h => h.pawn == prisoner);
            prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            Pawn downed = Hostile(new IntVec3(95, 0, 30));
            held.RemoveAll(h => h.pawn == downed);
            HealthUtility.DamageUntilDowned(downed, false);
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            bool always = true;
            for (int i = 0; i < 180; i++)
            {
                always &= RuleOf(p) == 0f;
                yield return 1;
            }
            Out("  " + State(p) + "; prisoner " + prisoner.IsPrisoner + ", downed " + downed.Downed + " dead " + downed.Dead);
            Check("D5 wild animal, prisoner, downed enemy: standing", always && RuleOf(p) == 0f && !Vanilla() && prisoner.IsPrisoner && (downed.Downed || downed.Dead), State(p));
            Gone(p, animal, prisoner, downed);
        }

        // D6: a manhunter animal far away: crouching within about a second; the animal dead: standing again.
        private static IEnumerator<int> DraftManhunter()
        {
            Out("--- D6 manhunter");
            Wipe(190, 20, 210, 40);
            Wipe(190, 150, 210, 170);
            Pawn p = Soldier(new IntVec3(200, 0, 30));
            p.drafter.FireAtWill = false;
            Pawn animal;
            Rand.PushState(pawnSeed++);
            try
            {
                animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), null, forceGenerateNewPawn: true));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(animal, new IntVec3(200, 0, 160), Map);
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            Check("D6a the animal wild (not manhunting): standing", RuleOf(p) == 0f && !Vanilla(), State(p));
            bool started = animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Manhunter, forced: true);
            int t0 = Find.TickManager.TicksGame, crouchAt = -1;
            for (int i = 0; i < 300; i++)
            {
                if (RuleOf(p) == 1f)
                {
                    crouchAt = Find.TickManager.TicksGame - t0;
                    break;
                }
                yield return 1;
            }
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            Drawn(p, out float dc, out _);
            Out("  manhunter " + started + " (" + animal.MentalStateDef?.defName + ") at " + animal.Position.DistanceTo(p.Position).ToString("0") + " cells: " + State(p) + ", crouch after " + crouchAt + " ticks");
            Check("D6b a manhunter: crouching within about a second", started && crouchAt >= 0 && crouchAt <= 61 && dc == 1f && p.CurJobDef == JobDefOf.Wait_Combat, "crouch after " + crouchAt + ", " + State(p));
            animal.Kill(null);
            int t1 = Find.TickManager.TicksGame, standAt = -1;
            for (int i = 0; i < 300; i++)
            {
                if (RuleOf(p) == 0f)
                {
                    standAt = Find.TickManager.TicksGame - t1;
                    break;
                }
                yield return 1;
            }
            Check("D6c the manhunter dead: standing again within about a second", standAt >= 0 && standAt <= 61 && !Vanilla(), "stand after " + standAt + ", " + State(p));
            Gone(p);
            foreach (Thing c in Map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Where(x => x is Corpse cp && cp.InnerPawn == animal).ToList())
            {
                c.Destroy(DestroyMode.Vanish);
            }
        }

        // D7: auto-attack from Wait_Combat: a hostile comes into range right after the pack asked the map (its flag still off
        // for up to a second): crouched from the aim's warmup on, the flag on or not; firing: crouched.
        private static IEnumerator<int> DraftAutoAttack()
        {
            Out("--- D7 auto-attack from Wait_Combat");
            Wipe(110, 10, 140, 50);
            Pawn p = Soldier(new IntVec3(125, 0, 20));
            Armour(p);
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            // just after an ask (the flag fresh: off)
            int asks = Asks();
            for (int i = 0; i < 120 && Asks() == asks; i++)
            {
                RuleOf(p);
                if (Asks() != asks)
                {
                    break;
                }
                yield return 1;
            }
            Pawn foe = Hostile(new IntVec3(125, 0, 38));
            int t0 = Find.TickManager.TicksGame;
            Out("  hostile at " + foe.Position.DistanceTo(p.Position).ToString("0") + " cells, flag cached " + Cached() + ", vanilla " + Vanilla());
            int aimAt = -1;
            float aimRule = -1f;
            bool aimFlag = true;
            string aimState = "";
            for (int i = 0; i < 300; i++)
            {
                Unhurt(foe);
                if (p.CurJobDef == JobDefOf.Wait_Combat && AimingNow(p))
                {
                    aimAt = Find.TickManager.TicksGame - t0;
                    aimFlag = Cached();
                    aimRule = RuleOf(p);
                    aimState = State(p);
                    break;
                }
                yield return 1;
            }
            Out("  aiming after " + aimAt + " ticks: " + aimState);
            Check("D7a auto-attack (Wait_Combat, the aim's warmup): crouched" + (aimFlag ? " (the map's flag already on)" : " with the map's flag still off"), aimAt >= 0 && aimRule == 1f, aimState);
            int t1 = Find.TickManager.TicksGame;
            bool fired = false;
            for (int i = 0; i < 400 && !fired; i++)
            {
                Unhurt(foe);
                fired = By(p, Launches, t1).Count > 0;
                yield return 1;
            }
            foreach (int w in Wait(10))
            {
                yield return w;
            }
            Drawn(p, out float dc, out _);
            Out("  firing: " + State(p) + ", launches " + By(p, Launches, t1).Count);
            Check("D7b auto-attack firing: crouched", fired && RuleOf(p) == 1f && dc == 1f, State(p) + ", launches " + By(p, Launches, t1).Count);
            Gone(p, foe);
            foreach (int f in Settle(100))
            {
                yield return f;
            }
        }

        // D8: a direct attack order (the targeting gizmo) at a neutral animal on a peaceful map: AttackStatic, crouched.
        private static IEnumerator<int> DraftAttackOrder()
        {
            Out("--- D8 attack order, peaceful map");
            Wipe(150, 10, 180, 50);
            Pawn target = Target(new IntVec3(165, 0, 34));
            Pawn p = Soldier(new IntVec3(165, 0, 20));
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            int t0 = Find.TickManager.TicksGame;
            Order(p, target);
            for (int i = 0; i < 400 && By(p, Launches, t0).Count == 0; i++)
            {
                yield return 1;
            }
            foreach (int w in Wait(10))
            {
                yield return w;
            }
            Drawn(p, out float dc, out _);
            Out("  " + State(p) + ", launches " + By(p, Launches, t0).Count);
            Check("D8 attack order, no threat: crouched (AttackStatic)", p.CurJobDef == JobDefOf.AttackStatic && RuleOf(p) == 1f && dc == 1f && !Vanilla() && !Cached() && By(p, Launches, t0).Count > 0, State(p));
            Gone(p, target);
            foreach (int f in Settle(100))
            {
                yield return f;
            }
        }

        // D9: reloading on a peaceful map: after a burst, the gun's reload gizmo (CompAmmoUser.TryStartReload): crouched.
        private static IEnumerator<int> DraftReload()
        {
            Out("--- D9 reload, peaceful map");
            Wipe(190, 60, 220, 100);
            Pawn target = Target(new IntVec3(205, 0, 84));
            Pawn p = Soldier(new IntVec3(205, 0, 70));
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            int t0 = Find.TickManager.TicksGame;
            Order(p, target);
            for (int i = 0; i < 400 && By(p, Launches, t0).Count < 3; i++)
            {
                yield return 1;
            }
            Gone(target);
            p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            CompAmmoUser ammo = p.equipment.Primary.TryGetComp<CompAmmoUser>();
            Out("  idle again: " + State(p) + ", magazine " + ammo?.CurMagCount + "/" + ammo?.MagSize);
            ammo?.TryStartReload();
            bool seen = false;
            float rule = -1f, dc = -1f;
            string st = "";
            for (int i = 0; i < 120 && !seen; i++)
            {
                if (p.CurJobDef != null && p.CurJobDef.defName == "ReloadWeapon")
                {
                    foreach (int w in Wait(10))
                    {
                        yield return w;
                    }
                    rule = RuleOf(p);
                    Drawn(p, out dc, out _);
                    st = State(p);
                    seen = true;
                    break;
                }
                yield return 1;
            }
            Out("  reloading: " + st);
            Check("D9 reload, no threat: crouched (CE's ReloadWeapon)", seen && rule == 1f && dc == 1f && !Vanilla(), seen ? st : "no reload job: " + State(p));
            Gone(p);
        }

        // D10: manning a turret (the ManTurret order) on a peaceful map: crouched.
        private static IEnumerator<int> DraftManTurret()
        {
            Out("--- D10 man a turret, peaceful map");
            Wipe(60, 60, 90, 90);
            Thing mortar = Build("Turret_Mortar", new IntVec3(75, 0, 75));
            Pawn p = Soldier(new IntVec3(70, 0, 70));
            Camera(p.Position);
            foreach (int w in UntilIdle(p))
            {
                yield return w;
            }
            p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar), JobTag.DraftedOrder);
            bool seen = false;
            for (int i = 0; i < 600 && !seen; i++)
            {
                seen = p.CurJobDef == JobDefOf.ManTurret && !p.pather.MovingNow && p.Position == ((Building)mortar).InteractionCell;
                yield return 1;
            }
            Camera(p.Position);
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            Drawn(p, out float dc, out _);
            Out("  " + State(p));
            Check("D10 manning a turret, no threat: crouched (ManTurret)", seen && p.CurJobDef == JobDefOf.ManTurret && RuleOf(p) == 1f && dc == 1f && !Vanilla(), State(p));
            Gone(p, mortar);
        }
    }
}
