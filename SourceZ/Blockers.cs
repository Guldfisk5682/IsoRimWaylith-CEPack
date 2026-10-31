using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using CombatExtended.Compatibility;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Other mods' shields between floors (plan §2a BlockerRegistry; user decision M13: the same 3D rule as CE's shields).
    // Mods register their projectile blockers with CE's BlockerRegistry: a callback per projectile step (from, to: CE's
    // LastPos and ExactPosition), one when a projectile comes down (ImpactSomething: shells landing), and the cells their
    // shields cover (ShieldZonesCallback). Their own geometry is flat: a circle round the shield's cell in raw rows (CE's
    // compat classes for ED-Shields, Rimatomics, Vanilla Psycasts Expanded all do it so), so a shield on another floor than
    // the projectile never met it. Between floors the pack takes the shield's dome as for CE's shields (Shields, ProjectZ's
    // Shields3D rule):
    //  - where the shields are: the registry's shield zones (each one a radial around the shield's cell, the centre cell
    //    first: GenRadial.RadialCellsAround, as every CE compat class makes them); the dome is the hemisphere of the zone's
    //    radius over the middle of the centre cell, on the floor of its band, in virtual space; floor slabs and roofs don't
    //    cut it;
    //  - a projectile step (in 3D) entering a dome on another floor (or the first step after coming down into the dome's
    //    storey from the one above), or a projectile coming down inside a dome on another floor or through it, is offered
    //    to the registered callbacks in the dome's frame: the projectile seen in that storey's frame (FrameView), the step
    //    a line through the dome's centre against the projectile's heading (so a flat circle test meets it there). The
    //    callbacks decide as they always do (active, hostile, energy, effects) and intercept;
    //  - the projectile is stopped at the dome entry (coming down: where it came down), in its own frame (CE's
    //    InterceptProjectile, a prefix while offered).
    // A shield in the projectile's own storey: the callbacks as CE calls them, unchanged. A mod that registers blockers but
    // no shield zones: no geometry to go by, its shields stay on their own floor.
    internal static class Blockers
    {
        public static int Entries, Offers, Met, Landings, ZoneSets;

        private delegate bool BetweenFn(ProjectileCE p, Vector3 from, Vector3 to);
        private delegate bool LandFn(ProjectileCE p, Thing launcher);
        private static readonly BetweenFn between = AccessTools.MethodDelegate<BetweenFn>(AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.CheckForCollisionBetweenCallback)));
        private static readonly LandFn land = AccessTools.MethodDelegate<LandFn>(AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.ImpactSomethingCallback)));

        private struct Zone
        {
            public IntVec3 ground;
            public int floor;
            public float radius;
        }

        // the zones of the map, once per tick
        private static readonly List<Zone> zones = new List<Zone>();
        private static Map zonesMap;
        private static int zonesTick = -1;

        // the projectile offered now, and where it is stopped if a callback intercepts it
        private static ProjectileCE offered;
        private static Layout offeredL;
        private static int offeredFrom, offeredTo;
        private static Vector3 offeredEntry;

        public static void Patch(Harmony h)
        {
            MethodInfo b = AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.CheckForCollisionBetweenCallback));
            MethodInfo l = AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.ImpactSomethingCallback));
            if (b == null || l == null || AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.ShieldZonesCallback)) == null)
            {
                throw new MissingMethodException("BlockerRegistry callbacks");
            }
            MethodInfo intercept = AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.InterceptProjectile), new[] { typeof(object), typeof(Vector3), typeof(bool) });
            if (intercept == null)
            {
                throw new MissingMethodException("ProjectileCE.InterceptProjectile(object, Vector3, bool)");
            }
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE), "CheckForCollisionBetween"), typeof(Blockers), nameof(Between_Transpiler));
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.ImpactSomething)), typeof(Blockers), nameof(Land_Transpiler));
            PackZ.Prefix(h, intercept, typeof(Blockers), nameof(Intercept_Prefix));
        }

        public static IEnumerable<CodeInstruction> Between_Transpiler(IEnumerable<CodeInstruction> instructions) =>
            SwapCall(instructions, AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.CheckForCollisionBetweenCallback)), AccessTools.Method(typeof(Blockers), nameof(Between)), "ProjectileCE.CheckForCollisionBetween");

        public static IEnumerable<CodeInstruction> Land_Transpiler(IEnumerable<CodeInstruction> instructions) =>
            SwapCall(instructions, AccessTools.Method(typeof(BlockerRegistry), nameof(BlockerRegistry.ImpactSomethingCallback)), AccessTools.Method(typeof(Blockers), nameof(Landing)), "ProjectileCE.ImpactSomething");

        // One call to `from` (same arguments) swapped for `to`.
        private static IEnumerable<CodeInstruction> SwapCall(IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to, string where)
        {
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == from)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = to;
                    found++;
                }
                yield return ci;
            }
            if (found != 1)
            {
                throw new InvalidOperationException(where + ": " + found + " " + from.Name + " calls (expected 1)");
            }
        }

        // CE: BlockerRegistry.CheckForCollisionBetweenCallback(this, LastPos, ExactPosition), each step.
        public static bool Between(ProjectileCE p, Vector3 from, Vector3 to)
        {
            if (between(p, from, to))
            {
                return true;
            }
            Layout L = Layout.Of(p.Map);
            return L != null && Across(p, L, false, null);
        }

        // CE: BlockerRegistry.ImpactSomethingCallback(this, launcher), a projectile coming down.
        public static bool Landing(ProjectileCE p, Thing launcher)
        {
            if (land(p, launcher))
            {
                return true;
            }
            Layout L = Layout.Of(p.Map);
            return L != null && Across(p, L, true, launcher);
        }

        private static bool Across(ProjectileCE p, Layout L, bool landing, Thing launcher)
        {
            if (offered != null || !p.Spawned)
            {
                return false;   // (a callback that steps a projectile itself: no nested offers)
            }
            List<Zone> zs = ZonesOf(p);
            if (zs.Count == 0)
            {
                return false;
            }
            Vector3 now = Frames.exactGet(p), last = Frames.lastPosRef(p);
            int kp = L.FrameOf(now);
            if (kp == Layout.None)
            {
                return false;
            }
            float storey = ProjectZ.Api.StoreyHeight;
            Vector3 v1 = L.ToVirtual(now, kp), v0 = L.ToVirtual(last, kp);
            for (int i = 0; i < zs.Count; i++)
            {
                Zone z = zs[i];
                if (z.floor == kp && (landing || last.y <= Layout.S))
                {
                    continue;   // its own storey: CE's call above
                }
                Vector3 s = new Vector3(z.ground.x + 0.5f, z.floor * storey, z.ground.z + 0.5f);
                Vector3 at;
                if (landing)
                {
                    // down inside the dome, or through it (a floor below it, through an open plane)
                    float dx = v1.x - s.x, dz = v1.z - s.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 > z.radius * z.radius)
                    {
                        continue;
                    }
                    float top = s.y + Mathf.Sqrt(z.radius * z.radius - d2);
                    if (v1.y > top + 0.05f)
                    {
                        continue;
                    }
                    at = v1;   // (stopped where it came down: the shell is gone or bursts there, in its own frame)
                }
                else if (!Shields.DomeEntry(v0, v1, s, z.radius, false, out at) || at.y < s.y - 0.05f)
                {
                    continue;
                }
                Entries++;
                if (Offer(p, L, kp, z, at, landing, launcher, now - last))
                {
                    return true;
                }
                if (!p.Spawned || p.Destroyed)
                {
                    return true;
                }
            }
            return false;
        }

        // The callbacks, with the projectile seen in the dome's frame (see the class header).
        private static bool Offer(ProjectileCE p, Layout L, int kp, Zone z, Vector3 at, bool landing, Thing launcher, Vector3 heading)
        {
            Offers++;
            int kz = z.floor;
            Vector3 atZ = L.FromVirtual(at, kz);
            Vector2 hd = new Vector2(heading.x, heading.z);
            if (hd.sqrMagnitude < 1e-8f)
            {
                Vector3 vel = Frames.velocityRef(p);
                hd = new Vector2(vel.x, vel.z);
            }
            hd = hd.sqrMagnitude < 1e-8f ? new Vector2(0f, 1f) : hd.normalized;
            IntVec3 cz = L.Cell(z.ground, kz);
            Vector3 centre = new Vector3(cz.x + 0.5f, atZ.y, cz.z + 0.5f);
            Vector3 from = centre - new Vector3(hd.x, 0f, hd.y) * (z.radius + 2f);
            offered = p;
            offeredL = L;
            offeredFrom = kp;
            offeredTo = kz;
            offeredEntry = L.FromVirtual(at, kp);
            bool met;
            try
            {
                FrameView.Move(p, L, kp, kz);
                met = landing ? land(p, launcher) : between(p, from, centre);
            }
            catch (Exception e)
            {
                // another mod's callback failed on a projectile from another floor: that shield lets it pass
                met = false;
                CallbackErrors++;
                PackZ.Once("blocker:offer", "Another mod's shield failed on a projectile from another floor (" + PackZ.Reason(e) + "): its shields stay on their own floor where that happens.", true);
            }
            finally
            {
                EndView();
                offered = null;
            }
            if (met)
            {
                Met++;
                if (landing)
                {
                    Landings++;
                }
                PackZ.Changed++;
            }
            return met;
        }

        // The projectile back in its own frame (if it is still there).
        private static void EndView()
        {
            ProjectileCE p = offered;
            if (p == null || offeredFrom == offeredTo)
            {
                return;
            }
            int k = offeredTo;
            offeredTo = offeredFrom;
            if (p.Spawned && !p.Destroyed)
            {
                FrameView.Move(p, offeredL, k, offeredFrom);
            }
        }

        // CE: InterceptProjectile(interceptor, impactPosition, destroyCompletely). A callback stopping the offered
        // projectile: back in its own frame first, stopped at the dome entry.
        public static void Intercept_Prefix(ProjectileCE __instance, ref Vector3 impactPosition)
        {
            if (offered == null || __instance != offered)
            {
                return;
            }
            EndView();
            impactPosition = offeredEntry;
        }

        // The registry's shield zones on the projectile's map (CE passes the thing to be protected; the projectile stands
        // in for it: it has no faction, so every zone that protects a non-hostile thing is given).
        private static List<Zone> ZonesOf(ProjectileCE p)
        {
            Map map = p.Map;
            int tick = Find.TickManager.TicksGame;
            if (map == zonesMap && tick == zonesTick)
            {
                return zones;
            }
            zonesMap = map;
            zonesTick = tick;
            zones.Clear();
            Layout L = Layout.Of(map);
            // (other mods' code, called with a projectile for the thing to protect: a failure keeps the zones read so far)
            try
            {
                foreach (IEnumerable<IntVec3> cells in BlockerRegistry.ShieldZonesCallback(p))
                {
                    if (cells == null)
                    {
                        continue;
                    }
                    try
                    {
                        AddZone(cells, L);
                    }
                    catch (Exception e)
                    {
                        CallbackErrors++;
                        PackZ.Once("blocker:zone", "Another mod's shield zone could not be read (" + PackZ.Reason(e) + "): that shield stays on its own floor.", true);
                    }
                }
            }
            catch (Exception e)
            {
                CallbackErrors++;
                PackZ.Once("blocker:zones", "Another mod's shield zones failed for a projectile (" + PackZ.Reason(e) + "): its shields stay on their own floor.", true);
            }
            if (zones.Count > 0)
            {
                ZoneSets++;
            }
            return zones;
        }

        public static int CallbackErrors, CentresMoved;
        private static readonly List<IntVec3> zoneCells = new List<IntVec3>();

        // One zone: its centre is the first cell (GenRadial.RadialCellsAround, as CE's compat classes make them); a zone
        // whose first cell is not at the middle of its cells (more than a cell off) is centred on its cells' bounding box.
        // The radius: the farthest cell from the centre.
        private static void AddZone(IEnumerable<IntVec3> cells, Layout L)
        {
            zoneCells.Clear();
            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
            foreach (IntVec3 cell in cells)
            {
                zoneCells.Add(cell);
                x0 = Mathf.Min(x0, cell.x);
                x1 = Mathf.Max(x1, cell.x);
                z0 = Mathf.Min(z0, cell.z);
                z1 = Mathf.Max(z1, cell.z);
            }
            if (zoneCells.Count == 0)
            {
                return;
            }
            IntVec3 c = zoneCells[0];
            IntVec3 box = new IntVec3((x0 + x1) / 2, 0, (z0 + z1) / 2);
            if (Mathf.Abs(c.x - box.x) > 1 || Mathf.Abs(c.z - box.z) > 1)
            {
                c = box;
                CentresMoved++;
            }
            float r2 = 0f;
            foreach (IntVec3 cell in zoneCells)
            {
                float dx = cell.x - c.x, dz = cell.z - c.z;
                r2 = Mathf.Max(r2, dx * dx + dz * dz);
            }
            int k = L.FrameOf(c);
            if (k == Layout.None)
            {
                return;
            }
            zones.Add(new Zone { ground = L.Ground(c), floor = k, radius = Mathf.Max(0.5f, Mathf.Sqrt(r2)) });
        }

        // Harness: the zones read last, and a callback being offered a projectile now.
        internal static List<(IntVec3 ground, int floor, float radius)> ZonesRead => zones.ConvertAll(z => (z.ground, z.floor, z.radius));

        internal static bool Offering => offered != null;
    }
}
