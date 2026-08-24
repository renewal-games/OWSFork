using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSManagement.DTOs;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Characters
{
    public class SetCharacterFlagsRequest
    {
        private readonly Guid _customerGuid;
        private readonly SetCharacterFlagsDTO _dto;
        private readonly ICharactersRepository _charactersRepository;

        public SetCharacterFlagsRequest(Guid customerGuid, SetCharacterFlagsDTO dto, ICharactersRepository charactersRepository)
        {
            _customerGuid = customerGuid;
            _dto = dto;
            _charactersRepository = charactersRepository;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            if (_dto == null || _dto.CharacterID < 1)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = "A valid CharacterID is required."
                };
            }

            return await _charactersRepository.SetCharacterFlags(_customerGuid, _dto.CharacterID,
                _dto.IsAdmin, _dto.IsModerator, _dto.IsInternalNetworkTestUser);
        }
    }
}
