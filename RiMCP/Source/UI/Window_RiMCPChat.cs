using System.Collections.Generic;
using RiMCP.Chat;
using RiMCP.Settings;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // The chat with an agent: the history, a text box, and Send. It does not pause the game and can
    // be dragged and resized. Enter sends; Shift+Enter starts a new line.
    internal sealed class Window_RiMCPChat : Window
    {
        private const string InputName = "RiMCPChatInput";
        private const float InputHeight = 56f;
        private const float SendWidth = 80f;
        private const float Gap = 6f;
        private const float Pad = 6f;
        private const float HeaderHeight = 20f;
        private const float TitleHeight = 24f;

        // Night palette: deep indigo for the assistant with a soft gold name, neutral slate for the player.
        private static readonly Color PlayerFill = new Color(0.17f, 0.19f, 0.22f, 0.8f);
        private static readonly Color AssistantFill = new Color(0.14f, 0.13f, 0.33f, 0.85f);
        private static readonly Color PlayerName = new Color(0.72f, 0.76f, 0.82f);
        private static readonly Color AssistantName = new Color(0.96f, 0.82f, 0.48f);

        // The newest entry the player has had in front of them; the bottom bar button shows a dot for newer ones.
        private static int seenId;

        private Vector2 scroll;
        private string input = "";
        private int shownId = -1;
        private bool focused;
        // WindowUpdate runs outside OnGUI, where the IMGUI focus state is not reliable: focus is only
        // read and released inside DoWindowContents, and WindowUpdate works from these two flags.
        private bool inputFocusedLast;
        private bool releaseRequested;
        // Row heights by entry id; entries never change, so a height only changes with the text width.
        private readonly Dictionary<int, float> heightCache = new Dictionary<int, float>();
        private float cachedTextWidth = -1f;

        public Window_RiMCPChat()
        {
            draggable = true;
            resizeable = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            closeOnAccept = false;
            closeOnClickedOutside = false;
            doCloseX = true;
        }

        private static string AssistantLabel
        {
            get { return RiMCPMod.Settings == null ? RiMCPSettings.DefaultAssistantName : RiMCPMod.Settings.AssistantLabel; }
        }

        private static string PlayerLabel
        {
            get { return RiMCPMod.Settings == null ? RiMCPSettings.DefaultPlayerName : RiMCPMod.Settings.PlayerLabel; }
        }

        public static bool HasUnread
        {
            get { return ChatLog.LastAssistantId() > seenId; }
        }

        public static void Toggle()
        {
            Window_RiMCPChat open = Find.WindowStack.WindowOfType<Window_RiMCPChat>();
            if (open != null)
            {
                open.Close();
            }
            else
            {
                Find.WindowStack.Add(new Window_RiMCPChat());
            }
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(460f, 520f); }
        }

        // The camera polls its movement keys (KeyBindingDef.IsDown reads Input.GetKey), so a consumed
        // event cannot stop them; the window flag is the game's own switch for it (CameraDriver
        // skips all camera input while any window sets it). Kept current here and in the GUI pass.
        public override void WindowUpdate()
        {
            base.WindowUpdate();
            // A click on the map never reaches DoWindowContents, so a click outside the window drops
            // the focus here; otherwise the field keeps it and the camera stays frozen.
            if (Input.GetMouseButtonDown(0) && !windowRect.Contains(Verse.UI.MousePositionOnUIInverted))
            {
                releaseRequested = true;
            }
            preventCameraMotion = inputFocusedLast && !releaseRequested;
        }

        public override void PreClose()
        {
            base.PreClose();
            ReleaseFocus();
            releaseRequested = false;
            inputFocusedLast = false;
            preventCameraMotion = false;
        }

        private static void ReleaseFocus()
        {
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
        }

        public override void DoWindowContents(Rect inRect)
        {
            seenId = ChatLog.LastId;
            Text.Font = GameFont.Small;

            // Own title row (icon + name) instead of the window's plain-text title.
            Rect titleRow = new Rect(inRect.x, inRect.y, inRect.width - 30f, TitleHeight);
            ChatIcon.Draw(new Rect(titleRow.x, titleRow.y + 1f, 20f, 20f));
            GUI.color = AssistantName;
            Widgets.Label(new Rect(titleRow.x + 24f, titleRow.y, titleRow.width - 24f, TitleHeight), AssistantLabel);
            GUI.color = Color.white;
            inRect.yMin += TitleHeight + Gap;

            Rect inputRect = new Rect(inRect.x, inRect.yMax - InputHeight, inRect.width - SendWidth - Gap, InputHeight);
            Rect sendRect = new Rect(inputRect.xMax + Gap, inRect.yMax - InputHeight, SendWidth, InputHeight);
            Rect historyRect = new Rect(inRect.x, inRect.y, inRect.width, inputRect.y - Gap - inRect.y);

            DrawHistory(historyRect);

            // While the box has focus the game must not read keys as hotkeys or camera movement.
            Event current = Event.current;
            // A click inside the window but outside the field also drops the focus.
            if (current.type == EventType.MouseDown && !inputRect.Contains(current.mousePosition))
            {
                releaseRequested = true;
            }
            bool inputFocused = GUI.GetNameOfFocusedControl() == InputName;
            if (releaseRequested)
            {
                // Keep releasing until the game reports the field unfocused.
                if (inputFocused)
                {
                    ReleaseFocus();
                }
                else
                {
                    releaseRequested = false;
                }
                inputFocused = false;
            }
            inputFocusedLast = inputFocused;
            preventCameraMotion = inputFocused;
            // Enter is taken before the text area sees it, so it sends instead of adding a line. The
            // newline character Unity sends as its own event is eaten too, or it lands in the cleared box.
            if (inputFocused && current.type == EventType.KeyDown && !current.shift)
            {
                bool enterKey = current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter;
                if (enterKey || current.character == '\n' || current.character == '\r')
                {
                    current.Use();
                    if (enterKey)
                    {
                        Send();
                    }
                }
            }
            GUI.SetNextControlName(InputName);
            input = Widgets.TextArea(inputRect, input);
            if (Widgets.ButtonText(sendRect, "Send"))
            {
                Send();
            }
            if (!focused && current.type == EventType.Repaint)
            {
                GUI.FocusControl(InputName);
                focused = true;
            }
        }

        private void Send()
        {
            string text = input == null ? "" : input.Trim();
            if (text.Length == 0)
            {
                return;
            }
            if (text.Length > ChatService.MaxTextLength)
            {
                text = text.Substring(0, ChatService.MaxTextLength);
            }
            int? tick = Current.Game != null && Find.TickManager != null ? (int?)Find.TickManager.TicksGame : null;
            ChatLog.Add(ChatSender.Player, text, tick);
            input = "";
            GUI.FocusControl(InputName);
        }

        private void DrawHistory(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.25f));
            Rect inner = rect.ContractedBy(2f);
            ChatEntry[] entries = ChatLog.Snapshot();
            float width = inner.width - 16f;
            float textWidth = width - Pad * 2f;

            if (entries.Length == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(inner.ContractedBy(8f), "Nothing yet. Type below; an agent that reads the chat can answer.");
                GUI.color = Color.white;
                return;
            }

            if (!Mathf.Approximately(textWidth, cachedTextWidth))
            {
                heightCache.Clear();
                cachedTextWidth = textWidth;
            }
            if (heightCache.Count > ChatLog.MaxEntries * 2)
            {
                heightCache.Clear();
            }
            float[] heights = new float[entries.Length];
            float total = 0f;
            for (int i = 0; i < entries.Length; i++)
            {
                float height;
                if (!heightCache.TryGetValue(entries[i].Id, out height))
                {
                    height = HeaderHeight + Text.CalcHeight(entries[i].Text, textWidth) + Pad;
                    heightCache[entries[i].Id] = height;
                }
                heights[i] = height;
                total += height + Gap;
            }

            Rect view = new Rect(0f, 0f, width, total);
            // A new entry scrolls to the bottom; otherwise the player's scroll position stays.
            if (entries[entries.Length - 1].Id != shownId)
            {
                scroll.y = total;
                shownId = entries[entries.Length - 1].Id;
            }
            Widgets.BeginScrollView(inner, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < entries.Length; i++)
            {
                // Only rows inside the scroll view are drawn.
                if (y + heights[i] >= scroll.y && y <= scroll.y + inner.height)
                {
                    DrawEntry(new Rect(0f, y, width, heights[i]), entries[i]);
                }
                y += heights[i] + Gap;
            }
            Widgets.EndScrollView();
        }

        private static void DrawEntry(Rect rect, ChatEntry entry)
        {
            bool player = entry.From == ChatSender.Player;
            Widgets.DrawBoxSolid(rect, player ? PlayerFill : AssistantFill);
            Rect header = new Rect(rect.x + Pad, rect.y + 2f, rect.width - Pad * 2f, HeaderHeight);
            GUI.color = player ? PlayerName : AssistantName;
            Widgets.Label(header, player ? PlayerLabel : AssistantLabel);
            GUI.color = Color.white;
            Rect body = new Rect(rect.x + Pad, rect.y + HeaderHeight, rect.width - Pad * 2f, rect.height - HeaderHeight);
            Widgets.Label(body, entry.Text);
        }
    }
}
