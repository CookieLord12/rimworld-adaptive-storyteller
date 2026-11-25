using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using Verse;

namespace StorytellerTelemetry
{
    /// <summary>Things that happened on a map during some window (between two snapshots, a fight, a crisis, an aftermath).</summary>
    internal sealed class Counters
    {
        public float colonistDamage;
        public float colonyOtherDamage;
        public float berserkDamage;
        public float hostileDamage;
        public float hostileDamageByColony;
        public int colonistsDowned;
        public int colonistsDied;
        public int colonistsKidnapped;
        public int colonyOtherDied;
        public int hostilesDowned;
        public int hostilesDied;
        public int hostilesKilledByColony;
        public int hostilesExited;
        public int prisonersTaken;
        public int mentalBreaks;
        public int buildingsDestroyed;
        public float buildingValueLost;
        public float stolenValue;
        public int firesByColony;
        public int firesByHostile;
        public int firesNatural;

        public JsonObj ToJson()
        {
            return new JsonObj()
                .N("colonist_damage_taken", colonistDamage)
                .N("colony_other_damage_taken", colonyOtherDamage)
                .N("berserk_colonist_damage_taken", berserkDamage)
                .N("hostile_damage_taken", hostileDamage)
                .N("hostile_damage_by_colony", hostileDamageByColony)
                .I("colonists_downed", colonistsDowned)
                .I("colonists_died", colonistsDied)
                .I("colonists_kidnapped", colonistsKidnapped)
                .I("colony_other_died", colonyOtherDied)
                .I("hostiles_downed", hostilesDowned)
                .I("hostiles_died", hostilesDied)
                .I("hostiles_killed_by_colony", hostilesKilledByColony)
                .I("hostiles_exited_map", hostilesExited)
                .I("prisoners_taken", prisonersTaken)
                .I("mental_breaks", mentalBreaks)
                .I("buildings_destroyed", buildingsDestroyed)
                .N("building_value_lost", buildingValueLost)
                .N("stolen_value", stolenValue)
                .I("fires_started_by_colony", firesByColony)
                .I("fires_started_by_hostiles", firesByHostile)
                .I("fires_started_naturally", firesNatural);
        }
    }

    /// <summary>
    /// A stretch of time in which something is seriously wrong on a map: a fight, a fire, a lethal disease,
    /// starvation, extreme temperature, a colonist death. Overlapping problems are one crisis, because what
    /// kills a colony is usually several of them at once.
    /// </summary>
    internal sealed class CrisisTrack
    {
        public int id;
        public int startTick;
        public int quietSince = -1;
        public List<string> factors = new List<string>();
        public List<JsonObj> factorTimeline = new List<JsonObj>();
        public int maxConcurrent;
        public List<int> engagementIds = new List<int>();
        public List<int> incidentSeqs = new List<int>();
        public List<string> incidentLabels = new List<string>();
        public Counters totals = new Counters();
        public int peakFires;
        public int peakHomeFires;
        public int peakDrafted;
        public float wealthStart;
        public int buildingsStart;
        public int colonistsStart;
        public float healthStart;
        public float moodStart;
        public int sickStart;
        public int downedStart;
        public double realSeconds;
        public double pausedSeconds;
        public int pauseCount;
        public bool asked;
    }

    /// <summary>
    /// The aftermath of a crisis: follows the colony until it is stable again, because much of what makes an
    /// event hard (infections, long recoveries, mood crashes, breaks) only shows up afterwards.
    /// </summary>
    internal sealed class RecoveryTrack
    {
        public CrisisTrack crisis;
        public string title;
        public string summary;
        public int endTick;
        // Tick since which each condition has held without interruption; -1 while it does not hold.
        public int tendedSince = -1;
        public int downedSince = -1;
        public int healthSince = -1;
        public int moodSince = -1;
        public int sickSince = -1;
        public float minMood = 1f;
        public float minHealth = 1f;
        public int peakSick;
        public int peakNeedTending;
        public Counters during = new Counters();

        public int StableSince => tendedSince < 0 || downedSince < 0 || healthSince < 0 || moodSince < 0 || sickSince < 0
            ? -1
            : Math.Max(Math.Max(Math.Max(tendedSince, downedSince), Math.Max(healthSince, moodSince)), sickSince);
    }

    internal sealed class MapTrack
    {
        public int lastSnapshotTick = -1;
        public Counters interval = new Counters();

        public bool engaged;
        public bool internalFight;
        public int engagementId;
        public int engagementStartTick;
        public int quietChecks;
        public Counters engagement;
        public int peakHostiles;
        public float peakHostilePower;
        public int peakHostilesInHome;
        public int peakFires;
        public float wealthStart;
        public int colonistsStart;
        public Dictionary<int, KeyValuePair<int, int>> injuriesStart;
        public List<int> incidentSeqs = new List<int>();

        // How the player handled the fight.
        public double realSeconds;
        public double pausedSeconds;
        public int pauseCount;
        public Dictionary<string, int> msBySpeed;
        public int firstDraftTick;
        public int peakDrafted;

        public CrisisTrack crisis;
        public RecoveryTrack recovery;
        public int lastColonistDeathTick = -999999;

        /// <summary>Threat incidents fired on this map that no engagement has claimed yet.</summary>
        public List<(int seq, int tick)> pendingThreats = new List<(int seq, int tick)>();

        /// <summary>Every incident fired on this map in the last day, of any category: a crisis links to these.</summary>
        public List<(int seq, int tick, string label)> recentIncidents = new List<(int seq, int tick, string label)>();
    }

    /// <summary>
    /// Drives periodic snapshots and detects engagements (fights) and crises (anything seriously wrong).
    /// Saves nothing into the savegame, so the mod can be added or removed freely.
    /// </summary>
    public class TelemetryComponent : GameComponent
    {
        private const int CheckInterval = 50;
        private const int SlowCheckInterval = 250;
        private const int QuietChecksToEnd = 2;
        private const int LinkWindowTicks = 60000;
        private const int CrisisQuietTicks = 2500;
        private const int RecoveryTimeoutTicks = 300000;
        private const int StableHoldTicks = 2500;
        private const int HomeFireThreshold = 5;
        private const int MapFireThreshold = 50;

        internal static TelemetryComponent Instance;

        private readonly Game game;
        private readonly Dictionary<int, MapTrack> tracks = new Dictionary<int, MapTrack>();
        private readonly List<int> tmpRemove = new List<int>();
        private readonly List<string> tmpFactors = new List<string>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double lastUpdateSeconds;
        private bool wasPaused;
        private bool gameOverLogged;
        private bool incapacitated;
        private string lastStoryteller;
        private string lastDifficulty;
        private float lastThreatScale = -1f;

        public TelemetryComponent(Game game)
        {
            this.game = game;
            Instance = this;
        }

        internal static TelemetryComponent Active
        {
            get
            {
                TelemetryComponent c = Instance;
                return c != null && c.game == Current.Game ? c : null;
            }
        }

        public override void StartedNewGame()
        {
            Telemetry.initKind = "new";
        }

        public override void LoadedGame()
        {
            Telemetry.initKind = "load";
        }

        internal MapTrack Track(Map map)
        {
            if (!tracks.TryGetValue(map.uniqueID, out MapTrack t))
            {
                t = new MapTrack();
                tracks[map.uniqueID] = t;
            }
            return t;
        }

        /// <summary>Applies a counter change to every window currently open on the map.</summary>
        internal void Count(Map map, Action<Counters> change)
        {
            if (map == null)
            {
                return;
            }
            lock (tracks)
            {
                MapTrack t = Track(map);
                change(t.interval);
                if (t.engagement != null)
                {
                    change(t.engagement);
                }
                if (t.crisis != null)
                {
                    change(t.crisis.totals);
                }
                else if (t.recovery != null)
                {
                    change(t.recovery.during);
                }
            }
        }

        internal bool IsEngaged(Map map)
        {
            lock (tracks)
            {
                return map != null && tracks.TryGetValue(map.uniqueID, out MapTrack t) && t.engaged;
            }
        }

        /// <summary>Id of the crisis currently open on the map, or 0.</summary>
        internal int CrisisId(Map map)
        {
            lock (tracks)
            {
                return map != null && tracks.TryGetValue(map.uniqueID, out MapTrack t) && t.crisis != null ? t.crisis.id : 0;
            }
        }

        internal void NoteColonistDeath(Map map)
        {
            if (map == null)
            {
                return;
            }
            lock (tracks)
            {
                Track(map).lastColonistDeathTick = Find.TickManager.TicksGame;
            }
        }

        /// <summary>Called right before an incident executes: snapshot the map so every incident has its exact pre-state.</summary>
        internal void BeforeIncident(Map map, int seq, bool isThreat, string label)
        {
            lock (tracks)
            {
                int tick = Find.TickManager.TicksGame;
                MapTrack t = Track(map);
                WriteSnapshot(map, t, "incident", seq);
                t.recentIncidents.RemoveAll(i => tick - i.tick > LinkWindowTicks);
                t.recentIncidents.Add((seq, tick, label));
                if (t.crisis != null)
                {
                    t.crisis.incidentSeqs.Add(seq);
                    t.crisis.incidentLabels.Add(label);
                }
                if (isThreat)
                {
                    if (t.engaged)
                    {
                        t.incidentSeqs.Add(seq);
                    }
                    else
                    {
                        t.pendingThreats.Add((seq, tick));
                    }
                }
            }
        }

        /// <summary>Runs every frame, also while paused: measures real time, pausing and game speed during fights and crises.</summary>
        public override void GameComponentUpdate()
        {
            if (!Telemetry.Enabled || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                double now = clock.Elapsed.TotalSeconds;
                double dt = Math.Min(now - lastUpdateSeconds, 5.0);
                lastUpdateSeconds = now;
                bool paused = Find.TickManager.Paused;
                bool pauseStarted = paused && !wasPaused;
                wasPaused = paused;
                string speed = paused ? "Paused" : Find.TickManager.CurTimeSpeed.ToString();
                lock (tracks)
                {
                    foreach (MapTrack t in tracks.Values)
                    {
                        if (t.crisis != null)
                        {
                            t.crisis.realSeconds += dt;
                            if (paused) t.crisis.pausedSeconds += dt;
                            if (pauseStarted) t.crisis.pauseCount++;
                        }
                        if (!t.engaged)
                        {
                            continue;
                        }
                        t.realSeconds += dt;
                        if (paused) t.pausedSeconds += dt;
                        if (pauseStarted) t.pauseCount++;
                        JsonObj.Bump(t.msBySpeed, speed, (int)(dt * 1000.0));
                    }
                }
            }
            catch (Exception e)
            {
                Telemetry.Fail("update", e);
            }
        }

        public override void GameComponentTick()
        {
            if (!Telemetry.Enabled)
            {
                return;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick % CheckInterval != 0)
            {
                return;
            }
            try
            {
                lock (tracks)
                {
                    bool slow = tick % SlowCheckInterval == 0;
                    CheckStorytellerChanged();
                    CheckGameOver();
                    List<Map> maps = Find.Maps;
                    for (int i = 0; i < maps.Count; i++)
                    {
                        Map map = maps[i];
                        if (!map.IsPlayerHome && map.mapPawns.FreeColonistsSpawnedCount == 0)
                        {
                            continue;
                        }
                        MapTrack t = Track(map);
                        if (slow)
                        {
                            CheckEngagement(map, t, tick);
                            CheckCrisis(map, t, tick);
                            CheckRecovery(map, t, tick);
                        }
                        TelemetrySettings s = TelemetryMod.Settings;
                        int due = t.engaged ? s.combatSnapshotIntervalTicks
                            : t.crisis != null ? Math.Max(1000, s.combatSnapshotIntervalTicks)
                            : s.snapshotIntervalTicks;
                        if (t.lastSnapshotTick < 0 || tick - t.lastSnapshotTick >= due)
                        {
                            WriteSnapshot(map, t, t.engaged ? "combat" : t.crisis != null ? "crisis" : "interval", 0);
                        }
                    }
                    PruneRemovedMaps(maps, tick);
                    if (slow)
                    {
                        CheckIncapacitated(tick);
                    }
                }
            }
            catch (Exception e)
            {
                Telemetry.Fail("tick", e);
            }
        }

        private void CheckStorytellerChanged()
        {
            Storyteller st = Find.Storyteller;
            string def = st.def?.defName;
            string diff = st.difficultyDef?.defName;
            float scale = st.difficulty.threatScale;
            if (lastStoryteller != null && (def != lastStoryteller || diff != lastDifficulty || scale != lastThreatScale))
            {
                Telemetry.Write(Telemetry.Rec("storyteller_changed").O("storyteller", Snap.Storyteller()));
            }
            lastStoryteller = def;
            lastDifficulty = diff;
            lastThreatScale = scale;
        }

        private void CheckGameOver()
        {
            if (gameOverLogged || !Find.GameEnder.gameEnding)
            {
                return;
            }
            gameOverLogged = true;
            Telemetry.Write(Telemetry.Rec("game_over").O("storyteller", Snap.Storyteller()));
        }

        /// <summary>
        /// The game only declares game over when everyone is dead. A colony with nobody left standing is over
        /// in practice, so record that moment and ask for the verdict right away.
        /// </summary>
        internal void ColonistLost(Pawn pawn)
        {
            // Checked the moment a colonist goes down, before the game can send help (the man in black).
            lock (tracks)
            {
                CheckIncapacitated(Find.TickManager.TicksGame, pawn);
            }
        }

        private void CheckIncapacitated(int tick, Pawn excluding = null)
        {
            bool anyAble = false;
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
            {
                if (p != excluding && !p.Dead && !p.Downed && !p.DevelopmentalStage.Baby())
                {
                    anyAble = true;
                    break;
                }
            }
            if (anyAble)
            {
                incapacitated = false;
                return;
            }
            if (incapacitated)
            {
                return;
            }
            incapacitated = true;
            Telemetry.Write(Telemetry.Rec("colony_incapacitated").O("storyteller", Snap.Storyteller()));
            if (!TelemetryMod.Settings.askRating)
            {
                return;
            }
            bool asked = false;
            foreach (MapTrack t in tracks.Values)
            {
                CrisisTrack c = t.crisis ?? t.recovery?.crisis;
                if (c != null && !c.asked)
                {
                    c.asked = true;
                    asked = true;
                    Find.WindowStack.Add(new Dialog_RateFight(c, "Colony down: " + Title(c, tick), Summary(c.totals), tick - c.startTick, "colony_incapacitated"));
                }
            }
            if (!asked)
            {
                Find.WindowStack.Add(new Dialog_RateFight(null, "Colony down (day " + (tick / 60000f).ToString("0.#") + ")", "nobody is left standing", 0, "colony_incapacitated"));
            }
        }

        private void WriteSnapshot(Map map, MapTrack t, string reason, int incidentSeq)
        {
            int tick = Find.TickManager.TicksGame;
            JsonObj o = Snap.MapSnapshot(map, reason);
            if (incidentSeq > 0)
            {
                o.I("incident_seq", incidentSeq);
            }
            o.B("engaged", t.engaged);
            if (t.engaged)
            {
                o.I("engagement_id", t.engagementId);
            }
            if (t.crisis != null)
            {
                o.I("crisis_id", t.crisis.id).Strs("crisis_factors", t.crisis.factors);
            }
            // What happened on this map since the previous snapshot.
            o.O("since_last", t.interval.ToJson().I("ticks", t.lastSnapshotTick < 0 ? 0 : tick - t.lastSnapshotTick));
            Telemetry.Write(o);
            t.interval = new Counters();
            t.lastSnapshotTick = tick;
        }

        // ---------------------------------------------------------------- engagements (fights)

        private void CheckEngagement(Map map, MapTrack t, int tick)
        {
            bool threat = GenHostility.AnyHostileActiveThreatToPlayer(map);
            if (threat)
            {
                List<Pawn> hostiles = Snap.ActiveHostiles(map);
                float power = 0f;
                for (int i = 0; i < hostiles.Count; i++)
                {
                    power += hostiles[i].kindDef?.combatPower ?? 0f;
                }
                if (!t.engaged)
                {
                    StartEngagement(map, t, tick, hostiles);
                }
                t.quietChecks = 0;
                t.peakHostiles = Math.Max(t.peakHostiles, hostiles.Count);
                t.peakHostilePower = Math.Max(t.peakHostilePower, power);
                t.peakHostilesInHome = Math.Max(t.peakHostilesInHome, Snap.HostilesInHome(map, hostiles));
                t.peakFires = Math.Max(t.peakFires, map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count);

                int drafted = Drafted(map);
                if (drafted > 0 && t.firstDraftTick < 0)
                {
                    t.firstDraftTick = tick;
                }
                t.peakDrafted = Math.Max(t.peakDrafted, drafted);
            }
            else if (t.engaged)
            {
                t.quietChecks++;
                if (t.quietChecks >= QuietChecksToEnd)
                {
                    EndEngagement(map, map.uniqueID, t, tick, false);
                }
            }
        }

        private static int Drafted(Map map)
        {
            int drafted = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Drafted) drafted++;
            }
            return drafted;
        }

        private void StartEngagement(Map map, MapTrack t, int tick, List<Pawn> hostiles)
        {
            t.engaged = true;
            // A fight against the colony's own people (a berserk colonist) rather than an outside enemy.
            t.internalFight = hostiles.Count > 0 && hostiles.TrueForAll(h => h.Faction == Faction.OfPlayer);
            t.engagementId = Telemetry.NextSeq();
            t.engagementStartTick = tick;
            t.engagement = new Counters();
            t.peakHostiles = 0;
            t.peakHostilePower = 0f;
            t.peakHostilesInHome = 0;
            t.peakFires = 0;
            t.wealthStart = map.wealthWatcher.WealthTotal;
            t.colonistsStart = map.mapPawns.FreeColonistsSpawnedCount;
            t.injuriesStart = Snap.InjuryState(map);
            t.realSeconds = 0;
            t.pausedSeconds = 0;
            t.pauseCount = 0;
            t.msBySpeed = new Dictionary<string, int>();
            t.firstDraftTick = -1;
            t.peakDrafted = 0;
            t.incidentSeqs = new List<int>();
            foreach ((int seq, int pTick) in t.pendingThreats)
            {
                if (tick - pTick <= LinkWindowTicks)
                {
                    t.incidentSeqs.Add(seq);
                }
            }
            t.pendingThreats.Clear();

            Telemetry.Write(Telemetry.Rec("engagement_start")
                .I("engagement_id", t.engagementId)
                .I("map", map.uniqueID)
                .B("internal", t.internalFight)
                .Ints("incident_seqs", t.incidentSeqs)
                .O("hostiles", Snap.PawnGroup(hostiles)));
            WriteSnapshot(map, t, "engagement_start", 0);
        }

        private void EndEngagement(Map map, int mapId, MapTrack t, int tick, bool mapRemoved)
        {
            Counters c = t.engagement;
            JsonObj o = Telemetry.Rec("engagement_end")
                .I("engagement_id", t.engagementId)
                .I("map", mapId)
                .B("internal", t.internalFight)
                .B("map_removed", mapRemoved)
                .I("start_tick", t.engagementStartTick)
                .I("duration_ticks", tick - t.engagementStartTick)
                .Ints("incident_seqs", t.incidentSeqs)
                .I("peak_hostiles", t.peakHostiles)
                .N("peak_hostile_combat_power", t.peakHostilePower)
                .I("peak_hostiles_in_home_area", t.peakHostilesInHome)
                .I("peak_fires", t.peakFires)
                .N("wealth_start", t.wealthStart)
                .I("colonists_start", t.colonistsStart)
                .O("totals", c.ToJson())
                .O("player", new JsonObj()
                    .N("real_seconds", t.realSeconds)
                    .N("paused_seconds", t.pausedSeconds)
                    .I("pause_count", t.pauseCount)
                    .Counts("ms_by_speed", t.msBySpeed)
                    .I("ticks_to_first_draft", t.firstDraftTick < 0 ? -1 : t.firstDraftTick - t.engagementStartTick)
                    .I("peak_drafted", t.peakDrafted));
            if (t.crisis != null)
            {
                o.I("crisis_id", t.crisis.id);
            }
            if (map != null)
            {
                int newPermanent = 0, newMissing = 0;
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (t.injuriesStart != null && t.injuriesStart.TryGetValue(p.thingIDNumber, out KeyValuePair<int, int> before))
                    {
                        newPermanent += Math.Max(0, Snap.PermanentInjuries(p) - before.Key);
                        newMissing += Math.Max(0, Snap.MissingParts(p) - before.Value);
                    }
                }
                o.N("wealth_end", map.wealthWatcher.WealthTotal)
                    .I("colonists_end", map.mapPawns.FreeColonistsSpawnedCount)
                    .I("new_permanent_injuries", newPermanent)
                    .I("new_missing_parts", newMissing);
            }
            Telemetry.Write(o);
            t.engaged = false;
            t.engagement = null;
            t.quietChecks = 0;
            if (map != null)
            {
                WriteSnapshot(map, t, "engagement_end", 0);
            }
        }

        // ---------------------------------------------------------------- crises (anything seriously wrong)

        private void CheckCrisis(Map map, MapTrack t, int tick)
        {
            List<string> f = tmpFactors;
            f.Clear();
            if (t.engaged)
            {
                f.Add(t.internalFight ? "internal_conflict" : "combat");
            }
            int fires = Snap.Fires(map, out int homeFires, out _);
            if (homeFires >= HomeFireThreshold || fires >= MapFireThreshold) f.Add("fire");
            if (Snap.SeriousDisease(map)) f.Add("disease");
            if (Snap.HasSevere(map, HediffDefOf.Malnutrition, 0.3f)) f.Add("starvation");
            if (Snap.HasSevere(map, HediffDefOf.Hypothermia, 0.3f) || Snap.HasSevere(map, HediffDefOf.Heatstroke, 0.3f)) f.Add("temperature");
            if (tick - t.lastColonistDeathTick <= CrisisQuietTicks) f.Add("colonist_death");

            CrisisTrack c = t.crisis;
            if (f.Count == 0)
            {
                if (c == null)
                {
                    return;
                }
                if (c.quietSince < 0)
                {
                    c.quietSince = tick;
                }
                else if (tick - c.quietSince >= CrisisQuietTicks)
                {
                    EndCrisis(map, map.uniqueID, t, tick, false);
                }
                return;
            }
            if (c == null)
            {
                c = StartCrisis(map, t, tick, f);
            }
            c.quietSince = -1;
            c.maxConcurrent = Math.Max(c.maxConcurrent, f.Count);
            c.peakFires = Math.Max(c.peakFires, fires);
            c.peakHomeFires = Math.Max(c.peakHomeFires, homeFires);
            c.peakDrafted = Math.Max(c.peakDrafted, Drafted(map));
            if (t.engaged && !c.engagementIds.Contains(t.engagementId))
            {
                c.engagementIds.Add(t.engagementId);
            }
            foreach (string factor in f)
            {
                if (!c.factors.Contains(factor))
                {
                    c.factors.Add(factor);
                    // The order problems arrived in, and how many were already open: this is the stacking.
                    c.factorTimeline.Add(new JsonObj().S("factor", factor).I("after_ticks", tick - c.startTick).I("concurrent", f.Count));
                }
            }
        }

        private CrisisTrack StartCrisis(Map map, MapTrack t, int tick, List<string> factors)
        {
            if (t.recovery != null)
            {
                WriteRecovery(map.uniqueID, t, tick, "interrupted_by_new_crisis", true);
            }
            CrisisTrack c = new CrisisTrack
            {
                id = Telemetry.NextSeq(),
                startTick = tick,
                wealthStart = map.wealthWatcher.WealthTotal,
                buildingsStart = map.listerBuildings.allBuildingsColonist.Count,
                colonistsStart = map.mapPawns.FreeColonistsSpawnedCount,
                sickStart = Snap.SickColonists(map)
            };
            Snap.Vitals(map, out _, out c.downedStart, out c.healthStart, out c.moodStart);
            foreach ((int seq, int iTick, string label) in t.recentIncidents)
            {
                if (tick - iTick <= LinkWindowTicks)
                {
                    c.incidentSeqs.Add(seq);
                    c.incidentLabels.Add(label);
                }
            }
            t.crisis = c;
            Telemetry.Write(Telemetry.Rec("crisis_start")
                .I("crisis_id", c.id)
                .I("map", map.uniqueID)
                .Strs("factors", factors)
                .Ints("recent_incident_seqs", c.incidentSeqs)
                .Strs("recent_incidents", c.incidentLabels)
                .O("fragility", Snap.Fragility(map)));
            WriteSnapshot(map, t, "crisis_start", 0);
            return c;
        }

        private void EndCrisis(Map map, int mapId, MapTrack t, int tick, bool mapRemoved)
        {
            CrisisTrack c = t.crisis;
            int end = c.quietSince >= 0 ? c.quietSince : tick;
            JsonObj o = Telemetry.Rec("crisis_end")
                .I("crisis_id", c.id)
                .I("map", mapId)
                .B("map_removed", mapRemoved)
                .I("start_tick", c.startTick)
                .I("duration_ticks", end - c.startTick)
                .Strs("factors", c.factors)
                .Objs("factor_timeline", c.factorTimeline)
                .I("max_concurrent_factors", c.maxConcurrent)
                .Ints("engagement_ids", c.engagementIds)
                .Ints("incident_seqs", c.incidentSeqs)
                .Strs("incidents", c.incidentLabels)
                .I("peak_fires", c.peakFires)
                .I("peak_fires_in_home", c.peakHomeFires)
                .N("wealth_start", c.wealthStart)
                .I("buildings_start", c.buildingsStart)
                .I("colonists_start", c.colonistsStart)
                .O("totals", c.totals.ToJson())
                .O("player", new JsonObj()
                    .N("real_seconds", c.realSeconds)
                    .N("paused_seconds", c.pausedSeconds)
                    .I("pause_count", c.pauseCount)
                    .I("peak_drafted", c.peakDrafted));
            if (map != null)
            {
                o.N("wealth_end", map.wealthWatcher.WealthTotal)
                    .I("buildings_end", map.listerBuildings.allBuildingsColonist.Count)
                    .I("colonists_end", map.mapPawns.FreeColonistsSpawnedCount);
            }
            Telemetry.Write(o);
            t.crisis = null;
            if (map == null)
            {
                return;
            }
            WriteSnapshot(map, t, "crisis_end", 0);
            t.recovery = new RecoveryTrack
            {
                crisis = c,
                title = Title(c, c.startTick),
                summary = Summary(c.totals),
                endTick = tick
            };
        }

        private static string Title(CrisisTrack c, int tick)
        {
            string what = string.Join(" + ", c.factors).Replace('_', ' ');
            if (c.incidentLabels.Count > 0)
            {
                List<string> labels = c.incidentLabels.Count > 3 ? c.incidentLabels.GetRange(c.incidentLabels.Count - 3, 3) : c.incidentLabels;
                what += " (" + string.Join(", ", labels) + ")";
            }
            return what + ", day " + (tick / 60000f).ToString("0.#");
        }

        private static string Summary(Counters c)
        {
            return "colonists downed: " + c.colonistsDowned + ", died: " + c.colonistsDied
                + ", enemies down: " + (c.hostilesDied + c.hostilesDowned) + ", buildings lost: " + c.buildingsDestroyed;
        }

        // ---------------------------------------------------------------- aftermath

        private static int Hold(int since, bool holds, int tick)
        {
            return holds ? (since < 0 ? tick : since) : -1;
        }

        private void CheckRecovery(Map map, MapTrack t, int tick)
        {
            RecoveryTrack r = t.recovery;
            if (r == null || t.crisis != null)
            {
                return;
            }
            CrisisTrack c = r.crisis;
            Snap.Vitals(map, out int needTend, out int downed, out float health, out float mood);
            int sick = Snap.SickColonists(map);
            r.minMood = Math.Min(r.minMood, mood);
            r.minHealth = Math.Min(r.minHealth, health);
            r.peakSick = Math.Max(r.peakSick, sick);
            r.peakNeedTending = Math.Max(r.peakNeedTending, needTend);
            // Compared with how things were before the crisis, so a baby or a bedridden colonist does not block recovery.
            r.tendedSince = Hold(r.tendedSince, needTend == 0, tick);
            r.downedSince = Hold(r.downedSince, downed <= c.downedStart, tick);
            r.healthSince = Hold(r.healthSince, health >= Math.Min(c.healthStart, 0.98f) - 0.02f, tick);
            r.moodSince = Hold(r.moodSince, mood >= c.moodStart - 0.05f, tick);
            r.sickSince = Hold(r.sickSince, sick <= c.sickStart, tick);

            // Stable = every condition has held at the same time for an in-game hour.
            int stable = r.StableSince;
            if (stable >= 0 && tick - stable >= StableHoldTicks)
            {
                WriteRecovery(map.uniqueID, t, tick, "recovered", true);
            }
            else if (tick - r.endTick > RecoveryTimeoutTicks)
            {
                WriteRecovery(map.uniqueID, t, tick, "timeout", true);
            }
        }

        /// <summary>
        /// Closes the aftermath of a crisis. Times are ticks after the crisis ended until the condition held for good
        /// (-1 = it did not hold when the record was written). Then asks for the rating, so it covers the consequences too.
        /// </summary>
        private void WriteRecovery(int mapId, MapTrack t, int tick, string status, bool ask)
        {
            RecoveryTrack r = t.recovery;
            CrisisTrack c = r.crisis;
            t.recovery = null;
            int stable = r.StableSince;
            Telemetry.Write(Telemetry.Rec("recovery")
                .I("crisis_id", c.id)
                .I("map", mapId)
                .S("status", status)
                .I("ticks_tracked", tick - r.endTick)
                .I("ticks_to_stable", stable < 0 ? -1 : stable - r.endTick)
                .I("ticks_to_all_tended", r.tendedSince < 0 ? -1 : r.tendedSince - r.endTick)
                .I("ticks_to_downed_back_to_baseline", r.downedSince < 0 ? -1 : r.downedSince - r.endTick)
                .I("ticks_to_health_restored", r.healthSince < 0 ? -1 : r.healthSince - r.endTick)
                .I("ticks_to_mood_restored", r.moodSince < 0 ? -1 : r.moodSince - r.endTick)
                .I("ticks_to_sickness_cleared", r.sickSince < 0 ? -1 : r.sickSince - r.endTick)
                .N("mood_before", c.moodStart)
                .N("mood_min", r.minMood)
                .N("health_before", c.healthStart)
                .N("health_min", r.minHealth)
                .I("sick_before", c.sickStart)
                .I("downed_before", c.downedStart)
                .I("peak_sick_colonists", r.peakSick)
                .I("peak_need_tending", r.peakNeedTending)
                .O("during", r.during.ToJson()));
            if (ask && !c.asked && TelemetryMod.Settings.askRating)
            {
                c.asked = true;
                Find.WindowStack.Add(new Dialog_RateFight(c, r.title, r.summary, tick - r.endTick, status));
            }
        }

        private void PruneRemovedMaps(List<Map> maps, int tick)
        {
            tmpRemove.Clear();
            foreach (KeyValuePair<int, MapTrack> kv in tracks)
            {
                bool exists = false;
                for (int i = 0; i < maps.Count; i++)
                {
                    if (maps[i].uniqueID == kv.Key)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    tmpRemove.Add(kv.Key);
                }
            }
            foreach (int id in tmpRemove)
            {
                MapTrack t = tracks[id];
                if (t.engaged)
                {
                    EndEngagement(null, id, t, tick, true);
                }
                if (t.crisis != null)
                {
                    EndCrisis(null, id, t, tick, true);
                }
                if (t.recovery != null)
                {
                    WriteRecovery(id, t, tick, "map_removed", false);
                }
                tracks.Remove(id);
            }
        }
    }
}
