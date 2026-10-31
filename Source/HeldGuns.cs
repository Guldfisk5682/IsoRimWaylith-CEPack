using System;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // Held guns under CE in the 3D view.
    //
    // The game draws an aimed weapon in PawnRenderUtility.DrawEquipmentAiming with one Graphics.DrawMesh, which IsoRim
    // rewrites to its own drawing (Api.DrawMesh): on a 3D figure the picture is caught and the weapon's model put in the
    // figure's hands, its barrel end being where shots and the flash start; on a 2D card it stands up with the card. CE
    // transpiles that call into its own helper (HarmonyCE.Harmony_PawnRenderer_DrawEquipmentAiming.DrawMesh: CE's recoil,
    // muzzle jump and sprite offsets, the casing point), which draws straight to Unity: IsoRim never saw the weapon, so it lay
    // flat at the pawn's feet and shots started from a guessed muzzle (lower, no model).
    //
    // Here, with the 3D view on, CE's helper places the weapon the game's way (the game's matrix: its drawn size, offset
    // and turn) plus CE's recoil kick and muzzle jump (what CE animates when it fires), and hands it to IsoRim (Api.DrawMesh):
    // the pose and barrel end come out as without CE, with CE's recoil on top. CE's sprite tuning for its top-down view
    // (GunDrawExtension DrawSize, DrawOffset) is left out: IsoRim fits its model or solid sprite to the game's picture. The
    // casing point CE keeps for its ejected casings is set as CE sets it. Platform weapons (CE's WeaponPlatform: parts drawn
    // by CE itself) and the view off: CE's own helper.
    [HarmonyPatch]
    public static class HeldGuns
    {
        public static bool Enabled = true;
        public static int Drawn, LeftToCe;

        private static MethodBase target;
        private static AccessTools.FieldRef<Vector3> recoilOffset;
        private static AccessTools.FieldRef<float> muzzleJump;
        private static AccessTools.FieldRef<Vector3> casingDrawPos;
        private static readonly AccessTools.FieldRef<Verb_ShootCE, Vector3> verbDrawPos = AccessTools.FieldRefAccess<Verb_ShootCE, Vector3>("drawPos");
        private static readonly AccessTools.FieldRef<GunDrawExtension, Vector2> drawOffset = AccessTools.FieldRefAccess<GunDrawExtension, Vector2>("DrawOffset");
        private static readonly AccessTools.FieldRef<GunDrawExtension, Vector2> casingOffset = AccessTools.FieldRefAccess<GunDrawExtension, Vector2>("CasingOffset");

        public static bool Prepare()
        {
            if (target != null)
            {
                return true;
            }
            Type t = AccessTools.TypeByName("CombatExtended.HarmonyCE.Harmony_PawnRenderer_DrawEquipmentAiming");
            MethodInfo m = t != null ? AccessTools.Method(t, "DrawMesh") : null;
            FieldInfo rec = t != null ? AccessTools.Field(t, "recoilOffset") : null;
            FieldInfo jump = t != null ? AccessTools.Field(t, "muzzleJump") : null;
            FieldInfo casing = t != null ? AccessTools.Field(t, "casingDrawPos") : null;
            if (m == null || rec == null || jump == null || casing == null || m.GetParameters().Length != 7)
            {
                Log.Warning(Pack.Tag + "CE's held-weapon drawing (Harmony_PawnRenderer_DrawEquipmentAiming.DrawMesh) not found: held guns stay as CE draws them.");
                return false;
            }
            recoilOffset = AccessTools.StaticFieldRefAccess<Vector3>(rec);
            muzzleJump = AccessTools.StaticFieldRefAccess<float>(jump);
            casingDrawPos = AccessTools.StaticFieldRefAccess<Vector3>(casing);
            target = m;
            return true;
        }

        public static MethodBase TargetMethod() => target;

        // CE: DrawMesh(Mesh mesh, Matrix4x4 matrix, Material mat, int layer, Thing eq, Vector3 position, float aimAngle);
        // matrix is the game's, position its draw point (the game's recoil in), aimAngle the aim.
        public static bool Prefix(Mesh mesh, Matrix4x4 matrix, Material mat, int layer, Thing eq, Vector3 position, float aimAngle)
        {
            if (!Enabled || !IsoRim.Api.ViewReady || eq == null || eq is WeaponPlatform || eq.Graphic == null)
            {
                LeftToCe++;
                return true;
            }
            try
            {
                bool flip = aimAngle > 200f && aimAngle < 340f;
                float jump = muzzleJump();
                if (flip)
                {
                    jump = -jump;
                    muzzleJump() = jump;   // (as CE's helper leaves it)
                }
                float yaw = matrix.rotation.eulerAngles.y;
                // The casing point, as CE sets it (its ejected casings).
                CompEquippable equippable = eq.TryGetComp<CompEquippable>();
                if (equippable != null && equippable.PrimaryVerb is Verb_ShootCE shoot)
                {
                    GunDrawExtension ext = eq.def.GetModExtension<GunDrawExtension>();
                    Vector2 d = ext != null ? drawOffset(ext) : Vector2.zero;
                    Vector2 c = ext != null ? casingOffset(ext) : Vector2.zero;
                    Vector3 off = new Vector3(d.x + c.x, 0f, d.y + c.y);
                    if (flip)
                    {
                        off.x = -off.x;
                    }
                    verbDrawPos(shoot) = casingDrawPos() + off.RotatedBy(yaw);
                }
                Vector2 size = eq.Graphic.drawSize;
                Matrix4x4 m = Matrix4x4.TRS(position + recoilOffset(), Quaternion.AngleAxis(yaw + jump, Vector3.up), new Vector3(size.x, 0f, size.y));
                IsoRim.Api.DrawMesh(mesh, m, mat, layer);
                Drawn++;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Drawing a held CE weapon failed (drawn as CE draws it): " + e, 74120311);
                return true;
            }
        }
    }
}
