using RiMCP.Settings;
using RimWorld;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // The bottom bar's assistant button: the icon left of the assistant's name (a setting). It opens
    // or closes the chat window. A gold dot shows an assistant message the player has not seen.
    public sealed class MainButtonWorker_RiMCPChat : MainButtonWorker
    {
        private const float IconSize = 22f;
        private const float IconGap = 4f;
        private static readonly Color Unread = new Color(0.96f, 0.8f, 0.4f);

        public override bool Visible
        {
            get
            {
                RiMCPSettings settings = RiMCPMod.Settings;
                return settings != null && settings.BridgeEnabled && base.Visible;
            }
        }

        public override void DoButton(Rect rect)
        {
            // The vanilla look, hover and sound come from the subtle button; the label and icon are ours.
            Text.Font = GameFont.Small;
            bool clicked = Widgets.ButtonTextSubtle(rect, "", 0f, -1f, SoundDefOf.Mouseover_Category);

            string label = RiMCPMod.Settings == null ? RiMCPSettings.DefaultAssistantName : RiMCPMod.Settings.AssistantLabel;
            float iconSize = Mathf.Min(IconSize, rect.height - 6f);
            float labelWidth = Mathf.Min(Text.CalcSize(label).x, rect.width - iconSize - IconGap - 8f);
            float x = rect.center.x - (iconSize + IconGap + labelWidth) / 2f;
            ChatIcon.Draw(new Rect(x, rect.center.y - iconSize / 2f, iconSize, iconSize));

            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x + iconSize + IconGap, rect.y, labelWidth + 2f, rect.height), label);
            Text.Anchor = anchor;

            if (Window_RiMCPChat.HasUnread)
            {
                Color previous = GUI.color;
                GUI.color = Unread;
                GUI.DrawTexture(new Rect(rect.xMax - 12f, rect.y + 5f, 8f, 8f), BaseContent.WhiteTex);
                GUI.color = previous;
                TooltipHandler.TipRegion(rect, label + ": a new message is waiting.");
            }
            if (clicked)
            {
                InterfaceTryActivate();
            }
        }

        public override void Activate()
        {
            Window_RiMCPChat.Toggle();
        }
    }
}
