using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterInventoryRequest : IOwnedCharacterWrite
    {
        public string CharacterName { get; set; }
        public Guid? OwnerToken { get; set; }
        string IOwnedCharacterWrite.OwnedCharacterName => CharacterName;

        public IEnumerable<UpdateCharacterInventory> Inventory { get; set; }

        // Optimistic lock. Send the Characters.EconomyRevision this bag was computed from and a
        // snapshot that lost a race with a shop transaction is rejected with "stale_revision" instead
        // of overwriting it; resync from NewEconomyRevision, recompute the bag, resend. 0 is a real
        // revision (a character no shop op has touched yet). Omitting it (or sending a negative) is
        // legacy last-write-wins, refused with "revision_required" once
        // EconomyOptions.RequireExpectedRevision is on.
        public long? ExpectedRevision { get; set; }

        // Order of this owner's bag saves. Requests are handled in the order they reach the database,
        // not the order they were sent, and each replaces the whole bag; a save whose SaveSeq is not
        // above the last one written with the same OwnerToken is answered Success with "superseded" and
        // writes nothing. Send a number that grows with every bag save made under one OwnerToken, from
        // one counter shared with UpdateCharacterData: max(last sent + 1, Unix time in milliseconds)
        // keeps growing even if the counter is lost while the token is kept.
        // It is never compared with another owner's SaveSeq, so a zone server that takes the character
        // over needs neither the previous owner's last value nor a clock that agrees with it: owners are
        // ordered by their claim, and once the new owner has saved, a late save from the owner it
        // replaced is "superseded" whatever its SaveSeq.
        // Omitting it (or sending 0 or a negative) keeps last-write-wins between saves.
        public long? SaveSeq { get; set; }

        private Guid customerGUID;
        private ICharactersRepository charactersRepository;
        private EconomyOptions economyOptions;

        public void SetData(ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid, EconomyOptions economyOptions)
        {
            this.charactersRepository = charactersRepository;
            customerGUID = customerGuid.CustomerGUID;
            this.economyOptions = economyOptions;
        }

        public async Task<UpdateCharacterInventoryResponse> Handle()
        {
            long? expectedRevision = ExpectedRevision >= 0 ? ExpectedRevision : null;
            if (expectedRevision == null && economyOptions.RequireExpectedRevision)
            {
                return new UpdateCharacterInventoryResponse
                {
                    Success = false,
                    ErrorMessage = "revision_required",
                    ReasonCode = "revision_required"
                };
            }

            try
            {
                long? saveSeq = SaveSeq > 0 ? SaveSeq : null;
                return await charactersRepository.UpdateCharacterInventory(customerGUID, CharacterName, Inventory, OwnerToken, expectedRevision, saveSeq);
            }
            catch (Exception ex)
            {
                return new UpdateCharacterInventoryResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ReasonCode = "exception"
                };
            }
        }
    }
}
