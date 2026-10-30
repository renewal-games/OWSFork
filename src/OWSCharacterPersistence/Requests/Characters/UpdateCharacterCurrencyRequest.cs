using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Options;
using System;
using System.Threading.Tasks;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterCurrencyRequest : IOwnedCharacterWrite
    {
        public string CharacterName { get; set; }
        public Guid? OwnerToken { get; set; }
        string IOwnedCharacterWrite.OwnedCharacterName => CharacterName;

        public int Gold { get; set; }

        // The Characters.EconomyRevision this wallet was computed from. Sent, the write is refused with
        // ReasonCode "stale_revision" when it no longer matches rather than overwriting a shop
        // transaction that committed in between. Omitted (null), behaviour is legacy last-write-wins,
        // refused with "revision_required" once EconomyOptions.RequireExpectedRevision is on.
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

        public async Task<UpdateCharacterCurrencyResponse> Handle()
        {
            // Gold is written as an absolute value, so this is the only place an out-of-range wallet
            // (a negative balance, or one past the cap) can be stopped before it lands.
            if (Gold < 0 || (economyOptions.MaxGold > 0 && Gold > economyOptions.MaxGold))
            {
                return new UpdateCharacterCurrencyResponse
                {
                    Success = false,
                    ErrorMessage = "gold_out_of_range",
                    ReasonCode = "gold_out_of_range"
                };
            }

            if (ExpectedRevision == null && economyOptions.RequireExpectedRevision)
            {
                return new UpdateCharacterCurrencyResponse
                {
                    Success = false,
                    ErrorMessage = "revision_required",
                    ReasonCode = "revision_required"
                };
            }

            UpdateCharacterCurrencyResponse response = new UpdateCharacterCurrencyResponse();
            response.Success = true;

            try
            {
                response = await charactersRepository.UpdateCharacterCurrency(customerGUID, CharacterName, Gold, ExpectedRevision);
            }
            catch (Exception ex)
            {
                response.ErrorMessage = ex.Message;
                response.Success = false;
            }

            return response;
        }
    }
}
