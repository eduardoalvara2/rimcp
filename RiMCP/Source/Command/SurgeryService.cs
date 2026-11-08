using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // Operations as a pawn's Health tab offers them, under the same rules: the pawns it allows
    // operations on, the recipes and body parts its Operations list shows, the options it shows
    // disabled, and the bill its click adds.
    internal static class SurgeryService
    {
        public static BridgeResponse ListSurgeries(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Pawn pawn = RequirePatient(context.Map, route["pawnId"]);
            List<SurgeryOption> options = Options(pawn);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("pawn", SummaryDto.Pawn(pawn)),
                Dto.Field("options", options.Select(option => option.ToDto(pawn)).ToArray()),
                Dto.Field("queued", pawn.BillStack.Bills.Select(Queued).ToArray()),
                Dto.Field("billCapacity", Dto.Obj(
                    Dto.Field("count", pawn.BillStack.Count),
                    Dto.Field("max", BillStack.MaxCount)))));
        }

        public static BridgeResponse AddSurgeryBill(BridgeRequest request, RouteMatch route)
        {
            AddSurgeryBillBody body = CommandUtil.ReadBody<AddSurgeryBillBody>(request);
            if (string.IsNullOrWhiteSpace(body.RecipeDefName))
            {
                throw new CommandException(400, "Missing required field 'recipeDefName'.");
            }
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            string pawnId = route["pawnId"];
            // The game allows the same operation queued twice, so a retry with the same requestId
            // returns the bill it added instead of adding another.
            RequestKey key = RequestKey.For("add_surgery_bill", requestId, context.Map, pawnId, body.RecipeDefName, body.PartIndex);
            return CommandReceipts.Once(context, key, () => Add(context.Map, pawnId, body));
        }

        private static CommandResult Add(Map map, string pawnId, AddSurgeryBillBody body)
        {
            Pawn pawn = RequirePatient(map, pawnId);
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(body.RecipeDefName.Trim());
            if (recipe == null)
            {
                throw new CommandException(404, "Recipe not found.");
            }
            string name = ReadUtil.PawnName(pawn);
            if (!pawn.def.AllRecipes.Contains(recipe))
            {
                throw new CommandException(409, "'" + ReadUtil.DefLabel(recipe) + "' is not an operation for " + name + ".");
            }
            if (!recipe.AvailableNow)
            {
                throw new CommandException(409, "'" + ReadUtil.DefLabel(recipe) + "' is not available yet" + (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished ? ": it needs the research " + ReadUtil.DefLabel(recipe.researchPrerequisite) + "." : "."));
            }
            BodyPartRecord part = null;
            if (recipe.targetsBodyPart)
            {
                if (!body.PartIndex.HasValue)
                {
                    throw new CommandException(400, "'" + ReadUtil.DefLabel(recipe) + "' is done on a body part; give partIndex, from list_surgeries.");
                }
                part = recipe.Worker.GetPartsToApplyOn(pawn, recipe).FirstOrDefault(each => each.Index == body.PartIndex.Value);
                if (part == null || !recipe.AvailableOnNow(pawn, part))
                {
                    throw new CommandException(409, "'" + ReadUtil.DefLabel(recipe) + "' is not applicable to that part of " + name + " now. list_surgeries lists the parts it is.");
                }
            }
            else if (body.PartIndex.HasValue)
            {
                throw new CommandException(400, "'" + ReadUtil.DefLabel(recipe) + "' is not done on a body part; leave out partIndex.");
            }

            SurgeryOption option = Options(pawn).FirstOrDefault(each => each.Recipe == recipe && each.Part == part);
            if (option == null)
            {
                throw new CommandException(409, "The Health tab does not offer '" + ReadUtil.DefLabel(recipe) + "' for " + name + ": " + NotOffered(pawn, recipe));
            }
            if (option.DisabledReason != null)
            {
                throw new CommandException(409, "'" + option.Label + "' is unavailable: " + option.DisabledReason);
            }
            if (option.Unsupported != null)
            {
                throw new CommandException(409, "'" + option.Label + "' " + option.Unsupported);
            }
            if (pawn.BillStack.Count >= BillStack.MaxCount)
            {
                throw new CommandException(409, name + " already has the maximum of " + BillStack.MaxCount + " bills.");
            }

            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("pawn", SummaryDto.Pawn(pawn)),
                Dto.Field("alreadyQueued", AlreadyQueued(pawn, recipe, part)));
            // As the option's click: the game asks first, as before an implant a faction's title
            // forbids, and queues only on Yes.
            string confirmation = Confirmation(pawn, recipe);
            if (confirmation != null)
            {
                List<Window> before = FollowUps.Snapshot();
                Find.WindowStack.Add(new Dialog_MessageBox(confirmation, "Yes".Translate(), () => Create(pawn, recipe, part), "No".Translate()));
                data["queued"] = false;
                data["asking"] = "The game asks before queuing '" + option.Label + "'; answer with answer_dialog.";
                FollowUps.Report(data, "add_surgery_bill", option.Label, FollowUps.OpenedSince(before), null, pawn);
                return new CommandResult(false, data);
            }
            Bill_Medical bill = Create(pawn, recipe, part);
            data["queued"] = true;
            data["bill"] = Queued(bill);
            data["position"] = pawn.BillStack.Bills.IndexOf(bill) + 1;
            data["warnings"] = Warnings(pawn, recipe, part).ToArray();
            return new CommandResult(true, data);
        }

        // Without the game's messages and its "nobody can do this" box, which the warnings report.
        private static Bill_Medical Create(Pawn pawn, RecipeDef recipe, BodyPartRecord part)
        {
            return HealthCardUtility.CreateSurgeryBill(pawn, recipe, part, null, false);
        }

        // As ITab_Pawn_Health.ShouldAllowOperations: who the Health tab offers operations on.
        private static Pawn RequirePatient(Map map, string pawnId)
        {
            Pawn pawn = PawnReadService.FindPawn(map, pawnId);
            if (pawn == null)
            {
                throw new CommandException(404, "Pawn not found.");
            }
            string name = ReadUtil.PawnName(pawn);
            if (pawn.Dead)
            {
                throw new CommandException(409, name + " is dead; operations are not done on corpses.");
            }
            if (!pawn.def.AllRecipes.Any(recipe => recipe.AvailableNow && recipe.AvailableOnNow(pawn)))
            {
                throw new CommandException(409, "No operation is available for " + name + " now.");
            }
            if (pawn.IsMutant && !pawn.mutant.Def.entitledToMedicalCare)
            {
                throw new CommandException(409, name + " is not given medical care.");
            }
            if (pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony || pawn.HostFaction == Faction.OfPlayer && !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
            {
                return pawn;
            }
            if (!(pawn.RaceProps.IsFlesh && pawn.Faction != null && pawn.Faction.HostileTo(Faction.OfPlayer)) && !pawn.RaceProps.Humanlike && pawn.Downed)
            {
                return pawn;
            }
            throw new CommandException(409, "The Health tab offers no operations on " + name + ": operations are for the colony's own pawns, its prisoners, guests in its care who cannot move, and downed animals that are not hostile.");
        }

        // As HealthCardUtility.DrawMedOperationsTab builds the Operations list.
        private static List<SurgeryOption> Options(Pawn pawn)
        {
            List<SurgeryOption> options = new List<SurgeryOption>();
            foreach (RecipeDef recipe in pawn.def.AllRecipes)
            {
                if (!recipe.AvailableNow)
                {
                    continue;
                }
                AcceptanceReport report = recipe.Worker.AvailableReport(pawn);
                if (!report.Accepted && report.Reason.NullOrEmpty())
                {
                    continue;
                }
                List<ThingDef> missing = recipe.PotentiallyMissingIngredients(null, pawn.MapHeld).ToList();
                if (HiddenForMissing(recipe, missing))
                {
                    continue;
                }
                if (recipe.targetsBodyPart)
                {
                    foreach (BodyPartRecord part in recipe.Worker.GetPartsToApplyOn(pawn, recipe))
                    {
                        if (recipe.AvailableOnNow(pawn, part))
                        {
                            options.Add(new SurgeryOption(pawn, recipe, part, report, missing));
                        }
                    }
                }
                else if (!pawn.health.hediffSet.HasHediff(recipe.addsHediff))
                {
                    options.Add(new SurgeryOption(pawn, recipe, null, report, missing));
                }
            }
            return options;
        }

        // The list leaves out implants and drugs not on the map, and recipes that ask to be hidden
        // while anything is missing.
        private static bool HiddenForMissing(RecipeDef recipe, List<ThingDef> missing)
        {
            return missing.Any(def => def.isTechHediff || def.IsDrug) || missing.Count > 0 && recipe.dontShowIfAnyIngredientMissing;
        }

        private static string NotOffered(Pawn pawn, RecipeDef recipe)
        {
            AcceptanceReport report = recipe.Worker.AvailableReport(pawn);
            if (!report.Accepted)
            {
                return "it is not something " + ReadUtil.PawnName(pawn) + " can have now.";
            }
            List<ThingDef> missing = recipe.PotentiallyMissingIngredients(null, pawn.MapHeld).ToList();
            if (HiddenForMissing(recipe, missing))
            {
                return "it needs " + string.Join(", ", missing.Select(def => def.label).ToArray()) + ", and there is none on the map.";
            }
            if (!recipe.targetsBodyPart && recipe.addsHediff != null && pawn.health.hediffSet.HasHediff(recipe.addsHediff))
            {
                return ReadUtil.PawnName(pawn) + " already has " + recipe.addsHediff.label + ".";
            }
            return "it is not available now.";
        }

        // The question the option's click asks before queuing, as GenerateSurgeryOption does.
        private static string Confirmation(Pawn pawn, RecipeDef recipe)
        {
            HediffDef hediff = recipe.addsHediff ?? recipe.changesHediffLevel;
            if (hediff != null)
            {
                TaggedString violation = CompRoyalImplant.CheckForViolations(pawn, hediff, recipe.hediffLevelOffset);
                if (!violation.NullOrEmpty())
                {
                    return violation.Resolve();
                }
            }
            TaggedString confirmation = recipe.Worker.GetConfirmation(pawn);
            return confirmation.NullOrEmpty() ? null : confirmation.Resolve();
        }

        // What the game says once the bill is added, as CreateSurgeryBill does with its messages.
        private static List<object> Warnings(Pawn pawn, RecipeDef recipe, BodyPartRecord part)
        {
            List<object> warnings = new List<object>();
            Map map = pawn.MapHeld;
            bool anyDoctor = map.mapPawns.PawnsInFaction(Faction.OfPlayer)
                .Where(each => each.IsFreeColonist || each.IsColonyMechPlayerControlled)
                .Any(recipe.PawnSatisfiesSkillRequirements);
            if (!anyDoctor)
            {
                warnings.Add(Warning("noSkilledDoctor", "RecipeRequiresSkills".Translate(ReadUtil.DefLabel(recipe)) + " " + recipe.MinSkillString));
            }
            if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe && !map.mapPawns.FreeColonists.Any(MechanitorUtility.IsMechanitor))
            {
                warnings.Add(Warning("noMechanitor", "Only a mechanitor can do '" + ReadUtil.DefLabel(recipe) + "', and the colony has none here."));
            }
            if (!pawn.InBed() && pawn.RaceProps.IsFlesh)
            {
                if (pawn.RaceProps.Humanlike)
                {
                    if (!map.listerBuildings.allBuildingsColonist.Any(building => building is Building_Bed && RestUtility.CanUseBedEver(pawn, building.def) && ((Building_Bed)building).Medical))
                    {
                        warnings.Add(Warning("noMedicalBed", "MessageNoMedicalBeds".Translate()));
                    }
                }
                else if (!map.listerBuildings.allBuildingsColonist.Any(building => building is Building_Bed && RestUtility.CanUseBedEver(pawn, building.def)))
                {
                    warnings.Add(Warning("noAnimalBed", "MessageNoAnimalBeds".Translate()));
                }
            }
            if (pawn.Faction != null && !pawn.Faction.Hidden && !pawn.Faction.HostileTo(Faction.OfPlayer) && recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer))
            {
                warnings.Add(Warning("angersFaction", "MessageMedicalOperationWillAngerFaction".Translate(pawn.HomeFaction)));
            }
            return warnings;
        }

        private static object Warning(string kind, string message)
        {
            return Dto.Obj(Dto.Field("kind", kind), Dto.Field("message", message.StripTags().Trim()));
        }

        private static bool AlreadyQueued(Pawn pawn, RecipeDef recipe, BodyPartRecord part)
        {
            return pawn.BillStack.Bills.OfType<Bill_Medical>().Any(bill => bill.recipe == recipe && bill.Part == part);
        }

        private static object Queued(Bill bill)
        {
            Bill_Medical medical = bill as Bill_Medical;
            return Dto.Obj(
                Dto.Field("billId", bill.GetUniqueLoadID()),
                Dto.Field("recipeDefName", bill.recipe.defName),
                Dto.Field("label", bill.LabelCap.StripTags()),
                Dto.Field("part", Part(medical == null ? null : medical.Part)),
                Dto.Field("suspended", bill.suspended));
        }

        private static object Part(BodyPartRecord part)
        {
            return part == null ? null : Dto.Obj(
                Dto.Field("index", part.Index),
                Dto.Field("label", part.LabelCap),
                Dto.Field("defName", part.def.defName));
        }

        private sealed class SurgeryOption
        {
            public readonly RecipeDef Recipe;
            public readonly BodyPartRecord Part;
            public readonly string Label;
            public readonly string DisabledReason;
            public readonly string Unsupported;
            private readonly List<ThingDef> missing;

            // As GenerateSurgeryOption labels the option and disables it.
            public SurgeryOption(Pawn pawn, RecipeDef recipe, BodyPartRecord part, AcceptanceReport report, List<ThingDef> missing)
            {
                Recipe = recipe;
                Part = part;
                this.missing = missing;
                string label = recipe.Worker.GetLabelWhenUsedOn(pawn, part).CapitalizeFirst();
                if (part != null && !recipe.hideBodyPartNames)
                {
                    label += " (" + part.Label + ")";
                }
                Label = label.StripTags();
                if (!report.Reason.NullOrEmpty())
                {
                    DisabledReason = report.Reason.StripTags();
                }
                else if (missing.Count > 0)
                {
                    DisabledReason = "missing ingredients: " + string.Join(", ", missing.Select(def => "MissingMedicalBillIngredient".Translate(def.label).ToString()).ToArray()) + ".";
                }
                else if (recipe.Worker is Recipe_ImplantXenogerm)
                {
                    Unsupported = "asks for the xenogerm in a window agents cannot use yet; it is left to the player.";
                }
            }

            public object ToDto(Pawn pawn)
            {
                return Dto.Obj(
                    Dto.Field("id", Recipe.defName + "@" + (Part == null ? "-" : Part.Index.ToString())),
                    Dto.Field("recipeDefName", Recipe.defName),
                    Dto.Field("label", Label),
                    Dto.Field("part", SurgeryService.Part(Part)),
                    Dto.Field("disabledReason", DisabledReason),
                    Dto.Field("unsupported", Unsupported),
                    Dto.Field("missingIngredients", missing.Count == 0 ? null : missing.Select(def => def.label).ToArray()),
                    Dto.Field("skillRequired", Recipe.skillRequirements.NullOrEmpty() ? null : Recipe.skillRequirements.Select(requirement => Dto.Obj(
                        Dto.Field("skill", requirement.skill.defName),
                        Dto.Field("level", requirement.minLevel))).ToArray()),
                    Dto.Field("mechanitorOnly", Recipe.mechanitorOnlyRecipe ? (object)true : null),
                    Dto.Field("alreadyQueued", AlreadyQueued(pawn, Recipe, Part)));
            }
        }

        [DataContract]
        private sealed class AddSurgeryBillBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "recipeDefName")]
            public string RecipeDefName { get; set; }

            [DataMember(Name = "partIndex")]
            public int? PartIndex { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
