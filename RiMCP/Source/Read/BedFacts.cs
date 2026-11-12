using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // A bed as a client counts free ones: who it is for, who owns it, who lies in it now.
    internal static class BedFacts
    {
        // A bed with an owner slot open can still be assigned; a medical bed has no owners (patients
        // lie in it), so its free slots are the ones with no one in them.
        public static int FreeSlots(bool medical, int slots, int owners, int occupants)
        {
            return Math.Max(0, slots - (medical ? occupants : owners));
        }

        public static object Of(Building_Bed bed)
        {
            List<Pawn> owners = bed.CompAssignableToPawn == null ? new List<Pawn>() : bed.OwnersForReading.ToList();
            List<Pawn> occupants = bed.CurOccupants.ToList();
            int slots = bed.SleepingSlotsCount;
            return Dto.Obj(
                Dto.Field("ownerType", bed.ForOwnerType.ToString().ToLowerInvariant()),
                Dto.Field("forPrisoners", bed.ForPrisoners),
                Dto.Field("forSlaves", bed.ForSlaves),
                Dto.Field("medical", bed.Medical),
                Dto.Field("slots", slots),
                Dto.Field("owners", owners.Select(pawn => (object)SummaryDto.Pawn(pawn)).ToArray()),
                Dto.Field("occupants", occupants.Select(pawn => (object)SummaryDto.Pawn(pawn)).ToArray()),
                Dto.Field("freeSlots", FreeSlots(bed.Medical, slots, owners.Count, occupants.Count)));
        }
    }
}
