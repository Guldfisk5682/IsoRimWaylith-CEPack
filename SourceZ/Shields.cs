using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Shields between floors (plan §2a CheckIntercept, decision D8; user decision 2026-10-30: ProjectZ's dome rule). CE
    // checks every projectile interceptor on the map (shield bubbles, projectors, psychic skip shields, mech shields:
    // CompProjectileInterceptor) with its centre at the interceptor's cell, 0.5 CE units over its floor. A shield on
    // another floor than the projectile is a band away in raw rows, so it never met a projectile passing through it from
    // another storey. ProjectileCE.CheckIntercept, between floors only, takes ProjectZ's dome (Shields3D, the rule for
    // vanilla shields under IsoRimZ):
    //  - the dome is a hemisphere of the shield's radius (cells) over the middle of its cell, on its floor, in virtual
    //    space (heights in cells); floor slabs and roofs don't cut it (a dome under a slab stops shots over the slab
    //    where they pass through its sphere);
    //  - a projectile is stopped where its step (LastPos -> ExactPosition, in 3D) enters the dome from outside (leaving
    //    it too when the shield stops outgoing shots); under the dome's floor it passes;
    //  - CE's own check then runs on: hostility, outgoing rule, hit points, EMP and effects; the projectile is placed at
    //    the entry point (in its own frame) and the shield's flash angle is taken over the ground.
    // The same 3D test runs on the first step after a projectile came down into the shield's storey from the one above:
    // CE's circle would count it as already inside the dome when it was over it.
    // One floor, or a map without floors: CE's own check, unchanged. Personal shield belts (CompShield) are no sphere in
    // CE: they absorb a hit on their wearer, which Frames already delivers between floors.
    internal static class Shields
    {
        public static int Checked, Met, UnderFloor, ThroughPlane;

        // the interceptor being checked (CheckIntercept runs one at a time; set by Centre, the method's first call)
        private static bool cross, enters;
        private static Layout lay;
        private static int kShield, kProj;
        private static Vector3 entry;   // the dome entry point in the projectile's frame

        // CE_Utility.IntersectionPoint (Unity-typed: called through a delegate)
        private delegate bool SectFn(Vector3 p1, Vector3 p2, Vector3 center, float radius, out Vector3[] sect, bool catchOutbound, bool spherical, Map map);
        private static readonly SectFn sectFn = AccessTools.MethodDelegate<SectFn>(AccessTools.Method(typeof(CE_Utility), Fault.Name(nameof(CE_Utility.IntersectionPoint), "shields")));

        public static void Patch(Harmony h)
        {
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE), "CheckIntercept"), typeof(Shields), nameof(CheckIntercept_Transpiler));
        }

        public static IEnumerable<CodeInstruction> CheckIntercept_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo shifted = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3ShiftedWithAltitude), new[] { typeof(float) });
            MethodInfo trueCenter = AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(Thing) });
            MethodInfo sect = AccessTools.Method(typeof(CE_Utility), nameof(CE_Utility.IntersectionPoint));
            Dictionary<MethodInfo, MethodInfo> swaps = new Dictionary<MethodInfo, MethodInfo>
            {
                { shifted, AccessTools.Method(typeof(Shields), nameof(Centre)) },
                { trueCenter, AccessTools.Method(typeof(Shields), nameof(TrueCenterFor)) },
                { sect, AccessTools.Method(typeof(Shields), nameof(Intersect)) },
            };
            Dictionary<MethodInfo, int> found = new Dictionary<MethodInfo, int>();
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && swaps.TryGetValue(mi, out MethodInfo mine))
                {
                    // the call's arguments are on the stack: add the projectile (this; the centre also gets the
                    // interceptor comp, CheckIntercept's second argument) and call the frame version
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    if (mi == shifted)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_2);
                    }
                    yield return new CodeInstruction(OpCodes.Call, mine);
                    found[mi] = found.TryGetValue(mi, out int n) ? n + 1 : 1;
                    continue;
                }
                yield return ci;
            }
            if (!found.ContainsKey(shifted) || !found.ContainsKey(sect) || found[shifted] != 1)
            {
                throw new InvalidOperationException("ProjectileCE.CheckIntercept: shield centre or intersection call not found");
            }
        }

        // CE: interceptorThing.Position.ToVector3ShiftedWithAltitude(0.5f), the shield's centre. Between floors the dome
        // test is made here (ProjectZ's rule, in virtual space); the centre handed back to CE then only steers its own
        // pre-checks: far over the projectile when the step doesn't enter the dome (CE: no intercept), just ahead of it
        // when it does (CE's distance and outgoing pre-checks pass; Intersect gives the entry point).
        public static Vector3 Centre(ref IntVec3 c, float altitude, ProjectileCE p, CompProjectileInterceptor comp)
        {
            Vector3 v = c.ToVector3ShiftedWithAltitude(altitude);
            cross = false;
            Layout L = Layout.Of(p.Map);
            if (L == null || comp == null)
            {
                return v;
            }
            Vector3 now = Frames.exactGet(p), last = Frames.lastPosRef(p);
            int ks = L.FrameOf(c), kp = L.FrameOf(now);
            if (ks == Layout.None || kp == Layout.None)
            {
                return v;
            }
            if (ks == kp && last.y <= Layout.S)
            {
                return v;   // one floor: CE's own (its circle over the ground holds within a storey)
            }
            // another floor, or the first step after coming down into this storey from the one above (CE's circle would
            // count the projectile as already inside: it was over the dome)
            Checked++;
            cross = true;
            lay = L;
            kShield = ks;
            kProj = kp;
            enters = false;
            float r = comp.Props.radius;
            IntVec3 g = L.Ground(c);
            Vector3 s = new Vector3(g.x + 0.5f, ks * ProjectZ.Api.StoreyHeight, g.z + 0.5f);
            Vector3 p0 = L.ToVirtual(last, kp), p1 = L.ToVirtual(now, kp);
            if (DomeEntry(p0, p1, s, r, comp.Props.interceptOutgoingProjectiles, out Vector3 at))
            {
                if (at.y < s.y - 0.05f)
                {
                    UnderFloor++;
                }
                else
                {
                    enters = true;
                    entry = L.FromVirtual(at, kp);
                    if (!PlanesOpen(L, p.Map, s + new Vector3(0f, 0.01f, 0f), at))
                    {
                        ThroughPlane++;   // (a counter for the harness: a slab or roof between the dome's centre and the entry)
                    }
                }
            }
            if (!enters)
            {
                return new Vector3(now.x, now.y + 1e4f, now.z);
            }
            Vector3 dir = now - last;
            dir = dir.sqrMagnitude < 1e-8f ? Vector3.forward : dir.normalized;
            return now + dir * (r + 0.05f);
        }

        // ProjectZ Shields3D.Crosses: where the segment p0 -> p1 enters the sphere (s, r) from outside (or leaves it, when
        // outgoing shots count).
        internal static bool DomeEntry(Vector3 p0, Vector3 p1, Vector3 s, float r, bool outgoing, out Vector3 at)
        {
            at = p1;
            Vector3 d = p1 - p0;
            Vector3 m = p0 - s;
            float a = Vector3.Dot(d, d);
            if (a < 1e-6f)
            {
                return false;
            }
            float b = 2f * Vector3.Dot(d, m);
            float c = Vector3.Dot(m, m) - r * r;
            float disc = b * b - 4f * a * c;
            if (disc < 0f)
            {
                return false;
            }
            float q = Mathf.Sqrt(disc);
            float t = c > 0f ? (-b - q) / (2f * a) : (outgoing ? (-b + q) / (2f * a) : -1f);
            if (t < 0f || t > 1f)
            {
                return false;
            }
            at = p0 + d * t;
            return true;
        }

        // CE: the shield's TrueCenter for its flash angle: over the ground.
        public static Vector3 TrueCenterFor(Thing t, ProjectileCE p)
        {
            Vector3 v = t.TrueCenter();
            return cross ? lay.TranslateRows(v, kShield, kProj) : v;
        }

        // CE: CE_Utility.IntersectionPoint(LastPos, ExactPosition, centre, radius, ...). Between floors: the dome entry
        // found in Centre (CE moves the projectile there).
        public static bool Intersect(Vector3 p1, Vector3 p2, Vector3 center, float radius, out Vector3[] sect, bool catchOutbound, bool spherical, Map map, ProjectileCE p)
        {
            if (!cross)
            {
                return sectFn(p1, p2, center, radius, out sect, catchOutbound, spherical, map);
            }
            sect = new[] { entry, entry };
            if (!enters)
            {
                return false;
            }
            Met++;
            PackZ.Changed++;
            return true;
        }

        // The line between two virtual points (heights in cells) crosses no solid floor plane (diagnostics; ProjectZ Api PlaneSolid at
        // the column where it passes each plane's height).
        internal static bool PlanesOpen(Layout L, Map map, Vector3 a, Vector3 b)
        {
            float lo = Mathf.Min(a.y, b.y), hi = Mathf.Max(a.y, b.y);
            if (hi - lo < 1e-4f)
            {
                return true;
            }
            float storey = ProjectZ.Api.StoreyHeight;
            for (int k = L.min + 1; k <= L.max + 1; k++)
            {
                float level = k * storey;
                if (level <= lo || level >= hi)
                {
                    continue;
                }
                float t = (level - a.y) / (b.y - a.y);
                IntVec3 g = new IntVec3(Mathf.FloorToInt(a.x + t * (b.x - a.x)), 0, Mathf.FloorToInt(a.z + t * (b.z - a.z)));
                if (ProjectZ.Api.PlaneSolid(g, k, map))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
