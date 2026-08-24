using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSManagement.DTOs;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Users
{
    public class SetUserNetworkTestFlagRequest
    {
        private readonly Guid _customerGuid;
        private readonly SetUserNetworkTestFlagDTO _dto;
        private readonly IUsersRepository _usersRepository;

        public SetUserNetworkTestFlagRequest(Guid customerGuid, SetUserNetworkTestFlagDTO dto, IUsersRepository usersRepository)
        {
            _customerGuid = customerGuid;
            _dto = dto;
            _usersRepository = usersRepository;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            if (_dto == null || _dto.UserGUID == Guid.Empty)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = "A valid UserGUID is required."
                };
            }

            return await _usersRepository.SetNetworkTestFlagForUser(_customerGuid, _dto.UserGUID, _dto.IsInternalNetworkTestUser);
        }
    }
}
