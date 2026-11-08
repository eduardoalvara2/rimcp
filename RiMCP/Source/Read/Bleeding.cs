using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // A pawn's bleeding as the Health tab shows it: the total bleed rate, a share of the pawn's
    // blood per day, and the time until blood loss kills, from the game's own
    // HealthUtility.TicksUntilDeathDueToBloodLoss. The tab shows the line only above 1% a day; at a
    // day or more left it says Won't bleed out soon, and for a Deathless pawn (Biotech) it shows no
    // time, as blood loss cannot kill them.
    internal sealed class Bleeding
    {
        public const float ShownRate = 0.01f;

        public float RatePerDay;
        // Null when blood loss cannot kill the pawn.
        public float? HoursUntilDeath;

        // Null when the pawn is not bleeding, as the Health tab has it.
        public static Bleeding Of(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null || pawn.Dead)
            {
                return null;
            }
            float rate = pawn.health.hediffSet.BleedRateTotal;
            if (!(rate > ShownRate))
            {
                return null;
            }
            bool deathless = ModsConfig.BiotechActive && pawn.genes != null && GeneDefOf.Deathless != null && pawn.genes.HasActiveGene(GeneDefOf.Deathless);
            return From(rate, deathless ? (int?)null : HealthUtility.TicksUntilDeathDueToBloodLoss(pawn));
        }

        public static Bleeding From(float rate, int? ticksUntilDeath)
        {
            if (!(rate > ShownRate))
            {
                return null;
            }
            return new Bleeding
            {
                RatePerDay = rate,
                HoursUntilDeath = ticksUntilDeath == null || ticksUntilDeath.Value == int.MaxValue ? (float?)null : (float)ticksUntilDeath.Value / GenDate.TicksPerHour
            };
        }

        // Most urgent first: the soonest death, then the fastest bleeding; those blood loss cannot
        // kill come last.
        public static List<T> MostUrgentFirst<T>(IEnumerable<T> items, Func<T, Bleeding> bleeding)
        {
            return items
                .OrderBy(item => bleeding(item).HoursUntilDeath ?? float.MaxValue)
                .ThenByDescending(item => bleeding(item).RatePerDay)
                .ToList();
        }

        public object RateDto
        {
            get { return Math.Round((double)RatePerDay, 3); }
        }

        public object HoursDto
        {
            get { return HoursUntilDeath == null ? null : (object)Math.Round((double)HoursUntilDeath.Value, 2); }
        }

        public string Describe()
        {
            string rate = "bleeding " + Math.Round(RatePerDay * 100).ToString(CultureInfo.InvariantCulture) + "% a day";
            return HoursUntilDeath == null
                ? rate + ", which cannot kill them"
                : HoursUntilDeath.Value.ToString("0.0", CultureInfo.InvariantCulture) + " in-game hours until death by blood loss, " + rate;
        }
    }
}
