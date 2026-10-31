using System.Collections.Generic;
using CombatExtended;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // A projectile seen from another storey's frame for the length of one call into code that is not the pack's (another
    // mod's shield or CIWS target): its positional fields are moved into frame `to` (the same pure translation as
    // Frames.Rebase, over any number of storeys: rows by the bands' shift, heights by the storeys between), the foreign
    // code then compares it with its own things in raw rows and local heights as if both were in one storey, and the
    // fields are moved back. Position follows through CE's ExactPosition setter, so the thing grid stays right whatever
    // the foreign code does meanwhile (it may move or destroy the projectile).
    internal static class FrameView
    {
        private static readonly AccessTools.FieldRef<ProjectileCE, List<Vector3>> predictedRef = AccessTools.FieldRefAccess<ProjectileCE, List<Vector3>>("cachedPredictedPositions");
        private static readonly AccessTools.FieldRef<ProjectileCE, int> shotLineTickRef = AccessTools.FieldRefAccess<ProjectileCE, int>("lastShotLine");

        public static int Moves;

        // Moves p's fields from frame `from` into frame `to` (no-op for one frame or a destroyed projectile).
        public static void Move(ProjectileCE p, Layout L, int from, int to)
        {
            if (from == to || p == null || p.Destroyed || !L.Has(from) || !L.Has(to))
            {
                return;
            }
            Moves++;
            float dz = L.Shift(to) - L.Shift(from);
            float dy = -(to - from) * Layout.S;
            Frames.lastPosRef(p) = Frames.lastPosRef(p) + new Vector3(0f, dy, dz);
            Vector2 o = Frames.originRef(p);
            Frames.originRef(p) = new Vector2(o.x, o.y + dz);
            Vector2 d = Frames.destRef(p);
            Frames.destRef(p) = new Vector2(d.x, d.y + dz);
            p.OriginIV3 = new IntVec3(p.OriginIV3.x, 0, p.OriginIV3.z + (int)dz);
            p.shotHeight += dy;
            p.intendedTargetHeight += dy;
            predictedRef(p) = null;
            shotLineTickRef(p) = -1;
            Frames.exactSet(p, Frames.exactGet(p) + new Vector3(0f, dy, dz));
        }
    }
}
