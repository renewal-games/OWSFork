namespace OWSData.Models.Composites
{
    public class UpdateCharacterCurrencyResponse : SuccessAndErrorMessage
    {
        // Post-write Characters.EconomyRevision. The zone server resyncs its cached revision from
        // this so shop ops keep passing their optimistic-lock check after heartbeat gold saves.
        // 0 when the write failed or the backend/DB does not support the revision protocol.
        public long NewEconomyRevision { get; set; }

        // "" on success. "stale_revision" when the caller supplied an ExpectedRevision that no longer
        // matches: the wallet it computed lost a race with a shop transaction, so writing it would undo
        // that transaction. Reconcile against NewEconomyRevision/AuthoritativeGold instead of retrying.
        public string ReasonCode { get; set; } = "";

        // Committed Characters.Gold. On a stale_revision refusal this is what the wallet actually is,
        // so a caller whose local copy diverged (e.g. a purchase whose response was lost) can correct
        // itself without writing. 0 when unsupported.
        public int AuthoritativeGold { get; set; }
    }
}
