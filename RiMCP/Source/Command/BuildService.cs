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
    // Buildings: checking where one fits, which changes nothing, and placing blueprints for
    // colonists to build. Both use the game's build tool through BuildPlacement, which works on the
    // map shown in game.
    internal static class BuildService
    {
        private const int MaxPlacements = 50;
        private const int MaxCheckSide = 20;
        private const int MaxListedFits = 50;
        private const int MaxListedPerReason = 10;

        public static BridgeResponse CheckPlacement(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            if (map != Find.CurrentMap)
            {
                throw new BridgeException(409, "check_placement uses the game's build tool, which works on the map shown in game (map " + Find.CurrentMap.uniqueID + "). Switch to map " + map.uniqueID + " in game first.");
            }
            BuildPlacement building = BuildPlacement.Resolve(query.Get("def"), query.Get("stuff"), query.Get("rotation"), "");
            int? x = query.OptionalInt("x");
            int? z = query.OptionalInt("z");
            if (!x.HasValue || !z.HasValue)
            {
                throw new BridgeException(400, "Give x and z: the position to check, or the south-west corner of an area of positions.");
            }
            int width = Side(query, "width");
            int height = Side(query, "height");

            // Positions nearest the middle of the area come first, so the first fit is the most central.
            IntVec3 centre = new IntVec3(x.Value + width / 2, 0, z.Value + height / 2);
            List<IntVec3> positions = new List<IntVec3>();
            int outside = 0;
            for (int px = x.Value; px < x.Value + width; px++)
            {
                for (int pz = z.Value; pz < z.Value + height; pz++)
                {
                    IntVec3 cell = new IntVec3(px, 0, pz);
                    if (cell.InBounds(map))
                    {
                        positions.Add(cell);
                    }
                    else
                    {
                        outside++;
                    }
                }
            }
            positions = positions.OrderBy(cell => cell.DistanceToSquared(centre)).ThenBy(cell => cell.x).ThenBy(cell => cell.z).ToList();

            string unavailable = building.Unavailable(map);
            List<IntVec3> fits = new List<IntVec3>();
            Dictionary<string, List<IntVec3>> rejected = new Dictionary<string, List<IntVec3>>();
            if (unavailable == null)
            {
                foreach (IntVec3 cell in positions)
                {
                    string reason = building.CannotPlaceAt(cell);
                    if (reason == null)
                    {
                        fits.Add(cell);
                        continue;
                    }
                    List<IntVec3> cells;
                    if (!rejected.TryGetValue(reason, out cells))
                    {
                        rejected[reason] = cells = new List<IntVec3>();
                    }
                    cells.Add(cell);
                }
            }

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("building", building.Describe()),
                Dto.Field("available", unavailable == null),
                Dto.Field("reason", unavailable),
                Dto.Field("fitsCount", fits.Count),
                Dto.Field("fits", fits.Take(MaxListedFits).Select(Position).ToArray()),
                Dto.Field("rejected", rejected
                    .OrderByDescending(pair => pair.Value.Count)
                    .Select(pair => (object)Dto.Obj(
                        Dto.Field("reason", pair.Key),
                        Dto.Field("count", pair.Value.Count),
                        Dto.Field("positions", pair.Value.Take(MaxListedPerReason).Select(Position).ToArray())))
                    .ToArray()),
                Dto.Field("outsideMap", outside > 0 ? (object)outside : null),
                Dto.Field("blockers", unavailable == null ? Blockers.ToArray(BuildPlacement.BuildBlockers(map, new[] { building })) : null)));
        }

        // Each placement is checked and placed in order, so a later one cannot overlap an earlier
        // one. A placement that is already there, as a blueprint, frame or building, is satisfied.
        // A floor may also be a rectangle (x, z to x2, z2): each cell is checked and laid on its
        // own, the cells that cannot take it are skipped, and the answer counts them by reason.
        public static BridgeResponse PlaceBlueprint(BridgeRequest request, RouteMatch route)
        {
            PlaceBlueprintBody body = CommandUtil.ReadBody<PlaceBlueprintBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (body.Placements == null || body.Placements.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'placements'.");
            }
            if (body.Placements.Length > MaxPlacements)
            {
                throw new CommandException(400, "At most " + MaxPlacements + " placements per call.");
            }
            // Every placement is read before any is placed, so a malformed one changes nothing.
            CellRect?[] rects = ReadRectangles(body.Placements);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;
            BuildPlacement[] buildings = new BuildPlacement[body.Placements.Length];
            for (int i = 0; i < buildings.Length; i++)
            {
                PlacementBody placement = body.Placements[i];
                string field = "placements[" + i + "].";
                buildings[i] = BuildPlacement.Resolve(placement.Def, placement.Stuff, placement.Rotation, field);
                if (rects[i].HasValue && !buildings[i].IsFloor)
                {
                    throw new CommandException(400, field + "x2 and z2 give a rectangle, which only floors take; a building goes at one position.");
                }
            }
            // The material counts only when given: the game's default depends on what the colony has.
            RequestKey key = RequestKey.For("place_blueprint", requestId, map, string.Join(";", buildings.Select((building, i) =>
                building.Def.defName + "," + (string.IsNullOrWhiteSpace(body.Placements[i].Stuff) ? "" : building.Stuff.defName) + "," + BuildPlacement.RotationName(building.Rotation) + "," + body.Placements[i].X + "," + body.Placements[i].Z + "," + body.Placements[i].X2 + "," + body.Placements[i].Z2).ToArray()));
            return CommandReceipts.Once(context, key, () => Place(buildings, rects, body.Placements, map));
        }

        // The rectangles of the placements that give x2 and z2, null for the others, checked for
        // shape and for how many cells they cover together.
        private static CellRect?[] ReadRectangles(PlacementBody[] placements)
        {
            CellRect?[] rects = new CellRect?[placements.Length];
            int cells = 0;
            for (int i = 0; i < placements.Length; i++)
            {
                PlacementBody placement = placements[i];
                string field = "placements[" + i + "]";
                if (placement == null || !placement.X.HasValue || !placement.Z.HasValue)
                {
                    throw new CommandException(400, "Each placement needs def, x, and z (" + field + ").");
                }
                if (placement.X2.HasValue != placement.Z2.HasValue)
                {
                    throw new CommandException(400, "A rectangle needs both x2 and z2 (" + field + ").");
                }
                if (!placement.X2.HasValue)
                {
                    continue;
                }
                rects[i] = CellSweep.Between(placement.X.Value, placement.Z.Value, placement.X2.Value, placement.Z2.Value, field);
                cells += rects[i].Value.Area;
                if (cells > CommandCells.MaxCells)
                {
                    throw new CommandException(400, "The rectangles cover more than " + CommandCells.MaxCells + " cells; split the floor into several calls.");
                }
            }
            return rects;
        }

        // The counts in the answer are of cells: a rectangle counts each of its cells.
        private static CommandResult Place(BuildPlacement[] buildings, CellRect?[] rects, PlacementBody[] placements, Map map)
        {
            List<object> results = new List<object>();
            List<BuildPlacement> placed = new List<BuildPlacement>();
            int applied = 0, satisfied = 0, rejectedCount = 0;
            for (int i = 0; i < buildings.Length; i++)
            {
                BuildPlacement building = buildings[i];
                Lazy<string> unavailable = new Lazy<string>(() => building.Unavailable(map));
                if (rects[i].HasValue)
                {
                    CellSweep sweep;
                    results.Add(PlaceRectangle(building, rects[i].Value, map, unavailable, placed, out sweep));
                    applied += sweep.Applied;
                    satisfied += sweep.AlreadySatisfied;
                    rejectedCount += sweep.Rejected;
                    continue;
                }
                IntVec3 cell = new IntVec3(placements[i].X.Value, 0, placements[i].Z.Value);
                Thing thing;
                string state, reason;
                string outcome = PlaceOne(building, cell, map, unavailable, out thing, out state, out reason);
                // In god mode the build tool builds at once, and nothing blocks a finished building.
                if (outcome == "applied" && state != "built")
                {
                    placed.Add(building);
                }
                applied += outcome == "applied" ? 1 : 0;
                satisfied += outcome == "alreadySatisfied" ? 1 : 0;
                rejectedCount += outcome == "rejected" ? 1 : 0;
                results.Add(Dto.Obj(
                    Dto.Field("def", building.Def.defName),
                    Dto.Field("stuff", building.Stuff == null ? null : building.Stuff.defName),
                    Dto.Field("x", cell.x),
                    Dto.Field("z", cell.z),
                    Dto.Field("rotation", building.IsFloor ? null : BuildPlacement.RotationName(building.Rotation)),
                    Dto.Field("outcome", outcome),
                    Dto.Field("reason", reason),
                    Dto.Field("id", thing == null ? null : ReadUtil.ThingId(thing)),
                    Dto.Field("state", state),
                    Dto.Field("occupies", outcome == "rejected" ? null : building.Occupies(cell))));
            }

            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("applied", applied),
                Dto.Field("alreadySatisfied", satisfied),
                Dto.Field("rejectedCount", rejectedCount),
                Dto.Field("results", results.ToArray()),
                Dto.Field("blockers", Blockers.ToArray(BuildPlacement.BuildBlockers(map, placed))));
            return new CommandResult(applied > 0, data);
        }

        // One position: already there (a blueprint, frame or building, or a floor already laid),
        // refused with the game's reason, or placed. state is what is there after.
        private static string PlaceOne(BuildPlacement building, IntVec3 cell, Map map, Lazy<string> unavailable, out Thing thing, out string state, out string reason)
        {
            thing = null;
            state = null;
            reason = null;
            if (!cell.InBounds(map))
            {
                reason = "Outside the map.";
                return "rejected";
            }
            if (!cell.Fogged(map))
            {
                thing = building.Existing(cell, map);
                if (thing != null)
                {
                    state = BuildPlacement.StateOf(thing);
                    return "alreadySatisfied";
                }
                if (building.FloorAlreadyLaid(cell, map))
                {
                    state = "built";
                    return "alreadySatisfied";
                }
            }
            reason = unavailable.Value ?? building.CannotPlaceAt(cell);
            if (reason != null)
            {
                return "rejected";
            }
            thing = building.Place(cell, map);
            if (thing != null)
            {
                state = BuildPlacement.StateOf(thing);
                return "applied";
            }
            // In god mode the tool lays a floor at once, leaving no blueprint.
            if (building.FloorAlreadyLaid(cell, map))
            {
                state = "built";
                return "applied";
            }
            reason = "The game did not place it.";
            return "rejected";
        }

        // Every cell of the rectangle is tried; the cells that cannot take the floor are skipped and
        // counted by the game's reason for each.
        private static object PlaceRectangle(BuildPlacement floor, CellRect rect, Map map, Lazy<string> unavailable, List<BuildPlacement> placed, out CellSweep sweep)
        {
            sweep = new CellSweep();
            int outside = 0, builtAtOnce = 0, blueprints = 0;
            foreach (IntVec3 cell in rect)
            {
                if (!cell.InBounds(map))
                {
                    outside++;
                    continue;
                }
                Thing thing;
                string state, reason;
                string outcome = PlaceOne(floor, cell, map, unavailable, out thing, out state, out reason);
                if (outcome == "applied")
                {
                    sweep.Apply();
                    if (state == "built")
                    {
                        builtAtOnce++;
                    }
                    else
                    {
                        blueprints++;
                        placed.Add(floor);
                    }
                }
                else if (outcome == "alreadySatisfied")
                {
                    sweep.Satisfy();
                }
                else
                {
                    sweep.Reject(cell, reason);
                }
            }
            return Dto.Obj(
                Dto.Field("def", floor.Def.defName),
                Dto.Field("x1", rect.minX),
                Dto.Field("z1", rect.minZ),
                Dto.Field("x2", rect.maxX),
                Dto.Field("z2", rect.maxZ),
                Dto.Field("outcome", sweep.Outcome),
                Dto.Field("applied", sweep.Applied),
                Dto.Field("alreadySatisfied", sweep.AlreadySatisfied),
                Dto.Field("rejectedCount", sweep.Rejected),
                Dto.Field("outsideMap", outside > 0 ? (object)outside : null),
                Dto.Field("builtAtOnce", builtAtOnce > 0 ? (object)builtAtOnce : null),
                Dto.Field("reasons", sweep.Rejected > 0 ? sweep.Reasons() : null),
                Dto.Field("costPerCell", CostDto(floor.Cost(), 1)),
                Dto.Field("totalCost", blueprints > 0 ? CostDto(floor.Cost(), blueprints) : null),
                Dto.Field("workPerCell", Math.Round(floor.Def.GetStatValueAbstract(StatDefOf.WorkToBuild, null))));
        }

        private static object[] CostDto(IEnumerable<ThingDefCountClass> cost, int times)
        {
            return cost.Select(entry => (object)Dto.Obj(Dto.Field("defName", entry.thingDef.defName), Dto.Field("count", entry.count * times))).ToArray();
        }

        // Takes the constructed floor off cells with the game's Remove floor tool, which marks
        // each for a colonist with Construction to remove (at once in god mode). Natural ground has
        // nothing to remove, and a floor a building stands on stays when the building needs it.
        // cancel takes the removal orders off instead.
        public static BridgeResponse RemoveFloor(BridgeRequest request, RouteMatch route)
        {
            RemoveFloorBody body = CommandUtil.ReadBody<RemoveFloorBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (body.Rects == null || body.Rects.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'rects'.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;
            int outside;
            List<IntVec3> cells = CommandCells.FromRects(body.Rects, map, "rects", out outside);
            bool cancel = body.Cancel ?? false;
            RequestKey key = RequestKey.For("remove_floor", requestId, map, body.Rects, cancel);
            return CommandReceipts.Once(context, key, () => RemoveFloors(cells, outside, cancel, map));
        }

        private static CommandResult RemoveFloors(List<IntVec3> cells, int outside, bool cancel, Map map)
        {
            Designator_RemoveFloor tool = new Designator_RemoveFloor();
            CellSweep sweep = new CellSweep();
            foreach (IntVec3 cell in cells)
            {
                Designation pending = map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor);
                if (cancel)
                {
                    if (pending == null)
                    {
                        sweep.Satisfy();
                    }
                    else
                    {
                        map.designationManager.RemoveDesignation(pending);
                        sweep.Apply();
                    }
                    continue;
                }
                if (pending != null)
                {
                    sweep.Satisfy();
                    continue;
                }
                AcceptanceReport report = cell.Fogged(map) ? AcceptanceReport.WasRejected : tool.CanDesignateCell(cell);
                if (!report.Accepted)
                {
                    sweep.Reject(cell, FloorRemovalRefusal(cell, map, report));
                    continue;
                }
                tool.DesignateSingleCell(cell);
                sweep.Apply();
            }
            // The orders do nothing until someone with Construction does them.
            List<object> blockers = new List<object>();
            if (!cancel && sweep.Applied > 0 && !Blockers.AnyWorker(map, WorkTypeDefOf.Construction, SkillDefOf.Construction, 0))
            {
                blockers.Add(Blockers.Of("noConstructor",
                    "No colonist has Construction on, so the floors will not be removed.",
                    Dto.Field("skill", SkillDefOf.Construction.defName)));
            }
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("cancel", cancel ? (object)true : null),
                Dto.Field("applied", sweep.Applied),
                Dto.Field("alreadySatisfied", sweep.AlreadySatisfied),
                Dto.Field("rejectedCount", sweep.Rejected),
                Dto.Field("reasons", sweep.Rejected > 0 ? sweep.Reasons() : null),
                Dto.Field("outsideMap", outside > 0 ? (object)outside : null),
                Dto.Field("blockers", Blockers.ToArray(blockers)));
            return new CommandResult(sweep.Applied > 0, data);
        }

        // The game's tool often refuses without a reason, so name it from the rules it applies.
        private static string FloorRemovalRefusal(IntVec3 cell, Map map, AcceptanceReport report)
        {
            if (cell.Fogged(map))
            {
                return "Unexplored.";
            }
            Building edifice = cell.GetEdifice(map);
            if (edifice != null && edifice.def.Fillage == FillCategory.Full && edifice.def.passability == Traversability.Impassable)
            {
                return "A wall or rock fills the cell.";
            }
            if (!map.terrainGrid.CanRemoveTopLayerAt(cell))
            {
                return "No constructed floor to remove: natural ground cannot be removed.";
            }
            if (WorkGiver_ConstructRemoveFloor.AnyBuildingBlockingFloorRemoval(cell, map))
            {
                return "A building on the cell needs the floor under it.";
            }
            return string.IsNullOrWhiteSpace(report.Reason) ? "The floor cannot be removed here." : report.Reason.StripTags();
        }

        private static int Side(ReadRequest query, string field)
        {
            int side = query.OptionalInt(field) ?? 1;
            if (side < 1 || side > MaxCheckSide)
            {
                throw new BridgeException(400, "Field '" + field + "' must be 1 to " + MaxCheckSide + ".");
            }
            return side;
        }

        private static object Position(IntVec3 cell)
        {
            return Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z));
        }

        [DataContract]
        private sealed class PlaceBlueprintBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "placements")]
            public PlacementBody[] Placements { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class PlacementBody
        {
            [DataMember(Name = "def")]
            public string Def { get; set; }

            [DataMember(Name = "stuff")]
            public string Stuff { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "rotation")]
            public string Rotation { get; set; }

            [DataMember(Name = "x2")]
            public int? X2 { get; set; }

            [DataMember(Name = "z2")]
            public int? Z2 { get; set; }
        }

        [DataContract]
        private sealed class RemoveFloorBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "rects")]
            public CellRectBody[] Rects { get; set; }

            [DataMember(Name = "cancel")]
            public bool? Cancel { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
