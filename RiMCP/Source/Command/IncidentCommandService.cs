using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // Fires one allow-listed incident on a map now, for testing: a colony can practise capturing a
    // downed raider or trading without waiting for the storyteller. Off unless the player turns on
    // "Allow test incidents" in RimMCP's settings, which no tool can change.
    internal static class IncidentCommandService
    {
        public const string TraderCaravan = "TraderCaravanArrival";
        public const string OrbitalTrader = "OrbitalTraderArrival";
        public const string Raid = "RaidEnemy";

        // Nothing outside this list can be fired, whatever the game defines.
        public static readonly string[] Allowed = { TraderCaravan, OrbitalTrader, Raid };

        // A raid small enough to practise on: a couple of weak raiders on most storytellers.
        public const int DefaultRaidPoints = 60;
        public const int MaxRaidPoints = 5000;

        public const string Disabled = "fire_incident is for testing only and is turned off. The player turns it on with the 'Allow test incidents (testing only)' checkbox on the Safety tab of RimMCP's mod settings; no tool can. Nothing was sent to the game.";

        public static BridgeResponse FireIncident(BridgeRequest request, RouteMatch route)
        {
            // The gate comes first: with it off, nothing else about the call is looked at or run.
            if (!BridgeRuntime.TestIncidentsAllowed)
            {
                throw new CommandException(403, Disabled);
            }
            FireIncidentBody body = CommandUtil.ReadBody<FireIncidentBody>(request);
            string name = body.Incident == null ? null : body.Incident.Trim();
            string incident = Allowed.FirstOrDefault(each => string.Equals(each, name, StringComparison.OrdinalIgnoreCase));
            if (incident == null)
            {
                throw new CommandException(400, "Field 'incident' must be one of: " + string.Join(", ", Allowed) + ".");
            }
            if (body.Points.HasValue && incident != Raid)
            {
                throw new CommandException(400, "Only " + Raid + " takes points.");
            }
            if (body.Points.HasValue && (body.Points.Value < 1 || body.Points.Value > MaxRaidPoints))
            {
                throw new CommandException(400, "Field 'points' must be from 1 to " + MaxRaidPoints + ".");
            }
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            RequestKey key = RequestKey.For("fire_incident", requestId, context.Map, incident, body.Points);
            return CommandReceipts.Once(context, key, () => Fire(context, incident, body.Points));
        }

        private static CommandResult Fire(CommandContext context, string incident, int? points)
        {
            Map map = context.Map;
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(incident);
            if (def == null)
            {
                throw new CommandException(404, "This game has no incident " + incident + ".");
            }
            if (def.requireColonistsPresent && map.mapPawns.FreeColonistsSpawnedCount == 0)
            {
                throw new CommandException(409, incident + " needs a colonist on the map, and there is none.");
            }

            // The storyteller's own default parameters for this map, forced as a debug fire is.
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.forced = true;
            if (incident == Raid)
            {
                parms.points = points ?? DefaultRaidPoints;
            }
            IncidentWorker worker = def.Worker;
            if (!worker.CanFireNow(parms))
            {
                throw new CommandException(409, "The game says " + incident + " cannot fire on this map now" + (incident == OrbitalTrader ? " (a map holds at most five passing ships)." : "."));
            }

            HashSet<int> known = new HashSet<int>(Find.LetterStack.LettersListForReading.Select(letter => letter.ID));
            bool fired = worker.TryExecute(parms);
            List<object> letters = Find.LetterStack.LettersListForReading.Where(letter => !known.Contains(letter.ID)).Select(SerializeLetter).ToList();
            if (fired)
            {
                FollowUps.Announce("fired the test incident " + incident + ".");
            }
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("incident", incident),
                Dto.Field("fired", fired),
                Dto.Field("points", incident == Raid ? (object)Math.Round(parms.points, 1) : null),
                Dto.Field("faction", parms.faction == null ? null : parms.faction.Name),
                Dto.Field("raidStrategy", parms.raidStrategy == null ? null : parms.raidStrategy.defName),
                Dto.Field("arrival", parms.raidArrivalMode == null ? null : parms.raidArrivalMode.defName),
                Dto.Field("pawnCount", incident == Raid && fired ? (object)parms.pawnCount : null),
                Dto.Field("traderKind", parms.traderKind == null ? null : parms.traderKind.defName),
                Dto.Field("letter", letters.Count == 0 ? null : letters[0]),
                Dto.Field("letters", letters.Count > 1 ? letters.ToArray() : null),
                Dto.Field("reason", fired ? null : "The game could not set the incident up (for example no faction could send it, or the points were too low for any group)."));
            return new CommandResult(fired, data);
        }

        private static object SerializeLetter(Letter letter)
        {
            ChoiceLetter choice = letter as ChoiceLetter;
            return Dto.Obj(
                Dto.Field("id", letter.ID),
                Dto.Field("label", letter.Label.ToString().StripTags()),
                Dto.Field("type", letter.def == null ? null : letter.def.defName),
                Dto.Field("text", choice == null ? null : choice.Text.ToString().StripTags()));
        }

        [DataContract]
        private sealed class FireIncidentBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "incident")]
            public string Incident { get; set; }

            [DataMember(Name = "points")]
            public int? Points { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
