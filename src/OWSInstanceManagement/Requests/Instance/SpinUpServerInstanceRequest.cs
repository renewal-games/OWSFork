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
        private ICharactersRepository _charactersRepository;

        public void SetData(IOptions<RabbitMQOptions> rabbitMQOptions, ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            _rabbitMQOptions = rabbitMQOptions;
            _charactersRepository = charactersRepository;
            CustomerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            // ZoneName becomes the first token of the zone server's command line on the launcher.
            if (!ZoneMapName.IsValid(ZoneName))
            {
                return new BadRequestObjectResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = "Invalid ZoneName." });
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
