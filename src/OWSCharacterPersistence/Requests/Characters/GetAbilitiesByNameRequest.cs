using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSData.Models.Composites;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class GetAbilitiesByNameRequest
    {
        public string CharacterName { get; set; } = default!;

        private ICharactersRepository _repo = default!;
        private IHeaderCustomerGUID _cust = default!;

        public void SetData(ICharactersRepository repo, IHeaderCustomerGUID cust)
        {
            _repo = repo;
            _cust = cust;
        }

        public async Task<IActionResult> Handle()
        {
            var rows = await _repo.GetCharacterAbilities(
                           _cust.CustomerGUID,
                           CharacterName);

            return new OkObjectResult(rows);
        }
    }
}