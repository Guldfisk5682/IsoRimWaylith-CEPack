using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Supplemental physical occupancy for registered entities only; never changes thingGrid or walkability.
    internal static class TargetGeometry
    {
        internal static bool Ready;
        private sealed class Registration : IDisposable
        {
            internal string Owner;
            internal Type Type;
            internal ITargetGeometryProvider Provider;
            public void Dispose()
            {
                if (!registrations.Remove(this)) return;
                Rebuild();
            }
        }
        private sealed class Entry
        {
            internal Registration Registration;
            internal Bounds Volume;
            internal readonly List<IntVec3> Cells = new();
        }
        private sealed class Index
        {
            public Index() { }
            internal readonly Dictionary<Thing, Entry> Entities = new();
            internal readonly Dictionary<IntVec3, List<Thing>> Cells = new();
        }
        private static readonly List<Registration> registrations = new();
        private static readonly ConditionalWeakTable<Map, Index> indices = new();
        internal readonly record struct State(Map Map, int Frame, ProjectileCE Projectile);
        [ThreadStatic] private static State context;
        private static readonly Func<ProjectileCE, Ray> shotLine = AccessTools.MethodDelegate<Func<ProjectileCE, Ray>>(
            AccessTools.PropertyGetter(typeof(ProjectileCE), "ShotLine"));

        internal static IDisposable Register(string owner, Type type, ITargetGeometryProvider provider)
        {
            if (string.IsNullOrEmpty(owner) || type == null || !typeof(Thing).IsAssignableFrom(type) || provider == null)
                throw new ArgumentException("Target geometry requires an owner, Thing type and provider.");
            if (registrations.Any(x => x.Owner == owner && x.Type == type))
                throw new InvalidOperationException("Target geometry is already registered: " + owner + " / " + type.FullName);
            var r = new Registration { Owner = owner, Type = type, Provider = provider };
            registrations.Add(r);
            Rebuild();
            return r;
        }

        internal static void Activate() => Rebuild();

        private static void Rebuild()
        {
            if (Find.Maps == null) return;
            foreach (Map map in Find.Maps)
            {
                indices.Remove(map);
                foreach (Thing thing in map.listerThings.AllThings)
                    if (registrations.Any(r => r.Type.IsInstanceOfType(thing))) Refresh(thing);
            }
        }

        internal static void Remove(Thing thing)
        {
            Map map = thing.Map;
            if (map == null || !indices.TryGetValue(map, out var index) || !index.Entities.Remove(thing, out var old)) return;
            foreach (IntVec3 cell in old.Cells)
            {
                List<Thing> list = index.Cells[cell]; list.Remove(thing);
                if (list.Count == 0) index.Cells.Remove(cell);
            }
        }

        internal static void Refresh(Thing thing)
        {
            Remove(thing);
            if (!Ready || !thing.Spawned || thing.Destroyed || registrations.Count == 0) return;
            Map map = thing.Map;
            Layout l = Layout.Of(map);
            if (l == null) return;
            // Later explicit registrations override earlier ones as a whole, never volume and aim independently.
            for (int i = registrations.Count - 1; i >= 0; i--)
            {
                Registration r = registrations[i];
                if (!r.Type.IsInstanceOfType(thing) || !r.Provider.TryGetVolume(thing, map, out Bounds b)) continue;
                Vector3 min = b.min, max = b.max;
                if (!float.IsFinite(min.x) || !float.IsFinite(min.y) || !float.IsFinite(min.z) ||
                    !float.IsFinite(max.x) || !float.IsFinite(max.y) || !float.IsFinite(max.z) ||
                    b.size.x <= 0f || b.size.y <= 0f || b.size.z <= 0f)
                    throw new InvalidOperationException(r.Owner + " returned invalid target geometry for " + thing);
                Index index = indices.GetOrCreateValue(map);
                var entry = new Entry { Registration = r, Volume = b };
                int bottom = Mathf.Max(l.min, Mathf.FloorToInt(min.y / ProjectZ.Api.StoreyHeight));
                int top = Mathf.Min(l.max, Mathf.FloorToInt(max.y / ProjectZ.Api.StoreyHeight));
                for (int k = bottom; k <= top; k++)
                    for (int x = Mathf.Max(0, Mathf.FloorToInt(min.x)); x <= Mathf.Min(l.width - 1, Mathf.FloorToInt(max.x)); x++)
                        for (int z = Mathf.Max(0, Mathf.FloorToInt(min.z)); z <= Mathf.Min(l.rows - 1, Mathf.FloorToInt(max.z)); z++)
                        {
                            IntVec3 c = l.Cell(new IntVec3(x, 0, z), k);
                            if (!index.Cells.TryGetValue(c, out var list)) index.Cells.Add(c, list = new());
                            list.Add(thing); entry.Cells.Add(c);
                        }
                index.Entities.Add(thing, entry);
                return;
            }
        }

        private static bool TryEntry(Thing thing, Map map, out Entry entry)
        {
            entry = null;
            return Ready && thing != null && thing.Spawned && thing.Map == map &&
                indices.TryGetValue(map, out var index) && index.Entities.TryGetValue(thing, out entry);
        }
        internal static GeometryStatus Resolve(Thing target, TargetQuery query, out TargetAim aim)
        {
            aim = default;
            if (!Ready) return GeometryStatus.Unavailable;
            if (!TryEntry(target, query.Map, out var entry)) return GeometryStatus.NotApplicable;
            if (!entry.Registration.Provider.TryGetAim(target, query, out aim)) return GeometryStatus.NotApplicable;
            if (!aim.RangeCell.InBounds(query.Map) || !float.IsFinite(aim.PointVirtual.x) ||
                !float.IsFinite(aim.PointVirtual.y) || !float.IsFinite(aim.PointVirtual.z))
                throw new InvalidOperationException(entry.Registration.Owner + " returned an invalid aim for " + target);
            return GeometryStatus.Success;
        }
        internal static bool TryAim(Thing target, Verb verb, IntVec3 root, out TargetAim aim) =>
            Resolve(target, new(QueryContext.MapOf(verb?.caster), verb?.caster, root, verb,
                (verb as Verb_LaunchProjectileCE)?.Projectile), out aim) == GeometryStatus.Success;

        internal static List<Thing> At(Map map, IntVec3 cell) => Ready && map != null && indices.TryGetValue(map, out var index) &&
            index.Cells.TryGetValue(cell, out var list) ? list : null;

        internal static Bounds BoundsIn(Thing target, Map map, int frame)
        {
            if (!TryEntry(target, map, out var entry)) return Aim.boundsFor(target);
            return InFrame(entry.Volume, Layout.Of(map), frame);
        }
        private static Bounds InFrame(Bounds b, Layout l, int frame)
        {
            b.center = l.FromVirtual(b.center, frame);
            b.size = new Vector3(b.size.x, b.size.y / Layout.CellsPerUnit, b.size.z);
            return b;
        }
        internal static State Enter(Map map, int frame, ProjectileCE projectile = null)
        { State old = context; context = new(map, frame, projectile); return old; }
        internal static void Exit(State old) => context = old;

        internal static List<Thing> CollisionThings(ThingGrid grid, IntVec3 cell, ProjectileCE projectile)
        {
            List<Thing> native = grid.ThingsListAtFast(cell);
            List<Thing> extra = At(projectile.Map, cell);
            if (extra == null) return native;
            var result = new List<Thing>(native);
            foreach (Thing t in extra) if (!result.Contains(t)) result.Add(t);
            return result;
        }
        internal static float CoverHeight(LightingTracker tracker, IntVec3 cell, ProjectileCE projectile)
        {
            float height = tracker.HighestCoverAt(cell);
            List<Thing> extra = At(projectile.Map, cell);
            if (extra != null)
            {
                int frame = Layout.Of(projectile.Map).FrameOf(cell);
                foreach (Thing t in extra) height = Mathf.Max(height, BoundsIn(t, projectile.Map, frame).max.y);
            }
            return height;
        }
        internal static Vector3 DrawPos(Thing thing, ProjectileCE projectile)
        {
            Layout l = Layout.Of(projectile.Map);
            return l != null && TryEntry(thing, projectile.Map, out _)
                ? ContactCenter(thing, projectile) : thing.DrawPos;
        }
        internal static Vector3 ContactCenter(Thing thing, ProjectileCE projectile)
        {
            Layout l = Layout.Of(projectile.Map);
            if (l == null || !TryEntry(thing, projectile.Map, out _)) return thing.TrueCenter();
            Ray ray = shotLine(projectile);
            Bounds b = BoundsIn(thing, projectile.Map, l.FrameOf(Frames.exactGet(projectile)));
            return b.IntersectRay(ray, out float distance) ? ray.GetPoint(distance) : b.center;
        }

        internal static void Patch(Harmony h)
        {
            foreach (MethodInfo spawn in AccessTools.GetDeclaredMethods(typeof(GenSpawn)).Where(m =>
                m.Name == "Spawn" && m.ReturnType == typeof(Thing) && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Thing)))
                PackZ.Postfix(h, spawn, typeof(TargetGeometry), nameof(Spawned));
            PackZ.Prefix(h, PackZ.Method(typeof(Thing), nameof(Thing.DeSpawn)), typeof(TargetGeometry), nameof(Despawning));
            PackZ.Postfix(h, AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation)), typeof(TargetGeometry), nameof(Rotated));
            PackZ.Postfix(h, PackZ.Method(typeof(CE_Utility), nameof(CE_Utility.GetBoundsFor), new[] { typeof(Thing) }), typeof(TargetGeometry), nameof(BoundsPostfix));
            MethodInfo collision = PackZ.Method(typeof(ProjectileCE), "CheckCellForCollision");
            h.Patch(collision, prefix: new HarmonyMethod(typeof(TargetGeometry), nameof(CollisionPrefix)) { priority = Priority.First });
            PackZ.Applied++;
            h.Patch(collision, finalizer: new HarmonyMethod(typeof(TargetGeometry), nameof(CollisionFinalizer)));
            PackZ.Transpiler(h, collision, typeof(TargetGeometry), nameof(CollisionReads));
            foreach (var lambda in AccessTools.GetDeclaredMethods(typeof(ProjectileCE)).Where(m =>
                m.Name.StartsWith("<CheckCellForCollision>b__") && m.ReturnType == typeof(float) &&
                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Thing)))
                PackZ.Transpiler(h, lambda, typeof(TargetGeometry), nameof(CollisionReads));
            h.Patch(PackZ.Method(typeof(Verb), nameof(Verb.OutOfRange)), postfix: new HarmonyMethod(typeof(TargetGeometry), nameof(RangePostfix)) { priority = int.MinValue });
            PackZ.Postfix(h, Aim.ByName(typeof(Verb_LaunchProjectileCE), "TryFindCEShootLineFromTo", 4), typeof(TargetGeometry), nameof(LinePostfix));
        }
        private static void Spawned(Thing __result)
        { if (__result != null && registrations.Any(r => r.Type.IsInstanceOfType(__result))) Refresh(__result); }
        private static void Despawning(Thing __instance) => Remove(__instance);
        private static void Rotated(Thing __instance)
        { if (registrations.Any(r => r.Type.IsInstanceOfType(__instance))) Refresh(__instance); }
        private static void BoundsPostfix(Thing thing, ref Bounds __result)
        {
            if (context.Map != null && TryEntry(thing, context.Map, out var entry))
                __result = InFrame(entry.Volume, Layout.Of(context.Map), context.Frame);
        }
        private static void CollisionPrefix(ProjectileCE __instance, out State __state)
        {
            Layout l = Layout.Of(__instance.Map);
            __state = Enter(__instance.Map, l?.FrameOf(Frames.exactGet(__instance)) ?? Layout.None, __instance);
        }
        private static void CollisionFinalizer(State __state) => Exit(__state);
        private static void RangePostfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (__instance is not Verb_LaunchProjectileCE || !TryAim(targ.Thing, __instance, root, out var aim)) return;
            Map map = __instance.caster.Map;
            __result = ProjectZ.Api.RangeDistance(root, aim.RangeCell, map) > __instance.EffectiveRange ||
                ProjectZ.Api.Distance3D(root, aim.RangeCell, map) < __instance.verbProps.EffectiveMinRange(targ, __instance.caster);
        }
        private static void LinePostfix(Verb_LaunchProjectileCE __instance, IntVec3 root, LocalTargetInfo targ,
            ref Vector3 targetPos)
        {
            if (TryAim(targ.Thing, __instance, root, out var aim))
                targetPos = Layout.Of(__instance.caster.Map).FromVirtual(aim.PointVirtual, ProjectZ.Api.FloorOf(root, __instance.caster.Map));
        }
        private static IEnumerable<CodeInstruction> CollisionReads(IEnumerable<CodeInstruction> instructions)
        {
            var draw = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
            var center = AccessTools.Method(typeof(GenThing), nameof(GenThing.TrueCenter), new[] { typeof(Thing) });
            var cover = AccessTools.Method(typeof(LightingTracker), nameof(LightingTracker.HighestCoverAt));
            var things = AccessTools.Method(typeof(ThingGrid), nameof(ThingGrid.ThingsListAtFast), new[] { typeof(IntVec3) });
            foreach (var c in instructions)
            {
                string helper = c.Calls(draw) ? nameof(DrawPos) : c.Calls(center) ? nameof(ContactCenter)
                    : c.Calls(cover) ? nameof(CoverHeight) : c.Calls(things) ? nameof(CollisionThings) : null;
                if (helper != null)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(c).MoveBlocksFrom(c);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TargetGeometry), helper));
                }
                else yield return c;
            }
        }
    }
}
