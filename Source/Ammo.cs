using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Xml;
using CombatExtended;
using HarmonyLib;
using Verse;

namespace IsoRimCEPack
{
    // CE ammo models (Defs/CE_Ammo_Generic.xml, Defs/CE_Ammo_NonGeneric.xml, written by tools/make_ce_ammo_defs.js).
    //
    // Generic looks: CE's generic-ammo option maps every ammo def of a calibre set (an AmmoSetDef with similarTo) to the def
    // of the same ammoClass in the generic set (AmmoGeneralizer.GeneralizeAmmo). The pack gives each calibre def that
    // counterpart's look, always (whether the option is on or off), through IsoRim's model rules (Api 42): the rule answers
    // "R3D_CE_<look>" for an ammo def, the look keyed by the counterpart's CE texture folder (AmmoLooks.Generic).
    //  - A generic def itself: its own texture's look.
    //  - FuelCell/Large is one CE art for two looks: by ammoClass (GrenadeIncendiary: the incendiary look; else demolition).
    //  - A def in several calibre sets with different looks (.38 Special in Pistol and PistolMagnum sets ...): the look of
    //    the same family as its own texture folder (Pistol/* gets the pistol look, Revolver/* the revolver look).
    //  - A link whose ammoClass the generic set lacks (CE leaves it as it is): no answer when its own texture has a look of
    //    its own (graphicPath binds it then: 105mm HEAT, the APCR rounds); else (12.7x55mm HP) the generic set's FMJ look.
    // The mapping is read from the original set links: on the rule's first call or, if CE's generalizer runs first (generic
    // ammo option on), just before it rewrites the links (prefix below). Older IsoRim (no Api 42): no rule; the graphicPath
    // patch still gives things drawn with a generic CE texture that texture's look.
    public class CEPackMod : Mod
    {
        public static CEPackSettings Settings;

        public CEPackMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<CEPackSettings>();
            int v;
            try
            {
                v = IsoRim.Api.Version;
            }
            catch (Exception)
            {
                v = 0;
            }
            if (v < 42)
            {
                return;
            }
            try
            {
                AmmoRule.Register();
            }
            catch (Exception e)
            {
                Log.Error(Pack.Tag + "Ammo model rule not added (CE ammo keeps its texture-path models): " + e);
            }
        }

        public override string SettingsCategory() => Content.Name;

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("CEPack_CookOffSparks".Translate(), ref Settings.cookOffSparks, "CEPack_CookOffSparksTip".Translate());
            list.End();
        }
    }

    // The pack's settings. Scribe keys are never renamed or removed (a retired one is hidden, not deleted).
    public class CEPackSettings : ModSettings
    {
        public bool cookOffSparks = true;   // sparks, a flash and a puff of smoke over ammo that cooks off (Puffs)

        public override void ExposeData()
        {
            Scribe_Values.Look(ref cookOffSparks, "cookOffSparks", true);
        }
    }

    public static class AmmoRule
    {
        private static Dictionary<ThingDef, string> map;
        private static bool failed;

        // Counts for the harness (cepack:ammobind).
        internal static int Generic, Calibre, ByFolder, Fallback, Unmatched;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Register()
        {
            IsoRim.Api.AddModelRule(new Func<ThingDef, string>(Rule));
            new Harmony("peren.isorimwaylith.cepack.ammo").Patch(AccessTools.Method(typeof(AmmoGeneralizer), nameof(AmmoGeneralizer.GeneralizeAmmo)),
                prefix: new HarmonyMethod(typeof(AmmoRule), nameof(Generalizer_Prefix)));
        }

        public static void Generalizer_Prefix()
        {
            Build();
        }

        public static string Rule(ThingDef def)
        {
            if (!(def is AmmoDef))
            {
                return null;
            }
            Build();
            string name;
            return map.TryGetValue(def, out name) ? name : null;
        }

        internal static string Answer(ThingDef def)
        {
            Build();
            string name;
            return def != null && map.TryGetValue(def, out name) ? name : null;
        }

        private static string TexPath(ThingDef d)
        {
            string p = d?.graphicData?.texPath;
            return p == null ? null : p.Replace('\\', '/').Trim('/');
        }

        private static string Family(string texPath)   // "Things/Ammo/<family>/..."
        {
            if (texPath == null)
            {
                return null;
            }
            string[] s = texPath.Split('/');
            return s.Length > 2 ? s[2] : null;
        }

        private static string LookOf(AmmoDef d)
        {
            string p = TexPath(d);
            if (p == null)
            {
                return null;
            }
            if (string.Equals(p, AmmoLooks.FuelCellLarge, StringComparison.OrdinalIgnoreCase))
            {
                return d.ammoClass != null && d.ammoClass.defName == "GrenadeIncendiary" ? "fuelcell_inc" : "fuelcell_demo";
            }
            string look;
            return AmmoLooks.Generic.TryGetValue(p, out look) ? look : null;
        }

        private struct Candidate
        {
            public string look;
            public string family;   // the counterpart's texture family
        }

        private static void Build()
        {
            if (map != null)
            {
                return;
            }
            map = new Dictionary<ThingDef, string>();
            if (failed)
            {
                return;
            }
            try
            {
                Dictionary<AmmoDef, List<Candidate>> calibre = new Dictionary<AmmoDef, List<Candidate>>();
                Dictionary<AmmoDef, AmmoSetDef> unmatched = new Dictionary<AmmoDef, AmmoSetDef>();
                HashSet<AmmoDef> generic = new HashSet<AmmoDef>();
                foreach (AmmoSetDef set in DefDatabase<AmmoSetDef>.AllDefs)
                {
                    AmmoSetDef similar = set.similarTo;
                    if (similar == null || set.ammoTypes == null || similar.ammoTypes == null)
                    {
                        continue;
                    }
                    foreach (AmmoLink g in similar.ammoTypes)
                    {
                        if (g?.ammo != null)
                        {
                            generic.Add(g.ammo);
                        }
                    }
                    foreach (AmmoLink link in set.ammoTypes)
                    {
                        AmmoDef a = link?.ammo;
                        if (a == null)
                        {
                            continue;
                        }
                        AmmoLink cp = similar.ammoTypes.Find(x => x?.ammo != null && x.ammo.ammoClass == a.ammoClass);
                        string look = cp != null ? LookOf(cp.ammo) : null;
                        if (look == null)
                        {
                            if (cp == null && !unmatched.ContainsKey(a))
                            {
                                unmatched[a] = similar;
                            }
                            continue;
                        }
                        List<Candidate> list;
                        if (!calibre.TryGetValue(a, out list))
                        {
                            calibre[a] = list = new List<Candidate>();
                        }
                        list.Add(new Candidate { look = look, family = Family(TexPath(cp.ammo)) });
                    }
                }
                Generic = Calibre = ByFolder = Fallback = Unmatched = 0;
                foreach (AmmoDef g in generic)
                {
                    string look = LookOf(g);
                    if (look != null)
                    {
                        map[g] = "R3D_CE_" + look;
                        Generic++;
                    }
                }
                foreach (KeyValuePair<AmmoDef, List<Candidate>> kv in calibre)
                {
                    if (map.ContainsKey(kv.Key))
                    {
                        continue;   // a generic def that is also in a calibre set keeps its own look
                    }
                    List<Candidate> c = kv.Value;
                    string pick = c[0].look;
                    bool mixed = false;
                    for (int i = 1; i < c.Count; i++)
                    {
                        mixed |= c[i].look != pick;
                    }
                    if (mixed)
                    {
                        string own = Family(TexPath(kv.Key));
                        for (int i = 0; i < c.Count; i++)
                        {
                            if (string.Equals(c[i].family, own, StringComparison.OrdinalIgnoreCase))
                            {
                                pick = c[i].look;
                                break;
                            }
                        }
                        ByFolder++;
                    }
                    map[kv.Key] = "R3D_CE_" + pick;
                    Calibre++;
                }
                foreach (KeyValuePair<AmmoDef, AmmoSetDef> kv in unmatched)
                {
                    if (map.ContainsKey(kv.Key))
                    {
                        continue;   // matched in another set
                    }
                    string own = TexPath(kv.Key);
                    if (own != null && (AmmoLooks.Generic.ContainsKey(own) || AmmoLooks.NonGeneric.Contains(own)))
                    {
                        Unmatched++;   // its own CE texture has a look: graphicPath binds it
                        continue;
                    }
                    AmmoLink fmj = kv.Value.ammoTypes.Find(x => x?.ammo?.ammoClass != null && x.ammo.ammoClass.defName == "FullMetalJacket")
                        ?? (kv.Value.ammoTypes.Count > 0 ? kv.Value.ammoTypes[0] : null);
                    string look = fmj != null ? LookOf(fmj.ammo) : null;
                    if (look != null)
                    {
                        map[kv.Key] = "R3D_CE_" + look;
                        Fallback++;
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                map = new Dictionary<ThingDef, string>();
                Log.Error(Pack.Tag + "Reading CE's generic ammo table failed (CE ammo keeps its texture-path models): " + e);
            }
        }
    }

    // IsoRim's pattern for fields of newer Apis (Docs/Api.md, "Gating fields by version"): the operation runs only when IsoRim
    // is at least minVersion.
    public class PatchOperation_IfIsoRimApi : PatchOperation
    {
        public int minVersion;
        public PatchOperation apply;

        protected override bool ApplyWorker(XmlDocument xml)
        {
            int have;
            try
            {
                have = IsoRim.Api.Version;
            }
            catch (Exception)
            {
                have = 0;
            }
            return have < minVersion || apply == null || apply.Apply(xml);
        }
    }
}
