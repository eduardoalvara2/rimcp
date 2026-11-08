using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    // The traders on a map and what each would trade, read through the game's own trade code. No
    // trade window opens and nothing changes: the session used to read prices is closed again.
    internal static class TradeReadService
    {
        public static BridgeResponse ListTraders(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Map map = context.Map;
            List<TraderEntry> traders = TradeSupport.Traders(map);
            List<object> rows = new List<object>();
            foreach (TraderEntry entry in traders)
            {
                rows.Add(SerializeTrader(map, entry));
            }
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("count", rows.Count),
                Dto.Field("tradableNow", rows.Count(row => (bool)((Dictionary<string, object>)row)["canTrade"])),
                Dto.Field("traders", rows.ToArray()),
                Dto.Field("keyFormat", TradeSupport.KeyHelp)));
        }

        private static object SerializeTrader(Map map, TraderEntry entry)
        {
            List<NegotiatorEntry> all = TradeSupport.Negotiators(map, entry);
            List<NegotiatorEntry> able = all.Where(each => each.Problem == null).ToList();
            string problem = TradeSupport.TraderProblem(entry);
            if (problem == null && able.Count == 0)
            {
                problem = entry.Orbital && all.Any(each => each.Problem != null && each.Problem.StartsWith("There is no"))
                    ? all.First(each => each.Problem != null && each.Problem.StartsWith("There is no")).Problem
                    : "No colonist can negotiate with this trader now.";
            }
            Building_CommsConsole console = able.Select(each => each.Console).FirstOrDefault(each => each != null);
            ITrader trader = entry.Trader;
            return Dto.Obj(
                Dto.Field("traderId", entry.Id),
                Dto.Field("kind", entry.Orbital ? "orbital" : "caravan"),
                Dto.Field("label", trader.TraderName),
                Dto.Field("traderKind", Dto.Obj(
                    Dto.Field("defName", trader.TraderKind.defName),
                    Dto.Field("label", trader.TraderKind.LabelCap.ToString()))),
                Dto.Field("faction", trader.Faction == null ? null : trader.Faction.Name),
                Dto.Field("position", entry.Pawn == null ? null : ReadUtil.Cell(entry.Pawn.Position)),
                Dto.Field("ticksUntilDeparture", TradeSupport.TicksUntilDeparture(entry)),
                Dto.Field("canTrade", problem == null),
                Dto.Field("reason", problem),
                Dto.Field("console", console == null ? null : Dto.Obj(
                    Dto.Field("id", console.ThingID),
                    Dto.Field("label", console.LabelCap.ToString()),
                    Dto.Field("position", ReadUtil.Cell(console.Position)))),
                Dto.Field("negotiators", able.Select(each => SerializeNegotiator(each, entry)).ToArray()),
                Dto.Field("unavailable", all.Where(each => each.Problem != null).Select(each => (object)Dto.Obj(
                    Dto.Field("id", each.Pawn.ThingID),
                    Dto.Field("name", each.Pawn.LabelShort),
                    Dto.Field("reason", each.Problem))).ToArray()));
        }

        private static object SerializeNegotiator(NegotiatorEntry negotiator, TraderEntry entry)
        {
            Pawn pawn = negotiator.Pawn;
            return Dto.Obj(
                Dto.Field("id", pawn.ThingID),
                Dto.Field("name", pawn.LabelShort),
                Dto.Field("social", pawn.skills == null ? (int?)null : pawn.skills.GetSkill(SkillDefOf.Social).Level),
                Dto.Field("tradePriceImprovement", Math.Round(pawn.GetStatValue(StatDefOf.TradePriceImprovement), 3)),
                Dto.Field("consoleId", negotiator.Console == null ? null : negotiator.Console.ThingID));
        }

        // The window's tradeables for a trader and a negotiator, with prices as that negotiator gets
        // them, filtered and paged. The session opens and closes inside this call.
        public static BridgeResponse GetTrade(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            ReadRequest query = context.Request;
            string traderId = query.Get("traderId");
            if (string.IsNullOrWhiteSpace(traderId))
            {
                throw new BridgeException(400, "Missing required field 'traderId' (from list_traders).");
            }
            string text = query.Get("query");
            string defName = query.Get("def");
            if (context.Map == null)
            {
                throw new BridgeException(409, "No active map is loaded.");
            }
            Map map = context.Map;
            TraderEntry entry = TradeSupport.FindTrader(map, traderId);
            string negotiatorId = query.Get("negotiatorId");
            NegotiatorEntry negotiator = string.IsNullOrWhiteSpace(negotiatorId)
                ? TradeSupport.BestNegotiator(TradeSupport.Negotiators(map, entry))
                : TradeSupport.FindNegotiator(map, entry, negotiatorId);
            if (negotiator == null)
            {
                throw new BridgeException(409, TradeSupport.TraderProblem(entry) ?? "No colonist can negotiate with this trader now; list_traders says why.");
            }

            using (TradeSupport.Session session = TradeSupport.Open(entry, negotiator.Pawn))
            {
                List<TradeRow> all = TradeSupport.Rows(session.Deal);
                List<TradeRow> matches = all
                    .Where(row => string.IsNullOrWhiteSpace(defName) || string.Equals(row.Tradeable.ThingDef.defName, defName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Where(row => string.IsNullOrWhiteSpace(text)
                        || row.Tradeable.Label.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                        || row.Tradeable.ThingDef.defName.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                        || row.Key.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                Page<TradeRow> page = new Page<TradeRow>(matches, query);
                bool detailed = query.Detail != ReadDetail.Summary;
                Pawn who = negotiator.Pawn;
                object[] items = page.Items.Select(row => SerializeRow(row, who, detailed)).ToArray();
                Tradeable currency = session.Deal.CurrencyTradeable;
                return ReadEnvelope.Ok(context, Dto.Obj(
                    Dto.Field("trader", SerializeTrader(map, entry)),
                    Dto.Field("negotiator", SerializeNegotiator(negotiator, entry)),
                    Dto.Field("summary", Dto.Obj(
                        Dto.Field("currency", entry.Trader.TradeCurrency == TradeCurrency.Silver ? "silver" : entry.Trader.TradeCurrency.ToString()),
                        Dto.Field("colonySilver", TradeSupport.Silver(session.Deal, Transactor.Colony)),
                        Dto.Field("traderSilver", TradeSupport.Silver(session.Deal, Transactor.Trader)),
                        Dto.Field("tradeables", all.Count),
                        Dto.Field("matches", matches.Count),
                        Dto.Field("cannotSellReasons", session.Deal.cannotSellReasons.Count == 0 ? null : session.Deal.cannotSellReasons.Select(reason => (object)reason.StripTags()).ToArray()),
                        Dto.Field("sellingNote", entry.Orbital ? "A ship takes only goods within range of a powered orbital trade beacon, and what it sells is dropped by pod near one." : "A caravan takes goods in the home area or in storage it can reach, and what it sells is placed near its trader."))),
                    Dto.Field("tradeables", items),
                    Dto.Field("keyFormat", TradeSupport.KeyHelp)),
                    page.Truncated, page.NextCursor);
            }
        }

        private static object SerializeRow(TradeRow row, Pawn negotiator, bool detailed)
        {
            Tradeable tradeable = row.Tradeable;
            Thing thing = tradeable.AnyThing;
            int colony = TradeSupport.ColonyCount(tradeable);
            int trader = TradeSupport.TraderCount(tradeable);
            bool traderWills = tradeable.TraderWillTrade;
            string refusal = TradeSupport.ColonyRefusal(tradeable, negotiator);
            bool canBuy = trader > 0 && traderWills;
            bool canSell = colony > 0 && traderWills && refusal == null;
            string why = !traderWills ? "This trader does not trade this kind of item." : refusal;
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("key", row.Key),
                Dto.Field("label", tradeable.Label.StripTags()),
                Dto.Field("defName", thing.def.defName),
                Dto.Field("colonyCount", colony),
                Dto.Field("traderCount", trader),
                Dto.Field("buyPrice", canBuy ? (object)TradeSupport.Price(tradeable, TradeAction.PlayerBuys) : null),
                Dto.Field("sellPrice", canSell ? (object)TradeSupport.Price(tradeable, TradeAction.PlayerSells) : null),
                Dto.Field("traderWillTrade", traderWills),
                Dto.Field("colonyWillTrade", refusal == null),
                Dto.Field("canBuy", canBuy),
                Dto.Field("canSell", canSell),
                Dto.Field("reason", why));
            if (detailed)
            {
                dto["stuff"] = thing.Stuff == null ? null : thing.Stuff.defName;
                dto["quality"] = TradeSupport.Quality(tradeable);
                dto["hitPoints"] = thing.def.useHitPoints ? (object)thing.HitPoints : null;
                dto["maxHitPoints"] = thing.def.useHitPoints ? (object)thing.MaxHitPoints : null;
                dto["marketValue"] = Math.Round(tradeable.BaseMarketValue, 2);
                dto["kind"] = thing is Pawn ? "pawn" : "item";
                if (thing is Pawn)
                {
                    dto["thingIds"] = tradeable.thingsColony.Concat(tradeable.thingsTrader).Take(20).Select(each => (object)each.ThingID).ToArray();
                }
            }
            return dto;
        }
    }
}
