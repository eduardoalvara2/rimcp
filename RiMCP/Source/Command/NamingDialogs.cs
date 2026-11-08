using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Read;
using Verse;
using Verse.Grammar;

namespace RiMCP.Command
{
    // The dialogs the game opens on its own a few days into a colony to name the player's faction,
    // its settlement, or both, which pause the game until they are answered. An answer types the
    // names into the dialog's text boxes and runs its OK button's code: the dialog's own validity
    // check, its own naming code (in commitment mode the game also renames the save, as it does for
    // the player), the message it shows, and closing it. The names are then the game's, so it does
    // not ask again. Main thread only.
    internal static class NamingDialogs
    {
        public const string Ok = "ok";
        public const string FactionName = "factionName";
        public const string SettlementName = "settlementName";

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo CurName = typeof(Dialog_GiveName).GetField("curName", Instance);
        private static readonly FieldInfo CurSecondName = typeof(Dialog_GiveName).GetField("curSecondName", Instance);
        private static readonly FieldInfo UseSecondName = typeof(Dialog_GiveName).GetField("useSecondName", Instance);
        private static readonly FieldInfo SuggestingPawn = typeof(Dialog_GiveName).GetField("suggestingPawn", Instance);
        private static readonly FieldInfo NameMessageKey = typeof(Dialog_GiveName).GetField("nameMessageKey", Instance);
        private static readonly FieldInfo SecondNameMessageKey = typeof(Dialog_GiveName).GetField("secondNameMessageKey", Instance);
        private static readonly FieldInfo GainedNameMessageKey = typeof(Dialog_GiveName).GetField("gainedNameMessageKey", Instance);
        private static readonly FieldInfo InvalidNameMessageKey = typeof(Dialog_GiveName).GetField("invalidNameMessageKey", Instance);
        private static readonly FieldInfo InvalidSecondNameMessageKey = typeof(Dialog_GiveName).GetField("invalidSecondNameMessageKey", Instance);
        private static readonly PropertyInfo FirstCharLimit = typeof(Dialog_GiveName).GetProperty("FirstCharLimit", Instance);
        private static readonly PropertyInfo SecondCharLimit = typeof(Dialog_GiveName).GetProperty("SecondCharLimit", Instance);
        private static readonly MethodInfo IsValidName = typeof(Dialog_GiveName).GetMethod("IsValidName", Instance, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo IsValidSecondName = typeof(Dialog_GiveName).GetMethod("IsValidSecondName", Instance, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo Named = typeof(Dialog_GiveName).GetMethod("Named", Instance, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo NamedSecond = typeof(Dialog_GiveName).GetMethod("NamedSecond", Instance, null, new[] { typeof(string) }, null);
        private static readonly FieldInfo FactionAndSettlementOf = typeof(Dialog_NamePlayerFactionAndSettlement).GetField("settlement", Instance);
        private static readonly FieldInfo SettlementOf = typeof(Dialog_NamePlayerSettlement).GetField("settlement", Instance);

        // Only the game's faction and settlement dialogs, whose names answer_dialog knows, and only
        // while the members their OK button uses are there.
        public static bool Handles(Dialog_GiveName dialog)
        {
            bool known = dialog is Dialog_NamePlayerFactionAndSettlement || dialog is Dialog_NamePlayerFaction || dialog is Dialog_NamePlayerSettlement;
            return known && CurName != null && CurSecondName != null && UseSecondName != null && GainedNameMessageKey != null
                && FirstCharLimit != null && SecondCharLimit != null && IsValidName != null && IsValidSecondName != null && Named != null && NamedSecond != null;
        }

        // The answer_dialog fields the dialog asks for, its first text box first.
        public static List<string> Fields(Dialog_GiveName dialog)
        {
            if (dialog is Dialog_NamePlayerFactionAndSettlement)
            {
                return new List<string> { FactionName, SettlementName };
            }
            return new List<string> { dialog is Dialog_NamePlayerFaction ? FactionName : SettlementName };
        }

        // Each name as list_dialogs shows it: what it names, the text in its box now (the game's
        // suggestion until someone types), the longest name of plain letters the dialog's own check
        // takes, and the settlement's name now.
        public static object Describe(Dialog_GiveName dialog)
        {
            return Fields(dialog).Select(field =>
            {
                bool second = IsSecond(dialog, field);
                Settlement settlement = field == SettlementName ? SettlementBeingNamed(dialog) : null;
                return Dto.Obj(
                    Dto.Field("field", field),
                    Dto.Field("names", field == FactionName ? "faction" : "settlement"),
                    Dto.Field("text", (second ? CurSecondName : CurName).GetValue(dialog) as string),
                    Dto.Field("maxLength", Longest(dialog, field)),
                    Dto.Field("settlement", settlement == null ? null : Dto.Obj(
                        Dto.Field("id", settlement.ID),
                        Dto.Field("name", settlement.Name))));
            }).ToArray();
        }

        // Why the dialog's own check refuses the name, or null when it takes it. The check sees the
        // name trimmed, as OK does.
        public static string Refusal(Dialog_GiveName dialog, string field, string name)
        {
            string trimmed = (name ?? "").Trim();
            if (Valid(dialog, field, trimmed))
            {
                return null;
            }
            string what = field == FactionName ? "faction" : "settlement";
            string why = Why(dialog, field, trimmed);
            string said = GameText((string)(IsSecond(dialog, field) ? InvalidSecondNameMessageKey : InvalidNameMessageKey).GetValue(dialog));
            return "The dialog refuses the " + what + " name '" + trimmed + "'" + (why == null ? "." : ": " + why + ".")
                + (string.IsNullOrEmpty(said) ? "" : " The game says: " + said.StripTags().Trim());
        }

        // As typing into the name's text box.
        public static void Type(Dialog_GiveName dialog, string field, string name)
        {
            (IsSecond(dialog, field) ? CurSecondName : CurName).SetValue(dialog, name);
        }

        // OK, as the dialog's button: the names applied, the game's message, and the dialog closed.
        public static void Answer(Dialog_GiveName dialog)
        {
            string[] names = Apply(dialog);
            string gained = GainedNameMessageKey.GetValue(dialog) as string;
            if (!string.IsNullOrEmpty(gained) && LanguageDatabase.activeLanguage != null)
            {
                TaggedString message = names.Length == 2 ? gained.Translate(names[0], names[1]) : gained.Translate(names[0]);
                Messages.Message(message, MessageTypeDefOf.TaskCompletion, false);
            }
            Find.WindowStack.TryRemove(dialog);
        }

        // What OK runs before its message: the text boxes trimmed, the dialog's own validity check,
        // then its own naming code for each name. Returns the names given.
        internal static string[] Apply(Dialog_GiveName dialog)
        {
            string first = ((CurName.GetValue(dialog) as string) ?? "").Trim();
            bool two = (bool)UseSecondName.GetValue(dialog);
            string second = two ? ((CurSecondName.GetValue(dialog) as string) ?? "").Trim() : null;
            if (!(bool)Call(IsValidName, dialog, first) || two && !(bool)Call(IsValidSecondName, dialog, second))
            {
                throw new CommandException(409, "The dialog refused the names when OK was pressed; read it again with list_dialogs.");
            }
            Call(Named, dialog, first);
            if (two)
            {
                Call(NamedSecond, dialog, second);
            }
            return two ? new[] { first, second } : new[] { first };
        }

        // The dialog's question, as it shows it.
        public static string Question(Dialog_GiveName dialog)
        {
            Pawn pawn = SuggestingPawn == null ? null : SuggestingPawn.GetValue(dialog) as Pawn;
            if (pawn == null || LanguageDatabase.activeLanguage == null)
            {
                return null;
            }
            string first = NameMessageKey == null ? null : NameMessageKey.GetValue(dialog) as string;
            string second = SecondNameMessageKey == null || !(bool)UseSecondName.GetValue(dialog) ? null : SecondNameMessageKey.GetValue(dialog) as string;
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(first))
            {
                parts.Add(first.Translate(pawn.LabelShort, pawn).CapitalizeFirst().ToString());
            }
            if (!string.IsNullOrEmpty(second))
            {
                parts.Add(second.Translate(pawn.LabelShort, pawn).ToString());
            }
            return parts.Count == 0 ? null : string.Join("\n\n", parts.ToArray());
        }

        public static string OkText()
        {
            return GameText("OK") ?? "OK";
        }

        // The naming code the dialog runs, so a mod's dialog is known for its own code.
        public static Delegate OwnCode(Dialog_GiveName dialog)
        {
            MethodInfo own = dialog.GetType().GetMethod("Named", Instance, null, new[] { typeof(string) }, null);
            return own == null || own.IsAbstract ? null : Delegate.CreateDelegate(typeof(Action<string>), dialog, own, false);
        }

        private static bool Valid(Dialog_GiveName dialog, string field, string name)
        {
            bool second = IsSecond(dialog, field);
            return name.Length <= Limit(dialog, second) && (bool)Call(second ? IsValidSecondName : IsValidName, dialog, name);
        }

        // The game says only that a name is invalid; this says which part of its check failed.
        private static string Why(Dialog_GiveName dialog, string field, string name)
        {
            if (name.Length == 0)
            {
                return "it is empty";
            }
            int longest = Longest(dialog, field);
            if (name.Length > longest)
            {
                return "it has " + name.Length + " characters, and the dialog takes at most " + longest;
            }
            string fileCharacters = field == FactionName ? GenText.GetInvalidFilenameCharacters() : "";
            string[] refused = name.Distinct()
                .Where(each => GrammarResolver.ContainsSpecialChars(each.ToString()) || fileCharacters.IndexOf(each) >= 0)
                .Select(each => char.IsControl(each) ? "U+" + ((int)each).ToString("X4") : each.ToString())
                .ToArray();
            return refused.Length == 0 ? null : "it has characters the game does not take there: " + string.Join(" ", refused);
        }

        // The longest name of plain letters the dialog's own check takes, which can be less than its
        // text box holds: a faction name also has to be a valid file name.
        private static int Longest(Dialog_GiveName dialog, string field)
        {
            bool second = IsSecond(dialog, field);
            int low = 0;
            int high = Limit(dialog, second);
            while (low < high)
            {
                int length = (low + high + 1) / 2;
                if (Valid(dialog, field, new string('a', length)))
                {
                    low = length;
                }
                else
                {
                    high = length - 1;
                }
            }
            return low;
        }

        private static int Limit(Dialog_GiveName dialog, bool second)
        {
            return (int)(second ? SecondCharLimit : FirstCharLimit).GetValue(dialog, null);
        }

        private static bool IsSecond(Dialog_GiveName dialog, string field)
        {
            return dialog is Dialog_NamePlayerFactionAndSettlement && field == SettlementName;
        }

        private static Settlement SettlementBeingNamed(Dialog_GiveName dialog)
        {
            FieldInfo field = dialog is Dialog_NamePlayerFactionAndSettlement ? FactionAndSettlementOf : dialog is Dialog_NamePlayerSettlement ? SettlementOf : null;
            return field == null ? null : field.GetValue(dialog) as Settlement;
        }

        // A key in the game's language, or null before languages load.
        private static string GameText(string key)
        {
            if (string.IsNullOrEmpty(key) || LanguageDatabase.activeLanguage == null)
            {
                return null;
            }
            return key.Translate().ToString();
        }

        private static object Call(MethodInfo method, object target, string argument)
        {
            try
            {
                return method.Invoke(target, new object[] { argument });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }
}
