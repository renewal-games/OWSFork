namespace OWSData.Models.Composites
{
    public class UpdateCharacterCurrencyResponse : SuccessAndErrorMessage
    {
        // Post-write Characters.EconomyRevision. The zone server resyncs its cached revision from
        // this so shop ops keep passing their optimistic-lock check after heartbeat gold saves.
        // 0 when the write failed or the backend/DB does not support the revision protocol.
        public long NewEconomyRevision { get; set; }
    }
}
