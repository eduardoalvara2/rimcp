using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // A bed as a client counts free ones: who it is for, who owns it, who lies in it now, and how
    // many more unrelated adult humanlikes of its kind the game would let use it.
    internal static class BedFacts
    {
        // The body size of an adult human; a bed with a smaller bed_maxBodySize (a crib) is for babies.
        public const float AdultBodySize = 1f;

        // The slots the game would give a newcomer adult of the bed's own kind (RestUtility.CanUseBedEver
        // and CanUseBedNow). ownerType is colonist, slave, prisoner or animal.
        //  - a bed for babies or animals, a deathrest casket (only a pawn who can deathrest uses
        //    it) and a prisoner bed outside a prison cell give an adult newcomer none, the first two
        //    by the bed's def and the last because the game refuses a prisoner bed outside a cell;
        //  - a medical bed has no owners: its free slots are those without a patient;
        //  - an unowned bed takes a colonist in one slot (the other slot is then open only to a
        //    lover of the owner, which a newcomer is not), and a slave or prisoner in any;
        //  - a partly owned bed takes no colonist (the game asks for a lover of an owner), and a
        //    slave or prisoner in a slot with no owner.
        public static int FreeSlots(bool humanlike, float maxBodySize, bool deathrest, string ownerType, bool inPrisonCell, bool medical, int slots, int owners, int occupants)
        {
            int unoccupied = Math.Max(0, slots - occupants);
            if (!humanlike)
            {
                return unoccupied;
            }
            if (maxBodySize < AdultBodySize || deathrest || (ownerType == "prisoner" && !inPrisonCell))
            {
                return 0;
            }
            if (medical)
            {
                return unoccupied;
            }
            bool confined = ownerType == "prisoner" || ownerType == "slave";
            if (owners == 0)
            {
                return confined ? unoccupied : Math.Min(1, unoccupied);
            }
            return confined ? Math.Max(0, Math.Min(slots - owners, unoccupied)) : 0;
        }

        public static object Of(Building_Bed bed)
        {
            List<Pawn> owners = bed.CompAssignableToPawn == null ? new List<Pawn>() : bed.OwnersForReading.ToList();
            List<Pawn> occupants = bed.CurOccupants.ToList();
            int slots = bed.SleepingSlotsCount;
            bool humanlike = bed.def.building.bed_humanlike;
            float maxBodySize = bed.def.building.bed_maxBodySize;
            bool deathrest = ModsConfig.BiotechActive && ThingDefOf.DeathrestCasket != null && bed.def == ThingDefOf.DeathrestCasket;
            bool inPrisonCell = bed.Spawned && bed.Position.IsInPrisonCell(bed.Map);
            string ownerType = humanlike ? bed.ForOwnerType.ToString().ToLowerInvariant() : "animal";
            return Dto.Obj(
                Dto.Field("ownerType", ownerType),
                Dto.Field("humanlike", humanlike),
                Dto.Field("forBabies", bed.ForHumanBabies),
                Dto.Field("forPrisoners", humanlike && bed.ForPrisoners),
                Dto.Field("forSlaves", humanlike && bed.ForSlaves),
                Dto.Field("inPrisonCell", inPrisonCell),
                Dto.Field("deathrest", deathrest),
                Dto.Field("medical", bed.Medical),
                Dto.Field("slots", slots),
                Dto.Field("owners", owners.Select(pawn => (object)SummaryDto.Pawn(pawn)).ToArray()),
                Dto.Field("occupants", occupants.Select(pawn => (object)SummaryDto.Pawn(pawn)).ToArray()),
                Dto.Field("freeSlots", FreeSlots(humanlike, maxBodySize, deathrest, ownerType, inPrisonCell, bed.Medical, slots, owners.Count, occupants.Count)));
        }
    }
}
