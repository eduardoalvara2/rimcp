using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RiMCP.Read
{
    // The terrain under a zone's cells, as the top layer the game reads (a floor over the ground).
    // get_map_view draws a zone cell as a zone mark and hides the terrain, so water under a
    // stockpile is only visible here.
    internal static class ZoneTerrain
    {
        public struct TerrainCell
        {
            public string DefName;
            public bool IsWater;
            public float ExtraDeteriorationFactor;
        }

        public static TerrainCell Read(Map map, IntVec3 cell)
        {
            TerrainDef terrain = cell.GetTerrain(map);
            return new TerrainCell
            {
                DefName = terrain == null ? "None" : terrain.defName,
                IsWater = terrain != null && terrain.IsWater,
                ExtraDeteriorationFactor = terrain == null ? 0f : terrain.extraDeteriorationFactor
            };
        }

        // terrainCounts (defName -> cells), terrainDeterioration (the terrains with an
        // extraDeteriorationFactor above 0, most cells first, as rows of defName, cells and the
        // factor: things lying there deteriorate that much faster), and waterCells.
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
            return Dto.Obj(
                Dto.Field("terrainCounts", counts),
                Dto.Field("terrainDeterioration", groups
                    .Where(group => group.First().ExtraDeteriorationFactor > 0f)
                    .Select(group => (object)Dto.Obj(
                        Dto.Field("defName", group.Key),
                        Dto.Field("cells", group.Count()),
                        Dto.Field("extraDeteriorationFactor", Math.Round(group.First().ExtraDeteriorationFactor, 2))))
                    .ToArray()),
                Dto.Field("waterCells", groups.Where(group => group.First().IsWater).Sum(group => group.Count())));
        }
    }
}
