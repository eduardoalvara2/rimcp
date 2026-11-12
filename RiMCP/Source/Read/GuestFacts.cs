using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    // What the game says about a pawn who is not a colonist: the prisoner tab's resistance, will and
    // interaction mode, whether the pawn can be recruited, and, for a downed visitor, whether
    // offering help makes them join. Everything here is read from the game's own fields and rules;
    // the pure parts take plain values so they can be checked without a map.
    internal static class GuestFacts
    {
        // Resistance is reduced by the warden's interactions and recruiting succeeds once it is 0.
        // The prisoner tab shows a value above 0 as at least 0.1, so a pawn one sliver from 0 does
        // not read as 0; a value of 0 or less reads as 0.
        public static float ShownResistance(float resistance)
        {
            return resistance > 0f ? Math.Max(0.1f, resistance) : 0f;
        }

        public static string Status(bool prisoner, bool slave, bool hosted)
        {
            return prisoner ? "prisoner" : slave ? "slave" : hosted ? "guest" : "none";
        }

        // resistance: recruiting is a count down and the attempt that follows it always works.
        // chance: animals and wild men are tamed or recruited by a roll, with no resistance.
        public static string RecruitMethod(bool animalOrWildMan)
        {
            return animalOrWildMan ? "chance" : "resistance";
        }

        // Where the recruitment stands, by the game's InteractionWorker_RecruitAttempt: while
        // resistance is above 0 each warden conversation lowers it, at 0 the next recruit attempt
        // succeeds.
        public static string RecruitState(bool recruitable, bool byResistance, float resistance)
        {
            if (!recruitable)
            {
                return "notRecruitable";
            }
            if (!byResistance)
            {
                return "byChance";
            }
            return resistance > 0f ? "resistanceRemaining" : "readyToRecruit";
        }

        // Which pawns carry a guest object: the colony's prisoners and slaves, anyone another
        // faction holds, and any other humanlike who is downed or would join if helped (a downed
        // raider is who a colonist may capture).
        public static bool Applies(bool humanlike, bool colonist, bool prisoner, bool slave, bool hosted, bool downed, bool willJoin)
        {
            if (!humanlike || colonist)
            {
                return false;
            }
            return prisoner || slave || hosted || downed || willJoin;
        }

        public static double Round(float value)
        {
            return Math.Round(value, 2);
        }

        public static object Of(Pawn pawn, string role)
        {
            if (pawn == null || pawn.guest == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.Dead)
            {
                return null;
            }
            bool willJoin = pawn.mindState != null && pawn.mindState.WillJoinColonyIfRescued;
            bool hosted = pawn.HostFaction != null;
            if (!Applies(true, role == "colonist", pawn.IsPrisoner, pawn.IsSlave, hosted, pawn.Downed, willJoin))
            {
                return null;
            }
            Pawn_GuestTracker guest = pawn.guest;
            bool recruitable = guest.Recruitable;
            bool byResistance = !pawn.AnimalOrWildMan();
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("status", Status(pawn.IsPrisoner, pawn.IsSlave, hosted)),
                Dto.Field("isPrisoner", pawn.IsPrisoner),
                Dto.Field("isSlave", pawn.IsSlave),
                Dto.Field("hostFaction", SummaryDto.FactionIdentity(pawn.HostFaction)),
                Dto.Field("downed", pawn.Downed),
                Dto.Field("willJoinIfRescued", willJoin),
                Dto.Field("canBeCaptured", pawn.CanBeCaptured()),
                Dto.Field("recruitable", recruitable));
            if (pawn.IsPrisoner || pawn.IsSlave)
            {
                dto["recruitMethod"] = RecruitMethod(!byResistance);
                dto["recruitState"] = RecruitState(recruitable, byResistance, guest.resistance);
                if (recruitable && byResistance)
                {
                    dto["resistance"] = Round(ShownResistance(guest.resistance));
                }
                if (ModsConfig.IdeologyActive)
                {
                    dto["will"] = Round(guest.will);
                }
                dto["interactionMode"] = ReadUtil.Def(guest.ExclusiveInteractionMode);
            }
            return dto;
        }
    }
}
