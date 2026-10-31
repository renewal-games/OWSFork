namespace OWSShared.Options
{
    public class EconomyOptions
    {
        public const string SectionName = "OWSEconomyConfig";

        // When true, the absolute bag and wallet saves (UpdateCharacterInventory, UpdateCharacterCurrency
        // and the inventory half of UpdateCharacterData) must carry the Characters.EconomyRevision they
        // were computed from; one without it is refused with "revision_required" instead of being written
        // last-write-wins over a shop transaction that committed meanwhile. Off by default only because
        // the zone server must send ExpectedRevision first (docs/mvp-hardening-spec.md item 2): turning it
        // on before that rejects every autosave.
        public bool RequireExpectedRevision { get; set; }

        // Upper bound on an absolute wallet write. 0 = uncapped (int.MaxValue). Negative gold is always refused.
        public int MaxGold { get; set; }
    }
}
