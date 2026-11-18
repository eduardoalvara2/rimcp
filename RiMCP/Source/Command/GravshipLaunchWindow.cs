using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // The game's "Gravship launch" window (Dialog_BeginGravshipLaunch), read and filled through the
    // objects the window itself uses. Nothing here, or in GravshipLaunchService, ever calls the
    // window's Start (its Begin button and Enter key), the ritual's action callback, or the ritual
    // itself: beginning the launch is the player's, and a test reads this assembly's code to keep it so.
    internal static class GravshipLaunchWindow
    {
        public const string Role = "pilot";
        public const string CoRole = "copilot";

        private const BindingFlags Inner = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        internal static readonly FieldInfo Assignments = typeof(Dialog_BeginRitual).GetField("assignments", Inner);
        internal static readonly FieldInfo RitualField = typeof(Dialog_BeginRitual).GetField("ritual", Inner);
        internal static readonly FieldInfo TargetField = typeof(Dialog_BeginRitual).GetField("target", Inner);
        internal static readonly FieldInfo Drawer = typeof(Dialog_BeginLordJob).GetField("participantsDrawer", Inner);
        internal static readonly FieldInfo ForceVisitors = typeof(Dialog_BeginGravshipLaunch).GetField("forceVisitorsToLeave", Inner);
        internal static readonly FieldInfo BoardAnimals = typeof(Dialog_BeginGravshipLaunch).GetField("boardColonyAnimals", Inner);
        internal static readonly FieldInfo BoardMechs = typeof(Dialog_BeginGravshipLaunch).GetField("boardColonyMechs", Inner);
        internal static readonly MethodInfo Blocking = typeof(Dialog_BeginRitual).GetMethod("BlockingIssues", Inner);
        internal static readonly MethodInfo Factors = typeof(Dialog_BeginLordJob).GetMethod("PopulateQualityFactors", Inner);
        internal static readonly FieldInfo LaunchRitual = typeof(Command_Ritual).GetField("ritual", Inner);
        internal static readonly FieldInfo LaunchTarget = typeof(Command_Ritual).GetField("targetInfo", Inner);

        // Names the game must still have for the window to be read and filled; empty when all are there.
        public static string[] Missing()
        {
            List<string> missing = new List<string>();
            foreach (KeyValuePair<string, object> member in new Dictionary<string, object>
            {
                { "Dialog_BeginRitual.assignments", Assignments },
                { "Dialog_BeginRitual.ritual", RitualField },
                { "Dialog_BeginRitual.target", TargetField },
                { "Dialog_BeginLordJob.participantsDrawer", Drawer },
                { "Dialog_BeginGravshipLaunch.forceVisitorsToLeave", ForceVisitors },
                { "Dialog_BeginGravshipLaunch.boardColonyAnimals", BoardAnimals },
                { "Dialog_BeginGravshipLaunch.boardColonyMechs", BoardMechs },
                { "Dialog_BeginRitual.BlockingIssues", Blocking },
                { "Dialog_BeginLordJob.PopulateQualityFactors", Factors },
                { "Command_Ritual.ritual", LaunchRitual },
                { "Command_Ritual.targetInfo", LaunchTarget }
            })
            {
                if (member.Value == null)
                {
                    missing.Add(member.Key);
                }
            }
            return missing.ToArray();
        }

        // Why use_gizmo does not press the console's "Launch..." button, and what to use instead; null for
        // any other button.
        public static string ButtonReason(Verse.Command command)
        {
            Command_Ritual ritual = command as Command_Ritual;
            return ritual == null || LaunchRitual == null ? null : LaunchReason(LaunchRitual.GetValue(ritual) as Precept_Ritual);
        }

        // The same, for the ritual a button begins.
        public static string LaunchReason(Precept_Ritual ritual)
        {
            if (!(ritual is Precept_GravshipLaunch))
            {
                return null;
            }
            return "It opens the Gravship launch window, which only the player can begin; prepare_gravship_launch opens it already filled.";
        }

        // list_dialogs' description of an open Gravship launch window, or null for any other window or
        // when it cannot be read (the window is then listed without it).
        public static object Read(Window window)
        {
            Dialog_BeginGravshipLaunch launch = window as Dialog_BeginGravshipLaunch;
            if (launch == null || Missing().Length > 0)
            {
                return null;
            }
            try
            {
                return Describe(launch);
            }
            catch (Exception ex)
            {
                return Dto.Obj(Dto.Field("error", "The window could not be read: " + ex.Message));
            }
        }

        public static Dialog_BeginGravshipLaunch Open()
        {
            WindowStack stack = Find.WindowStack;
            return stack == null ? null : stack.Windows.OfType<Dialog_BeginGravshipLaunch>().FirstOrDefault();
        }

        public static RitualRoleAssignments AssignmentsOf(Dialog_BeginGravshipLaunch window)
        {
            return (RitualRoleAssignments)Assignments.GetValue(window);
        }

        public static TargetInfo TargetOf(Dialog_BeginGravshipLaunch window)
        {
            return (TargetInfo)TargetField.GetValue(window);
        }

        // As the window shows itself: who is in each role, the three checkboxes, whether Begin is
        // enabled and what blocks it, and the expected quality with its factors.
        public static Dictionary<string, object> Describe(Dialog_BeginGravshipLaunch window)
        {
            RitualRoleAssignments assignments = AssignmentsOf(window);
            TargetInfo target = TargetOf(window);
            Precept_Ritual ritual = (Precept_Ritual)RitualField.GetValue(window);
            List<Pawn> candidates = assignments.AllCandidatePawns ?? new List<Pawn>();
            List<string> issues = Issues(window);

            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("window", window.GetType().Name),
                Dto.Field("title", window.HeaderLabel.ToString().StripTags()),
                Dto.Field("console", SummaryDto.Thing(target.Thing)),
                Dto.Field("pilot", Person(assignments.FirstAssignedPawn(Role))),
                Dto.Field("copilot", Person(assignments.FirstAssignedPawn(CoRole))),
                Dto.Field("crew", assignments.SpectatorsForReading.Select(Person).ToArray()),
                Dto.Field("notParticipating", candidates.Where(pawn => assignments.RoleForPawn(pawn) == null && !assignments.PawnSpectating(pawn)).Select(Person).ToArray()),
                Dto.Field("forceVisitorsToLeave", (bool)ForceVisitors.GetValue(window)),
                Dto.Field("boardColonyAnimals", (bool)BoardAnimals.GetValue(window)),
                // The window has no mechanoid checkbox without Biotech.
                Dto.Field("boardColonyMechanoids", ModsConfig.BiotechActive ? (object)(bool)BoardMechs.GetValue(window) : null),
                Dto.Field("canBegin", issues.Count == 0 && window.CanBegin),
                Dto.Field("blockingIssues", issues.ToArray()),
                Dto.Field("beginButton", window.OkButtonLabel.ToString().StripTags()));
            AddQuality(dto, window, ritual);
            return dto;
        }

        public static string[] BlockingIssues(Dialog_BeginGravshipLaunch window)
        {
            return Issues(window).ToArray();
        }

        private static List<string> Issues(Dialog_BeginGravshipLaunch window)
        {
            IEnumerable raw = Blocking.Invoke(window, null) as IEnumerable;
            List<string> issues = new List<string>();
            if (raw != null)
            {
                foreach (object issue in raw)
                {
                    string text = issue == null ? null : issue.ToString().StripTags();
                    if (!string.IsNullOrEmpty(text))
                    {
                        issues.Add(text);
                    }
                }
            }
            return issues;
        }

        // The window's quality panel: its factors, most important first as it lists them, and the
        // expected quality it shows under them. A ritual whose outcome has no quality shows none.
        private static void AddQuality(Dictionary<string, object> dto, Dialog_BeginGravshipLaunch window, Precept_Ritual ritual)
        {
            if (ritual != null && ritual.outcomeEffect != null && !ritual.outcomeEffect.ShowQuality)
            {
                dto["expectedQuality"] = null;
                dto["qualityFactors"] = new object[0];
                return;
            }
            object[] args = { default(FloatRange) };
            List<QualityFactor> factors = (Factors.Invoke(window, args) as List<QualityFactor>) ?? new List<QualityFactor>();
            FloatRange range = (FloatRange)args[0];
            dto["expectedQuality"] = Dialog_BeginLordJob.QualityNumberToString(range);
            dto["qualityMin"] = Math.Round(range.min, 4);
            dto["qualityMax"] = Math.Round(range.max, 4);
            dto["qualityFactors"] = factors.OrderByDescending(factor => factor.priority).Select(Factor).ToArray();
        }

        private static object Factor(QualityFactor factor)
        {
            return Dto.Obj(
                Dto.Field("label", Clean(factor.label)),
                Dto.Field("count", Clean(factor.count)),
                Dto.Field("change", Clean(factor.qualityChange)),
                Dto.Field("positive", factor.positive),
                Dto.Field("present", factor.present),
                Dto.Field("uncertain", factor.uncertainOutcome ? (object)true : null),
                Dto.Field("tip", Clean(factor.toolTip)));
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? null : text.StripTags();
        }

        private static object Person(Pawn pawn)
        {
            return SummaryDto.Pawn(pawn);
        }
    }
}
