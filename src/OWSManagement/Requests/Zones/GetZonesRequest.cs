using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Zones
{
    public class GetZonesRequest
    {
        private readonly Guid _customerGuid;
        private readonly IInstanceManagementRepository _instanceManagementRepository;

        public GetZonesRequest(Guid customerGuid, IInstanceManagementRepository instanceManagementRepository)
        {
            _customerGuid = customerGuid;
            _instanceManagementRepository = instanceManagementRepository;
        }

        public async Task<IEnumerable<ZoneSummary>> Handle()
        {
            return await _instanceManagementRepository.GetZones(_customerGuid);
        }
    }
}
