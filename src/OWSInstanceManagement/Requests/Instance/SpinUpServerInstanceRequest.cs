using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared;
using OWSShared.Interfaces;
using OWSShared.Messages;
using OWSShared.Options;
using RabbitMQ.Client;
using Serilog;
using SimpleInjector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class SpinUpServerInstanceRequest : IRequestHandler<SpinUpServerInstanceRequest, IActionResult>, IRequest
    {
        public int WorldServerID { get; set; }
        public int ZoneInstanceID { get; set; }
        public string ZoneName { get; set; }
        public int Port { get; set; }

        private IOptions<RabbitMQOptions> _rabbitMQOptions;
        private SuccessAndErrorMessage Output;
        private Guid CustomerGUID;
        //private IServiceProvider ServiceProvider;
        private IInstanceManagementRepository _instanceManagementRepository;

        public void SetData(IOptions<RabbitMQOptions> rabbitMQOptions, IInstanceManagementRepository instanceManagementRepository, IHeaderCustomerGUID customerGuid)
        {
            _rabbitMQOptions = rabbitMQOptions;
            _instanceManagementRepository = instanceManagementRepository;
            CustomerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            // ZoneName becomes the first token of the zone server's command line on the launcher.
            if (!ZoneMapName.IsValid(ZoneName))
            {
                return new BadRequestObjectResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = "Invalid ZoneName." });
            }

            if (WorldServerID < 1 || ZoneInstanceID < 1 || !SpinUpPort.IsValid(Port))
            {
                return new BadRequestObjectResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = "Invalid WorldServerID, ZoneInstanceID or Port." });
            }

            //The launcher starts a process from these values, so they must describe a zone instance this customer already has.
            //GetZoneInstance also returns an empty row when the lookup fails, which refuses the spin-up too.
            GetServerInstanceFromPort zoneInstance = await _instanceManagementRepository.GetZoneInstance(CustomerGUID, ZoneInstanceID);

            if (zoneInstance == null || zoneInstance.MapInstanceID != ZoneInstanceID
                || zoneInstance.WorldServerID != WorldServerID || zoneInstance.Port != Port)
            {
                Log.Warning($"SpinUpServerInstance - Refused: World Server {WorldServerID} and Port {Port} do not match a Zone Instance {ZoneInstanceID} of this customer.");
                return new BadRequestObjectResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = "WorldServerID, ZoneInstanceID and Port do not match a zone instance." });
            }

            MQSpinUpServerMessage serverSpinUpMessage = new()
            {
                CustomerGUID = CustomerGUID,
                WorldServerID = WorldServerID,
                ZoneInstanceID = ZoneInstanceID,
                MapName = ZoneName,
                Port = Port
            };

            try
            {
                PublishSpinUpMessage(serverSpinUpMessage);
            }
            catch (Exception ex)
            {
                //No launcher took the command, so no server will start for this instance. Say so instead of
                //reporting a success the caller would wait on.
                Log.Error(ex, $"SpinUpServerInstance - Could not send the spin-up of Zone Instance {ZoneInstanceID} to World Server {WorldServerID}.");

                Output = new SuccessAndErrorMessage()
                {
                    Success = false,
                    ErrorMessage = $"Could not send the spin-up of Zone Instance {ZoneInstanceID}: {ex.Message}"
                };

                return new OkObjectResult(Output);
            }

            Output = new SuccessAndErrorMessage() {
                Success = true,
                ErrorMessage = ""
            };

            return new OkObjectResult(Output);
        }

        //Returns only once RabbitMQ confirmed the command reached the launcher queue for WorldServerID; throws otherwise.
        protected virtual void PublishSpinUpMessage(MQSpinUpServerMessage serverSpinUpMessage)
        {
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
                    LauncherCommandChannel.Publish(channel, LauncherCommandChannel.SpinUpExchange, serverSpinUpMessage.WorldServerID, serverSpinUpMessage.Serialize());
                }
            }
        }
    }
}
