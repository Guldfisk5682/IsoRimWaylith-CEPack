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
    // A drop pod (any skyfaller with contents) shot down by a CIWS between floors. CE drops the contents at the cell under
    // the pod's flat draw position (CompCIWSImpactHandler_Skyfaller.OnDestroying: DrawPos.ToIntVec3()); that point is
    // lifted up the screen for the pod's height, 10 to 100 cells away from the pod while it is high. On a map with floors
    // the raw cell lies in another floor's band (or the void rows), so the contents came down on an unrelated column of
    // another storey. Here the drop cell is the column under the pod's real position (CE's own Skyfaller_Utility
    // .CurrentPosition: x, z of its flight), on the pod's own storey (the band of its Position), kept in the ground rect;
    // where nothing valid is found the pod's own cell (one log line). A map without floors: CE's cell, unchanged.
    internal static class PodDrops
    {
        private static readonly Func<Skyfaller, Vector3> skyPos = AccessTools.MethodDelegate<Func<Skyfaller, Vector3>>(
            AccessTools.Method(typeof(Skyfaller_Utility), nameof(Skyfaller_Utility.CurrentPosition), new[] { typeof(Skyfaller) }));

        public static int Moved, Fallbacks;
        public static IntVec3 Last = IntVec3.Invalid;   // harness: the drop cell given last

        public static void Patch(Harmony h)
        {
            PackZ.Transpiler(h, PackZ.Method(typeof(CompCIWSImpactHandler_Skyfaller), "OnDestroying"), typeof(PodDrops), nameof(Transpiler));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo toCell = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.ToIntVec3), new[] { typeof(Vector3) });
            MethodInfo mine = AccessTools.Method(typeof(PodDrops), nameof(DropCell));
            int found = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(toCell))
                {
                    // (the draw position is on the stack) -> DropCell(drawPos, this)
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, mine);
                    found++;
                    continue;
                }
                yield return ci;
            }
            if (found != 1)
            {
                throw new InvalidOperationException("CompCIWSImpactHandler_Skyfaller.OnDestroying: " + found + " ToIntVec3 calls (expected 1)");
            }
        }

        public static IntVec3 DropCell(Vector3 drawPos, CompCIWSImpactHandler_Skyfaller comp)
        {
            IntVec3 raw = drawPos.ToIntVec3();
            Thing pod = comp?.parent;
            Map map = pod?.Map;
            Layout L = Layout.Of(map);
            if (L == null)
            {
                return raw;
            }
            int k = L.FrameOf(pod.Position);
            Vector3 real = pod is Skyfaller s ? skyPos(s) : pod.Position.ToVector3Shifted();
            IntVec3 c = IntVec3.Invalid;
            if (k != Layout.None)
            {
                IntVec3 g = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(real.x), 0, L.width - 1), 0, Mathf.Clamp(Mathf.FloorToInt(real.z) - L.Shift(k), 0, L.rows - 1));
                c = L.Cell(g, k);
            }
            if (!c.IsValid || !c.InBounds(map))
            {
                Fallbacks++;
                PackZ.Once("poddrop", "A shot-down drop pod's contents had no cell under the pod on its floor: dropped at the pod's own cell.");
                c = pod.Position;
            }
            if (c != raw)
            {
                PackZ.Changed++;
                Moved++;
            }
            Last = c;
            return c;
        }
    }
}
