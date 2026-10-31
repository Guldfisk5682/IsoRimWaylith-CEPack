using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRimCEPackZ
{
    // Harness, P4 checks (mode p4): CIWS, turrets and CE's AI between floors, real orders and real ticks (see TestZ). A
    // hostile CIWS is a mechanoid charge blaster turret (CE: Building_CIWS_CE with a projectile CIWS verb) shooting down a
    // colonist's mortar shells; raiders are pawns of a hostile faction held in place without weapons.
    public static partial class TestZ
    {
        // -cepackonly=a,b,...: only those checks (P4 letters), for debugging; the full run is the record.
        private static IEnumerable<IEnumerator<int>> P4Checks()
        {
            string[] only = GenCommandLine.TryGetCommandLineArg("cepackonly", out string o) ? o.Split(',') : null;
            var all = new (string id, Func<IEnumerator<int>> run)[]
            {
                ("a", CiwsOpen), ("b", CiwsUnderSlab), ("c", () => CiwsUpstairs(false)), ("d", () => CiwsUpstairs(true)), ("e", AutoTurret), ("f", MannedForced),
                ("g", MortarUpDown), ("h", FireArc), ("i", NonSnap), ("j", RetargetCheck), ("k", TurretControl),
            };
            Check("P4 patch groups landed (CIWS, turrets, AI)", Ciws.On && Turrets.On && Ai.On, "CIWS " + Ciws.On + ", turrets " + Turrets.On + ", AI " + Ai.On + ", scope swaps " + Scope.Swapped);
            foreach ((string id, Func<IEnumerator<int>> run) in all)
            {
                if (only == null || only.Contains(id))
                {
                    yield return run();
                }
            }
        }

        private static void P4Counters()
        {
            Out("P4 counters: CIWS translated " + Ciws.Translated + ", line checks " + Ciws.LineChecks + ", blocked points " + Ciws.BlockedPoints + ", paths cut at a floor " + Ciws.CutPaths + ", top aims " + Ciws.TopAims
                + "; turret orders across " + Turrets.OrdersAcross + " refused " + Turrets.OrdersRefused + "; segment queries across " + Scope.PawnsNearSegmentCalls + "; scope swaps " + Scope.Swapped
                + "; changed " + PackZ.Changed);
        }

        // ------------------------------------------------------------------ fixtures
        private static Faction Enemy => Find.FactionManager.AllFactions.Where(f => !f.IsPlayer && !f.def.hidden && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer))
            .OrderBy(f => f.loadID).FirstOrDefault();

        private static Pawn Raider(IntVec3 c)
        {
            Faction fac = Enemy;
            Pawn p;
            Rand.PushState(pawnSeed++);
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(fac?.def.basicMemberKind ?? PawnKindDefOf.Colonist, fac, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                    allowAddictions: false));
            }
            finally
            {
                Rand.PopState();
            }
            p.equipment?.DestroyAllEquipment();
            p.inventory?.DestroyAll();
            GenSpawn.Spawn(p, c, Map);
            held.Add((p, c));
            return p;
        }

        private static Thing BuildAs(string def, IntVec3 c, Faction f, Rot4? rot = null)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamed(def);
            Thing t = ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
            t.SetFaction(f);
            t = GenSpawn.Spawn(t, c, Map, rot ?? Rot4.North);
            CompCanBeDormant dormant = t.TryGetComp<CompCanBeDormant>();
            if (dormant != null && !dormant.Awake)
            {
                dormant.WakeUp();
            }
            return t;
        }

        private static Pawn Gunner(IntVec3 c, Building_TurretGunCE turret)
        {
            Pawn g = Colonist(c, null);
            g.drafter.Drafted = false;
            g.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, turret));
            return g;
        }

        private static void Load(Building_TurretGunCE t)
        {
            if (t.CompAmmo != null && (!t.CompAmmo.CanBeFiredNow || t.CompAmmo.CurMagCount <= 0))
            {
                t.CompAmmo.ResetAmmoCount(t.CompAmmo.SelectedAmmo);
            }
        }

        private static List<Shot> Of(Thing a, Thing b, List<Shot> from, int since) => from.Where(s => (s.by == a.ThingID || (b != null && s.by == b.ThingID)) && s.tick >= since).ToList();

        private static bool InFlight(Thing a, Thing b) => Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>()
            .Any(p => !p.landed && !p.Destroyed && (p.launcher == a || (b != null && p.launcher == b)));

        // A manned mortar (gunner one cell south) ordered at aim; runs until `shells` overhead shells are launched and down.
        private static IEnumerator<int> FireShells(Building_TurretGunCE mortar, Pawn gunner, LocalTargetInfo aim, int shells, int t0)
        {
            yield return 200;
            Load(mortar);
            mortar.OrderAttack(aim);
            Out("  mortar " + mortar.Position + " ordered at " + aim.Cell + ": forced " + mortar.ForcedTarget + ", manned " + mortar.mannableComp?.MannedNow);
            for (int guard = 0; guard < 60; guard++)
            {
                yield return 100;
                Load(mortar);
                if (guard % 10 == 5 && Of(gunner, mortar, Launches, t0).Count == 0)
                {
                    Out("   mortar state: current " + mortar.CurrentTarget + ", forced " + mortar.ForcedTarget + ", manned " + mortar.mannableComp?.MannedNow + ", warmup " + mortar.burstWarmupTicksLeft + ", cooldown " + mortar.burstCooldownTicksLeft
                        + ", verb " + mortar.AttackVerb?.state + ", available " + mortar.AttackVerb?.Available() + ", roofed " + mortar.Position.Roofed(Map) + ", can hit " + ((Verb_LaunchProjectileCE)mortar.AttackVerb).CanHitTarget(aim, out string why) + " " + why
                        + ", gunner job " + gunner.CurJobDef);
                }
                if (Of(gunner, mortar, Launches, t0).Count(s => s.overhead) >= shells && !InFlight(gunner, mortar))
                {
                    break;
                }
            }
            mortar.ResetForcedTarget();
            for (int guard = 0; guard < 20 && InFlight(gunner, mortar); guard++)
            {
                yield return 50;
            }
        }

        // ------------------------------------------------------------------ CIWS
        private static Building_CIWS_CE Ciws_(IntVec3 c) => (Building_CIWS_CE)BuildAs("Turret_AutoChargeBlaster", c, Faction.OfMechanoids);

        private static string CiwsReport(Thing ciws, Thing gunner, Thing mortar, int t0, out int shells, out int ciwsLaunches, out int intercepts, out List<Shot> landed)
        {
            List<Shot> sl = Of(gunner, mortar, Launches, t0).Where(s => s.overhead).ToList();
            HashSet<string> shellIds = new HashSet<string>(sl.Select(s => s.def + "#" + s.id));
            shells = sl.Count;
            ciwsLaunches = By(ciws, Launches, t0).Count;
            intercepts = By(ciws, Impacts, t0).Count(s => shellIds.Contains(s.hit));
            landed = Of(gunner, mortar, Impacts, t0).Where(s => s.overhead).ToList();
            return "shells " + shells + ", CIWS shots " + ciwsLaunches + ", intercepts " + intercepts + ", shells landed " + landed.Count + " (" + Hits(landed) + ")";
        }

        // ---- P4a: a CIWS on the ground in the open shoots down shells over the top floor (another frame).
        private static IEnumerator<int> CiwsOpen()
        {
            Out("--- P4a CIWS on the ground, shells overhead (in the top storey's frame)");
            if (L == null || !L.Has(1))
            {
                Check("P4a CIWS", false, "needs a map with floors");
                yield break;
            }
            Wipe(195, 195, 240, 240);
            Wipe(205, 88, 225, 110);
            Building_CIWS_CE ciws = Ciws_(On(215, 215, 0));
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(215, 100, 0));
            Pawn gunner = Gunner(On(215, 98, 0), mortar);
            int t0 = Find.TickManager.TicksGame, tr0 = Ciws.Translated;
            IEnumerator<int> fire = FireShells(mortar, gunner, new LocalTargetInfo(On(217, 218, 0)), 3, t0);
            while (fire.MoveNext())
            {
                yield return fire.Current;
            }
            string r = CiwsReport(ciws, gunner, mortar, t0, out int shells, out int shots, out int intercepts, out _);
            Out("  " + r + ", translated " + (Ciws.Translated - tr0));
            Check("P4a CIWS on the ground engages and intercepts shells in the top storey's frame", shells > 0 && shots > 0 && intercepts > 0 && Ciws.Translated > tr0, r);
            Gone(ciws, gunner, mortar);
        }

        // ---- P4b: a CIWS under a long slab does not engage a shell flying over it. The mortar stands out of the CIWS's
        // range to the west (158 cells); the slab runs from beyond the CIWS's range in the west to the east edge and the
        // shell's target is on it: every point of the flight in range is over the slab, so no line runs out under the
        // slab's edge (that would be a real line: a shell low over open ground in range). One shell: none broke the slab.
        private static IEnumerator<int> CiwsUnderSlab()
        {
            Out("--- P4b CIWS under a slab, a shell flying over it");
            Wipe(25, 0, 249, 78);
            Wipe(0, 25, 12, 45);
            Slab(30, 0, 249, 75);
            Building_CIWS_CE ciws = Ciws_(On(160, 35, 0));
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(2, 35, 0));
            Pawn gunner = Gunner(On(2, 33, 0), mortar);
            Out("  CIWS shot height " + ((Verb_LaunchProjectileCE)ciws.GunCompEq.AllVerbs.First(v => v is VerbCIWS)).ShotHeight.ToString("0.00") + " (storey " + Layout.S.ToString("0.00") + " CE units)");
            int t0 = Find.TickManager.TicksGame, bl0 = Ciws.BlockedPoints;
            int dbg = 0;
            Ciws.Debug = s =>
            {
                if (s.Contains("MortarShell") && dbg++ < 6)
                {
                    Out("   tick " + Find.TickManager.TicksGame + s);
                }
            };
            yield return 200;
            Load(mortar);
            mortar.OrderAttack(new LocalTargetInfo(On(215, 35, 1)));
            for (int guard = 0; guard < 400 && Of(gunner, mortar, Launches, t0).Count(s => s.overhead) == 0; guard++)
            {
                yield return 5;
            }
            mortar.ResetForcedTarget();
            for (int guard = 0; guard < 60 && InFlight(gunner, mortar); guard++)
            {
                yield return 20;
            }
            Ciws.Debug = null;
            List<Shot> atShells = By(ciws, Launches, t0).Where(s => s.line.StartsWith("Bullet_81mmMortarShell")).ToList();
            Out("  CIWS launches: " + string.Join(", ", By(ciws, Launches, t0).GroupBy(s => s.line.Split('#')[0]).Select(g => g.Key + " x" + g.Count()))
                + "; at shells: " + string.Join("; ", atShells.Take(8).Select(s => "t" + s.tick + " " + s.line)));
            string r = CiwsReport(ciws, gunner, mortar, t0, out int shells, out int shots, out int intercepts, out _);
            Out("  " + r + ", blocked points " + (Ciws.BlockedPoints - bl0));
            Check("P4b CIWS under a slab: no shot at the shell over it (lines blocked by the slab)", shells > 0 && atShells.Count == 0 && intercepts == 0 && Ciws.BlockedPoints > bl0,
                "shots at shells " + atShells.Count + "; " + r + ", blocked points " + (Ciws.BlockedPoints - bl0));
            Gone(ciws, gunner, mortar);
        }

        // ---- P4c/d: a CIWS on floor 1 (on a slab): shells aimed at its floor (c) and at the ground beside the building (d).
        private static IEnumerator<int> CiwsUpstairs(bool beside)
        {
            int x = beside ? 90 : 150;
            Out("--- P4" + (beside ? "d" : "c") + " CIWS on floor 1, shells aimed at " + (beside ? "the ground beside the building" : "its floor"));
            Wipe(x - 20, 195, x + 20, 245);
            Wipe(x - 10, 88, x + 10, 110);
            Slab(x - 10, 205, x + 10, 225);
            Building_CIWS_CE ciws = Ciws_(On(x, 215, 1));
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(x, 100, 0));
            Pawn gunner = Gunner(On(x, 98, 0), mortar);
            int t0 = Find.TickManager.TicksGame, tr0 = Ciws.Translated, top0 = Ciws.TopAims;
            IntVec3 aim = beside ? On(x, 233, 0) : On(x + 2, 218, 1);
            IEnumerator<int> fire = FireShells(mortar, gunner, new LocalTargetInfo(aim), 3, t0);
            while (fire.MoveNext())
            {
                yield return fire.Current;
            }
            string r = CiwsReport(ciws, gunner, mortar, t0, out int shells, out int shots, out int intercepts, out _);
            Out("  " + r + ", translated " + (Ciws.Translated - tr0) + ", top aims across " + (Ciws.TopAims - top0) + " (floors " + L.min + ".." + L.max + ")");
            Check("P4" + (beside ? "d" : "c") + " CIWS on floor 1 intercepts shells aimed at " + (beside ? "the ground beside the building" : "its floor"), shells > 0 && shots > 0 && intercepts > 0, r);
            Gone(ciws, gunner, mortar);
        }

        // ------------------------------------------------------------------ turrets
        // ---- P4e: an auto turret on floor 1 picks and hits a raider on the ground in the open; another, over a slab, does
        // not pick a raider under it.
        private static IEnumerator<int> AutoTurret()
        {
            Out("--- P4e auto turret on floor 1, raider on the ground");
            Wipe(10, 130, 75, 172);
            Slab(20, 140, 26, 146);
            Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_AutoMiniTurret", On(23, 146, 1));
            turret.TryGetComp<CompCanBeDormant>()?.WakeUp();
            Load(turret);
            Pawn raider = Raider(On(23, 160, 0));
            int t0 = Find.TickManager.TicksGame;
            Thing picked = null;
            float aimed = float.NaN;
            for (int i = 0; i < 60; i++)
            {
                yield return 10;
                Load(turret);
                picked ??= turret.CurrentTarget.Thing;
                if (float.IsNaN(aimed) && turret.CurrentTarget.Thing == raider && By(turret, Launches, t0).Count > 0)
                {
                    aimed = turret.top.CurRotation;   // the top's turn while shooting at the raider (IsoRimZ's turret-top aim)
                }
            }
            List<Shot> hits = By(turret, Impacts, t0);
            int onRaider = hits.Count(s => s.hit.EndsWith("#" + raider.thingIDNumber));
            float bearing = (L.Ground(raider.Position).ToVector3Shifted() - L.Ground(turret.Position).ToVector3Shifted()).AngleFlat();
            Out("  turret: active " + turret.Active + ", picked " + picked + ", launched " + By(turret, Launches, t0).Count + ", impacts " + Hits(hits) + ", top rotation while firing " + aimed.ToString("0")
                + " (ground bearing " + bearing.ToString("0") + ")");
            Check("P4e auto turret on floor 1: picks and hits the raider on the ground, the top turned to it", picked == raider && onRaider > 0 && Mathf.Abs(Mathf.DeltaAngle(aimed, bearing)) < 10f,
                "picked " + picked + ", raider hits " + onRaider + "/" + hits.Count + ", top " + aimed.ToString("0") + " vs bearing " + bearing.ToString("0"));
            Gone(turret, raider);

            // the same over a slab, the raider under it (nothing else in the turret's range)
            Slab(50, 140, 70, 166);
            Building_TurretGunCE turret2 = (Building_TurretGunCE)Build("Turret_AutoMiniTurret", On(60, 145, 1));
            turret2.TryGetComp<CompCanBeDormant>()?.WakeUp();
            Load(turret2);
            Pawn raider2 = Raider(On(60, 158, 0));
            t0 = Find.TickManager.TicksGame;
            Thing picked2 = null;
            for (int i = 0; i < 12; i++)
            {
                yield return 50;
                Load(turret2);
                picked2 ??= turret2.CurrentTarget.Thing;
            }
            int launched2 = By(turret2, Launches, t0).Count;
            Out("  turret over the slab: active " + turret2.Active + ", picked " + picked2 + ", launched " + launched2 + ", can it see the raider (CE sight) " + ((Verb_LaunchProjectileCE)turret2.AttackVerb).CanHitTarget(raider2));
            Check("P4e auto turret over a slab: does not pick the raider under it", picked2 == null && launched2 == 0, "picked " + picked2 + ", launched " + launched2);
            Gone(turret2, raider2);
        }

        // ---- P4f: a manned turret on floor 1, the player's forced target on the ground.
        private static IEnumerator<int> MannedForced()
        {
            Out("--- P4f manned turret on floor 1, forced target on the ground");
            Wipe(80, 130, 110, 172);
            Slab(89, 139, 97, 147);
            Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_M240B", On(93, 146, 1));
            Pawn gunner = Gunner(On(93, 143, 1), turret);
            Pawn raider = Raider(On(93, 162, 0));
            yield return 150;
            int t0 = Find.TickManager.TicksGame, o0 = Turrets.OrdersAcross;
            Load(turret);
            turret.OrderAttack(raider);
            LocalTargetInfo forcedNow = turret.ForcedTarget;
            yield return 500;
            List<Shot> hits = Of(turret, gunner, Impacts, t0);
            int onRaider = hits.Count(s => s.hit.EndsWith("#" + raider.thingIDNumber));
            Out("  manned " + turret.mannableComp?.MannedNow + ", forced " + forcedNow + ", launched " + Of(turret, gunner, Launches, t0).Count + ", impacts " + Hits(hits));
            Check("P4f manned turret: the forced order across floors is taken and the raider hit", forcedNow.Thing == raider && Turrets.OrdersAcross > o0 && onRaider > 0,
                "forced " + forcedNow + ", orders across " + (Turrets.OrdersAcross - o0) + ", raider hits " + onRaider + "/" + hits.Count);
            turret.ResetForcedTarget();
            Gone(turret, gunner, raider);
        }

        // ---- P4g: a mortar on the ground at a floor-1 target (the shell lands on floor 1) and one on floor 1 at the ground.
        private static IEnumerator<int> MortarUpDown()
        {
            Out("--- P4g mortar up and down floors");
            Wipe(10, 10, 70, 120);
            Slab(40, 90, 60, 110);
            Building_TurretGunCE mortar = (Building_TurretGunCE)Build("Turret_Mortar", On(20, 22, 0));
            Pawn gunner = Gunner(On(20, 20, 0), mortar);
            int t0 = Find.TickManager.TicksGame, o0 = Turrets.OrdersAcross;
            IntVec3 up = On(50, 100, 1);
            IEnumerator<int> fire = FireShells(mortar, gunner, new LocalTargetInfo(up), 1, t0);
            while (fire.MoveNext())
            {
                yield return fire.Current;
            }
            List<Shot> hits = Of(gunner, mortar, Impacts, t0).Where(s => s.overhead).ToList();
            Out("  up: orders across " + (Turrets.OrdersAcross - o0) + ", " + Hits(hits));
            // (as check 6: shells over the slab land on its top, or through a hole an earlier shell broke (CE shells break
            // big holes: the shell drifts on a cell or two below the plane); some miss past its edge)
            List<Shot> overSlab = hits.Where(s => s.ground.x >= 40 && s.ground.x <= 60 && s.ground.z >= 90 && s.ground.z <= 110).ToList();
            Check("P4g mortar up: the order across floors is taken, the shells over the slab land on floor 1", Turrets.OrdersAcross > o0 && overSlab.Count(s => s.frame == 1) > 0
                && overSlab.All(s => s.frame == 1 || GenRadial.RadialCellsAround(s.ground, 2.5f, true).Any(g => !ProjectZ.Api.HasFloorAt(g, 1, Map))), "orders across " + (Turrets.OrdersAcross - o0) + ", over the slab " + overSlab.Count + " (on floor 1 " + overSlab.Count(s => s.frame == 1) + "), " + Hits(hits));
            Gone(gunner, mortar);

            Wipe(110, 5, 136, 95);
            Slab(118, 8, 128, 18);
            Building_TurretGunCE mortar2 = (Building_TurretGunCE)Build("Turret_Mortar", On(123, 14, 1));
            Pawn gunner2 = Gunner(On(123, 12, 1), mortar2);
            t0 = Find.TickManager.TicksGame;
            o0 = Turrets.OrdersAcross;
            IntVec3 down = On(123, 85, 0);
            fire = FireShells(mortar2, gunner2, new LocalTargetInfo(down), 1, t0);
            while (fire.MoveNext())
            {
                yield return fire.Current;
            }
            List<Shot> hits2 = Of(gunner2, mortar2, Impacts, t0).Where(s => s.overhead).ToList();
            Out("  down: orders across " + (Turrets.OrdersAcross - o0) + ", " + Hits(hits2));
            Check("P4g mortar down: the order across floors is taken, the shell lands on the ground near the target", Turrets.OrdersAcross > o0 && hits2.Count > 0 && hits2[0].frame == 0
                && (hits2[0].ground - down).LengthHorizontal < 15f, "orders across " + (Turrets.OrdersAcross - o0) + ", " + Hits(hits2));
            // below the minimum range (32) in 3D: refused (CE measured the raw rows, a band away)
            int r0 = Turrets.OrdersRefused;
            mortar2.OrderAttack(new LocalTargetInfo(On(123, 30, 0)));
            Check("P4g mortar: a target on another floor below the minimum range is refused", Turrets.OrdersRefused > r0 && !mortar2.ForcedTarget.IsValid, "forced " + mortar2.ForcedTarget);
            Gone(gunner2, mortar2);
        }

        // ---- P4h: fire arc (CE setting on): a manned turret on floor 1 facing north; raiders on the ground north (inside
        // the arc by their ground direction) and south (outside; in raw rows every ground cell is "south").
        private static IEnumerator<int> FireArc()
        {
            Out("--- P4h fire arc, targets on the ground");
            System.Reflection.FieldInfo arcSetting = AccessTools.Field(typeof(Settings), "enableArcOfFire");
            object was = arcSetting.GetValue(Controller.settings);
            arcSetting.SetValue(Controller.settings, true);
            Wipe(115, 128, 145, 176);
            Slab(124, 144, 132, 152);
            Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_M240B", On(128, 149, 1), Rot4.North);
            CompFireArc arc = turret.CompFireArc;
            arc.CurrentCenterAngle = 0f;
            arc.CurrentSpan = 90f;
            // the player's forced targets (cells on the ground): north inside the arc, south outside
            IntVec3 cellN = On(129, 165, 0), cellS = On(128, 133, 0);
            turret.OrderAttack(new LocalTargetInfo(cellN));
            bool northOk = turret.ForcedTarget.Cell == cellN;
            turret.ResetForcedTarget();
            turret.OrderAttack(new LocalTargetInfo(cellS));
            bool southRefused = !turret.ForcedTarget.IsValid;
            turret.ResetForcedTarget();
            Out("  arc " + arc.CenterAngle + "/" + arc.Span + ", within: north cell " + arc.WithinFireArc(cellN) + ", south cell " + arc.WithinFireArc(cellS));
            Check("P4h fire arc: a forced target across floors inside the arc is taken, outside refused", northOk && southRefused, "north taken " + northOk + ", south refused " + southRefused);
            Pawn south = Raider(On(128, 134, 0));
            Pawn gunner = Gunner(On(128, 146, 1), turret);
            Thing picked = null;
            for (int i = 0; i < 8; i++)
            {
                yield return 50;
                picked ??= turret.CurrentTarget.Thing;
            }
            Pawn north = Raider(On(130, 164, 0));
            int t0 = Find.TickManager.TicksGame;
            Thing picked2 = null;
            for (int i = 0; i < 10; i++)
            {
                yield return 50;
                picked2 ??= turret.CurrentTarget.Thing;
            }
            int launched = Of(turret, gunner, Launches, t0).Count;
            Out("  only the south raider: picked " + picked + "; with the north one: picked " + picked2 + ", launched " + launched);
            Check("P4h fire arc: auto pick across floors respects the arc", picked == null && picked2 == north && launched > 0, "south only: " + picked + ", then " + picked2 + ", launched " + launched);
            turret.ResetForcedTarget();
            arcSetting.SetValue(Controller.settings, was);
            Gone(turret, gunner, north, south);
        }

        // ---- P4i: a non-snap turret (flak) on floor 1 turns to and fires at a raider on the ground to its east.
        private static IEnumerator<int> NonSnap()
        {
            Out("--- P4i non-snap turret on floor 1, raider on the ground");
            Wipe(150, 122, 188, 182);
            Slab(158, 138, 172, 152);
            Building_TurretGunCE flak = (Building_TurretGunCE)Build("Turret_Flak", On(165, 145, 1), Rot4.North);
            Pawn gunner = Gunner(On(165, 140, 1), flak);
            Pawn raider = Raider(On(186, 147, 0));
            int t0 = Find.TickManager.TicksGame;
            Thing picked = null;
            for (int i = 0; i < 24; i++)
            {
                yield return 50;
                Load(flak);
                picked ??= flak.CurrentTarget.Thing;
            }
            float bearing = (L.Ground(raider.Position).ToVector3Shifted() - L.Ground(flak.Position).ToVector3Shifted()).AngleFlat();
            int launched = Of(flak, gunner, Launches, t0).Count;
            float diff = Mathf.Abs(Mathf.DeltaAngle(flak.NonSnapTurretRot, bearing));
            Out("  manned " + flak.mannableComp?.MannedNow + ", picked " + picked + ", turret rotation " + flak.NonSnapTurretRot.ToString("0") + " (ground bearing " + bearing.ToString("0") + "), launched " + launched
                + ", impacts " + Hits(Of(flak, gunner, Impacts, t0)));
            Check("P4i non-snap turret: picks the raider across floors, turns to its ground bearing and fires", picked == raider && diff < 10f && launched > 0,
                "picked " + picked + ", rotation off by " + diff.ToString("0") + ", launched " + launched);
            Gone(flak, gunner, raider);
        }

        // ---- P4j: mid-burst retarget (CE: a burst whose target goes down turns to a mate of the target near the shot): a
        // manned turret on floor 1 bursts at a raider on the ground (A, the player's forced target); after the first shot A
        // goes down. One of A's mates stands near the 3D line on the ground (B), another on floor 1 behind the turret, on
        // the raw map segment (C). (A pawn's own burst ends with its attack job when the target goes down; a turret's goes on.)
        private static IEnumerator<int> RetargetCheck()
        {
            Out("--- P4j mid-burst retarget, bystanders on two floors");
            Wipe(10, 182, 60, 245);
            Slab(28, 186, 38, 198);
            Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_M240B", On(33, 198, 1));
            Pawn gunner = Gunner(On(33, 195, 1), turret);
            Pawn a = Raider(On(33, 214, 0));
            Pawn c = Raider(On(33, 189, 1));
            Pawn b = Raider(On(34, 210, 0));
            yield return 150;
            Load(turret);
            int t0 = Find.TickManager.TicksGame, seg0 = Scope.PawnsNearSegmentCalls;
            turret.OrderAttack(a);
            int guard = 0;
            while (Of(turret, gunner, Launches, t0).Count == 0 && guard++ < 400)
            {
                yield return 1;
            }
            held.RemoveAll(h => h.pawn == a);
            Scope.Debug = Out;
            HealthUtility.DamageUntilDowned(a, false);
            Verb v = turret.AttackVerb;
            Out("  target down at tick " + Find.TickManager.TicksGame + " after " + Of(turret, gunner, Launches, t0).Count + " launches (downed " + a.Downed + "); verb " + v.state
                + ", manned " + turret.mannableComp?.MannedNow);
            Thing next = null;
            for (int i = 0; i < 60 && next == null; i++)
            {
                yield return 2;
                Thing ct = v.CurrentTarget.Thing;
                if (ct != null && ct != a)
                {
                    next = ct;
                }
            }
            List<Shot> hits = Of(turret, gunner, Impacts, t0);
            Scope.Debug = null;
            Out("  new target " + next + " (B " + b + ", C " + c + "), segment queries across " + (Scope.PawnsNearSegmentCalls - seg0) + ", impacts " + Hits(hits));
            Check("P4j retarget: the mate near the 3D line on the ground, not the one on the raw segment upstairs", next == b && Scope.PawnsNearSegmentCalls > seg0,
                "new target " + next + ", segment queries across " + (Scope.PawnsNearSegmentCalls - seg0));
            turret.ResetForcedTarget();
            Gone(turret, gunner, a, b, c);
        }

        // ---- P4k: control: a turret fight on the ground only (auto turret and a non-snap turret): the pack changes none of
        // CE's values.
        private static IEnumerator<int> TurretControl()
        {
            Out("--- P4k control: ground-only turret fight");
            Wipe(190, 118, 245, 188);
            Building_TurretGunCE turret = (Building_TurretGunCE)Build("Turret_AutoMiniTurret", On(200, 130, 0));
            turret.TryGetComp<CompCanBeDormant>()?.WakeUp();
            Load(turret);
            Building_TurretGunCE flak = (Building_TurretGunCE)Build("Turret_Flak", On(230, 130, 0), Rot4.North);
            Pawn gunner = Gunner(On(230, 125, 0), flak);
            yield return 200;
            Pawn raider = Raider(On(200, 148, 0));
            Pawn raider2 = Raider(On(232, 160, 0));
            int t0 = Find.TickManager.TicksGame, ch0 = PackZ.Changed, mv0 = Frames.RebasesUp + Frames.RebasesDown + Frames.PlaneHits;
            for (int i = 0; i < 16; i++)
            {
                yield return 50;
                Load(flak);
                Load(turret);
            }
            int launched = By(turret, Launches, t0).Count, launched2 = Of(flak, gunner, Launches, t0).Count;
            int changed = PackZ.Changed - ch0, moves = Frames.RebasesUp + Frames.RebasesDown + Frames.PlaneHits - mv0;
            Out("  auto turret launched " + launched + ", flak launched " + launched2 + ", changed " + changed + ", frame moves " + moves);
            Check("P4k control: ground-only turret fight, the pack changed no CE value", launched > 0 && launched2 > 0 && changed == 0,
                "launched " + launched + "/" + launched2 + ", changed " + changed + ", frame moves " + moves + " (a shell arcing over a storey plane in open air: a pure translation)");
            Gone(turret, flak, gunner, raider, raider2);
        }
    }
}
