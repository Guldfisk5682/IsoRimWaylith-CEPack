using System;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    [Flags]
    public enum ExtensionCapabilities { None = 0, PlacementQueries = 1, TargetGeometry = 2, SurfaceExplosions = 4 }
    public enum GeometryStatus { Unavailable, Success, InvalidInput, NotApplicable }
    public enum FixedObstruction { Unavailable, None, MapEdge, Building, FloorPlane }

    // Points/bounds are ProjectZ virtual cells (absolute y); IntVec3 values are real map-band cells.
    // Calls and registrations belong to the game's main thread. Query handles and registration tokens are not save data.
    public static class Api
    {
        public static int Version => 1;
        public static ExtensionCapabilities Capabilities =>
            (PlacementQuery.Ready ? ExtensionCapabilities.PlacementQueries : 0) |
            (TargetGeometry.Ready ? ExtensionCapabilities.TargetGeometry : 0) |
            (SurfaceExplosions.Ready ? ExtensionCapabilities.SurfaceExplosions : 0);

        public static GeometryStatus OpenPlacement(PlacementSpec spec, out IPlacementQuery query) => PlacementQuery.Open(spec, out query);
        public static IDisposable RegisterTargetGeometry(string owner, Type targetType, ITargetGeometryProvider provider) =>
            TargetGeometry.Register(owner, targetType, provider);
        public static void NotifyTargetGeometryChanged(Thing target) => TargetGeometry.Refresh(target);
        public static GeometryStatus ResolveTargetAim(Thing target, TargetQuery query, out TargetAim aim) =>
            TargetGeometry.Resolve(target, query, out aim);
        public static IDisposable RegisterSurfaceExplosionRule(string owner, ISurfaceExplosionRule rule) =>
            SurfaceExplosions.Register(owner, rule);
    }

    public readonly record struct PlacementSpec(Map Map, ThingDef TurretDef, ThingDef Stuff, ThingDef Ammunition,
        IntVec3 Cell, Rot4 Rotation, Faction Faction, Pawn Operator = null);
    public readonly record struct WeaponGeometry(float EffectiveRange, float MinimumRangeInCover,
        ThingDef Projectile, bool DirectFire, bool Mannable, bool Incendiary);
    public readonly record struct RangeArcResult(float MaximumRangeDistance, float MinimumRangeDistance,
        float MinimumRange, bool InRange, bool WithinArc, bool ThreatDisabled);
    public readonly record struct FixedCorridorResult(FixedObstruction Obstruction, IntVec3 Cell, Vector3 ContactVirtual)
    {
        public bool Clear => Obstruction == FixedObstruction.None;
    }
    public interface IPlacementQuery : IDisposable
    {
        GeometryStatus ReadWeapon(out WeaponGeometry weapon);
        GeometryStatus CheckRangeAndArc(LocalTargetInfo target, out RangeArcResult result);
        GeometryStatus TraceFixedCorridor(LocalTargetInfo target, out FixedCorridorResult result);
    }

    public readonly record struct TargetQuery(Map Map, Thing Shooter, IntVec3 Root, Verb Verb, ThingDef Projectile);
    public readonly record struct TargetAim(Vector3 PointVirtual, IntVec3 RangeCell);
    public interface ITargetGeometryProvider
    {
        // False leaves the entity on the native path. Both methods belong to the same selected provider.
        bool TryGetVolume(Thing target, Map map, out Bounds volumeVirtual);
        bool TryGetAim(Thing target, TargetQuery query, out TargetAim aim);
    }

    public enum SurfaceSide { Below, Above }
    public readonly record struct SurfaceContact(Map Map, IntVec3 GroundCell, int UpperFloor,
        IntVec3 SurfaceCell, IntVec3 IncidentCell, Vector3 PointVirtual, Vector3 NormalVirtual,
        SurfaceSide IncidentSide, bool HasFloor);
    public readonly record struct SurfaceExplosionContext(ExplosionCE Explosion, SurfaceContact Contact, ThingDef ProjectileDef);
    public interface ISurfaceExplosionRule
    {
        // Read-only policy: select the actual contact's terrain, never edit the explosion or apply damage here.
        bool ShouldDamageTerrain(SurfaceExplosionContext context);
    }
}
