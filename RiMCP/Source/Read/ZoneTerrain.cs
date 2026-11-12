using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RiMCP.Read
{
    // The terrain under a zone's cells, as the game reads it (TerrainAt): temporary terrain first
    // (ice, Odyssey floodwater), then a bridge, floor or substructure over the ground. A bridge
    // over water reads as Bridge, with factor 0. get_map_view draws a zone cell as a zone mark and hides the terrain, so water under a
    // stockpile is only visible here.
    internal static class ZoneTerrain
    {
        public struct TerrainCell
        {
            public string DefName;
            public bool IsWater;
            public float ExtraDeteriorationFactor;
            // The terrain is TerrainDef.temporary (ice, floodwater) ...
            public bool Temporary;
            // ... and the terrain under it (TerrainAtIgnoreTemp) is water.
            public bool WaterUnderTemporary;
        }

        public static TerrainCell Read(Map map, IntVec3 cell)
        {
            int index = map.cellIndices.CellToIndex(cell);
            TerrainDef terrain = map.terrainGrid.TerrainAt(index);
            TerrainDef beneath = map.terrainGrid.TerrainAtIgnoreTemp(index);
            return new TerrainCell
            {
                DefName = terrain.defName,
                IsWater = terrain.IsWater,
                ExtraDeteriorationFactor = terrain.extraDeteriorationFactor,
                Temporary = terrain.temporary,
                WaterUnderTemporary = terrain.temporary && beneath != null && beneath.IsWater
            };
        }

        // terrainCounts (defName -> cells), terrainDeterioration (the terrains with an
        // extraDeteriorationFactor other than 0, as the game tests it, most cells first, as rows of
        // defName, cells and the factor: things lying there deteriorate that much faster),
        // waterCells, temporaryCells (defName -> cells for temporary terrains) and
        // waterUnderTemporaryCells (water frozen over or flooded now, which a thaw or the flood
        // passing gives back).
        public static Dictionary<string, object> Summarize(IEnumerable<TerrainCell> cells)
        {
            List<IGrouping<string, TerrainCell>> groups = cells
                .GroupBy(cell => cell.DefName)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            Dictionary<string, object> counts = new Dictionary<string, object>();
            foreach (IGrouping<string, TerrainCell> group in groups)
            {
                counts[group.Key] = group.Count();
            }
            Dictionary<string, object> temporary = new Dictionary<string, object>();
            foreach (IGrouping<string, TerrainCell> group in groups.Where(group => group.First().Temporary))
            {
                temporary[group.Key] = group.Count();
            }
            return Dto.Obj(
                Dto.Field("terrainCounts", counts),
                Dto.Field("terrainDeterioration", groups
                    .Where(group => group.First().ExtraDeteriorationFactor != 0f)
                    .Select(group => (object)Dto.Obj(
                        Dto.Field("defName", group.Key),
                        Dto.Field("cells", group.Count()),
                        Dto.Field("extraDeteriorationFactor", Math.Round(group.First().ExtraDeteriorationFactor, 2))))
                    .ToArray()),
                Dto.Field("waterCells", groups.Where(group => group.First().IsWater).Sum(group => group.Count())),
                Dto.Field("temporaryCells", temporary),
                Dto.Field("waterUnderTemporaryCells", groups.SelectMany(group => group).Count(cell => cell.WaterUnderTemporary)));
        }
    }
}
