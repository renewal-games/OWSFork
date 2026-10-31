namespace OWSData.Models.Composites
{
    public class UpdateCharacterDataResponse : SuccessAndErrorMessage
    {
        // The inventory half's UpdateCharacterInventoryResponse fields, so a caller that sent
        // ExpectedRevision can adopt the bumped revision (or resync on "stale_revision") exactly as it
        // would from UpdateCharacterInventory. 0 / "" when the inventory write was not attempted.
        public long NewEconomyRevision { get; set; }
        public string ReasonCode { get; set; } = "";
    }
}
