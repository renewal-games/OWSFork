using Grpc.Core;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Grpc;
using Google.Protobuf.WellKnownTypes;
using GuildServiceApp.Protos;
using MySqlX.XDevAPI;
using PartyServiceApp.Protos;
using OWSGuild.Requests.Guild;
using Microsoft.IdentityModel.Tokens;
using Google.Protobuf.Collections;

namespace OWSGuild.Service
{

    public class GuildService : GuildApp.GuildAppBase
    {
        public static List<ClientInfo> _guildClients = new List<ClientInfo>();

        private readonly ICharactersRepository _charactersRepository;
        private readonly IUsersRepository _usersRepository;
        private readonly IHeaderCustomerGUID _customerGuid;

        static private Dictionary<string, RepeatedField<GuildStorageItem>> ItemStorage = new Dictionary<string, RepeatedField<GuildStorageItem>>();

        public GuildService(ICharactersRepository charactersRepository,
            IUsersRepository usersRepository,
            IHeaderCustomerGUID customerGuid)
        {
            _charactersRepository = charactersRepository;
            _usersRepository = usersRepository;
            _customerGuid = customerGuid;
        }

        public override async Task RegisterGuild(GuildRegister request, IServerStreamWriter<GuildToSend> responseStream, ServerCallContext context)
        {
            string id = request.UserId;
            string userName = request.UserName;

            // Registration is bound to the session's character, so a client cannot register the stream as someone else.
            Guid customerGuid = await ResolveCustomerGuid(request.CustomerGuid, context, userName);

            _guildClients.Add(new ClientInfo
            {
                Id = id,
                UserName = userName,
                serverStreamWriter = responseStream
            });

            _customerGuid.CustomerGUID = customerGuid;
            GetInitialGuildSettingsRequest getInitalGuildSettings = new GetInitialGuildSettingsRequest();
            getInitalGuildSettings.SetData(_charactersRepository, _customerGuid, userName);

            GuildToSend guildInformation = await getInitalGuildSettings.Handle();
            if(!guildInformation.GuildInfo.GuildGuid.IsNullOrEmpty() && ItemStorage.ContainsKey(guildInformation.GuildInfo.GuildGuid))
            {
                guildInformation.GuildBank.GuildCurrentStoredItems.Add(ItemStorage.GetValueOrDefault(guildInformation.GuildInfo.GuildGuid));
            }

            if (guildInformation != null && guildInformation != default && guildInformation.GuildMembers.Count != 0)
            {
                await responseStream.WriteAsync(guildInformation);
            }

            Func<bool> isCancelled = () => context.CancellationToken.IsCancellationRequested;
            SpinWait.SpinUntil(isCancelled);
            _guildClients.RemoveAll(x => x.Id == id);
            return;
        }


        public override async Task<Empty> SendGuildMsg(GuildToSend request, ServerCallContext context)
        {
            ClientInfo client = null;
            List<Task> guildTasks = new List<Task>();
            GuildToSend GuildInformation = null;
            _customerGuid.CustomerGUID = await ResolveCustomerGuid(request.CustomerGuid, context, null);
            switch (request.GuildAction)
            {
                case GuildAction.MessageTypeAsk:
                    client = _guildClients.Find(client => client.UserName == request.GuildMembers[1].CharName);
                    if (client == null || client == default)
                    {
                        break;
                    }
                    guildTasks.Add(client.serverStreamWriter.WriteAsync(request));
                    await Task.WhenAll(guildTasks);
                    break;
                case GuildAction.MessageTypeCreate:
                case GuildAction.MessageTypeAdd:

                    CreateGuildOrAddMemberRequest createGuildOrAddMemberRequest = new CreateGuildOrAddMemberRequest();
                    createGuildOrAddMemberRequest.SetData(_charactersRepository, _customerGuid, request);

                    GuildInformation = await createGuildOrAddMemberRequest.Handle();

                    foreach (var guildMember in GuildInformation.GuildMembers)
                    {
                        client = _guildClients.Find(client => client.UserName == guildMember.CharName);
                        if (client == null || client == default)
                        {
                            continue;
                        }
                        guildTasks.Add(client.serverStreamWriter.WriteAsync(GuildInformation));
                    }
                    await Task.WhenAll(guildTasks);
                    break;
                case GuildAction.MessageTypeAbility:
                    AddGuildAbilitiesRequest addGuildAbilities = new AddGuildAbilitiesRequest();
                    addGuildAbilities.SetData(_charactersRepository, _customerGuid, request);

                    GuildInformation = await addGuildAbilities.Handle();

                    foreach (var guildMember in GuildInformation.GuildMembers)
                    {
                        client = _guildClients.Find(client => client.UserName == guildMember.CharName);
                        if (client == null || client == default)
                        {
                            continue;
                        }
                        guildTasks.Add(client.serverStreamWriter.WriteAsync(GuildInformation));
                    }
                    await Task.WhenAll(guildTasks);
                    break;
                case GuildAction.MessageTypeStorageDeposit:
                    if (!ItemStorage.ContainsKey(request.GuildInfo.GuildGuid))
                    {
                        ItemStorage.Add(request.GuildInfo.GuildGuid, request.GuildBank.GuildStorageItems);
                    }
                    else
                    {
                        RepeatedField<GuildStorageItem> ItemInList = ItemStorage.GetValueOrDefault(request.GuildInfo.GuildGuid);

                        foreach (GuildStorageItem ItemToDeposit in request.GuildBank.GuildStorageItems)
                        {
                            bool itemFound = false;
                            foreach (GuildStorageItem ItemStored in ItemInList)
                            {
                                if (ItemStored.GuildItemId.Equals(ItemToDeposit.GuildItemId) && ItemStored.GuildItemCustomData.Equals(ItemToDeposit.GuildItemCustomData))
                                {
                                    ItemStored.GuildItemQuantity += ItemToDeposit.GuildItemQuantity;
                                    itemFound = true;
                                    break;
                                }
                            }
                            if (!itemFound)
                            {
                                ItemInList.Add(ItemToDeposit);
                            }
                        }
                    }
                    
                    request.GuildBank.GuildCurrentStoredItems.Add(ItemStorage.GetValueOrDefault(request.GuildInfo.GuildGuid));
                    foreach (var guildMember in request.GuildMembers)
                    {
                        client = _guildClients.Find(client => client.UserName == guildMember.CharName);
                        if (client == null || client == default)
                        {
                            continue;
                        }
                        guildTasks.Add(client.serverStreamWriter.WriteAsync(request));
                    }
                    await Task.WhenAll(guildTasks);
                    break;
                case GuildAction.MessageTypeStorageWithdraw:
                    if (!ItemStorage.ContainsKey(request.GuildInfo.GuildGuid))
                    {
                        ItemStorage.Add(request.GuildInfo.GuildGuid, new RepeatedField<GuildStorageItem>());
                    }
                    else
                    {
                        RepeatedField<GuildStorageItem> ItemInList = ItemStorage.GetValueOrDefault(request.GuildInfo.GuildGuid);
                        foreach (GuildStorageItem ItemToDeposit in request.GuildBank.GuildStorageItems)
                        {
                            bool itemFound = false;
                            foreach (GuildStorageItem ItemStored in ItemInList)
                            {
                                if (ItemStored.GuildItemId.Equals(ItemToDeposit.GuildItemId) && ItemStored.GuildItemCustomData.Equals(ItemToDeposit.GuildItemCustomData))
                                {
                                    itemFound = true;
                                    int totalQuantityFound = ItemStored.GuildItemQuantity;
                                    ItemStored.GuildItemQuantity = Math.Max(ItemStored.GuildItemQuantity - ItemToDeposit.GuildItemQuantity, 0);
                                    ItemToDeposit.GuildItemQuantity = totalQuantityFound - ItemStored.GuildItemQuantity;
                                    if (ItemStored.GuildItemQuantity <= 0)
                                    {
                                        ItemInList.Remove(ItemStored);
                                    }
                                    break;
                                }
                            }
                            if (!itemFound)
                            {
                                ItemToDeposit.GuildItemQuantity = 0;
                            }
                        }
                    }

                    request.GuildBank.GuildCurrentStoredItems.Add(ItemStorage.GetValueOrDefault(request.GuildInfo.GuildGuid));
                    foreach (var guildMember in request.GuildMembers)
                    {
                        client = _guildClients.Find(client => client.UserName == guildMember.CharName);
                        if (client == null || client == default)
                        {
                            continue;
                        }
                        guildTasks.Add(client.serverStreamWriter.WriteAsync(request));
                    }
                    await Task.WhenAll(guildTasks);
                    break;
            }
            return new Empty();
        }

        /// <summary>
        /// Parses the customer_guid the message carries; a malformed or empty one is InvalidArgument. Session auth is gated
        /// like Chat and Party (on unless OWS_GRPC_REQUIRE_SESSION_AUTH is "false").
        /// When it is on, the customerguid/usersessionguid metadata must resolve to a live session of that same customer,
        /// and, when characterName is given, the session's selected character must be that character.
        /// </summary>
        private async Task<Guid> ResolveCustomerGuid(string requestCustomerGuid, ServerCallContext context, string characterName)
        {
            if (!Guid.TryParse(requestCustomerGuid, out Guid customerGuid) || customerGuid == Guid.Empty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid customer_guid is required."));
            }

            if (GrpcSessionAuth.IsRequired && !await IsSessionAuthorized(context, customerGuid, characterName))
            {
                GrpcSessionAuth.LogRejected(context, "guild");
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for guild."));
            }

            return customerGuid;
        }

        private async Task<bool> IsSessionAuthorized(ServerCallContext context, Guid requestCustomerGuid, string characterName)
        {
            if (!GrpcSessionAuth.TryReadCredentials(context, out Guid metaCustomerGuid, out Guid userSessionGuid))
            {
                return false;
            }

            // The message may only speak for the customer the session belongs to.
            if (metaCustomerGuid != requestCustomerGuid)
            {
                return false;
            }

            var session = await _usersRepository.GetUserSession(metaCustomerGuid, userSessionGuid);
            if (session == null || !session.UserGuid.HasValue)
            {
                return false;
            }

            return characterName == null
                || string.Equals(session.SelectedCharacterName?.Trim(), characterName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

    }

    public class ClientInfo
    {
        public string Id { get; set; }
        public string UserName { get; set; }
        public IServerStreamWriter<GuildToSend> serverStreamWriter { get; set; }
    }
}