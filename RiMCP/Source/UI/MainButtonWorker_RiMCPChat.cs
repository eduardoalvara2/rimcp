using RiMCP.Settings;
using RimWorld;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // The bottom bar's Claude button: opens or closes the chat window. A dot shows an assistant
    // message the player has not seen.
    public sealed class MainButtonWorker_RiMCPChat : MainButtonWorker
    {
        private static readonly Color Unread = new Color(0.95f, 0.75f, 0.2f);

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
            base.DoButton(rect);
            if (Window_RiMCPChat.HasUnread)
            {
                Color previous = GUI.color;
                GUI.color = Unread;
                GUI.DrawTexture(new Rect(rect.xMax - 12f, rect.y + 5f, 8f, 8f), BaseContent.WhiteTex);
                GUI.color = previous;
                TooltipHandler.TipRegion(rect, "Claude: a new message is waiting.");
            }
        }

        public override void Activate()
        {
            Window_RiMCPChat.Toggle();
        }
    }
}
