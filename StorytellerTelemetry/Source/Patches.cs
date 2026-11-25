using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace StorytellerTelemetry
{
    internal sealed class IncidentCtx
    {
        public int seq;
        public Map map;
        public float pointsIn;
        public float defaultPoints = -1f;
        public HashSet<int> hostilesBefore;
        public JsonObj raid;
        public string source = "direct";
        public string sourceQuestPart;
        public bool queued;
        public int decisionId;
    }

    internal sealed class FireCtx
    {
        public FiringIncident fi;
        public bool queued;
        public bool executed;
        public int decisionId;
    }

    /// <summary>State shared between the patches below. Every hook swallows its own errors.</summary>
    internal static class Hooks
    {
        [ThreadStatic] private static List<IncidentCtx> stack;
        [ThreadStatic] private static FireCtx fire;

        public static IncidentCtx Top => stack != null && stack.Count > 0 ? stack[stack.Count - 1] : null;

        private static readonly AccessTools.FieldRef<IncidentWorker, int> LastCheckTick =
            AccessTools.FieldRefAccess<IncidentWorker, int>("lastCheckCanRunTick");
        private static readonly AccessTools.FieldRef<IncidentWorker, bool> LastCheckResult =
            AccessTools.FieldRefAccess<IncidentWorker, bool>("lastCanRunResult");
        private static readonly MethodInfo ChanceFinal = AccessTools.Method(typeof(StorytellerComp), "IncidentChanceFinal");

        public static void FireBegin(FiringIncident fi, bool queued)
        {
            FireCtx f = new FireCtx { fi = fi, queued = queued };
            fire = f;
            if (fi?.source != null && fi.def != null && fi.parms?.target != null)
            {
                try
                {
                    f.decisionId = Candidates(fi);
                }
                catch (Exception e)
                {
                    Telemetry.Fail("candidates", e);
                }
            }
        }

        /// <summary>
        /// What the storyteller could have fired at this moment: every incident allowed on the target that passes
        /// CanFireNow, with the weight the storyteller would give it. Leaves the game's caches and random stream untouched.
        /// </summary>
        private static int Candidates(FiringIncident fi)
        {
            IIncidentTarget target = fi.parms.target;
            List<JsonObj> usable = new List<JsonObj>();
            Dictionary<IncidentCategoryDef, IncidentParms> parmsByCat = new Dictionary<IncidentCategoryDef, IncidentParms>();
            Rand.PushState();
            try
            {
                foreach (IncidentDef def in DefDatabase<IncidentDef>.AllDefsListForReading)
                {
                    if (def.category == null || !def.TargetAllowed(target))
                    {
                        continue;
                    }
                    if (!parmsByCat.TryGetValue(def.category, out IncidentParms parms))
                    {
                        parms = StorytellerUtility.DefaultParmsNow(def.category, target);
                        parmsByCat[def.category] = parms;
                    }
                    IncidentWorker w = def.Worker;
                    int savedTick = LastCheckTick(w);
                    bool savedResult = LastCheckResult(w);
                    bool can;
                    try
                    {
                        can = w.CanFireNow(parms);
                    }
                    catch (Exception)
                    {
                        can = false;
                    }
                    finally
                    {
                        LastCheckTick(w) = savedTick;
                        LastCheckResult(w) = savedResult;
                    }
                    if (!can)
                    {
                        continue;
                    }
                    float weight = (float)ChanceFinal.Invoke(fi.source, new object[] { def, target });
                    usable.Add(new JsonObj().S("def", def.defName).S("category", def.category.defName).N("weight", weight));
                }
            }
            finally
            {
                Rand.PopState();
            }
            int id = Telemetry.NextSeq();
            JsonObj o = Telemetry.Rec("candidates")
                .I("decision_id", id)
                .S("chosen", fi.def.defName)
                .S("chosen_category", fi.def.category?.defName)
                .S("source", fi.source.GetType().Name)
                .S("target_type", Snap.TargetType(target));
            if (target is Map map)
            {
                o.I("map", map.uniqueID);
            }
            Telemetry.Write(o.Objs("usable", usable));
            return id;
        }

        public static void FireEnd(FiringIncident fi, bool result)
        {
            FireCtx f = fire;
            fire = null;
            if (f == null || f.executed || result || fi?.def == null)
            {
                return;
            }
            // The storyteller picked this incident, but CanFireNow refused it.
            Telemetry.Write(Telemetry.Rec("incident_rejected")
                .S("def", fi.def.defName)
                .S("category", fi.def.category?.defName)
                .S("source", fi.source?.GetType().Name)
                .B("queued", f.queued)
                .S("target_type", Snap.TargetType(fi.parms?.target))
                .N("points", fi.parms?.points ?? -1f));
        }

        public static IncidentCtx IncidentBegin(IncidentWorker worker, IncidentParms parms)
        {
            IncidentCtx ctx = new IncidentCtx
            {
                seq = Telemetry.NextSeq(),
                pointsIn = parms.points,
                map = parms.target as Map
            };
            FireCtx f = fire;
            if (f != null && f.fi?.parms == parms)
            {
                f.executed = true;
                ctx.decisionId = f.decisionId;
                ctx.queued = f.queued;
                ctx.source = f.fi.source != null ? f.fi.source.GetType().Name : (f.queued ? "queue" : "storyteller");
                ctx.sourceQuestPart = f.fi.sourceQuestPart?.GetType().Name;
            }
            if (stack == null)
            {
                stack = new List<IncidentCtx>();
            }
            stack.Add(ctx);

            if (parms.target != null)
            {
                ctx.defaultPoints = Snap.ThreatPoints(parms.target);
            }
            if (ctx.map != null)
            {
                ctx.hostilesBefore = new HashSet<int>();
                foreach (Pawn p in Snap.ActiveHostiles(ctx.map))
                {
                    ctx.hostilesBefore.Add(p.thingIDNumber);
                }
                IncidentCategoryDef cat = worker.def?.category;
                bool threat = cat == IncidentCategoryDefOf.ThreatBig || cat == IncidentCategoryDefOf.ThreatSmall;
                TelemetryComponent.Active?.BeforeIncident(ctx.map, ctx.seq, threat, worker.def?.label ?? worker.def?.defName);
            }
            return ctx;
        }

        public static void IncidentEnd(IncidentWorker worker, IncidentParms parms, bool result, IncidentCtx ctx, Exception error)
        {
            if (stack != null)
            {
                stack.Remove(ctx);
            }
            IncidentDef def = worker.def;
            JsonObj o = Telemetry.Rec("incident")
                .I("seq", ctx.seq)
                .S("def", def?.defName)
                .S("category", def?.category?.defName)
                .S("worker", worker.GetType().Name)
                .B("success", result && error == null)
                .B("threw", error != null)
                .S("source", ctx.source)
                .S("source_quest_part", ctx.sourceQuestPart)
                .B("queued", ctx.queued)
                .B("forced", parms.forced)
                .S("quest", parms.quest?.root?.defName)
                .S("target_type", Snap.TargetType(parms.target));
            if (ctx.decisionId > 0)
            {
                o.I("decision_id", ctx.decisionId);
            }
            if (ctx.map != null)
            {
                o.I("map", ctx.map.uniqueID);
            }
            o.N("default_points", ctx.defaultPoints)
                .N("points_in", ctx.pointsIn)
                .N("points", parms.points)
                .N("point_multiplier", parms.pointMultiplier)
                .S("faction", parms.faction?.def.defName)
                .S("raid_strategy", parms.raidStrategy?.defName)
                .S("arrival_mode", parms.raidArrivalMode?.defName)
                .S("pawn_group_kind", parms.pawnGroupKind?.defName)
                .S("pawn_kind", parms.pawnKind?.defName)
                .I("pawn_count", parms.pawnCount);
            if (ctx.raid != null)
            {
                o.O("raid", ctx.raid);
            }
            if (ctx.map != null && ctx.hostilesBefore != null && !ctx.map.Disposed)
            {
                List<Pawn> added = new List<Pawn>();
                foreach (Pawn p in Snap.ActiveHostiles(ctx.map))
                {
                    if (!ctx.hostilesBefore.Contains(p.thingIDNumber))
                    {
                        added.Add(p);
                    }
                }
                if (added.Count > 0)
                {
                    // Hostiles that are physically on the map right after the incident (drop pods and delayed arrivals are not here yet).
                    o.O("hostiles_spawned", Snap.PawnGroup(added));
                }
            }
            Telemetry.Write(o);
        }

        public static void RaidGenerated(IncidentParms parms, List<Pawn> pawns)
        {
            IncidentCtx ctx = Top;
            if (ctx != null && pawns != null)
            {
                ctx.raid = Snap.PawnGroup(pawns);
            }
        }

        public static void PawnEvent(Pawn pawn, AdaptationEvent ev, DamageInfo? dinfo)
        {
            if (pawn == null)
            {
                return;
            }
            Map map = pawn.MapHeld;
            TelemetryComponent comp = TelemetryComponent.Active;
            string side = Snap.Side(pawn);
            if (side == "neutral" && (comp == null || !comp.IsEngaged(map)))
            {
                return; // wildlife dying outside of a fight is noise
            }
            if (ev == AdaptationEvent.Downed && pawn.DevelopmentalStage.Baby())
            {
                return; // babies count as permanently downed; being born is not a casualty
            }
            bool ours = side == "colonist" || side == "berserk";
            JsonObj o = Telemetry.Rec("pawn_event")
                .S("event", ev.ToString())
                .I("pawn_id", pawn.thingIDNumber)
                .S("kind", pawn.kindDef?.defName)
                .S("race_kind", Snap.RaceKind(pawn))
                .S("side", side)
                .S("faction", pawn.Faction?.def.defName);
            if (map != null)
            {
                o.I("map", map.uniqueID);
                IntVec3 pos = pawn.PositionHeld;
                if (pos.InBounds(map))
                {
                    // Where it happened: at the perimeter or inside the base.
                    o.B("in_home_area", map.areaManager.Home[pos]).B("roofed", pos.Roofed(map));
                }
            }
            Hediff culprit = null;
            if (ev == AdaptationEvent.Died && lastKilled == pawn)
            {
                culprit = lastKillCulprit;
                if (!dinfo.HasValue)
                {
                    dinfo = lastKillDinfo; // the game does not pass the killing blow along with the death notification
                }
            }
            int crisisId = comp != null ? comp.CrisisId(map) : 0;
            if (crisisId > 0)
            {
                o.I("crisis_id", crisisId);
            }
            if (ours || side == "colony")
            {
                Snap.DeathContext(o, pawn, map, culprit);
            }
            if (ours && ev == AdaptationEvent.Died)
            {
                comp?.NoteColonistDeath(map);
            }
            if (ours)
            {
                try
                {
                    comp?.ColonistLost(pawn);
                }
                catch (Exception e)
                {
                    Telemetry.Fail("colonist lost", e);
                }
            }
            string instigatorSide = null;
            if (dinfo.HasValue)
            {
                Thing inst = dinfo.Value.Instigator;
                instigatorSide = InstigatorSide(inst);
                o.S("damage_def", dinfo.Value.Def?.defName);
                if (inst != null)
                {
                    // Who did it: the colony, or something else (a predator, another faction, friendly fire among raiders).
                    o.S("instigator_kind", inst is Pawn ip ? ip.kindDef?.defName : inst.def?.defName)
                        .S("instigator_side", instigatorSide);
                }
            }
            bool byColony = instigatorSide == "colonist" || instigatorSide == "colony";
            Telemetry.Write(o);

            comp?.Count(map, c =>
            {
                if (ours)
                {
                    if (ev == AdaptationEvent.Downed) c.colonistsDowned++;
                    else if (ev == AdaptationEvent.Kidnapped) c.colonistsKidnapped++;
                    else if (ev == AdaptationEvent.Died) c.colonistsDied++;
                }
                else if (side == "hostile")
                {
                    if (ev == AdaptationEvent.Downed) c.hostilesDowned++;
                    else if (ev == AdaptationEvent.Died)
                    {
                        c.hostilesDied++;
                        if (byColony) c.hostilesKilledByColony++;
                    }
                }
                else if (side == "colony" && ev == AdaptationEvent.Died)
                {
                    c.colonyOtherDied++;
                }
            });
        }

        [ThreadStatic] private static Pawn lastKilled;
        [ThreadStatic] private static DamageInfo? lastKillDinfo;

        [ThreadStatic] private static Hediff lastKillCulprit;

        public static void KillBegin(Pawn pawn, DamageInfo? dinfo, Hediff culprit)
        {
            lastKilled = pawn;
            lastKillDinfo = dinfo;
            lastKillCulprit = culprit;
        }

        public static void FireStarted(Map map, Thing instigator)
        {
            // Spreading fire inherits the instigator of the fire it came from, so this follows a blaze back to its origin.
            string by = InstigatorSide(instigator);
            TelemetryComponent.Active?.Count(map, c =>
            {
                if (by == "colonist" || by == "berserk" || by == "colony") c.firesByColony++;
                else if (by == "hostile") c.firesByHostile++;
                else c.firesNatural++;
            });
        }

        private static string InstigatorSide(Thing inst)
        {
            if (inst == null)
            {
                return null;
            }
            if (inst is Pawn p)
            {
                return Snap.Side(p);
            }
            if (inst.Faction == null)
            {
                return "neutral";
            }
            return inst.Faction == Faction.OfPlayer ? "colony" : (inst.Faction.HostileTo(Faction.OfPlayer) ? "hostile" : "neutral");
        }

        public static void Damage(Pawn pawn, DamageInfo dinfo, float amount)
        {
            if (pawn == null || amount <= 0f)
            {
                return;
            }
            Map map = pawn.MapHeld;
            TelemetryComponent comp = TelemetryComponent.Active;
            if (map == null || comp == null)
            {
                return;
            }
            string side = Snap.Side(pawn);
            if (side == "neutral")
            {
                return;
            }
            string by = InstigatorSide(dinfo.Instigator);
            bool byColony = by == "colonist" || by == "colony";
            comp.Count(map, c =>
            {
                if (side == "colonist") c.colonistDamage += amount;
                else if (side == "berserk") c.berserkDamage += amount;
                else if (side == "hostile")
                {
                    c.hostileDamage += amount;
                    if (byColony) c.hostileDamageByColony += amount;
                }
                else c.colonyOtherDamage += amount;
            });
        }

        public static void BuildingDestroyed(Building b)
        {
            Map map = b.Map;
            if (map == null || b.Faction != Faction.OfPlayer)
            {
                return;
            }
            float value = b.GetStatValue(StatDefOf.MarketValueIgnoreHp);
            TelemetryComponent.Active?.Count(map, c =>
            {
                c.buildingsDestroyed++;
                c.buildingValueLost += value;
            });
        }

        public static void PawnExitingMap(Pawn pawn)
        {
            Map map = pawn.Map;
            if (map == null || Snap.Side(pawn) != "hostile")
            {
                return;
            }
            // A raider leaving: fled, gave up, or is carrying loot out. (A carried pawn is a kidnap, logged as a pawn_event.)
            Thing carried = pawn.carryTracker?.CarriedThing;
            float stolen = carried != null && !(carried is Pawn) ? carried.MarketValue * carried.stackCount : 0f;
            TelemetryComponent.Active?.Count(map, c =>
            {
                c.hostilesExited++;
                c.stolenValue += stolen;
            });
        }

        public static void PrisonerTaken(Pawn pawn)
        {
            TelemetryComponent.Active?.Count(pawn.MapHeld, c => c.prisonersTaken++);
        }

        public static void MentalBreak(Pawn pawn, MentalStateDef state, bool causedByMood)
        {
            if (pawn == null || !pawn.IsColonist)
            {
                return;
            }
            Map map = pawn.MapHeld;
            bool baby = pawn.DevelopmentalStage.Baby();
            JsonObj o = Telemetry.Rec("mental_break")
                .I("pawn_id", pawn.thingIDNumber)
                .S("stage", pawn.DevelopmentalStage.ToString())
                .B("aggressive", state != null && state.IsAggro)
                .S("state", state?.defName)
                .B("caused_by_mood", causedByMood);
            if (map != null)
            {
                o.I("map", map.uniqueID);
            }
            Telemetry.Write(o);
            if (!baby)
            {
                TelemetryComponent.Active?.Count(map, c => c.mentalBreaks++);
            }
        }
    }

    // Every incident, whoever fired it (storyteller, quest, debug menu, another mod).
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    internal static class Patch_IncidentWorker_TryExecute
    {
        private static void Prefix(IncidentWorker __instance, IncidentParms parms, out IncidentCtx __state)
        {
            __state = null;
            if (!Telemetry.Enabled || parms == null || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                __state = Hooks.IncidentBegin(__instance, parms);
            }
            catch (Exception e)
            {
                Telemetry.Fail("incident begin", e);
            }
        }

        private static void Finalizer(IncidentWorker __instance, IncidentParms parms, bool __result, IncidentCtx __state, Exception __exception)
        {
            if (__state == null)
            {
                return;
            }
            try
            {
                Hooks.IncidentEnd(__instance, parms, __result, __state, __exception);
            }
            catch (Exception e)
            {
                Telemetry.Fail("incident end", e);
            }
        }
    }

    // Which storyteller component chose the incident, and incidents the storyteller wanted but could not fire.
    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
    internal static class Patch_Storyteller_TryFire
    {
        private static void Prefix(FiringIncident fi, bool queued)
        {
            if (Telemetry.Enabled)
            {
                Hooks.FireBegin(fi, queued);
            }
        }

        private static void Finalizer(FiringIncident fi, bool __result)
        {
            try
            {
                Hooks.FireEnd(fi, __result);
            }
            catch (Exception e)
            {
                Telemetry.Fail("fire end", e);
            }
        }
    }

    // Exact raid composition, including pawns still in drop pods.
    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    internal static class Patch_IncidentWorker_Raid_TryGenerateRaidInfo
    {
        private static void Postfix(IncidentParms parms, List<Pawn> pawns, bool debugTest, bool __result)
        {
            if (debugTest || !__result)
            {
                return;
            }
            try
            {
                Hooks.RaidGenerated(parms, pawns);
            }
            catch (Exception e)
            {
                Telemetry.Fail("raid info", e);
            }
        }
    }

    // Downed / died / kidnapped, for every pawn. This is the same signal the vanilla adaptation system listens to.
    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.Notify_PawnEvent))]
    internal static class Patch_Storyteller_Notify_PawnEvent
    {
        private static void Postfix(Pawn pawn, AdaptationEvent ev, DamageInfo? dinfo)
        {
            if (!Telemetry.Enabled)
            {
                return;
            }
            try
            {
                Hooks.PawnEvent(pawn, ev, dinfo);
            }
            catch (Exception e)
            {
                Telemetry.Fail("pawn event", e);
            }
        }
    }

    // Where fires come from: a colonist (pyromaniac, stray shot), an enemy, or nature (lightning, flashstorm).
    [HarmonyPatch(typeof(FireUtility), nameof(FireUtility.TryStartFireIn))]
    internal static class Patch_FireUtility_TryStartFireIn
    {
        private static void Postfix(bool __result, Map map, Thing instigator)
        {
            if (!__result || map == null || !Telemetry.Enabled)
            {
                return;
            }
            try
            {
                Hooks.FireStarted(map, instigator);
            }
            catch (Exception e)
            {
                Telemetry.Fail("fire started", e);
            }
        }
    }

    // Remembers the killing blow so the death event can say who did it.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    internal static class Patch_Pawn_Kill
    {
        private static void Prefix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            Hooks.KillBegin(__instance, dinfo, exactCulprit);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    internal static class Patch_Pawn_PostApplyDamage
    {
        private static void Postfix(Pawn __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (!Telemetry.Enabled)
            {
                return;
            }
            try
            {
                Hooks.Damage(__instance, dinfo, totalDamageDealt);
            }
            catch (Exception e)
            {
                Telemetry.Fail("damage", e);
            }
        }
    }

    // Colony buildings destroyed by damage (not deconstructed).
    [HarmonyPatch(typeof(Building), nameof(Building.Destroy))]
    internal static class Patch_Building_Destroy
    {
        private static void Prefix(Building __instance, DestroyMode mode)
        {
            if (mode != DestroyMode.KillFinalize || !Telemetry.Enabled)
            {
                return;
            }
            try
            {
                Hooks.BuildingDestroyed(__instance);
            }
            catch (Exception e)
            {
                Telemetry.Fail("building destroyed", e);
            }
        }
    }

    // Raiders leaving the map, and what they carry out.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
    internal static class Patch_Pawn_ExitMap
    {
        private static void Prefix(Pawn __instance)
        {
            if (!Telemetry.Enabled)
            {
                return;
            }
            try
            {
                Hooks.PawnExitingMap(__instance);
            }
            catch (Exception e)
            {
                Telemetry.Fail("exit map", e);
            }
        }
    }

    // Enemies captured as prisoners.
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    internal static class Patch_Pawn_GuestTracker_SetGuestStatus
    {
        private static void Prefix(Pawn ___pawn, out bool __state)
        {
            __state = ___pawn != null && ___pawn.IsPrisonerOfColony;
        }

        private static void Postfix(Pawn ___pawn, bool __state)
        {
            if (__state || !Telemetry.Enabled)
            {
                return;
            }
            try
            {
                if (___pawn != null && ___pawn.IsPrisonerOfColony)
                {
                    Hooks.PrisonerTaken(___pawn);
                }
            }
            catch (Exception e)
            {
                Telemetry.Fail("prisoner", e);
            }
        }
    }

    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    internal static class Patch_MentalStateHandler_TryStartMentalState
    {
        private static void Postfix(bool __result, MentalStateDef stateDef, bool causedByMood, Pawn ___pawn)
        {
            if (!__result || !Telemetry.Enabled)
            {
                return;
            }
            try
            {
                Hooks.MentalBreak(___pawn, stateDef, causedByMood);
            }
            catch (Exception e)
            {
                Telemetry.Fail("mental break", e);
            }
        }
    }
}
