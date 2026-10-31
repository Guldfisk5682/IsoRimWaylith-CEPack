using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Harness (P3, areas): explosions, suppression, danger and shields between floors, as the player plays them (real
    // orders, real ticks). Read-outs: every ExplosionCE started (its floor, height and cells), every suppression a pawn
    // takes (CompSuppressable.AddSuppression) and every shield interception (ProjectileCE.CheckIntercept true).
    public static partial class TestZ
    {
        public sealed class Blast
        {
            public int id, tick, frame;
            public string by;
            public IntVec3 ground;
            public float height, radius;
            public HashSet<IntVec3> cells;
            public Dictionary<int, int> perFrame;
        }

        public static readonly List<Blast> Blasts = new List<Blast>();
        public static readonly List<(int pawn, float amount, int tick, string by, string def)> Suppressions = new List<(int, float, int, string, string)>();
        private static ProjectileCE suppressing;   // the projectile in ApplySuppression
        public static readonly List<(int proj, string by, int frame, int shield, int tick)> Intercepts = new List<(int, string, int, int, int)>();

        private static readonly AccessTools.FieldRef<Explosion, List<IntVec3>> cellsRef = AccessTools.FieldRefAccess<Explosion, List<IntVec3>>("cellsToAffect");

        private static void PatchAreas(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ExplosionCE), nameof(ExplosionCE.StartExplosionCE)), postfix: new HarmonyMethod(typeof(TestZ), nameof(Blast_Postfix)));
            h.Patch(AccessTools.Method(typeof(CompSuppressable), nameof(CompSuppressable.AddSuppression), new[] { typeof(float), typeof(IntVec3) }), postfix: new HarmonyMethod(typeof(TestZ), nameof(Suppress_Postfix)));
            h.Patch(AccessTools.Method(typeof(ProjectileCE), "CheckIntercept"), postfix: new HarmonyMethod(typeof(TestZ), nameof(Intercept_Postfix)));
            h.Patch(AccessTools.Method(typeof(ProjectileCE), "ApplySuppression"), prefix: new HarmonyMethod(typeof(TestZ), nameof(ApplySuppression_Prefix)), postfix: new HarmonyMethod(typeof(TestZ), nameof(ApplySuppression_Postfix)));
            h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestZ), nameof(TakeDamage_Prefix)));
        }

        public static void Blast_Postfix(ExplosionCE __instance)
        {
            if (!Active || __instance.Map == null || !__instance.Spawned)
            {
                return;
            }
            Layout L = Layout.Of(__instance.Map);
            List<IntVec3> cs = cellsRef(__instance) ?? new List<IntVec3>();
            Blast b = new Blast
            {
                id = __instance.thingIDNumber,
                tick = Find.TickManager.TicksGame,
                frame = L != null ? L.FrameOf(__instance.Position) : 0,
                by = __instance.instigator?.ThingID ?? "-",
                ground = L != null ? L.Ground(__instance.Position) : __instance.Position,
                height = __instance.height,
                radius = __instance.radius,
                cells = new HashSet<IntVec3>(cs),
                perFrame = cs.GroupBy(c => L != null ? L.FrameOf(c) : 0).ToDictionary(g => g.Key, g => g.Count()),
            };
            Blasts.Add(b);
            if (Blasts.Count < 200)
            {
                Out("  blast id " + b.id + " tick " + b.tick + " by " + b.by + " frame " + b.frame + " ground " + b.ground + " h " + b.height.ToString("0.00") + " r " + b.radius.ToString("0.0")
                    + " cells per frame " + string.Join(",", b.perFrame.OrderBy(kv => kv.Key).Select(kv => "f" + kv.Key + ":" + kv.Value)));
            }
        }

        public static void ApplySuppression_Prefix(ProjectileCE __instance) => suppressing = __instance;

        public static void ApplySuppression_Postfix() => suppressing = null;

        // Damage to the harness's held pawns (areas checks): what hit them.
        public static bool LogDamage;
        public static readonly List<(int pawn, string def)> Damages = new List<(int, string)>();

        // Damage a pawn took other than a falling slab or roof (Crush: ProjectZ's SlabDamage breaks a slab a blast hits).
        private static int BlastHurt(Pawn p) => Damages.Count(d => d.pawn == p.thingIDNumber && d.def != "Crush");

        public static void TakeDamage_Prefix(Thing __instance, DamageInfo dinfo)
        {
            if (Active && LogDamage && __instance is Pawn p && held.Any(x => x.pawn == p))
            {
                Out("    damage " + p.ThingID + " " + dinfo.Def?.defName + " " + dinfo.Amount.ToString("0.0") + " by " + (dinfo.Instigator?.ThingID ?? "-") + " weapon " + (dinfo.Weapon?.defName ?? "-") + " tick " + Find.TickManager.TicksGame);
                Damages.Add((p.thingIDNumber, dinfo.Def?.defName ?? "-"));
            }
        }

        public static void Suppress_Postfix(CompSuppressable __instance, float amount)
        {
            if (Active && __instance.parent != null)
            {
                Suppressions.Add((__instance.parent.thingIDNumber, amount, Find.TickManager.TicksGame, suppressing?.launcher?.ThingID ?? "-", suppressing?.def.defName ?? "-"));
            }
        }

        public static void Intercept_Postfix(ProjectileCE __instance, Thing interceptorThing, bool __result)
        {
            if (!Active || !__result || __instance.Map == null)
            {
                return;
            }
            Layout L = Layout.Of(__instance.Map);
            int k = L != null ? L.FrameOf(TExact(__instance)) : 0;
            Intercepts.Add((__instance.thingIDNumber, __instance.launcher?.ThingID ?? "-", k, interceptorThing.thingIDNumber, Find.TickManager.TicksGame));
            Out("  intercept " + __instance.def.defName + " id " + __instance.thingIDNumber + " frame " + k + " at " + TExact(__instance).ToString("F2") + " by shield " + interceptorThing.ThingID
                + " (floor " + (L != null ? L.FrameOf(interceptorThing.Position) : 0) + ")");
        }

        // what the pack did (not how often it looked: Shields.Checked)
        private static string Effects() => Areas.BlastCellsAdded + "/" + Areas.BlastCellsDropped + "/" + Areas.BlastFalloff3D + "/" + Areas.SuppressedAcross + "/" + Areas.SuppressLeaksDropped + "/" + Shields.Met + "/" + Shields.UnderFloor + "/" + Shields.ThroughPlane;

        private static string AreaCounters() => "blast cells added " + Areas.BlastCellsAdded + " dropped " + Areas.BlastCellsDropped + ", falloff 3D " + Areas.BlastFalloff3D
            + ", suppressed across " + Areas.SuppressedAcross + " leaks dropped " + Areas.SuppressLeaksDropped + ", radius clips " + Areas.ClipsApplied
            + ", shields checked across " + Shields.Checked + " met " + Shields.Met + " under floor " + Shields.UnderFloor + " through a plane " + Shields.ThroughPlane
            + ", slab hits judged " + SlabHits.Judged + " ring cells dropped " + SlabHits.RingDropped;

        // ------------------------------------------------------------------ fixtures
        // A hole in floor k over the ground block: open air up there, open sky under it (no roof).
        private static void Hole(int x0, int z0, int x1, int z1, int k = 1)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    Map.terrainGrid.SetTerrain(On(x, z, k), Def("PZ_OpenAir"));
                    Map.roofGrid.SetRoof(On(x, z, k - 1), null);
                }
            }
        }

        // A humanlike without a faction (no weapon): CE suppresses it (another faction than the shooter's), it doesn't fight.
        private static Pawn Human(IntVec3 c)
        {
            Pawn p;
            Rand.PushState(pawnSeed++);
            try
            {
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Villager") ?? PawnKindDefOf.Colonist;
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null, forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false));
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

        private static Thing Shield(IntVec3 c)
        {
            Thing t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("BulletShieldPsychic"));
            return GenSpawn.Spawn(t, c, Map);
        }

        private static int Hurt(Pawn p) => p.Dead ? 1000 : p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury || h is Hediff_MissingPart);

        private static bool Suppressed(Pawn p, int since, Thing by = null, string def = null, int until = int.MaxValue) => SuppressedBy(p, since, by, def, until) > 0f;

        private static float SuppressedBy(Pawn p, int since, Thing by = null, string def = null, int until = int.MaxValue) => Suppressions.Where(s => s.pawn == p.thingIDNumber && s.tick >= since && s.tick <= until && (by == null || s.by == by.ThingID) && (def == null || s.def.StartsWith(def))).Sum(s => s.amount);

        private static List<Blast> BlastsBy(Thing launcher, int since) => Blasts.Where(b => b.by == launcher.ThingID && b.tick >= since).ToList();

        private static string BlastText(List<Blast> bl) => string.Join("; ", bl.Take(8).Select(b => "f" + b.frame + " " + b.ground + " h" + b.height.ToString("0.00") + " "
            + string.Join(",", b.perFrame.OrderBy(kv => kv.Key).Select(kv => "f" + kv.Key + ":" + kv.Value))));

        // Lets a burst's fragments finish: CE throws them over rendered frames (a coroutine, CompFragments), not over
        // ticks, and the harness runs many ticks per frame; so one tick per frame for a while, then the rest of the ticks.
        private static IEnumerable<int> Settle(int ticks)
        {
            for (int i = 0; i < 100; i++)
            {
                yield return 1;
            }
            yield return Mathf.Max(1, ticks - 100);
        }

        // Waits until launcher's blasts reach n (or the guard runs out).
        private static IEnumerable<int> UntilBlasts(Thing launcher, int since, int n, int guardTicks)
        {
            int waited = 0;
            while (BlastsBy(launcher, since).Count < n && waited < guardTicks)
            {
                waited += 20;
                yield return 20;
            }
        }

        // ---- 8. explosions between floors (D5: IsoRimZ's rule from CE's real blast height)
        private static IEnumerator<int> Explosions()
        {
            Out("--- 8 explosions between floors");
            LogDamage = true;
            Wipe(60, 128, 100, 172);

            // 8a: a rocket hits a muffalo on floor 1 at the edge of a hole: the blast reaches the ground under the hole,
            // not the ground under the slab at the same distance
            Slab(70, 132, 86, 154);
            Hole(81, 145, 83, 147);
            for (int x = 72; x <= 86; x++)
            {
                Build("Wall", On(x, 150, 1));   // stops the rest of the burst on floor 1
            }
            Pawn target = Target(On(80, 146, 1));
            Pawn underHole = Target(On(82, 146, 0));
            Pawn underSlab = Target(On(78, 146, 0));
            Pawn rocketeer = Colonist(On(80, 134, 1), "Gun_TripleRocket");   // (12 cells: clear of its own fragments)
            yield return 30;
            string can = CanHit(rocketeer, target);
            int t0 = Find.TickManager.TicksGame;
            Order(rocketeer, target);
            foreach (int w in UntilBlasts(rocketeer, t0, 1, 600))
            {
                yield return w;
            }
            yield return 200;
            List<Blast> bl = BlastsBy(rocketeer, t0);
            bool reachHole = bl.Any(b => b.frame == 1 && b.cells.Contains(On(82, 146, 0)));
            bool reachSlab = bl.Any(b => b.frame == 1 && b.cells.Contains(On(78, 146, 0)));
            Out("  8a: can hit " + can + ", blasts " + bl.Count + ": " + BlastText(bl));
            Out("  8a: injuries: target " + Hurt(target) + ", under the hole " + Hurt(underHole) + ", under the slab " + Hurt(underSlab) + " (fragments through the hole may reach it: report only)");
            Check("8a blast on floor 1 at a hole: reaches the ground under the hole, not under the slab", bl.Count > 0 && bl[0].frame == 1 && reachHole && !reachSlab && Hurt(underHole) > 0,
                "blasts " + bl.Count + ", first on floor " + (bl.Count > 0 ? bl[0].frame.ToString() : "-") + ", cell under the hole " + (reachHole ? "in" : "out") + ", under the slab " + (reachSlab ? "in" : "out")
                + ", injuries under the hole " + Hurt(underHole));
            Gone(rocketeer, target, underHole, underSlab);
            foreach (int w in Settle(400))
            {
                yield return w;
            }

            // 8b: a frag grenade on a slab over a muffalo: nothing below (blast cells, fragments, injuries)
            Slab(88, 156, 98, 166);
            Pawn below = Target(On(93, 163, 0));
            Pawn thrower = Colonist(On(93, 157, 1), "Weapon_GrenadeFrag");
            yield return 30;
            int hurt0 = Hurt(below);
            can = CanHit(thrower, On(93, 163, 1));
            t0 = Find.TickManager.TicksGame;
            Order(thrower, On(93, 163, 1));
            foreach (int w in UntilBlasts(thrower, t0, 1, 600))
            {
                yield return w;
            }
            yield return 120;
            bl = BlastsBy(thrower, t0);
            List<Shot> frags = By(thrower, Impacts, t0).Where(s => s.def.StartsWith("Fragment")).ToList();
            int fragsBelow = frags.Count(s => s.frame == 0 && s.ground.x >= 88 && s.ground.x <= 98 && s.ground.z >= 156 && s.ground.z <= 166);
            Out("  8b: can hit " + can + ", blasts " + BlastText(bl) + ", fragments landed " + frags.Count + " (floor 1 " + frags.Count(s => s.frame == 1) + ", under the slab " + fragsBelow + ")");
            Check("8b grenade on a slab: no blast or fragment reaches the muffalo below", bl.Count > 0 && bl.All(b => b.frame == 1 && !b.perFrame.ContainsKey(0)) && fragsBelow == 0 && BlastHurt(below) == 0,
                "blasts " + BlastText(bl) + ", fragments under the slab " + fragsBelow + ", injuries below " + hurt0 + " -> " + Hurt(below) + ", blast/fragment damage " + BlastHurt(below) + ", hits on it " + Impacts.Count(s => s.hit.EndsWith("#" + below.thingIDNumber)));
            Out("  REPORT 8b SlabDamage under CE: slab cells broken over the grenade: " + Enumerable.Range(88, 11).Sum(x => Enumerable.Range(156, 11).Count(z => Map.terrainGrid.TerrainAt(On(x, z, 1)) != Def("PZ_UpperFloor"))) + ", Crush damage below " + Damages.Count(d => d.pawn == below.thingIDNumber && d.def == "Crush"));
            Gone(thrower, below);

            // 8c: a frag grenade on the ground under a slab, a muffalo on the slab above: nothing reaches it
            Slab(62, 160, 72, 170);
            Pawn above = Target(On(67, 165, 1));
            Pawn thrower2 = Colonist(On(67, 158, 0), "Weapon_GrenadeFrag");
            yield return 30;
            int hurtA0 = Hurt(above);
            can = CanHit(thrower2, On(67, 165, 0));
            t0 = Find.TickManager.TicksGame;
            Order(thrower2, On(67, 165, 0));
            foreach (int w in UntilBlasts(thrower2, t0, 1, 600))
            {
                yield return w;
            }
            yield return 120;
            bl = BlastsBy(thrower2, t0);
            frags = By(thrower2, Impacts, t0).Where(s => s.def.StartsWith("Fragment")).ToList();
            Out("  8c: can hit " + can + ", blasts " + BlastText(bl) + ", fragments landed " + frags.Count + " (on floor 1 " + frags.Count(s => s.frame == 1) + ")");
            Check("8c grenade under a slab: nothing reaches the muffalo on the slab", bl.Count > 0 && bl.All(b => b.frame == 0 && !b.perFrame.ContainsKey(1)) && frags.All(s => s.frame == 0) && BlastHurt(above) == 0,
                "blasts " + BlastText(bl) + ", fragments on floor 1 " + frags.Count(s => s.frame == 1) + ", injuries above " + hurtA0 + " -> " + Hurt(above) + ", blast/fragment damage " + BlastHurt(above));
            Gone(thrower2, above);

            // 8d: every explosion's height is its height over its own floor
            List<Blast> mine = Blasts.Where(b => b.ground.x >= 60 && b.ground.x <= 100 && b.ground.z >= 128 && b.ground.z <= 172).ToList();
            Check("8d explosion heights: over their own floor (0 .. one storey)", mine.Count > 0 && mine.All(b => b.height >= -0.05f && b.height <= Layout.S + 0.05f),
                mine.Count + " blasts, heights " + string.Join(",", mine.Select(b => "f" + b.frame + ":" + b.height.ToString("0.00"))));
        }

        // ---- 9. suppression between floors (D4: blasts on their 3D cells, bullets in the storey they pass through)
        private static IEnumerator<int> Suppression()
        {
            Out("--- 9 suppression between floors");
            // 9a: a rocket blast on floor 1 at a hole's edge: a pawn under the hole (3D in reach) is suppressed, one under
            // the slab at the same distance is not, one on floor 1 is (CE's own)
            Wipe(104, 128, 146, 172);
            Slab(110, 132, 126, 154);
            Hole(121, 145, 123, 147);
            for (int x = 112; x <= 126; x++)
            {
                Build("Wall", On(x, 150, 1));
            }
            Pawn target = Target(On(120, 146, 1));
            Pawn wHole = Human(On(123, 146, 0));
            Pawn wSlab = Human(On(117, 146, 0));
            Pawn wSame = Human(On(116, 146, 1));
            Pawn rocketeer = Colonist(On(120, 134, 1), "Gun_TripleRocket");
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            Order(rocketeer, target);
            foreach (int w in UntilBlasts(rocketeer, t0, 1, 600))
            {
                yield return w;
            }
            yield return 100;
            int tb = BlastsBy(rocketeer, t0).Select(b => b.tick).DefaultIfEmpty(-1).First();   // the first rocket's blast (the rest of the burst and its fragments land later)
            Out("  9a: rocket suppression of the pawn under the slab at ticks " + string.Join(",", Suppressions.Where(s => s.pawn == wSlab.thingIDNumber && s.by == rocketeer.ThingID).Select(s => (s.tick - t0) + " " + s.def + " " + s.amount.ToString("0"))) + " (first blast at " + (tb - t0) + ")");
            Out("  9a: blasts " + BlastText(BlastsBy(rocketeer, t0)) + "; suppression: under the hole " + SuppressedBy(wHole, tb, rocketeer, "Bullet_Rocket", tb).ToString("0.0") + ", under the slab " + SuppressedBy(wSlab, tb, rocketeer, "Bullet_Rocket", tb).ToString("0.0")
                + ", on floor 1 " + SuppressedBy(wSame, tb, rocketeer, "Bullet_Rocket", tb).ToString("0.0"));
            Check("9a blast suppression (first rocket): under the hole yes, under the slab no, same floor yes", Suppressed(wHole, tb, rocketeer, "Bullet_Rocket", tb) && !Suppressed(wSlab, tb, rocketeer, "Bullet_Rocket", tb) && Suppressed(wSame, tb, rocketeer, "Bullet_Rocket", tb),
                "under the hole " + Suppressed(wHole, tb, rocketeer, "Bullet_Rocket", tb) + ", under the slab " + Suppressed(wSlab, tb, rocketeer, "Bullet_Rocket", tb) + ", same floor " + Suppressed(wSame, tb, rocketeer, "Bullet_Rocket", tb));
            Gone(rocketeer, target, wHole, wSlab, wSame);
            foreach (int w in Settle(400))
            {
                yield return w;
            }

            // 9b: bullets from the ground up onto floor 1 suppress a pawn next to their path up there
            Wipe(104, 174, 146, 214);
            Slab(110, 186, 122, 198);   // (the original P3 fixture: the target 6 cells behind the slab edge)
            Pawn target2 = Target(On(116, 192, 1));
            // The witness stands 1 cell off the path, next to the target. CE suppresses, each tick, the pawns near that tick's
            // flight segment except those within 3 cells of its start (RayCastSuppression), and its "near" narrows with the
            // segment's length (ThingsNearSegment: off the line by at most 1 + 3 / length cells), so a pawn 2 cells off the
            // path is missed by floor 1's first, long segment (up to 6 cells: the step after the move up). 1 cell off is
            // near every segment; floor 1's first segment starts by z 188 (the bullets cross the floor plane by then),
            // more than 3 cells from the witness. (At (118, 190) 9b failed 5 of 11 runs.)
            Pawn wUp = Human(On(117, 191, 1));
            Pawn shooter = Colonist(On(116, 176, 0), "Gun_AssaultRifle");
            // 9c control: bullets on the ground under a slab don't suppress a pawn on the slab over their path
            Slab(128, 184, 140, 200);
            Pawn target3 = Target(On(134, 196, 0));
            Pawn wOver = Human(On(135, 190, 1));
            Pawn wGround = Human(On(136, 190, 0));
            Pawn shooter2 = Colonist(On(134, 176, 0), "Gun_AssaultRifle");
            yield return 30;
            string can = CanHit(shooter, target2), can2 = CanHit(shooter2, target3);
            t0 = Find.TickManager.TicksGame;
            int arcs0 = Aim.HighArcs;
            Order(shooter, target2);
            Order(shooter2, target3);
            yield return 500;
            List<Shot> up = By(shooter, Impacts, t0), dn = By(shooter2, Impacts, t0);
            Out("  9b: can hit " + can + ", high arcs taken " + (Aim.HighArcs - arcs0) + ", impacts " + Hits(up) + "; suppression of the pawn on floor 1 by the path " + SuppressedBy(wUp, t0, shooter, "Bullet_").ToString("0.0"));
            Out("  9c: can hit " + can2 + ", impacts " + Hits(dn) + "; suppression: on the slab over the path " + SuppressedBy(wOver, t0, shooter2, "Bullet_").ToString("0.0") + ", on the ground by the path " + SuppressedBy(wGround, t0, shooter2, "Bullet_").ToString("0.0"));
            Check("9b bullets up onto floor 1 suppress a pawn by their path there", can == "yes" && up.Count > 0 && Suppressed(wUp, t0, shooter, "Bullet_"), "can hit " + can + ", impacts " + up.Count + ", suppressed " + Suppressed(wUp, t0, shooter, "Bullet_"));
            Check("9c bullets under a slab: the pawn on the slab above is not suppressed (the one by the path on the ground is)", can2 == "yes" && dn.Count > 0 && !Suppressed(wOver, t0, shooter2, "Bullet_") && Suppressed(wGround, t0, shooter2, "Bullet_"),
                "can hit " + can2 + ", impacts " + dn.Count + ", on the slab " + Suppressed(wOver, t0, shooter2, "Bullet_") + ", on the ground " + Suppressed(wGround, t0, shooter2, "Bullet_"));
            Gone(shooter, shooter2, target2, target3, wUp, wOver, wGround);
        }

        // ---- 10. CE's danger map per floor: a blast marks its own floor's cells only
        private static IEnumerator<int> Danger()
        {
            Out("--- 10 danger areas per floor");
            Wipe(150, 128, 178, 174);
            // 10a: an armoured slab, so the grenade breaks no cell (a hole would let fragments, and their danger, down to
            // the ground: right, but not what 10a is about)
            Slab(155, 136, 173, 156, 1, "PZ_UpperFloor_Plasteel");
            Slab(162, 166, 176, 174);   // 10b: over the ground grenade (its fragments stay in the ground storey)
            Pawn thrower = Colonist(On(164, 138, 1), "Weapon_GrenadeFrag");
            Pawn thrower2 = Colonist(On(170, 164, 0), "Weapon_GrenadeFrag");
            yield return 30;
            DangerTracker dt = Map.GetComponent<DangerTracker>();
            int t0 = Find.TickManager.TicksGame;
            Order(thrower, On(164, 146, 1));
            foreach (int w in UntilBlasts(thrower, t0, 1, 600))
            {
                yield return w;
            }
            int f0 = 0, f1 = 0;
            for (int x = 155; x <= 173; x++)
            {
                for (int z = 136; z <= 156; z++)
                {
                    f0 += dt.DangerAt(On(x, z, 0)) > 0f ? 1 : 0;
                    f1 += dt.DangerAt(On(x, z, 1)) > 0f ? 1 : 0;
                }
            }
            int broken = Enumerable.Range(155, 19).Sum(x => Enumerable.Range(136, 21).Count(z => Map.terrainGrid.TerrainAt(On(x, z, 1)) != Def("PZ_UpperFloor_Plasteel")));
            Out("  10a: blast " + BlastText(BlastsBy(thrower, t0)) + "; danger cells over the slab block: floor 1 " + f1 + ", ground " + f0 + "; slab cells broken " + broken);
            Check("10a grenade on floor 1: danger on floor 1, none on the ground under it", f1 > 0 && f0 == 0 && broken == 0, "floor 1 " + f1 + ", ground " + f0 + ", slab cells broken " + broken);
            foreach (int w in Settle(400))
            {
                yield return w;
            }
            t0 = Find.TickManager.TicksGame;
            Order(thrower2, On(170, 170, 0));
            foreach (int w in UntilBlasts(thrower2, t0, 1, 600))
            {
                yield return w;
            }
            int g0 = 0, g1 = 0;
            for (int x = 164; x <= 176; x++)
            {
                for (int z = 162; z <= 174; z++)
                {
                    g0 += dt.DangerAt(On(x, z, 0)) > 0f ? 1 : 0;
                    g1 += dt.DangerAt(On(x, z, 1)) > 0f ? 1 : 0;
                }
            }
            Out("  10b: blast " + BlastText(BlastsBy(thrower2, t0)) + "; danger cells round it: ground " + g0 + ", floor 1 " + g1);
            Check("10b grenade on the ground under a slab: danger on the ground, none on floor 1 over it", g0 > 0 && g1 == 0, "ground " + g0 + ", floor 1 " + g1);
            Gone(thrower, thrower2);
        }

        // ---- 11. shields between floors (D8 + user decision 2026-10-30: ProjectZ's dome in 3D over its floor; slabs don't cut it)
        private static IEnumerator<int> ShieldChecks()
        {
            Out("--- 11 shields between floors");
            // 11a: a dome on the ground round a target, the shooter on a balcony: shots meet the dome while still on floor 1
            Wipe(180, 128, 200, 160);
            Slab(186, 129, 194, 135);
            Pawn targetA = Target(On(190, 142, 0));
            Thing shieldA = Shield(On(190, 142, 0));
            Pawn shooterA = Colonist(On(190, 135, 1), "Gun_AssaultRifle");
            // 11b: a dome on floor 1 round a target, the shooter on the ground: shots meet it up there
            Wipe(202, 128, 222, 160);
            Slab(206, 140, 218, 154);   // (the original P3 fixture: the target 7 cells behind the slab edge)
            Pawn targetB = Target(On(212, 147, 1));
            Thing shieldB = Shield(On(212, 147, 1));
            Pawn shooterB = Colonist(On(212, 129, 0), "Gun_AssaultRifle");
            // 11c: a dome on the ground under a slab, shots over the slab on floor 1 pass through its sphere: stopped there (domes pass through slabs)
            Wipe(224, 128, 245, 170);
            Slab(226, 136, 244, 166);
            Thing shieldC = Shield(On(235, 151, 0));
            Pawn targetC = Target(On(235, 160, 1));
            Pawn shooterC = Colonist(On(235, 140, 1), "Gun_AssaultRifle");
            yield return 30;
            string canA = CanHit(shooterA, targetA), canB = CanHit(shooterB, targetB), canC = CanHit(shooterC, targetC);
            int t0 = Find.TickManager.TicksGame;
            int through0 = Shields.ThroughPlane;
            int arcs11 = Aim.HighArcs;
            Order(shooterA, targetA);
            Order(shooterB, targetB);
            Order(shooterC, targetC);
            yield return 500;
            List<(int proj, string by, int frame, int shield, int tick)> ia = Intercepts.Where(i => i.by == shooterA.ThingID && i.tick >= t0).ToList();
            List<(int proj, string by, int frame, int shield, int tick)> ib = Intercepts.Where(i => i.by == shooterB.ThingID && i.tick >= t0).ToList();
            List<(int proj, string by, int frame, int shield, int tick)> ic = Intercepts.Where(i => i.by == shooterC.ThingID && i.tick >= t0).ToList();
            int hitA = By(shooterA, Impacts, t0).Count(s => s.hit.EndsWith("#" + targetA.thingIDNumber));
            int hitB = By(shooterB, Impacts, t0).Count(s => s.hit.EndsWith("#" + targetB.thingIDNumber));
            int hitC = By(shooterC, Impacts, t0).Count(s => s.hit.EndsWith("#" + targetC.thingIDNumber));
            Out("  11a: can hit " + canA + ", launched " + By(shooterA, Launches, t0).Count + ", intercepts " + ia.Count + " (on floor 1 " + ia.Count(i => i.frame == 1) + "), target hits " + hitA + ": " + Hits(By(shooterA, Impacts, t0)));
            Out("  11b: can hit " + canB + ", launched " + By(shooterB, Launches, t0).Count + " at ticks " + string.Join(",", By(shooterB, Launches, t0).Select(s => s.tick - t0)) + ", intercepts " + ib.Count + " (on floor 1 " + ib.Count(i => i.frame == 1) + "), target hits " + hitB);
            Out("  11c: can hit " + canC + ", launched " + By(shooterC, Launches, t0).Count + ", intercepts " + ic.Count + ", target hits " + hitC + " (on floor 1 " + ic.Count(i => i.frame == 1) + "), entries through the slab " + (Shields.ThroughPlane - through0) + "; high arcs taken in 11 " + (Aim.HighArcs - arcs11));
            Check("11a ground dome, shots from floor 1: all stopped where they meet the dome (up on floor 1 or at its floor plane)", canA == "yes" && ia.Count > 0 && ia.Any(i => i.frame == 1) && hitA == 0,
                "can hit " + canA + ", intercepts " + ia.Count + " on floor 1 " + ia.Count(i => i.frame == 1) + ", target hits " + hitA);
            Check("11b floor-1 dome, shots from the ground: stopped", canB == "yes" && ib.Count > 0 && hitB == 0, "can hit " + canB + ", intercepts " + ib.Count + ", target hits " + hitB);
            Check("11c dome under a slab: shots over the slab are stopped where they pass through its sphere (up on floor 1)", canC == "yes" && ic.Count > 0 && ic.All(i => i.frame == 1) && hitC == 0 && Shields.ThroughPlane > through0,
                "can hit " + canC + ", intercepts " + ic.Count + " on floor 1 " + ic.Count(i => i.frame == 1) + ", target hits " + hitC + ", entries through the slab " + (Shields.ThroughPlane - through0));
            Gone(shooterA, shooterB, shooterC, targetA, targetB, targetC, shieldA, shieldB, shieldC);
        }

        // ---- 15. slab edge (the P2 high-arc regression): rifle bursts from the ground at targets on floor 1 just behind a
        // slab's edge (the P3 9b and 11b geometry), three rounds with their own seeds. A shot aimed low crosses floor 1's
        // plane over the slab's first cells and hits the slab edge (physics, like cover); the rest reach floor 1 (or fly on
        // up past it and leave the map). No shot takes the high arc (a near-vertical shot into the sky): every launch angle
        // stays low and nothing is left in flight.
        private static IEnumerator<int> SlabEdge()
        {
            Out("--- 15 slab edge: rifle bursts up at targets just behind a slab edge (no high arc)");
            // (report: the loaded direct-fire weapons' v^2 / g over their range, Aim's lob rule; default projectiles)
            List<(string w, float ratio)> lobs = new List<(string, float)>();
            foreach (ThingDef wd in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsRangedWeapon && d.Verbs.Count > 0))
            {
                VerbProperties vp = wd.Verbs[0];
                if (vp.defaultProjectile?.projectile is ProjectilePropertiesCE pp && !pp.flyOverhead && !pp.isInstant && pp.speed > 0f && pp.GravityPerWidth > 0f && vp.range > 0f)
                {
                    lobs.Add((wd.defName, pp.speed * pp.speed / pp.GravityPerWidth / vp.range));
                }
            }
            Out("  lob rule (v^2/g over range <= " + Aim.LobRangeFactor + "): lob " + string.Join(", ", lobs.Where(l => l.ratio <= Aim.LobRangeFactor).OrderBy(l => l.ratio).Select(l => l.w + " " + l.ratio.ToString("0.0")))
                + "; nearest above: " + string.Join(", ", lobs.Where(l => l.ratio > Aim.LobRangeFactor).OrderBy(l => l.ratio).Take(12).Select(l => l.w + " " + l.ratio.ToString("0.0")))
                + "; Gun_AssaultRifle " + lobs.Where(l => l.w == "Gun_AssaultRifle").Select(l => l.ratio.ToString("0")).FirstOrDefault());
            int arcs0 = Aim.HighArcs, low0 = Aim.LowArcsBlocked, inFlight = 0;
            (string name, int x, int shooterZ, int edgeZ, int targetZ, int endZ)[] fixtures = { ("15a", 172, 181, 192, 199, 206), ("15b", 200, 180, 190, 196, 202) };
            var all = fixtures.ToDictionary(f => f.name, f => (cans: new List<string>(), launched: new List<Shot>(), hits: new List<Shot>(), onTarget: 0));
            for (int round = 0; round < 3; round++)
            {
                Seed(1500 + round);
                Wipe(160, 176, 212, 214);
                List<(Pawn shooter, Pawn target)> pairs = new List<(Pawn, Pawn)>();
                foreach (var f in fixtures)
                {
                    Slab(f.x - 6, f.edgeZ, f.x + 6, f.endZ);
                    pairs.Add((Colonist(On(f.x, f.shooterZ, 0), "Gun_AssaultRifle"), Target(On(f.x, f.targetZ, 1))));
                }
                yield return 30;
                int t0 = Find.TickManager.TicksGame;
                for (int i = 0; i < fixtures.Length; i++)
                {
                    all[fixtures[i].name].cans.Add(CanHit(pairs[i].shooter, pairs[i].target));
                    Order(pairs[i].shooter, pairs[i].target);
                }
                yield return 600;
                foreach ((Pawn shooter, Pawn target) in pairs)
                {
                    Gone(shooter);   // (the burst ends; what it fired flies on)
                }
                yield return 200;
                HashSet<string> ids = new HashSet<string>(pairs.Select(pr => pr.shooter.ThingID));
                inFlight += Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).OfType<ProjectileCE>().Count(p => !p.landed && p.launcher != null && ids.Contains(p.launcher.ThingID));
                for (int i = 0; i < fixtures.Length; i++)
                {
                    var a = all[fixtures[i].name];
                    List<Shot> hits = By(pairs[i].shooter, Impacts, t0);
                    a.launched.AddRange(By(pairs[i].shooter, Launches, t0));
                    a.hits.AddRange(hits);
                    a.onTarget += hits.Count(s => s.hit.EndsWith("#" + pairs[i].target.thingIDNumber));
                    all[fixtures[i].name] = a;
                    Gone(pairs[i].target);
                }
            }
            Out("  15: high arcs taken " + (Aim.HighArcs - arcs0) + ", low arcs aimed into a slab " + (Aim.LowArcsBlocked - low0) + ", still in flight " + inFlight);
            foreach (var f in fixtures)
            {
                var a = all[f.name];
                float maxAngle = a.launched.Select(s => s.angle).DefaultIfEmpty(0f).Max();
                int up = a.hits.Count(s => s.frame == 1);
                List<Shot> edge = a.hits.Where(s => s.frame == 0 && s.height >= Layout.S - 0.05f).ToList();
                int beyond = a.hits.Count(s => s.frame == 0 && s.height < Layout.S - 0.05f && s.ground.z > f.endZ);
                int other = a.hits.Count(s => s.frame == 0 && s.height < Layout.S - 0.05f && s.ground.z <= f.endZ);
                bool edgeNear = edge.All(s => s.ground.z >= f.edgeZ && s.ground.z <= f.edgeZ + 3);
                bool cans = a.cans.All(c => c == "yes");
                Out("  " + f.name + ": can hit " + string.Join(",", a.cans) + ", launched " + a.launched.Count + " (angles " + a.launched.Select(s => s.angle).DefaultIfEmpty(0f).Min().ToString("0.0") + ".." + maxAngle.ToString("0.0")
                    + " deg), impacts " + a.hits.Count + " (floor 1 " + up + ", target " + a.onTarget + ", slab edge " + edge.Count + " at rows " + string.Join(",", edge.Select(s => s.ground.z))
                    + ", ground past the slab " + beyond + ", other " + other + "), left the map " + (a.launched.Count - a.hits.Count));
                Check(f.name + " slab edge: rifle bursts up, no high arc; shots reach floor 1 or stop on the slab edge", cans && a.launched.Count > 0 && maxAngle <= 20f && Aim.HighArcs == arcs0 && inFlight == 0
                    && a.onTarget > 0 && other == 0 && edgeNear,
                    "can hit " + string.Join(",", a.cans) + ", launched " + a.launched.Count + ", steepest " + maxAngle.ToString("0.0") + " deg, high arcs " + (Aim.HighArcs - arcs0) + ", in flight " + inFlight
                    + ", target hits " + a.onTarget + ", slab edge " + edge.Count + (edgeNear ? "" : " (NOT at the edge)") + ", other " + other);
            }
        }

        // ---- 12b. same-floor control: blasts, suppression and a shield all on the ground: the pack changes nothing
        private static IEnumerator<int> SameFloorAreas()
        {
            Out("--- 12b same-floor areas (control)");
            Wipe(60, 176, 100, 214);
            Build("Sandbags", On(80, 196, 0));
            Pawn target = Target(On(80, 200, 0));
            Pawn witness = Human(On(84, 199, 0));
            Pawn thrower = Colonist(On(80, 190, 0), "Weapon_GrenadeFrag");
            Pawn rocketeer = Colonist(On(72, 184, 0), "Gun_TripleRocket");
            Pawn targetS = Target(On(92, 204, 0));
            Thing shield = Shield(On(92, 204, 0));
            Pawn shooter = Colonist(On(92, 186, 0), "Gun_AssaultRifle");
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            int changed0 = PackZ.Changed;
            string c0 = Effects();
            Order(thrower, On(80, 198, 0));
            Order(rocketeer, target);
            Order(shooter, targetS);
            yield return 600;
            int changed = PackZ.Changed - changed0;
            List<Blast> bl = Blasts.Where(b => b.tick >= t0 && (b.by == thrower.ThingID || b.by == rocketeer.ThingID)).ToList();
            int ints = Intercepts.Count(i => i.by == shooter.ThingID && i.tick >= t0);
            Out("  blasts " + BlastText(bl) + ", witness suppression " + SuppressedBy(witness, t0).ToString("0.0") + ", intercepts " + ints);
            Out("  counters before: " + c0);
            Out("  counters after:  " + AreaCounters());
            Check("12b same-floor blasts, suppression and shield: the pack changed none of CE's values", bl.Count > 0 && ints > 0 && changed == 0 && c0 == Effects(),
                "blasts " + bl.Count + ", intercepts " + ints + ", changed values " + changed + ", counters " + (c0 == Effects() ? "unchanged" : "CHANGED"));
            Gone(thrower, rocketeer, shooter, target, targetS, witness, shield);
        }

        // Fight mode, part 2: a same-floor fight with explosions (grenades, rockets) and a shield; its outcome hash.
        private static IEnumerator<int> FightBlast()
        {
            Out("--- 14b same-floor fight with explosions");
            Wipe(55, 112, 80, 145);
            Build("Sandbags", On(66, 130, 0));
            Pawn target = Target(On(66, 135, 0));
            Pawn witness = Human(On(70, 134, 0));
            Pawn thrower = Colonist(On(66, 127, 0), "Weapon_GrenadeFrag");
            Pawn rocketeer = Colonist(On(60, 118, 0), "Gun_TripleRocket");
            Pawn shooter = Colonist(On(74, 118, 0), "Gun_AssaultRifle");
            Thing shield = Shield(On(74, 140, 0));
            Pawn targetS = Target(On(74, 140, 0));
            yield return 30;
            int t0 = Find.TickManager.TicksGame;
            int changed0 = PackZ.Changed;
            Order(thrower, On(66, 134, 0));
            Order(rocketeer, target);
            Order(shooter, targetS);
            yield return 900;
            List<Shot> hits = Impacts.Where(s => s.tick >= t0).ToList();
            List<Blast> bl = Blasts.Where(b => b.tick >= t0).ToList();
            string line = string.Join("|", hits.Select(s => (s.tick - t0) + ":" + s.def + ":" + s.frame + ":" + s.ground + ":" + s.height.ToString("0.000") + ":" + (s.hit == "-" ? "-" : s.hit.Substring(0, s.hit.IndexOf('#')))))
                + "#" + string.Join("|", bl.Select(b => (b.tick - t0) + ":" + b.ground + ":" + b.height.ToString("0.000") + ":" + b.cells.Count))
                + "#" + string.Join("|", Suppressions.Where(s => s.tick >= t0).Select(s => (s.tick - t0) + ":" + s.amount.ToString("0.000")))
                + "#" + string.Join("|", Intercepts.Where(i => i.tick >= t0).Select(i => (i.tick - t0).ToString()))
                + "#" + Hurt(target) + ":" + Hurt(witness) + ":" + Hurt(targetS);
            ulong h = 1469598103934665603UL;
            foreach (char ch in line)
            {
                h = (h ^ ch) * 1099511628211UL;
            }
            Out("  impacts " + hits.Count + ", blasts " + bl.Count + ", suppressions " + Suppressions.Count(s => s.tick >= t0) + ", intercepts " + Intercepts.Count(i => i.tick >= t0));
            Out("HASH blastfight=" + h.ToString("x16") + " impacts=" + hits.Count + " blasts=" + bl.Count);
            int changed = PackZ.Changed - changed0;
            Check("14b same-floor fight with explosions ran", hits.Count > 0 && bl.Count > 0, hits.Count + " impacts, " + bl.Count + " blasts");
            Check("14b same-floor fight with explosions: the pack changed none of CE's values", changed == 0, changed + " changed values");
            Gone(thrower, rocketeer, shooter, target, targetS, witness, shield);
        }
    }
}
