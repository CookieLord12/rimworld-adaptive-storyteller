using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace StorytellerTelemetry
{
    /// <summary>Read-only views of game state, turned into JSON.</summary>
    internal static class Snap
    {
        /// <summary>Storyteller, difficulty and the global factors that feed raid points.</summary>
        public static JsonObj Storyteller(bool includeFactions = true)
        {
            Storyteller st = Find.Storyteller;
            Difficulty d = st.difficulty;
            JsonObj o = new JsonObj()
                .S("def", st.def?.defName)
                .S("difficulty", st.difficultyDef?.defName)
                .N("threat_scale", d.threatScale)
                .B("allow_big_threats", d.allowBigThreats)
                .N("adaptation_effect_factor", d.adaptationEffectFactor)
                .N("adaptation_growth_factor", d.adaptationGrowthRateFactorOverZero)
                .N("enemy_death_on_downed_factor", d.enemyDeathOnDownedChanceFactor)
                .N("colonist_mood_offset", d.colonistMoodOffset)
                .B("fixed_wealth_mode", d.fixedWealthMode)
                .N("adapt_days", Find.StoryWatcher.watcherAdaptation.AdaptDays)
                .N("adapt_points_factor", Find.StoryWatcher.watcherAdaptation.TotalThreatPointsFactor)
                .N("time_points_factor", st.def.pointsFactorFromDaysPassed.Evaluate(GenDate.DaysPassedSinceSettle))
                .N("pop_adapt_days", Find.StoryWatcher.watcherPopAdaptation.AdaptDays)
                .N("population_intent", StorytellerUtilityPopulation.PopulationIntent)
                .I("free_colonists_total", PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count())
                .I("free_colonists_off_map", ColonistsOffMap())
                .I("caravans", Find.WorldObjects.Caravans.Count)
                .I("incident_queue", st.incidentQueue.Count)
                .I("active_quests", Find.QuestManager.ActiveQuestsListForReading.Count)
                .I("research_finished", DefDatabase<ResearchProjectDef>.AllDefsListForReading.Count(r => r.IsFinished))
                .S("player_tech_level", Faction.OfPlayer.def.techLevel.ToString());

            if (!includeFactions)
            {
                return o;
            }
            List<JsonObj> factions = new List<JsonObj>();
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
            {
                if (f.IsPlayer || f.Hidden || f.temporary)
                {
                    continue;
                }
                factions.Add(new JsonObj()
                    .S("def", f.def.defName)
                    .S("relation", f.PlayerRelationKind.ToString())
                    .I("goodwill", f.PlayerGoodwill)
                    .B("defeated", f.defeated));
            }
            o.Objs("factions", factions);
            return o;
        }

        /// <summary>Free colonists that are not on any map (caravans, transport pods).</summary>
        private static int ColonistsOffMap()
        {
            int onMaps = 0;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                onMaps += maps[i].mapPawns.FreeColonistsCount;
            }
            return Math.Max(0, PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count() - onMaps);
        }

        /// <summary>Quick colonist condition check used for recovery tracking.</summary>
        public static void Vitals(Map map, out int needTend, out int downed, out float healthAvg, out float moodAvg)
        {
            List<Pawn> cols = map.mapPawns.FreeColonistsSpawned;
            needTend = 0;
            downed = 0;
            float h = 0f, m = 0f;
            int mn = 0;
            foreach (Pawn p in cols)
            {
                if (p.Downed) downed++;
                if (p.health.HasHediffsNeedingTend()) needTend++;
                h += p.health.summaryHealth.SummaryHealthPercent;
                if (p.needs?.mood != null)
                {
                    m += p.needs.mood.CurLevelPercentage;
                    mn++;
                }
            }
            healthAvg = cols.Count > 0 ? h / cols.Count : 1f;
            moodAvg = mn > 0 ? m / mn : 1f;
        }

        public static int SickColonists(Map map)
        {
            int sick = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.health.hediffSet.HasImmunizableNotImmuneHediff()) sick++;
            }
            return sick;
        }

        public static int PermanentInjuries(Pawn p)
        {
            int n = 0;
            List<Hediff> hs = p.health.hediffSet.hediffs;
            for (int i = 0; i < hs.Count; i++)
            {
                if (hs[i].IsPermanent()) n++;
            }
            return n;
        }

        public static int MissingParts(Pawn p)
        {
            return p.health.hediffSet.GetMissingPartsCommonAncestors().Count;
        }

        /// <summary>Per colonist: (permanent injuries, missing parts). Compared before and after a fight.</summary>
        public static Dictionary<int, KeyValuePair<int, int>> InjuryState(Map map)
        {
            Dictionary<int, KeyValuePair<int, int>> d = new Dictionary<int, KeyValuePair<int, int>>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                d[p.thingIDNumber] = new KeyValuePair<int, int>(PermanentInjuries(p), MissingParts(p));
            }
            return d;
        }

        public static int HostilesInHome(Map map, List<Pawn> hostiles)
        {
            Area home = map.areaManager.Home;
            int n = 0;
            for (int i = 0; i < hostiles.Count; i++)
            {
                if (home[hostiles[i].Position]) n++;
            }
            return n;
        }

        /// <summary>One entry per colonist, so the model can see who matters rather than only averages.</summary>
        private static List<JsonObj> ColonistDetails(Map map)
        {
            List<JsonObj> list = new List<JsonObj>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                JsonObj o = new JsonObj()
                    .I("id", p.thingIDNumber)
                    .N("age", p.ageTracker.AgeBiologicalYearsFloat)
                    .S("xenotype", p.genes?.Xenotype?.defName)
                    .S("stage", p.DevelopmentalStage.ToString())
                    .B("dependent", IsDependent(p))
                    .B("slave", p.IsSlaveOfColony)
                    .B("downed", p.Downed)
                    .B("capable_of_violence", !p.WorkTagIsDisabled(WorkTags.Violent))
                    .N("health", p.health.summaryHealth.SummaryHealthPercent)
                    .N("mood", p.needs?.mood != null ? p.needs.mood.CurLevelPercentage : double.NaN)
                    .N("consciousness", p.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness))
                    .N("moving", p.health.capacities.GetLevel(PawnCapacityDefOf.Moving))
                    .N("manipulation", p.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation))
                    .N("sight", p.health.capacities.GetLevel(PawnCapacityDefOf.Sight))
                    .I("bionics", p.health.hediffSet.CountAddedAndImplantedParts())
                    .I("permanent_injuries", PermanentInjuries(p))
                    .I("missing_parts", MissingParts(p))
                    .I("psylink", p.GetPsylinkLevel())
                    .S("weapon", p.equipment?.Primary?.def.defName)
                    .N("armor_sharp", p.GetStatValue(StatDefOf.ArmorRating_Sharp))
                    .N("market_value", p.MarketValue);
                List<string> traits = new List<string>();
                if (p.story?.traits != null)
                {
                    foreach (Trait t in p.story.traits.allTraits)
                    {
                        traits.Add(t.Degree != 0 ? t.def.defName + ":" + t.Degree : t.def.defName);
                    }
                }
                o.Strs("traits", traits);
                o.Objs("diseases", Diseases(p, out _, out _));
                Dictionary<string, int> skills = new Dictionary<string, int>();
                if (p.skills != null)
                {
                    foreach (SkillRecord s in p.skills.skills)
                    {
                        skills[s.def.defName] = s.TotallyDisabled ? -1 : s.Level;
                    }
                }
                o.Counts("skills", skills);
                list.Add(o);
            }
            return list;
        }

        /// <summary>How defensible the base is: roof cover, how enclosed it is, walls and doors.</summary>
        private static JsonObj Base(Map map)
        {
            int cells = 0, roofed = 0, thick = 0;
            List<IntVec3> standable = new List<IntVec3>();
            foreach (IntVec3 c in map.areaManager.Home.ActiveCells)
            {
                cells++;
                RoofDef r = map.roofGrid.RoofAt(c);
                if (r != null)
                {
                    roofed++;
                    if (r.isThickRoof) thick++;
                }
                if (c.Standable(map))
                {
                    standable.Add(c);
                }
            }
            // Share of the home area an attacker can walk into from the map edge without opening a door. 0 = fully walled in.
            int sampled = 0, open = 0;
            int step = Math.Max(1, standable.Count / 120);
            TraverseParms tp = TraverseParms.For(TraverseMode.NoPassClosedDoors);
            for (int i = 0; i < standable.Count; i += step)
            {
                sampled++;
                if (map.reachability.CanReachMapEdge(standable[i], tp)) open++;
            }
            int doors = 0, walls = 0, beds = 0;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                Building b = all[i];
                if (b.def.IsDoor) doors++;
                else if (b.def.IsWall) walls++;
                else if (b is Building_Bed) beds++;
            }
            return new JsonObj()
                .I("home_cells", cells)
                .N("home_roofed_share", cells > 0 ? (double)roofed / cells : double.NaN)
                .N("home_mountain_roof_share", cells > 0 ? (double)thick / cells : double.NaN)
                .N("home_open_to_edge_share", sampled > 0 ? (double)open / sampled : double.NaN)
                .I("walls", walls)
                .I("doors", doors)
                .I("beds", beds);
        }

        /// <summary>Non-combat pressure: food runway, sickness, power.</summary>
        private static JsonObj Pressure(Map map)
        {
            int eaters = map.mapPawns.FreeColonistsSpawnedCount + map.mapPawns.PrisonersOfColonySpawnedCount;
            int sick = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.health.hediffSet.HasImmunizableNotImmuneHediff()) sick++;
            }
            float gain = 0f, stored = 0f;
            foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
            {
                gain += net.CurrentEnergyGainRate();
                stored += net.CurrentStoredEnergy();
            }
            return new JsonObj()
                .N("food_days", eaters > 0 ? map.resourceCounter.TotalHumanEdibleNutrition / (eaters * 1.6f) : double.NaN)
                .I("sick_colonists", sick)
                .N("power_net_watts", gain / CompPower.WattsToWattDaysPerTick)
                .N("power_stored_wd", stored)
                .I("fires", map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count);
        }

        /// <summary>A colonist who cannot get themselves out of danger: a baby, or someone who can barely move.</summary>
        public static bool IsDependent(Pawn p)
        {
            return p.DevelopmentalStage.Baby() || p.health.capacities.GetLevel(PawnCapacityDefOf.Moving) < 0.3f;
        }

        public static bool IsAble(Pawn p)
        {
            return !p.Dead && !p.Downed && !p.InMentalState && !IsDependent(p) && !p.DevelopmentalStage.Juvenile();
        }

        public static int Fires(Map map, out int inHome, out IntVec3 homeCentroid)
        {
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            Area home = map.areaManager.Home;
            inHome = 0;
            long sx = 0, sz = 0;
            for (int i = 0; i < fires.Count; i++)
            {
                IntVec3 c = fires[i].Position;
                if (c.InBounds(map) && home[c])
                {
                    inHome++;
                    sx += c.x;
                    sz += c.z;
                }
            }
            homeCentroid = inHome > 0 ? new IntVec3((int)(sx / inHome), 0, (int)(sz / inHome)) : IntVec3.Invalid;
            return fires.Count;
        }

        /// <summary>Diseases and other conditions that can kill on their own, with how the immunity race is going.</summary>
        public static List<JsonObj> Diseases(Pawn p, out float worstSeverity, out bool losing)
        {
            List<JsonObj> list = new List<JsonObj>();
            worstSeverity = 0f;
            losing = false;
            List<Hediff> hs = p.health.hediffSet.hediffs;
            for (int i = 0; i < hs.Count; i++)
            {
                Hediff h = hs[i];
                HediffComp_Immunizable imm = h.TryGetComp<HediffComp_Immunizable>();
                if (imm == null && (h.def.lethalSeverity <= 0f || h is Hediff_Injury))
                {
                    continue;
                }
                float lethal = h.def.lethalSeverity > 0f ? h.def.lethalSeverity : 1f;
                float sev = h.Severity / lethal;
                JsonObj o = new JsonObj().S("def", h.def.defName).N("severity", sev).B("tended", h.IsTended()).B("chronic", h.def.chronic);
                if (h.def.chronic)
                {
                    list.Add(o); // a standing condition (bad heart, carcinoma), not an acute emergency
                    continue;
                }
                if (imm != null)
                {
                    o.N("immunity", imm.Immunity);
                    if (imm.FullyImmune)
                    {
                        list.Add(o);
                        continue;
                    }
                    if (sev > imm.Immunity && sev > 0.3f) losing = true;
                }
                if (sev > worstSeverity) worstSeverity = sev;
                list.Add(o);
            }
            return list;
        }

        /// <summary>True when some colonist has a potentially lethal condition that is past the half-way mark or losing the immunity race.</summary>
        public static bool SeriousDisease(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Diseases(p, out float worst, out bool losing);
                if (worst >= 0.5f || losing) return true;
            }
            return false;
        }

        public static bool HasSevere(Map map, HediffDef def, float minSeverity)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Hediff h = p.health.hediffSet.GetFirstHediffOfDef(def);
                if (h != null && h.Severity >= minSeverity) return true;
            }
            return false;
        }

        public static bool AnyBerserk(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.InAggroMentalState) return true;
            }
            return false;
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<WeatherDecider, int> RainAllowedAgain =
            HarmonyLib.AccessTools.FieldRefAccess<WeatherDecider, int>("ticksWhenRainAllowedAgain");

        /// <summary>How little it would take to break the colony: who can act, who needs carrying, mood margin, fire risk.</summary>
        public static JsonObj Fragility(Map map)
        {
            int able = 0, dependents = 0, doctors = 0, belowMinor = 0, belowMajor = 0, belowExtreme = 0;
            float minMargin = 1f;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (IsDependent(p) || p.DevelopmentalStage.Juvenile()) dependents++;
                if (IsAble(p))
                {
                    able++;
                    if (p.skills != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && p.skills.GetSkill(SkillDefOf.Medicine).Level >= 6) doctors++;
                }
                if (p.needs?.mood != null && p.mindState?.mentalBreaker != null && !p.DevelopmentalStage.Baby())
                {
                    float mood = p.needs.mood.CurLevel;
                    float minor = p.mindState.mentalBreaker.BreakThresholdMinor;
                    if (mood < minor) belowMinor++;
                    if (mood < p.mindState.mentalBreaker.BreakThresholdMajor) belowMajor++;
                    if (mood < p.mindState.mentalBreaker.BreakThresholdExtreme) belowExtreme++;
                    if (mood - minor < minMargin) minMargin = mood - minor;
                }
            }
            int buildings = 0, flammable = 0, walls = 0, flammableWalls = 0;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                Building b = all[i];
                bool burns = b.GetStatValue(StatDefOf.Flammability) > 0f;
                buildings++;
                if (burns) flammable++;
                if (b.def.IsWall)
                {
                    walls++;
                    if (burns) flammableWalls++;
                }
            }
            int fires = Fires(map, out int homeFires, out _);
            return new JsonObj()
                .I("able_adults", able)
                .I("dependents", dependents)
                .I("able_doctors", doctors)
                .I("below_minor_break", belowMinor)
                .I("below_major_break", belowMajor)
                .I("below_extreme_break", belowExtreme)
                .N("min_mood_margin", minMargin)
                .N("flammable_building_share", buildings > 0 ? (double)flammable / buildings : double.NaN)
                .N("flammable_wall_share", walls > 0 ? (double)flammableWalls / walls : double.NaN)
                .N("rain_rate", map.weatherManager.RainRate)
                .I("fires", fires)
                .I("fires_in_home", homeFires)
                // The game's own relief valve: rain becomes far more likely during a large fire, unless something (a flashstorm) has blocked rain.
                .B("large_fire_danger", map.fireWatcher.LargeFireDangerPresent)
                .I("rain_blocked_ticks", Math.Max(0, RainAllowedAgain(map.weatherDecider) - Find.TickManager.TicksGame));
        }

        /// <summary>Where things are: camera, colonists, fire and hostiles. Lets analysis tell what the player was looking at.</summary>
        public static JsonObj Positions(Map map)
        {
            JsonObj o = new JsonObj();
            bool viewing = Find.CurrentMap == map;
            o.B("viewing_this_map", viewing);
            if (viewing)
            {
                IntVec3 cam = Find.CameraDriver.MapPosition;
                CellRect view = Find.CameraDriver.CurrentViewRect;
                o.Ints("camera", new[] { cam.x, cam.z }).Ints("view_size", new[] { view.Width, view.Height });
            }
            List<JsonObj> cols = new List<JsonObj>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                cols.Add(new JsonObj().I("id", p.thingIDNumber).I("x", p.Position.x).I("z", p.Position.z).B("drafted", p.Drafted).B("able", IsAble(p)));
            }
            o.Objs("colonists", cols);
            Fires(map, out int homeFires, out IntVec3 fc);
            if (homeFires > 0)
            {
                o.Ints("home_fire_centroid", new[] { fc.x, fc.z });
            }
            List<Pawn> hostiles = ActiveHostiles(map);
            if (hostiles.Count > 0)
            {
                long sx = 0, sz = 0;
                foreach (Pawn h in hostiles)
                {
                    sx += h.Position.x;
                    sz += h.Position.z;
                }
                o.Ints("hostile_centroid", new[] { (int)(sx / hostiles.Count), (int)(sz / hostiles.Count) });
            }
            return o;
        }

        /// <summary>Why and in what situation a pawn went down or died.</summary>
        public static void DeathContext(JsonObj o, Pawn p, Map map, Hediff culprit)
        {
            o.N("age", p.ageTracker.AgeBiologicalYearsFloat)
                .S("stage", p.DevelopmentalStage.ToString())
                .B("burning", p.IsBurning())
                .B("in_bed", p.InBed());
            if (culprit != null)
            {
                o.S("cause_hediff", culprit.def.defName);
            }
            Dictionary<string, int> conditions = new Dictionary<string, int>();
            foreach (Hediff h in p.health.hediffSet.hediffs)
            {
                if (h.def.isBad && !h.IsPermanent() && !(h is Hediff_MissingPart))
                {
                    JsonObj.Bump(conditions, h.def.defName);
                }
            }
            o.Counts("conditions", conditions);
            if (map != null && p.PositionHeld.InBounds(map))
            {
                float best = -1f;
                foreach (Pawn c in map.mapPawns.FreeColonistsSpawned)
                {
                    if (c == p || !IsAble(c)) continue;
                    float d = c.Position.DistanceTo(p.PositionHeld);
                    if (best < 0f || d < best) best = d;
                }
                o.N("nearest_able_colonist_distance", best);
            }
        }

        /// <summary>Base threat points for a target, without disturbing the game's random number stream.</summary>
        public static float ThreatPoints(IIncidentTarget target)
        {
            Rand.PushState();
            try
            {
                return StorytellerUtility.DefaultThreatPointsNow(target);
            }
            finally
            {
                Rand.PopState();
            }
        }

        public static string Side(Pawn p)
        {
            try
            {
                if (p.IsColonist)
                {
                    // A colonist on a violent break is a threat to the colony, not part of its defence.
                    return !p.Dead && p.InAggroMentalState ? "berserk" : "colonist";
                }
                if (p.IsPrisonerOfColony)
                {
                    return "prisoner";
                }
                if (p.Faction != null && p.Faction == Faction.OfPlayer)
                {
                    return "colony";
                }
                if (p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer))
                {
                    return "hostile";
                }
                if (!p.Dead && p.HostileTo(Faction.OfPlayer))
                {
                    return "hostile";
                }
            }
            catch (Exception)
            {
            }
            return "neutral";
        }

        public static string RaceKind(Pawn p)
        {
            if (p.RaceProps.Humanlike)
            {
                return "humanlike";
            }
            if (p.RaceProps.IsMechanoid)
            {
                return "mech";
            }
            if (p.IsAnimal)
            {
                return "animal";
            }
            return "other";
        }

        /// <summary>Composition of a group of pawns (a raid, a manhunter pack...).</summary>
        public static JsonObj PawnGroup(IEnumerable<Pawn> pawns)
        {
            int count = 0;
            float power = 0f;
            Dictionary<string, int> kinds = new Dictionary<string, int>();
            Dictionary<string, int> weapons = new Dictionary<string, int>();
            Dictionary<string, int> races = new Dictionary<string, int>();
            foreach (Pawn p in pawns)
            {
                if (p == null)
                {
                    continue;
                }
                count++;
                power += p.kindDef?.combatPower ?? 0f;
                JsonObj.Bump(kinds, p.kindDef?.defName);
                JsonObj.Bump(races, RaceKind(p));
                JsonObj.Bump(weapons, p.equipment?.Primary?.def.defName);
            }
            return new JsonObj()
                .I("count", count)
                .N("combat_power", power)
                .Counts("race_kinds", races)
                .Counts("kinds", kinds)
                .Counts("weapons", weapons);
        }

        public static List<Pawn> ActiveHostiles(Map map)
        {
            List<Pawn> list = new List<Pawn>();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (!p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer))
                {
                    list.Add(p);
                }
            }
            return list;
        }

        public static JsonObj Hostiles(Map map, out int count, out float power)
        {
            List<Pawn> hostiles = ActiveHostiles(map);
            Dictionary<string, int> factions = new Dictionary<string, int>();
            count = hostiles.Count;
            power = 0f;
            foreach (Pawn p in hostiles)
            {
                power += p.kindDef?.combatPower ?? 0f;
                JsonObj.Bump(factions, p.Faction?.def.defName ?? "none");
            }
            return new JsonObj()
                .I("count", count)
                .N("combat_power", power)
                .Counts("by_faction", factions)
                .I("in_home_area", HostilesInHome(map, hostiles))
                .B("active_threat", GenHostility.AnyHostileActiveThreatToPlayer(map));
        }

        private static JsonObj Colonists(Map map)
        {
            List<Pawn> cols = map.mapPawns.FreeColonistsSpawned;
            int n = cols.Count;
            int downed = 0, drafted = 0, mental = 0, needTend = 0, ranged = 0, melee = 0, unarmed = 0, children = 0, violent = 0;
            float moodSum = 0f, moodMin = 1f, healthSum = 0f, healthMin = 1f, bleed = 0f, pain = 0f;
            float shootSum = 0f, meleeSum = 0f, armorSum = 0f, weaponValue = 0f;
            int shootMax = 0, meleeMax = 0, moodN = 0, skillN = 0;
            foreach (Pawn p in cols)
            {
                if (p.Downed) downed++;
                if (p.Drafted) drafted++;
                if (p.InMentalState) mental++;
                if (p.health.HasHediffsNeedingTend()) needTend++;
                if (!p.ageTracker.Adult) children++;
                if (!p.WorkTagIsDisabled(WorkTags.Violent)) violent++;

                float h = p.health.summaryHealth.SummaryHealthPercent;
                healthSum += h;
                if (h < healthMin) healthMin = h;
                bleed += p.health.hediffSet.BleedRateTotal;
                pain += p.health.hediffSet.PainTotal;

                if (p.needs?.mood != null)
                {
                    float m = p.needs.mood.CurLevelPercentage;
                    moodSum += m;
                    moodN++;
                    if (m < moodMin) moodMin = m;
                }
                if (p.skills != null)
                {
                    int s = p.skills.GetSkill(SkillDefOf.Shooting).Level;
                    int ml = p.skills.GetSkill(SkillDefOf.Melee).Level;
                    shootSum += s;
                    meleeSum += ml;
                    skillN++;
                    if (s > shootMax) shootMax = s;
                    if (ml > meleeMax) meleeMax = ml;
                }
                ThingWithComps w = p.equipment?.Primary;
                if (w == null)
                {
                    unarmed++;
                }
                else
                {
                    weaponValue += w.MarketValue;
                    if (w.def.IsRangedWeapon) ranged++;
                    else melee++;
                }
                armorSum += p.GetStatValue(StatDefOf.ArmorRating_Sharp);
            }
            return new JsonObj()
                .I("count", n)
                .I("children", children)
                .I("capable_of_violence", violent)
                .I("downed", downed)
                .I("drafted", drafted)
                .I("in_mental_state", mental)
                .I("need_tending", needTend)
                .N("mood_avg", moodN > 0 ? moodSum / moodN : double.NaN)
                .N("mood_min", moodN > 0 ? moodMin : double.NaN)
                .N("health_avg", n > 0 ? healthSum / n : double.NaN)
                .N("health_min", n > 0 ? healthMin : double.NaN)
                .N("bleed_rate_total", bleed)
                .N("pain_avg", n > 0 ? pain / n : double.NaN)
                .N("shooting_avg", skillN > 0 ? shootSum / skillN : double.NaN)
                .I("shooting_max", shootMax)
                .N("melee_avg", skillN > 0 ? meleeSum / skillN : double.NaN)
                .I("melee_max", meleeMax)
                .I("armed_ranged", ranged)
                .I("armed_melee", melee)
                .I("unarmed", unarmed)
                .N("weapon_value_total", weaponValue)
                .N("armor_sharp_avg", n > 0 ? armorSum / n : double.NaN)
                .I("prisoners", map.mapPawns.PrisonersOfColonySpawnedCount)
                .I("slaves", map.mapPawns.SlavesOfColonySpawned.Count)
                .I("animals", map.mapPawns.SpawnedColonyAnimals.Count)
                .I("mechs", map.mapPawns.SpawnedColonyMechs.Count);
        }

        private static JsonObj Defense(Map map)
        {
            int turrets = 0, traps = 0, buildings = 0;
            Dictionary<string, int> turretDefs = new Dictionary<string, int>();
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                Building b = all[i];
                buildings++;
                if (b is Building_Turret)
                {
                    turrets++;
                    JsonObj.Bump(turretDefs, b.def.defName);
                }
                else if (b is Building_Trap)
                {
                    traps++;
                }
            }
            return new JsonObj()
                .I("buildings", buildings)
                .I("turrets", turrets)
                .I("traps", traps)
                .Counts("turret_defs", turretDefs)
                .I("home_area_cells", map.areaManager.Home.TrueCount);
        }

        private static JsonObj Resources(Map map)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (KeyValuePair<ThingDef, int> kv in map.resourceCounter.AllCountedAmounts)
            {
                if (kv.Value > 0)
                {
                    counts[kv.Key.defName] = kv.Value;
                }
            }
            return new JsonObj()
                .I("silver", map.resourceCounter.Silver)
                .N("food_nutrition", map.resourceCounter.TotalHumanEdibleNutrition)
                .I("medicine", map.resourceCounter.GetCountIn(ThingRequestGroup.Medicine))
                .Counts("counted", counts);
        }

        /// <summary>Full state of one map, as a "snapshot" record body.</summary>
        public static JsonObj MapSnapshot(Map map, string reason)
        {
            JsonObj o = Telemetry.Rec("snapshot")
                .S("reason", reason)
                .I("map", map.uniqueID)
                .B("player_home", map.IsPlayerHome)
                .N("map_age_days", map.AgeInDays);

            // Faction relations change slowly; leave them out of the high-frequency combat snapshots.
            bool light = reason == "combat" || reason == "crisis";
            o.O("storyteller", Storyteller(!light));

            WealthWatcher w = map.wealthWatcher;
            o.O("wealth", new JsonObj()
                .N("total", w.WealthTotal)
                .N("items", w.WealthItems)
                .N("buildings", w.WealthBuildings)
                .N("floors", w.WealthFloorsOnly)
                .N("pawns", w.WealthPawns)
                .N("for_storyteller", map.PlayerWealthForStoryteller));

            o.O("threat", new JsonObj()
                .N("default_points", ThreatPoints(map))
                .N("progress_score", StorytellerUtility.GetProgressScore(map))
                .S("danger_rating", map.dangerWatcher.DangerRating.ToString())
                .N("fire_danger", map.fireWatcher.FireDanger)
                .N("days_since_big_threat", (Find.TickManager.TicksGame - map.storyState.LastThreatBigTick) / 60000f));

            o.O("colonists", Colonists(map));
            o.O("defense", Defense(map));
            o.O("hostiles", Hostiles(map, out _, out _));
            o.O("resources", Resources(map));
            o.O("pressure", Pressure(map));
            o.O("fragility", Fragility(map));
            o.O("positions", Positions(map));
            if (!light)
            {
                // Slower-changing and bulkier detail; skipped in the high-frequency combat snapshots.
                o.O("base", Base(map));
                o.Objs("colonist_details", ColonistDetails(map));
            }

            List<string> conditions = new List<string>();
            foreach (GameCondition c in map.gameConditionManager.ActiveConditions)
            {
                conditions.Add(c.def.defName);
            }
            o.O("env", new JsonObj()
                .S("biome", map.Biome?.defName)
                .S("season", GenLocalDate.Season(map).ToString())
                .I("hour", GenLocalDate.HourOfDay(map))
                .N("outdoor_temp", map.mapTemperature.OutdoorTemp)
                .S("weather", map.weatherManager.curWeather?.defName)
                .Strs("conditions", conditions));
            return o;
        }

        public static string TargetType(IIncidentTarget target)
        {
            if (target is Map)
            {
                return "map";
            }
            if (target is Caravan)
            {
                return "caravan";
            }
            if (target is World)
            {
                return "world";
            }
            return target?.GetType().Name;
        }
    }
}
