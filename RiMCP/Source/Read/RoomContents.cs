using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // What a room holds, by kind, from the same list the game scores the room's role from
    // (Room.ContainedAndAdjacentThings: things in the room and the buildings in its walls that face
    // it, such as a cooler or a wall lamp). Finished buildings only; walls, conduits and other
    // linked structure are left out, and the rest not named as a kind are counted by def.
    internal static class RoomContents
    {
        private const int MaxSpots = 10;

        public sealed class Result
        {
            public readonly Dictionary<string, object> Counts = new Dictionary<string, object>();
            public readonly Dictionary<string, object> Things = new Dictionary<string, object>();
        }

        public static Result Read(Map map, Room room, HashSet<IntVec3> cells, RoomIndex index)
        {
            Result result = new Result();
            Dictionary<string, List<object>> kinds = new Dictionary<string, List<object>>();
            Dictionary<string, int> others = new Dictionary<string, int>();
            Dictionary<ThingDef, List<Thing>> explosives = new Dictionary<ThingDef, List<Thing>>();
            // The game's list is reused by its next call, so it is copied.
            foreach (Thing thing in room.ContainedAndAdjacentThings.ToList())
            {
                if (thing == null || !thing.Spawned || thing is Frame || thing is Blueprint)
                {
                    continue;
                }
                ThingWithComps withComps = thing as ThingWithComps;
                if (withComps != null && withComps.GetComp<CompExplosive>() != null)
                {
                    List<Thing> things;
                    if (!explosives.TryGetValue(thing.def, out things))
                    {
                        explosives[thing.def] = things = new List<Thing>();
                    }
                    things.Add(thing);
                }
                Building building = thing as Building;
                if (building == null)
                {
                    continue;
                }
                string kind;
                object entry = Describe(building, map, cells, index, out kind);
                if (kind == null)
                {
                    continue;
                }
                if (kind == "other")
                {
                    int count;
                    others.TryGetValue(building.def.defName, out count);
                    others[building.def.defName] = count + 1;
                    continue;
                }
                List<object> list;
                if (!kinds.TryGetValue(kind, out list))
                {
                    kinds[kind] = list = new List<object>();
                }
                list.Add(entry);
            }

            foreach (KeyValuePair<string, List<object>> pair in kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                result.Counts[pair.Key] = pair.Value.Count;
                result.Things[pair.Key] = pair.Value.ToArray();
            }
            AddStockpiles(map, cells, result);
            if (explosives.Count > 0)
            {
                result.Counts["explosives"] = explosives.Values.Sum(things => things.Count);
                result.Things["explosives"] = explosives
                    .OrderBy(pair => pair.Key.defName, StringComparer.Ordinal)
                    .Select(pair => (object)Dto.Obj(
                        Dto.Field("def", ReadUtil.Def(pair.Key)),
                        Dto.Field("things", pair.Value.Count),
                        Dto.Field("stackCount", pair.Value.Sum(thing => thing.stackCount)),
                        Dto.Field("positions", pair.Value.Take(MaxSpots).Select(thing => Spot(thing.Position)).ToArray())))
                    .ToArray();
            }
            if (others.Count > 0)
            {
                result.Counts["otherBuildings"] = others.Values.Sum();
                result.Things["otherBuildings"] = others.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => (object)pair.Value);
            }
            return result;
        }

        // One building: its kind (null to leave it out) and what to say about it.
        private static object Describe(Building building, Map map, HashSet<IntVec3> cells, RoomIndex index, out string kind)
        {
            kind = null;
            ThingDef def = building.def;
            if (building is Building_Door)
            {
                return null;
            }
            Dictionary<string, object> dto = Basics(building, cells);
            CompPowerTrader power = building.GetComp<CompPowerTrader>();
            Building_Bed bed = building as Building_Bed;
            if (bed != null)
            {
                kind = "beds";
                dto["ownerType"] = bed.ForOwnerType.ToString().ToLowerInvariant();
                dto["medical"] = bed.Medical;
                dto["slots"] = bed.SleepingSlotsCount;
                dto["owners"] = bed.CompAssignableToPawn == null ? new object[0] : bed.OwnersForReading.Select(pawn => (object)ReadUtil.PawnName(pawn)).ToArray();
                return dto;
            }
            if (building is Building_ResearchBench)
            {
                kind = "researchBenches";
                AddPower(dto, power);
                return dto;
            }
            IBillGiver giver = building as IBillGiver;
            if (giver != null)
            {
                kind = IsButcherTable(def) ? "butcherTables" : MakesFood(def) ? "stoves" : "workTables";
                dto["bills"] = giver.BillStack.Bills.Select(bill => (object)Dto.Obj(
                    Dto.Field("label", bill.Label),
                    Dto.Field("suspended", bill.suspended))).ToArray();
                AddPower(dto, power);
                return dto;
            }
            Building_TempControl control = building as Building_TempControl;
            if (control != null)
            {
                AddPower(dto, power);
                dto["rotation"] = building.Rotation.ToStringHuman().ToLowerInvariant();
                // The sides a cooler or vent works across, by the game's own geometry.
                IntVec3 front = building.Position + IntVec3.South.RotatedBy(building.Rotation);
                IntVec3 back = building.Position + IntVec3.North.RotatedBy(building.Rotation);
                if (building is Building_Cooler)
                {
                    kind = "coolers";
                    dto["targetC"] = TargetOf(building);
                    dto["running"] = control.compTempControl != null && control.compTempControl.operatingAtHighPower;
                    dto["coldSide"] = index.Side(front);
                    dto["hotSide"] = index.Side(back);
                }
                else if (building is Building_Heater)
                {
                    kind = "heaters";
                    dto["targetC"] = TargetOf(building);
                    dto["running"] = control.compTempControl != null && control.compTempControl.operatingAtHighPower;
                }
                else if (building is Building_Vent)
                {
                    kind = "vents";
                    dto["open"] = FlickUtility.WantsToBeOn(building);
                    dto["between"] = new[] { index.Side(front), index.Side(back) };
                }
                else
                {
                    kind = "other";
                }
                return dto;
            }
            CompGlower glower = building.GetComp<CompGlower>();
            if (glower != null)
            {
                kind = "lamps";
                CompSchedule schedule = building.GetComp<CompSchedule>();
                dto["on"] = glower.Glows;
                dto["radius"] = Math.Round(glower.GlowRadius, 1);
                dto["scheduledOff"] = schedule != null && !schedule.Allowed;
                dto["switchedOff"] = !FlickUtility.WantsToBeOn(building) && (schedule == null || schedule.Allowed);
                AddPower(dto, power);
                return dto;
            }
            Building_Storage storage = building as Building_Storage;
            if (storage != null)
            {
                kind = "shelves";
                SlotGroup group = storage.GetSlotGroup();
                dto["cells"] = group == null ? 0 : group.CellsList.Count;
                dto["priority"] = group == null ? null : group.Settings.Priority.ToString();
                dto["stackCount"] = group == null ? 0 : group.HeldThings.Count();
                return dto;
            }
            if (IsStructure(def))
            {
                return null;
            }
            kind = "other";
            return dto;
        }

        private static void AddStockpiles(Map map, HashSet<IntVec3> cells, Result result)
        {
            List<object> zones = new List<object>();
            int stockpileCells = 0;
            foreach (Zone zone in map.zoneManager.AllZones)
            {
                Zone_Stockpile stockpile = zone as Zone_Stockpile;
                if (stockpile == null)
                {
                    continue;
                }
                int inside = stockpile.Cells.Count(cells.Contains);
                if (inside == 0)
                {
                    continue;
                }
                stockpileCells += inside;
                zones.Add(Dto.Obj(
                    Dto.Field("id", ZoneReadService.ZoneId(map, zone)),
                    Dto.Field("label", zone.label),
                    Dto.Field("cellsInRoom", inside),
                    Dto.Field("cells", stockpile.Cells.Count),
                    Dto.Field("priority", stockpile.GetStoreSettings().Priority.ToString())));
            }
            if (stockpileCells > 0)
            {
                result.Counts["stockpileCells"] = stockpileCells;
                result.Things["stockpiles"] = zones.ToArray();
            }
        }

        private static Dictionary<string, object> Basics(Building building, HashSet<IntVec3> cells)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(building)),
                Dto.Field("def", ReadUtil.Def(building.def)),
                Dto.Field("position", Spot(building.Position)));
            if (!building.OccupiedRect().Cells.Any(cells.Contains))
            {
                dto["adjacent"] = true;
            }
            return dto;
        }

        private static void AddPower(Dictionary<string, object> dto, CompPowerTrader power)
        {
            if (power != null)
            {
                dto["powered"] = power.PowerOn;
            }
        }

        private static object TargetOf(Building building)
        {
            CompTempControl control = building.GetComp<CompTempControl>();
            return control == null ? null : (object)Math.Round(control.TargetTemperature, 1);
        }

        public static object Spot(IntVec3 cell)
        {
            return Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z));
        }

        private static bool IsButcherTable(ThingDef def)
        {
            return def.AllRecipes != null && def.AllRecipes.Any(recipe => recipe.defName == "ButcherCorpseFlesh");
        }

        // The game's own test for a Kitchen: a production building with a recipe whose product a
        // person can eat (RoomRoleWorker_Kitchen).
        private static bool MakesFood(ThingDef def)
        {
            if (def.designationCategory != DesignationCategoryDefOf.Production || def.AllRecipes == null)
            {
                return false;
            }
            return def.AllRecipes.Any(recipe => recipe.products.Any(product => product.thingDef.IsNutritionGivingIngestible && product.thingDef.ingestible.HumanEdible));
        }

        // Walls, conduits and the like draw as linked pieces; the game's tables are not among them.
        private static bool IsStructure(ThingDef def)
        {
            return (def.graphicData != null && def.graphicData.linkType != LinkDrawerType.None) || (def.IsEdifice() && def.passability == Traversability.Impassable && def.fillPercent >= 1f);
        }
    }
}
