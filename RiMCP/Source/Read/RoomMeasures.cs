using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RiMCP.Read
{
    // The pieces of a room read that need no game object beyond cells and numbers, so they can be
    // checked on their own: where a room is, how its cells are lit, what is outside its walls, and
    // what it stands on.
    internal static class RoomMeasures
    {
        // Cells with less glow than this are dark: it is where the game's WorkSpeedGlobal factor
        // (0.8 at no light) reaches 1.0, so a worker in a darker cell is slowed.
        public const float DarkGlow = 0.3f;

        // The corners of the cells' bounding box, or null for no cells.
        public static object Bounds(IList<IntVec3> cells)
        {
            if (cells.Count == 0)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("x1", cells.Min(cell => cell.x)),
                Dto.Field("z1", cells.Min(cell => cell.z)),
                Dto.Field("x2", cells.Max(cell => cell.x)),
                Dto.Field("z2", cells.Max(cell => cell.z)));
        }

        // The cell every room id is built from: the lowest row, then the westmost cell in it.
        public static IntVec3 Anchor(IEnumerable<IntVec3> cells)
        {
            IntVec3 anchor = IntVec3.Invalid;
            foreach (IntVec3 cell in cells)
            {
                if (!anchor.IsValid || cell.z < anchor.z || (cell.z == anchor.z && cell.x < anchor.x))
                {
                    anchor = cell;
                }
            }
            return anchor;
        }

        // The cells as rows from the south, each with its runs of consecutive x as [from, to]:
        // a 5 by 4 room is four rows of one run each, not twenty positions.
        public static object[] Runs(IEnumerable<IntVec3> cells)
        {
            List<object> rows = new List<object>();
            foreach (IGrouping<int, IntVec3> row in cells.GroupBy(cell => cell.z).OrderBy(group => group.Key))
            {
                List<object> runs = new List<object>();
                int from = 0, to = 0;
                bool open = false;
                foreach (int x in row.Select(cell => cell.x).Distinct().OrderBy(x => x))
                {
                    if (open && x == to + 1)
                    {
                        to = x;
                        continue;
                    }
                    if (open)
                    {
                        runs.Add(new[] { from, to });
                    }
                    from = to = x;
                    open = true;
                }
                if (open)
                {
                    runs.Add(new[] { from, to });
                }
                rows.Add(Dto.Obj(Dto.Field("z", row.Key), Dto.Field("runs", runs.ToArray())));
            }
            return rows.ToArray();
        }

        // Light over the cells, as the game's glow grid reads it. glow is what a colonist standing
        // there gets now, sky included; glowWithoutSky is what the lamps alone give, which is what
        // the cell has at night. A roofed cell has no sky light, so the two are the same.
        public sealed class Light
        {
            public int Cells;
            public int Dark;
            public int DarkWithoutSky;
            public float MinGlow = float.MaxValue;
            public float MinGlowWithoutSky = float.MaxValue;

            public void Add(float glow, float glowWithoutSky)
            {
                Cells++;
                Dark += glow < DarkGlow ? 1 : 0;
                DarkWithoutSky += glowWithoutSky < DarkGlow ? 1 : 0;
                MinGlow = Math.Min(MinGlow, glow);
                MinGlowWithoutSky = Math.Min(MinGlowWithoutSky, glowWithoutSky);
            }

            public Dictionary<string, object> ToDto()
            {
                return Dto.Obj(
                    Dto.Field("darkCells", Dark),
                    Dto.Field("litCells", Cells - Dark),
                    Dto.Field("minGlow", Cells == 0 ? null : (object)Math.Round(MinGlow, 2)),
                    Dto.Field("darkCellsWithoutSky", DarkWithoutSky),
                    Dto.Field("minGlowWithoutSky", Cells == 0 ? null : (object)Math.Round(MinGlowWithoutSky, 2)));
            }
        }

        // What the cell two steps out from a room's edge is, which is what the room's temperature
        // is pulled toward through the wall (RoomTempTracker): the air of another room or the
        // outdoors where the wall is one cell thick, nothing where it is two or more cells thick
        // (the room then leans toward the outdoor temperature at half strength), or a door.
        public enum Exposure
        {
            Outdoors,
            OtherRoom,
            Solid,
            Door
        }

        public static Dictionary<string, object> Walls(IList<Exposure> exposures)
        {
            int total = exposures.Count;
            int outdoors = exposures.Count(e => e == Exposure.Outdoors);
            int rooms = exposures.Count(e => e == Exposure.OtherRoom);
            int solid = exposures.Count(e => e == Exposure.Solid);
            int doors = exposures.Count(e => e == Exposure.Door);
            return Dto.Obj(
                Dto.Field("equalizeCells", total),
                Dto.Field("thinToOutdoors", outdoors),
                Dto.Field("thinToRooms", rooms),
                Dto.Field("thick", solid),
                Dto.Field("toDoors", doors),
                Dto.Field("thickShare", total == 0 ? null : (object)Math.Round((double)solid / total, 2)));
        }

        // The terrain under the cells: how many are natural ground and how many hold a floor the
        // colony built, and by terrain (most cells first) with what each does to the room.
        public struct FloorCell
        {
            public string DefName;
            public string Label;
            public bool Constructed;
            public float Beauty;
            public float Cleanliness;
        }

        public static Dictionary<string, object> Floor(IEnumerable<FloorCell> cells, bool byTerrain)
        {
            List<FloorCell> list = cells.ToList();
            List<IGrouping<string, FloorCell>> groups = list.GroupBy(cell => cell.DefName).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToList();
            return Dto.Obj(
                Dto.Field("constructedCells", list.Count(cell => cell.Constructed)),
                Dto.Field("naturalCells", list.Count(cell => !cell.Constructed)),
                Dto.Field("dominant", groups.Count == 0 ? null : groups[0].Key),
                Dto.Field("byTerrain", !byTerrain ? null : groups.Select(group => (object)Dto.Obj(
                    Dto.Field("defName", group.Key),
                    Dto.Field("label", group.First().Label),
                    Dto.Field("cells", group.Count()),
                    Dto.Field("constructed", group.First().Constructed),
                    Dto.Field("beauty", Math.Round(group.First().Beauty, 2)),
                    Dto.Field("cleanliness", Math.Round(group.First().Cleanliness, 2)))).ToArray()));
        }
    }
}
