using Microsoft.AspNetCore.Mvc;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using Microsoft.Extensions.Options;
using OWSShared.Interfaces;
using OWSShared.Messages;
using OWSShared.Options;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class GetServerToConnectToRequest : IRequestHandler<GetServerToConnectToRequest, IActionResult>, IRequest
    {
        public string CharacterName { get; set; }
        public string ZoneName { get; set; }
        public int PlayerGroupType { get; set; }

        private JoinMapByCharName Output;
        private Guid CustomerGUID;
        private ICharactersRepository charactersRepository;
        private IOptions<RabbitMQOptions> rabbitMQOptions;

        public void SetData(IOptions<RabbitMQOptions> rabbitMQOptions, ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            CustomerGUID = customerGuid.CustomerGUID;
            this.charactersRepository = charactersRepository;
            this.rabbitMQOptions = rabbitMQOptions;
        }

        public async Task<IActionResult> Handle()
        {
            if (String.IsNullOrEmpty(ZoneName) || ZoneName == "GETLASTZONENAME")
            {
                GetCharByCharName character = await charactersRepository.GetCharByCharName(CustomerGUID, CharacterName);

                if (character == null || String.IsNullOrEmpty(character.MapName))
                {
                    return new BadRequestResult();
                }

                ZoneName = character.MapName;
            }

            JoinMapByCharName joinMapByCharacterName = await charactersRepository.JoinMapByCharName(CustomerGUID, CharacterName, ZoneName, PlayerGroupType);

            bool readyForPlayersToConnect = false;

            if (joinMapByCharacterName == null)
            {
                Output = new JoinMapByCharName
                {
                    Success = false,
                    ErrorMessage = "GetServerToConnectTo: No response was returned when looking up the destination zone."
                };

                return new OkObjectResult(Output);
            }

            if (!joinMapByCharacterName.Success && !String.IsNullOrEmpty(joinMapByCharacterName.ErrorMessage))
            {
                return new OkObjectResult(joinMapByCharacterName);
            }

            if (joinMapByCharacterName.WorldServerID < 1)
            {
                Output = new JoinMapByCharName
                {
                    Success = false,
                    ErrorMessage = "GetServerToConnectTo: WorldServerID is less than 1. Make sure you setup at least one valid World Server and that it is currently running!"
                };

                return new OkObjectResult(Output);
            }

            if (joinMapByCharacterName.NeedToStartupMap)
            {
                try
                {
                    var factory = new ConnectionFactory()
                    {
                        HostName = rabbitMQOptions.Value.RabbitMQHostName,
                        Port = rabbitMQOptions.Value.RabbitMQPort,
                        UserName = rabbitMQOptions.Value.RabbitMQUserName,
                        Password = rabbitMQOptions.Value.RabbitMQPassword
                    };

                    using (var connection = factory.CreateConnection())
                    {
                        using (var channel = connection.CreateModel())
                        {
                            channel.ExchangeDeclare(exchange: "ows.serverspinup",
                                type: "direct",
                                durable: false,
                                autoDelete: false);

                            MQSpinUpServerMessage serverSpinUpMessage = new()
                            {
                                CustomerGUID = CustomerGUID,
                                WorldServerID = joinMapByCharacterName.WorldServerID,
                                ZoneInstanceID = joinMapByCharacterName.MapInstanceID,
                                MapName = joinMapByCharacterName.MapNameToStart,
                                Port = joinMapByCharacterName.Port
                            };

                            var body = serverSpinUpMessage.Serialize();

                            channel.BasicPublish(exchange: "ows.serverspinup",
                                                 routingKey: String.Format("ows.serverspinup.{0}", joinMapByCharacterName.WorldServerID),
                                                 basicProperties: null,
                                                 body: body);
                        }
                    }
                }
                catch
                {
                    return await ReleaseReservationAndReturnFailure(joinMapByCharacterName, "GetServerToConnectTo: Failed to request zone server spin-up.");
                }

                //Wait 5 seconds before the first CheckMapInstanceStatus to give it time to spin up
                await Task.Delay(TimeSpan.FromSeconds(5));

                readyForPlayersToConnect = await WaitForServerReadyToConnect(joinMapByCharacterName.MapInstanceID);
            }
            else if (joinMapByCharacterName.MapInstanceID > 0 && joinMapByCharacterName.MapInstanceStatus == 1)
            {
                //CheckMapInstanceStatus every 2 seconds for up to 90 seconds
                readyForPlayersToConnect = await WaitForServerReadyToConnect(joinMapByCharacterName.MapInstanceID);
            }
            else if (joinMapByCharacterName.MapInstanceID > 0 && joinMapByCharacterName.MapInstanceStatus == 2)
            {
                //The zone server is ready to connect to
                readyForPlayersToConnect = true;
            }

            if (!readyForPlayersToConnect)
            {
                return await ReleaseReservationAndReturnFailure(joinMapByCharacterName, "GetServerToConnectTo: Zone server did not become ready before the timeout.");
            }

            await charactersRepository.AddCharacterToMapInstanceByCharName(CustomerGUID, CharacterName, joinMapByCharacterName.MapInstanceID);

            Output = joinMapByCharacterName;
            return new OkObjectResult(Output);
        }

        private async Task<bool> WaitForServerReadyToConnect(int mapInstanceID)
        {
            DateTime StartPollingTime = DateTime.Now;

            while (DateTime.Now < StartPollingTime.AddSeconds(90))
            {
                //Check Map Status
                var resultCheckMapInstanceStatus = await charactersRepository.CheckMapInstanceStatus(CustomerGUID, mapInstanceID);

                if (resultCheckMapInstanceStatus.Status == 2) //Ready to play
                {
                    /*using (RPGDataDataContext dc = new RPGDataDataContext())
                    {
                        dc.AddCharacterToMapInstanceByCharName(gCustomerGUID, id, MapInstanceID); //id = CharName
                    }*/

                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return false;
        }

        private async Task<IActionResult> ReleaseReservationAndReturnFailure(JoinMapByCharName joinMapByCharacterName, string errorMessage)
        {
            if (joinMapByCharacterName?.MapInstanceID > 0)
            {
                await charactersRepository.ReleaseCharacterMapReservation(CustomerGUID, CharacterName, joinMapByCharacterName.MapInstanceID);
            }

            Output = joinMapByCharacterName ?? new JoinMapByCharName();
            Output.Success = false;
            Output.ErrorMessage = errorMessage;
            return new OkObjectResult(Output);
        }
    }
}
