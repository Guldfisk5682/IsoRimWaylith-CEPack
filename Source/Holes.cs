using System;
using System.Collections.Generic;
using CombatExtended;
using HarmonyLib;
using ProjectileImpactFX;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // CE's bullet holes on a building it hit, drawn on the wall's face at the impact height (the 3D view only).
    //
    // CE (ImpactFleckThrower.ThrowFleck -> TriggerBulletHole) makes a Fleck_BulletHole at the hit building's DrawPos plus a
    // random shift, a flat point of its cell: in 3D it lies on the ground under the wall. The fleck is still made as always
    // (the same random numbers, the same simulation), only with no colour (Puffs.Unseen); the one drawn here stands in for
    // it, flat on the camera-facing side of the wall: the face the shot came in by when the camera sees it, else the face
    // most toward the camera (one an adjoining full wall does not cover), at the height the shot hit (CE's height mapped as
    // the shots are: ProjectileCE_DrawAt.CellsPerUnit), along the face where it hit. Texture, colour, size, turn and fade
    // in / solid / fade out are the fleck def's and CE's. Nothing here draws random numbers or touches thing IDs.
    // Holes on the ground (hard floors) are left as CE makes them: flat is right there. The view off: CE's holes as usual.
    [StaticConstructorOnStartup]   // (a MaterialPropertyBlock static: made on the main thread at startup)
    public static class Holes
    {
        public static bool Enabled = true;
        public static int Made, Drawn;
        private const int Max = 600;

        private struct Hole
        {
            public FleckDef def;
            public Map map;
            public IntVec3 cell;      // the hit building's cell (its floor's rows)
            public Vector3 impact;    // where the shot hit: x, z its flat point, y the height over its floor (cells)
            public Vector3 jitter;    // CE's random shift of the hole from the building's middle (flat)
            public float top;         // the building's CE height mapped (cells): the hole stays under it
            public float scale, rotation, born, solid;
            public Color color;
        }

        private static readonly List<Hole> holes = new List<Hole>();
        private static Map holeMap;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static float Now => Find.TickManager.TicksGame / 60f;

        // ImpactFleckThrower.ThrowFleck(loc, position, map, projProps, def, hitThing, direction): the building hit, and where.
        [ThreadStatic] private static Building hitting;
        [ThreadStatic] private static Vector3 hitAt;

        public static void ThrowFleck_Prefix(object[] __args)
        {
            hitting = null;
            if (!Enabled || !IsoRim.Api.ViewReady || __args == null || __args.Length < 6 || !(__args[5] is Building b) || !b.Spawned)
            {
                return;
            }
            hitAt = (Vector3)__args[0];
            hitting = b;
        }

        public static void ThrowFleck_Finalizer() => hitting = null;

        // FleckManager.CreateFleck (Puffs' prefix): CE's bullet hole on the building being hit is drawn here instead.
        public static bool CreateFleck(ref FleckCreationData data, Color unseen)
        {
            Building b = hitting;
            if (b == null || data.def == null || data.def != CE_FleckDefOf.Fleck_BulletHole || b.Map == null)
            {
                return false;
            }
            try
            {
                Map map = b.Map;
                if (map != holeMap)
                {
                    holes.Clear();
                    holeMap = map;
                }
                if (holes.Count >= Max)
                {
                    holes.RemoveAt(0);
                }
                float perUnit = ProjectileCE_DrawAt.CellsPerUnit;
                float top = new CollisionVertical(b).Max * perUnit;
                Vector3 mid = b.DrawPos;
                holes.Add(new Hole
                {
                    def = data.def,
                    map = map,
                    cell = b.Position,
                    impact = new Vector3(hitAt.x, Mathf.Max(0f, hitAt.y) * perUnit, hitAt.z),
                    jitter = new Vector3(data.spawnPosition.x - mid.x, 0f, data.spawnPosition.z - mid.z),
                    top = top > 0.05f ? top : IsoRim.Api.WallHeight,
                    scale = data.scale,
                    rotation = data.rotation,
                    born = Now,
                    solid = data.solidTimeOverride ?? data.def.solidTime,
                    color = data.instanceColor ?? Color.white
                });
                Made++;
                data.instanceColor = unseen;
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Putting a CE bullet hole on the wall failed (left as CE makes it): " + e, 74120341);
                return false;
            }
        }

        private static readonly IntVec3[] Faces = { IntVec3.South, IntVec3.East, IntVec3.North, IntVec3.West };

        // The face of the hole's building to draw it on (a cardinal direction), or invalid when none is open to the camera.
        private static IntVec3 FaceFor(Hole h, Vector3 forward)
        {
            Vector3 c = h.cell.ToVector3Shifted();
            Vector3 d = new Vector3(h.impact.x - c.x, 0f, h.impact.z - c.z);
            IntVec3 entered = Mathf.Abs(d.x) > Mathf.Abs(d.z) ? (d.x > 0f ? IntVec3.East : IntVec3.West) : (d.z > 0f ? IntVec3.North : IntVec3.South);
            if (Seen(h, entered, forward))
            {
                return entered;
            }
            IntVec3 best = IntVec3.Invalid;
            float bestDot = 0f;
            for (int i = 0; i < Faces.Length; i++)
            {
                float dot = Faces[i].x * forward.x + Faces[i].z * forward.z;
                if (dot < bestDot - 0.001f && Seen(h, Faces[i], forward))
                {
                    best = Faces[i];
                    bestDot = dot;
                }
            }
            return best;
        }

        // A face toward the camera and not covered by a full-height building beside it.
        private static bool Seen(Hole h, IntVec3 n, Vector3 forward)
        {
            if (n.x * forward.x + n.z * forward.z > -0.05f)
            {
                return false;
            }
            IntVec3 next = h.cell + n;
            if (!next.InBounds(h.map))
            {
                return true;
            }
            Building e = next.GetEdifice(h.map);
            return e == null || e.def.Fillage != FillCategory.Full || e is Building_Door;
        }

        // Drawn every frame with the map's dynamic things (DynamicDrawManager.DrawDynamicThings postfix, Sweep).
        public static void Draw(Map map)
        {
            if (holes.Count == 0 || map != holeMap)
            {
                return;
            }
            float now = Now;
            float squash = IsoRim.Api.HeightSquash;
            bool view = Enabled && IsoRim.Api.ViewReady;
            Vector3 forward = FloorMath.View * Vector3.forward;
            for (int i = holes.Count - 1; i >= 0; i--)
            {
                Hole h = holes[i];
                FleckDef def = h.def;
                float age = now - h.born;
                float life = def.fadeInTime + h.solid + def.fadeOutTime;
                Building b = h.cell.InBounds(map) ? h.cell.GetEdifice(map) : null;
                if (age > life || age < -1f || b == null)
                {
                    holes.RemoveAt(i);   // (gone with the wall)
                    continue;
                }
                if (!view || IsoRim.Api.WallCutAway(map, h.cell))
                {
                    continue;
                }
                float alpha = age <= def.fadeInTime ? (def.fadeInTime > 0f ? age / def.fadeInTime : 1f)
                    : age <= def.fadeInTime + h.solid ? 1f
                    : def.fadeOutTime > 0f ? 1f - Mathf.InverseLerp(def.fadeInTime + h.solid, life, age) : 1f;
                if (alpha <= 0f || def.graphicData == null)
                {
                    continue;
                }
                IntVec3 n = FaceFor(h, forward);
                if (!n.IsValid)
                {
                    continue;
                }
                Graphic g = def.graphicData.Graphic;
                Material mat = g?.MatSingle;
                if (mat == null)
                {
                    continue;
                }
                Vector2 ds = def.graphicData.drawSize;
                float sx = Mathf.Max(0.0001f, Mathf.Abs(h.scale)) * ds.x;
                float sz = Mathf.Max(0.0001f, Mathf.Abs(h.scale)) * ds.y;
                Vector3 normal = new Vector3(n.x, 0f, n.z);
                Vector3 along = new Vector3(-n.z, 0f, n.x);   // the face's horizontal direction
                Vector3 c = h.cell.ToVector3Shifted();
                // along the face: where the shot hit on the face it came in by, else CE's random place on this one
                Vector3 entry = h.impact - c;
                bool cameIn = Vector3.Dot(new Vector3(entry.x, 0f, entry.z), normal) > 0.3f;
                float side = Vector3.Dot(cameIn ? new Vector3(entry.x, 0f, entry.z) : h.jitter, along);
                float room = Mathf.Max(0f, 0.5f - sx * 0.35f);
                side = Mathf.Clamp(side, -room, room);
                float hMin = Mathf.Min(sz * 0.35f, h.top * 0.5f);
                float y = Mathf.Clamp(h.impact.y, hMin, Mathf.Max(hMin, h.top - sz * 0.35f));
                Vector3 at = c + normal * 0.515f + along * side;
                at.y = y;
                if (!IsoRim.Api.FloorShown(at))
                {
                    continue;
                }
                Color col = g.Color * h.color;
                col.a *= alpha;
                block.SetColor(ShaderPropertyIDs.Color, col);
                // the fleck's quad (x across, z up its texture, y its normal) laid flat on the face
                Matrix4x4 onFace = Matrix4x4.identity;
                onFace.SetColumn(0, new Vector4(along.x, along.y, along.z, 0f));
                onFace.SetColumn(1, new Vector4(normal.x, normal.y, normal.z, 0f));
                onFace.SetColumn(2, new Vector4(0f, 1f, 0f, 0f));
                Matrix4x4 m = Matrix4x4.Translate(new Vector3(at.x, at.y / squash, at.z))
                    * Matrix4x4.Scale(new Vector3(1f, 1f / squash, 1f))
                    * onFace
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(h.rotation, Vector3.up))
                    * Matrix4x4.Scale(new Vector3(sx, 1f, sz));
                Graphics.DrawMesh(MeshPool.plane10, IsoRim.Api.DrawnMatrix(m), mat, 0, null, 0, block);
                Drawn++;
            }
        }

        public static int Live => holes.Count;
    }

    [HarmonyPatch(typeof(ImpactFleckThrower), nameof(ImpactFleckThrower.ThrowFleck))]
    public static class ImpactFleckThrower_ThrowFleck
    {
        public static void Prefix(object[] __args) => Holes.ThrowFleck_Prefix(__args);

        public static Exception Finalizer(Exception __exception)
        {
            Holes.ThrowFleck_Finalizer();
            return __exception;
        }
    }
}
