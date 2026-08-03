using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using System;
using System.Threading.Tasks;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterCurrencyRequest
    {
        public string CharacterName { get; set; }

        public int Gold { get; set; }

        private Guid customerGUID;
        private ICharactersRepository charactersRepository;

        public void SetData(ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            this.charactersRepository = charactersRepository;
            customerGUID = customerGuid.CustomerGUID;
        }

        public async Task<UpdateCharacterCurrencyResponse> Handle()
        {
            UpdateCharacterCurrencyResponse response = new UpdateCharacterCurrencyResponse();
            response.Success = true;

            try
            {
                response.NewEconomyRevision = await charactersRepository.UpdateCharacterCurrency(customerGUID, CharacterName, Gold);
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
