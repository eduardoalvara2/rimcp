using System.Collections.Generic;
using RiMCP.Chat;
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

        private static readonly Color PlayerFill = new Color(0.18f, 0.28f, 0.42f, 0.55f);
        private static readonly Color AssistantFill = new Color(0.25f, 0.25f, 0.25f, 0.55f);
        private static readonly Color PlayerName = new Color(0.55f, 0.75f, 1f);
        private static readonly Color AssistantName = new Color(0.95f, 0.7f, 0.45f);

        // The newest entry the player has had in front of them; the bottom bar button shows a dot for newer ones.
        private static int seenId;

        private Vector2 scroll;
        private string input = "";
        private int shownId = -1;
        private bool focused;

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
            optionalTitle = "Claude";
        }

        public static bool HasUnread
        {
            get { return ChatLog.LastId > seenId && System.Array.Exists(ChatLog.After(seenId), entry => entry.From == ChatSender.Assistant); }
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

        public override void DoWindowContents(Rect inRect)
        {
            seenId = ChatLog.LastId;
            Text.Font = GameFont.Small;

            Rect inputRect = new Rect(inRect.x, inRect.yMax - InputHeight, inRect.width - SendWidth - Gap, InputHeight);
            Rect sendRect = new Rect(inputRect.xMax + Gap, inRect.yMax - InputHeight, SendWidth, InputHeight);
            Rect historyRect = new Rect(inRect.x, inRect.y, inRect.width, inputRect.y - Gap - inRect.y);

            DrawHistory(historyRect);

            // Enter is taken before the text area sees it, so it sends instead of adding a line.
            Event current = Event.current;
            if (current.type == EventType.KeyDown && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                && !current.shift && GUI.GetNameOfFocusedControl() == InputName)
            {
                current.Use();
                Send();
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

            List<float> heights = new List<float>(entries.Length);
            float total = 0f;
            foreach (ChatEntry entry in entries)
            {
                float height = HeaderHeight + Text.CalcHeight(entry.Text, textWidth) + Pad;
                heights.Add(height);
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
                DrawEntry(new Rect(0f, y, width, heights[i]), entries[i]);
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
            Widgets.Label(header, player ? "You" : "Claude");
            GUI.color = Color.white;
            Rect body = new Rect(rect.x + Pad, rect.y + HeaderHeight, rect.width - Pad * 2f, rect.height - HeaderHeight);
            Widgets.Label(body, entry.Text);
        }
    }
}
