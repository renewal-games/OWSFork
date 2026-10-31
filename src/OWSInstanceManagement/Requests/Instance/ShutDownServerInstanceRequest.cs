using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Messages;
using OWSShared.Options;
using RabbitMQ.Client;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class ShutDownServerInstanceRequest : IRequestHandler<SpinUpServerInstanceRequest, IActionResult>, IRequest
    {
        public int WorldServerID { get; set; }
        public int ZoneInstanceID { get; set; }

        private IOptions<RabbitMQOptions> _rabbitMQOptions;
        private SuccessAndErrorMessage Output;
        private Guid CustomerGUID;
        private IInstanceManagementRepository _instanceMangementRepository;

        public void SetData(IOptions<RabbitMQOptions> rabbitMQOptions, IInstanceManagementRepository instanceMangementRepository, IHeaderCustomerGUID customerGuid)
        {
            _rabbitMQOptions = rabbitMQOptions;
            _instanceMangementRepository = instanceMangementRepository;
            CustomerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            //The launcher to notify is the one that owns the row. WorldServerID in the body is only a caller's hint and
            //is not trusted: publishing to the wrong launcher leaves the row at status 3 with nobody to complete it.
            GetServerInstanceFromPort zoneInstance = await _instanceMangementRepository.GetZoneInstance(CustomerGUID, ZoneInstanceID);

            if (zoneInstance == null || zoneInstance.MapInstanceID != ZoneInstanceID)
            {
                return new OkObjectResult(new SuccessAndErrorMessage()
                {
                    Success = false,
                    ErrorMessage = $"Zone Instance {ZoneInstanceID} was not found."
                });
            }

            if (WorldServerID != zoneInstance.WorldServerID)
            {
                Log.Warning($"ShutDownServerInstance - Zone Instance {ZoneInstanceID} belongs to World Server {zoneInstance.WorldServerID}, not {WorldServerID}. Using {zoneInstance.WorldServerID}.");
            }

            //Set the status to 3 (shutting down) BEFORE publishing the shutdown message. The launcher's follow-up
            //CompleteZoneInstanceShutdown requires status 3; publishing first creates a race where the launcher can
            //call CompleteZoneInstanceShutdown before the status is set (papered over by a retry loop launcher-side).
            Output = await _instanceMangementRepository.SetZoneInstanceStatus(CustomerGUID, ZoneInstanceID, 3);

            if (!Output.Success)
            {
                return new OkObjectResult(Output);
            }

            try
            {
                PublishShutDownMessage(zoneInstance.WorldServerID);
            }
            catch (Exception ex)
            {
                //Nobody will complete the shutdown, and status 3 stops the server's heartbeat from updating the row.
                //Put the previous status back so the instance stays usable and a later shutdown can be retried.
                Log.Error(ex, $"ShutDownServerInstance - Could not publish the shutdown of Zone Instance {ZoneInstanceID} to World Server {zoneInstance.WorldServerID}.");

                if (zoneInstance.Status != 3)
                {
                    await _instanceMangementRepository.SetZoneInstanceStatus(CustomerGUID, ZoneInstanceID, zoneInstance.Status);
                }

                return new OkObjectResult(new SuccessAndErrorMessage()
                {
                    Success = false,
                    ErrorMessage = $"Could not send the shutdown of Zone Instance {ZoneInstanceID}: {ex.Message}"
                });
            }

            return new OkObjectResult(Output);
        }

        //Returns only once RabbitMQ confirmed the command reached the launcher queue for worldServerID; throws otherwise.
        protected virtual void PublishShutDownMessage(int worldServerID)
        {
            //Send the servershutdown message to RabbitMQ
            ConnectionFactory factory = new()
            {
                HostName = _rabbitMQOptions.Value.RabbitMQHostName,
                Port = _rabbitMQOptions.Value.RabbitMQPort,
                UserName = _rabbitMQOptions.Value.RabbitMQUserName,
                Password = _rabbitMQOptions.Value.RabbitMQPassword
            };

            using (var connection = factory.CreateConnection())
            {
                using (var channel = connection.CreateModel())
                {
                    MQShutDownServerMessage serverShutDownMessage = new()
                    {
                        CustomerGUID = CustomerGUID,
                        ZoneInstanceID = ZoneInstanceID
                    };

                    LauncherCommandChannel.Publish(channel, LauncherCommandChannel.ShutDownExchange, worldServerID, serverShutDownMessage.Serialize());
                }
            }
        }
    }
}
