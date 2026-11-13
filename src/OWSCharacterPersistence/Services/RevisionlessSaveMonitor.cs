using System;
using Serilog;

namespace OWSCharacterPersistence.Services
{
    /// <summary>
    /// Counts bag and wallet saves that arrive without ExpectedRevision. With
    /// EconomyOptions.RequireExpectedRevision off, each one is written last-write-wins and can put back items
    /// or gold a shop transaction just took (docs/mvp-hardening-spec.md item 2). The flag can only go on once
    /// the zone server stops sending them, so this makes that visible: the first one logs at once, later ones
    /// as one summary line per interval instead of one line per autosave.
    /// </summary>
    public sealed class RevisionlessSaveMonitor
    {
        public const string Inventory = "inventory";
        public const string Currency = "currency";
        public const string CharacterData = "characterData";

        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

        // Used by the API. Tests build their own so counts do not leak between them.
        public static readonly RevisionlessSaveMonitor Shared = new RevisionlessSaveMonitor();

        private readonly ILogger logger;
        private readonly Func<DateTime> utcNow;
        private readonly TimeSpan interval;
        private readonly object gate = new object();

        private DateTime? lastReportUtc;
        private long total;
        private long inventory;
        private long currency;
        private long characterData;
        private long refused;

        // logger null = the process-wide Serilog logger, resolved when a line is written so the one Program
        // configures is used rather than whatever was current when this was built.
        public RevisionlessSaveMonitor(ILogger logger = null, Func<DateTime> utcNow = null, TimeSpan? interval = null)
        {
            this.logger = logger;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.interval = interval ?? DefaultInterval;
        }

        // Every revision-less save since the process started.
        public long Total
        {
            get { lock (gate) { return total; } }
        }

        // save is one of Inventory, Currency or CharacterData. refused = RequireExpectedRevision was on, so the
        // save was answered revision_required instead of written.
        public void Record(string save, bool refused)
        {
            long count, inventoryCount, currencyCount, characterDataCount, refusedCount;
            double windowSeconds;
            lock (gate)
            {
                total++;
                switch (save)
                {
                    case Inventory: inventory++; break;
                    case Currency: currency++; break;
                    default: characterData++; break;
                }
                if (refused) this.refused++;

                DateTime now = utcNow();
                if (lastReportUtc.HasValue && now - lastReportUtc.Value < interval)
                {
                    return;
                }

                windowSeconds = lastReportUtc.HasValue ? (now - lastReportUtc.Value).TotalSeconds : 0;
                inventoryCount = inventory;
                currencyCount = currency;
                characterDataCount = characterData;
                refusedCount = this.refused;
                count = inventoryCount + currencyCount + characterDataCount;
                inventory = currency = characterData = this.refused = 0;
                lastReportUtc = now;
            }

            (logger ?? Log.Logger).Warning(
                "economy_revisionless_saves {Count} in {WindowSeconds:0}s (inventory {Inventory}, currency {Currency}, characterData {CharacterData}), " +
                "{Refused} refused as revision_required. The rest were written last-write-wins and can overwrite a committed shop " +
                "transaction; RequireExpectedRevision can go on once the zone server sends ExpectedRevision and this stops.",
                count, windowSeconds, inventoryCount, currencyCount, characterDataCount, refusedCount);
        }
    }
}
