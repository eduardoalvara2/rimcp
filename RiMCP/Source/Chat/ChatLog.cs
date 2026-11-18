using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace RiMCP.Chat
{
    internal static class ChatSender
    {
        public const string Player = "player";
        public const string Assistant = "assistant";
    }

    internal sealed class ChatEntry
    {
        public readonly int Id;
        public readonly string From;
        public readonly string Text;
        public readonly DateTime AtUtc;
        // Game ticks when the entry was made; null when no game was loaded.
        public readonly int? Tick;

        public ChatEntry(int id, string from, string text, DateTime atUtc, int? tick)
        {
            Id = id;
            From = from;
            Text = text;
            AtUtc = atUtc;
            Tick = tick;
        }

        public Dictionary<string, object> ToDto()
        {
            Dictionary<string, object> dto = new Dictionary<string, object>();
            dto["id"] = Id;
            dto["from"] = From;
            dto["text"] = Text;
            dto["at"] = AtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            dto["tick"] = Tick.HasValue ? (object)Tick.Value : null;
            return dto;
        }
    }

    // A wait for a player entry newer than After. Done is set when one arrives.
    internal sealed class ChatWaiter
    {
        public readonly int After;
        public readonly DateTime ExpiresUtc;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

        public ChatWaiter(int after, DateTime expiresUtc)
        {
            After = after;
            ExpiresUtc = expiresUtc;
        }
    }

    // The chat between the player and an agent. It lives for the whole process, so it survives
    // loading another save, and is read by the bridge's threads while the main thread writes it.
    internal static class ChatLog
    {
        public const int MaxEntries = 300;

        private static readonly object Gate = new object();
        private static readonly List<ChatEntry> Entries = new List<ChatEntry>();
        private static readonly List<ChatWaiter> Waiters = new List<ChatWaiter>();
        private static int lastId;
        // The newest player entry a waiting reader was handed, and when: the window shows the
        // assistant thinking about it until an assistant entry follows.
        private static int deliveredId;
        private static DateTime deliveredAtUtc;

        // Names this process's log. An id from another run (an agent's saved cursor) means nothing here.
        public static readonly string LogId = Guid.NewGuid().ToString("N");

        public static int LastId
        {
            get
            {
                lock (Gate)
                {
                    return lastId;
                }
            }
        }

        public static ChatEntry Add(string from, string text, int? tick)
        {
            lock (Gate)
            {
                ChatEntry entry = new ChatEntry(++lastId, from, text, DateTime.UtcNow, tick);
                Entries.Add(entry);
                DateTime now = DateTime.UtcNow;
                Waiters.RemoveAll(old => old.ExpiresUtc < now);
                if (Entries.Count > MaxEntries)
                {
                    Entries.RemoveRange(0, Entries.Count - MaxEntries);
                }
                if (from == ChatSender.Player)
                {
                    foreach (ChatWaiter waiter in Waiters.Where(w => entry.Id > w.After))
                    {
                        waiter.Done.Set();
                    }
                }
                return entry;
            }
        }

        // What a reader holding cursor `after` should see, taken in one step. A cursor past the last id
        // comes from a previous process: the whole log is returned and Reset is true.
        public static ChatEntry[] Read(int after, out int last, out bool reset)
        {
            lock (Gate)
            {
                last = lastId;
                reset = after > lastId;
                return reset ? Entries.ToArray() : Entries.Where(entry => entry.Id > after).ToArray();
            }
        }

        // The id of the newest assistant entry, 0 when there is none. Allocates nothing.
        public static int LastAssistantId()
        {
            lock (Gate)
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    if (Entries[i].From == ChatSender.Assistant)
                    {
                        return Entries[i].Id;
                    }
                }
                return 0;
            }
        }

        // The player's newest entry when no assistant entry follows it, else 0. Allocates nothing.
        public static int PendingPlayerId()
        {
            lock (Gate)
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    if (Entries[i].From == ChatSender.Assistant)
                    {
                        return 0;
                    }
                    if (Entries[i].From == ChatSender.Player)
                    {
                        return Entries[i].Id;
                    }
                }
                return 0;
            }
        }

        // Records that a reader took the player entries up to id.
        public static void MarkDelivered(int id)
        {
            lock (Gate)
            {
                if (id > deliveredId)
                {
                    deliveredId = id;
                    deliveredAtUtc = DateTime.UtcNow;
                }
            }
        }

        // True when a reader took the player entry id; atUtc is when the newest delivery happened.
        public static bool IsDelivered(int id, out DateTime atUtc)
        {
            lock (Gate)
            {
                atUtc = deliveredAtUtc;
                return id > 0 && deliveredId >= id;
            }
        }

        // The newest entries, oldest first, for the window.
        public static ChatEntry[] Snapshot()
        {
            lock (Gate)
            {
                return Entries.ToArray();
            }
        }

        // A waiter for a player entry newer than id; already done when one exists. Release it when
        // finished; one nobody released is dropped a few seconds after its wait would have ended.
        public static ChatWaiter Wait(int id, double seconds)
        {
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                Waiters.RemoveAll(old => old.ExpiresUtc < now);
                ChatWaiter waiter = new ChatWaiter(id, now.AddSeconds(seconds + 10));
                if (id > lastId || Entries.Any(entry => entry.From == ChatSender.Player && entry.Id > id))
                {
                    waiter.Done.Set();
                }
                else
                {
                    Waiters.Add(waiter);
                }
                return waiter;
            }
        }

        public static void Release(ChatWaiter waiter)
        {
            lock (Gate)
            {
                Waiters.Remove(waiter);
            }
        }

        // For tests.
        internal static void Reset()
        {
            lock (Gate)
            {
                Entries.Clear();
                foreach (ChatWaiter waiter in Waiters)
                {
                    waiter.Done.Set();
                }
                Waiters.Clear();
                lastId = 0;
                deliveredId = 0;
                deliveredAtUtc = default(DateTime);
            }
        }
    }
}
