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

        //Set by the launcher's idle shutdown. The instance is then only shut down if it is still idle when the status
        //changes: a player may have been routed to it since the launcher read the player count.
        public bool OnlyIfIdle { get; set; }

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
            //If an idle shutdown finds the instance no longer idle, nothing is published, so the launcher kills nothing.
            Output = await _instanceMangementRepository.BeginZoneInstanceShutdown(CustomerGUID, ZoneInstanceID, OnlyIfIdle);

            if (!Output.Success)
            {
                if (OnlyIfIdle)
                {
                    Log.Information($"ShutDownServerInstance - Skipped idle shutdown of Zone Instance {ZoneInstanceID}: {Output.ErrorMessage}");
                }

                return new OkObjectResult(Output);
            }

            try
            {
                PublishShutDownMessage(zoneInstance.WorldServerID);
            }
            catch (Exception ex)
            {
                bool certainlyNotQueued = LauncherCommandChannel.CertainlyNotQueued(ex);
                Log.Error(ex, $"ShutDownServerInstance - Could not confirm the shutdown of Zone Instance {ZoneInstanceID} to World Server {zoneInstance.WorldServerID} (certainly not queued: {certainlyNotQueued}).");

                if (!certainlyNotQueued)
                {
                    //The command may still be queued (a confirm that timed out or was lost with the connection), so the launcher
                    //may yet stop the process and call CompleteZoneInstanceShutdown, which needs status 3. Putting the old status
                    //back would route joins to a server about to die and make that completion fail. If the command really was
                    //lost, status 3 stops the heartbeat from refreshing the row, so the health monitor sees it go stale and
                    //sends the shutdown again.
                    return new OkObjectResult(new SuccessAndErrorMessage()
                    {
                        Success = false,
                        ErrorMessage = $"Could not confirm the shutdown of Zone Instance {ZoneInstanceID}; it may still be delivered, so the instance stays shutting down: {ex.Message}"
                    });
                }

                //No launcher can receive the command, so nobody will complete the shutdown, and status 3 stops the server's
                //heartbeat from updating the row. Put the previous status back so the instance stays usable and a later
                //shutdown can be retried.
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
        //A failure to connect or open a channel is reported as certainly not queued, since nothing was sent.
        protected virtual void PublishShutDownMessage(int worldServerID)
        {
            ConnectionFactory factory = new()
            {
                HostName = _rabbitMQOptions.Value.RabbitMQHostName,
                Port = _rabbitMQOptions.Value.RabbitMQPort,
                UserName = _rabbitMQOptions.Value.RabbitMQUserName,
                Password = _rabbitMQOptions.Value.RabbitMQPassword
            };

            MQShutDownServerMessage serverShutDownMessage = new()
            {
                CustomerGUID = CustomerGUID,
                ZoneInstanceID = ZoneInstanceID
            };

            LauncherCommandChannel.ConnectAndPublish(factory, LauncherCommandChannel.ShutDownExchange, worldServerID, serverShutDownMessage.Serialize());
        }
    }
}
