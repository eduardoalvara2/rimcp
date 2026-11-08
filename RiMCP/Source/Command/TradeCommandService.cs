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
            if ((body.MaxColonyCost.HasValue && body.MaxColonyCost.Value < 0) || (body.MinColonyGain.HasValue && body.MinColonyGain.Value < 0))
            {
                throw new CommandException(400, "maxColonyCost and minColonyGain are silver amounts from 0.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            Map map = context.Map;
            RequestKey key = RequestKey.For("execute_trade", requestId, map, body.TraderId, body.NegotiatorId,
                string.Join(";", lines.Select(line => (line.Buy ? "b:" : "s:") + line.Key + "=" + line.Count).ToArray()), body.MaxColonyCost, body.MinColonyGain);
            return CommandReceipts.Once(context, key, () => Run(context, body.TraderId, body.NegotiatorId, lines, body.MaxColonyCost, body.MinColonyGain));
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

        private static CommandResult Run(CommandContext context, string traderId, string negotiatorId, List<Line> lines, int? maxColonyCost, int? minColonyGain)
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
                foreach (IGrouping<Tradeable, Planned> same in plan.GroupBy(each => each.Row.Tradeable))
                {
                    if (same.Count() > 1)
                    {
                        throw new CommandException(400, "Items '" + string.Join("' and '", same.Select(each => each.Line.Key).ToArray()) + "' are one tradeable (the game trades identical animals and stacks as one); give one line with the total count. Nothing was traded.");
                    }
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
                if (maxColonyCost.HasValue && -colonyChange > maxColonyCost.Value)
                {
                    throw new CommandException(409, "The deal costs the colony " + (-colonyChange) + " silver, more than the maxColonyCost of " + maxColonyCost.Value + ": prices moved since get_trade. Nothing was traded.");
                }
                if (minColonyGain.HasValue && colonyChange < minColonyGain.Value)
                {
                    throw new CommandException(409, "The deal brings the colony " + colonyChange + " silver, less than the minColonyGain of " + minColonyGain.Value + ": prices moved since get_trade. Nothing was traded.");
                }

                // The game's ship call tells the player of relatives among the ship's captives.
                if (entry.Orbital)
                {
                    PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter_Send(entry.Ship.Goods.OfType<Pawn>(), "LetterRelatedPawnsTradeShip".Translate(Faction.OfPlayer.def.pawnsPlural), LetterDefOf.NeutralEvent);
                }

                Snapshot before = Snapshot.Of(map);
                bool traded;
                bool done = deal.TryExecute(out traded);
                if (!done)
                {
                    throw new CommandException(409, "The game refused the deal (an ideology precept, or the funds check). Nothing was traded.");
                }

                // From here the goods have changed hands: whatever goes wrong in reporting must not drop
                // the receipt, or a retry would trade again.
                try
                {
                    return Report(map, entry, who, deal, plan, before, traded, colonyBefore, traderBefore, colonyChange);
                }
                catch (Exception e)
                {
                    return new CommandResult(traded, Dto.Obj(
                        Dto.Field("traded", traded),
                        Dto.Field("traderId", entry.Id),
                        Dto.Field("negotiatorId", who.ThingID),
                        Dto.Field("silver", Dto.Obj(Dto.Field("colonyChange", colonyChange))),
                        Dto.Field("verified", false),
                        Dto.Field("warnings", new object[] { "The deal went through, but reading back its result failed (" + e.GetType().Name + ": " + e.Message + "). Check the colony's silver and items before trading again." })));
                }
            }
        }

        private static CommandResult Report(Map map, TraderEntry entry, Pawn who, TradeDeal deal, List<Planned> plan, Snapshot before, bool traded, int colonyBefore, int traderBefore, int colonyChange)
        {
            // TryExecute rebuilt the tradeables, so what the window would show now is read afresh.
            List<TradeRow> after = TradeSupport.Rows(deal);
            Tradeable currencyAfter = deal.CurrencyTradeable;
            int colonyShown = currencyAfter == null ? 0 : currencyAfter.CountHeldBy(Transactor.Colony);
            int traderAfter = currencyAfter == null ? 0 : currencyAfter.CountHeldBy(Transactor.Trader);
            Arrivals arrivals = Snapshot.Arrivals(map, before);

            List<string> warnings = new List<string>();
            List<object> results = new List<object>();
            foreach (Planned each in plan)
            {
                results.Add(Verify(each, after, arrivals, entry, warnings));
            }
            // The silver is checked on the trader's side, which the game's own numbers keep: a ship pays by
            // drop pod and a caravan puts silver down near its carrier, so what the colony shows can lag.
            if (!SilverVerified(traderBefore, traderAfter, colonyChange))
            {
                warnings.Add("The trader's silver went from " + traderBefore + " to " + traderAfter + ", not by the " + colonyChange + " the deal came to.");
            }

            if (traded && entry.Pawn != null && entry.Pawn.mindState != null && entry.Pawn.mindState.hasQuest)
            {
                // What closing the trade window does for a trader with a quest to give (Odyssey).
                TradeUtility.ReceiveQuestFromTrader(entry.Pawn, who);
            }

            FollowUps.Announce("traded with " + entry.Trader.TraderName + " through " + who.LabelShort + ": "
                + plan.Count(each => each.Line.Buy) + " bought, " + plan.Count(each => !each.Line.Buy) + " sold, " + SignedSilver(colonyChange) + ".",
                entry.Pawn == null ? null : new LookTargets(entry.Pawn));

            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("traded", traded),
                Dto.Field("traderId", entry.Id),
                Dto.Field("negotiatorId", who.ThingID),
                Dto.Field("silver", Dto.Obj(
                    Dto.Field("colonyChange", colonyChange),
                    Dto.Field("traderBefore", traderBefore),
                    Dto.Field("traderAfter", traderAfter),
                    Dto.Field("colonyBefore", colonyBefore),
                    Dto.Field("colonyShownAfter", colonyShown),
                    Dto.Field("deliveredSilver", arrivals.GainedOnMap("Silver") + arrivals.PodContents("Silver")))),
                Dto.Field("items", results.ToArray()),
                Dto.Field("delivery", Delivery(entry, arrivals)),
                Dto.Field("verified", warnings.Count == 0),
                Dto.Field("warnings", warnings.Count == 0 ? null : warnings.Select(warning => (object)warning).ToArray()));
            return new CommandResult(traded, data);
        }

        // The trader's silver moves by what the colony's moves by, the other way.
        internal static bool SilverVerified(int traderBefore, int traderAfter, int colonyChange)
        {
            return traderAfter == traderBefore - colonyChange;
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
            // The things of the tradeable as planned, to find what is left of it afterwards.
            public HashSet<int> Members;
            public List<Pawn> TraderPawns;
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
            return new Planned { Row = row, Line = line, ColonyBefore = colony, TraderBefore = trader, Label = label, DefName = tradeable.ThingDef.defName, Members = new HashSet<int>(TradeSupport.Members(tradeable).Select(thing => thing.thingIDNumber)), TraderPawns = tradeable.thingsTrader.OfType<Pawn>().ToList() };
        }

        // Pass or fail is the trader's side of the count, which the game's numbers keep whatever happens
        // to the goods afterwards; where the goods landed is information, except that goods seen nowhere
        // (not on the map, not in the colony's count) are a warning.
        private static object Verify(Planned planned, List<TradeRow> after, Arrivals arrivals, TraderEntry entry, List<string> warnings)
        {
            List<TradeRow> now = TradeSupport.RowsContaining(after, planned.Members);
            int colonyAfter = now.Sum(row => TradeSupport.ColonyCount(row.Tradeable));
            int traderAfter = now.Sum(row => TradeSupport.TraderCount(row.Tradeable));
            bool pawn = planned.Row.Tradeable.AnyThing is Pawn;
            bool ok;
            int? landed = null;
            string where = null;
            if (planned.Line.Buy)
            {
                ok = traderAfter == planned.TraderBefore - planned.Line.Count;
                landed = pawn
                    ? planned.TraderPawns.Count(each => each.Faction == Faction.OfPlayer)
                    : entry.Orbital ? arrivals.PodContents(planned.DefName) : arrivals.GainedOnMap(planned.DefName);
                where = pawn ? "joined the colony" : entry.Orbital ? "dropped by pod" : "placed near the trader";
                if (!ok)
                {
                    warnings.Add(planned.Label + ": expected the trader to have " + (planned.TraderBefore - planned.Line.Count) + " left of " + planned.TraderBefore + "; it has " + traderAfter + ".");
                }
                else if (!BuyArrived(planned.TraderBefore, traderAfter, planned.Line.Count, landed.Value, planned.ColonyBefore, colonyAfter))
                {
                    warnings.Add(planned.Label + ": the trader gave up " + planned.Line.Count + " but they were not seen arriving (" + landed.Value + " found).");
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
                Dto.Field("landed", landed),
                Dto.Field("where", where),
                Dto.Field("ok", ok));
        }

        // Vanilla places a caravan's goods near the carrier that held them and drops a ship's by pod
        // near a trade beacon; this reports what actually appeared on the map.
        // A purchase went through when the trader gave up that many and the goods turned up: on the map
        // as new things or as growth of a stack they merged into (a caravan places goods beside what is
        // already there), or in what the colony counts of it.
        internal static bool BuyArrived(int traderBefore, int traderAfter, int count, int gainedOnMap, int colonyBefore, int colonyAfter)
        {
            return traderAfter == traderBefore - count && (gainedOnMap >= count || colonyAfter - colonyBefore >= count);
        }

        // What each def gained on a map between two looks at its items: new stacks in full, merged ones
        // by their growth; stacks that shrank count for nothing.
        internal static Dictionary<string, int> GainByDef(Dictionary<int, int> before, IEnumerable<KeyValuePair<int, KeyValuePair<string, int>>> after)
        {
            Dictionary<string, int> gains = new Dictionary<string, int>();
            foreach (KeyValuePair<int, KeyValuePair<string, int>> item in after)
            {
                int was;
                int gain = item.Value.Value - (before.TryGetValue(item.Key, out was) ? was : 0);
                if (gain > 0)
                {
                    int sum;
                    gains.TryGetValue(item.Value.Key, out sum);
                    gains[item.Value.Key] = sum + gain;
                }
            }
            return gains;
        }

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
                Dto.Field("note", "Goods are placed near the carrier that held them, wherever it stands, so they may lie outside the colony's home area and storage until hauled; the caravan's own pawns leave them alone."),
                Dto.Field("items", arrivals.Placed.Select(item => (object)Dto.Obj(
                    Dto.Field("id", item.Thing.ThingID),
                    Dto.Field("defName", item.Thing.def.defName),
                    Dto.Field("count", item.Gained),
                    Dto.Field("mergedIntoStack", item.Gained < item.Thing.stackCount ? (object)item.Thing.stackCount : null),
                    Dto.Field("x", item.Thing.Position.x),
                    Dto.Field("z", item.Thing.Position.z))).Take(50).ToArray()));
        }

        // The items and drop pods on a map, to tell what a deal brought.
        private sealed class Snapshot
        {
            public Dictionary<int, int> Items = new Dictionary<int, int>();
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
                        snapshot.Items[thing.thingIDNumber] = thing.stackCount;
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
                                arrivals.Delivered.Add(new KeyValuePair<string, int>(inside.GetInnerIfMinified().def.defName, inside.stackCount));
                            }
                            arrivals.Pods.Add(info);
                        }
                    }
                    else if (thing.def.category == ThingCategory.Item)
                    {
                        int was;
                        int gained = thing.stackCount - (before.Items.TryGetValue(thing.thingIDNumber, out was) ? was : 0);
                        if (gained > 0)
                        {
                            arrivals.Placed.Add(new PlacedItem { Thing = thing, Gained = gained });
                        }
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

        private sealed class PlacedItem
        {
            public Thing Thing;
            public int Gained;
        }

        private sealed class PodInfo
        {
            public IntVec3 Position;
            public List<string> Contents = new List<string>();
        }

        private sealed class Arrivals
        {
            public List<PodInfo> Pods = new List<PodInfo>();
            public List<PlacedItem> Placed = new List<PlacedItem>();
            public List<KeyValuePair<string, int>> Delivered = new List<KeyValuePair<string, int>>();

            public int GainedOnMap(string defName)
            {
                return Placed.Where(item => item.Thing.GetInnerIfMinified().def.defName == defName).Sum(item => item.Gained);
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

            [DataMember(Name = "maxColonyCost")]
            public int? MaxColonyCost { get; set; }

            [DataMember(Name = "minColonyGain")]
            public int? MinColonyGain { get; set; }

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
