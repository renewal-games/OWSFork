using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.RequestPayloads;
using System;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class ShutDownInstanceLauncherRequest
    {
        public WorldServerIDRequestPayload Request { get; set; }

        private SuccessAndErrorMessage _output;
        private IInstanceManagementRepository _instanceManagementRepository;
        private Guid _customerGUID;
        private Guid _launcherGuid;

        public void SetData(IInstanceManagementRepository instanceManagementRepository, IHeaderCustomerGUID customerGuid, Guid launcherGuid)
        {
            _instanceManagementRepository = instanceManagementRepository;
            _customerGUID = customerGuid.CustomerGUID;
            _launcherGuid = launcherGuid;
        }

        public async Task<IActionResult> Handle()
        {
            if (Request == null)
            {
                return new BadRequestObjectResult("Request is required.");
            }

            int? worldServerID = await _instanceManagementRepository.GetWorldServerIDForLauncher(_customerGUID, _launcherGuid);
            if (!worldServerID.HasValue || worldServerID.Value != Request.WorldServerID)
            {
                return new StatusCodeResult(403);
            }

            _output = await _instanceManagementRepository.ShutDownWorldServer(_customerGUID, Request.WorldServerID);

            return new OkObjectResult(_output);
        }
    }
}
