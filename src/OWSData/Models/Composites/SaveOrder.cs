using System;
using System.Threading;

namespace OWSData.Models.Composites
{
    /// <summary>
    /// Where a stats, quests or abilities save falls in its owner token's save order (issue #247). The character row
    /// lock orders the writes but not the snapshots, so each key keeps the order of the save that last wrote it and an
    /// earlier save that reaches the lock late leaves it alone. See PostgresQueries.SaveOrderSet.
    /// </summary>
    public sealed class SaveOrder
    {
        public SaveOrder(long? sequence, DateTime receivedAt)
        {
            Sequence = sequence > 0 ? sequence : null;
            ReceivedAt = receivedAt;
        }

        /// <summary>
        /// The caller's own number for the snapshot, taken when it was built and reused on a retry. Null when the caller
        /// sends none (or 0). Between two saves of the same token that both carry one, it decides the order.
        /// </summary>
        public long? Sequence { get; }

        /// <summary>
        /// When this service received the save, from <see cref="SaveClock"/>. Orders the saves of a token that does not
        /// send <see cref="Sequence"/>: an earlier request held up on its way to the character row lock (connection pool,
        /// owner check, lock wait) cannot overwrite a later one that got there first.
        /// </summary>
        public DateTime ReceivedAt { get; }

        /// <summary>A save stamped now, for callers that have no earlier receipt time.</summary>
        public static SaveOrder Now(long? sequence = null) => new SaveOrder(sequence, SaveClock.Next());
    }

    /// <summary>
    /// Receipt stamps for <see cref="SaveOrder"/>: UTC, whole microseconds (what a Postgres timestamptz keeps), and
    /// strictly increasing within the process even if the system clock steps back. CharacterPersistence runs as one
    /// process, so stamps from different requests compare.
    /// </summary>
    public static class SaveClock
    {
        private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        private static long _last;

        public static DateTime Next()
        {
            while (true)
            {
                long last = Interlocked.Read(ref _last);
                long now = DateTime.UtcNow.Ticks / TicksPerMicrosecond * TicksPerMicrosecond;
                long next = Math.Max(now, last + TicksPerMicrosecond);
                if (Interlocked.CompareExchange(ref _last, next, last) == last)
                {
                    return new DateTime(next, DateTimeKind.Utc);
                }
            }
        }
    }
}
