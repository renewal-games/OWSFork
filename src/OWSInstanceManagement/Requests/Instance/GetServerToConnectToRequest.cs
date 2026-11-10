using Microsoft.AspNetCore.Mvc;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using Microsoft.Extensions.Options;
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
                    PublishSpinUpMessage(new MQSpinUpServerMessage
                    {
                        CustomerGUID = CustomerGUID,
                        WorldServerID = joinMapByCharacterName.WorldServerID,
                        ZoneInstanceID = joinMapByCharacterName.MapInstanceID,
                        MapName = joinMapByCharacterName.MapNameToStart,
                        Port = joinMapByCharacterName.Port
                    });
                }
                catch (Exception ex)
                {
                    //Only a command that certainly reached no launcher queue lets the instance go. One that may be queued
                    //(lost or timed-out confirm) can still start a server on that row and port, so the row stays for the
                    //launcher or the CleanUpInstances sweep.
                    Log.Warning(ex, "GetServerToConnectTo: could not confirm the spin-up of Zone Instance {MapInstanceId}.", joinMapByCharacterName.MapInstanceID);
                    return await ReleaseReservationAndReturnFailure(joinMapByCharacterName, "GetServerToConnectTo: Failed to request zone server spin-up.",
                        removeUnstartedInstance: LauncherCommandChannel.CertainlyNotQueued(ex));
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

        //Returns only once RabbitMQ confirmed the command reached the launcher queue; throws otherwise.
        protected virtual void PublishSpinUpMessage(MQSpinUpServerMessage serverSpinUpMessage)
        {
            var factory = new ConnectionFactory()
            {
                HostName = rabbitMQOptions.Value.RabbitMQHostName,
                Port = rabbitMQOptions.Value.RabbitMQPort,
                UserName = rabbitMQOptions.Value.RabbitMQUserName,
                Password = rabbitMQOptions.Value.RabbitMQPassword
            };

            LauncherCommandChannel.ConnectAndPublish(factory, LauncherCommandChannel.SpinUpExchange, serverSpinUpMessage.WorldServerID, serverSpinUpMessage.Serialize());
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

        //removeUnstartedInstance is for a spin-up this request created whose command certainly reached no launcher. The zone
        //instance row would otherwise stay at Status 1 until the two-minute CleanUpInstances sweep, and every joiner who
        //found it would wait on it instead of spinning up a new instance.
        private async Task<IActionResult> ReleaseReservationAndReturnFailure(JoinMapByCharName joinMapByCharacterName, string errorMessage, bool removeUnstartedInstance = false)
        {
            if (joinMapByCharacterName?.MapInstanceID > 0)
            {
                await charactersRepository.ReleaseCharacterMapReservation(CustomerGUID, CharacterName, joinMapByCharacterName.MapInstanceID);

                if (removeUnstartedInstance)
                {
                    try
                    {
                        await charactersRepository.RemoveUnstartedMapInstance(CustomerGUID, joinMapByCharacterName.MapInstanceID);
                    }
                    catch (Exception ex)
                    {
                        //The join has already failed; the CleanUpInstances sweep still reaps the row.
                        Log.Error(ex, "GetServerToConnectTo: could not remove unstarted Zone Instance {MapInstanceId}.", joinMapByCharacterName.MapInstanceID);
                    }
                }
            }

            Output = joinMapByCharacterName ?? new JoinMapByCharName();
            Output.Success = false;
            Output.ErrorMessage = errorMessage;
            return new OkObjectResult(Output);
        }
    }
}
