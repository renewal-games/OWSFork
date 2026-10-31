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
                return await charactersRepository.UpdateCharacterInventory(customerGUID, CharacterName, Inventory, expectedRevision);
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
