using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // A final contact travels with its projectile; selected terrain travels with its actual explosion.
    internal static class SurfaceExplosions
    {
        internal static bool Ready;
        private sealed class Rule : IDisposable
        {
            internal string Owner;
            internal ISurfaceExplosionRule Policy;
            public void Dispose() => rules.Remove(this);
        }
        private static readonly List<Rule> rules = new();
        private sealed class ContactData : IExposable
        {
            public ContactData() { }
            internal Map Map;
            internal IntVec3 Ground, Surface, Incident;
            internal int Upper;
            internal Vector3 Point, Normal;
            internal bool Below, Floor, Final;
            internal SurfaceContact Contact => new(Map, Ground, Upper, Surface, Incident, Point, Normal,
                Below ? SurfaceSide.Below : SurfaceSide.Above, Floor);
            public void ExposeData()
            {
                Scribe_References.Look(ref Map, "map");
                Scribe_Values.Look(ref Ground, "ground"); Scribe_Values.Look(ref Surface, "surface");
                Scribe_Values.Look(ref Incident, "incident"); Scribe_Values.Look(ref Upper, "upper");
                Scribe_Values.Look(ref Point, "point"); Scribe_Values.Look(ref Normal, "normal");
                Scribe_Values.Look(ref Below, "below"); Scribe_Values.Look(ref Floor, "floor");
                Scribe_Values.Look(ref Final, "final");
            }
        }
        private sealed class TerrainData : IExposable
        {
            public TerrainData() { }
            internal HashSet<IntVec3> Pending = new(), Processed = new(), TerrainOnly = new();
            public void ExposeData()
            {
                Scribe_Collections.Look(ref Pending, "pending", LookMode.Value);
                Scribe_Collections.Look(ref Processed, "processed", LookMode.Value);
                Scribe_Collections.Look(ref TerrainOnly, "terrainOnly", LookMode.Value);
                Pending ??= new(); Processed ??= new(); TerrainOnly ??= new();
            }
        }
        private static readonly ConditionalWeakTable<ProjectileCE, ContactData> contacts = new();
        private static readonly ConditionalWeakTable<ExplosionCE, TerrainData> targets = new();
        [ThreadStatic] private static ProjectileCE source;
        private static readonly Action<DamageWorker, Explosion, IntVec3> damageTerrain = AccessTools.MethodDelegate<Action<DamageWorker, Explosion, IntVec3>>(
            AccessTools.Method(typeof(DamageWorker), "ExplosionDamageTerrain"));
        private static readonly AccessTools.FieldRef<Explosion, List<IntVec3>> affectCells =
            AccessTools.FieldRefAccess<Explosion, List<IntVec3>>("cellsToAffect");
        private static readonly Func<Explosion, IntVec3, int> affectTick = AccessTools.MethodDelegate<Func<Explosion, IntVec3, int>>(
            AccessTools.Method(typeof(Explosion), "GetCellAffectTick"));

        internal static IDisposable Register(string owner, ISurfaceExplosionRule rule)
        {
            if (string.IsNullOrEmpty(owner) || rule == null) throw new ArgumentException("Surface rule requires owner and policy.");
            if (rules.Any(x => x.Owner == owner)) throw new InvalidOperationException("Surface rule already registered: " + owner);
            var entry = new Rule { Owner = owner, Policy = rule }; rules.Add(entry); return entry;
        }
        internal static void BeginStep(ProjectileCE p)
        { if (contacts.TryGetValue(p, out var value) && (!value.Final || !p.landed)) contacts.Remove(p); }

        internal static void Contact(ProjectileCE p, Layout l, int upper, Vector3 point, int frame, bool below, bool final)
        {
            if (!Ready) return;
            Vector3 v = l.ToVirtual(point, frame);
            v.y = upper * ProjectZ.Api.StoreyHeight;
            var g = new IntVec3(Mathf.FloorToInt(v.x), 0, Mathf.FloorToInt(v.z));
            bool floor = ProjectZ.Api.HasFloorAt(g, upper, p.Map);
            var data = new ContactData { Map = p.Map, Ground = g, Upper = upper,
                Surface = l.Cell(g, floor ? upper : upper - 1), Incident = l.Cell(g, below ? upper - 1 : upper),
                Point = v, Normal = below ? Vector3.down : Vector3.up, Below = below, Floor = floor, Final = final };
            contacts.Remove(p); contacts.Add(p, data);
        }

        internal static void Patch(Harmony h)
        {
            foreach (Type t in GenTypes.AllTypes.Where(t => !t.ContainsGenericParameters && typeof(ProjectileCE).IsAssignableFrom(t)))
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t).Where(m => !m.IsStatic && !m.IsAbstract &&
                    (m.Name == "Impact" || m.Name == "Tick" || m.Name == "RayCast")))
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(SurfaceExplosions), nameof(SourcePrefix)) { priority = Priority.First });
                    PackZ.Applied++;
                    if (m.Name == "Impact" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Thing))
                        PackZ.Prefix(h, m, typeof(SurfaceExplosions), nameof(ImpactContactPrefix));
                    h.Patch(m, finalizer: new HarmonyMethod(typeof(SurfaceExplosions), nameof(SourceFinalizer)));
                }
            foreach (Type t in new[] { typeof(CompExplosiveCE), typeof(CompExplosive), typeof(AmmoThing), typeof(CompAbilityEffect_ExplosionCE) })
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t).Where(m => m.Name == "Explode" || m.Name == "Detonate" || m.Name == "TryDetonate" || m.Name == "Apply"))
                {
                    PackZ.Prefix(h, m, typeof(SurfaceExplosions), nameof(ComponentPrefix));
                    h.Patch(m, finalizer: new HarmonyMethod(typeof(SurfaceExplosions), nameof(SourceFinalizer)));
                }
            PackZ.Prefix(h, PackZ.Method(typeof(GenExplosionCE), nameof(GenExplosionCE.DoExplosion)), typeof(SurfaceExplosions), nameof(OriginPrefix));
            PackZ.Prefix(h, PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.StartExplosionCE)), typeof(SurfaceExplosions), nameof(StartPrefix));
            PackZ.Postfix(h, PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.StartExplosionCE)), typeof(SurfaceExplosions), nameof(StartPostfix));
            PackZ.Postfix(h, PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.MergeWith)), typeof(SurfaceExplosions), nameof(MergePostfix));
            PackZ.Prefix(h, PackZ.Method(typeof(Explosion), "AffectCell"), typeof(SurfaceExplosions), nameof(AffectPrefix));
            PackZ.Postfix(h, PackZ.Method(typeof(Explosion), "AffectCell"), typeof(SurfaceExplosions), nameof(AffectPostfix));
            PackZ.Postfix(h, PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.ExposeData)), typeof(SurfaceExplosions), nameof(ProjectileSave));
            PackZ.Postfix(h, PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.ExposeData)), typeof(SurfaceExplosions), nameof(ExplosionSave));
        }
        private static void SourcePrefix(ProjectileCE __instance, out ProjectileCE __state)
        {
            __state = source; source = __instance;
        }
        private static void ImpactContactPrefix(ProjectileCE __instance, Thing __0)
        {
            if (!contacts.TryGetValue(__instance, out var contact) || contact.Final) return;
            // A descending plane was only a proposal until native CE accepted its ground impact.
            Vector3 exact = Frames.exactGet(__instance);
            bool planeHeight = contact.Below ? exact.y >= CollisionVertical.WallCollisionHeight - 0.01f : exact.y <= 0.01f;
            if (__0 != null || !__instance.landed || !planeHeight)
                contacts.Remove(__instance);
            else
            {
                contact.Final = true;
                Layout l = Layout.Of(contact.Map);
                // Native CE can meet the roof's bounds just before the geometric plane itself.
                // Preserve that exact contact, and resolve its actual column for terrain ownership.
                contact.Point = l.ToVirtual(exact, l.FrameOf(exact));
                IntVec3 g = new(Mathf.FloorToInt(contact.Point.x), 0, Mathf.FloorToInt(contact.Point.z));
                contact.Ground = g;
                contact.Floor = ProjectZ.Api.HasFloorAt(g, contact.Upper, contact.Map);
                contact.Surface = l.Cell(g, contact.Floor ? contact.Upper : contact.Upper - 1);
                contact.Incident = l.Cell(g, contact.Below ? contact.Upper - 1 : contact.Upper);
            }
        }
        private static void ComponentPrefix(object __instance, out ProjectileCE __state)
        { __state = source; source = (__instance as ThingComp)?.parent as ProjectileCE; }
        private static void SourceFinalizer(ProjectileCE __state) => source = __state;
        private static bool SourceContact(out ContactData data)
        {
            data = null;
            return Ready && source != null && contacts.TryGetValue(source, out data) && data.Final;
        }
        private static void OriginPrefix(ref IntVec3 center, Map map, ref float height)
        {
            if (!SourceContact(out var contact) || contact.Map != map) return;
            center = contact.Incident;
            if (contact.Below) height = MathF.BitDecrement(CollisionVertical.WallCollisionHeight);
        }
        private static void StartPrefix(ExplosionCE __instance)
        {
            if (targets.TryGetValue(__instance, out var previous)) previous.TerrainOnly.Clear();
            if (!SourceContact(out var contact) || contact.Map != __instance.Map || contact.Incident != __instance.Position) return;
            var context = new SurfaceExplosionContext(__instance, contact.Contact, source.def);
            if (!rules.Any(r => r.Policy.ShouldDamageTerrain(context))) return;
            var data = targets.GetOrCreateValue(__instance);
            if (!data.Processed.Contains(contact.Surface)) data.Pending.Add(contact.Surface);
        }
        private static void StartPostfix(ExplosionCE __instance)
        {
            if (!__instance.Spawned || !targets.TryGetValue(__instance, out var data) || data.Pending.Count == 0) return;
            List<IntVec3> queue = affectCells(__instance);
            foreach (IntVec3 cell in data.Pending)
            {
                if (data.Processed.Contains(cell) || queue.Contains(cell)) continue;
                // Add after CE's neighbour expansion and ExplosionStart. The extra surface must never
                // seed ordinary upper-floor neighbours, flames or a DamageWorker's start effects.
                data.TerrainOnly.Add(cell);
                int tick = affectTick(__instance, cell);
                int at = queue.Count;
                while (at > 0 && affectTick(__instance, queue[at - 1]) < tick) at--;
                queue.Insert(at, cell);
            }
        }
        private static bool SlabCell(IntVec3 cell, Map map)
        {
            Layout l = Layout.Of(map);
            if (l == null || !cell.InBounds(map)) return false;
            int k = l.FrameOf(cell);
            if (k == Layout.None || k <= l.min || !ProjectZ.Api.HasFloorAt(l.Ground(cell), k, map)) return false;
            return map.terrainGrid.TopTerrainAt(cell).destroyOnBombDamageThreshold >= 0f ||
                map.terrainGrid.UnderTerrainAt(cell)?.destroyOnBombDamageThreshold >= 0f;
        }
        private static bool AffectPrefix(Explosion __instance, IntVec3 c, out bool __state)
        {
            __state = false;
            if (__instance is not ExplosionCE ce || !targets.TryGetValue(ce, out var data) || !data.Pending.Contains(c)) return true;
            if (!data.TerrainOnly.Contains(c)) { __state = true; return true; }
            data.Pending.Remove(c); data.Processed.Add(c);
            // Missing terrain remains a skipped terrain target, never an ordinary cell affecting occupants.
            if (SlabCell(c, ce.Map)) damageTerrain(ce.damType.Worker, ce, c);
            return false;
        }
        private static void AffectPostfix(Explosion __instance, IntVec3 c, bool __state)
        {
            if (!__state || __instance is not ExplosionCE ce || !targets.TryGetValue(ce, out var data)) return;
            data.Pending.Remove(c); data.Processed.Add(c);
        }
        private static void MergePostfix(ExplosionCE __instance, ExplosionCE other, ExplosionCE merged, bool __result)
        {
            if (!__result) return;
            foreach (ExplosionCE from in new[] { __instance, other })
            {
                if (!targets.TryGetValue(from, out var data) || from == merged) continue;
                var into = targets.GetOrCreateValue(merged);
                into.Pending.UnionWith(data.Pending); into.Processed.UnionWith(data.Processed);
                into.TerrainOnly.UnionWith(data.TerrainOnly);
                targets.Remove(from);
            }
            if (targets.TryGetValue(merged, out var combined)) combined.Pending.ExceptWith(combined.Processed);
        }
        private static void ProjectileSave(ProjectileCE __instance)
        {
            contacts.TryGetValue(__instance, out var data);
            Scribe_Deep.Look(ref data, "cepackSurfaceContact");
            if (data != null) { contacts.Remove(__instance); contacts.Add(__instance, data); }
        }
        private static void ExplosionSave(ExplosionCE __instance)
        {
            targets.TryGetValue(__instance, out var data);
            Scribe_Deep.Look(ref data, "cepackSurfaceTerrain");
            if (data != null) { targets.Remove(__instance); targets.Add(__instance, data); }
        }
    }
}
