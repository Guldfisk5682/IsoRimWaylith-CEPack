using System;
using System.Collections.Generic;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimCEPack
{
    // A thrown grenade in flight (a CE projectile thrown by hand: Verb_ThrowGrenade, or a verb logged as a throw) shows the
    // thrown item's 3D model when IsoRim has one for it and shows item models: drawn with IsoRim's own item drawing
    // (Api.DrawItem, Api 40: the model as the item lies on the ground, by the player's item settings) at the projectile's
    // drawn point, turning as it flies. Anything else, or an older IsoRim: the flat card (ProjectileCE_DrawAt).
    public static class Grenades
    {
        public static bool Enabled = true;
        public static int Drawn;

        private static readonly Dictionary<ThingDef, Thing> items = new Dictionary<ThingDef, Thing>();
        private static bool apiChecked, apiOk;

        public static bool Draw(ProjectileCE p, Vector3 at, float yaw)
        {
            ThingDef gun = p.equipmentDef;
            if (!Enabled || gun == null || p.def.projectile.flyOverhead || !Api40())
            {
                return false;
            }
            if (!items.TryGetValue(gun, out Thing item))
            {
                item = Thrown(gun) && IsoRim.Api.HasModel(gun) ? MakeItem(gun) : null;
                items[gun] = item;
            }
            if (item == null || !IsoRim.Api.ModelsOn)
            {
                return false;
            }
            float spin = p.def.projectile.spinRate != 0f ? yaw : yaw + p.FlightTicks * 12f;   // tumbling as thrown
            if (!DrawItem(item, new Vector3(at.x, Mathf.Max(0f, at.y - 0.05f), at.z), spin))
            {
                return false;
            }
            Drawn++;
            return true;
        }

        private static bool Api40()
        {
            if (!apiChecked)
            {
                apiChecked = true;
                apiOk = IsoRim.Api.Version >= 40;
            }
            return apiOk;
        }

        // (separate so an IsoRim without Api 40 never loads the call)
        private static bool DrawItem(Thing item, Vector3 at, float yaw) => IsoRim.Api.DrawItem(item, at, yaw);

        // A stand-in item to draw, never spawned. Made without ThingMaker (no thing ID taken, no random numbers drawn: the
        // game's simulation is not touched by drawing).
        private static Thing MakeItem(ThingDef def)
        {
            if (def.category != ThingCategory.Item || def.thingClass == null)
            {
                return null;
            }
            Rand.PushState();
            try
            {
                Thing t = (Thing)Activator.CreateInstance(def.thingClass);
                t.def = def;
                if (def.MadeFromStuff)
                {
                    t.SetStuffDirect(GenStuff.DefaultStuffFor(def));
                }
                t.stackCount = 1;
                if (t is ThingWithComps twc)
                {
                    twc.InitializeComps();
                }
                return t;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Making the model of a thrown " + def.defName + " failed (shown as its card): " + e, 74120341);
                return null;
            }
            finally
            {
                Rand.PopState();
            }
        }

        // A weapon that is itself thrown: CE's grenade verb, or a verb the game logs as a throw.
        private static bool Thrown(ThingDef def)
        {
            if (def.Verbs == null)
            {
                return false;
            }
            foreach (VerbProperties v in def.Verbs)
            {
                if (ThrownVerb(def, v) || v.rangedFireRulepack?.defName == "Combat_RangedFire_Thrown")
                {
                    return true;
                }
            }
            return false;
        }

        // CE's hand-thrown weapons: its grenade verb, or a one-use verb of a weapon that stacks (grenades, molotovs; not a
        // disposable launcher).
        private static bool ThrownVerb(ThingDef def, VerbProperties v)
        {
            return v.verbClass != null && (typeof(Verb_ThrowGrenade).IsAssignableFrom(v.verbClass)
                || (typeof(Verb_ShootCEOneUse).IsAssignableFrom(v.verbClass) && def.stackLimit > 1 && def.category == ThingCategory.Item));
        }

        // CE's patch of the game's grenades (PatchOperationMakeGunCECompatible) gives them new verbs without the game's mark of
        // a throw (rangedFireRulepack Combat_RangedFire_Thrown, which also words the battle log "threw"); IsoRim reads that
        // mark to hold a thrown weapon in the hand and let it leave from there, as it does without CE. Given back to CE's
        // hand-thrown verbs that have no log rules of their own. At startup, once.
        public static int Marked;

        public static void MarkThrown()
        {
            RulePackDef thrown = DefDatabase<RulePackDef>.GetNamedSilentFail("Combat_RangedFire_Thrown");
            if (thrown == null)
            {
                return;
            }
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!def.IsWeapon || def.Verbs == null)
                {
                    continue;
                }
                foreach (VerbProperties v in def.Verbs)
                {
                    if (v.rangedFireRulepack == null && ThrownVerb(def, v))
                    {
                        v.rangedFireRulepack = thrown;
                        Marked++;
                    }
                }
            }
        }
    }
}
