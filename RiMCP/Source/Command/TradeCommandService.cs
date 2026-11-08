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
    // Trades through the game's TradeSession and TradeDeal, with no window: the counts are set on
    // the game's own tradeables, the silver is checked as the window checks it, and the deal runs with
    // TradeDeal.TryExecute, so the game's own rules and side effects (goodwill, tales, ideology
    // events, where the goods go) apply as they do for a player.
    internal static class TradeCommandService
    {
        private const int MaxEntries = 50;
        private const int MaxCount = 1000000;

        public static BridgeResponse ExecuteTrade(BridgeRequest request, RouteMatch route)
        {
            ExecuteTradeBody body = CommandUtil.ReadBody<ExecuteTradeBody>(request);
            if (string.IsNullOrWhiteSpace(body.TraderId))
            {
                throw new CommandException(400, "Missing required field 'traderId' (from list_traders).");
            }
            if (string.IsNullOrWhiteSpace(body.NegotiatorId))
            {
                throw new CommandException(400, "Missing required field 'negotiatorId' (from list_traders).");
            }
            List<Line> lines = new List<Line>();
            lines.AddRange(ReadLines(body.Buy, "buy", true));
            lines.AddRange(ReadLines(body.Sell, "sell", false));
            if (lines.Count == 0)
            {
                throw new CommandException(400, "Give at least one item in 'buy' or 'sell'.");
            }
            foreach (IGrouping<string, Line> group in lines.GroupBy(line => line.Key))
            {
                if (group.Count() > 1)
                {
                    throw new CommandException(400, "Item '" + group.Key + "' is listed more than once; the trade window has one count per item, so buy or sell it, once.");
                }
            }
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            Map map = context.Map;
            RequestKey key = RequestKey.For("execute_trade", requestId, map, body.TraderId, body.NegotiatorId,
                string.Join(";", lines.Select(line => (line.Buy ? "b:" : "s:") + line.Key + "=" + line.Count).ToArray()));
            return CommandReceipts.Once(context, key, () => Run(context, body.TraderId, body.NegotiatorId, lines));
        }

        private sealed class Line
        {
            public string Key;
            public int Count;
            public bool Buy;
        }

        private static IEnumerable<Line> ReadLines(TradeLineBody[] entries, string field, bool buy)
        {
            if (entries == null)
            {
                yield break;
            }
            if (entries.Length > MaxEntries)
            {
                throw new CommandException(400, "Field '" + field + "' takes at most " + MaxEntries + " items.");
            }
            foreach (TradeLineBody entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
                {
                    throw new CommandException(400, "Each item in '" + field + "' needs a key from get_trade.");
                }
                if (!entry.Count.HasValue || entry.Count.Value < 1 || entry.Count.Value > MaxCount)
                {
                    throw new CommandException(400, "Item '" + entry.Key + "' in '" + field + "' needs a whole count from 1 to " + MaxCount + ".");
                }
                yield return new Line { Key = entry.Key.Trim(), Count = entry.Count.Value, Buy = buy };
            }
        }

        private static CommandResult Run(CommandContext context, string traderId, string negotiatorId, List<Line> lines)
        {
            Map map = context.Map;
            TraderEntry entry = TradeSupport.FindTrader(map, traderId);
            NegotiatorEntry negotiator = TradeSupport.FindNegotiator(map, entry, negotiatorId);
            Pawn who = negotiator.Pawn;

            using (TradeSupport.Session session = TradeSupport.Open(entry, who))
            {
                TradeDeal deal = session.Deal;
                List<TradeRow> rows = TradeSupport.Rows(deal);
                Tradeable currency = deal.CurrencyTradeable;
                if (currency == null)
                {
                    throw new CommandException(409, "The deal has no silver to pay with.");
                }

                // Every count is checked before any is set, so a refusal leaves the deal untouched.
                List<Planned> plan = new List<Planned>();
                foreach (Line line in lines)
                {
                    TradeRow row = TradeSupport.FindRow(rows, line.Key);
                    if (row == null)
                    {
                        throw new CommandException(404, "No tradeable with key '" + line.Key + "'. get_trade lists the keys now; they change when the goods do.");
                    }
                    plan.Add(Check(entry, who, row, line));
                }

                int colonyBefore = currency.CountHeldBy(Transactor.Colony);
                int traderBefore = currency.CountHeldBy(Transactor.Trader);
                foreach (Planned each in plan)
                {
                    each.Row.Tradeable.AdjustTo(each.Line.Buy ? each.Line.Count : -each.Line.Count);
                }

                // As the window does on every frame, then as Accept does.
                deal.UpdateCurrencyCount();
                int colonyChange = currency.CountToTransferToSource;
                if (currency.CountPostDealFor(Transactor.Colony) < 0)
                {
                    throw new CommandException(409, "The colony cannot afford this: it comes to " + (-colonyChange) + " silver and the colony has " + colonyBefore + (entry.Orbital ? " (a ship counts only silver within range of a powered orbital trade beacon)" : " (a caravan counts only silver in the home area or in storage it can reach)") + ". Nothing was traded.");
                }
                if (!deal.DoesTraderHaveEnoughSilver())
                {
                    throw new CommandException(409, "The trader cannot pay this in full: it owes " + colonyChange + " silver and has " + traderBefore + ". The window would ask the player to confirm a short deal; sell less. Nothing was traded.");
                }

                Snapshot before = Snapshot.Of(map);
                bool traded;
                bool done = deal.TryExecute(out traded);
                if (!done)
                {
                    throw new CommandException(409, "The game refused the deal (an ideology precept, or the funds check). Nothing was traded.");
                }
                // TryExecute rebuilt the tradeables, so what the window would show now is read afresh.
                List<TradeRow> after = TradeSupport.Rows(deal);
                int colonyAfter = deal.CurrencyTradeable == null ? 0 : deal.CurrencyTradeable.CountHeldBy(Transactor.Colony);
                int traderAfter = deal.CurrencyTradeable == null ? 0 : deal.CurrencyTradeable.CountHeldBy(Transactor.Trader);
                Arrivals arrivals = Snapshot.Arrivals(map, before);

                List<string> warnings = new List<string>();
                List<object> results = new List<object>();
                foreach (Planned each in plan)
                {
                    results.Add(Verify(each, after, arrivals, entry, warnings));
                }
                if (colonyAfter - colonyBefore != colonyChange)
                {
                    warnings.Add("The colony's silver changed by " + (colonyAfter - colonyBefore) + ", not the " + colonyChange + " the deal came to.");
                }

                FollowUps.Announce("traded with " + entry.Trader.TraderName + " through " + who.LabelShort + ": "
                    + plan.Count(each => each.Line.Buy) + " bought, " + plan.Count(each => !each.Line.Buy) + " sold, " + SignedSilver(colonyAfter - colonyBefore) + ".",
                    entry.Pawn == null ? null : new LookTargets(entry.Pawn));

                Dictionary<string, object> data = Dto.Obj(
                    Dto.Field("traded", traded),
                    Dto.Field("traderId", entry.Id),
                    Dto.Field("negotiatorId", who.ThingID),
                    Dto.Field("silver", Dto.Obj(
                        Dto.Field("colonyBefore", colonyBefore),
                        Dto.Field("colonyAfter", colonyAfter),
                        Dto.Field("colonyChange", colonyAfter - colonyBefore),
                        Dto.Field("traderBefore", traderBefore),
                        Dto.Field("traderAfter", traderAfter))),
                    Dto.Field("items", results.ToArray()),
                    Dto.Field("delivery", Delivery(entry, arrivals)),
                    Dto.Field("verified", warnings.Count == 0),
                    Dto.Field("warnings", warnings.Count == 0 ? null : warnings.Select(warning => (object)warning).ToArray()));
                return new CommandResult(traded, data);
            }
        }

        private static string SignedSilver(int change)
        {
            return (change >= 0 ? "+" : "") + change + " silver";
        }

        private sealed class Planned
        {
            public TradeRow Row;
            public Line Line;
            public int ColonyBefore;
            public int TraderBefore;
            public string Label;
            public string DefName;
        }

        // The window's limits for one count: the trader will take or sell it, the negotiator will, and
        // the side giving it has that many.
        private static Planned Check(TraderEntry entry, Pawn negotiator, TradeRow row, Line line)
        {
            Tradeable tradeable = row.Tradeable;
            string label = tradeable.Label.StripTags();
            int colony = TradeSupport.ColonyCount(tradeable);
            int trader = TradeSupport.TraderCount(tradeable);
            if (!tradeable.TraderWillTrade)
            {
                throw new CommandException(409, entry.Trader.TraderName + " does not trade " + label + ". Nothing was traded.");
            }
            if (line.Buy)
            {
                if (line.Count > trader)
                {
                    throw new CommandException(409, entry.Trader.TraderName + " has " + trader + " of " + label + " ('" + row.Key + "'), not " + line.Count + ". Nothing was traded.");
                }
            }
            else
            {
                string refusal = TradeSupport.ColonyRefusal(tradeable, negotiator);
                if (refusal != null)
                {
                    throw new CommandException(409, refusal + " Nothing was traded.");
                }
                if (line.Count > colony)
                {
                    string where = entry.Orbital
                        ? "A ship takes only goods within range of a powered orbital trade beacon, in the beacon's open area, that are fresh, not biocoded, and not worn by a corpse."
                        : "A caravan takes goods that are in the home area or in storage, reachable by the caravan, not under fog, fresh, not biocoded, and not worn by a corpse.";
                    throw new CommandException(409, "The colony has " + colony + " of " + label + " ('" + row.Key + "') it can sell this trader, not " + line.Count + ". " + where + " Nothing was traded.");
                }
            }
            return new Planned { Row = row, Line = line, ColonyBefore = colony, TraderBefore = trader, Label = label, DefName = tradeable.ThingDef.defName };
        }

        private static object Verify(Planned planned, List<TradeRow> after, Arrivals arrivals, TraderEntry entry, List<string> warnings)
        {
            TradeRow now = TradeSupport.FindRow(after, planned.Line.Key);
            int colonyAfter = now == null ? 0 : TradeSupport.ColonyCount(now.Tradeable);
            int traderAfter = now == null ? 0 : TradeSupport.TraderCount(now.Tradeable);
            bool pawn = planned.Row.Tradeable.AnyThing is Pawn;
            bool ok;
            string where = null;
            if (planned.Line.Buy)
            {
                int landed = pawn
                    ? planned.Row.Tradeable.thingsTrader.OfType<Pawn>().Take(planned.Line.Count).Count(each => each.Faction == Faction.OfPlayer)
                    : entry.Orbital ? arrivals.PodContents(planned.DefName) : arrivals.NewOnMap(planned.DefName);
                ok = traderAfter == planned.TraderBefore - planned.Line.Count && landed >= planned.Line.Count;
                where = pawn ? "joined the colony" : entry.Orbital ? "dropped by pod" : "placed near the trader";
                if (!ok)
                {
                    warnings.Add(planned.Label + ": expected " + planned.Line.Count + " bought; the trader has " + traderAfter + " left of " + planned.TraderBefore + " and " + landed + " arrived.");
                }
            }
            else
            {
                ok = colonyAfter == planned.ColonyBefore - planned.Line.Count;
                if (!ok)
                {
                    warnings.Add(planned.Label + ": expected the colony to have " + (planned.ColonyBefore - planned.Line.Count) + " left; it shows " + colonyAfter + ".");
                }
            }
            return Dto.Obj(
                Dto.Field("key", planned.Line.Key),
                Dto.Field("label", planned.Label),
                Dto.Field("action", planned.Line.Buy ? "buy" : "sell"),
                Dto.Field("count", planned.Line.Count),
                Dto.Field("colonyBefore", planned.ColonyBefore),
                Dto.Field("colonyAfter", colonyAfter),
                Dto.Field("traderBefore", planned.TraderBefore),
                Dto.Field("traderAfter", traderAfter),
                Dto.Field("where", where),
                Dto.Field("ok", ok));
        }

        // Vanilla places a caravan's goods near the carrier that held them and drops a ship's by pod
        // near a trade beacon; this reports what actually appeared on the map.
        private static object Delivery(TraderEntry entry, Arrivals arrivals)
        {
            if (entry.Orbital)
            {
                return Dto.Obj(
                    Dto.Field("mode", "dropPod"),
                    Dto.Field("note", "Goods arrive in drop pods, which land within a few seconds near a trade beacon."),
                    Dto.Field("pods", arrivals.Pods.Select(pod => (object)Dto.Obj(
                        Dto.Field("x", pod.Position.x),
                        Dto.Field("z", pod.Position.z),
                        Dto.Field("contents", pod.Contents.ToArray()))).ToArray()));
            }
            return Dto.Obj(
                Dto.Field("mode", "nearTrader"),
                Dto.Field("note", "Goods are placed near the trader's carrier, outside the colony's storage, and are forbidden while the caravan stays; haul or unforbid them."),
                Dto.Field("items", arrivals.Placed.Select(item => (object)Dto.Obj(
                    Dto.Field("id", item.ThingID),
                    Dto.Field("defName", item.def.defName),
                    Dto.Field("count", item.stackCount),
                    Dto.Field("x", item.Position.x),
                    Dto.Field("z", item.Position.z))).Take(50).ToArray()));
        }

        // The items and drop pods on a map, to tell what a deal brought.
        private sealed class Snapshot
        {
            public HashSet<int> Items = new HashSet<int>();
            public HashSet<int> Pods = new HashSet<int>();

            public static Snapshot Of(Map map)
            {
                Snapshot snapshot = new Snapshot();
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Skyfaller)
                    {
                        snapshot.Pods.Add(thing.thingIDNumber);
                    }
                    else if (thing.def.category == ThingCategory.Item)
                    {
                        snapshot.Items.Add(thing.thingIDNumber);
                    }
                }
                return snapshot;
            }

            public static Arrivals Arrivals(Map map, Snapshot before)
            {
                Arrivals arrivals = new Arrivals();
                foreach (Thing thing in map.listerThings.AllThings.ToList())
                {
                    Skyfaller pod = thing as Skyfaller;
                    if (pod != null)
                    {
                        if (!before.Pods.Contains(pod.thingIDNumber))
                        {
                            PodInfo info = new PodInfo { Position = pod.Position };
                            foreach (Thing inside in PodContents(pod))
                            {
                                info.Contents.Add(inside.def.defName + (inside.stackCount > 1 ? " x" + inside.stackCount : ""));
                                arrivals.Delivered.Add(new KeyValuePair<string, int>(inside.def.defName, inside.stackCount));
                            }
                            arrivals.Pods.Add(info);
                        }
                    }
                    else if (thing.def.category == ThingCategory.Item && !before.Items.Contains(thing.thingIDNumber))
                    {
                        arrivals.Placed.Add(thing);
                    }
                }
                return arrivals;
            }

            private static IEnumerable<Thing> PodContents(Skyfaller pod)
            {
                if (pod.innerContainer == null)
                {
                    yield break;
                }
                foreach (Thing thing in pod.innerContainer)
                {
                    ActiveTransporter transporter = thing as ActiveTransporter;
                    if (transporter != null && transporter.Contents != null)
                    {
                        foreach (Thing inside in transporter.Contents.innerContainer)
                        {
                            yield return inside;
                        }
                    }
                    else
                    {
                        yield return thing;
                    }
                }
            }
        }

        private sealed class PodInfo
        {
            public IntVec3 Position;
            public List<string> Contents = new List<string>();
        }

        private sealed class Arrivals
        {
            public List<PodInfo> Pods = new List<PodInfo>();
            public List<Thing> Placed = new List<Thing>();
            public List<KeyValuePair<string, int>> Delivered = new List<KeyValuePair<string, int>>();

            public int NewOnMap(string defName)
            {
                return Placed.Where(item => item.def.defName == defName).Sum(item => item.stackCount);
            }

            public int PodContents(string defName)
            {
                return Delivered.Where(pair => pair.Key == defName).Sum(pair => pair.Value);
            }
        }

        [DataContract]
        private sealed class ExecuteTradeBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "traderId")]
            public string TraderId { get; set; }

            [DataMember(Name = "negotiatorId")]
            public string NegotiatorId { get; set; }

            [DataMember(Name = "buy")]
            public TradeLineBody[] Buy { get; set; }

            [DataMember(Name = "sell")]
            public TradeLineBody[] Sell { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class TradeLineBody
        {
            [DataMember(Name = "key")]
            public string Key { get; set; }

            [DataMember(Name = "count")]
            public int? Count { get; set; }
        }
    }
}
