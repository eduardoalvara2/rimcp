using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RiMCP.Bridge;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    // A trader the colony can deal with now: a trade caravan's trader pawn, or an orbital trade ship.
    internal sealed class TraderEntry
    {
        public string Id;
        public bool Orbital;
        public ITrader Trader;
        public Pawn Pawn;
        public TradeShip Ship;
    }

    // A colonist who could negotiate with a trader, and what stops one who cannot.
    internal sealed class NegotiatorEntry
    {
        public Pawn Pawn;
        public Building_CommsConsole Console;
        public string Problem;
    }

    // One row of the trade window: the game's Tradeable under a key that survives the game building
    // the tradeables again.
    internal sealed class TradeRow
    {
        public string Key;
        public Tradeable Tradeable;
    }

    // The game's trade rules, read without opening a trade window. A TradeSession is set up the way
    // Dialog_Trade sets it up, and always closed again.
    internal static class TradeSupport
    {
        public const string KeyHelp = "def[:stuff][@quality][#n] for items, pawn:<thingId> for animals and people";

        // --- Traders ---

        public static List<TraderEntry> Traders(Map map)
        {
            List<TraderEntry> traders = new List<TraderEntry>();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.TraderKind != null && p.Faction != Faction.OfPlayer && PawnReadService.Visible(map, p)).OrderBy(p => p.thingIDNumber))
            {
                traders.Add(new TraderEntry { Id = pawn.ThingID, Pawn = pawn, Trader = pawn });
            }
            foreach (TradeShip ship in map.passingShipManager.passingShips.OfType<TradeShip>().OrderBy(s => s.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                traders.Add(new TraderEntry { Id = ship.GetUniqueLoadID(), Orbital = true, Ship = ship, Trader = ship });
            }
            return traders;
        }

        public static TraderEntry FindTrader(Map map, string traderId)
        {
            string id = traderId == null ? null : traderId.Trim();
            TraderEntry found = Traders(map).FirstOrDefault(entry => entry.Id == id || (entry.Pawn != null && entry.Pawn.GetUniqueLoadID() == id));
            if (found == null)
            {
                throw new BridgeException(404, "No trader with that id on this map. list_traders names the traders here now: a caravan leaves and a ship departs.");
            }
            return found;
        }

        // Why this trader cannot be dealt with at all, or null. Who could negotiate is separate.
        public static string TraderProblem(TraderEntry entry)
        {
            if (entry.Orbital)
            {
                return entry.Ship.CanTradeNow ? null : "The ship has departed.";
            }
            if (entry.Pawn.mindState != null && entry.Pawn.mindState.traderDismissed)
            {
                return "The trader was dismissed and is leaving.";
            }
            return entry.Trader.CanTradeNow ? null : "The trader cannot trade now: it is downed, hostile, busy, or has nothing it will trade.";
        }

        public static int? TicksUntilDeparture(TraderEntry entry)
        {
            return entry.Orbital ? (int?)Math.Max(0, entry.Ship.ticksUntilDeparture) : null;
        }

        // --- Negotiators ---

        public static List<NegotiatorEntry> Negotiators(Map map, TraderEntry entry)
        {
            List<Building_CommsConsole> consoles = entry.Orbital
                ? map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>().Where(console => console.CanUseCommsNow).ToList()
                : new List<Building_CommsConsole>();
            bool beacon = entry.Orbital && Building_OrbitalTradeBeacon.AllPowered(map).Any();
            List<NegotiatorEntry> result = new List<NegotiatorEntry>();
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber))
            {
                NegotiatorEntry negotiator = new NegotiatorEntry { Pawn = pawn };
                negotiator.Problem = PawnProblem(pawn, entry);
                if (negotiator.Problem == null && entry.Orbital)
                {
                    if (consoles.Count == 0)
                    {
                        negotiator.Problem = "There is no powered comms console to call the ship from.";
                    }
                    else if (!beacon)
                    {
                        negotiator.Problem = "There is no powered orbital trade beacon, so nothing could be sold to the ship or delivered by it.";
                    }
                    else
                    {
                        negotiator.Console = consoles.Where(console => pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some)).OrderBy(console => console.Position.DistanceToSquared(pawn.Position)).FirstOrDefault();
                        if (negotiator.Console == null)
                        {
                            negotiator.Problem = "No powered comms console can be reached.";
                        }
                    }
                }
                result.Add(negotiator);
            }
            return result;
        }

        // The checks the game's own menus make before a colonist trades: able to talk, not downed or
        // in a mental state, Social not disabled, a permit where the trader asks for one, and for a
        // caravan, a path to the trader.
        private static string PawnProblem(Pawn pawn, TraderEntry entry)
        {
            if (pawn.Downed)
            {
                return "Downed.";
            }
            if (pawn.InMentalState)
            {
                return "In a mental state.";
            }
            if (pawn.health == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
            {
                return "Cannot talk.";
            }
            AcceptanceReport report = pawn.CanTradeWith(entry.Trader.Faction, entry.Trader.TraderKind);
            if (!report.Accepted)
            {
                return string.IsNullOrEmpty(report.Reason) ? "Cannot trade with this faction." : report.Reason.StripTags();
            }
            if (!entry.Orbital && !pawn.CanReach(entry.Pawn, PathEndMode.OnCell, Danger.Deadly))
            {
                return "Cannot reach the trader.";
            }
            return null;
        }

        public static NegotiatorEntry FindNegotiator(Map map, TraderEntry entry, string negotiatorId)
        {
            string id = negotiatorId == null ? null : negotiatorId.Trim();
            List<NegotiatorEntry> all = Negotiators(map, entry);
            NegotiatorEntry found = all.FirstOrDefault(each => each.Pawn.ThingID == id || each.Pawn.GetUniqueLoadID() == id);
            if (found == null)
            {
                throw new BridgeException(404, "No such colonist on this map. list_traders names who could negotiate.");
            }
            if (found.Problem != null)
            {
                throw new BridgeException(409, found.Pawn.LabelShort + " cannot negotiate with this trader: " + found.Problem);
            }
            return found;
        }

        // The colonist who gets the best prices, as the game's own comms and trade picks do.
        public static NegotiatorEntry BestNegotiator(IEnumerable<NegotiatorEntry> negotiators)
        {
            return negotiators.Where(each => each.Problem == null)
                .OrderByDescending(each => each.Pawn.GetStatValue(StatDefOf.TradePriceImprovement))
                .ThenBy(each => each.Pawn.thingIDNumber)
                .FirstOrDefault();
        }

        // --- Sessions ---

        // A trade session with no window: the same statics Dialog_Trade fills in, set back when it
        // closes. It skips SetupWith, which would flash a message about goods the colony cannot sell.
        internal sealed class Session : IDisposable
        {
            public readonly TradeDeal Deal;
            public readonly Pawn Negotiator;

            public Session(ITrader trader, Pawn negotiator)
            {
                if (TradeSession.Active || (Find.WindowStack != null && Find.WindowStack.WindowOfType<Dialog_Trade>() != null))
                {
                    throw new BridgeException(409, "A trade window is open in game. Close it, or let the player finish, before agents trade.");
                }
                Negotiator = negotiator;
                TradeSession.trader = trader;
                TradeSession.playerNegotiator = negotiator;
                TradeSession.giftMode = false;
                try
                {
                    TradeSession.deal = new TradeDeal();
                }
                catch
                {
                    Dispose();
                    throw;
                }
                Deal = TradeSession.deal;
            }

            public void Dispose()
            {
                TradeSession.Close();
                TradeSession.playerNegotiator = null;
                TradeSession.deal = null;
                TradeSession.giftMode = false;
            }
        }

        public static Session Open(TraderEntry entry, Pawn negotiator)
        {
            string problem = TraderProblem(entry);
            if (problem != null)
            {
                throw new BridgeException(409, problem);
            }
            if (entry.Trader.TradeCurrency != TradeCurrency.Silver)
            {
                throw new BridgeException(409, "This trader deals in royal favor, not silver, which agents cannot trade yet.");
            }
            return new Session(entry.Trader, negotiator);
        }

        // --- Tradeables ---

        // The rows of the deal, in a stable order, each under its key. Items key on def, stuff and
        // quality, which is what the game groups them by apart from hit points; where one key still
        // covers several tradeables (hit point bands, unique items) they are numbered #2, #3, ... in
        // order of hit points, then age. Animals and people key on a thing id of theirs.
        public static List<TradeRow> Rows(TradeDeal deal)
        {
            List<Tradeable> items = deal.AllTradeables.Where(each => each.IsThing && each.HasAnyThing && !each.IsCurrency).ToList();
            List<TradeRow> rows = new List<TradeRow>();
            foreach (IGrouping<string, Tradeable> group in items.GroupBy(BaseKey))
            {
                List<Tradeable> ordered = group.OrderBy(each => each.AnyThing.HitPoints).ThenBy(each => each.AnyThing.thingIDNumber).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    rows.Add(new TradeRow { Key = i == 0 ? group.Key : group.Key + "#" + (i + 1), Tradeable = ordered[i] });
                }
            }
            return rows.OrderBy(row => row.Key, StringComparer.Ordinal).ToList();
        }

        private static string BaseKey(Tradeable tradeable)
        {
            Thing thing = tradeable.AnyThing;
            if (thing is Pawn)
            {
                return "pawn:" + thing.ThingID;
            }
            StringBuilder key = new StringBuilder(thing.def.defName);
            if (thing.Stuff != null)
            {
                key.Append(':').Append(thing.Stuff.defName);
            }
            QualityCategory quality;
            if (thing.TryGetQuality(out quality))
            {
                key.Append('@').Append(quality);
            }
            return key.ToString();
        }

        // A pawn's key names one pawn, but the game groups identical animals, so any pawn of the
        // tradeable finds it.
        public static TradeRow FindRow(List<TradeRow> rows, string key)
        {
            string wanted = key == null ? null : key.Trim();
            TradeRow row = rows.FirstOrDefault(each => each.Key == wanted);
            if (row == null && wanted != null && wanted.StartsWith("pawn:", StringComparison.Ordinal))
            {
                string id = wanted.Substring(5);
                row = rows.FirstOrDefault(each => each.Tradeable.thingsColony.Concat(each.Tradeable.thingsTrader).Any(thing => thing.ThingID == id));
            }
            return row;
        }

        public static int ColonyCount(Tradeable tradeable)
        {
            return tradeable.CountHeldBy(Transactor.Colony);
        }

        public static int TraderCount(Tradeable tradeable)
        {
            return tradeable.CountHeldBy(Transactor.Trader);
        }

        // The negotiator's own refusal to sell this, which the window shows in place of the counter:
        // an ideology that does not sell slaves.
        public static string ColonyRefusal(Tradeable tradeable, Pawn negotiator)
        {
            if (ModsConfig.IdeologyActive && TransferableUIUtility.TradeIsPlayerSellingToSlavery(tradeable, TradeSession.trader.Faction)
                && !new HistoryEvent(HistoryEventDefOf.SoldSlave, negotiator.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo())
            {
                return negotiator.LabelShort + " will not sell people into slavery.";
            }
            return null;
        }

        public static string Quality(Tradeable tradeable)
        {
            QualityCategory quality;
            return tradeable.AnyThing.TryGetQuality(out quality) ? quality.ToString() : null;
        }

        public static double Price(Tradeable tradeable, TradeAction action)
        {
            return Math.Round(tradeable.GetPriceFor(action), 2);
        }

        public static int Silver(TradeDeal deal, Transactor side)
        {
            Tradeable currency = deal.CurrencyTradeable;
            return currency == null ? 0 : currency.CountHeldBy(side);
        }
    }
}
