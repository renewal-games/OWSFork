using OWSData.Models;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSManagement.DTOs;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Users
{
    public class EditUserRequest
    {
        private readonly Guid _customerGuid;
        private EditUserDTO _editUserDTO { get; set; }
        private readonly IUsersRepository _usersRepository;

        public EditUserRequest(Guid customerGuid, EditUserDTO editUserDTO, IUsersRepository usersRepository)
        {
            _customerGuid = customerGuid;
            _editUserDTO = editUserDTO;
            _usersRepository = usersRepository;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            SuccessAndErrorMessage result = await _usersRepository.UpdateUser(_customerGuid, _editUserDTO.UserGUID,
                _editUserDTO.FirstName, _editUserDTO.LastName, _editUserDTO.Email);

            if (!result.Success || string.IsNullOrWhiteSpace(_editUserDTO.Role))
            {
                return result;
            }

            // Role is a separate write so that clients that never send it (the game, the
            // older console build) cannot blank it out by omission.
            if (!UserRoles.TryNormalize(_editUserDTO.Role, out string normalizedRole))
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = $"Unknown role: {_editUserDTO.Role}. Valid roles are {string.Join(", ", UserRoles.All)}."
                };
            }

            return await _usersRepository.UpdateUserRole(_customerGuid, _editUserDTO.UserGUID, normalizedRole);
        }
    }
}
