using System;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // Test harness only: dormant unless the game is started with -cepackprobe. IsoRim's own harness probe (IsoRimProbe.txt)
    // drives the scene; a probe token "cepack:<cmd>:<args>" is unknown to it, so it logs "[IsoRim probe] unknown key
    // cepack:..." and goes on with the line. That warning is caught here (Verse.Log.Warning postfix) and the command run at
    // once, in order with the probe's other commands (ticks, bursts):
    //   cepack:off / cepack:on       the pack's drawing off (CE draws) / on, for before-and-after frames in one run
    //   cepack:ammo:x:z              the CE turret in the cell gets a full magazine of its selected ammo
    //   cepack:order:x:z:tx:tz       the CE turret in the cell is ordered to attack the cell (the player's gizmo: OrderAttack)
    //   cepack:stats                 counters, the floor layout and every CE projectile in flight with its drawn point
    // Every flight's start (muzzle against CE's launch point) is logged while the harness is on.
    [StaticConstructorOnStartup]
    public static class Harness
    {
        public static bool On;

        static Harness()
        {
            if (!GenCommandLine.CommandLineArgPassed("cepackprobe"))
            {
                return;
            }
            On = true;
            new Harmony("peren.isorimwaylith.cepack.harness").Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(Harness), nameof(Warning_Postfix)));
            Log.Message(Pack.Tag + "harness on");
        }

        private const string Key = "[IsoRim probe] unknown key cepack:";
        private static bool running;

        public static void Warning_Postfix(string text)
        {
            if (running || text == null || !text.StartsWith(Key))
            {
                return;
            }
            running = true;
            try
            {
                Run(text.Substring(Key.Length).Split(':'));
            }
            catch (Exception e)
            {
                Log.Message(Pack.Tag + "probe failed: " + e);
            }
            finally
            {
                running = false;
            }
        }

        private static int I(string s) => int.Parse(s);

        private static void Run(string[] a)
        {
            Map map = Find.CurrentMap;
            switch (a[0])
            {
                case "off":
                case "on":
                    ProjectileCE_DrawAt.Enabled = a[0] == "on";
                    Log.Message(Pack.Tag + "probe drawing " + a[0]);
                    break;
                case "ammo":
                {
                    Building_TurretGunCE t = new IntVec3(I(a[1]), 0, I(a[2])).GetFirstBuilding(map) as Building_TurretGunCE;
                    CompAmmoUser ammo = t?.CompAmmo;
                    if (ammo == null)
                    {
                        Log.Message(Pack.Tag + "probe ammo: no CE turret with ammo at " + a[1] + "," + a[2]);
                        break;
                    }
                    ammo.ResetAmmoCount(ammo.SelectedAmmo);
                    Log.Message(Pack.Tag + "probe ammo: " + t.def.defName + " " + ammo.CurrentAmmo?.defName + " x" + ammo.CurMagCount);
                    break;
                }
                case "order":
                {
                    Building_TurretGunCE t = new IntVec3(I(a[1]), 0, I(a[2])).GetFirstBuilding(map) as Building_TurretGunCE;
                    if (t == null)
                    {
                        Log.Message(Pack.Tag + "probe order: no CE turret at " + a[1] + "," + a[2]);
                        break;
                    }
                    IntVec3 tc = new IntVec3(I(a[3]), 0, I(a[4]));
                    t.OrderAttack(new LocalTargetInfo(tc));
                    Log.Message(Pack.Tag + "probe order: " + t.def.defName + " -> " + tc + ", forced " + t.ForcedTarget + ", manned " + (t.TryGetComp<CompMannable>()?.MannedNow));
                    break;
                }
                case "tickfly":
                {
                    // tickfly:max:flightTicks[:defPart]: real game ticks, one at a time, until a CE projectile (defName containing
                    // defPart) has flown that many ticks (at most max ticks).
                    int max = I(a[1]), need = I(a[2]), done = 0;
                    string part = a.Length > 3 ? a[3] : "";
                    Func<ProjectileCE> found = () => map.listerThings.AllThings.OfType<ProjectileCE>()
                        .FirstOrDefault(p => p.FlightTicks >= need && p.def.defName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
                    while (done < max && found() == null)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();   // paused game: the drawn position follows the ticks
                    }
                    ProjectileCE hit = found();
                    Log.Message(Pack.Tag + "probe tickfly " + part + ": " + done + " ticks, " + (hit != null ? hit.def.defName + " " + ProjectileCE_DrawAt.Describe(hit) : "none flying"));
                    break;
                }
                case "tickdown":
                {
                    // tickdown:max:height[:defPart]: real game ticks until a CE projectile is coming down at or below height (CE units).
                    int max = I(a[1]), done = 0;
                    float below = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    string part = a.Length > 3 ? a[3] : "";
                    Func<ProjectileCE> found = () => map.listerThings.AllThings.OfType<ProjectileCE>()
                        .FirstOrDefault(p => p.FlightTicks > 2 && p.Height <= below && p.Height < ProjectileCE_DrawAt.LastHeight(p) && p.def.defName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
                    while (done < max && found() == null)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    ProjectileCE hit = found();
                    Log.Message(Pack.Tag + "probe tickdown " + part + ": " + done + " ticks, " + (hit != null ? hit.def.defName + " tick " + hit.FlightTicks + " " + ProjectileCE_DrawAt.Describe(hit) : "none coming down"));
                    break;
                }
                case "stats":
                {
                    string floors = "none";
                    if (IsoRim.Api.Floors != null && map != null)
                    {
                        float h;
                        int shift;
                        bool f1 = IsoRim.Api.Floors.Floor(map, 1, out h, out shift);
                        floors = "ground rows " + IsoRim.Api.Floors.GroundRows(map) + ", floor 1 " + f1 + " height " + h.ToString("0.00") + " shift " + shift;
                    }
                    Log.Message(Pack.Tag + "probe stats: drawn " + ProjectileCE_DrawAt.Drawn + " (lobbed " + ProjectileCE_DrawAt.DrawnLobbed + "), errors "
                        + ProjectileCE_DrawAt.Errors + ", cells per CE unit " + ProjectileCE_DrawAt.CellsPerUnit.ToString("0.000") + ", floors: " + floors);
                    foreach (Building_TurretGunCE t in map.listerThings.AllThings.OfType<Building_TurretGunCE>().ToList())
                    {
                        CompAmmoUser am = t.CompAmmo;
                        Log.Message(Pack.Tag + "probe turret " + t.def.defName + " " + t.Position + " active " + t.Active + " manned " + (t.mannableComp != null ? t.mannableComp.MannedNow.ToString() : "-")
                            + " forced " + t.ForcedTarget + " current " + t.currentTargetInt + " warmup " + t.burstWarmupTicksLeft + " cooldown " + t.burstCooldownTicksLeft
                            + " reloading " + t.isReloading + " verb " + (t.AttackVerb != null ? t.AttackVerb.state.ToString() : "-")
                            + " ammo " + (am != null ? "use " + am.UseAmmo + " mag " + am.CurMagCount + " canFire " + am.CanBeFiredNow : "-"));
                    }
                    foreach (Pawn cp in map.mapPawns.FreeColonistsSpawned)
                    {
                        Log.Message(Pack.Tag + "probe colonist " + cp.LabelShort + " " + cp.Position + " job " + (cp.CurJob != null ? cp.CurJob.def.defName + " -> " + cp.CurJob.targetA : "none")
                            + " weapon " + (cp.equipment?.Primary != null ? cp.equipment.Primary.def.defName : "-"));
                    }
                    foreach (ProjectileCE p in map.listerThings.AllThings.OfType<ProjectileCE>().ToList())
                    {
                        Log.Message(Pack.Tag + "probe flying " + p.def.defName + " (" + p.GetType().Name + ") tick " + p.FlightTicks + " " + ProjectileCE_DrawAt.Describe(p));
                    }
                    break;
                }
                default:
                    Log.Message(Pack.Tag + "probe: unknown " + string.Join(":", a));
                    break;
            }
        }
    }
}
