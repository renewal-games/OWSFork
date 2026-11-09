using Microsoft.AspNetCore.Mvc;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.RequestPayloads;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class GetZoneInstancesForWorldServerRequest : IRequestHandler<GetServerToConnectToRequest, IActionResult>, IRequest
    {
        public WorldServerIDRequestPayload Request { get; set; }

        private IEnumerable<GetZoneInstancesForWorldServer> _output;
        private IInstanceManagementRepository _instanceManagementRepository;
        private Guid _customerGUID;

        public void SetData(IInstanceManagementRepository instanceManagementRepository, IHeaderCustomerGUID customerGuid)
        {
            _instanceManagementRepository = instanceManagementRepository;
            _customerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            //Only a launcher asks for its own list, once per health monitoring cycle (service-key gated), so the request
            //doubles as its heartbeat. Every launcher version already sends it, so liveness needs no launcher redeploy.
            //Spin-up skips launchers that stop asking (PostgresQueries.GetActiveWorldServersByLoad).
            await _instanceManagementRepository.RecordWorldServerHeartbeat(_customerGUID, Request.WorldServerID);

            _output = await _instanceManagementRepository.GetZoneInstancesForWorldServer(_customerGUID, Request.WorldServerID);

            return new OkObjectResult(_output);
        }
    }
}
