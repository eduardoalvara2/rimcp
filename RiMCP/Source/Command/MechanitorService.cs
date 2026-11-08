using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // A mechanitor's control groups as the Mechs tab and the control group buttons show them, which
    // are not commands, so list_gizmos leaves them out. Groups are numbered from 1, as the game
    // labels them.
    internal static class MechanitorService
    {
        public static BridgeResponse GetMechanitor(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Pawn mechanitor = RequireMechanitor(PawnReadService.FindPawn(context.Map, route["pawnId"]));
            return ReadEnvelope.Ok(context, Describe(mechanitor));
        }

        public static BridgeResponse SetMechControl(BridgeRequest request, RouteMatch route)
        {
            SetMechControlBody body = CommandUtil.ReadBody<SetMechControlBody>(request);
            int index;
            if (!int.TryParse(route["controlGroup"], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 1)
            {
                throw new CommandException(400, "controlGroup must be a group number from 1, as get_mechanitor lists them.");
            }
            string[] assign = (body.Assign ?? new string[0]).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToArray();
            if (string.IsNullOrWhiteSpace(body.WorkMode) && assign.Length == 0)
            {
                throw new CommandException(400, "Give workMode, assign, or both.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            Pawn mechanitor = RequireMechanitor(CommandUtil.ResolveSpawnedPawn(context, route["pawnId"]));
            string name = ReadUtil.PawnName(mechanitor);
            List<MechanitorControlGroup> groups = mechanitor.mechanitor.controlGroups;
            if (index > groups.Count)
            {
                throw new CommandException(404, name + " has " + groups.Count + " control group" + (groups.Count == 1 ? "" : "s") + "; there is no group " + index + ".");
            }
            MechanitorControlGroup group = groups[index - 1];
            MechWorkModeDef workMode = null;
            if (!string.IsNullOrWhiteSpace(body.WorkMode))
            {
                // The control group button's menu offers every work mode, none of them needing a
                // target.
                workMode = WorkModes().FirstOrDefault(mode => string.Equals(mode.defName, body.WorkMode.Trim(), System.StringComparison.OrdinalIgnoreCase));
                if (workMode == null)
                {
                    throw new CommandException(400, "Unknown workMode '" + body.WorkMode + "'. Use one of: " + string.Join(", ", WorkModes().Select(mode => mode.defName).ToArray()) + ".");
                }
            }
            // As the Mechs tab: any mech this mechanitor oversees moves to another group.
            List<Pawn> mechs = new List<Pawn>();
            foreach (string id in assign)
            {
                Pawn mech = PawnReadService.FindPawn(context.Map, id);
                if (mech == null)
                {
                    throw new CommandException(404, "No mech '" + id + "' on this map.");
                }
                if (!mech.IsColonyMech || mech.GetOverseer() != mechanitor)
                {
                    throw new CommandException(409, ReadUtil.PawnName(mech) + " is not a mech " + name + " oversees.");
                }
                mechs.Add(mech);
            }
            bool modeChanged = workMode != null && group.WorkMode != workMode;
            if (modeChanged)
            {
                AcceptanceReport control = mechanitor.mechanitor.CanControlMechs;
                if (!control.Accepted)
                {
                    throw new CommandException(409, string.IsNullOrEmpty(control.Reason) ? name + " cannot control mechs right now." : control.Reason.StripTags());
                }
                // The button of a group without mechs has no work mode menu.
                if (group.MechsForReading.Count == 0 && mechs.Count == 0)
                {
                    throw new CommandException(409, "Control group " + index + " has no mechs; assign some to it, in the same call or before.");
                }
            }

            // Everything is checked before anything changes: assignments first, then the work mode,
            // which the group's mechs take.
            List<string> assigned = new List<string>();
            foreach (Pawn mech in mechs.Where(each => mechanitor.mechanitor.GetControlGroup(each) != group))
            {
                group.Assign(mech);
                assigned.Add(ReadUtil.ThingId(mech));
            }
            if (modeChanged)
            {
                group.SetWorkMode(workMode);
            }
            bool changed = modeChanged || assigned.Count > 0;
            Dictionary<string, object> data = Describe(mechanitor);
            data["applied"] = Dto.Obj(
                Dto.Field("controlGroup", index),
                Dto.Field("workMode", modeChanged ? workMode.defName : null),
                Dto.Field("assigned", assigned.ToArray()));
            if (!changed)
            {
                data["alreadySatisfied"] = true;
            }
            return CommandEnvelope.Ok(context, changed, data);
        }

        private static Pawn RequireMechanitor(Pawn pawn)
        {
            if (pawn == null)
            {
                throw new CommandException(404, "Pawn not found.");
            }
            if (pawn.mechanitor == null || pawn.Faction != Faction.OfPlayer)
            {
                throw new CommandException(409, ReadUtil.PawnName(pawn) + " is not one of the colony's mechanitors" + (ModsConfig.BiotechActive ? "." : " (mechanitors need Biotech)."));
            }
            return pawn;
        }

        // GetWorkModeOptions offers these, in this order.
        private static IEnumerable<MechWorkModeDef> WorkModes()
        {
            return DefDatabase<MechWorkModeDef>.AllDefsListForReading.OrderBy(mode => mode.uiOrder);
        }

        private static Dictionary<string, object> Describe(Pawn mechanitor)
        {
            Pawn_MechanitorTracker tracker = mechanitor.mechanitor;
            AcceptanceReport control = tracker.CanControlMechs;
            List<Pawn> controlled = tracker.ControlledPawns;
            return Dto.Obj(
                Dto.Field("mechanitor", SummaryDto.Pawn(mechanitor)),
                Dto.Field("canControlMechs", Dto.Obj(
                    Dto.Field("accepted", control.Accepted),
                    Dto.Field("reason", string.IsNullOrEmpty(control.Reason) ? null : control.Reason.StripTags()))),
                Dto.Field("bandwidth", Dto.Obj(
                    Dto.Field("total", tracker.TotalBandwidth),
                    Dto.Field("used", tracker.UsedBandwidth),
                    Dto.Field("fromSubjects", tracker.UsedBandwidthFromSubjects),
                    Dto.Field("fromGestation", tracker.UsedBandwidthFromGestation))),
                Dto.Field("controlGroups", tracker.controlGroups.Select(group => Dto.Obj(
                    Dto.Field("index", group.Index),
                    Dto.Field("label", group.LabelIndexWithWorkMode),
                    Dto.Field("workMode", group.WorkMode == null ? null : group.WorkMode.defName),
                    Dto.Field("mechs", group.MechsForReading.Select(mech => Mech(mech, controlled)).ToArray()))).ToArray()),
                // Mechs beyond the mechanitor's bandwidth, which go out of control.
                Dto.Field("uncontrolledMechs", tracker.OverseenPawns.Where(mech => !mech.IsGestating() && !controlled.Contains(mech)).Select(mech => Mech(mech, controlled)).ToArray()),
                Dto.Field("workModes", WorkModes().Select(mode => Dto.Obj(
                    Dto.Field("defName", mode.defName),
                    Dto.Field("label", ReadUtil.DefLabel(mode)),
                    Dto.Field("description", mode.description))).ToArray()));
        }

        private static object Mech(Pawn mech, List<Pawn> controlled)
        {
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(mech)),
                Dto.Field("label", mech.LabelShortCap.StripTags()),
                Dto.Field("defName", mech.def.defName),
                Dto.Field("drafted", mech.Drafted),
                Dto.Field("controlled", controlled.Contains(mech)),
                Dto.Field("energy", mech.needs == null || mech.needs.energy == null ? null : (object)System.Math.Round(mech.needs.energy.CurLevelPercentage, 2)));
        }

        [DataContract]
        private sealed class SetMechControlBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "workMode")]
            public string WorkMode { get; set; }

            [DataMember(Name = "assign")]
            public string[] Assign { get; set; }
        }
    }
}
