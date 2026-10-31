using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // Stacked floors as IsoRim lays them out (IsoRim.Api.Floors: an IsoRimZ layout; none without it). A point here is in true
    // space over the floor of its row: x, z the game's rows of that floor's band, y the height over that floor (cells).
    // Floors are stacked translations, so the same 3D point can be written in any floor's band (Move).
    internal static class FloorMath
    {
        // The floor whose band holds row z: 0 on the ground (and without floors), -1 for a void row or past the top band.
        public static int FloorOfRow(Map map, float z, out float height, out int shift)
        {
            height = 0f;
            shift = 0;
            IsoRim.IFloorLayout floors = IsoRim.Api.Floors;
            int rows = floors != null && map != null ? floors.GroundRows(map) : 0;
            if (rows <= 0 || z < rows)
            {
                return 0;
            }
            int n = floors.FloorCount(map);
            for (int k = 1; k <= n; k++)
            {
                floors.Floor(map, k, out float hk, out int sk);
                if (z >= sk && z < sk + rows)
                {
                    height = hk;
                    shift = sk;
                    return k;
                }
            }
            return -1;
        }

        // Floor k's height and band shift (0, 0 for the ground); false for a floor the map does not have.
        public static bool FloorAt(Map map, int k, out float height, out int shift, out bool shown)
        {
            height = 0f;
            shift = 0;
            shown = true;
            if (k == 0)
            {
                return true;
            }
            IsoRim.IFloorLayout floors = IsoRim.Api.Floors;
            if (floors == null || map == null || floors.GroundRows(map) <= 0 || k < 0 || k > floors.FloorCount(map))
            {
                return false;
            }
            shown = floors.Floor(map, k, out height, out shift);
            return true;
        }

        // A point of floor 'from' (its band's rows, height over it) written over floor 'to'.
        public static Vector3 Move(Map map, Vector3 p, int from, int to)
        {
            if (from == to || from < 0 || to < 0)
            {
                return p;
            }
            FloorAt(map, from, out float hf, out int sf, out _);
            FloorAt(map, to, out float ht, out int st, out _);
            return new Vector3(p.x, p.y + hf - ht, p.z - sf + st);
        }

        // The floor to draw something of floor k on: k when it is shown, else the highest shown floor below it (the same 3D
        // place drawn over that floor: in the open air above what is shown). -1: nothing shown below.
        public static int ShownFloor(Map map, int k)
        {
            for (int j = k; j >= 0; j--)
            {
                if (FloorAt(map, j, out _, out _, out bool shown) && shown)
                {
                    return j;
                }
            }
            return -1;
        }

        // The ground row of a point (its band's rows taken back to the ground's).
        public static float GroundZ(Map map, float z)
        {
            int k = FloorOfRow(map, z, out _, out int shift);
            return k > 0 ? z - shift : z;
        }

        // The camera's turn and tilt as one rotation (true space).
        public static Quaternion View => Quaternion.Euler(IsoRim.Api.ViewPitch, IsoRim.Api.ViewYaw, 0f);
    }
}
