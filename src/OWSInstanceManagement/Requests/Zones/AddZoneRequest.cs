using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared;
using OWSShared.Interfaces;
using OWSShared.RequestPayloads;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Zones
{
    public class AddZoneRequest
    {
        public AddOrUpdateZoneRequestPayload addOrUpdateZone { get; set; }

        private Guid customerGUID;
        private IInstanceManagementRepository instanceManagementRepository;

        public void SetData(IInstanceManagementRepository instanceManagementRepository, IHeaderCustomerGUID customerGuid)
        {
            this.instanceManagementRepository = instanceManagementRepository;
            customerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            // The launcher starts zone servers with this MapName as the first token of their command line.
            if (addOrUpdateZone == null || !ZoneMapName.IsValid(addOrUpdateZone.MapName))
            {
                return new BadRequestObjectResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = "Invalid MapName." });
            }

            SuccessAndErrorMessage successAndErrorMessage = new SuccessAndErrorMessage();

            successAndErrorMessage = await instanceManagementRepository.AddZone(customerGUID, addOrUpdateZone.MapName, addOrUpdateZone.ZoneName, addOrUpdateZone.WorldCompContainsFilter, addOrUpdateZone.WorldCompListFilter, addOrUpdateZone.SoftPlayerCap, addOrUpdateZone.HardPlayerCap, 
                addOrUpdateZone.MapMode, addOrUpdateZone.MinutesToShutdownAfterEmpty);

            return new OkObjectResult(successAndErrorMessage);
        }
    }
}
