using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatExtended.Lasers;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // CE's laser beams (LaserBeamCE fires instantly; LaserBeamGraphicCE is the beam seen for its lifetime) in the 3D view.
    //
    // CE draws the beam as one flat strip at a fixed altitude from its start a to its end b (CE's points: x, z the map rows,
    // y the height in CE units), so in 3D it lay on the ground. Here the same strip (CE's mesh, material, colour, fading and
    // flicker, all as CE runs them) runs through the air from the muzzle to the point hit at its true height, turned about
    // its length to face the camera, as the VE Framework pack draws VEF's beams:
    //  - start: IsoRim's muzzle (Api.Muzzle: the drawn barrel end, a turret's gun) when the beam leaves the shooter (CE starts
    //    it at its own shot point), else CE's start (a shield's reflections). End: CE's end at its height.
    //  - heights: CE units to cells as the projectiles (ProjectileCE_DrawAt.CellsPerUnit).
    //  - stacked floors (IsoRimZ): the pack's IsoRimZ part fires a laser between floors in the frame of the storey the beam
    //    ends in, so CE's two points are in one floor's band (the start's height over that floor can be above or below it).
    //    The strip is laid in that band (never over the void rows between bands) and drawn onto its floor; when that floor
    //    is hidden, over the highest shown floor below (the same 3D place, as overhead shells). A beam whose thing sits on a
    //    hidden floor's rows (the game culls it there) is drawn from the DrawDynamicThings sweep.
    // With the view off CE draws as usual.
    public static class Beams
    {
        public static bool Enabled = true;
        public static int Strips, Bolts, FromMuzzle, Swept, Hidden;
        public static Vector3 LastStart, LastEnd;   // the last strip drawn (true space over the floor it was drawn on)

        private sealed class Info
        {
            public bool muzzle;     // start at the muzzle below
            public Vector3 start;   // true space over the shooter's floor (its rows, height over it)
            public int drawnFrame = -1;
        }

        private static readonly ConditionalWeakTable<LaserBeamGraphicCE, Info> infos = new ConditionalWeakTable<LaserBeamGraphicCE, Info>();
        private static readonly List<LaserBeamGraphicCE> live = new List<LaserBeamGraphicCE>();

        private static readonly AccessTools.FieldRef<LaserBeamGraphicCE, Vector3> aRef = AccessTools.FieldRefAccess<LaserBeamGraphicCE, Vector3>("a");
        private static readonly AccessTools.FieldRef<LaserBeamGraphicCE, Vector3> bRef = AccessTools.FieldRefAccess<LaserBeamGraphicCE, Vector3>("b");

        [ThreadStatic] private static LaserBeamCE spawning;
        [ThreadStatic] private static LaserBeamGraphicCE drawing;

        // ---- LaserBeamCE.SpawnBeam: the projectile making the beam (its shooter and gun)
        public static void SpawnBeam_Prefix(LaserBeamCE __instance) => spawning = __instance;

        public static void SpawnBeam_Finalizer() => spawning = null;

        // ---- LaserBeamGraphicCE.Setup: where the beam leaves the shooter as drawn
        public static void Setup_Postfix(LaserBeamGraphicCE __instance, Thing launcher, Vector3 origin, Vector3 destination)
        {
            Rand.PushState();   // (runs in the tick: nothing here may draw the game's random numbers)
            try
            {
                Info info = infos.GetValue(__instance, _ => new Info());
                live.Add(__instance);
                LaserBeamCE p = spawning;
                if (!Enabled || !IsoRim.Api.ViewReady || launcher == null || !launcher.Spawned || p == null)
                {
                    return;
                }
                Map map = launcher.Map;
                Thing owner = ProjectileCE_DrawAt.MuzzleOwnerFor(launcher, p.equipmentDef, launcher.Position, p.Map);
                if (owner == null)
                {
                    return;
                }
                // Does the beam start at the shooter? (CE's start, in the end's band, against the shooter's column)
                Vector3 own = owner.DrawPos;
                Vector2 flat = new Vector2(origin.x - own.x, FloorMath.GroundZ(map, origin.z) - FloorMath.GroundZ(map, own.z));
                if (flat.magnitude > 2.5f)
                {
                    return;
                }
                Vector3 dir = new Vector3(destination.x - own.x, 0f, FloorMath.GroundZ(map, destination.z) - FloorMath.GroundZ(map, own.z));
                dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
                info.start = IsoRim.Api.Muzzle(owner, p.equipmentDef, owner is Building_Turret ? owner.DrawPos : launcher.DrawPos, dir);
                info.muzzle = true;
                FromMuzzle++;
                if (Harness.On)
                {
                    Log.Message(Pack.Tag + "beam " + __instance.def.defName + " by " + launcher.LabelShort + ": CE " + origin.ToString("F2") + " -> " + destination.ToString("F2")
                        + ", muzzle " + info.start.ToString("F2") + " (CE start height " + (origin.y * ProjectileCE_DrawAt.CellsPerUnit).ToString("0.00") + " cells)");
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Finding a CE beam's muzzle failed (drawn from CE's start): " + e, 74120321);
            }
            finally
            {
                Rand.PopState();
            }
        }

        // ---- LaserBeamGraphicCE.DrawAt: CE's drawing runs, its two Graphics.DrawMesh calls redirected here
        public static void DrawAt_Prefix(LaserBeamGraphicCE __instance)
        {
            drawing = Enabled && IsoRim.Api.ViewReady ? __instance : null;
            if (drawing != null)
            {
                infos.GetValue(__instance, _ => new Info()).drawnFrame = Time.frameCount;
            }
        }

        public static void DrawAt_Finalizer() => drawing = null;

        public static IEnumerable<CodeInstruction> DrawAt_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo strip = AccessTools.Method(typeof(Beams), nameof(DrawStrip));
            MethodInfo bolt = AccessTools.Method(typeof(Beams), nameof(DrawBolt));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo m && m.Name == "DrawMesh" && m.DeclaringType != null && m.DeclaringType.Name == "Graphics")
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 7 && ps[1].ParameterType == typeof(Matrix4x4))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = strip;
                        found++;
                    }
                    else if (ps.Length == 9 && ps[1].ParameterType == typeof(Vector3))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = bolt;
                        found++;
                    }
                }
                yield return ci;
            }
            if (found != 2)
            {
                Log.Warning(Pack.Tag + "CE's beam drawing (LaserBeamGraphicCE.DrawAt) has " + found + " of its 2 draws where expected: the others stay flat.");
            }
        }

        // CE: Graphics.DrawMesh(mesh, drawingMatrix, materialBeam, 0, null, 0, MatPropertyBlock) for a straight beam.
        public static void DrawStrip(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties)
        {
            LaserBeamGraphicCE g = drawing;
            if (g == null)
            {
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties);
                return;
            }
            if (!Ends(g, out Vector3 s, out Vector3 e))
            {
                Hidden++;
                return;
            }
            float width = ((Vector3)matrix.GetColumn(0)).magnitude;   // CE's beam width
            Vector3 axis = e - s;
            float length = axis.magnitude;
            if (length < 1e-4f)
            {
                return;
            }
            Vector3 dir = axis / length;
            Matrix4x4 m = Matrix4x4.Scale(new Vector3(1f, 1f / IsoRim.Api.HeightSquash, 1f)) * Matrix4x4.TRS((s + e) * 0.5f, Facing(dir), new Vector3(width, 1f, length));
            Graphics.DrawMesh(mesh, IsoRim.Api.DrawnMatrix(m), material, layer, camera, submeshIndex, properties);
            Strips++;
            LastStart = s;
            LastEnd = e;
        }

        // CE: Graphics.DrawMesh(boltMesh, b, LookRotation(b - a), materialBeam, 0, null, 0, MatPropertyBlock, no shadows) for
        // each arc of a lightning beam (the bolt runs back from b toward a).
        public static void DrawBolt(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties,
            UnityEngine.Rendering.ShadowCastingMode castShadows)
        {
            LaserBeamGraphicCE g = drawing;
            if (g == null)
            {
                Graphics.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex, properties, castShadows);
                return;
            }
            if (!Ends(g, out Vector3 s, out Vector3 e))
            {
                Hidden++;
                return;
            }
            Vector3 a = aRef(g), b = bRef(g);
            float ceLength = Vector3.Distance(a, b);
            Vector3 axis = e - s;
            float length = axis.magnitude;
            if (length < 1e-4f || ceLength < 1e-4f)
            {
                return;
            }
            Matrix4x4 m = Matrix4x4.Scale(new Vector3(1f, 1f / IsoRim.Api.HeightSquash, 1f)) * Matrix4x4.TRS(e, Facing(axis / length), new Vector3(1f, 1f, length / ceLength));
            Graphics.DrawMesh(mesh, IsoRim.Api.DrawnMatrix(m), material, layer, camera, submeshIndex, properties);
            Bolts++;
        }

        // Along dir, its face (the mesh's up) turned toward the camera.
        private static Quaternion Facing(Vector3 dir)
        {
            Vector3 toCamera = -(FloorMath.View * Vector3.forward);
            Vector3 up = toCamera - dir * Vector3.Dot(toCamera, dir);
            if (up.sqrMagnitude < 1e-6f)
            {
                up = Vector3.up - dir * Vector3.Dot(Vector3.up, dir);
            }
            return Quaternion.LookRotation(dir, up.normalized);
        }

        // The beam's two ends in true space over the floor it is drawn on (that floor's rows); false when nothing of it shows.
        private static bool Ends(LaserBeamGraphicCE g, out Vector3 s, out Vector3 e)
        {
            Map map = g.Map;
            float k = ProjectileCE_DrawAt.CellsPerUnit;
            Vector3 a = aRef(g), b = bRef(g);
            e = new Vector3(b.x, b.y * k, b.z);
            s = new Vector3(a.x, a.y * k, a.z);
            int floor = FloorMath.FloorOfRow(map, b.z, out _, out _);
            if (floor < 0)
            {
                return false;
            }
            Info info = infos.GetValue(g, _ => new Info());
            if (info.muzzle)
            {
                int from = FloorMath.FloorOfRow(map, info.start.z, out _, out _);
                if (from >= 0)
                {
                    s = FloorMath.Move(map, info.start, from, floor);
                }
            }
            int shown = FloorMath.ShownFloor(map, floor);
            if (shown < 0)
            {
                return false;
            }
            s = FloorMath.Move(map, s, floor, shown);
            e = FloorMath.Move(map, e, floor, shown);
            return true;
        }

        // Beams the game did not draw this frame (their thing on a hidden floor's rows, or culled): drawn here.
        public static void Sweep(Map map)
        {
            if (live.Count == 0)
            {
                return;
            }
            int frame = Time.frameCount;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                LaserBeamGraphicCE g = live[i];
                if (g == null || g.Destroyed || !g.Spawned)
                {
                    live.RemoveAt(i);
                    continue;
                }
                if (!Enabled || !IsoRim.Api.ViewReady || g.Map != map || infos.GetValue(g, _ => new Info()).drawnFrame == frame)
                {
                    continue;
                }
                try
                {
                    ((Thing)g).DrawNowAt(((Thing)g).DrawPos);
                    Swept++;
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(Pack.Tag + "Drawing a CE beam from the sweep failed: " + e, 74120322);
                }
            }
        }
    }

    [HarmonyPatch(typeof(LaserBeamCE), "SpawnBeam")]
    public static class LaserBeamCE_SpawnBeam
    {
        public static void Prefix(LaserBeamCE __instance) => Beams.SpawnBeam_Prefix(__instance);

        public static Exception Finalizer(Exception __exception)
        {
            Beams.SpawnBeam_Finalizer();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(LaserBeamGraphicCE), "Setup")]
    public static class LaserBeamGraphicCE_Setup
    {
        public static void Postfix(LaserBeamGraphicCE __instance, Thing launcher, Vector3 origin, Vector3 destination) => Beams.Setup_Postfix(__instance, launcher, origin, destination);
    }

    [HarmonyPatch(typeof(LaserBeamGraphicCE), "DrawAt")]
    public static class LaserBeamGraphicCE_DrawAt
    {
        public static void Prefix(LaserBeamGraphicCE __instance) => Beams.DrawAt_Prefix(__instance);

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Beams.DrawAt_Transpiler(instructions);

        public static Exception Finalizer(Exception __exception)
        {
            Beams.DrawAt_Finalizer();
            return __exception;
        }
    }
}
