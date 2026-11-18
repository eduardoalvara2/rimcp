using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // prepare_gravship_launch: presses the pilot console's "Launch..." button as a click does, which
    // opens the game's own "Gravship launch" window, then sets who flies and what boards through the
    // window's own role assignments, with the game's own rules. It never begins the launch: no code
    // here calls the window's Start (its Begin button and Enter key), the ritual's action callback,
    // or the ritual, and the player presses Begin. See GravshipLaunchWindow.
    internal static class GravshipLaunchService
    {
        public static BridgeResponse Prepare(BridgeRequest request, RouteMatch route)
        {
            PrepareBody body = CommandUtil.ReadBody<PrepareBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (string.IsNullOrWhiteSpace(body.PilotId))
            {
                throw new CommandException(400, "Missing required field 'pilotId'.");
            }
            // Left out, the crew stays what the game's own auto-fill chose; an empty list means nobody else.
            List<string> crewIds = body.CrewIds == null ? null : body.CrewIds.Select(id => id == null ? "" : id.Trim()).ToList();
            if (crewIds != null && crewIds.Any(id => id.Length == 0))
            {
                throw new CommandException(400, "Field 'crewIds' must hold pawn ids only.");
            }
            string pilotId = body.PilotId.Trim();
            string copilotId = string.IsNullOrWhiteSpace(body.CopilotId) ? null : body.CopilotId.Trim();
            RequireEach(pilotId, copilotId, crewIds);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            RequestKey key = RequestKey.For("prepare_gravship_launch", requestId, context.Map, route["thingId"], pilotId, copilotId, crewIds == null ? "-" : string.Join(",", crewIds.ToArray()),
                body.ForceVisitorsToLeave, body.BoardColonyAnimals, body.BoardColonyMechanoids);
            return CommandReceipts.Once(context, key, () => Run(context.Map, route["thingId"], pilotId, copilotId, crewIds, body));
        }

        // A pawn takes one seat: it cannot be the pilot and the copilot, or twice in the crew.
        private static void RequireEach(string pilotId, string copilotId, List<string> crewIds)
        {
            List<string> given = new List<string> { pilotId };
            if (copilotId != null)
            {
                given.Add(copilotId);
            }
            if (crewIds != null)
            {
                given.AddRange(crewIds);
            }
            string twice = given.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).FirstOrDefault();
            if (twice != null)
            {
                throw new CommandException(400, "Pawn '" + twice + "' is given more than one place; a pawn is the pilot, the copilot, or crew.");
            }
        }

        private static CommandResult Run(Map map, string thingId, string pilotId, string copilotId, List<string> crewIds, PrepareBody body)
        {
            string[] missing = GravshipLaunchWindow.Missing();
            if (missing.Length > 0)
            {
                throw new CommandException(409, "This game version's launch window differs from the one RimMCP was written for (missing " + string.Join(", ", missing) + "); the player opens it in game.");
            }
            Thing console = Targets.Parse(map, thingId, null, null, "thing").Thing;
            if (console == null)
            {
                throw new CommandException(404, "Thing not found.");
            }
            if (console.TryGetComp<CompPilotConsole>() == null)
            {
                throw new CommandException(409, console.LabelCap.StripTags() + " is not a pilot console.");
            }
            // An open window is the player's, or an earlier call's with the player maybe changing it: it is
            // never refilled, since that could undo what the player chose. The player cancels it or begins.
            WindowStack stack = Find.WindowStack;
            Window already = stack.Windows.FirstOrDefault(window => window is Dialog_BeginRitual);
            if (already != null)
            {
                throw new CommandException(409, "A ritual window (" + already.GetType().Name + ") is already open, so it is left as it is: it may hold the player's choices. "
                    + "Ask the player to Cancel it or press Begin, then call again. list_dialogs describes an open Gravship launch window.");
            }
            if (Targets.Active())
            {
                throw new CommandException(409, "The player is choosing a target in game; try again when they finish.");
            }

            // The same pawn under two id forms (ThingID and load id) is one pawn, so seats are compared as pawns.
            Pawn pilot = Resolve(map, pilotId);
            Pawn copilot = copilotId == null ? null : Resolve(map, copilotId);
            List<Pawn> crew = crewIds == null ? null : crewIds.Select(id => Resolve(map, id)).ToList();
            List<Pawn> seated = new List<Pawn> { pilot };
            if (copilot != null)
            {
                seated.Add(copilot);
            }
            if (crew != null)
            {
                seated.AddRange(crew);
            }
            Pawn same = seated.GroupBy(pawn => pawn).Where(group => group.Count() > 1).Select(group => group.Key).FirstOrDefault();
            if (same != null)
            {
                throw new CommandException(400, ReadUtil.PawnName(same) + " is given more than one place (under different ids); a pawn is the pilot, the copilot, or crew.");
            }

            Command_Ritual button = Button(console);
            if (button.Disabled)
            {
                throw new CommandException(409, "The 'Launch...' button is unavailable" + (string.IsNullOrEmpty(button.disabledReason) ? "." : ": " + button.disabledReason.StripTags()));
            }

            // The player's click on "Launch...": it opens the window, which fills itself as it opens.
            try
            {
                button.ProcessInput(new UnityEngine.Event());
            }
            catch
            {
                // If the window was added and then failed to open or fill itself, none is left behind.
                foreach (Window opened in Find.WindowStack.Windows.Where(each => each is Dialog_BeginRitual).ToList())
                {
                    Close(opened);
                }
                throw;
            }
            Dialog_BeginGravshipLaunch window = GravshipLaunchWindow.Open();
            if (window == null)
            {
                throw new CommandException(409, "The game did not open the Gravship launch window.");
            }
            // Enter reaches the window's Begin through WindowStack.Notify_PressedAccept, which asks a window's
            // closeOnAccept (or forceCatchAcceptAndCancelEventEvenIfUnfocused); with both off, Enter does
            // nothing here, so a launch begins only with the mouse on the Begin button.
            window.closeOnAccept = false;
            window.forceCatchAcceptAndCancelEventEvenIfUnfocused = false;
            try
            {
                Fill(window, pilot, copilot, crew, body);
            }
            catch
            {
                // A refused call leaves no window behind: closing it is Cancel's own action.
                Close(window);
                throw;
            }
            Dictionary<string, object> described = Describe(window);
            return new CommandResult(true, Dto.Obj(
                Dto.Field("console", SummaryDto.Thing(console)),
                Dto.Field("window", described),
                Dto.Field("note", "The window is open and paused over the game, filled. Nothing has begun: the player presses Begin (or Cancel) in game. On a window opened by this tool Begin is by mouse only: the Enter key is turned off, so typing in the chat cannot begin the launch.")));
        }

        // The console's own "Launch..." button, as the game builds it with the console selected.
        private static Command_Ritual Button(Thing console)
        {
            foreach (GizmoEntry entry in GizmoService.Build(console))
            {
                Command_Ritual ritual = entry.Command as Command_Ritual;
                if (ritual == null || !(GravshipLaunchWindow.LaunchRitual.GetValue(ritual) is Precept_GravshipLaunch))
                {
                    continue;
                }
                TargetInfo target = (TargetInfo)GravshipLaunchWindow.LaunchTarget.GetValue(ritual);
                if (target.Thing == console)
                {
                    return ritual;
                }
            }
            throw new CommandException(409, console.LabelCap.StripTags() + " shows no 'Launch...' button: the gravship launch is a ritual of the player's ideoligion (it needs Ideology and Odyssey) on a pilot console linked to a grav engine. list_gizmos shows its buttons.");
        }

        // The window's own role assignments and its own rules decide, in two passes: every seat is
        // checked first and the assignments are changed only if all are allowed.
        private static void Fill(Dialog_BeginGravshipLaunch window, Pawn pilotPawn, Pawn copilotPawn, List<Pawn> crewPawns, PrepareBody body)
        {
            RitualRoleAssignments assignments = GravshipLaunchWindow.AssignmentsOf(window);
            TargetInfo target = GravshipLaunchWindow.TargetOf(window);
            RitualRole pilotRole = assignments.GetRole(GravshipLaunchWindow.Role);
            RitualRole copilotRole = assignments.GetRole(GravshipLaunchWindow.CoRole);
            if (pilotRole == null || copilotRole == null)
            {
                throw new CommandException(409, "The launch window has no pilot and copilot roles here; the player fills it in game.");
            }
            if (body.BoardColonyMechanoids.HasValue && !ModsConfig.BiotechActive)
            {
                throw new CommandException(409, "The window has no mechanoid checkbox without Biotech.");
            }

            Pawn pilot = Seat(assignments, pilotPawn, pilotRole, "the pilot");
            Pawn copilot = copilotPawn == null ? null : Seat(assignments, copilotPawn, copilotRole, "the copilot");
            List<Pawn> crew = crewPawns == null ? null : crewPawns.Select(pawn => Seat(assignments, pawn, null, "crew")).ToList();

            if (crew != null)
            {
                // Everyone else goes to "Not participating", as dragging them there does: the window's
                // own list of candidates, taken out of every role and out of the crew.
                foreach (Pawn pawn in assignments.AllCandidatePawns.ToList())
                {
                    assignments.RemoveParticipant(pawn);
                }
            }
            else
            {
                // The game's own auto-fill keeps the crew it chose; whoever held a seat the tool takes goes
                // back to the crew, or out if the game will not have them there.
                Vacate(assignments, pilotRole, pilot);
                if (copilot != null)
                {
                    Vacate(assignments, copilotRole, copilot);
                }
            }
            Place(assignments, pilot, pilotRole, "the pilot");
            if (copilot != null)
            {
                Place(assignments, copilot, copilotRole, "the copilot");
            }
            if (crew != null)
            {
                foreach (Pawn member in crew)
                {
                    Place(assignments, member, null, "crew");
                }
            }
            // The seats really hold who was asked for, or nothing is offered.
            if (assignments.RoleForPawn(pilot) != pilotRole || copilot != null && assignments.RoleForPawn(copilot) != copilotRole)
            {
                throw new CommandException(409, "The game did not seat " + ReadUtil.PawnName(assignments.RoleForPawn(pilot) != pilotRole ? pilot : copilot) + " as asked.");
            }
            // The window's quality panel listens for changes, as it does after a drag.
            object drawer = GravshipLaunchWindow.Drawer.GetValue(window);
            System.Reflection.MethodInfo changed = drawer == null ? null : drawer.GetType().GetMethod("Notify_AssignmentsChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (changed != null)
            {
                changed.Invoke(drawer, null);
            }

            if (body.ForceVisitorsToLeave.HasValue)
            {
                GravshipLaunchWindow.ForceVisitors.SetValue(window, body.ForceVisitorsToLeave.Value);
            }
            if (body.BoardColonyAnimals.HasValue)
            {
                GravshipLaunchWindow.BoardAnimals.SetValue(window, body.BoardColonyAnimals.Value);
            }
            if (body.BoardColonyMechanoids.HasValue)
            {
                GravshipLaunchWindow.BoardMechs.SetValue(window, body.BoardColonyMechanoids.Value);
            }
        }

        // The pawn, checked as the game checks it for the seat (null is crew): it must be one the
        // window offers, and the game's own reason is given when it cannot take the seat.
        private static Pawn Resolve(Map map, string pawnId)
        {
            Pawn pawn = PawnReadService.FindPawn(map, pawnId);
            if (pawn == null)
            {
                throw CommandUtil.PawnNotFound(map, pawnId);
            }
            return pawn;
        }

        // Those in a seat, other than the pawn to take it, leave it for the crew.
        private static void Vacate(RitualRoleAssignments assignments, RitualRole role, Pawn incoming)
        {
            foreach (Pawn holder in assignments.AssignedPawns(role).Where(pawn => pawn != incoming).ToList())
            {
                if (!assignments.TryAssignSpectate(holder))
                {
                    assignments.RemoveParticipant(holder);
                }
            }
        }

        private static Pawn Seat(RitualRoleAssignments assignments, Pawn pawn, RitualRole role, string seat)
        {
            string name = ReadUtil.PawnName(pawn);
            string reason = assignments.PawnNotAssignableReason(pawn, role);
            if (reason != null)
            {
                throw new CommandException(409, name + " cannot be " + seat + ": " + reason.StripTags());
            }
            if (!assignments.AllCandidatePawns.Contains(pawn))
            {
                throw new CommandException(409, name + " is not offered by the Gravship launch window, which lists free colonists and prisoners on the map who are not busy in another gathering or ritual.");
            }
            if (role == null && !assignments.CanEverSpectate(pawn))
            {
                throw new CommandException(409, name + " cannot be crew: the game does not let this pawn join this ritual as crew.");
            }
            return pawn;
        }

        private static void Place(RitualRoleAssignments assignments, Pawn pawn, RitualRole role, string seat)
        {
            PsychicRitualRoleDef.Reason ignored;
            bool placed = role == null ? assignments.TryAssignSpectate(pawn) : assignments.TryAssign(pawn, role, out ignored);
            if (!placed)
            {
                throw new CommandException(409, "The game did not put " + ReadUtil.PawnName(pawn) + " in as " + seat + ".");
            }
        }

        private static Dictionary<string, object> Describe(Dialog_BeginGravshipLaunch window)
        {
            try
            {
                return GravshipLaunchWindow.Describe(window);
            }
            catch (System.Exception ex)
            {
                // The window is filled and open; only its description failed.
                return Dto.Obj(Dto.Field("window", window.GetType().Name), Dto.Field("error", "The window is open and filled, but could not be read: " + ex.Message));
            }
        }

        private static void Close(Window window)
        {
            if (Find.WindowStack != null && Find.WindowStack.IsOpen(window))
            {
                window.Close(false);
            }
        }

        [DataContract]
        private sealed class PrepareBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "pilotId")]
            public string PilotId { get; set; }

            [DataMember(Name = "copilotId")]
            public string CopilotId { get; set; }

            [DataMember(Name = "crewIds")]
            public List<string> CrewIds { get; set; }

            [DataMember(Name = "forceVisitorsToLeave")]
            public bool? ForceVisitorsToLeave { get; set; }

            [DataMember(Name = "boardColonyAnimals")]
            public bool? BoardColonyAnimals { get; set; }

            [DataMember(Name = "boardColonyMechanoids")]
            public bool? BoardColonyMechanoids { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
