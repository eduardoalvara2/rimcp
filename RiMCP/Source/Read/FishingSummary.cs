using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // A fishing zone (Odyssey) as its inspect pane shows it: the water it covers, the fish
    // population of that water body (the zone's first cell decides it, as in game), and why
    // colonists are or are not fishing there now.
    internal static class FishingSummary
    {
        // The game's own answers, which it keeps private; each falls back to its vanilla rule.
        private static readonly Func<Zone_Fishing, bool> OverTargetPopulation = Getter("OverTargetPopulation")
            ?? (zone => zone.Map.TileInfo.MaxFishPopulation > 0f && zone.Cells.Count > 0 && zone.FishType != WaterBodyType.None
                && zone.Map.waterBodyTracker.PopulationPercentAt(zone.Cells[0]) > zone.targetPopulationPct);
        private static readonly Func<Zone_Fishing, bool> UnderTargetResourceCount = Getter("UnderTargetResourceCount")
            ?? (zone => zone.repeatMode == FishRepeatMode.DoForever
                || (zone.repeatMode == FishRepeatMode.TargetCount ? zone.OwnedFishCount < zone.targetCount : zone.repeatCount > 0));

        public static Dictionary<string, object> Of(Zone_Fishing zone)
        {
            Map map = zone.Map;
            if (map.waterBodyTracker == null)
            {
                // A fishing zone from an Odyssey save, loaded without the DLC: the fish are not tracked.
                return Dto.Obj(
                    Dto.Field("waterType", zone.FishType.ToString()),
                    Dto.Field("blockers", Blockers.ToArray(new List<object> { Blockers.Of("noOdyssey", "Odyssey is not active, so this map tracks no fish and nobody fishes here.") })));
            }
            IntVec3 first = zone.Cells.Count == 0 ? IntVec3.Invalid : zone.Cells[0];
            int fishable = 0;
            foreach (IntVec3 cell in zone.Cells)
            {
                fishable += zone.IsFishable(cell) ? 1 : 0;
            }
            float population = first.IsValid ? map.waterBodyTracker.FishPopulationAt(first) : 0f;
            float max = first.IsValid ? map.waterBodyTracker.MaxPopulationAt(first) : 0f;

            List<object> blockers = new List<object>();
            if (!OverTargetPopulation(zone))
            {
                blockers.Add(Blockers.Of("populationBelowTarget", "The fish population is at or under the zone's target of " + (zone.targetPopulationPct * 100f).ToString("F0") + "% of its maximum, so colonists will not fish here."));
            }
            if (!zone.Allowed)
            {
                blockers.Add(Blockers.Of("notAllowed", "Fishing is switched off for this zone."));
            }
            if (!UnderTargetResourceCount(zone))
            {
                blockers.Add(Blockers.Of("targetReached", "The zone's repeat setting is satisfied: the colony already has " + zone.OwnedFishCount + " fish."));
            }
            else if (zone.PausedDueToResourceCount)
            {
                blockers.Add(Blockers.Of("paused", "Fishing is paused until the colony's fish fall to " + zone.unpauseAtCount + "."));
            }
            if (fishable == 0)
            {
                blockers.Add(zone.AllFishableCellsFrozen
                    ? Blockers.Of("frozen", "Every cell of the zone is frozen over.")
                    : Blockers.Of("noFishableCells", "No cell of the zone is open water now."));
            }

            return Dto.Obj(
                Dto.Field("waterType", zone.FishType.ToString()),
                Dto.Field("fishableCells", fishable),
                Dto.Field("fishPopulation", Math.Round(population, 1)),
                Dto.Field("maxFishPopulation", Math.Round(max, 1)),
                Dto.Field("populationPercent", max <= 0f ? null : (object)Math.Round(population / max, 3)),
                Dto.Field("populationLabel", first.IsValid ? ((string)FishingUtility.FishPopulationLabel(population)).StripTags() : null),
                Dto.Field("targetPopulationPercent", Math.Round(zone.targetPopulationPct, 3)),
                Dto.Field("allowed", zone.Allowed),
                Dto.Field("repeatMode", zone.repeatMode.ToString()),
                Dto.Field("targetCount", zone.repeatMode == FishRepeatMode.TargetCount ? (object)zone.targetCount : null),
                Dto.Field("repeatCount", zone.repeatMode == FishRepeatMode.RepeatCount ? (object)zone.repeatCount : null),
                Dto.Field("pauseWhenSatisfied", zone.repeatMode == FishRepeatMode.TargetCount ? (object)zone.pauseWhenSatisfied : null),
                Dto.Field("unpauseAtCount", zone.repeatMode == FishRepeatMode.TargetCount && zone.pauseWhenSatisfied ? (object)zone.unpauseAtCount : null),
                Dto.Field("ownedFish", zone.OwnedFishCount),
                Dto.Field("shouldFishNow", zone.ShouldFishNow && fishable > 0),
                Dto.Field("blockers", Blockers.ToArray(blockers)));
        }

        // Why a fishing zone refuses a cell of the other kind of water; null when they match. The
        // game's tool makes one zone for each kind, so a zone here holds one.
        public static string WaterMismatch(WaterBodyType zoneType, WaterBodyType cellType)
        {
            return zoneType == cellType
                ? null
                : "A fishing zone covers one kind of water: this zone is " + Name(zoneType) + " and this cell is " + Name(cellType) + ". Make a separate zone for it.";
        }

        // Why a fishing zone cannot be created or edited, or null. The game offers the tool only
        // with Odyssey and, outside god mode, once Fishing is researched. The research is null
        // without Odyssey, so it is only asked for after that check.
        public static string Unavailable(bool odysseyActive, Func<bool> researched, bool godMode)
        {
            if (!odysseyActive)
            {
                return "Fishing zones need the Odyssey DLC, which is not active.";
            }
            return godMode || researched() ? null : "Fishing zones need the Fishing research, which is not finished.";
        }

        private static string Name(WaterBodyType type)
        {
            return type == WaterBodyType.Freshwater ? "freshwater" : type == WaterBodyType.Saltwater ? "saltwater" : type.ToString().ToLowerInvariant();
        }

        private static Func<Zone_Fishing, bool> Getter(string name)
        {
            PropertyInfo property = typeof(Zone_Fishing).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getter = property == null || property.PropertyType != typeof(bool) ? null : property.GetGetMethod(true);
            return getter == null ? null : (Func<Zone_Fishing, bool>)Delegate.CreateDelegate(typeof(Func<Zone_Fishing, bool>), getter);
        }
    }
}
