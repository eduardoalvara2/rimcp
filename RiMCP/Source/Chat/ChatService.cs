using System;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Read;
using Verse;

namespace RiMCP.Chat
{
    // The chat tools: an agent says something to the player in game, and reads what the player typed.
    internal static class ChatService
    {
        public const int MaxTextLength = 4000;
        public const int MaxWaitSeconds = 50;
        private const int MaxToastLength = 200;
        private const string LetterLabel = "Claude";

        public static BridgeResponse SendMessage(BridgeRequest request, RouteMatch route)
        {
            SendMessageBody body = CommandUtil.ReadBody<SendMessageBody>(request);
            string text = body.Text == null ? "" : body.Text.Trim();
            if (text.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'text'.");
            }
            if (text.Length > MaxTextLength)
            {
                throw new CommandException(400, "Field 'text' must be at most " + MaxTextLength + " characters.");
            }

            bool playing = Current.ProgramState == ProgramState.Playing && Current.Game != null;
            ChatEntry entry = ChatLog.Add(ChatSender.Assistant, text, playing && Find.TickManager != null ? (int?)Find.TickManager.TicksGame : null);
            if (playing)
            {
                if (body.Letter == true)
                {
                    Find.LetterStack.ReceiveLetter(LetterLabel, text, LetterDefOf.NeutralEvent);
                }
                else
                {
                    Messages.Message(Toast(text), MessageTypeDefOf.NeutralEvent, false);
                }
            }
            return CommandEnvelope.Ok(new CommandContext(null), true, Dto.Obj(Dto.Field("id", entry.Id)));
        }

        public static BridgeResponse GetChat(BridgeRequest request, RouteMatch route)
        {
            ReadRequest query = ReadRequest.FromUri(request.Uri);
            int after = query.OptionalInt("after") ?? 0;
            int wait = query.OptionalInt("waitSeconds") ?? 0;
            if (after < 0)
            {
                throw new BridgeException(400, "Field 'after' must be 0 or more.");
            }
            if (wait < 0 || wait > MaxWaitSeconds)
            {
                throw new BridgeException(400, "Field 'waitSeconds' must be from 0 to " + MaxWaitSeconds + ".");
            }
            if (wait == 0)
            {
                return Reply(after);
            }
            // The bridge waits off the main thread, so the game keeps running, then asks for the
            // reply again: it holds whatever arrived meanwhile.
            // A waiter nobody releases (the client left) is purged once it expires, by the next Wait or Add.
            ChatWaiter waiter = ChatLog.Wait(after, wait);
            bool deferred = false;
            try
            {
                BridgeResponse response = Reply(after);
                if (!waiter.Done.IsSet)
                {
                    response.Deferred = new DeferredReply(waiter.Done, wait, () =>
                    {
                        ChatLog.Release(waiter);
                        return Reply(after);
                    });
                    deferred = true;
                }
                return response;
            }
            finally
            {
                if (!deferred)
                {
                    ChatLog.Release(waiter);
                }
            }
        }

        private static BridgeResponse Reply(int after)
        {
            ReadContext context = new ReadContext(new ReadRequest(), null);
            int last;
            bool reset;
            ChatEntry[] entries = ChatLog.Read(after, out last, out reset);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("messages", entries.Select(entry => (object)entry.ToDto()).ToArray()),
                Dto.Field("lastId", last),
                Dto.Field("logId", ChatLog.LogId),
                Dto.Field("reset", reset)));
        }

        // The toast is a glance; the chat window and the letter keep the whole text.
        internal static string Toast(string text)
        {
            return text.Length <= MaxToastLength ? text : text.Substring(0, MaxToastLength).TrimEnd() + "...";
        }

        [DataContract]
        private sealed class SendMessageBody
        {
            [DataMember(Name = "text")]
            public string Text { get; set; }

            [DataMember(Name = "letter")]
            public bool? Letter { get; set; }
        }
    }
}
