using System;
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
    //    Api.DrawnMatrix (onto that floor; nothing on a hidden one, Api.FloorShown), as IsoRim places vanilla shots.
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
                Harmony h = new Harmony("peren.isorimwaylith.cepack");
                h.PatchAll(typeof(Pack).Assembly);
            }
            catch (Exception e)
            {
                Log.Error(Tag + "Setup failed (CE's projectiles stay as CE draws them): " + e);
            }
        }
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
        }

        private static readonly ConditionalWeakTable<ProjectileCE, Flight> flights = new ConditionalWeakTable<ProjectileCE, Flight>();

        private static readonly Action<ThingWithComps> postDraw =
            AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));

        // The game's round projectile shadow (Verse.Projectile.shadowMaterial), as IsoRim draws for vanilla shells.
        private static readonly AccessTools.FieldRef<Material> shadowMat =
            AccessTools.StaticFieldRefAccess<Material>(AccessTools.Field(typeof(Projectile), "shadowMaterial"));

        // CE members typed with Unity types are read through Harmony accessors: CombatExtended.dll is built against Unity
        // (UnityEngine.CoreModule), this pack against Waylith (UnityStubs); the game maps CE onto Waylith when it loads it.
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector2> originRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("origin");
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector2> destinationRef = AccessTools.FieldRefAccess<ProjectileCE, Vector2>("Destination");
        private static readonly AccessTools.FieldRef<ProjectileCE, Vector3> lastPosRef = AccessTools.FieldRefAccess<ProjectileCE, Vector3>("LastPos");
        private static readonly Func<ProjectileCE, Vector3> exactPosition =
            AccessTools.MethodDelegate<Func<ProjectileCE, Vector3>>(AccessTools.PropertyGetter(typeof(ProjectileCE), "ExactPosition"));

        // CE height units to IsoRim cells (see the header).
        public static float CellsPerUnit => IsoRim.Api.WallHeight / CollisionVertical.WallCollisionHeight;

        // Harness counters, and the harness switch (Harness: drawing off shows CE's own drawing).
        public static int Drawn, DrawnLobbed, Errors;
        public static bool Enabled = true;

        public static bool Prefix(ProjectileCE __instance)
        {
            ProjectileCE p = __instance;
            if (!Enabled || !IsoRim.Api.ViewReady || p == null || p.def?.graphicData == null || p.Map == null)
            {
                return true;
            }
            try
            {
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

        // Where the projectile is drawn now: true space over its own floor (x, z the game's rows, y cells over that floor).
        private static Vector3 Point(ProjectileCE p, Vector3 e)
        {
            Flight f = flights.GetValue(p, Begin);
            Vector2 o = originRef(p);
            float done = new Vector2(e.x - o.x, e.z - o.y).magnitude;
            float rest = f.fade > 0.01f ? 1f - Mathf.Clamp01(done / f.fade) : 0f;
            return new Vector3(e.x + f.lead.x * rest, Mathf.Max(0f, e.y * CellsPerUnit + f.lift * rest), e.z + f.lead.z * rest);
        }

        // Harness: CE's height a tick ago (CE units).
        public static float LastHeight(ProjectileCE p) => lastPosRef(p).y;

        // Harness: CE's point and the drawn one.
        public static string Describe(ProjectileCE p)
        {
            Vector3 e = exactPosition(p);
            Vector3 at = Point(p, e);
            return "CE " + e.ToString("F2") + " drawn " + at.ToString("F2") + " seen over ground " + (IsoRim.Api.ViewReady ? IsoRim.Api.ScreenToGround(IsoRim.Api.WorldToScreen(at)).ToString("F1") : "-");
        }

        private static void Draw(ProjectileCE p)
        {
            float k = CellsPerUnit;
            Vector3 e = exactPosition(p);
            Vector2 o = originRef(p);
            Vector3 at = Point(p, e);
            if (!IsoRim.Api.FloorShown(at))
            {
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
                Matrix4x4 m = Matrix4x4.TRS(drawn, rotation, Vector3.one);
                Graphics.DrawMesh(MeshPool.GridPlane(size), IsoRim.Api.DrawnMatrix(m), mat, 0);
            }
            Drawn++;
            postDraw(p);
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
                if (owner != null)
                {
                    Vector3 dir = dest - from;
                    dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
                    Vector3 muzzle = IsoRim.Api.Muzzle(owner, p.equipmentDef, owner is Building_Turret ? owner.DrawPos : from, dir);
                    f.lead = new Vector3(muzzle.x - from.x, 0f, muzzle.z - from.z);
                    f.lift = muzzle.y - p.shotHeight * CellsPerUnit;
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

        // Who the shot visibly leaves from: the turret when a pawn manning it fired its gun (a manned mortar: CE names the
        // pawn as the launcher), else the launching pawn or turret. Null when the shot did not leave from a shooter (CE's
        // fragments, cook-offs, shells arriving from the world map, a launch point away from the launcher).
        private static Thing MuzzleOwner(ProjectileCE p, Vector3 from)
        {
            Thing launcher = p.launcher;
            if (launcher == null || !launcher.Spawned || launcher.Map != p.Map || p.equipment == p)
            {
                return null;
            }
            ThingDef gun = p.equipmentDef;
            IntVec3 c = from.ToIntVec3();
            if (launcher is Pawn && gun != null && c.InBounds(p.Map) && c.GetEdifice(p.Map) is Building_Turret manned
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
