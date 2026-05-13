using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using System;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class CompleteZoneInstanceShutdownRequest
    {
        public int ZoneInstanceID { get; set; }

        private Guid CustomerGUID;
        private IInstanceManagementRepository _instanceManagementRepository;

        public void SetData(IInstanceManagementRepository instanceManagementRepository, IHeaderCustomerGUID customerGuid)
        {
            _instanceManagementRepository = instanceManagementRepository;
            CustomerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            SuccessAndErrorMessage output = await _instanceManagementRepository.CompleteZoneInstanceShutdown(CustomerGUID, ZoneInstanceID);

            return new OkObjectResult(output);
        }
    }
}
