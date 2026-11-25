using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace StorytellerTelemetry
{
    public class TelemetrySettings : ModSettings
    {
        public bool enabled = true;
        public int snapshotIntervalTicks = 2500;
        public int combatSnapshotIntervalTicks = 250;
        public bool askRating = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref snapshotIntervalTicks, "snapshotIntervalTicks", 2500);
            Scribe_Values.Look(ref combatSnapshotIntervalTicks, "combatSnapshotIntervalTicks", 250);
            Scribe_Values.Look(ref askRating, "askRating", true);
        }
    }

    public class TelemetryMod : Mod
    {
        public static TelemetrySettings Settings = new TelemetrySettings();

        public TelemetryMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<TelemetrySettings>();
        }

        public override string SettingsCategory()
        {
            return "Storyteller Telemetry";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);
            l.CheckboxLabeled("Record telemetry", ref Settings.enabled);
            l.CheckboxLabeled("Ask me to rate each crisis (fight, fire, disease...) once its aftermath is over", ref Settings.askRating);
            l.Gap();
            l.Label("Colony snapshot every " + Settings.snapshotIntervalTicks + " ticks (" + (Settings.snapshotIntervalTicks / 2500f).ToString("0.##") + " in-game hours)");
            Settings.snapshotIntervalTicks = Mathf.RoundToInt(l.Slider(Settings.snapshotIntervalTicks, 250f, 60000f) / 250f) * 250;
            l.Label("Snapshot every " + Settings.combatSnapshotIntervalTicks + " ticks while hostiles are on the map");
            Settings.combatSnapshotIntervalTicks = Mathf.RoundToInt(l.Slider(Settings.combatSnapshotIntervalTicks, 50f, 2500f) / 50f) * 50;
            l.Gap();
            l.Label("Output folder:\n" + Telemetry.RootFolder);
            l.End();
        }
    }

    [StaticConstructorOnStartup]
    internal static class Startup
    {
        static Startup()
        {
            new Harmony("cookis.storytellertelemetry").PatchAll();
            Log.Message("[Storyteller Telemetry] Active. Writing to " + Telemetry.RootFolder);
        }
    }

    /// <summary>Owns the output file. One file per loaded game ("session"), grouped in one folder per colony ("run").</summary>
    internal static class Telemetry
    {
        public const int SchemaVersion = 4;

        private static readonly object Lock = new object();
        private static StreamWriter writer;
        private static Game sessionGame;
        private static int seq;
        private static readonly HashSet<string> reportedFailures = new HashSet<string>();

        /// <summary>Set by the GameComponent: "new" or "load".</summary>
        public static string initKind = "unknown";

        public static string RunId { get; private set; }

        public static bool Enabled => TelemetryMod.Settings == null || TelemetryMod.Settings.enabled;

        public static string RootFolder => Path.Combine(GenFilePaths.SaveDataFolderPath, "StorytellerTelemetry");

        public static int NextSeq()
        {
            lock (Lock)
            {
                return ++seq;
            }
        }

        /// <summary>New record with the fields every record carries.</summary>
        public static JsonObj Rec(string type)
        {
            JsonObj o = new JsonObj().S("type", type);
            int tick = 0;
            float day = 0f;
            try
            {
                tick = Find.TickManager.TicksGame;
                day = GenDate.DaysPassedSinceSettleFloat;
            }
            catch (Exception)
            {
            }
            return o.I("tick", tick).N("day", day);
        }

        public static void Write(JsonObj rec)
        {
            if (!Enabled || rec == null)
            {
                return;
            }
            lock (Lock)
            {
                try
                {
                    if (!EnsureSession())
                    {
                        return;
                    }
                    writer.WriteLine(rec.ToString());
                    writer.Flush();
                }
                catch (Exception e)
                {
                    Fail("write", e);
                }
            }
        }

        /// <summary>Logs a telemetry failure once per site, so a broken hook can never spam or break the game.</summary>
        public static void Fail(string where, Exception e)
        {
            lock (Lock)
            {
                if (!reportedFailures.Add(where))
                {
                    return;
                }
            }
            Log.Warning("[Storyteller Telemetry] " + where + " failed (reported once): " + e);
        }

        private static bool EnsureSession()
        {
            Game game = Current.Game;
            if (game == null || Current.ProgramState != ProgramState.Playing || Find.World == null)
            {
                return false;
            }
            if (game == sessionGame && writer != null)
            {
                return true;
            }
            writer?.Dispose();
            writer = null;
            sessionGame = game;
            seq = 0;

            RunId = Sanitize(Find.World.info.seedString) + "_" + Find.World.info.persistentRandomValue.ToString("x8", CultureInfo.InvariantCulture);
            string dir = Path.Combine(RootFolder, RunId);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "session_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
            writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

            writer.WriteLine(SessionStart().ToString());
            writer.WriteLine(Catalog().ToString());
            writer.Flush();
            return true;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "world";
            }
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
        }

        private static JsonObj SessionStart()
        {
            JsonObj o = Rec("session_start")
                .I("schema", SchemaVersion)
                .S("run_id", RunId)
                .S("init", initKind)
                .S("real_time_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .S("game_version", VersionControl.CurrentVersionStringWithRev);
            try
            {
                o.S("world_seed", Find.World.info.seedString)
                    .S("scenario", Find.Scenario?.name)
                    .B("permadeath", Current.Game.Info.permadeathMode)
                    .I("snapshot_interval", TelemetryMod.Settings.snapshotIntervalTicks)
                    .I("combat_snapshot_interval", TelemetryMod.Settings.combatSnapshotIntervalTicks);
                List<string> mods = new List<string>();
                foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                {
                    mods.Add(m.PackageId);
                }
                o.Strs("mods", mods);
                o.O("storyteller", Snap.Storyteller());
            }
            catch (Exception e)
            {
                Fail("session_start", e);
            }
            return o;
        }

        /// <summary>The action space: every incident the storyteller could pick, with its static properties.</summary>
        private static JsonObj Catalog()
        {
            JsonObj o = Rec("catalog");
            try
            {
                List<JsonObj> incidents = new List<JsonObj>();
                foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefsListForReading)
                {
                    List<string> tags = new List<string>();
                    if (d.targetTags != null)
                    {
                        foreach (IncidentTargetTagDef t in d.targetTags)
                        {
                            tags.Add(t.defName);
                        }
                    }
                    incidents.Add(new JsonObj()
                        .S("def", d.defName)
                        .S("category", d.category?.defName)
                        .S("worker", d.workerClass?.Name)
                        .S("mod", d.modContentPack?.PackageId)
                        .N("base_chance", d.baseChance)
                        .I("earliest_day", d.earliestDay)
                        .N("min_refire_days", d.minRefireDays)
                        .I("min_population", d.minPopulation)
                        .B("points_scaleable", d.pointsScaleable)
                        .N("min_threat_points", d.minThreatPoints <= float.MinValue ? double.NaN : d.minThreatPoints)
                        .N("max_threat_points", d.maxThreatPoints >= float.MaxValue ? double.NaN : d.maxThreatPoints)
                        .Strs("target_tags", tags));
                }
                o.Objs("incidents", incidents);

                List<string> strategies = new List<string>();
                foreach (RaidStrategyDef d in DefDatabase<RaidStrategyDef>.AllDefsListForReading)
                {
                    strategies.Add(d.defName);
                }
                o.Strs("raid_strategies", strategies);

                List<string> arrivals = new List<string>();
                foreach (PawnsArrivalModeDef d in DefDatabase<PawnsArrivalModeDef>.AllDefsListForReading)
                {
                    arrivals.Add(d.defName);
                }
                o.Strs("arrival_modes", arrivals);
            }
            catch (Exception e)
            {
                Fail("catalog", e);
            }
            return o;
        }
    }
}
