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
        // "stale_owner" when the owner token no longer owns the character: nothing was written.
        // "superseded", with Success true, when the request sent a SaveSeq and a newer bag is already
        // stored: one this owner sent with a higher SaveSeq (or this same save), or any numbered save of
        // the owner that replaced it. Nothing was written and nothing needs redoing.
        public string ReasonCode { get; set; } = "";

        // Characters.InventorySaveSeq as this request left it, for a request that sent a SaveSeq: its own
        // SaveSeq when the bag was written, the stored one that outranked it on "superseded" (the
        // replacing owner's own number when that is what outranked it). 0 otherwise.
        public long InventorySaveSeq { get; set; }
    }
}
