using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RiMCP.Command
{
    // The two roof areas, painted with the rules of the game's roof tools. Colonists with
    // Construction work build roofs over the build-roof area and remove them under the no-roof area
    // (WorkGiver_BuildRoof, WorkGiver_RemoveRoof), and the no-roof area also keeps the game from
    // roofing a new room's cells by itself (AutoBuildRoofAreaSetter). Each tool takes the cells it
    // paints out of the other area, so a cell is in one of them at most.
    internal static class RoofAreas
    {
        public const string BuildRoofId = "buildRoof";
        public const string NoRoofId = "noRoof";

        // The roof area set_area names, or null for any other id.
        public static Area Named(Map map, string areaId)
        {
            if (string.Equals(areaId, BuildRoofId, StringComparison.OrdinalIgnoreCase))
            {
                return map.areaManager.BuildRoof;
            }
            if (string.Equals(areaId, NoRoofId, StringComparison.OrdinalIgnoreCase))
            {
                return map.areaManager.NoRoof;
            }
            return null;
        }

        public static bool Is(Area area)
        {
            return area is Area_BuildRoof || area is Area_NoRoof;
        }

        public static string Id(Area area)
        {
            return area is Area_BuildRoof ? BuildRoofId : area is Area_NoRoof ? NoRoofId : null;
        }

        // What painting cells into (add) or out of a roof area does, decided before anything
        // changes, with the checks of the game's tools: Designator_AreaBuildRoof and
        // Designator_AreaNoRoof to add, and Designator_AreaIgnoreRoof, which clears both areas, to
        // remove. All three skip unexplored cells, and no-roof refuses overhead mountain, whose thick
        // roof nothing can remove. fogged is the map's fog (cell.Fogged(map)).
        public static RoofPaint Plan(Map map, Area area, List<IntVec3> cells, int outside, bool add, Func<IntVec3, bool> fogged)
        {
            bool noRoof = area is Area_NoRoof;
            Area other = noRoof ? (Area)map.areaManager.BuildRoof : map.areaManager.NoRoof;
            RoofPaint paint = new RoofPaint(area, other, add);
            paint.Outcomes.Outside = outside;
            foreach (IntVec3 cell in cells)
            {
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (fogged(cell))
                {
                    paint.Outcomes.Reject(cell, "Unexplored.");
                }
                else if (add && noRoof && roof != null && roof.isThickRoof)
                {
                    paint.Outcomes.Reject(cell, "Overhead mountain: nothing can remove a thick rock roof.");
                }
                else if (area[cell] == add)
                {
                    paint.Outcomes.AlreadySatisfied++;
                }
                else
                {
                    paint.Cells.Add(cell);
                    paint.Outcomes.Applied++;
                    if (add)
                    {
                        paint.TakenFromOther += other[cell] ? 1 : 0;
                        paint.RoofWork += noRoof == (roof != null) ? 1 : 0;
                    }
                }
            }
            return paint;
        }

        public static void Apply(RoofPaint paint)
        {
            foreach (IntVec3 cell in paint.Cells)
            {
                paint.Area[cell] = paint.Add;
                if (paint.Add)
                {
                    paint.Other[cell] = false;
                }
            }
        }
    }

    // A planned change to a roof area. RoofWork counts the painted cells that now have work: roofs
    // to remove under the no-roof area, and cells with no roof yet in the build-roof area, which
    // colonists roof where a wall or column within 6.9 cells can hold it.
    internal sealed class RoofPaint
    {
        public readonly Area Area;
        public readonly Area Other;
        public readonly bool Add;
        public readonly CellOutcomes Outcomes = new CellOutcomes();
        public readonly List<IntVec3> Cells = new List<IntVec3>();
        public int TakenFromOther;
        public int RoofWork;

        public RoofPaint(Area area, Area other, bool add)
        {
            Area = area;
            Other = other;
            Add = add;
        }

        public object ToDto()
        {
            Dictionary<string, object> dto = (Dictionary<string, object>)Outcomes.ToDto();
            if (Add)
            {
                bool noRoof = Area is Area_NoRoof;
                dto[noRoof ? "roofsToRemove" : "roofsToBuild"] = RoofWork;
                dto[noRoof ? "takenFromBuildRoof" : "takenFromNoRoof"] = TakenFromOther;
            }
            return dto;
        }
    }
}
