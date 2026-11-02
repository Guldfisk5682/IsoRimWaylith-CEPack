using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPackZ
{
    // Test harness only (mode "popup", -cepacktest=popup): popping up to fire (the pack's Posture.cs). A crouched colonist
    // firing a CE gun is drawn raised as far as CE raises its shot (the verb's ShotHeight, CE's own value), back to the full
    // crouch when it stops aiming. Real flow: drafted colonists ordered to fire at a held raider (no weapon), real CE jobs,
    // stances, bursts and launches, real ticks; each check its own fixture and seed (the runner's). Renders: each case close
    // up with the pop-up on and off (as before), two camera turns. A far hostile is held awake for the whole mode, so the
    // drafted-idle crouch is drawn once the firing stops.
    public static partial class TestZ
    {
        private static IEnumerable<IEnumerator<int>> PopupChecks()
        {
            // (-cepopup=1,5: only those checks; each keeps its own seed)
            Func<IEnumerator<int>>[] all =
            {
                () => PopCase(1, "sandbags in the line of fire", 160, 10, "Sandbags", false, Rifle, 0),
                () => PopCase(2, "low wall (CE embrasure) in the line of fire", 185, 10, "CE_Embrasure", false, Rifle, 0),
                () => PopCase(3, "barricade in the line of fire", 210, 10, "Barricade", false, Rifle, 0),
                () => PopCase(4, "sandbags beside (not in the line of fire)", 160, 50, "Sandbags", true, Rifle, 0),
                () => PopCase(5, "no cover", 185, 50, null, false, Rifle, 0),
                () => PopCase(6, "upstairs behind sandbags, target on the ground", 160, 120, "Sandbags", false, Rifle, 1),
                () => PopCase(7, "burst weapon (heavy SMG) behind sandbags", 210, 50, "Sandbags", false, "Gun_HeavySMG", 0),
                () => PopCase(8, "bolt-action rifle behind sandbags", 160, 85, "Sandbags", false, "Gun_BoltActionRifle", 0),
            };
            string[] only = GenCommandLine.TryGetCommandLineArg("cepopup", out string sel) && sel != null ? sel.Split(',') : null;
            FarThreat();
            for (int i = 0; i < all.Length; i++)
            {
                yield return only == null || only.Contains((i + 1).ToString()) ? all[i]() : Nothing();
            }
        }

        // ------------------------------------------------------------------ the pack's Posture.cs (the Z DLL does not reference
        // the pack's: reflection)
        private static Type postureType;

        private static Type PostureT => postureType ??= AccessTools.TypeByName("IsoRimCEPack.Posture");

        private static bool PopOn
        {
            set { AccessTools.Field(PostureT, "PopOn").SetValue(null, value); }
        }

        private static string PopReport(Pawn p) => (string)AccessTools.Method(PostureT, "PopReport").Invoke(null, new object[] { p });

        private static float PopShot(Pawn p) => (float)AccessTools.Method(PostureT, "PopShotHeight").Invoke(null, new object[] { p });

        // The drawn start of a CE shot as the pack's projectile drawing has it (ProjectileCE_DrawAt.Flight: the muzzle it read
        // when the shot was first drawn, against CE's launch point): true if the shot has been drawn.
        private static object flightTable;
        private static System.Reflection.MethodInfo flightGet;
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector2> popOrigin = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("origin");

        private static readonly Func<Pawn, Vector2> popBody =
            AccessTools.MethodDelegate<Func<Pawn, Vector2>>(AccessTools.Method(typeof(CE_Utility), nameof(CE_Utility.GetCollisionBodyFactors)));

        private static bool DrawnStart(ProjectileCE p, out Vector3 start)
        {
            start = Vector3.zero;
            if (flightTable == null)
            {
                flightTable = AccessTools.Field(AccessTools.TypeByName("IsoRimCEPack.ProjectileCE_DrawAt"), "flights").GetValue(null);
                flightGet = flightTable.GetType().GetMethod("TryGetValue");
            }
            object[] args = { p, null };
            if (!(bool)flightGet.Invoke(flightTable, args) || args[1] == null)
            {
                return false;
            }
            Traverse f = Traverse.Create(args[1]);
            Vector3 lead = f.Field("lead").GetValue<Vector3>();
            float lift = f.Field("lift").GetValue<float>();
            Vector2 o = popOrigin(p);
            start = new Vector3(o.x + lead.x, p.shotHeight * Layout.CellsPerUnit + lift, o.y + lead.z);
            return true;
        }

        // What IsoRim draws a cover's top at, cells (its model, or IsoRim's low wall top).
        private static float CoverTop(string def) => def == "Sandbags" ? 0.516f : def == "Barricade" ? 0.55f : def == "CE_Embrasure" ? 0.76f : 0f;

        private static float groundRise = -1f;   // U1: the muzzle's rise over the full crouch behind sandbags

        private static float MuzzleY(Pawn p) => IsoRim.Api.TryGetMuzzle(p.equipment.Primary, p, out Vector3 m) ? m.y : -1f;

        // ------------------------------------------------------------------ the checks
        // One case: a drafted colonist behind (or beside, or without) cover, ordered to fire at a raider 14 cells north.
        private static IEnumerator<int> PopCase(int n, string label, int x0, int z0, string cover, bool beside, string weapon, int floor)
        {
            string id = "u" + n;
            Out("--- U" + n + " " + label + " (" + weapon + ")");
            if (floor > 0 && (L == null || !L.Has(floor)))
            {
                Check("U" + n + " " + label, false, "needs a map with floors");
                yield break;
            }
            Wipe(x0, z0, x0 + 20, z0 + 32);
            int sx = x0 + 10, sz = z0 + 6;
            if (floor > 0)
            {
                Slab(sx - 4, sz - 4, sx + 4, sz + 4, floor);
            }
            List<Thing> covers = new List<Thing>();
            if (cover != null)
            {
                IEnumerable<IntVec3> cells = beside ? new[] { new IntVec3(sx + 1, 0, sz), new IntVec3(sx + 1, 0, sz - 1) }
                    : new[] { new IntVec3(sx - 1, 0, sz + 1), new IntVec3(sx, 0, sz + 1), new IntVec3(sx + 1, 0, sz + 1) };
                foreach (IntVec3 c in cells)
                {
                    covers.Add(Build(cover, On(c.x, c.z, floor)));
                }
            }
            Pawn target = Raider(On(sx, sz + 14, 0));
            Armour(target);
            Pawn p = Soldier(On(sx, sz, floor), weapon);
            p.drafter.FireAtWill = false;   // (idle until ordered: the idle crouch measured first)
            float lift = floor > 0 ? floor * ProjectZ.Api.StoreyHeight : 0f;
            if (floor > 0)
            {
                ProjectZ.Api.SetViewedFloor(floor);
            }
            Find.UIRoot.screenshotMode.Active = true;
            Find.CameraDriver.SetRootPosAndSize(new IntVec3(sx, 0, sz).ToVector3Shifted(), 3f);   // (drawn: the posture is worked out as drawn)
            yield return 60;
            for (int i = 0; i < 300 && p.CurJobDef != JobDefOf.Wait_Combat; i++)
            {
                yield return 1;   // (CE reloads a fresh gun first)
            }
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            float idleCrouch;
            Drawn(p, out idleCrouch, out _);
            Out("  idle before: " + Posture(p));
            Out("  can hit: " + CanHit(p, target));
            int t0 = Find.TickManager.TicksGame;
            Order(p, target);
            HashSet<int> seen = new HashSet<int>();
            List<string> starts = new List<string>();
            float worstStart = 0f;
            int drawnShots = 0, firstLaunch = -1, lastLaunch = -1;
            float burstMin = 2f, burstMax = -1f;
            float shotAtLaunch = -1f, ceAtLaunch = -1f;
            int watch = weapon == "Gun_BoltActionRifle" ? 420 : 240;
            for (int i = 0; i < 400 + watch; i++)
            {
                Unhurt(target);
                Unhurt(p);
                yield return 1;
                List<Shot> mine = By(p, Launches, t0);
                if (mine.Count > 0 && firstLaunch < 0)
                {
                    firstLaunch = Find.TickManager.TicksGame;
                }
                if (mine.Count > 0)
                {
                    lastLaunch = Math.Max(lastLaunch, mine.Max(s => s.tick));
                }
                // the drawn crouch between the first and the last shot seen so far (a burst's gaps)
                Drawn(p, out float dcNow, out _);
                if (firstLaunch >= 0 && Find.TickManager.TicksGame <= lastLaunch + 1)
                {
                    burstMin = Mathf.Min(burstMin, dcNow);
                    burstMax = Mathf.Max(burstMax, dcNow);
                }
                foreach (Thing t in Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
                {
                    if (t is ProjectileCE pr && pr.launcher == p && !seen.Contains(pr.thingIDNumber) && DrawnStart(pr, out Vector3 st))
                    {
                        seen.Add(pr.thingIDNumber);
                        drawnShots++;
                        IsoRim.Api.TryGetMuzzle(p.equipment.Primary, p, out Vector3 mz);
                        float d = Mathf.Abs(st.y - mz.y);
                        float flat = new Vector2(st.x - mz.x, st.z - mz.z).magnitude;
                        worstStart = Mathf.Max(worstStart, Mathf.Max(d, flat));
                        if (shotAtLaunch < 0f)
                        {
                            shotAtLaunch = pr.shotHeight;
                            ceAtLaunch = PopShot(p);
                        }
                        if (starts.Count < 6)
                        {
                            starts.Add("drawn start y " + st.y.ToString("0.000") + " muzzle y " + mz.y.ToString("0.000") + " (flat " + flat.ToString("0.000") + "), CE launch " + pr.shotHeight.ToString("0.000") + " units = "
                                + (pr.shotHeight * Layout.CellsPerUnit).ToString("0.000") + " cells");
                        }
                    }
                }
                if (firstLaunch >= 0 && Find.TickManager.TicksGame - firstLaunch >= watch)
                {
                    break;
                }
            }
            Out("  launches " + By(p, Launches, t0).Count + " (first at +" + (firstLaunch - t0) + ", last at +" + (lastLaunch - t0) + "), drawn shots " + drawnShots);
            foreach (string s in starts)
            {
                Out("    " + s);
            }
            // aiming now (the next shot's warmup or the cooldown): the numbers, then the close-ups with the pop-up on and off
            for (int i = 0; i < 200 && !AimingNow(p); i++)
            {
                Unhurt(target);
                yield return 1;
            }
            foreach (int w in Wait(20))
            {
                yield return w;
            }
            bool aiming = AimingNow(p);
            string report = PopReport(p);
            float ce = PopShot(p);
            Drawn(p, out float crouch, out _);
            float muzzle = MuzzleY(p);
            IsoRim.Api.PostureOf(p, out float rule, out _);
            foreach (int turn in new[] { 2, 1 })
            {
                foreach (int w in Snap(id + "_t" + turn + "_after", new IntVec3(sx, 0, sz), 3.5f, turn, lift: lift))
                {
                    yield return w;
                }
            }
            PopOn = false;
            foreach (int w in Wait(30))
            {
                yield return w;
            }
            Drawn(p, out float crouchOff, out _);
            float muzzleOff = MuzzleY(p);
            foreach (int turn in new[] { 2, 1 })
            {
                foreach (int w in Snap(id + "_t" + turn + "_before", new IntVec3(sx, 0, sz), 3.5f, turn, lift: lift))
                {
                    yield return w;
                }
            }
            PopOn = true;
            IsoRim.Api.Dev.PosturesOn = false;
            foreach (int w in Wait(30))
            {
                yield return w;
            }
            float muzzleStand = MuzzleY(p);
            IsoRim.Api.Dev.PosturesOn = true;
            foreach (int w in Wait(30))
            {
                yield return w;
            }
            float body = popBody(p).y, bottom = new CollisionVertical(p).Min;
            float low = CollisionVertical.BodyRegionBottomHeight * body + 0.01f, high = CollisionVertical.BodyRegionMiddleHeight * body;
            float want = Mathf.Clamp01((ce - bottom - low) / (high - low));
            want = want < 0.005f ? 1f : 1f - want;
            float top = CoverTop(cover);
            if (n == 1)
            {
                groundRise = muzzle - muzzleOff;
            }
            Out("  aiming " + aiming + ": " + report);
            Out("  " + Posture(p));
            Out("  NUMBERS U" + n + " " + label + ": CE shot " + ce.ToString("0.000") + " units (" + (ce * Layout.CellsPerUnit).ToString("0.000") + " cells mapped), crouch " + crouch.ToString("0.000")
                + " (rule " + rule.ToString("0.000") + ", expected " + want.ToString("0.000") + "), drawn muzzle " + muzzle.ToString("0.000") + " cells; before (full crouch " + crouchOff.ToString("0.000") + ") "
                + muzzleOff.ToString("0.000") + "; standing " + muzzleStand.ToString("0.000") + "; drawn cover top " + (cover != null ? top.ToString("0.000") : "-")
                + (cover != null ? ", muzzle over it by " + (muzzle - top).ToString("0.000") + " (before " + (muzzleOff - top).ToString("0.000") + ")" : ""));
            Check("U" + n + "a " + label + ": aiming, the crouch CE's shot height asks for", aiming && Mathf.Abs(crouch - want) < 0.01f && Mathf.Abs(crouchOff - 1f) < 0.001f,
                "aiming " + aiming + ", crouch " + crouch.ToString("0.000") + " want " + want.ToString("0.000") + ", pop-up off " + crouchOff.ToString("0.000"));
            if (cover == null)
            {
                Check("U" + n + "b no cover: no visible change (the full crouch, the muzzle where it was)", want == 1f && Mathf.Abs(muzzle - muzzleOff) < 0.005f,
                    "want " + want.ToString("0.000") + ", muzzle " + muzzle.ToString("0.000") + " vs " + muzzleOff.ToString("0.000"));
            }
            else
            {
                Check("U" + n + "b raised: CE raises the shot (" + (beside ? "cover beside: CE's height takes all 8 cells around" : "cover in front") + "), the muzzle rises", want < 1f && muzzle > muzzleOff + 0.02f && muzzle <= muzzleStand + 0.005f,
                    "muzzle " + muzzleOff.ToString("0.000") + " -> " + muzzle.ToString("0.000") + " (standing " + muzzleStand.ToString("0.000") + ")");
                if (floor > 0)
                {
                    // (the muzzle height upstairs reads in that floor's frame: compared with the same pose on the ground, U1)
                    Check("U" + n + "c the same rise over the full crouch as on the ground (U1)", groundRise > 0f && Mathf.Abs((muzzle - muzzleOff) - groundRise) < 0.005f,
                        "rise " + (muzzle - muzzleOff).ToString("0.000") + ", on the ground " + groundRise.ToString("0.000") + "; standing here " + muzzleStand.ToString("0.000"));
                }
                else if (!beside && cover != "CE_Embrasure")
                {
                    Check("U" + n + "c the muzzle over the drawn cover top", muzzle > top, "muzzle " + muzzle.ToString("0.000") + ", cover top " + top.ToString("0.000") + " (before " + muzzleOff.ToString("0.000") + ")");
                }
            }
            Check("U" + n + "d the drawn shots start at the drawn muzzle (CE launches unchanged: CE's own height)", drawnShots > 0 && worstStart < 0.03f && Mathf.Abs(shotAtLaunch - ceAtLaunch) < 0.001f,
                "drawn shots " + drawnShots + ", worst start-muzzle " + worstStart.ToString("0.000") + ", CE launch " + shotAtLaunch.ToString("0.000") + " vs read " + ceAtLaunch.ToString("0.000"));
            if (weapon == "Gun_HeavySMG" || weapon == Rifle)
            {
                Check("U" + n + "e no dip between shots (first to last launch watched)", burstMax >= 0f && burstMax - burstMin < 0.02f,
                    "drawn crouch " + burstMin.ToString("0.000") + ".." + burstMax.ToString("0.000"));
            }
            else
            {
                Out("  bolt-action drawn crouch first to last shot " + burstMin.ToString("0.000") + ".." + burstMax.ToString("0.000"));
            }
            // the firing stops (the raider gone): eased back to the full crouch (drafted idle, the far hostile awake)
            Gone(target);
            int stopTick = Find.TickManager.TicksGame;
            int backAt = -1;
            List<string> ease = new List<string>();
            float minRule = 2f;
            for (int i = 0; i < 120; i++)
            {
                yield return 1;
                Drawn(p, out float dc, out _);
                IsoRim.Api.PostureOf(p, out float rc, out _);
                minRule = Mathf.Min(minRule, rc);
                if (i < 30 && i % 3 == 0)
                {
                    ease.Add("+" + (i + 1) + " rule " + rc.ToString("0.00") + " drawn " + dc.ToString("0.00") + (i == 0 ? " (job " + (p.CurJobDef?.defName ?? "none") + ", stance " + p.stances.curStance?.GetType().Name + ")" : ""));
                }
                if (backAt < 0 && dc >= 0.999f && !AimingNow(p))
                {
                    backAt = Find.TickManager.TicksGame - stopTick;
                    break;
                }
            }
            Out("  stop: " + string.Join(", ", ease) + "; " + Posture(p));
            Check("U" + n + "f firing stopped: back to the full crouch, eased", backAt > 0 && backAt <= 60 && (want >= 1f || backAt > 2) && minRule > 0f, "back after " + backAt + " ticks, lowest rule on the way " + minRule.ToString("0.000") + " (0: a flicker up), idle crouch before firing " + idleCrouch.ToString("0.00"));
            if (floor > 0)
            {
                ProjectZ.Api.SetViewedFloor(0);
            }
            Gone(p);
            Gone(covers.ToArray());
            foreach (int w in Settle(100))
            {
                yield return w;
            }
        }
    }
}
