using System;
using System.Collections.Generic;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // What happened to each cell of a rectangle: counted, and every refusal grouped under its
    // reason with the first few positions, so a rectangle of dozens of cells answers in a few lines.
    // Used for floors, where one call covers a whole room.
    internal sealed class CellSweep
    {
        public const int MaxPositionsPerReason = 10;

        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, List<IntVec3>> cellsByReason = new Dictionary<string, List<IntVec3>>();
        private readonly Dictionary<string, int> countByReason = new Dictionary<string, int>();
        public int Cells;
        public int Applied;
        public int AlreadySatisfied;
        public int Rejected;

        public void Apply()
        {
            Cells++;
            Applied++;
        }

        public void Satisfy()
        {
            Cells++;
            AlreadySatisfied++;
        }

        public void Reject(IntVec3 cell, string reason)
        {
            Cells++;
            Rejected++;
            List<IntVec3> positions;
            if (!cellsByReason.TryGetValue(reason, out positions))
            {
                cellsByReason[reason] = positions = new List<IntVec3>();
                countByReason[reason] = 0;
                order.Add(reason);
            }
            countByReason[reason]++;
            if (positions.Count < MaxPositionsPerReason)
            {
                positions.Add(cell);
            }
        }

        // applied, if any cell took it; alreadySatisfied, if every cell had it; rejected otherwise
        // (also when no cell was on the map).
        public string Outcome
        {
            get { return Applied > 0 ? "applied" : Cells > 0 && Rejected == 0 ? "alreadySatisfied" : "rejected"; }
        }

        // Reasons with the most cells first.
        public object[] Reasons()
        {
            List<string> reasons = new List<string>(order);
            reasons.Sort((a, b) => countByReason[b].CompareTo(countByReason[a]));
            object[] result = new object[reasons.Count];
            for (int i = 0; i < result.Length; i++)
            {
                string reason = reasons[i];
                List<object> positions = new List<object>();
                foreach (IntVec3 cell in cellsByReason[reason])
                {
                    positions.Add(Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z)));
                }
                result[i] = Dto.Obj(
                    Dto.Field("reason", reason),
                    Dto.Field("count", countByReason[reason]),
                    Dto.Field("positions", positions.ToArray()));
            }
            return result;
        }

        // The cells of the rectangle between two corners, given in either order, as a rectangle
        // whose sides are at most CommandCells.MaxSide. field prefixes the error messages.
        public static CellRect Between(int x1, int z1, int x2, int z2, string field)
        {
            int minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
            int minZ = Math.Min(z1, z2), maxZ = Math.Max(z1, z2);
            if (maxX - minX + 1 > CommandCells.MaxSide || maxZ - minZ + 1 > CommandCells.MaxSide)
            {
                throw new CommandException(400, field + " is " + (maxX - minX + 1) + " by " + (maxZ - minZ + 1) + " cells; each side may be at most " + CommandCells.MaxSide + ". Split it into several rectangles.");
            }
            return CellRect.FromLimits(minX, minZ, maxX, maxZ);
        }
    }
}
