using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Harness, mode m230lf: a third-party CIWS turret, the M230LF sentry gun of "Defensive Machine Gun Turret Pack"
    // (sumghai.DefensiveMachineGunTurretPack; under CE: Building_CIWS_CE with a VerbCIWSSkyfaller verb), against real
    // incoming enemy drop pods (DropPodUtility.DropThingsNear, the raid path: the game's drop cell finder with ProjectZ's
    // drop rules), real ticks. Each check builds its own fixture in its own area, fixed seed (TestZ.Script).
    //   L  load: the def, its class and CIWS verb, CE's CIWS setting, the pack's CIWS group
    //   G  turret on the ground, pods onto the ground near it (same storey)
    //   D  turret on a floor-1 deck, pods onto its deck (same storey) and onto the ground beside the building (across)
    //   B  turret on the ground under a solid slab, pods onto the slab top: no engagement
    // Pods are dropped one at a time; each is followed until it lands or is shot down.
    public static partial class TestZ
    {
        private static IEnumerable<IEnumerator<int>> M230Checks()
        {
            M230Hooks();
            yield return M230Load();
            yield return M230Ground();
            yield return M230Deck();
            yield return M230Blocked();
            yield return M230Edge();
            yield return M230Drops();
        }

        // ---- PD: every pod shot down drops its contents on its own storey, under the pod (the pack's drop cell: the
        // column under the pod's real position, on the band of its storey), never in another band or a void row.
        private static IEnumerator<int> M230Drops()
        {
            yield return 1;
            List<PodLog> down = pods.Values.Where(p => p.shotDown).ToList();
            int otherBand = down.Count(p => FrameName(p.packDrop) != FrameName(p.cell));
            int far = down.Count(p => L != null && p.packDrop.IsValid && L.FrameOf(p.packDrop) != Layout.None && (L.Ground(p.packDrop) - p.realGround).LengthHorizontal > 1.5f);
            Out("  pods shot down " + down.Count + ": " + string.Join("; ", down.Select(p => "#" + p.id + " pod " + Gc(p.cell) + " " + FrameName(p.cell) + ", real column " + p.realGround.x + "," + p.realGround.z
                + ", CE's cell " + p.dropAt + " " + FrameName(p.dropAt) + ", drop " + p.packDrop + " " + FrameName(p.packDrop) + " (ground " + Gc(p.packDrop) + ")")) + "; pack drops moved " + PodDrops.Moved + ", fallbacks " + PodDrops.Fallbacks);
            if (L == null)
            {
                Check("PD no floors: CE's own drop cell", down.All(p => p.packDrop == p.dropAt), down.Count + " shot down");
                yield break;
            }
            Check("PD shot-down pods drop their contents on their own storey, under the pod (0 in another band)", down.Count > 0 && otherBand == 0 && far == 0 && PodDrops.Fallbacks == 0,
                down.Count + " shot down, in another band " + otherBand + ", away from the pod's column " + far + ", fallbacks " + PodDrops.Fallbacks);
        }

        // ---- E (record only, no pass rule): pods shot down near the ground band's north edge. CE drops a shot-down pod's
        // contents at the cell under its flat draw position (DrawPos.ToIntVec3: far from the pod while it is high); near
        // the band's north edge that cell can be a void row or another floor's band. Logged per pod.
        private static IEnumerator<int> M230Edge()
        {
            Out("--- E M230LF near the ground band's north edge: where shot-down pods drop their contents (record)");
            Wipe(170, 200, 240, 249);
            Building_CIWS_CE t = M230(On(205, 215, 0));
            yield return 30;
            List<PodLog> list = new List<PodLog>();
            foreach ((int x, int z) in new[] { (205, 238), (220, 244), (190, 242), (210, 230) })
            {
                IEnumerator<int> e = DropOne(t, On(x, z, 0), "north edge", list);
                while (e.MoveNext())
                {
                    yield return e.Current;
                }
            }
            string r = PodSummary(t, list, out _, out _, out int down);
            Out("  " + r + "; drop cells: " + string.Join("; ", list.Where(p => p.shotDown).Select(p => p.dropAt + " " + FrameName(p.dropAt) + (p.dropAt.InBounds(Map) ? "" : " off-map"))));
            Out("CHECK INFO E shot-down contents near the band edge: " + down + " shot down, drop cells in a void row " + list.Count(p => p.shotDown && p.dropAt.InBounds(Map) && FrameName(p.dropAt) == "VOID")
                + ", in another band " + list.Count(p => p.shotDown && p.dropAt.InBounds(Map) && FrameName(p.dropAt) != "VOID" && FrameName(p.dropAt) != FrameName(p.cell)));
            poweredM230.Remove(t);
            Gone(t);
        }

        private sealed class PodLog
        {
            public int id;
            public string def, aim;
            public IntVec3 target;       // the cell asked for (raw)
            public IntVec3 cell;         // the pod's cell (raw)
            public int frame;
            public bool landed, shotDown;
            public IntVec3 downAt;       // the pod's cell when shot down (raw)
            public IntVec3 dropAt;       // CE's drop cell for the contents (DrawPos.ToIntVec3)
            public float downHeight;     // CE height of the pod when shot down
            public IntVec3 realGround = IntVec3.Invalid;   // the ground column under the pod's real position when shot down
            public IntVec3 packDrop = IntVec3.Invalid;     // the drop cell the contents got (the pack's, PodDrops.Last)
            public int ticksLeft;
            public Pawn rider;
        }

        private static readonly Dictionary<int, PodLog> pods = new Dictionary<int, PodLog>();
        private static bool m230Hooked;
        private static readonly HashSet<Thing> poweredM230 = new HashSet<Thing>();

        private static void M230Hooks()
        {
            if (m230Hooked)
            {
                return;
            }
            m230Hooked = true;
            Harmony h = new Harmony("peren.isorimwaylith.cepack.testz.m230");
            h.Patch(AccessTools.Method(typeof(Skyfaller), "Impact"), prefix: new HarmonyMethod(typeof(TestZ), nameof(SkyImpact_Prefix)));
            h.Patch(AccessTools.Method(typeof(CompCIWSImpactHandler_Skyfaller), "OnDestroying"), prefix: new HarmonyMethod(typeof(TestZ), nameof(SkyDown_Prefix)), postfix: new HarmonyMethod(typeof(TestZ), nameof(SkyDown_Postfix)));
            h.Patch(AccessTools.PropertyGetter(typeof(CompPowerTrader), nameof(CompPowerTrader.PowerOn)), postfix: new HarmonyMethod(typeof(TestZ), nameof(M230Power_Postfix)));
        }

        public static void M230Power_Postfix(CompPowerTrader __instance, ref bool __result)
        {
            if (!__result && __instance?.parent != null && poweredM230.Contains(__instance.parent))
            {
                __result = true;
            }
        }

        public static void SkyImpact_Prefix(Skyfaller __instance)
        {
            if (pods.TryGetValue(__instance.thingIDNumber, out PodLog p))
            {
                p.landed = true;
                p.ticksLeft = __instance.ticksToImpact;
            }
        }

        public static void SkyDown_Prefix(CompCIWSImpactHandler_Skyfaller __instance)
        {
            Thing t = __instance.parent;
            if (t != null && pods.TryGetValue(t.thingIDNumber, out PodLog p))
            {
                p.shotDown = true;
                p.downAt = t.Position;
                p.dropAt = t.DrawPos.ToIntVec3();
                p.ticksLeft = (t as Skyfaller)?.ticksToImpact ?? -1;
                try
                {
                    // (CE's method returns its own Vector3 reference: called by reflection)
                    object v = AccessTools.Method(typeof(Skyfaller_Utility), "CurrentPosition", new[] { typeof(Skyfaller) }).Invoke(null, new object[] { t });
                    Vector3 real = (Vector3)v;
                    p.downHeight = real.y;
                    int k = L != null ? L.FrameOf(t.Position) : 0;
                    p.realGround = new IntVec3(Mathf.FloorToInt(real.x), 0, Mathf.FloorToInt(real.z) - (L != null && k != Layout.None ? L.Shift(k) : 0));
                    PodDrops.Last = IntVec3.Invalid;
                }
                catch (Exception)
                {
                    p.downHeight = float.NaN;
                }
            }
        }

        // The drop cell the contents got: the pack's (PodDrops.Last, set in OnDestroying), or CE's own without floors.
        public static void SkyDown_Postfix(CompCIWSImpactHandler_Skyfaller __instance)
        {
            Thing t = __instance.parent;
            if (t != null && pods.TryGetValue(t.thingIDNumber, out PodLog p) && p.shotDown)
            {
                p.packDrop = PodDrops.Last.IsValid ? PodDrops.Last : p.dropAt;
            }
        }

        private static string FrameName(IntVec3 c)
        {
            if (!c.IsValid)
            {
                return "invalid";
            }
            if (L == null)
            {
                return "f0";
            }
            int k = L.FrameOf(c);
            return k == Layout.None ? "VOID" : "f" + k;
        }

        private static string Gc(IntVec3 c) => c.IsValid && L != null && L.FrameOf(c) != Layout.None ? L.Ground(c).x + "," + L.Ground(c).z : c.x + "," + c.z;

        private static Building_CIWS_CE M230(IntVec3 c)
        {
            Thing t = BuildAs("Turret_M230LFs", c, Faction.OfPlayer);
            poweredM230.Add(t);
            Load((Building_TurretGunCE)t);   // (ammo system off in the harness: a loaded magazine, as after a reload)
            return t as Building_CIWS_CE;
        }

        private static Pawn PodRider()
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
            return p;
        }

        // One enemy drop pod at `at` (raw cell), followed until it is gone; then everything it left is removed.
        private static IEnumerator<int> DropOne(Thing turret, IntVec3 at, string aim, List<PodLog> into)
        {
            HashSet<int> before = new HashSet<int>(Map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).Select(t => t.thingIDNumber));
            Pawn rider = PodRider();
            DropPodUtility.DropThingsNear(at, Map, new Thing[] { rider }, 110, false, false, true, false, true, rider.Faction);
            Skyfaller pod = Map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).OfType<Skyfaller>().FirstOrDefault(t => !before.Contains(t.thingIDNumber));
            if (pod == null)
            {
                Out("   no pod spawned at " + at);
                yield break;
            }
            PodLog log = new PodLog { id = pod.thingIDNumber, def = pod.def.defName, aim = aim, target = at, cell = pod.Position, frame = L != null ? L.FrameOf(pod.Position) : 0, rider = rider, ticksLeft = pod.ticksToImpact };
            pods[log.id] = log;
            into.Add(log);
            int t0 = Find.TickManager.TicksGame;
            bool diag = GenCommandLine.CommandLineArgPassed("cepackm230diag") && into.Count == 1;
            for (int guard = 0; guard < 120 && pod.Spawned; guard++)
            {
                Load((Building_TurretGunCE)turret);
                if (diag && guard % 4 == 0 && turret is Building_CIWS_CE ct)
                {
                    M230Diag(ct, pod);
                }
                yield return 5;
            }
            yield return 20;
            List<Shot> at_ = Launches.Where(s => s.by == turret.ThingID && s.tick >= t0 && s.line == log.def + "#" + log.id).ToList();
            List<Shot> hits = Impacts.Where(s => s.by == turret.ThingID && s.tick >= t0 && s.hit == log.def + "#" + log.id).ToList();
            Thing body = rider.Dead ? (Thing)rider.Corpse : rider;
            string fate = body != null && body.Spawned ? (rider.Dead ? "dead" : "alive") + " at " + body.Position + " " + FrameName(body.Position) + " (ground " + Gc(body.Position) + ")" : (rider.Dead ? "dead, no corpse on the map" : "not on the map");
            Out("   pod " + log.def + "#" + log.id + " " + aim + ": asked " + at + ", pod cell " + log.cell + " " + FrameName(log.cell) + " (ground " + Gc(log.cell) + "), CIWS shots " + at_.Count + ", hits " + hits.Count
                + (log.shotDown ? ", SHOT DOWN at " + log.downAt + " h " + log.downHeight.ToString("0.0") + " ticks left " + log.ticksLeft + ", contents drop cell " + log.dropAt + " " + FrameName(log.dropAt) : log.landed ? ", landed" : ", gone (?)")
                + "; rider " + fate);
            if (hits.Count > 0)
            {
                Out("     first CIWS hit: frame " + hits[0].frame + " ground " + hits[0].ground + " h " + hits[0].height.ToString("0.00") + "; first shot: frame " + at_[0].frame + " ground " + at_[0].ground + " angle " + at_[0].angle.ToString("0.0"));
            }
            // clean: the landed pod, the rider (alive or dead), the slag
            foreach (Thing t in Map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).ToList())
            {
                if (!t.Destroyed)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
            if (rider.Corpse != null && rider.Corpse.Spawned)
            {
                rider.Corpse.Destroy(DestroyMode.Vanish);
            }
            else if (rider.Spawned)
            {
                rider.Destroy(DestroyMode.Vanish);
            }
            if (turret is Building_TurretGunCE tg)
            {
                tg.ResetCurrentTarget();
            }
            yield return 200;   // (the turret's burst cooldown over)
        }

        // Diagnostics only (-cepackm230diag): reads the turret's state and CE's own target tests; changes nothing.
        private static void M230Diag(Building_CIWS_CE t, Skyfaller pod)
        {
            try
            {
                VerbCIWSSkyfaller v = t.GunCompEq.AllVerbs.OfType<VerbCIWSSkyfaller>().FirstOrDefault();
                bool friendly = (bool)AccessTools.Method(typeof(VerbCIWSSkyfaller), "IsFriendlyTo").Invoke(v, new object[] { pod });
                int n = v.Targets?.Count() ?? -1;
                object[] args = { t.Position, new LocalTargetInfo(pod), null, null };
                bool line = (bool)typeof(VerbCIWS<Skyfaller>).GetMethods().First(m => m.Name == "TryFindCEShootLineFromTo" && m.DeclaringType == typeof(VerbCIWS<Skyfaller>) && m.GetParameters().Length == 4).Invoke(v, args);
                ShootLine sl = args[2] is ShootLine s0 ? s0 : default;
                Vector3 tp = args[3] is Vector3 v3 ? v3 : default;
                object pp = AccessTools.Method(typeof(VerbCIWSSkyfaller), "PredictPositions").Invoke(v, new object[] { pod, 70 });
                List<Vector3> pts = ((IEnumerable<Vector3>)pp).Take(70).ToList();
                Out("    diag t" + Find.TickManager.TicksGame + " pod ticksToImpact " + pod.ticksToImpact + " drawPos " + pod.DrawPos.ToString("F1") + "; turret current " + t.CurrentTarget + " warmup " + t.burstWarmupTicksLeft + " cooldown " + t.burstCooldownTicksLeft
                    + " attackVerb " + t.AttackVerb?.GetType().Name + " state " + t.AttackVerb?.state + "; CIWS verb available " + v.Available() + ", targets " + n + ", friendly " + friendly + ", interceptable " + v.Props.Interceptable(pod.def)
                    + ", line " + line + " dest " + sl.Dest + " pos " + tp.ToString("F1") + ", predicted " + pts.Count + " first " + (pts.Count > 0 ? pts[0].ToString("F1") : "-") + " [7] " + (pts.Count > 7 ? pts[7].ToString("F1") : "-") + " last " + (pts.Count > 0 ? pts[pts.Count - 1].ToString("F1") : "-")
                    + ", shot height " + v.ShotHeight.ToString("0.00") + " speed " + Traverse.Create(v).Property("ShotSpeed").GetValue<float>().ToString("0.0"));
            }
            catch (Exception e)
            {
                Out("    diag failed: " + e.GetType().Name + " " + e.Message + " " + e.StackTrace?.Split('\n').FirstOrDefault());
            }
        }

        private static string PodSummary(Thing turret, List<PodLog> list, out int shots, out int hits, out int down)
        {
            HashSet<string> ids = new HashSet<string>(list.Select(p => p.def + "#" + p.id));
            shots = Launches.Count(s => s.by == turret.ThingID && ids.Contains(s.line));
            hits = Impacts.Count(s => s.by == turret.ThingID && ids.Contains(s.hit));
            down = list.Count(p => p.shotDown);
            return "pods " + list.Count + ", CIWS shots " + shots + ", hits " + hits + ", shot down " + down + ", landed " + list.Count(p => p.landed);
        }

        // ---- L: load
        private static IEnumerator<int> M230Load()
        {
            Out("--- L M230LF load");
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_M230LFs");
            ThingDef gun = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_M230LFs");
            string verbs = gun != null ? string.Join(", ", gun.Verbs.Select(v => v.GetType().Name + ":" + v.verbClass?.Name + " range " + v.range + " proj " + v.defaultProjectile?.defName)) : "-";
            Out("  def " + (d != null) + ", class " + d?.thingClass?.FullName + ", gun verbs: " + verbs + "; CE EnableCIWS " + Controller.settings.EnableCIWS + "; pack CIWS group " + Ciws.On + " (switched off " + PackZ.SwitchedOff + ")"
                + "; ActiveTransporter group " + (int)ThingRequestGroup.ActiveTransporter);
            Wipe(200, 200, 215, 215);
            Building_CIWS_CE t = d != null ? M230(On(207, 207, 0)) : null;
            yield return 30;
            string tv = t != null ? string.Join(", ", t.GunCompEq.AllVerbs.Select(v => v.GetType().Name + (v is VerbCIWS c ? " active " + c.Active + " available " + c.Available() : ""))) : "-";
            Out("  built " + (t != null) + " " + t?.GetType().Name + ", active " + t?.Active + ", verbs: " + tv);
            bool ok = d != null && t != null && d.thingClass == typeof(Building_CIWS_CE) && gun.Verbs.Any(v => v.verbClass == typeof(VerbCIWSSkyfaller))
                && t.GunCompEq.AllVerbs.Any(v => v is VerbCIWSSkyfaller s && s.Available()) && Controller.settings.EnableCIWS && (Ciws.On || PackZ.SwitchedOff);
            Check("L M230LF loads, builds and has an available CIWS skyfaller verb", ok, "class " + d?.thingClass?.Name + ", verbs " + tv);
            poweredM230.Remove(t);
            Gone(t);
        }

        // ---- G: turret on the ground, pods onto the ground nearby.
        private static IEnumerator<int> M230Ground()
        {
            Out("--- G M230LF on the ground, pods onto the ground nearby");
            Wipe(15, 15, 75, 75);
            Building_CIWS_CE t = M230(On(45, 45, 0));
            yield return 30;
            int tr0 = Ciws.Translated;
            List<PodLog> list = new List<PodLog>();
            foreach ((int x, int z) in new[] { (45, 58), (58, 45), (33, 40), (50, 32) })
            {
                IEnumerator<int> e = DropOne(t, On(x, z, 0), "ground", list);
                while (e.MoveNext())
                {
                    yield return e.Current;
                }
            }
            string r = PodSummary(t, list, out int shots, out int hits, out int down);
            Out("  " + r + ", translated " + (Ciws.Translated - tr0));
            Check("G M230LF on the ground engages and intercepts pods dropping onto the ground nearby", list.Count > 0 && shots > 0 && hits > 0 && down > 0, r);
            poweredM230.Remove(t);
            Gone(t);
        }

        // ---- D: turret on a floor-1 deck; pods onto the deck and onto the ground beside the building.
        private static IEnumerator<int> M230Deck()
        {
            Out("--- D M230LF on a floor-1 deck, pods onto the deck and onto the ground beside the building");
            if (L == null || !L.Has(1))
            {
                Check("D deck", false, "needs a map with floors");
                yield break;
            }
            Wipe(95, 15, 175, 95);
            Slab(120, 40, 150, 70);
            Building_CIWS_CE t = M230(On(135, 55, 1));
            yield return 30;
            int tr0 = Ciws.Translated, top0 = Ciws.TopAims, bl0 = Ciws.BlockedPoints;
            List<PodLog> deck = new List<PodLog>(), beside = new List<PodLog>();
            foreach ((int x, int z) in new[] { (128, 63), (143, 48) })
            {
                IEnumerator<int> e = DropOne(t, On(x, z, 1), "deck", deck);
                while (e.MoveNext())
                {
                    yield return e.Current;
                }
            }
            int trDeck = Ciws.Translated - tr0;
            foreach ((int x, int z) in new[] { (135, 80), (162, 55), (108, 50) })
            {
                IEnumerator<int> e = DropOne(t, On(x, z, 0), "ground beside", beside);
                while (e.MoveNext())
                {
                    yield return e.Current;
                }
            }
            string rd = PodSummary(t, deck, out int sd, out int hd, out int dd);
            string rb = PodSummary(t, beside, out int sb, out int hb, out int db);
            Out("  deck: " + rd + "; beside: " + rb + "; translated (deck pods " + trDeck + ", all " + (Ciws.Translated - tr0) + "), top aims across " + (Ciws.TopAims - top0) + ", blocked points " + (Ciws.BlockedPoints - bl0));
            Check("D1 M230LF on a deck engages and intercepts pods dropping onto its deck", deck.Count > 0 && sd > 0 && hd > 0 && dd > 0, rd);
            Check("D2 M230LF on a deck engages and intercepts pods dropping onto the ground beside the building", beside.Count > 0 && sb > 0 && hb > 0 && db > 0, rb);
            poweredM230.Remove(t);
            Gone(t);
        }

        // ---- B: turret on the ground under a solid slab; pods onto the slab top.
        private static IEnumerator<int> M230Blocked()
        {
            Out("--- B M230LF under a solid slab, pods onto the slab top");
            if (L == null || !L.Has(1))
            {
                Check("B slab", false, "needs a map with floors");
                yield break;
            }
            Wipe(25, 140, 105, 220);
            Slab(35, 150, 95, 210);
            Building_CIWS_CE t = M230(On(65, 180, 0));
            yield return 30;
            int bl0 = Ciws.BlockedPoints;
            List<PodLog> list = new List<PodLog>();
            foreach ((int x, int z) in new[] { (65, 192), (77, 175), (54, 170) })
            {
                IEnumerator<int> e = DropOne(t, On(x, z, 1), "slab top", list);
                while (e.MoveNext())
                {
                    yield return e.Current;
                }
            }
            string r = PodSummary(t, list, out int shots, out int hits, out int down);
            Out("  " + r + ", blocked points " + (Ciws.BlockedPoints - bl0) + ", roofed over the turret " + t.Position.Roofed(Map));
            Check("B M230LF under a slab does not engage pods dropping onto the slab top", list.Count > 0 && shots == 0 && hits == 0 && list.All(p => p.landed), r + ", blocked points " + (Ciws.BlockedPoints - bl0));
            poweredM230.Remove(t);
            Gone(t);
        }
    }
}
