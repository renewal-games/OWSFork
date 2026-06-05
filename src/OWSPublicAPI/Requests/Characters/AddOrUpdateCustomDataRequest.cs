using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.DTOs;
using OWSShared.Interfaces;

namespace OWSPublicAPI.Requests.Characters
{
    /// <summary>
    /// Public, ownership-validated custom character data write.
    /// </summary>
    public class AddOrUpdateCustomDataRequest : IRequestHandler<AddOrUpdateCustomDataRequest, IActionResult>, IRequest
    {
        private const int MinLoginSlotIndex = 0;
        private const int MaxLoginSlotIndex = 6;
        private const string LoginSlotIndexFieldName = "LoginSlotIndex";

        private readonly AddOrUpdateCustomDataDTO _request;
        private readonly Guid _customerGUID;
        private readonly IUsersRepository _usersRepository;
        private readonly ICharactersRepository _charactersRepository;

        public AddOrUpdateCustomDataRequest(
            AddOrUpdateCustomDataDTO request,
            IUsersRepository usersRepository,
            ICharactersRepository charactersRepository,
            IHeaderCustomerGUID customerGuid)
        {
            _request = request;
            _customerGUID = customerGuid.CustomerGUID;
            _usersRepository = usersRepository;
            _charactersRepository = charactersRepository;
        }

        public async Task<IActionResult> Handle()
        {
            SuccessAndErrorMessage output = new SuccessAndErrorMessage
            {
                Success = false
            };

            if (!IsAllowedPublicWrite(out string validationError))
            {
                output.ErrorMessage = validationError;
                return new BadRequestObjectResult(output);
            }

            if (!await TryGetOwnedCharacter())
            {
                output.ErrorMessage = "Invalid session or character.";
                return new BadRequestObjectResult(output);
            }

            await _charactersRepository.AddOrUpdateCustomCharacterData(_customerGUID, _request.AddOrUpdateCustomCharacterData);

            output.Success = true;
            output.ErrorMessage = "";
            return new OkObjectResult(output);
        }

        private bool IsAllowedPublicWrite(out string errorMessage)
        {
            errorMessage = "";

            AddOrUpdateCustomCharacterData payload = _request?.AddOrUpdateCustomCharacterData;
            if (_request == null ||
                String.IsNullOrWhiteSpace(_request.UserSessionGUID) ||
                !Guid.TryParse(_request.UserSessionGUID, out _) ||
                payload == null ||
                String.IsNullOrWhiteSpace(payload.CharacterName) ||
                String.IsNullOrWhiteSpace(payload.CustomFieldName))
            {
                errorMessage = "Invalid custom data request.";
                return false;
            }

            if (!String.Equals(payload.CustomFieldName, LoginSlotIndexFieldName, StringComparison.Ordinal))
            {
                errorMessage = "This custom data field cannot be updated through the public API.";
                return false;
            }

            if (!Int32.TryParse(payload.FieldValue, out int slotIndex) ||
                slotIndex < MinLoginSlotIndex ||
                slotIndex > MaxLoginSlotIndex)
            {
                errorMessage = "LoginSlotIndex is out of range.";
                return false;
            }

            payload.FieldValue = slotIndex.ToString();

            return true;
        }

        private async Task<bool> TryGetOwnedCharacter()
        {
            Guid userSessionGuid = Guid.Parse(_request.UserSessionGUID);
            GetUserSession userSession = await _usersRepository.GetUserSession(_customerGUID, userSessionGuid);
            if (userSession == null || !userSession.UserGuid.HasValue)
            {
                return false;
            }

            GetCharByCharName characterData = await _charactersRepository.GetCharByCharName(_customerGUID, _request.AddOrUpdateCustomCharacterData.CharacterName);
            return characterData != null &&
                characterData.UserGuid.HasValue &&
                characterData.UserGuid == userSession.UserGuid;
        }
    }
}
