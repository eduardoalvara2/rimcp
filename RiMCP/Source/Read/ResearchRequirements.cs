using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // One start requirement ResearchProjectDef.CanStartNow checks: Met is the game's own check of
    // it, and Describe says what is missing.
    internal sealed class ResearchRequirement
    {
        public string Kind;
        public Func<ResearchProjectDef, bool> Met;
        public Func<ResearchProjectDef, Dictionary<string, object>> Describe;
    }

    // What keeps a research project from starting, so a refusal can name it instead of only saying
    // it cannot start now: every requirement CanStartNow checks besides being finished, in its order,
    // each read with the game's own check. Main thread only, with a colony loaded.
    internal static class ResearchRequirements
    {
        // The checks read the active expansions; tests replace Met.
        internal static readonly ResearchRequirement[] All =
        {
            new ResearchRequirement { Kind = "prerequisitesUnfinished", Met = project => project.PrerequisitesCompleted, Describe = Prerequisites },
            new ResearchRequirement { Kind = "techprintsMissing", Met = project => project.TechprintRequirementMet, Describe = Techprints },
            new ResearchRequirement { Kind = "missingResearchBench", Met = project => project.requiredResearchBuilding == null || project.PlayerHasAnyAppropriateResearchBench, Describe = Bench },
            new ResearchRequirement
            {
                Kind = "noMechanitor",
                Met = project => project.PlayerMechanitorRequirementMet,
                Describe = project => Blockers.Of("noMechanitor", "It needs a mechanitor in the colony (Biotech): a colonist with a mechlink. Mechanitor research cannot start until one joins or has a mechlink installed.")
            },
            new ResearchRequirement { Kind = "notStudied", Met = project => project.AnalyzedThingsRequirementsMet, Describe = Studied },
            new ResearchRequirement
            {
                Kind = "undiscovered",
                Met = project => !project.IsHidden,
                Describe = project => Blockers.Of("undiscovered", "It is hidden until the colony discovers what it studies (Anomaly).")
            },
            new ResearchRequirement
            {
                Kind = "gravEngineNotInspected",
                Met = project => project.InspectionRequirementsMet,
                Describe = project => Blockers.Of("gravEngineNotInspected", "It needs the grav engine inspected first (Odyssey).")
            }
        };

        // The requirements not met now, as blockers with a kind and a message.
        public static List<object> Unmet(ResearchProjectDef project)
        {
            return All.Where(requirement => !requirement.Met(project)).Select(requirement => (object)requirement.Describe(project)).ToList();
        }

        public static string Refusal(ResearchProjectDef project, List<object> unmet)
        {
            string[] messages = unmet.OfType<Dictionary<string, object>>().Select(each => each["message"] as string).Where(each => each != null).ToArray();
            return ReadUtil.DefLabel(project) + " cannot be started now. " + string.Join(" ", messages) + " get_def lists its startRequirements.";
        }

        private static Dictionary<string, object> Prerequisites(ResearchProjectDef project)
        {
            string[] visible = Unfinished(project.prerequisites);
            int hidden = Unfinished(project.hiddenPrerequisites).Length;
            List<string> parts = new List<string>();
            if (visible.Length > 0)
            {
                parts.Add("finish its prerequisites first: " + string.Join(", ", visible));
            }
            if (hidden > 0)
            {
                parts.Add(hidden + " hidden prerequisite" + (hidden == 1 ? " is" : "s are") + " not finished yet");
            }
            return Blockers.Of("prerequisitesUnfinished", "It needs other research first: " + string.Join("; ", parts.ToArray()) + ".",
                Dto.Field("defNames", visible),
                Dto.Field("hiddenUnfinished", hidden > 0 ? (object)hidden : null));
        }

        private static Dictionary<string, object> Techprints(ResearchProjectDef project)
        {
            ThingDef techprint = project.Techprint;
            return Blockers.Of("techprintsMissing", "It needs " + project.TechprintCount + " techprints applied, and " + project.TechprintsApplied + " are (Royalty): get them by trade, quests or raids, and a colonist applies them.",
                Dto.Field("applied", project.TechprintsApplied),
                Dto.Field("needed", project.TechprintCount),
                Dto.Field("techprint", techprint == null ? null : techprint.defName));
        }

        private static Dictionary<string, object> Bench(ResearchProjectDef project)
        {
            ThingDef bench = project.requiredResearchBuilding;
            string[] facilities = (project.requiredResearchFacilities ?? new List<ThingDef>()).Select(facility => facility.defName).ToArray();
            return Blockers.Of("missingResearchBench", "It needs a " + (string.IsNullOrEmpty(bench.label) ? bench.defName : bench.label) + (facilities.Length == 0 ? "" : " with these linked: " + string.Join(", ", facilities)) + " (power is not needed to start it).",
                Dto.Field("defName", bench.defName),
                Dto.Field("facilities", facilities.Length == 0 ? null : facilities));
        }

        private static Dictionary<string, object> Studied(ResearchProjectDef project)
        {
            string[] missing = (project.requiredAnalyzed ?? new List<ThingDef>()).Where(thing => !IsStudied(thing)).Select(thing => thing.defName).ToArray();
            return Blockers.Of("notStudied", "It needs these studied first (Anomaly): " + string.Join(", ", missing) + ".", Dto.Field("defNames", missing));
        }

        // As AnalyzedThingsCompleted counts one.
        private static bool IsStudied(ThingDef thing)
        {
            CompProperties_CompAnalyzableUnlockResearch props = thing.GetCompProperties<CompProperties_CompAnalyzableUnlockResearch>();
            AnalysisDetails details;
            Find.AnalysisManager.TryGetAnalysisProgress(props == null ? -1 : props.analysisID, out details);
            return details != null && details.Satisfied;
        }

        private static string[] Unfinished(List<ResearchProjectDef> projects)
        {
            return projects == null ? new string[0] : projects.Where(each => !each.IsFinished).Select(each => each.defName).ToArray();
        }
    }
}
