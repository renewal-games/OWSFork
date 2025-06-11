using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Models.Composites;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterAbilitiesRequest
    {
        // Payload from Unreal
        public string CharacterName { get; set; } = default!;
        public List<CharacterAbilityDto> CharacterAbilities { get; set; } = new();

        private ICharactersRepository _repo = default!;
        private IHeaderCustomerGUID _cust = default!;

        public void SetData(ICharactersRepository repo, IHeaderCustomerGUID cust)
        {
            _repo = repo;
            _cust = cust;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            var json = JsonSerializer.Serialize(
                            CharacterAbilities,
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            await _repo.UpdateCharacterAbilities(
                    _cust.CustomerGUID,
                    CharacterName,
                    json);

            return new SuccessAndErrorMessage
            {
                Success = true,
                ErrorMessage = string.Empty
            };
        }
    }
}