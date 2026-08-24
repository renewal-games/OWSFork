using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSManagement.DTOs;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Characters
{
    public class SetCharacterAdminFlagsRequest
    {
        private readonly Guid _customerGuid;
        private readonly SetCharacterAdminFlagsDTO _dto;
        private readonly ICharactersRepository _charactersRepository;

        public SetCharacterAdminFlagsRequest(Guid customerGuid, SetCharacterAdminFlagsDTO dto, ICharactersRepository charactersRepository)
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

            return await _charactersRepository.SetCharacterAdminFlags(_customerGuid, _dto.CharacterID, _dto.IsAdmin, _dto.IsModerator);
        }
    }
}
