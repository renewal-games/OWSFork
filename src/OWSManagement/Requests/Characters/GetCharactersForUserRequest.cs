using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Characters
{
    public class GetCharactersForUserRequest
    {
        private readonly Guid _customerGuid;
        private readonly Guid _userGuid;
        private readonly ICharactersRepository _charactersRepository;

        public GetCharactersForUserRequest(Guid customerGuid, Guid userGuid, ICharactersRepository charactersRepository)
        {
            _customerGuid = customerGuid;
            _userGuid = userGuid;
            _charactersRepository = charactersRepository;
        }

        public async Task<IEnumerable<AdminCharacterSummary>> Handle()
        {
            return await _charactersRepository.GetCharactersForUser(_customerGuid, _userGuid);
        }
    }
}
