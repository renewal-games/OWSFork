namespace OWSData.Models.Composites
{
    public class UpdateCharacterInventoryResponse : SuccessAndErrorMessage
    {
        // Post-write Characters.EconomyRevision, mirroring UpdateCharacterCurrencyResponse. Only
        // advances when the caller supplied an ExpectedRevision — see the repository for why an
        // unconditional bump would break clients that do not read this value back. 0 when the
        // backend/DB does not support the revision protocol.
        public long NewEconomyRevision { get; set; }

        // "" on success. "stale_revision" when the caller's snapshot lost a race with a shop
        // transaction and must be recomputed against NewEconomyRevision rather than retried as-is.
        public string ReasonCode { get; set; } = "";
    }
}
