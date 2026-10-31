using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using CombatExtended.Utilities;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Areas between floors (plan §2c, decisions D4, D5): Combat Extended's explosions and the pawns they suppress follow
    // IsoRimZ's explosion rule, one mechanism for both: the blast is a sphere at its real height (CE's explosion height,
    // the frame-local height Frames keeps), its cells on its own floor are CE's, and the other floors' cells are those
    // ProjectZ's picker (Api 6 ExplosionCells3D) reaches in 3D with a clear line (slabs, roofs and walls block; a
    // stairwell's opening or open air lets it through). So:
    //  - ExplosionCE.ExplosionCellsToHit (every CE explosion: shells, grenades, rockets, IEDs, cook-offs, bombardments):
    //    CE's cells kept on the centre's floor (any raw cell CE's radius ran into another band dropped), the other floors'
    //    cells added. ExplosionCE.Tick then affects them through the game's AffectCell, whose distances ProjectZ already
    //    measures in 3D (ExplosionDistance_Patch); CE's own damage and armour falloff (GetDamageAmountAtCE,
    //    GetArmorPenetrationAtCE) measure the 3D distance too (a transpiler, the same swap).
    //  - Suppression by a blast (ProjectileCE.Impact, ProjectileCE_Explosive's fuse): CE's pawns in range on the centre's
    //    floor, plus the pawns on the other floors' cells the same picker reaches at the suppression range. Suppressing
    //    a pawn on another floor measures CE's falloff and the shot's origin in that pawn's frame (ApplySuppression).
    //    Bullets suppress the pawns of the storey they pass through: that falls out of Frames (D4), nothing here.
    //  - Radius loops that only look around a cell (no height): kept on that cell's floor with ProjectZ's radius clip
    //    (Api 6 ClipRadialLoops): CE's danger map (DangerTracker.Notify_DangerRadiusAt), the cover search of a suppressed
    //    pawn (SuppressionUtility.GetCoverPositionFrom), shield zones for the AI (SuppressionUtility.InterceptorZonesFor),
    //    a bullet's neighbour notice (BulletCE.NotifyImpact).
    // On a map without floors every replacement returns CE's own value (one map lookup).
    internal static class Areas
    {
        // Harness counters.
        public static int BlastCellsAdded, BlastCellsDropped, BlastFalloff3D, SuppressedAcross, SuppressLeaksDropped, ClipsApplied;

        private static readonly List<IntVec3> cells = new List<IntVec3>();
        private static readonly IntVec3[] none = new IntVec3[0];

        public static void Patch(Harmony h)
        {
            PackZ.Postfix(h, AccessTools.PropertyGetter(typeof(ExplosionCE), nameof(ExplosionCE.ExplosionCellsToHit)) ?? throw new MissingMethodException("ExplosionCE.ExplosionCellsToHit"),
                typeof(Areas), nameof(Cells_Postfix));
            PackZ.Transpiler(h, PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.GetDamageAmountAtCE)), typeof(Areas), nameof(Falloff_Transpiler));
            PackZ.Transpiler(h, PackZ.Method(typeof(ExplosionCE), nameof(ExplosionCE.GetArmorPenetrationAtCE)), typeof(Areas), nameof(Falloff_Transpiler));
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE), nameof(ProjectileCE.Impact)), typeof(Areas), nameof(Suppress_Transpiler));
            Fault.Throw("areasgroup");   // harness: the group fails partway (its first four patches are undone)
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE_Explosive), nameof(ProjectileCE_Explosive.Tick)), typeof(Areas), nameof(Suppress_Transpiler));
            PackZ.Transpiler(h, PackZ.Method(typeof(ProjectileCE), "ApplySuppression"), typeof(Areas), nameof(ApplySuppression_Transpiler));
        }

        // ProjectZ's radius clip on CE's radius loops (not key: without it CE's loops stay as they are).
        public static void PatchClips()
        {
            Clip(AccessTools.Method(typeof(DangerTracker), nameof(DangerTracker.Notify_DangerRadiusAt)), "this");
            Clip(AccessTools.Method(typeof(SuppressionUtility), "GetCoverPositionFrom"), "arg:pawn");
            Clip(AccessTools.Method(typeof(BulletCE), "NotifyImpact"), "arg:map");
            MethodInfo zones = AccessTools.Method(typeof(SuppressionUtility), nameof(SuppressionUtility.InterceptorZonesFor));
            Clip(zones != null ? AccessTools.EnumeratorMoveNext(zones) : null, "this.pawn");
        }

        private static void Clip(MethodBase m, string mapPath)
        {
            if (m == null)
            {
                Log.Warning(PackZ.Tag + "radius clip: a CE method was not found (" + mapPath + ")");
                return;
            }
            if (ProjectZ.Api.ClipRadialLoops(m, mapPath))
            {
                ClipsApplied++;
            }
            else
            {
                Log.Warning(PackZ.Tag + "radius clip not applied to " + m.DeclaringType?.Name + "." + m.Name);
            }
        }

        // ------------------------------------------------------------------ explosion cells
        // CE's cells on the centre's floor plus ProjectZ's cells on the other floors, from the explosion's own height.
        public static void Cells_Postfix(ExplosionCE __instance, ref IEnumerable<IntVec3> __result)
        {
            Map map = __instance.Map;
            Layout L = Layout.Of(map);
            if (L == null || __result == null)
            {
                return;
            }
            int kc = L.FrameOf(__instance.Position);
            if (kc == Layout.None)
            {
                return;
            }
            List<IntVec3> same = __result.ToList();
            List<IntVec3> into = new List<IntVec3>(same.Count + 16);
            ProjectZ.Api.ExplosionCells3D(__instance.Position, map, __instance.radius, same, into, Mathf.Max(0f, __instance.height) * Layout.CellsPerUnit);
            int kept = 0;
            foreach (IntVec3 c in same)
            {
                if (L.FrameOf(c) == kc)
                {
                    kept++;
                }
            }
            if (kept != same.Count || into.Count != kept)
            {
                PackZ.Changed++;
                BlastCellsDropped += same.Count - kept;
                BlastCellsAdded += into.Count - kept;
            }
            __result = into;
        }

        // CE's falloff distance from the explosion to a cell: 3D between floors (ProjectZ Api Distance3D).
        public static IEnumerable<CodeInstruction> Falloff_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo dist = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
            MethodInfo mine = AccessTools.Method(typeof(Areas), nameof(BlastDistance));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(dist))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, mine);
                    found++;
                    continue;
                }
                yield return ci;
            }
            if (found == 0)
            {
                throw new InvalidOperationException("ExplosionCE." + __originalMethod.Name + ": no DistanceTo call");
            }
        }

        public static float BlastDistance(IntVec3 a, IntVec3 b, ExplosionCE e)
        {
            Map map = e?.Map;
            Layout L = Layout.Of(map);
            if (L != null)
            {
                int ka = L.FrameOf(a), kb = L.FrameOf(b);
                if (ka != kb && ka != Layout.None && kb != Layout.None)
                {
                    PackZ.Changed++;
                    BlastFalloff3D++;
                    return ProjectZ.Api.Distance3D(a, b, map);
                }
            }
            return a.DistanceTo(b);
        }

        // ------------------------------------------------------------------ suppression by a blast
        public static IEnumerable<CodeInstruction> Suppress_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo inRange = AccessTools.Method(typeof(CombatExtended.Utilities.GenClosest), nameof(CombatExtended.Utilities.GenClosest.PawnsInRange), new[] { typeof(IntVec3), typeof(Map), typeof(float) });
            MethodInfo mine = AccessTools.Method(typeof(Areas), nameof(PawnsInRange3D));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(inRange))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, mine);
                    found++;
                    continue;
                }
                yield return ci;
            }
            if (found == 0)
            {
                throw new InvalidOperationException(__originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + ": no PawnsInRange call");
            }
        }

        // CE's pawns within range of a blast at cell, on the blast's floor; plus the pawns on the other floors' cells the
        // blast reaches at that range (the explosion picker from the projectile's height).
        public static IEnumerable<Pawn> PawnsInRange3D(IntVec3 cell, Map map, float range, ProjectileCE p)
        {
            IEnumerable<Pawn> ce = cell.PawnsInRange(map, range);
            Layout L = Layout.Of(map);
            int kc = L != null ? L.FrameOf(cell) : Layout.None;
            if (kc == Layout.None)
            {
                return ce;
            }
            List<Pawn> res = new List<Pawn>();
            bool changed = false;
            foreach (Pawn x in ce)
            {
                if (x != null && L.FrameOf(x.Position) != kc)
                {
                    SuppressLeaksDropped++;
                    changed = true;
                    continue;
                }
                res.Add(x);
            }
            cells.Clear();
            float h = p != null ? Mathf.Max(0f, Frames.exactGet(p).y) * Layout.CellsPerUnit : -1f;
            ProjectZ.Api.ExplosionCells3D(cell, map, range, none, cells, h);
            foreach (IntVec3 c in cells)
            {
                List<Thing> list = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Pawn pawn && !res.Contains(pawn))
                    {
                        res.Add(pawn);
                        SuppressedAcross++;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                PackZ.Changed++;
            }
            return res;
        }

        // ApplySuppression for a pawn on another floor: CE's falloff from the blast measured with the pawn seen in the
        // projectile's frame, and the shot's origin (where the pawn takes cover from) seen in the pawn's frame.
        public static IEnumerable<CodeInstruction> ApplySuppression_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo origin = AccessTools.Field(typeof(ProjectileCE), nameof(ProjectileCE.OriginIV3));
            MethodInfo drawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
            MethodInfo originFor = AccessTools.Method(typeof(Areas), nameof(OriginFor));
            MethodInfo drawPosFor = AccessTools.Method(typeof(Areas), nameof(DrawPosFor));
            int o = 0, d = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldfld && ci.operand is FieldInfo f && f == origin)
                {
                    // (the projectile is on the stack) -> OriginFor(projectile, pawn)
                    yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, originFor);
                    o++;
                    continue;
                }
                if (ci.Calls(drawPos))
                {
                    // (the thing is on the stack) -> DrawPosFor(thing, projectile)
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, drawPosFor);
                    d++;
                    continue;
                }
                yield return ci;
            }
            if (o == 0 || d == 0)
            {
                throw new InvalidOperationException("ProjectileCE.ApplySuppression: " + o + " origin loads, " + d + " DrawPos calls");
            }
        }

        public static IntVec3 OriginFor(ProjectileCE p, Pawn pawn)
        {
            IntVec3 o = p.OriginIV3;
            Layout L = pawn != null ? Layout.Of(p.Map) : null;
            if (L == null)
            {
                return o;
            }
            int kp = L.FrameOf(Frames.exactGet(p)), kw = L.FrameOf(pawn.Position);
            if (kp == kw || kp == Layout.None || kw == Layout.None)
            {
                return o;
            }
            PackZ.Changed++;
            return new IntVec3(o.x, 0, o.z + L.Shift(kw) - L.Shift(kp));
        }

        public static Vector3 DrawPosFor(Thing t, ProjectileCE p)
        {
            Vector3 v = t.DrawPos;
            Layout L = Layout.Of(p.Map);
            if (L == null)
            {
                return v;
            }
            int kp = L.FrameOf(Frames.exactGet(p)), kt = L.FrameOf(t.Position);
            if (kp == kt || kp == Layout.None || kt == Layout.None)
            {
                return v;
            }
            PackZ.Changed++;
            return L.TranslateRows(v, kt, kp);
        }
    }
}
