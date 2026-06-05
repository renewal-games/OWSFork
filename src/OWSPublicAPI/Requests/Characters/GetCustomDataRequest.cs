using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Models.Tables;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.DTOs;
using OWSShared.Interfaces;

namespace OWSPublicAPI.Requests.Characters
{
    /// <summary>
    /// Public, ownership-validated custom character data read.
    /// </summary>
    public class GetCustomDataRequest : IRequestHandler<GetCustomDataRequest, IActionResult>, IRequest
    {
        private readonly GetCustomDataDTO _request;
        private readonly Guid _customerGUID;
        private readonly IUsersRepository _usersRepository;
        private readonly ICharactersRepository _charactersRepository;
        private readonly ICustomCharacterDataSelector _customCharacterDataSelector;

        public GetCustomDataRequest(
            GetCustomDataDTO request,
            IUsersRepository usersRepository,
            ICharactersRepository charactersRepository,
            IHeaderCustomerGUID customerGuid,
            ICustomCharacterDataSelector customCharacterDataSelector)
        {
            _request = request;
            _customerGUID = customerGuid.CustomerGUID;
            _usersRepository = usersRepository;
            _charactersRepository = charactersRepository;
            _customCharacterDataSelector = customCharacterDataSelector;
        }

        public async Task<IActionResult> Handle()
        {
            CustomCharacterDataRows output = new CustomCharacterDataRows
            {
                Rows = Enumerable.Empty<CustomCharacterData>()
            };

            if (!await TryGetOwnedCharacter())
            {
                return new BadRequestObjectResult(output);
            }

            var rows = await _charactersRepository.GetCustomCharacterData(_customerGUID, _request.CharacterName);

            output.Rows = (rows ?? Enumerable.Empty<CustomCharacterData>()).Where(row =>
                row != null &&
                _customCharacterDataSelector.ShouldExportThisCustomCharacterDataField(row.CustomFieldName));

            return new OkObjectResult(output);
        }

        private async Task<bool> TryGetOwnedCharacter()
        {
            if (_request == null ||
                String.IsNullOrWhiteSpace(_request.UserSessionGUID) ||
                String.IsNullOrWhiteSpace(_request.CharacterName) ||
                !Guid.TryParse(_request.UserSessionGUID, out Guid userSessionGuid))
            {
                return false;
            }

            GetUserSession userSession = await _usersRepository.GetUserSession(_customerGUID, userSessionGuid);
            if (userSession == null || !userSession.UserGuid.HasValue)
            {
                return false;
            }

            GetCharByCharName characterData = await _charactersRepository.GetCharByCharName(_customerGUID, _request.CharacterName);
            return characterData != null &&
                characterData.UserGuid.HasValue &&
                characterData.UserGuid == userSession.UserGuid;
        }
    }
}
