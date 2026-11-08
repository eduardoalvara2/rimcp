using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    // Windows on screen that no agent's call opened, such as a scenario's intro or a mod's notice,
    // which pause the game until someone closes them. Only a window the game opened on its own is
    // the agents' to handle: those the dialog adapters read become interactions, answered with
    // answer_dialog under dismiss_dialog's access, so only access that allows decisions answers the
    // game's questions, and the others can be closed with dismiss_dialog where they have a close
    // control. Every other window that pauses the game is listed as the player's. Main thread only.
    internal static class DialogService
    {
        // The tool game dialogs are answered under.
        public const string Tool = "dismiss_dialog";
        private const int MaxKept = 32;
        private static readonly List<KeptWindow> kept = new List<KeptWindow>();
        private static int lastId;

        private static readonly FieldInfo SettingsMod = typeof(Dialog_ModSettings).GetField("mod", BindingFlags.Instance | BindingFlags.NonPublic);

        // Windows the player opens to look at or change something, and, when they pause the game,
        // everything opened over them (such as a save list's delete confirmation): never the
        // agents' to answer or close. The inspect pane, open whenever something is selected, is not
        // one of them.
        private static readonly Type[] PlayerWindows =
        {
            typeof(Dialog_FileList), typeof(Dialog_Options), typeof(Dialog_ModSettings), typeof(MainTabWindow), typeof(Page),
            typeof(Dialog_Trade), typeof(Dialog_FormCaravan), typeof(Dialog_BillConfig)
        };

        public static BridgeResponse ListDialogs(BridgeRequest request, RouteMatch route)
        {
            return ReadEnvelope.Ok(ReadContext.From(request), Summary());
        }

        public static BridgeResponse DismissDialog(BridgeRequest request, RouteMatch route)
        {
            DismissDialogBody body = CommandUtil.ReadBody<DismissDialogBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            string windowId = route["windowId"];
            RequestKey key = RequestKey.For("dismiss_dialog", requestId, null, windowId);
            return CommandReceipts.Once(CommandUtil.ContextFor(null), key, () => Dismiss(windowId));
        }

        // The game's own windows that dismiss_dialog may close, checked in its code to only show
        // something. Mods' windows, such as update notices, may be closed too.
        private static readonly Type[] DismissableGameWindows = { };

        // Windows that hold the game paused, RimMCP's own included.
        public static List<Window> Pausing()
        {
            WindowStack stack = Find.WindowStack;
            return stack == null ? new List<Window>() : stack.Windows.Where(window => window.forcePause).ToList();
        }

        // As get_advance names them: RimMCP's own, which list_dialogs leaves out, marked.
        public static string PausingName(Window window)
        {
            return (IsOwn(window) ? "RimMCP:" : "") + window.GetType().Name;
        }

        // RimMCP's own windows: the notice shown while a save holds the game, and its settings.
        public static bool IsOwn(Window window)
        {
            string space = window.GetType().Namespace;
            if (space != null && (space == "RiMCP" || space.StartsWith("RiMCP.", StringComparison.Ordinal)))
            {
                return true;
            }
            return window is Dialog_ModSettings && SettingsMod != null && SettingsMod.GetValue(window) is RiMCPMod;
        }

        // Why a window is the player's to finish, or null when the game opened it on its own.
        // below is every window under it on the stack.
        internal static string PlayerOnly(Window window, IEnumerable<Window> below)
        {
            string left;
            if (Interactions.Owning(window) == null && Interactions.EverOwned(window, out left))
            {
                return left ?? "An agent's call opened it, and it was left to the player.";
            }
            string own = PlayerWindow(window);
            if (own != null)
            {
                return own;
            }
            if (WindowOrigins.ByPlayer(window))
            {
                return "It was opened by the player: it appeared just after they clicked or pressed a key.";
            }
            if (window is Dialog_Negotiation)
            {
                return "It is a call on the comms console, which the player makes.";
            }
            Dialog_MessageBox box = window as Dialog_MessageBox;
            if (box != null && box.buttonADestructive)
            {
                return "It confirms something that cannot be undone, such as deleting a file or quitting the game.";
            }
            // The game opens nothing on its own over a window of the player's that pauses it.
            Window under = below.LastOrDefault(each => each.forcePause && PlayerWindow(each) != null);
            return under == null ? null : "It was opened over the player's " + under.GetType().Name + " window.";
        }

        // The windowId check, the game-opened check, and the close control, as dismiss_dialog
        // applies them; null when it may close the window.
        internal static string DismissRefusal(Window window, IEnumerable<Window> below)
        {
            string player = PlayerOnly(window, below);
            if (player != null)
            {
                return "This window is the player's to finish: " + player;
            }
            if (!window.forcePause)
            {
                return "This window does not pause the game; it is left to the player.";
            }
            // The game's own windows may hold the player's edits; only those checked to show
            // something are closed.
            Type type = window.GetType();
            if (GameActions.IsGame(type.Assembly) && !DismissableGameWindows.Contains(type))
            {
                return "It is one of the game's own windows, which may hold the player's changes; the player closes it.";
            }
            if (window.doCloseX)
            {
                return null;
            }
            if (!window.closeOnCancel)
            {
                return "This window has no close control; the player must finish it in game.";
            }
            // Escape runs the window's own cancel code, which needs the game's input to run, so a
            // window with its own is left to the player.
            MethodInfo cancel = window.GetType().GetMethod("OnCancelKeyPressed", BindingFlags.Instance | BindingFlags.Public);
            return cancel != null && cancel.DeclaringType != typeof(Window)
                ? "This window's Escape key does more than close it; the player must close it in game."
                : null;
        }

        // Why advance_time cannot run past this window, as list_dialogs lists it.
        public static string HoldingMessage(Window window)
        {
            WindowOrigins.Update();
            List<Window> windows = Find.WindowStack.Windows.ToList();
            string name = window.GetType().Name;
            Operation asking = AskingOperation();
            if (asking != null)
            {
                return "The game is paused by a " + name + " window, the game's question for operation " + asking.Id + " (" + asking.Tool + "); see get_operation.";
            }
            string player = PlayerOnly(window, windows.Take(Math.Max(0, windows.IndexOf(window))));
            if (player != null)
            {
                return "The game is paused by a " + name + " window, which the player must finish: " + player + " list_dialogs lists it.";
            }
            return "The game is paused by a " + name + " window. list_dialogs shows it: answer it with answer_dialog, or close it with dismiss_dialog where it has a close control.";
        }

        internal static Dictionary<string, object> Summary()
        {
            WindowStack stack = Find.WindowStack;
            Interactions.Update();
            WindowOrigins.Update();
            kept.RemoveAll(each => !each.Dismissed && (stack == null || !stack.IsOpen(each.Window)));
            List<Window> windows = stack == null ? new List<Window>() : stack.Windows.ToList();
            List<object> dialogs = new List<object>();
            List<object> unsupported = new List<object>();
            bool blocksTime = false;
            Operation asking = AskingOperation();
            for (int index = 0; index < windows.Count; index++)
            {
                Window window = windows[index];
                Interaction interaction = Interactions.Owning(window);
                if (interaction == null && !Listed(window))
                {
                    continue;
                }
                blocksTime |= window.forcePause;
                List<Window> below = windows.Take(index).ToList();
                try
                {
                    object dialog = Dialog(window, below, interaction, asking);
                    if (dialog != null)
                    {
                        dialogs.Add(dialog);
                        continue;
                    }
                    string player = asking == null ? PlayerOnly(window, below) : null;
                    unsupported.Add(Unsupported(window, Dto.Field("canDismiss", asking == null && DismissRefusal(window, below) == null),
                        Dto.Field("heldBy", asking == null ? null : asking.Id + " (" + asking.Tool + ")"),
                        Dto.Field("reason", player)));
                }
                catch (Exception ex)
                {
                    unsupported.Add(Unsupported(window, Dto.Field("canDismiss", false), Dto.Field("error", Interactions.Unreadable(ex))));
                }
            }
            return Dto.Obj(
                Dto.Field("dialogs", dialogs.ToArray()),
                Dto.Field("unsupported", unsupported.ToArray()),
                Dto.Field("blocksTime", blocksTime));
        }

        // The window as an interaction: one an agent's call opened, or one the game opened that the
        // adapters read. Null otherwise. A dialog that cannot be read is left to the player.
        private static object Dialog(Window window, List<Window> below, Interaction interaction, Operation asking)
        {
            if (interaction == null)
            {
                // The game's question to the player during an operation is answered with
                // answer_confirmation or in game, never as an ordinary dialog.
                if (asking != null || PlayerOnly(window, below) != null)
                {
                    return null;
                }
                DialogAdapter adapter = DialogAdapter.For(window);
                if (adapter == null)
                {
                    return null;
                }
                interaction = Interactions.Start(Tool, window.GetType().Name, adapter);
            }
            try
            {
                return Describe(interaction, window);
            }
            catch (Exception ex)
            {
                Interactions.Abandon(interaction, ex);
                throw;
            }
        }

        private static Dictionary<string, object> Unsupported(Window window, params Dto.JsonField[] fields)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("windowId", Keep(window)),
                Dto.Field("type", window.GetType().Name),
                Dto.Field("title", string.IsNullOrEmpty(window.optionalTitle) ? null : window.optionalTitle.StripTags()));
            foreach (Dto.JsonField field in fields.Where(each => each.Value != null))
            {
                dto[field.Name] = field.Value;
            }
            dto["blocksTime"] = window.forcePause;
            return dto;
        }

        private static CommandResult Dismiss(string windowId)
        {
            if (Interactions.Find(windowId) != null)
            {
                throw new CommandException(409, windowId + " is a dialog agents can answer; answer it with answer_dialog (an option, or cancel: true).");
            }
            KeptWindow entry = kept.FirstOrDefault(each => each.Id == windowId);
            if (entry == null)
            {
                throw new CommandException(404, "No window '" + windowId + "'. list_dialogs lists the windows that can be dismissed.");
            }
            string type = entry.Window.GetType().Name;
            if (entry.Dismissed)
            {
                return new CommandResult(false, Dto.Obj(
                    Dto.Field("windowId", windowId),
                    Dto.Field("type", type),
                    Dto.Field("closed", true),
                    Dto.Field("alreadySatisfied", true),
                    Dto.Field("remaining", Summary())));
            }
            Window window = entry.Window;
            if (Find.WindowStack == null || !Find.WindowStack.IsOpen(window))
            {
                kept.Remove(entry);
                throw new CommandException(409, "The " + type + " window is already closed.");
            }
            Operation asking = AskingOperation();
            if (asking != null)
            {
                throw new CommandException(409, "Operation " + asking.Id + " (" + asking.Tool + ") is waiting on the game's question; see get_operation.");
            }
            WindowOrigins.Update();
            List<Window> windows = Find.WindowStack.Windows.ToList();
            string refusal = DismissRefusal(window, windows.Take(Math.Max(0, windows.IndexOf(window))));
            if (refusal != null)
            {
                throw new CommandException(409, refusal);
            }
            // As its close button does, or Escape for a window whose Escape only closes it.
            window.Close(false);
            if (Find.WindowStack.IsOpen(window))
            {
                Find.WindowStack.TryRemove(window, false);
            }
            if (Find.WindowStack.IsOpen(window))
            {
                throw new CommandException(409, "The " + type + " window did not close.");
            }
            entry.Dismissed = true;
            return new CommandResult(true, Dto.Obj(
                Dto.Field("windowId", windowId),
                Dto.Field("type", type),
                Dto.Field("closed", true),
                Dto.Field("remaining", Summary())));
        }

        // Every window that pauses the game, and every dialog, except RimMCP's own. A right-click
        // menu, a main tab, or the setup screens appear only when they pause it.
        private static bool Listed(Window window)
        {
            return !IsOwn(window) && (window.forcePause || IsDialog(window.GetType()));
        }

        private static string PlayerWindow(Window window)
        {
            if (window is MainTabWindow_Inspect || !PlayerWindows.Any(type => type.IsInstanceOfType(window)))
            {
                return null;
            }
            return window is Page ? "It is a new game's setup screen; get_setup and the setup tools drive it." : "It is the player's own " + window.GetType().Name + " window.";
        }

        private static bool IsDialog(Type type)
        {
            for (Type each = type; each != null && each != typeof(Window); each = each.BaseType)
            {
                if (each.Name.StartsWith("Dialog_", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static Operation AskingOperation()
        {
            Operation active = OperationRunner.Active;
            return active != null && active.State == Operation.AwaitingConfirmation ? active : null;
        }

        private static object Describe(Interaction interaction, Window window)
        {
            Dictionary<string, object> dto = (Dictionary<string, object>)Interactions.Describe(interaction);
            // Describe's origin names the mod behind a mod's dialog.
            object mod;
            if (dto.TryGetValue("origin", out mod))
            {
                dto.Remove("origin");
                dto["mod"] = mod;
            }
            dto["origin"] = interaction.Tool == Tool ? "game" : "agent";
            dto["blocksTime"] = window.forcePause;
            return dto;
        }

        // Windows a dismiss closed are kept, so a repeat answers alreadySatisfied; they go first
        // when the list is full.
        private static string Keep(Window window)
        {
            KeptWindow entry = kept.FirstOrDefault(each => each.Window == window && !each.Dismissed);
            if (entry != null)
            {
                return entry.Id;
            }
            entry = new KeptWindow { Id = "window-" + (++lastId), Window = window };
            kept.Add(entry);
            while (kept.Count > MaxKept)
            {
                KeptWindow dismissed = kept.FirstOrDefault(each => each.Dismissed);
                kept.Remove(dismissed ?? kept[0]);
            }
            return entry.Id;
        }

        internal static void Reset()
        {
            kept.Clear();
        }

        private sealed class KeptWindow
        {
            public string Id;
            public Window Window;
            public bool Dismissed;
        }

        [DataContract]
        private sealed class DismissDialogBody
        {
            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }

    // When each window on screen first appeared, and whether the player had just clicked or pressed
    // a key: a window that appears right after the player's input is the player's, whatever it is,
    // such as a psycast's confirmation. Input reaches RimMCP only in its OnGUI, so it is noted there,
    // and windows are looked at every frame before requests run. Main thread only.
    internal static class WindowOrigins
    {
        // A click opens its window in the same frame's events, seen the next frame; a few more
        // frames cover windows the game opens a moment later.
        public const int InputFrames = 5;
        private const int MaxTracked = 64;
        private static readonly List<Seen> seen = new List<Seen>();
        private static int lastInputFrame = int.MinValue / 2;

        public static void NoteInput(Event current)
        {
            if (current != null && (current.type == EventType.MouseDown || current.type == EventType.MouseUp || current.type == EventType.KeyDown))
            {
                NoteInput(Frame());
            }
        }

        internal static void NoteInput(int frame)
        {
            lastInputFrame = frame;
        }

        public static void Update()
        {
            WindowStack stack = Find.WindowStack;
            if (stack != null)
            {
                Observe(stack.Windows, Frame());
            }
        }

        // Apart and never inlined, so only a running game reads the engine's frame count.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Frame()
        {
            return Time.frameCount;
        }

        internal static void Observe(IList<Window> windows, int frame)
        {
            seen.RemoveAll(each => !windows.Contains(each.Window));
            foreach (Window window in windows)
            {
                if (!seen.Any(each => each.Window == window))
                {
                    seen.Add(new Seen { Window = window, ByPlayer = frame - lastInputFrame <= InputFrames });
                }
            }
            while (seen.Count > MaxTracked)
            {
                seen.RemoveAt(0);
            }
        }

        public static bool ByPlayer(Window window)
        {
            Seen entry = seen.FirstOrDefault(each => each.Window == window);
            return entry != null && entry.ByPlayer;
        }

        internal static void Reset()
        {
            seen.Clear();
            lastInputFrame = int.MinValue / 2;
        }

        private sealed class Seen
        {
            public Window Window;
            public bool ByPlayer;
        }
    }
}
