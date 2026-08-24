using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Characters
{
    public class SearchCharactersRequest
    {
        private readonly Guid _customerGuid;
        private readonly string _searchText;
        private readonly ICharactersRepository _charactersRepository;

        public SearchCharactersRequest(Guid customerGuid, string searchText, ICharactersRepository charactersRepository)
        {
            _customerGuid = customerGuid;
            _searchText = searchText;
            _charactersRepository = charactersRepository;
        }

        public async Task<IEnumerable<AdminCharacterSummary>> Handle()
        {
            return await _charactersRepository.SearchCharacters(_customerGuid, _searchText);
        }
    }
}
