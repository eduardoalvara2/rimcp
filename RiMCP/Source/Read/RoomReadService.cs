using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    // The base as rooms: each closed room with where it is, what the game calls it, the stats the
    // game computes for it, its temperature, roof, doors, the walls between it and the outdoors,
    // light, floor and contents. The default answer is a summary of each; the heavy parts (door and
    // lamp lists, contents by kind, terrain by def, the cells) come with detail or include.
    internal static class RoomReadService
    {
        private const int MaxSpots = 40;

        // Sections of a room that can be asked for with include; detail normal and full give all
        // but the cells, which only include=cells or full give.
        public static readonly string[] SectionNames = { "doors", "walls", "light", "floor", "contents", "cells" };

        public static BridgeResponse ListRooms(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            Sections wanted = ReadSections(query);
            List<RoomRoleDef> roles = query.DefList<RoomRoleDef>("roles");
            HashSet<string> ids = new HashSet<string>(query.List("rooms"), StringComparer.Ordinal);

            RoomIndex index = new RoomIndex(map);
            List<RoomView> all = EnvironmentReadService.Rooms(map)
                .Select(room => new RoomView(room, index))
                .ToList();
            IEnumerable<RoomView> matching = all
                .Where(view => roles.Count == 0 || roles.Contains(view.Room.Role))
                .Where(view => ids.Count == 0 || ids.Contains(view.Id))
                .OrderBy(view => view.Room.Role == null ? "" : view.Room.Role.defName, StringComparer.Ordinal)
                .ThenBy(view => view.Anchor.z)
                .ThenBy(view => view.Anchor.x)
                .ToList();
            Page<RoomView> page = new Page<RoomView>(matching, query);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("total", matching.Count()),
                Dto.Field("byRole", all
                    .GroupBy(view => view.Room.Role == null ? "None" : view.Room.Role.defName)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => (object)group.Count())),
                Dto.Field("outdoorTemperatureC", map.mapTemperature == null ? null : (object)Math.Round(map.mapTemperature.OutdoorTemp, 1)),
                Dto.Field("rooms", page.Items.Select(view => SerializeRoom(map, view, index, wanted)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        // include names this tool does not know are refused, so a typo is not a silent omission.
        public static Sections ReadSections(ReadRequest query)
        {
            foreach (string name in query.Include)
            {
                if (!SectionNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new BridgeException(400, "Unknown include '" + name + "'. Sections: " + string.Join(", ", SectionNames) + ".");
                }
            }
            bool all = query.Detail != ReadDetail.Summary;
            return new Sections
            {
                Doors = all || query.Include.Contains("doors"),
                Walls = all || query.Include.Contains("walls"),
                Light = all || query.Include.Contains("light"),
                Floor = all || query.Include.Contains("floor"),
                Contents = all || query.Include.Contains("contents"),
                Cells = query.Detail == ReadDetail.Full || query.Include.Contains("cells")
            };
        }

        internal sealed class Sections
        {
            public bool Doors, Walls, Light, Floor, Contents, Cells;
        }

        private static object SerializeRoom(Map map, RoomView view, RoomIndex index, Sections wanted)
        {
            Room room = view.Room;
            RoomRoleDef role = room.Role;
            List<IntVec3> cells = view.Cells;
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", view.Id),
                Dto.Field("role", ReadUtil.Def(role)),
                Dto.Field("properRoom", room.ProperRoom),
                Dto.Field("bbox", RoomMeasures.Bounds(cells)),
                Dto.Field("cellCount", cells.Count));
            AddStats(dto, room);
            float temperature = room.Temperature;
            dto["temperatureC"] = Math.Round(temperature, 1);
            dto["comfortable"] = temperature >= ThingDefOf.Human.GetStatValueAbstract(StatDefOf.ComfyTemperatureMin) && temperature <= ThingDefOf.Human.GetStatValueAbstract(StatDefOf.ComfyTemperatureMax);
            dto["usesOutdoorTemperature"] = room.UsesOutdoorTemperature;
            dto["psychologicallyOutdoors"] = room.PsychologicallyOutdoors;
            dto["roof"] = Roof(map, cells);

            List<Building_Door> doors = Doors(map, room, cells);
            dto["doorCount"] = doors.Count;
            if (wanted.Doors)
            {
                dto["doors"] = doors.Select(door => SerializeDoor(map, room, door, index)).ToArray();
            }

            List<RoomMeasures.Exposure> exposures = Exposures(map, room);
            Dictionary<string, object> walls = RoomMeasures.Walls(exposures);
            if (wanted.Walls)
            {
                walls["exposedToOutdoorAir"] = ExposedSpots(map, room);
            }
            dto["walls"] = walls;

            RoomMeasures.Light light = new RoomMeasures.Light();
            foreach (IntVec3 cell in cells)
            {
                light.Add(map.glowGrid.GroundGlowAt(cell), map.glowGrid.GroundGlowAt(cell, false, true));
            }
            dto["light"] = light.ToDto();

            Dictionary<TerrainDef, float[]> terrainStats = new Dictionary<TerrainDef, float[]>();
            dto["floor"] = RoomMeasures.Floor(cells.Select(cell => FloorAt(map, cell, terrainStats)), wanted.Floor);

            RoomContents.Result contents = RoomContents.Read(map, room, new HashSet<IntVec3>(cells), index);
            dto["contents"] = contents.Counts;
            if (contents.Things.ContainsKey("lamps"))
            {
                Dictionary<string, object>[] lamps = ((object[])contents.Things["lamps"]).Cast<Dictionary<string, object>>().ToArray();
                Dictionary<string, object> lightDto = (Dictionary<string, object>)dto["light"];
                lightDto["lampsOn"] = lamps.Count(lamp => (bool)lamp["on"]);
                lightDto["lampsOff"] = lamps.Count(lamp => !(bool)lamp["on"]);
                lightDto["lampsScheduledOff"] = lamps.Count(lamp => (bool)lamp["scheduledOff"]);
                // The lamps are the light's own detail, so they are listed there and not twice.
                if (wanted.Light)
                {
                    lightDto["lamps"] = contents.Things["lamps"];
                    contents.Things.Remove("lamps");
                }
            }
            if (wanted.Contents)
            {
                dto["things"] = contents.Things;
            }
            if (wanted.Cells)
            {
                dto["cells"] = RoomMeasures.Runs(cells);
            }
            return dto;
        }

        // Every stat the game computes for the room, by its defName, with the label of the stage
        // the value falls in for the stats that have stages (impressiveness "decent", cleanliness
        // "dirty"). A room too big to be a proper room, or open to the map edge, gets the game's
        // roomless scores.
        private static void AddStats(Dictionary<string, object> dto, Room room)
        {
            Dictionary<string, object> stats = new Dictionary<string, object>();
            Dictionary<string, object> labels = new Dictionary<string, object>();
            foreach (RoomStatDef stat in DefDatabase<RoomStatDef>.AllDefsListForReading.OrderBy(def => def.defName, StringComparer.Ordinal))
            {
                float value = room.GetStat(stat);
                stats[stat.defName] = Math.Round(value, 3);
                RoomStatScoreStage stage = stat.GetScoreStage(value);
                if (stage != null && !string.IsNullOrWhiteSpace(stage.label))
                {
                    labels[stat.defName] = stage.label;
                }
            }
            dto["stats"] = stats;
            dto["statLabels"] = labels.Count == 0 ? null : labels;
        }

        private static object Roof(Map map, List<IntVec3> cells)
        {
            int unroofed = 0, thick = 0, thin = 0;
            foreach (IntVec3 cell in cells)
            {
                RoofDef roof = cell.GetRoof(map);
                if (roof == null)
                {
                    unroofed++;
                }
                else if (roof.isThickRoof)
                {
                    thick++;
                }
                else
                {
                    thin++;
                }
            }
            return Dto.Obj(
                Dto.Field("roofed", thick + thin),
                Dto.Field("unroofed", unroofed),
                Dto.Field("thick", thick),
                Dto.Field("thin", thin));
        }

        // The doors in the room's edge, each once, in position order.
        private static List<Building_Door> Doors(Map map, Room room, List<IntVec3> cells)
        {
            HashSet<Building_Door> doors = new HashSet<Building_Door>();
            foreach (IntVec3 cell in cells)
            {
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 neighbour = cell + GenAdj.CardinalDirections[i];
                    if (!neighbour.InBounds(map))
                    {
                        continue;
                    }
                    Building_Door door = neighbour.GetDoor(map);
                    if (door != null)
                    {
                        doors.Add(door);
                    }
                }
            }
            return doors.OrderBy(door => door.Position.z).ThenBy(door => door.Position.x).ToList();
        }

        private static object SerializeDoor(Map map, Room room, Building_Door door, RoomIndex index)
        {
            List<object> to = new List<object>();
            for (int i = 0; i < 4; i++)
            {
                IntVec3 side = door.Position + GenAdj.CardinalDirections[i];
                if (!side.InBounds(map))
                {
                    continue;
                }
                Room other = side.GetRoom(map);
                if (other == null || other == room || other.IsDoorway)
                {
                    continue;
                }
                string reference = index.IdOf(other);
                if (!to.Contains(reference))
                {
                    to.Add(reference);
                }
            }
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(door)),
                Dto.Field("def", ReadUtil.Def(door.def)),
                Dto.Field("position", RoomContents.Spot(door.Position)),
                Dto.Field("to", to.ToArray()),
                Dto.Field("heldOpen", door.HoldOpen),
                Dto.Field("open", door.Open),
                Dto.Field("powered", door.powerComp == null ? null : (object)door.DoorPowerOn));
        }

        // What each of the room's equalization cells is: the cells two steps out from its edge that
        // RoomTempTracker pulls the room's temperature toward. A room open to the outdoor
        // temperature has none.
        private static List<RoomMeasures.Exposure> Exposures(Map map, Room room)
        {
            List<RoomMeasures.Exposure> result = new List<RoomMeasures.Exposure>();
            foreach (IntVec3 cell in room.TempTracker.EqualizeCellsForReading)
            {
                result.Add(ExposureOf(map, cell));
            }
            return result;
        }

        private static RoomMeasures.Exposure ExposureOf(Map map, IntVec3 cell)
        {
            Room other = cell.GetRoom(map);
            if (other == null)
            {
                return RoomMeasures.Exposure.Solid;
            }
            if (other.IsDoorway)
            {
                return RoomMeasures.Exposure.Door;
            }
            return other.TouchesMapEdge || other.UsesOutdoorTemperature ? RoomMeasures.Exposure.Outdoors : RoomMeasures.Exposure.OtherRoom;
        }

        // Where the room's wall is one cell thick against outdoor air: the cells two out from its
        // edge, which are open air. They are where a freezer loses its cold.
        private static object[] ExposedSpots(Map map, Room room)
        {
            return room.TempTracker.EqualizeCellsForReading
                .Where(cell => ExposureOf(map, cell) == RoomMeasures.Exposure.Outdoors)
                .OrderBy(cell => cell.z)
                .ThenBy(cell => cell.x)
                .Take(MaxSpots)
                .Select(cell => RoomContents.Spot(cell))
                .ToArray();
        }

        private static RoomMeasures.FloorCell FloorAt(Map map, IntVec3 cell, Dictionary<TerrainDef, float[]> stats)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            float[] values;
            if (!stats.TryGetValue(terrain, out values))
            {
                stats[terrain] = values = new[] { terrain.GetStatValueAbstract(StatDefOf.Beauty), terrain.GetStatValueAbstract(StatDefOf.Cleanliness) };
            }
            return new RoomMeasures.FloorCell
            {
                DefName = terrain.defName,
                Label = ReadUtil.DefLabel(terrain),
                // A floor laid on the ground (the kind Remove floor takes off), a foundation, or a
                // def tagged as a floor.
                Constructed = map.terrainGrid.UnderTerrainAt(cell) != null || map.terrainGrid.FoundationAt(cell) != null || terrain.IsFloor,
                Beauty = values[0],
                Cleanliness = values[1]
            };
        }

        // A room's cells, read once, with the id built from them.
        private sealed class RoomView
        {
            public readonly Room Room;
            public readonly List<IntVec3> Cells;
            public readonly IntVec3 Anchor;
            public readonly string Id;

            public RoomView(Room room, RoomIndex index)
            {
                Room = room;
                Cells = room.Cells.ToList();
                Anchor = RoomMeasures.Anchor(Cells);
                Id = index.Register(room, Anchor);
            }
        }
    }

    // Names rooms for one answer: "room:<map>:<x>,<z>" from a room's lowest, westmost cell, which
    // holds while the room keeps its shape, and "outdoors" for anything open to the map edge. The
    // same ids name the room a door or a cooler leads to.
    internal sealed class RoomIndex
    {
        private readonly Map map;
        private readonly Dictionary<Room, string> ids = new Dictionary<Room, string>();

        public RoomIndex(Map map)
        {
            this.map = map;
        }

        public string Register(Room room, IntVec3 anchor)
        {
            string id = "room:" + map.uniqueID + ":" + anchor.x + "," + anchor.z;
            ids[room] = id;
            return id;
        }

        public string IdOf(Room room)
        {
            if (room.TouchesMapEdge)
            {
                return "outdoors";
            }
            string id;
            if (!ids.TryGetValue(room, out id))
            {
                id = Register(room, RoomMeasures.Anchor(room.Cells));
            }
            return id;
        }

        // A cell beside a building: where it is, what room it is in (null for a wall), and whether
        // it is under a roof.
        public object Side(IntVec3 cell)
        {
            if (!cell.InBounds(map))
            {
                return null;
            }
            Room room = cell.GetRoom(map);
            return Dto.Obj(
                Dto.Field("x", cell.x),
                Dto.Field("z", cell.z),
                Dto.Field("room", room == null || room.IsDoorway ? null : IdOf(room)),
                Dto.Field("roofed", cell.Roofed(map)));
        }
    }
}
