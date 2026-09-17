using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using System;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Zones
{
    public class DeleteZoneRequest
    {
        private readonly Guid _customerGuid;
        private readonly int _mapId;
        private readonly IInstanceManagementRepository _instanceManagementRepository;

        public DeleteZoneRequest(Guid customerGuid, int mapId, IInstanceManagementRepository instanceManagementRepository)
        {
            _customerGuid = customerGuid;
            _mapId = mapId;
            _instanceManagementRepository = instanceManagementRepository;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            if (_mapId < 1)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = "A valid MapID is required."
                };
            }

            return await _instanceManagementRepository.DeleteZone(_customerGuid, _mapId);
        }
    }
}
