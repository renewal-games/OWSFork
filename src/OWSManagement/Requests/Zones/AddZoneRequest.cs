using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSManagement.DTOs;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Zones
{
    public class AddZoneRequest
    {
        private readonly Guid _customerGuid;
        private readonly AddOrUpdateZoneDTO _dto;
        private readonly IInstanceManagementRepository _instanceManagementRepository;

        public AddZoneRequest(Guid customerGuid, AddOrUpdateZoneDTO dto, IInstanceManagementRepository instanceManagementRepository)
        {
            _customerGuid = customerGuid;
            _dto = dto;
            _instanceManagementRepository = instanceManagementRepository;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            string validationError = ZoneValidation.Validate(_dto);

            if (validationError != null)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = validationError
                };
            }

            // The repository rejects a ZoneName already in use; nothing here needs to re-check it.
            return await _instanceManagementRepository.AddZone(
                _customerGuid,
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
