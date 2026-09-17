using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSManagement.DTOs;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Zones
{
    public class UpdateZoneRequest
    {
        private readonly Guid _customerGuid;
        private readonly AddOrUpdateZoneDTO _dto;
        private readonly IInstanceManagementRepository _instanceManagementRepository;

        public UpdateZoneRequest(Guid customerGuid, AddOrUpdateZoneDTO dto, IInstanceManagementRepository instanceManagementRepository)
        {
            _customerGuid = customerGuid;
            _dto = dto;
            _instanceManagementRepository = instanceManagementRepository;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            if (_dto == null || _dto.MapID < 1)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = "A valid MapID is required."
                };
            }

            string validationError = ZoneValidation.Validate(_dto);

            if (validationError != null)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = validationError
                };
            }

            return await _instanceManagementRepository.UpdateZone(
                _customerGuid,
                _dto.MapID,
                _dto.MapName,
                _dto.ZoneName,
                _dto.WorldCompContainsFilter,
                _dto.WorldCompListFilter,
                _dto.SoftPlayerCap,
                _dto.HardPlayerCap,
                _dto.MapMode,
                _dto.MinutesToShutdownAfterEmpty);
        }
    }
}
