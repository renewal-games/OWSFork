namespace OWSData.Models.Composites
{
    public class UpdateCharacterDataResponse : SuccessAndErrorMessage
    {
        // The inventory half's UpdateCharacterInventoryResponse fields, so a caller that sent
        // ExpectedRevision can adopt the bumped revision (or resync on "stale_revision") exactly as it
        // would from UpdateCharacterInventory. 0 when the inventory write was not attempted.
        public long NewEconomyRevision { get; set; }

        // "" on success. "stale_owner" when the owner token no longer owns the character (nothing was written),
        // "stale_zone" when another zone instance owns the character (nothing was written),
        // "bad_request" when a section is missing or the character does not exist, "no_inventory" when the
        // character has no CharInventory row (nothing was written). "stale_revision" / "revision_required"
        // when the economy revision guard refused the bag: quests and stats were still saved.
        public string ReasonCode { get; set; } = "";
    }
}
