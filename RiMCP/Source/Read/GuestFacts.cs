using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    // What the game says about a pawn who is not a colonist: the prisoner tab's resistance, will and
    // interaction mode, whether the pawn can be recruited, for a slave the slave tab's suppression,
    // and, for a downed visitor, whether helping or rescuing them makes them join. Everything here
    // is read from the game's own fields and rules; the pure parts take plain values so they can be
    // checked without a map.
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
        // resistance is above 0 each warden conversation (in Reduce resistance mode) lowers it, at 0
        // the next recruit attempt (Attempt to recruit mode) succeeds.
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

        // What the Rescue path does, by Pawn_GuestTracker.Notify_PawnUndowned: the pawn joins only
        // when no faction owns them or their faction has rescueesCanJoin (in Core only the hidden
        // Ancients), they are no prisoner and have not already left after a rescue; then the game
        // rolls a die seeded by the pawn, 50%, or 100% when the weather is unsafe for them (outdoor
        // temperature outside their safe range, toxic fallout or noxious haze). The roll itself is
        // not read: certain says the weather makes it 100% as it is now (the roll happens when the
        // pawn recovers, so it can change), chance that it is 50%. A baby never joins this way: the
        // rescue job does not make a baby a guest, and a baby never stops being downed.
        public static string JoinAfterRescue(bool humanlike, bool baby, bool prisoner, bool leftAfterRescue, bool factionAllows, bool unsafeWeather)
        {
            if (!humanlike || baby || prisoner || leftAfterRescue || !factionAllows)
            {
                return "never";
            }
            return unsafeWeather ? "certain" : "chance";
        }

        // The option the float menu gives a pawn who would join if helped: Offer help, or Free
        // prisoner for one another faction holds. Rescue is not offered then.
        public static string JoinOption(bool willJoin, bool prisoner)
        {
            return !willJoin ? null : prisoner ? "freePrisoner" : "offerHelp";
        }

        // FloatMenuOptionProvider_CapturePawn without the rescuer: a downed, non-colony pawn the
        // game lets one capture. Whether a colonist can reach them is for list_pawn_actions.
        public static bool CaptureOffered(bool downed, bool prisonerOfColony, bool canBeCaptured, bool wantsToBeRescued)
        {
            return downed && !prisonerOfColony && canBeCaptured && wantsToBeRescued;
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
            bool canBeCaptured = pawn.CanBeCaptured();
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("status", Status(pawn.IsPrisoner, pawn.IsSlave, hosted)),
                Dto.Field("isPrisoner", pawn.IsPrisoner),
                Dto.Field("isSlave", pawn.IsSlave),
                Dto.Field("hostFaction", SummaryDto.FactionIdentity(pawn.HostFaction)),
                Dto.Field("downed", pawn.Downed),
                Dto.Field("willJoinIfRescued", willJoin),
                Dto.Field("joinOption", JoinOption(willJoin, pawn.IsPrisoner)),
                Dto.Field("joinAfterRescue", JoinAfterRescue(true, pawn.DevelopmentalStage == DevelopmentalStage.Baby, pawn.IsPrisoner, guest.leftAfterRescue, FactionAllowsJoin(pawn), UnsafeWeather(pawn))),
                Dto.Field("canBeCaptured", canBeCaptured),
                Dto.Field("captureOffered", pawn.Spawned && CaptureOffered(pawn.Downed, pawn.IsPrisonerOfColony, canBeCaptured, HealthAIUtility.WantsToBeRescued(pawn))),
                Dto.Field("recruitable", recruitable));
            if (pawn.IsPrisonerOfColony)
            {
                dto["recruitMethod"] = RecruitMethod(!byResistance);
                dto["recruitState"] = RecruitState(recruitable, byResistance, guest.resistance);
                dto["recruitAttemptsOn"] = guest.IsInteractionEnabled(PrisonerInteractionModeDefOf.AttemptRecruit);
                if (recruitable && byResistance)
                {
                    dto["resistance"] = Round(ShownResistance(guest.resistance));
                }
                if (ModsConfig.IdeologyActive && byResistance)
                {
                    dto["will"] = Round(guest.will);
                }
                dto["interactionMode"] = ReadUtil.Def(guest.ExclusiveInteractionMode);
            }
            else if (pawn.IsSlaveOfColony)
            {
                dto["slaveInteractionMode"] = ReadUtil.Def(guest.slaveInteractionMode);
                Need_Suppression suppression = pawn.needs == null ? null : pawn.needs.TryGetNeed<Need_Suppression>();
                if (suppression != null)
                {
                    dto["suppression"] = Round(suppression.CurLevel);
                    dto["suppressionFallRate"] = Round(pawn.GetStatValue(StatDefOf.SlaveSuppressionFallRate));
                }
            }
            return dto;
        }

        private static bool FactionAllowsJoin(Pawn pawn)
        {
            return pawn.Faction == null || pawn.Faction.def.rescueesCanJoin;
        }

        private static bool UnsafeWeather(Pawn pawn)
        {
            Map map = pawn.MapHeld;
            if (map == null)
            {
                return false;
            }
            return !(pawn.SafeTemperatureRange().Includes(map.mapTemperature.OutdoorTemp)
                && !map.gameConditionManager.ConditionIsActive(GameConditionDefOf.ToxicFallout)
                && (!ModsConfig.BiotechActive || !map.gameConditionManager.ConditionIsActive(GameConditionDefOf.NoxiousHaze)));
        }
    }
}
