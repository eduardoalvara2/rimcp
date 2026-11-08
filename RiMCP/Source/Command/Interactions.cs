using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Read;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    internal sealed class DialogOption
    {
        public readonly string Id;
        public readonly int Index;
        public readonly string Text;
        public readonly string DisabledReason;

        public DialogOption(string id, int index, string text, string disabledReason)
        {
            Id = id;
            Index = index;
            Text = text;
            DisabledReason = disabledReason;
        }

        public bool SameAs(DialogOption other)
        {
            return other != null && other.Index == Index && other.Text == Text && other.DisabledReason == DisabledReason;
        }

        public object ToDto()
        {
            return Dto.Obj(Dto.Field("id", Id), Dto.Field("text", Text), Dto.Field("disabledReason", DisabledReason));
        }
    }

    internal sealed class DialogAdapter
    {
        private static readonly FieldInfo CurNode = typeof(Dialog_NodeTree).GetField("curNode", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NodeTitle = typeof(Dialog_NodeTree).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InteractiveAt = typeof(Dialog_NodeTree).GetField("makeInteractiveAtTime", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Activate = typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo DelayExpired = typeof(Dialog_MessageBox).GetProperty("InteractionDelayExpired", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MenuOptions = typeof(FloatMenu).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MenuTitle = typeof(FloatMenu).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SliderValue = typeof(Dialog_Slider).GetField("curValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SliderConfirm = typeof(Dialog_Slider).GetField("confirmAction", BindingFlags.Instance | BindingFlags.NonPublic);

        public string Kind;
        public Window Window;
        public Func<bool> IsOpen;
        public Func<object> PageKey;
        public Func<string> Title;
        public Func<string> Question;
        public Func<List<DialogOption>> Options;
        public Func<DialogOption, string> NotReady;
        public Func<bool> CanCancel;
        public Action<DialogOption> Choose;
        public Action Cancel;
        public Func<bool> Covered;
        public int? Min;
        public int? Max;
        public Action<int> SetValue;
        public Func<DialogOption, Delegate> Callback;
        // Answers that ran, whoever chose them, so a player's answer that leaves the dialog where it
        // was still shows. Null where every answer closes the dialog or moves it on.
        public Func<int> Activations;
        public Action Arm;
        // For a naming dialog: the answer_dialog fields it asks for (factionName, settlementName),
        // what list_dialogs shows of them, the dialog's own check of a name, and typing one in.
        // Null for every other dialog.
        public Func<List<string>> NameFields;
        public Func<object> Names;
        public Func<string, string, string> NameRefusal;
        public Action<string, string> SetName;

        // A dialog's own action under the counting wrappers adapters put around it, so a dialog
        // adapted again still finds the dialog's own code, a mod's included.
        public static Delegate Unwrapped(Delegate action)
        {
            Counted wrapper;
            while ((wrapper = action == null ? null : action.Target as Counted) != null)
            {
                action = wrapper.Original;
            }
            return action;
        }

        private static Action Wrap(Action original, Action count)
        {
            return new Counted(original, count).Run;
        }

        // A wrapper an adapter put around a dialog's action, known by its target.
        private sealed class Counted
        {
            public readonly Action Original;
            private readonly Action count;

            public Counted(Action original, Action count)
            {
                Original = original;
                this.count = count;
            }

            public void Run()
            {
                count();
                Original();
            }
        }

        public static DialogAdapter For(Window window)
        {
            Dialog_NodeTree tree = window as Dialog_NodeTree;
            if (tree != null && CurNode != null && Activate != null)
            {
                return NodeTree(tree);
            }
            Dialog_MessageBox box = window as Dialog_MessageBox;
            if (box != null)
            {
                return MessageBox(box);
            }
            FloatMenu menu = window as FloatMenu;
            if (menu != null && MenuOptions != null)
            {
                return Menu(menu);
            }
            Dialog_Slider slider = window as Dialog_Slider;
            if (slider != null && SliderValue != null && SliderConfirm != null)
            {
                return Slider(slider);
            }
            Dialog_GiveName naming = window as Dialog_GiveName;
            if (naming != null && NamingDialogs.Handles(naming))
            {
                return Naming(naming);
            }
            return null;
        }

        // Answered as its OK button does, with the names given typed into its text boxes first.
        private static DialogAdapter Naming(Dialog_GiveName dialog)
        {
            return new DialogAdapter
            {
                Kind = "naming",
                Window = dialog,
                IsOpen = () => Find.WindowStack.IsOpen(dialog),
                PageKey = () => dialog,
                Title = () => null,
                Question = () => Clean(NamingDialogs.Question(dialog)),
                Options = () => new List<DialogOption> { new DialogOption(NamingDialogs.Ok, 0, Clean(NamingDialogs.OkText()), null) },
                NotReady = option => null,
                CanCancel = () => false,
                Choose = option => NamingDialogs.Answer(dialog),
                Cancel = () => { },
                Covered = () => Covers(dialog),
                Callback = option => NamingDialogs.OwnCode(dialog),
                NameFields = () => NamingDialogs.Fields(dialog),
                Names = () => NamingDialogs.Describe(dialog),
                NameRefusal = (field, name) => NamingDialogs.Refusal(dialog, field, name),
                SetName = (field, name) => NamingDialogs.Type(dialog, field, name)
            };
        }

        private static DialogAdapter Slider(Dialog_Slider slider)
        {
            return new DialogAdapter
            {
                Kind = "count",
                Window = slider,
                IsOpen = () => Find.WindowStack.IsOpen(slider),
                PageKey = () => slider,
                Title = () => null,
                Question = () => Clean(slider.textGetter((int)SliderValue.GetValue(slider))),
                Options = () => new List<DialogOption> { new DialogOption("ok", 0, Clean("OK".Translate().ToString()), null) },
                NotReady = option => null,
                CanCancel = () => true,
                Min = slider.from,
                Max = slider.to,
                SetValue = value => SliderValue.SetValue(slider, value),
                Choose = option =>
                {
                    int value = (int)SliderValue.GetValue(slider);
                    slider.Close();
                    ((Action<int>)SliderConfirm.GetValue(slider))(value);
                },
                Cancel = () => slider.Close(),
                Covered = () => Covers(slider),
                Callback = option => SliderConfirm.GetValue(slider) as Delegate
            };
        }

        private static DialogAdapter NodeTree(Dialog_NodeTree dialog)
        {
            Func<DiaNode> node = () => (DiaNode)CurNode.GetValue(dialog);
            Dictionary<DiaOption, Action> originals = new Dictionary<DiaOption, Action>();
            int activations = 0;
            return new DialogAdapter
            {
                Kind = "dialog",
                Window = dialog,
                IsOpen = () => Find.WindowStack.IsOpen(dialog),
                PageKey = () => node(),
                Title = () => Clean(NodeTitle == null ? null : NodeTitle.GetValue(dialog) as string),
                Question = () => Clean(node().text.Resolve()),
                // Info card links only look at something.
                Options = () => node().options
                    .Select((option, index) => new { option, index })
                    .Where(each => each.option != null && each.option.hyperlink.def == null)
                    .Select(each => new DialogOption((each.index + 1).ToString(), each.index, LetterChoices.Text(each.option),
                        each.option.disabled ? (string.IsNullOrEmpty(each.option.disabledReason) ? "Unavailable." : Clean(each.option.disabledReason)) : null))
                    .ToList(),
                NotReady = option => InteractiveAt != null && Time.realtimeSinceStartup < (float)InteractiveAt.GetValue(dialog) ? "The dialog is not taking answers yet; try again in a moment." : null,
                CanCancel = () => dialog.closeOnCancel,
                Choose = option => Invoke(Activate, node().options[option.Index]),
                Cancel = () => dialog.Close(),
                Covered = () => Covers(dialog),
                Callback = option =>
                {
                    DiaOption chosen = node().options[option.Index];
                    Action original;
                    return Unwrapped(originals.TryGetValue(chosen, out original) ? original : chosen.action);
                },
                Activations = () => activations,
                Arm = () =>
                {
                    foreach (DiaOption option in node().options.Where(each => each != null && each.action != null && !originals.ContainsKey(each)).ToList())
                    {
                        Action original = option.action;
                        originals[option] = original;
                        option.action = Wrap(original, () => activations++);
                    }
                }
            };
        }

        private static DialogAdapter MessageBox(Dialog_MessageBox box)
        {
            int activations = 0;
            Dictionary<string, Action> originals = new Dictionary<string, Action>
            {
                { "a", box.buttonAAction },
                { "b", box.buttonBAction },
                { "c", box.buttonCAction }
            };
            Func<Action, Action> counted = original => original == null ? null : Wrap(original, () => activations++);
            box.buttonAAction = counted(box.buttonAAction);
            box.buttonBAction = counted(box.buttonBAction);
            box.buttonCAction = counted(box.buttonCAction);
            box.acceptAction = counted(box.acceptAction);
            box.cancelAction = counted(box.cancelAction);
            return new DialogAdapter
            {
                Kind = "confirmation",
                Window = box,
                IsOpen = () => Find.WindowStack.IsOpen(box),
                PageKey = () => box,
                Title = () => Clean(box.title),
                Question = () => Clean(box.text.Resolve()),
                Options = () =>
                {
                    List<DialogOption> buttons = new List<DialogOption> { new DialogOption("a", 0, Clean(box.buttonAText), null) };
                    if (box.buttonBText != null)
                    {
                        buttons.Add(new DialogOption("b", 1, Clean(box.buttonBText), null));
                    }
                    if (box.buttonCText != null)
                    {
                        buttons.Add(new DialogOption("c", 2, Clean(box.buttonCText), null));
                    }
                    return buttons;
                },
                NotReady = option => option.Id == "a" && DelayExpired != null && !(bool)DelayExpired.GetValue(box, null) ? "That button is still counting down; try again in a moment." : null,
                CanCancel = () => box.cancelAction != null || box.closeOnCancel,
                Choose = option =>
                {
                    Action action = option.Id == "a" ? box.buttonAAction : option.Id == "b" ? box.buttonBAction : box.buttonCAction;
                    if (action != null)
                    {
                        action();
                    }
                    if (option.Id != "c" || box.buttonCClose)
                    {
                        box.Close();
                    }
                },
                Cancel = () =>
                {
                    if (box.cancelAction != null)
                    {
                        box.cancelAction();
                    }
                    box.Close();
                },
                Covered = () => Covers(box),
                Callback = option => Unwrapped(originals[option.Id]),
                Activations = () => activations
            };
        }

        private static DialogAdapter Menu(FloatMenu menu)
        {
            // Otherwise the menu vanishes as soon as the mouse is away from it.
            menu.vanishIfMouseDistant = false;
            Func<List<FloatMenuOption>> options = () => (List<FloatMenuOption>)MenuOptions.GetValue(menu);
            return new DialogAdapter
            {
                Kind = "menu",
                Window = menu,
                IsOpen = () => Find.WindowStack.IsOpen(menu),
                PageKey = () => menu,
                Title = () => Clean(MenuTitle == null ? null : MenuTitle.GetValue(menu) as string),
                Question = () => null,
                Options = () => options().Select((option, index) => new DialogOption((index + 1).ToString(), index, Clean(option.Label), option.Disabled ? "Unavailable." : null)).ToList(),
                NotReady = option => null,
                CanCancel = () => true,
                Choose = option =>
                {
                    options()[option.Index].Chosen(menu.givesColonistOrders, menu);
                    Find.WindowStack.TryRemove(menu);
                },
                Cancel = () => menu.Cancel(),
                Covered = () => Covers(menu),
                Callback = option => options()[option.Index].action
            };
        }

        private static bool Covers(Window window)
        {
            List<Window> windows = Find.WindowStack.Windows.ToList();
            int index = windows.IndexOf(window);
            return index >= 0 && windows.Skip(index + 1).Any(above => above.absorbInputAroundWindow);
        }

        private static void Invoke(MethodInfo method, object target)
        {
            try
            {
                method.Invoke(target, null);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? text : text.StripTags().Trim();
        }
    }

    internal sealed class Interaction
    {
        public const string Open = "open";
        public const string Answered = "answered";
        public const string AnsweredByPlayer = "answeredByPlayer";
        public const string Closed = "closed";
        public const string Cancelled = "cancelled";
        public const string Revoked = "revoked";
        public const string Ended = "ended";

        public string Id;
        public string Tool;
        public string Subject;
        public ActionOrigin Origin;
        public Thing Selected;
        public object Game;
        public DialogAdapter Dialog;
        public int Page;
        public object PageKey;
        public List<DialogOption> Options;
        public int Seen;
        public string State = Open;
        public string Reason;
    }

    // Dialogs agents' calls opened. Every question shown, or shown again with changed options, is a
    // new page whose number is never reused, so an answer naming the page it read can never answer a
    // later question with the same buttons.
    internal static class Interactions
    {
        public const int MaxKept = 32;
        private static readonly List<Interaction> kept = new List<Interaction>();
        // Windows still on screen that an interaction ever had, with why the last one ended, so a
        // dialog left to the player is never taken back as the game's. Only the window and the
        // reason are kept, for the loaded game, while the window is open.
        public const int MaxOwned = 64;
        private static readonly List<OwnedWindow> owned = new List<OwnedWindow>();
        private static int lastId;
        private static int lastPage;

        public static List<Interaction> OpenNow
        {
            get
            {
                Update();
                return kept.Where(interaction => interaction.State == Interaction.Open).ToList();
            }
        }

        public static Interaction Start(string tool, string subject, DialogAdapter dialog, Thing selected = null, ActionOrigin origin = null)
        {
            Interaction interaction = new Interaction { Id = "dialog-" + (++lastId), Tool = tool, Subject = subject, Origin = origin, Game = Current.Game, Dialog = dialog, Selected = selected };
            if (dialog.Window != null)
            {
                owned.RemoveAll(each => each.Window == dialog.Window);
                owned.Add(new OwnedWindow { Window = dialog.Window, Game = Current.Game });
                while (owned.Count > MaxOwned)
                {
                    owned.RemoveAt(0);
                }
            }
            try
            {
                NewPage(interaction);
            }
            catch (Exception ex)
            {
                End(interaction, Interaction.Revoked, Unreadable(ex));
                throw;
            }
            kept.Add(interaction);
            while (kept.Count > MaxKept)
            {
                Interaction finished = kept.FirstOrDefault(each => each.State != Interaction.Open);
                if (finished == null)
                {
                    break;
                }
                kept.Remove(finished);
            }
            return interaction;
        }

        public static Interaction Find(string id)
        {
            return kept.FirstOrDefault(interaction => interaction.Id == id);
        }

        public static bool Owns(Window window)
        {
            return Owning(window) != null;
        }

        public static Interaction Owning(Window window)
        {
            return kept.FirstOrDefault(interaction => interaction.State == Interaction.Open && interaction.Dialog.Window == window);
        }

        // Whether an interaction ever had this window, and why the last one ended: null while one
        // is open, or when the ending gave no reason.
        public static bool EverOwned(Window window, out string reason)
        {
            OwnedWindow entry = window == null ? null : owned.FirstOrDefault(each => each.Window == window && ReferenceEquals(each.Game, Current.Game));
            reason = entry == null ? null : entry.Reason;
            return entry != null;
        }

        // A dialog that cannot be read, such as a mod's in an unexpected state, is left to the player
        // instead of failing every read.
        public static void Update()
        {
            WindowStack stack = Verse.Find.WindowStack;
            owned.RemoveAll(each => !ReferenceEquals(each.Game, Current.Game) || stack != null && !stack.IsOpen(each.Window));
            foreach (Interaction interaction in kept.Where(each => each.State == Interaction.Open).ToList())
            {
                try
                {
                    Observe(interaction);
                }
                catch (Exception ex)
                {
                    End(interaction, Interaction.Revoked, Unreadable(ex));
                }
            }
        }

        public static void Abandon(Interaction interaction, Exception ex)
        {
            if (interaction.State == Interaction.Open)
            {
                End(interaction, Interaction.Revoked, Unreadable(ex));
            }
        }

        public static string Unreadable(Exception ex)
        {
            return "RimMCP could not read the dialog (" + ex.GetType().Name + ": " + ex.Message + "), so it is left to the player.";
        }

        public static void Observe(Interaction interaction)
        {
            if (interaction.State != Interaction.Open)
            {
                return;
            }
            DialogAdapter dialog = interaction.Dialog;
            if (!ReferenceEquals(Current.Game, interaction.Game))
            {
                End(interaction, Interaction.Ended, "Another game was loaded, so the dialog no longer applies.");
            }
            else if (!dialog.IsOpen())
            {
                End(interaction, Interaction.Closed, "The dialog was closed before the agent answered it.");
            }
            else if (!ReferenceEquals(dialog.PageKey(), interaction.PageKey) || dialog.Activations != null && dialog.Activations() != interaction.Seen)
            {
                End(interaction, Interaction.AnsweredByPlayer, "The player answered the dialog.");
            }
            else if (!Same(interaction.Options, dialog.Options()))
            {
                NewPage(interaction);
            }
        }

        public static void Revoke(Func<Interaction, bool> which, string reason)
        {
            foreach (Interaction interaction in kept.Where(each => each.State == Interaction.Open && which(each)).ToList())
            {
                End(interaction, Interaction.Revoked, reason + " The dialog is left to the player.");
            }
        }

        // A mod's press stays a mod's through every answer; otherwise the answer's own code decides.
        public static ActionOrigin OriginOf(Interaction interaction, DialogOption option)
        {
            if (interaction.Origin != null && interaction.Origin.IsMod)
            {
                return interaction.Origin;
            }
            Delegate code = interaction.Dialog.Callback == null ? null : interaction.Dialog.Callback(option);
            ActionOrigin origin = GameActions.Of(new[] { GameActions.OriginType(code) });
            return origin.Type == null ? interaction.Origin ?? new ActionOrigin() : origin;
        }

        // The option returned is the one showing now, checked against the page it was read from.
        // names gives a naming dialog's names by answer_dialog field, such as factionName.
        public static DialogOption Prepare(Interaction interaction, int page, string optionId, int? value, IDictionary<string, string> names, out Action choose)
        {
            Require(interaction, page);
            DialogAdapter dialog = interaction.Dialog;
            if (dialog.SetValue == null && value.HasValue)
            {
                throw new CommandException(400, "This dialog takes no value.");
            }
            if (dialog.SetValue != null && (!value.HasValue || value < dialog.Min || value > dialog.Max))
            {
                throw new CommandException(400, "Give value, a count from " + dialog.Min + " to " + dialog.Max + ".");
            }
            Dictionary<string, string> given = names == null
                ? new Dictionary<string, string>()
                : names.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value);
            List<string> asked = dialog.NameFields == null ? null : dialog.NameFields();
            if (asked == null && given.Count > 0)
            {
                throw new CommandException(400, "This dialog takes no names; " + string.Join(" and ", given.Keys.ToArray()) + " answer a naming dialog.");
            }
            if (asked != null)
            {
                string extra = given.Keys.FirstOrDefault(field => !asked.Contains(field));
                if (extra != null)
                {
                    throw new CommandException(400, "This dialog does not ask for " + extra + "; it asks for " + string.Join(" and ", asked.ToArray()) + ".");
                }
                string missing = asked.FirstOrDefault(field => !given.ContainsKey(field));
                if (missing != null)
                {
                    throw new CommandException(400, "Give " + missing + ": this dialog asks for " + string.Join(" and ", asked.ToArray()) + ".");
                }
            }
            DialogOption read = interaction.Options.FirstOrDefault(option => option.Id == optionId);
            if (read == null)
            {
                throw new CommandException(400, "Page " + page + " has no option '" + optionId + "'; its options are " + string.Join(", ", interaction.Options.Select(option => option.Id).ToArray()) + ".");
            }
            DialogOption now = dialog.Options().FirstOrDefault(option => option.Id == optionId);
            if (!read.SameAs(now))
            {
                NewPage(interaction);
                throw new CommandException(409, "The options on page " + page + " changed since they were read; the dialog is now on page " + interaction.Page + ". Read it again with get_events.");
            }
            if (now.DisabledReason != null)
            {
                throw new CommandException(409, "'" + now.Text + "' is unavailable: " + now.DisabledReason);
            }
            string notReady = dialog.NotReady(now);
            if (notReady != null)
            {
                throw new CommandException(409, notReady);
            }
            // The dialog's own check, as its OK button would make it, before anything is typed in.
            if (asked != null)
            {
                foreach (string field in asked)
                {
                    string refusal = dialog.NameRefusal(field, given[field]);
                    if (refusal != null)
                    {
                        throw new CommandException(409, refusal);
                    }
                }
            }
            if (interaction.Selected != null && !interaction.Selected.Spawned)
            {
                throw new CommandException(409, interaction.Selected.LabelShortCap.StripTags() + ", which this dialog belongs to, is gone.");
            }
            choose = () =>
            {
                if (value.HasValue)
                {
                    dialog.SetValue(value.Value);
                }
                if (asked != null)
                {
                    foreach (string field in asked)
                    {
                        dialog.SetName(field, given[field]);
                    }
                }
                dialog.Choose(now);
            };
            return now;
        }

        // After an answer ran, or failed partway: it was the agent's, not the player's.
        public static void Settle(Interaction interaction)
        {
            DialogAdapter dialog = interaction.Dialog;
            if (dialog.Activations != null)
            {
                interaction.Seen = dialog.Activations();
            }
            if (dialog.IsOpen())
            {
                NewPage(interaction);
            }
            else
            {
                End(interaction, Interaction.Answered, null);
            }
        }

        public static void Cancel(Interaction interaction, int page)
        {
            Require(interaction, page);
            if (!interaction.Dialog.CanCancel())
            {
                throw new CommandException(409, "This dialog cannot be closed without an answer; choose one of its options.");
            }
            interaction.Dialog.Cancel();
            End(interaction, Interaction.Cancelled, null);
        }

        public static object Describe(Interaction interaction)
        {
            bool open = interaction.State == Interaction.Open;
            return Dto.Obj(
                Dto.Field("interactionId", interaction.Id),
                Dto.Field("tool", interaction.Tool),
                Dto.Field("subject", interaction.Subject),
                Dto.Field("kind", interaction.Dialog.Kind),
                Dto.Field("origin", interaction.Origin != null ? interaction.Origin.Mod : null),
                Dto.Field("state", interaction.State),
                Dto.Field("reason", interaction.Reason),
                Dto.Field("page", open ? (object)interaction.Page : null),
                Dto.Field("title", open ? interaction.Dialog.Title() : null),
                Dto.Field("question", open ? interaction.Dialog.Question() : null),
                Dto.Field("options", open ? interaction.Options.Select(option => option.ToDto()).ToArray() : null),
                Dto.Field("valueRange", open && interaction.Dialog.SetValue != null ? new[] { interaction.Dialog.Min, interaction.Dialog.Max } : null),
                Dto.Field("names", open && interaction.Dialog.Names != null ? interaction.Dialog.Names() : null),
                Dto.Field("canCancel", open ? (object)interaction.Dialog.CanCancel() : null));
        }

        internal static void Reset()
        {
            kept.Clear();
        }

        private static void Require(Interaction interaction, int page)
        {
            Observe(interaction);
            if (interaction.State != Interaction.Open)
            {
                throw new CommandException(409, interaction.Reason ?? "The dialog is " + interaction.State + ".");
            }
            if (page != interaction.Page)
            {
                throw new CommandException(409, "Page " + page + " is no longer showing; the dialog is on page " + interaction.Page + ".");
            }
            if (interaction.Dialog.Covered != null && interaction.Dialog.Covered())
            {
                throw new CommandException(409, "Another window is open above this dialog; answer or close that first.");
            }
        }

        private static void NewPage(Interaction interaction)
        {
            DialogAdapter dialog = interaction.Dialog;
            if (dialog.Arm != null)
            {
                dialog.Arm();
            }
            interaction.Page = ++lastPage;
            interaction.PageKey = dialog.PageKey();
            interaction.Options = dialog.Options();
            if (dialog.Activations != null)
            {
                interaction.Seen = dialog.Activations();
            }
        }

        private static bool Same(List<DialogOption> read, List<DialogOption> now)
        {
            return read.Count == now.Count && read.Zip(now, (a, b) => a.Id == b.Id && a.SameAs(b)).All(same => same);
        }

        private static void End(Interaction interaction, string state, string reason)
        {
            interaction.State = state;
            interaction.Reason = reason;
            OwnedWindow entry = interaction.Dialog.Window == null ? null : owned.FirstOrDefault(each => each.Window == interaction.Dialog.Window);
            if (entry != null)
            {
                entry.Reason = reason;
            }
        }

        private sealed class OwnedWindow
        {
            public Window Window;
            public object Game;
            public string Reason;
        }
    }
}
