using System;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // Flecks CE makes in the air, drawn where they are in 3D (the 3D view only).
    //
    // The game draws a fleck flat at its map point. CE puts two kinds in the air:
    //  - Trails (CompProjectileFleck: a rocket's flame and smoke, a flamethrower's glow, ...): each tick CE makes them at
    //    the projectile's DrawPos (its flat point shifted up the screen for its height), so in 3D they lay on the ground
    //    beside the flight. Here each one CE makes is put at the projectile's drawn point instead (ProjectileCE_DrawAt.Point:
    //    its true height, from the drawn muzzle), back along the last tick's flight as far as CE spaces them.
    //  - Its own muzzle flash (Verb_ShootCE: FleckMakerCE.Static(caster cell, ShotFlash, ...) when a gun without a magazine
    //    runs out of ammo; the game's per-shot flash is IsoRim's, at the muzzle already): put at the shooter's muzzle
    //    (Api.Muzzle) instead of its middle on the ground.
    //  - An ammo stack's cook-off flash (AmmoThing.TryLaunchCookOffProjectile: FleckMakerCE.Static(its cell, ShotFlash,
    //    cookOffFlashScale)): flat on the ground it lay under the stack's 3D model, faint and hidden; put just above the
    //    stack instead, standing up as a shot's flash does. With the setting "Cook-off sparks" (default on) each round also
    //    throws a few of the game's own flecks there (CookOffExtras): a short spray of sparks, a brighter flash and a small
    //    puff of smoke; with the view off they are the game's flat flecks.
    // IsoRim has no public way to stand a fleck up in the air, so these few are drawn here, from the fleck's own def as the
    // game draws it (its texture, colour, fade in / solid / fade out, growth, turn and drift, the size and turn CE gave it),
    // as a card facing the camera at that point (IsoRim stands its own puffs and flashes the same way). On stacked floors a
    // point stays on its floor's rows (drawn onto that floor; not drawn while it is hidden). The view off: CE's flecks as usual.
    [StaticConstructorOnStartup]   // (a MaterialPropertyBlock static: made on the main thread at startup)
    public static class Puffs
    {
        public static bool Enabled = true;
        public static int Trails, Flashes, CookOffs, Drawn;
        private const float CookOffHeight = 0.3f;   // cells over the ground: just above an ammo stack's model
        private const int Max = 4000;

        private struct Puff
        {
            public FleckDef def;
            public Vector3 at;        // true space over its floor (that floor's rows, height in cells)
            public Vector3 drift;     // cells a second, flat
            public float scale, rotation, rotationRate, born, solid;
            public Color color;
        }

        private static readonly List<Puff> puffs = new List<Puff>();
        private static Map puffMap;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        private static float Now => Find.TickManager.TicksGame / 60f;

        // The flat fleck the game makes is still made, as always (the same random numbers drawn, the same simulation), only
        // with no colour (an invisible quad): the one drawn here stands in for it.
        private static readonly Color Unseen = new Color(1f, 1f, 1f, 0f);

        // ---- trails: CompProjectileFleck.Emit, the fleck CE makes there (FleckManager.CreateFleck)
        [ThreadStatic] private static ProjectileCE emitting;
        [ThreadStatic] private static Vector3 emitFrom, emitStep;   // CE's DrawPos now and its step back to the last one
        [ThreadStatic] private static Vector3 drawnNow, drawnLast;  // the same two as drawn (true space)

        private static readonly AccessTools.FieldRef<CompProjectileFleck, Vector3> compLastPos = AccessTools.FieldRefAccess<CompProjectileFleck, Vector3>("lastPos");

        public static void Emit_Prefix(CompProjectileFleck __instance)
        {
            emitting = null;
            if (!Enabled || !IsoRim.Api.ViewReady || !(__instance.parent is ProjectileCE p) || !p.Spawned)
            {
                return;
            }
            Rand.PushState();   // (runs in the tick: nothing here may draw the game's random numbers)
            try
            {
                emitFrom = ((Thing)p).DrawPos;
                emitStep = compLastPos(__instance) - emitFrom;
                drawnNow = ProjectileCE_DrawAt.PointNow(p);
                drawnLast = ProjectileCE_DrawAt.PointLast(p);
                emitting = p;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Placing a CE projectile's trail failed (left as CE makes it): " + e, 74120331);
            }
            finally
            {
                Rand.PopState();
            }
        }

        public static void Emit_Finalizer() => emitting = null;

        // FleckManager.CreateFleck: a trail fleck while emitting, or CE's flash being made: drawn here, the game's made unseen.
        public static void CreateFleck_Prefix(ref FleckCreationData fleckData)
        {
            if (fleckData.def == null)
            {
                return;
            }
            if (Holes.CreateFleck(ref fleckData, Unseen))
            {
                return;   // a bullet hole on the wall hit (Holes)
            }
            if (flashing != null && fleckData.def == FleckDefOf.ShotFlash)
            {
                Add(flashMap, fleckData, flashAt);
                flashing = null;
                Flashes++;
                fleckData.instanceColor = Unseen;
                return;
            }
            Thing ammo = cooking;
            if (ammo != null && (extra || fleckData.def == FleckDefOf.ShotFlash))
            {
                if (!extra)
                {
                    cooking = null;   // (CE's one flash; the extras come after it, CookOffExtras)
                }
                if (Enabled && IsoRim.Api.ViewReady && ammo.Spawned && ammo.Map != null)
                {
                    IntVec3 c = ammo.Position;
                    Vector3 over = extra ? new Vector3(fleckData.spawnPosition.x, extraHeight, fleckData.spawnPosition.z) : new Vector3(c.x + 0.5f, CookOffHeight, c.z + 0.5f);
                    Add(ammo.Map, fleckData, over);
                    if (!extra)
                    {
                        CookOffs++;
                    }
                    fleckData.instanceColor = Unseen;
                }
                return;
            }
            ProjectileCE p = emitting;
            if (p == null)
            {
                return;
            }
            Vector3 d = fleckData.spawnPosition - emitFrom;
            d.y = 0f;
            Vector3 step = new Vector3(emitStep.x, 0f, emitStep.z);
            float t = step.sqrMagnitude > 1e-8f ? Vector3.Dot(d, step) / step.sqrMagnitude : 0f;
            Vector3 at = drawnNow + (drawnLast - drawnNow) * t;
            Add(p.Map, fleckData, at);
            Trails++;
            fleckData.instanceColor = Unseen;
        }

        // ---- CE's own muzzle flash (Verb_ShootCE.OnCastSuccessful: FleckMakerCE.Static(caster.Position, map, ShotFlash, scale))
        [ThreadStatic] private static Verb_ShootCE shooting;
        [ThreadStatic] private static Thing flashing;
        [ThreadStatic] private static Map flashMap;
        [ThreadStatic] private static Vector3 flashAt;

        public static void TryCastShot_Prefix(Verb_ShootCE __instance) => shooting = __instance;

        public static void TryCastShot_Finalizer() => shooting = null;

        public static void FleckStatic_Prefix(IntVec3 cell, Map map, FleckDef fleckDef)
        {
            flashing = null;
            Verb_ShootCE verb = shooting;
            Thing caster = verb?.caster;
            if (!Enabled || verb == null || fleckDef != FleckDefOf.ShotFlash || !IsoRim.Api.ViewReady || !(caster is Pawn) || !caster.Spawned || caster.Map != map || caster.Position != cell)
            {
                return;   // (a turret's flash: IsoRim puts it at its gun)
            }
            Rand.PushState();
            try
            {
                ThingDef gun = verb.EquipmentSource?.def;
                Thing owner = ProjectileCE_DrawAt.MuzzleOwnerFor(caster, gun, cell, map) ?? caster;
                LocalTargetInfo target = verb.CurrentTarget;
                Vector3 dir = Vector3.zero;
                if (target.IsValid)
                {
                    Vector3 to = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
                    dir = new Vector3(to.x - caster.DrawPos.x, 0f, FloorMath.GroundZ(map, to.z) - FloorMath.GroundZ(map, caster.DrawPos.z));
                    dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
                }
                flashAt = IsoRim.Api.Muzzle(owner, gun, caster.DrawPos, dir);
                flashMap = map;
                flashing = caster;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Putting CE's muzzle flash at the muzzle failed (left as CE makes it): " + e, 74120332);
            }
            finally
            {
                Rand.PopState();
            }
        }

        public static void FleckStatic_Finalizer() => flashing = null;

        // ---- an ammo stack cooking off (AmmoThing.TryLaunchCookOffProjectile): its ShotFlash goes above the stack
        [ThreadStatic] private static Thing cooking;

        public static void CookOff_Prefix(Thing __instance) => cooking = __instance;

        public static void CookOff_Finalizer() => cooking = null;

        private const int CookOffQuiet = 15;   // ticks: a stack's full burst at most this often; rounds between: a spark or two
        private static readonly Dictionary<int, int> lastBurst = new Dictionary<int, int>();
        [ThreadStatic] private static bool extra;
        [ThreadStatic] private static float extraHeight;
        public static int Extras;

        // After a round cooked off (CE's flash made, the method answered true): the setting's extra flecks over the stack,
        // made with the game's fleck drawing (stood up by CreateFleck_Prefix while the view is on, the game's flat flecks
        // with it off). Their random numbers come from a seed of their own (the stack and the tick): the game's are untouched.
        public static void CookOffExtras(Thing ammo, bool launched)
        {
            CEPackSettings st = CEPackMod.Settings;
            if (!launched || !Enabled || st == null || !st.cookOffSparks || ammo == null || !ammo.Spawned || ammo.Map == null)
            {
                return;
            }
            Map map = ammo.Map;
            Vector3 mid = ammo.Position.ToVector3Shifted();
            int now = Find.TickManager.TicksGame;
            bool full = !lastBurst.TryGetValue(ammo.thingIDNumber, out int last) || now - last >= CookOffQuiet || now < last;
            if (full)
            {
                if (lastBurst.Count > 256)
                {
                    lastBurst.Clear();
                }
                lastBurst[ammo.thingIDNumber] = now;
            }
            cooking = ammo;
            Rand.PushState(Gen.HashCombineInt(ammo.thingIDNumber, Find.TickManager.TicksGame));
            try
            {
                // a brighter flash than CE's, a short spray of sparks, now and then a small puff of smoke; a stack cooking off
                // fast (many rounds a second) shows that burst at most every CookOffQuiet ticks, a spark or two in between
                if (full)
                {
                    Make(map, FleckDefOf.ExplosionFlash, mid, CookOffHeight + 0.05f, Rand.Range(2.2f, 2.6f), 0f, 0f, 0f, null);
                }
                int sparks = full ? Rand.RangeInclusive(4, 6) : Rand.RangeInclusive(0, 1);
                for (int i = 0; i < sparks; i++)
                {
                    Vector3 off = new Vector3(Rand.Range(-0.15f, 0.15f), 0f, Rand.Range(-0.15f, 0.15f));
                    Make(map, FleckDefOf.MicroSparksFast, mid + off, CookOffHeight + Rand.Range(0.05f, 0.35f), Rand.Range(0.9f, 1.3f),
                        Rand.Range(0f, 360f), Rand.Range(1.0f, 2.0f), Rand.Range(-180f, 180f), null);
                }
                if (full && Rand.Chance(0.5f))
                {
                    Make(map, FleckDefOf.Smoke, mid, CookOffHeight + 0.25f, Rand.Range(0.8f, 1.1f), Rand.Range(0f, 360f), Rand.Range(0.1f, 0.3f),
                        Rand.Range(-30f, 30f), 0.6f);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Cook-off sparks failed (left out): " + e, 74120341);
            }
            finally
            {
                Rand.PopState();
                extra = false;
                cooking = null;
            }
        }

        private static void Make(Map map, FleckDef def, Vector3 at, float height, float scale, float angle, float speed, float spin, float? solid)
        {
            if (def == null)
            {
                return;
            }
            FleckCreationData data = FleckMaker.GetDataStatic(at, map, def, scale);
            data.rotation = Rand.Range(0f, 360f);
            data.rotationRate = spin;
            data.velocityAngle = angle;
            data.velocitySpeed = speed;
            data.solidTimeOverride = solid;
            extra = true;
            extraHeight = height;
            try
            {
                map.flecks.CreateFleck(data);
            }
            finally
            {
                extra = false;
            }
            Extras++;
        }

        private static void Add(Map map, FleckCreationData data, Vector3 at)
        {
            if (map != puffMap)
            {
                puffs.Clear();
                puffMap = map;
            }
            if (puffs.Count >= Max)
            {
                puffs.RemoveAt(0);
            }
            Vector3 drift = Vector3.zero;
            if (data.velocity.HasValue)
            {
                drift = data.velocity.Value;
                drift.y = 0f;
            }
            else if (data.velocitySpeed != 0f)
            {
                drift = Quaternion.AngleAxis(data.velocityAngle, Vector3.up) * Vector3.forward * data.velocitySpeed;
            }
            puffs.Add(new Puff
            {
                def = data.def,
                at = at,
                drift = drift,
                scale = data.scale,
                rotation = data.rotation,
                rotationRate = data.rotationRate,
                born = Now,
                solid = data.solidTimeOverride ?? data.def.solidTime,
                color = data.instanceColor ?? Color.white
            });
        }

        // Drawn every frame with the map's dynamic things (DynamicDrawManager.DrawDynamicThings postfix).
        public static void Draw(Map map)
        {
            if (puffs.Count == 0)
            {
                return;
            }
            if (map != puffMap)
            {
                return;
            }
            float now = Now;
            float squash = IsoRim.Api.HeightSquash;
            bool view = Enabled && IsoRim.Api.ViewReady;
            Quaternion face = Quaternion.Euler(IsoRim.Api.ViewPitch - 90f, IsoRim.Api.ViewYaw, 0f);
            Vector3 forward = FloorMath.View * Vector3.forward;
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                Puff p = puffs[i];
                FleckDef def = p.def;
                float age = now - p.born;
                float life = def.fadeInTime + p.solid + def.fadeOutTime;
                if (age > life || age < -1f)
                {
                    puffs.RemoveAt(i);
                    continue;
                }
                if (!view)
                {
                    continue;
                }
                float alpha = age <= def.fadeInTime ? (def.fadeInTime > 0f ? age / def.fadeInTime : 1f)
                    : age <= def.fadeInTime + p.solid ? 1f
                    : def.fadeOutTime > 0f ? 1f - Mathf.InverseLerp(def.fadeInTime + p.solid, life, age) : 1f;
                if (alpha <= 0f || def.graphicData == null)
                {
                    continue;
                }
                Vector3 at = p.at + p.drift * Mathf.Max(0f, age);
                if (!IsoRim.Api.FloorShown(at))
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
                float grow = def.growthRate * Mathf.Max(0f, age);
                float sx = Mathf.Max(0.0001f, Mathf.Abs(p.scale) + grow) * ds.x;
                float sz = Mathf.Max(0.0001f, Mathf.Abs(p.scale) + grow) * ds.y;
                // A glow (added light) is pulled toward the camera as IsoRim's glow cards, so what it hangs by does not cut it.
                if (mat.shader == ShaderDatabase.MoteGlow)
                {
                    at -= forward * (Mathf.Max(sx, sz) * 0.6f);
                }
                Color c = g.Color * p.color;
                c.a *= alpha;
                block.SetColor(ShaderPropertyIDs.Color, c);
                Matrix4x4 m = Matrix4x4.Translate(new Vector3(at.x, at.y / squash, at.z))
                    * Matrix4x4.Scale(new Vector3(1f, 1f / squash, 1f))
                    * Matrix4x4.Rotate(face)
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(p.rotation + p.rotationRate * age, Vector3.up))
                    * Matrix4x4.Scale(new Vector3(sx, 1f, sz));
                Graphics.DrawMesh(MeshPool.plane10, IsoRim.Api.DrawnMatrix(m), mat, 0, null, 0, block);
                Drawn++;
            }
        }

        public static int Live => puffs.Count;
    }

    [HarmonyPatch(typeof(CompProjectileFleck), "Emit")]
    public static class CompProjectileFleck_Emit
    {
        public static void Prefix(CompProjectileFleck __instance) => Puffs.Emit_Prefix(__instance);

        public static Exception Finalizer(Exception __exception)
        {
            Puffs.Emit_Finalizer();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) })]
    public static class FleckManager_CreateFleck
    {
        [HarmonyPriority(Priority.Last)]
        public static void Prefix(ref FleckCreationData fleckData) => Puffs.CreateFleck_Prefix(ref fleckData);
    }

    [HarmonyPatch(typeof(Verb_ShootCE), "OnCastSuccessful")]
    public static class Verb_ShootCE_OnCastSuccessful
    {
        public static void Prefix(Verb_ShootCE __instance) => Puffs.TryCastShot_Prefix(__instance);

        public static Exception Finalizer(Exception __exception)
        {
            Puffs.TryCastShot_Finalizer();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(FleckMakerCE), nameof(FleckMakerCE.Static), new[] { typeof(IntVec3), typeof(Map), typeof(FleckDef), typeof(float) })]
    public static class FleckMakerCE_Static
    {
        public static void Prefix(IntVec3 cell, Map map, FleckDef fleckDef) => Puffs.FleckStatic_Prefix(cell, map, fleckDef);

        public static Exception Finalizer(Exception __exception)
        {
            Puffs.FleckStatic_Finalizer();
            return __exception;
        }
    }

    // CE: AmmoThing.TryLaunchCookOffProjectile (private): the flash of a cooking-off ammo stack (Puffs). Not found (a CE
    // update renamed it): the flash stays as CE makes it, nothing else changes.
    [HarmonyPatch]
    public static class AmmoThing_CookOff
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            target ??= AccessTools.Method(typeof(AmmoThing), Pack.FaultName("TryLaunchCookOffProjectile"));
            if (target == null)
            {
                Log.Warning(Pack.Tag + "CE's ammo cook-off (AmmoThing.TryLaunchCookOffProjectile) not found: its flash stays flat on the ground.");
            }
            return target != null;
        }

        public static MethodBase TargetMethod() => target;

        public static void Prefix(Thing __instance) => Puffs.CookOff_Prefix(__instance);

        public static void Postfix(Thing __instance, bool __result) => Puffs.CookOffExtras(__instance, __result);

        public static Exception Finalizer(Exception __exception)
        {
            Puffs.CookOff_Finalizer();
            return __exception;
        }
    }
}
