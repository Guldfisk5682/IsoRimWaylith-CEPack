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
using Verse.AI;

namespace IsoRimCEPackZ
{
    // A lease owns one unspawned native CE turret. Nested leases never mutate the same prototype.
    internal sealed class PlacementQuery : IPlacementQuery
    {
        internal static bool Ready;
        private sealed class Pool
        {
            public Pool() { }
            internal readonly Dictionary<(ThingDef, ThingDef), Stack<Building_TurretGunCE>> Guns = new();
        }
        private static readonly ConditionalWeakTable<Map, Pool> pools = new();
        private static readonly AccessTools.FieldRef<Thing, Faction> faction = AccessTools.FieldRefAccess<Thing, Faction>("factionInt");
        private PlacementSpec spec;
        private Building_TurretGunCE gun;
        private readonly AmmoDef selected, ammunition;
        private readonly IntVec3 position;
        private readonly Rot4 rotation;
        private readonly Faction originalFaction;
        private readonly Stack<Building_TurretGunCE> pool;

        internal static void Patch(Harmony h)
        {
            PackZ.Transpiler(h, PackZ.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange)), typeof(PlacementQuery), nameof(MapReads));
            PackZ.Transpiler(h, AccessTools.PropertyGetter(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.ShooterPawn)),
                typeof(PlacementQuery), nameof(OperatorReads));
        }

        internal static GeometryStatus Open(PlacementSpec spec, out IPlacementQuery query)
        {
            query = null;
            if (!Ready) return GeometryStatus.Unavailable;
            if (spec.Map == null || !spec.Cell.InBounds(spec.Map) ||
                Layout.Of(spec.Map) is Layout l && l.FrameOf(spec.Cell) == Layout.None) return GeometryStatus.InvalidInput;
            if (spec.TurretDef?.thingClass == null || !typeof(Building_TurretGunCE).IsAssignableFrom(spec.TurretDef.thingClass))
                return GeometryStatus.NotApplicable;
            var pool = pools.GetOrCreateValue(spec.Map).Guns;
            var key = (spec.TurretDef, spec.Stuff);
            if (!pool.TryGetValue(key, out var idle)) pool.Add(key, idle = new());
            Building_TurretGunCE gun = idle.Count != 0 ? idle.Pop() : (Building_TurretGunCE)ThingMaker.MakeThing(spec.TurretDef, spec.Stuff);
            if (gun.AttackVerb is not Verb_LaunchProjectileCE)
            { idle.Push(gun); return GeometryStatus.NotApplicable; }
            if (spec.Ammunition != null && (spec.Ammunition is not AmmoDef ||
                gun.CompAmmo?.Props.ammoSet?.ammoTypes?.Any(x => x.ammo == spec.Ammunition) != true))
            { idle.Push(gun); return GeometryStatus.InvalidInput; }
            query = new PlacementQuery(spec, gun, idle);
            return GeometryStatus.Success;
        }

        private PlacementQuery(PlacementSpec spec, Building_TurretGunCE gun, Stack<Building_TurretGunCE> pool)
        {
            this.spec = spec; this.gun = gun; this.pool = pool;
            position = gun.Position; rotation = gun.Rotation; originalFaction = gun.Faction;
            if (gun.CompAmmo is CompAmmoUser ammo)
            {
                selected = ammo.SelectedAmmo; ammunition = ammo.CurrentAmmo;
                AmmoDef choice = spec.Ammunition as AmmoDef ?? ammunition ?? ammo.Props.ammoSet?.ammoTypes?.FirstOrDefault()?.ammo;
                ammo.SelectedAmmo = choice; ammo.CurrentAmmo = choice;
            }
            gun.Position = spec.Cell; gun.Rotation = spec.Rotation; faction(gun) = spec.Operator?.Faction ?? spec.Faction;
            if (gun.CompFireArc is CompFireArc arc && arc.CurrentSpan < 0f) arc.CurrentSpan = arc.Props.spanRange.max;
        }

        private T Run<T>(Func<T> operation)
        {
            QueryContext.State before = QueryContext.Enter(gun, spec.Map, spec.Operator);
            try
            {
                Scope.State scope = Scope.Enter(gun);
                try { return operation(); }
                finally { Scope.Exit(scope); }
            }
            finally { QueryContext.Exit(before); }
        }

        public GeometryStatus ReadWeapon(out WeaponGeometry weapon)
        {
            weapon = default;
            if (gun == null) return GeometryStatus.InvalidInput;
            weapon = Run(() =>
            {
                var verb = (Verb_LaunchProjectileCE)gun.AttackVerb;
                bool direct = !gun.def.building.IsMortar && verb is not Verb_ShootMortarCE && !verb.Projectile.projectile.flyOverhead;
                return new WeaponGeometry(verb.EffectiveRange, verb.VerbPropsCE.minRangeInCover,
                    verb.Projectile, direct, gun.TryGetComp<CompMannable>() != null, verb.IsIncendiary_Ranged());
            });
            return GeometryStatus.Success;
        }

        public GeometryStatus CheckRangeAndArc(LocalTargetInfo target, out RangeArcResult result)
        {
            result = default;
            if (gun == null || !target.IsValid || !target.Cell.InBounds(spec.Map) || target.HasThing && target.Thing.Map != spec.Map)
                return GeometryStatus.InvalidInput;
            result = Run(() =>
            {
                var verb = (Verb_LaunchProjectileCE)gun.AttackVerb;
                IntVec3 cell = target.Cell;
                if (TargetGeometry.Resolve(target.Thing, new(spec.Map, gun, spec.Cell, verb, verb.Projectile), out var aim) == GeometryStatus.Success)
                    cell = aim.RangeCell;
                bool cross = ProjectZ.Api.HasFloors(spec.Map) && !ProjectZ.Api.SameFloor(spec.Cell, cell, spec.Map);
                float maximum = cross ? ProjectZ.Api.RangeDistance(spec.Cell, cell, spec.Map)
                    : Mathf.Sqrt((target.HasThing && cell == target.Cell ? target.Thing.OccupiedRect() : CellRect.SingleCell(cell)).ClosestDistSquaredTo(spec.Cell));
                float minimumDistance = ProjectZ.Api.Distance3D(spec.Cell, cell, spec.Map);
                float minimum = verb.verbProps.EffectiveMinRange(target, gun);
                return new RangeArcResult(maximum, minimumDistance, minimum,
                    maximum <= verb.EffectiveRange && minimumDistance >= minimum, gun.CompFireArc?.WithinFireArc(target) ?? true,
                    target.Thing is IAttackTarget attack && attack.ThreatDisabled(spec.Operator != null ? spec.Operator : gun));
            });
            return GeometryStatus.Success;
        }

        public GeometryStatus TraceFixedCorridor(LocalTargetInfo target, out FixedCorridorResult result)
        {
            result = default;
            GeometryStatus status = ReadWeapon(out var weapon);
            if (status != GeometryStatus.Success) return status;
            if (!weapon.DirectFire) return GeometryStatus.NotApplicable;
            if (!target.IsValid || !target.Cell.InBounds(spec.Map) || target.HasThing && target.Thing.Map != spec.Map)
                return GeometryStatus.InvalidInput;
            result = Run(() => Trace(target));
            return GeometryStatus.Success;
        }

        private FixedCorridorResult Trace(LocalTargetInfo target)
        {
            var verb = (Verb_LaunchProjectileCE)gun.AttackVerb;
            Layout l = Layout.Of(spec.Map);
            int sourceFrame = l?.FrameOf(spec.Cell) ?? 0;
            Vector3 from = gun.TrueCenter(); from.y = verb.ShotHeight;
            Vector3 to = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            to.y = target.HasThing ? new CollisionVertical(target.Thing).Max : 0f;
            int targetFrame = l?.FrameOf(target.Cell) ?? 0;
            Vector3 a = l != null ? l.ToVirtual(from, sourceFrame) : new(from.x, from.y * Layout.CellsPerUnit, from.z);
            Vector3 b = l != null ? l.ToVirtual(to, targetFrame) : new(to.x, to.y * Layout.CellsPerUnit, to.z);
            if (TargetGeometry.Resolve(target.Thing, new(spec.Map, gun, spec.Cell, verb, verb.Projectile), out var aim) == GeometryStatus.Success)
                b = aim.PointVirtual;
            var steps = new List<ProjectZ.Api.LineStep>();
            if (ProjectZ.Api.WalkLine3D(a, b, spec.Map, steps, true) == 0)
                return new(FixedObstruction.MapEdge, target.Cell, b);
            Vector3 delta = b - a;
            float closest = float.PositiveInfinity;
            FixedCorridorResult hit = new(FixedObstruction.None, IntVec3.Invalid, default);
            var seen = new HashSet<Building>();
            foreach (var step in steps)
            {
                if (step.solidPlane != int.MinValue && delta.y != 0f)
                {
                    float t = (step.solidPlane * ProjectZ.Api.StoreyHeight - a.y) / delta.y;
                    if (t >= 0f && t <= 1f && t < closest)
                    { closest = t; hit = new(FixedObstruction.FloorPlane, step.cell, a + delta * t); }
                }
                IntVec3 ground = l?.Ground(step.cell) ?? step.cell;
                int low = l != null ? Mathf.Max(l.min, step.floor + Mathf.FloorToInt(step.lo / ProjectZ.Api.StoreyHeight)) : 0;
                int high = l != null ? Mathf.Min(l.max, step.floor + Mathf.FloorToInt(step.hi / ProjectZ.Api.StoreyHeight)) : 0;
                for (int k = low; k <= high; k++)
                {
                    IntVec3 cell = l?.Cell(ground, k) ?? ground;
                    if (!cell.InBounds(spec.Map)) return new(FixedObstruction.MapEdge, cell, a);
                    Building building = cell.GetEdifice(spec.Map);
                    if (building == null || building == target.Thing || building == gun || !seen.Add(building) ||
                        building.def.Fillage != FillCategory.Full || building is Building_Door { Open: true }) continue;
                    Vector3 src = l != null ? l.FromVirtual(a, k) : new(a.x, a.y / Layout.CellsPerUnit, a.z);
                    Vector3 dst = l != null ? l.FromVirtual(b, k) : new(b.x, b.y / Layout.CellsPerUnit, b.z);
                    Ray ray = new(src, dst - src);
                    if (Aim.boundsFor(building).IntersectRay(ray, out float distance) && distance <= (dst - src).magnitude)
                    {
                        float t = distance / (dst - src).magnitude;
                        if (t < closest) { closest = t; hit = new(FixedObstruction.Building, cell, a + delta * t); }
                    }
                }
            }
            return hit;
        }

        public void Dispose()
        {
            if (gun == null) return;
            if (gun.CompAmmo is CompAmmoUser ammo) { ammo.SelectedAmmo = selected; ammo.CurrentAmmo = ammunition; }
            gun.Position = position; gun.Rotation = rotation; faction(gun) = originalFaction;
            pool.Push(gun); gun = null; spec = default;
        }

        private static IEnumerable<CodeInstruction> MapReads(IEnumerable<CodeInstruction> instructions)
        {
            var map = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
            var held = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.MapHeld));
            foreach (var c in instructions)
            {
                if (c.Calls(map) || c.Calls(held))
                { c.operand = AccessTools.Method(typeof(QueryContext), c.Calls(map) ? nameof(QueryContext.MapOf) : nameof(QueryContext.MapHeldOf)); c.opcode = OpCodes.Call; }
                yield return c;
            }
        }
        private static IEnumerable<CodeInstruction> OperatorReads(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(CE_Utility), nameof(CE_Utility.TryGetTurretOperator));
            foreach (var c in instructions)
            { if (c.Calls(original)) c.operand = AccessTools.Method(typeof(QueryContext), nameof(QueryContext.OperatorOf)); yield return c; }
        }
    }
}
