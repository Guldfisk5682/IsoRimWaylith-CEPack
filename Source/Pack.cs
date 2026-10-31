using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // IsoRim Waylith - Combat Extended Pack (needs IsoRim Api 20; older: nothing is patched, CE draws as usual).
    //
    // CE's projectiles are ProjectileCE : ThingWithComps, not the game's Projectile, so IsoRim's own projectile drawing never
    // reaches them. CE draws each one as a flat sprite shifted up the screen for its height (TrajectoryWorker.ExactPosToDrawPos)
    // plus a dark copy of the sprite on the ground as its shadow; in the 3D view both lie near the ground.
    //
    // One central hook: a prefix on ProjectileCE.DrawAt (the only DrawAt in CE's projectile classes besides LaserBeamCE's,
    // which draws nothing). With the 3D view on it draws the projectile the way IsoRim draws the game's own projectiles
    // (IsoRim Projectiles.DrawAt / DrawLobbed) and skips CE's drawing; with the view off CE draws as usual.
    //  - Where: CE's real 3D flight, ExactPosition (x, z on the map, y the height in CE units), read every frame, so CE's
    //    ballistics, homing and smart rockets are followed as they are.
    //  - From the muzzle: CE launches from its own shot point and height; the drawing starts at IsoRim's muzzle
    //    (Api.Muzzle: the drawn barrel end, a turret's gun, a thrown thing's hand) and the difference (flat and in height)
    //    fades out along the way to what was aimed at, as IsoRim fades its lead. Only the drawing moves.
    //  - Direct fire (bullets, rockets, thrown grenades): one card lying flat at its height, its nose along the flight
    //    (spinning if the def spins), as IsoRim's plain projectile cards (not pitched: IsoRim does not pitch them either).
    //  - Indirect fire (flyOverhead: mortar and howitzer shells): one card facing the camera, its nose along the flight as
    //    seen on screen, as IsoRim's lobbed shells (SkyStrikes.Card / ScreenSpin, rebuilt here from Api.ViewPitch / ViewYaw).
    //  - Shadow: CE's dark sprite copy is dropped. IsoRim draws the game's round projectile shadow only for defs with a
    //    shadowSize, on the ground under the shot, shrinking with height; the same here.
    //  - Stacked floors (IsoRimZ): the point is worked out over the projectile's own floor (its rows) and placed with
    //    Api.DrawnMatrix (onto that floor; nothing on a hidden one, Api.FloorShown), as IsoRim places vanilla shots. The pack's
    //    IsoRimZ part moves a projectile into the band of the storey its height is in (its local height runs 0..one storey), so
    //    the drawn height is that floor's height plus the local one: seamless when it changes storey. An overhead shell on a
    //    hidden floor (high over the viewed one) is drawn over the highest shown floor below at its true height, from a
    //    DynamicDrawManager.DrawDynamicThings postfix (the game culls things of hidden floors).
    //
    // Height mapping. CE measures heights in its own units: a full wall (any Fillage.Full building) is
    // CollisionVertical.WallCollisionHeight = 2 units high, a cover building its fillPercent, a standing human about 1.75
    // (CE's body bounds), shots fired at about 85% of the shooter's height. IsoRim draws a full wall Api.WallHeight cells
    // high (1.2 by default). A CE height is drawn at  ce * Api.WallHeight / CollisionVertical.WallCollisionHeight  cells
    // (0.6 at the default wall height), so a shot passing over a wall or sandbag in CE's flight passes over it on screen
    // too. Pawns come out close with IsoRim's figures at the default wall height (a human's chest, CE ~1.2, at 0.72 of a
    // 1.25-cell figure); IsoRim does not scale figures with the wall height option, so a very high wall setting lifts the
    // shots relative to the figures.
    [StaticConstructorOnStartup]
    public static class Pack
    {
        internal const string Tag = "[IsoRim CE Pack] ";

        static Pack()
        {
            try
            {
                if (IsoRim.Api.Version < 20)
                {
                    Log.Message(Tag + "IsoRim is older than Api 20: Combat Extended's projectiles stay as CE draws them.");
                    return;
                }
                // the drawing classes' CE members (resolved in their type initializers) first: a CE update that renamed
                // one stops the drawing here, at startup, before anything is patched (CE then draws as usual)
                foreach (Type t in new[] { typeof(ProjectileCE_DrawAt), typeof(HeldGuns), typeof(Beams), typeof(Puffs), typeof(Holes), typeof(Grenades) })
                {
                    RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                }
                Harmony h = new Harmony("peren.isorimwaylith.cepack");
                h.PatchAll(typeof(Pack).Assembly);
                Grenades.MarkThrown();
            }
            catch (Exception e)
            {
                Exception inner = e;
                while (inner.InnerException != null)
                {
                    inner = inner.InnerException;
                }
                Log.Error(Tag + "Setup failed (CE's projectiles stay as CE draws them): " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        // Harness only: -cepackfault=draw simulates a renamed CE member in the drawing.
        internal static string FaultName(string real) =>
            GenCommandLine.TryGetCommandLineArg("cepackfault", out string f) && f != null && Array.IndexOf(f.Split(','), "draw") >= 0 ? real + "_cepackfault" : real;
    }

    [HarmonyPatch(typeof(ProjectileCE), "DrawAt")]
    public static class ProjectileCE_DrawAt
    {
        // How the drawn flight differs from CE's at the start (where CE launched vs the muzzle), faded out along the way.
        private sealed class Flight
        {
            public Vector3 lead;     // flat: from CE's launch point to the muzzle
            public float lift;       // cells: muzzle height minus CE's launch height (mapped)
            public float fade;       // cells (flat) over which lead and lift fade out: the way to what was aimed at
            public int drawnFrame = -1;
        }

        private static readonly ConditionalWeakTable<ProjectileCE, Flight> flights = new ConditionalWeakTable<ProjectileCE, Flight>();

        private static readonly Action<ThingWithComps> postDraw =
            AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));

        // The game's round projectile shadow (Verse.Projectile.shadowMaterial), as IsoRim draws for vanilla shells.
        private static readonly AccessTools.FieldRef<Material> shadowMat =
            AccessTools.StaticFieldRefAccess<Material>(AccessTools.Field(typeof(Projectile), "shadowMaterial"));

        // CE members typed with Unity types are read through Harmony accessors: CombatExtended.dll is built against Unity
        // (UnityEngine.CoreModule), this pack against Waylith (UnityStubs); the game maps CE onto Waylith when it loads it.
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector2> originRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>(Pack.FaultName("origin"));
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector2> destinationRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("Destination");
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector3> lastPosRef = AccessTools.FieldRefAccess<ProjectileCE, Vector3>("LastPos");
        private static readonly Func<ProjectileCE, Vector3> exactPosition =
            AccessTools.MethodDelegate<Func<ProjectileCE, Vector3>>(AccessTools.PropertyGetter(typeof(ProjectileCE), "ExactPosition"));

        // CE height units to IsoRim cells (see the header).
        public static float CellsPerUnit => IsoRim.Api.WallHeight / CollisionVertical.WallCollisionHeight;

        // Harness counters, and the harness switch (Harness: drawing off shows CE's own drawing).
        public static int Drawn, DrawnLobbed, Errors, Hidden, Lowered;
        public static bool Enabled = true;

        public static bool Prefix(ProjectileCE __instance)
        {
            ProjectileCE p = __instance;
            try
            {
                if (!Enabled || !IsoRim.Api.ViewReady || p == null || p.def?.graphicData == null || p.Map == null)
                {
                    return true;
                }
                // (CE: not drawn on its first tick when a pawn launched it; kept)
                if (p.FlightTicks == 0 && p.launcher is Pawn)
                {
                    return false;
                }
                Draw(p);
                return false;
            }
            catch (Exception e)
            {
                Errors++;
                Log.ErrorOnce(Pack.Tag + "Drawing a CE projectile failed (drawn as CE draws it): " + e, 74120301);
                return true;
            }
        }

        // Overhead shells the game did not draw this frame: a shell on a hidden upper floor's rows (IsoRimZ moves a CE
        // projectile into the band of the storey its height is in; the game culls things of hidden floors) is drawn from
        // here over the highest shown floor (ShownBelow). A postfix on the map's DynamicDrawManager.DrawDynamicThings.
        private static readonly AccessTools.FieldRef<DynamicDrawManager, Map> drawMap = AccessTools.FieldRefAccess<DynamicDrawManager, Map>("map");
        public static int Swept;

        public static void Sweep(DynamicDrawManager manager)
        {
            Map map = drawMap(manager);
            if (map != null && IsoRim.Api.ViewReady)
            {
                Beams.Sweep(map);   // beams whose thing the game culled (a hidden floor's rows)
                Puffs.Draw(map);    // trail and flash flecks in the air
                Holes.Draw(map);    // CE's bullet holes on the walls hit
            }
            if (!Enabled || !IsoRim.Api.ViewReady || map == null || IsoRim.Api.Floors == null || IsoRim.Api.Floors.GroundRows(map) <= 0)
            {
                return;
            }
            int frame = Time.frameCount;
            List<Thing> things = map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is ProjectileCE p) || p.landed || !p.def.projectile.flyOverhead || p.def.graphicData == null || p.FlightTicks == 0)
                {
                    continue;
                }
                Flight f = flights.GetValue(p, Begin);
                if (f.drawnFrame == frame || IsoRim.Api.FloorShown(Point(p, exactPosition(p))))
                {
                    continue;   // drawn by the game this frame, or on a shown floor (the game draws it when in view)
                }
                try
                {
                    Draw(p);
                    Swept++;
                }
                catch (Exception e)
                {
                    Errors++;
                    Log.ErrorOnce(Pack.Tag + "Drawing a CE shell over a hidden floor failed: " + e, 74120303);
                }
            }
        }

        // Where the projectile is drawn now: true space over its own floor (x, z the game's rows, y cells over that floor).
        private static Vector3 Point(ProjectileCE p, Vector3 e)
        {
            Flight f = flights.GetValue(p, Begin);
            Vector2 o = originRef(p);
            float done = new Vector2(e.x - o.x, e.z - o.y).magnitude;
            float rest = f.fade > 0.01f ? 1f - Mathf.Clamp01(done / f.fade) : 0f;
            return new Vector3(e.x + f.lead.x * rest, Mathf.Max(0f, e.y * CellsPerUnit + f.lift * rest), e.z + f.lead.z * rest);
        }

        // The drawn point now and a tick ago (true space over the projectile's floor): trails (Puffs).
        internal static Vector3 PointNow(ProjectileCE p) => Point(p, exactPosition(p));

        internal static Vector3 PointLast(ProjectileCE p) => Point(p, lastPosRef(p));

        // Harness: CE's height a tick ago (CE units).
        public static float LastHeight(ProjectileCE p) => lastPosRef(p).y;

        // Harness: CE's point and the drawn one.
        public static string Describe(ProjectileCE p)
        {
            Vector3 e = exactPosition(p);
            Vector3 at = Point(p, e);
            bool lowered = !IsoRim.Api.FloorShown(at) && p.def.projectile.flyOverhead && ShownBelow(p.Map, ref at);
            Vector2 px = IsoRim.Api.ViewReady ? IsoRim.Api.WorldToScreen(at) : Vector2.zero;
            return "CE " + e.ToString("F2") + " drawn " + at.ToString("F2") + (lowered ? " (over the shown floor below)" : "") + " screen px " + px.ToString("F0") + " seen over ground " + (IsoRim.Api.ViewReady ? IsoRim.Api.ScreenToGround(px).ToString("F1") : "-");
        }

        private static void Draw(ProjectileCE p)
        {
            flights.GetValue(p, Begin).drawnFrame = Time.frameCount;
            float k = CellsPerUnit;
            Vector3 e = exactPosition(p);
            Vector2 o = originRef(p);
            Vector3 at = Point(p, e);
            if (!IsoRim.Api.FloorShown(at) && !(p.def.projectile.flyOverhead && ShownBelow(p.Map, ref at)))
            {
                Hidden++;
                return;
            }
            float squash = IsoRim.Api.HeightSquash;
            float altitude = p.def.Altitude;
            Vector3 drawn = new Vector3(at.x, altitude + at.y / squash, at.z);
            Vector2 size = p.def.graphicData.drawSize;
            Vector3 step = e - lastPosRef(p);   // CE's last tick of flight (CE units)
            ProjectileProperties pp = p.def.projectile;

            if (pp.shadowSize > 0f)
            {
                Material sm = shadowMat();
                if (sm != null)
                {
                    float s = pp.shadowSize * Mathf.Lerp(1f, 0.6f, Mathf.Clamp01(at.y / 6f));
                    Matrix4x4 sh = Matrix4x4.TRS(new Vector3(at.x, altitude - 0.01f, at.z), Quaternion.identity, new Vector3(s, 1f, s));
                    Graphics.DrawMesh(MeshPool.plane10, IsoRim.Api.DrawnMatrix(sh), sm, 0);
                }
            }

            Material mat = p.def.DrawMatSingle;
            if (pp.flyOverhead)
            {
                // Indirect fire: a card facing the camera, its nose along the flight as seen on screen.
                Vector3 along = new Vector3(step.x, step.y * k, step.z);
                if (along.sqrMagnitude < 1e-8f)
                {
                    along = new Vector3(destinationRef(p).x - o.x, 0f, destinationRef(p).y - o.y);
                }
                Quaternion look = Quaternion.Euler(IsoRim.Api.ViewPitch, IsoRim.Api.ViewYaw, 0f);
                float right = Vector3.Dot(along, look * Vector3.right);
                float up = Vector3.Dot(along, look * Vector3.up);
                float spin = right * right + up * up > 1e-8f ? Mathf.Atan2(right, up) * Mathf.Rad2Deg : 0f;
                Matrix4x4 m = Matrix4x4.Translate(drawn)
                    * Matrix4x4.Scale(new Vector3(1f, 1f / squash, 1f))
                    * Matrix4x4.Rotate(Quaternion.Euler(IsoRim.Api.ViewPitch - 90f, IsoRim.Api.ViewYaw, 0f))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(spin, Vector3.up))
                    * Matrix4x4.Scale(new Vector3(size.x, 1f, size.y));
                Graphics.DrawMesh(MeshPool.plane10, IsoRim.Api.DrawnMatrix(m), mat, 0);
                DrawnLobbed++;
            }
            else
            {
                // Direct fire: a flat card at its height, its nose along the flight (or spinning).
                Quaternion rotation;
                if (pp.spinRate != 0f)
                {
                    float period = 60f / pp.spinRate;
                    rotation = Quaternion.AngleAxis(Find.TickManager.TicksGame % period / period * 360f, Vector3.up);
                }
                else
                {
                    Vector3 flat = new Vector3(step.x, 0f, step.z);
                    if (flat.sqrMagnitude < 1e-8f)
                    {
                        flat = new Vector3(destinationRef(p).x - o.x, 0f, destinationRef(p).y - o.y);
                    }
                    rotation = flat.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(flat) : Quaternion.identity;
                }
                if (Grenades.Draw(p, at, rotation.eulerAngles.y))
                {
                    Drawn++;
                    postDraw(p);
                    return;
                }
                Matrix4x4 m = Matrix4x4.TRS(drawn, rotation, Vector3.one);
                Graphics.DrawMesh(MeshPool.GridPlane(size), IsoRim.Api.DrawnMatrix(m), mat, 0);
            }
            Drawn++;
            postDraw(p);
        }

        // An overhead shell (mortar, artillery) over a floor the viewer has hidden (stacked floors: the floors above the one
        // viewed): it is in the open sky above what is shown, so it is drawn at its true height over the highest shown floor
        // below it (the same place in 3D: the floors are stacked translations). Direct fire on a hidden floor stays hidden
        // with that floor (its shooter and target are hidden too).
        private static bool ShownBelow(Map map, ref Vector3 at)
        {
            IsoRim.IFloorLayout floors = IsoRim.Api.Floors;
            int rows = floors != null && map != null ? floors.GroundRows(map) : 0;
            if (rows <= 0)
            {
                return false;
            }
            int n = floors.FloorCount(map);
            for (int k = n; k >= 1; k--)
            {
                floors.Floor(map, k, out float hk, out int sk);
                if (at.z < sk || at.z >= sk + rows)
                {
                    continue;
                }
                for (int j = k - 1; j >= 0; j--)
                {
                    float hj = 0f;
                    int sj = 0;
                    if (j == 0 || floors.Floor(map, j, out hj, out sj))
                    {
                        at = new Vector3(at.x, at.y + hk - hj, at.z - sk + sj);
                        Lowered++;
                        return true;
                    }
                }
                return false;
            }
            return false;
        }

        // Once a flight, when first drawn: where the drawing starts (IsoRim's muzzle) against CE's launch point.
        private static Flight Begin(ProjectileCE p)
        {
            Flight f = new Flight();
            Vector2 o = originRef(p);
            Vector3 from = new Vector3(o.x, 0f, o.y);
            Vector3 dest = new Vector3(destinationRef(p).x, 0f, destinationRef(p).y);
            f.fade = (dest - from).magnitude;
            if (p.intendedTarget.IsValid)
            {
                Vector3 aim = p.intendedTarget.CenterVector3;
                float toAim = new Vector2(aim.x - o.x, aim.z - o.y).magnitude;
                if (toAim > 0.5f && toAim < f.fade)
                {
                    f.fade = toAim;
                }
            }
            try
            {
                Thing owner = MuzzleOwner(p, from);
                if (IsGunShot(p))
                {
                    GunShots++;
                    if (owner == null)
                    {
                        GunShotsNoOwner++;
                    }
                }
                if (owner != null)
                {
                    // CE's start is in the frame of the storey the shot is in now (on stacked floors it may have been moved
                    // to another storey's rows before it is first drawn); the muzzle is in the owner's.
                    float dh;
                    Vector3 own = InBandOf(owner, from, out dh);
                    Vector3 dir = dest - from;
                    dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
                    Vector3 muzzle = IsoRim.Api.Muzzle(owner, p.equipmentDef, owner is Building_Turret ? owner.DrawPos : own, dir);
                    f.lead = new Vector3(muzzle.x - own.x, 0f, muzzle.z - own.z);
                    f.lift = muzzle.y + dh - p.shotHeight * CellsPerUnit;
                }
                if (Harness.On)
                {
                    Log.Message(Pack.Tag + "launch " + p.def.defName + " (" + p.GetType().Name + ", " + (p.def.projectile.flyOverhead ? "lobbed" : "direct") + ") by "
                        + (p.launcher != null ? p.launcher.LabelShort : "-") + " with " + (p.equipmentDef != null ? p.equipmentDef.defName : "-")
                        + ": muzzle owner " + (owner != null ? owner.LabelShort : "none") + ", CE start " + from.ToString("F2") + " height " + p.shotHeight.ToString("0.00")
                        + " (" + (p.shotHeight * CellsPerUnit).ToString("0.00") + " cells), lead " + f.lead.ToString("F2") + " lift " + f.lift.ToString("0.00")
                        + ", fade over " + f.fade.ToString("0.0") + ", tick " + Find.TickManager.TicksGame);
                }
            }
            catch (Exception e)
            {
                Errors++;
                Log.ErrorOnce(Pack.Tag + "Finding a CE shot's muzzle failed (drawn from CE's launch point): " + e, 74120302);
            }
            return f;
        }

        // Gun shots (a pawn's or a turret's ranged weapon) first drawn, and those whose muzzle owner was not found (drawn
        // from CE's launch point instead of the barrel end). Harness: cepack:muzzlecheck.
        public static int GunShots, GunShotsNoOwner;

        // Harness only (cepack:bandfix:off): the owner lookup without moving the launch point into the launcher's storey, to
        // show what the check catches.
        public static bool BandFix = true;

        // The row band (storey) of row z on a map with stacked floors (IsoRim.Api.Floors): its row shift and height over the
        // ground. The ground (and every map without floors): 0, 0.
        private static void Band(Map map, float z, out int shift, out float height)
        {
            shift = 0;
            height = 0f;
            IsoRim.IFloorLayout fl = IsoRim.Api.Floors;
            int rows = fl != null && map != null ? fl.GroundRows(map) : 0;
            if (rows <= 0 || z < rows)
            {
                return;
            }
            for (int k = fl.FloorCount(map); k >= 1; k--)
            {
                float h;
                int s;
                fl.Floor(map, k, out h, out s);
                if (z >= s && z < s + rows)
                {
                    shift = s;
                    height = h;
                    return;
                }
            }
        }

        // Point p moved into the storey of thing t (the same cell of that storey's rows), and how much higher t's storey
        // floor is than p's (cells). Frames of stacked floors are pure translations.
        internal static Vector3 InBandOf(Thing t, Vector3 p, out float dh)
        {
            int st, sp;
            float ht, hp;
            Band(t.Map, t.Position.z, out st, out ht);
            Band(t.Map, p.z, out sp, out hp);
            if (!BandFix)
            {
                dh = 0f;
                return p;
            }
            dh = ht - hp;
            return new Vector3(p.x, p.y, p.z + (st - sp));
        }

        // Who the shot visibly leaves from: the turret when a pawn manning it fired its gun (a manned mortar: CE names the
        // pawn as the launcher), else the launching pawn or turret. Null when the shot did not leave from a shooter (CE's
        // fragments, cook-offs, shells arriving from the world map, a launch point away from the launcher).
        // The muzzle owner of a CE shot from its launch point as it stands now (harness check: cepack:muzzlecheck).
        internal static Thing MuzzleOwnerOf(ProjectileCE p)
        {
            Vector2 o = originRef(p);
            return MuzzleOwner(p, new Vector3(o.x, 0f, o.y));
        }

        // A gun shot: a pawn's or a turret's ranged weapon (not fragments, cook-offs or thrown things).
        internal static bool IsGunShot(ProjectileCE p) => p.equipment != p && p.equipmentDef != null && p.equipmentDef.IsRangedWeapon && (p.launcher is Pawn || p.launcher is Building_Turret);

        private static Thing MuzzleOwner(ProjectileCE p, Vector3 from)
        {
            return p.equipment == p ? null : MuzzleOwnerFor(p.launcher, p.equipmentDef, from.ToIntVec3(), p.Map, from);
        }

        // The same for a beam or a flash that left from cell c (Beams, Puffs).
        internal static Thing MuzzleOwnerFor(Thing launcher, ThingDef gun, IntVec3 c, Map map) => MuzzleOwnerFor(launcher, gun, c, map, c.ToVector3Shifted());

        private static Thing MuzzleOwnerFor(Thing launcher, ThingDef gun, IntVec3 c, Map map, Vector3 from)
        {
            if (launcher == null || !launcher.Spawned || launcher.Map != map)
            {
                return null;
            }
            // Compared in the launcher's storey: a shot already moved to another storey's rows (stacked floors) still
            // left from its launcher.
            if (BandFix)
            {
                float dh;
                from = InBandOf(launcher, from, out dh);
                c = from.ToIntVec3();
            }
            if (launcher is Pawn && gun != null && c.InBounds(map) && c.GetEdifice(map) is Building_Turret manned
                && (manned.def == gun || (manned.def.building != null && manned.def.building.turretGunDef == gun)))
            {
                launcher = manned;
            }
            if (launcher is Building_Turret turret)
            {
                return turret.OccupiedRect().ExpandedBy(2).Contains(c) ? turret : null;
            }
            if (launcher is Pawn)
            {
                return new Vector2(launcher.DrawPos.x - from.x, launcher.DrawPos.z - from.z).magnitude < 1.5f ? launcher : null;
            }
            return null;
        }
    }
}

namespace IsoRimCEPack
{
    // Overhead shells on a hidden floor's rows (ProjectileCE_DrawAt.Sweep).
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    public static class DynamicDrawManager_Shells
    {
        public static void Postfix(DynamicDrawManager __instance) => ProjectileCE_DrawAt.Sweep(__instance);
    }
}
