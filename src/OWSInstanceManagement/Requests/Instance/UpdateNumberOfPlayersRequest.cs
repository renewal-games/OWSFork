using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    /// <summary>
    /// UpdateNumberOfPlayersRequest
    /// </summary>
    /// <remarks>
    /// Update the player count for the authenticated zone server's instance ID.
    /// The controller requires the server-to-server service key before this handler runs.
    /// </remarks>
    // The server-stat fields arrive flat in the same JSON body.
    public class UpdateNumberOfPlayersRequest : ZoneServerStats
    {
        //Request Parameters
        public int ZoneInstanceId { get; set; }
        public int NumberOfConnectedPlayers { get; set; }

        //Private objects
        private Guid CustomerGUID;
        private IInstanceManagementRepository _instanceMangementRepository;

        public void SetData(IInstanceManagementRepository instanceMangementRepository, IHeaderCustomerGUID customerGuid)
        {
            CustomerGUID = customerGuid.CustomerGUID;
            _instanceMangementRepository = instanceMangementRepository;
        }

        public async Task<IActionResult> Handle()
        {
            var output = await _instanceMangementRepository.UpdateNumberOfPlayers(CustomerGUID, ZoneInstanceId, NumberOfConnectedPlayers, this);

            return new OkObjectResult(output);
        }
    }
}
