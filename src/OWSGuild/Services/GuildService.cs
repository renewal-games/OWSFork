using Grpc.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Grpc;
using OWSShared.Options;
using Microsoft.Extensions.Options;
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
        private readonly IGuildStorageRepository _guildStorageRepository;
        private readonly EconomyOptions _economyOptions;

        public GuildService(ICharactersRepository charactersRepository,
            IUsersRepository usersRepository,
            IHeaderCustomerGUID customerGuid,
            IGuildStorageRepository guildStorageRepository,
            IOptions<EconomyOptions> economyOptions)
        {
            _charactersRepository = charactersRepository;
            _usersRepository = usersRepository;
            _customerGuid = customerGuid;
            _guildStorageRepository = guildStorageRepository;
            _economyOptions = economyOptions.Value;
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
                CustomerGuid = customerGuid,
                serverStreamWriter = responseStream
            });

            _customerGuid.CustomerGUID = customerGuid;
            GetInitialGuildSettingsRequest getInitalGuildSettings = new GetInitialGuildSettingsRequest();
            getInitalGuildSettings.SetData(_charactersRepository, _customerGuid, userName);

            GuildToSend guildInformation = await getInitalGuildSettings.Handle();
            if (!guildInformation.GuildInfo.GuildGuid.IsNullOrEmpty() && Guid.TryParse(guildInformation.GuildInfo.GuildGuid, out Guid guildGuid))
            {
                guildInformation.GuildBank.GuildCurrentStoredItems.Add(
                    ToProto(await _guildStorageRepository.GetStoredItems(customerGuid, guildGuid)));
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
            (Guid customerGuid, string sessionCharacter) = await ResolveCaller(request.CustomerGuid, context, null);
            _customerGuid.CustomerGUID = customerGuid;
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
                case GuildAction.MessageTypeStorageWithdraw:
                    await TransferStorage(request, customerGuid, sessionCharacter);
                    break;
            }
            return new Empty();
        }

        /// <summary>
        /// Deposit or withdraw for the session's own character (code issue #188). The request only names the guild and
        /// the stacks to move: the guild must be the character's own, every quantity must be positive, and the items
        /// move between the character's bag and the persisted guild storage in one transaction, so a deposit has to come
        /// out of the bag and a withdrawal can never exceed the stored balance. The update goes to the guild's members
        /// as the database lists them; request.GuildMembers is ignored.
        /// </summary>
        private async Task TransferStorage(GuildToSend request, Guid customerGuid, string sessionCharacter)
        {
            // A transfer rewrites the bag in the database while the zone server still holds the old bag in memory. Only
            // the economy-revision guard stops that stale bag being saved back over the transfer (re-minting a deposit,
            // erasing a withdrawal): the transfer bumps EconomyRevision, and with RequireExpectedRevision on, every bag
            // save must carry the revision it was computed from, so the stale save is refused and the zone server has to
            // resync. With it off, revision-less saves still win, so storage stays closed. This must be set together with
            // the character persistence API's OWSEconomyConfig, after the zone server sends ExpectedRevision
            // (docs/mvp-hardening-spec.md item 2).
            if (!_economyOptions.RequireExpectedRevision)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Guild storage is disabled until inventory saves require an economy revision (OWSEconomyConfig:RequireExpectedRevision)."));
            }

            // With session auth off there is no trusted character to debit or credit.
            if (string.IsNullOrWhiteSpace(sessionCharacter))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Sign in with a selected character to use guild storage."));
            }

            if (request.GuildInfo == null || !Guid.TryParse(request.GuildInfo.GuildGuid, out Guid guildGuid) || guildGuid == Guid.Empty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid guild_guid is required."));
            }

            if (request.GuildBank == null || request.GuildBank.GuildStorageItems.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "No guild storage items to move."));
            }

            var items = new List<GuildStorageTransferItem>();
            foreach (GuildStorageItem item in request.GuildBank.GuildStorageItems)
            {
                if (item.GuildItemQuantity <= 0)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "guild_item_quantity must be positive."));
                }

                if (string.IsNullOrWhiteSpace(item.GuildItemId))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "guild_item_id is required."));
                }

                items.Add(new GuildStorageTransferItem
                {
                    ItemIDTag = item.GuildItemId,
                    Quantity = item.GuildItemQuantity,
                    CustomData = item.GuildItemCustomData ?? ""
                });
            }

            bool deposit = request.GuildAction == GuildAction.MessageTypeStorageDeposit;
            GuildStorageTransferResult result = await _guildStorageRepository.Transfer(customerGuid, sessionCharacter, guildGuid, deposit, items);
            if (!result.Success)
            {
                throw new RpcException(result.ReasonCode switch
                {
                    "not_member" => new Status(StatusCode.PermissionDenied, "Your character is not a member of this guild."),
                    "bad_request" => new Status(StatusCode.InvalidArgument, "Invalid guild storage request."),
                    "insufficient_items" => new Status(StatusCode.FailedPrecondition, "Your inventory does not hold those items."),
                    "insufficient_storage" => new Status(StatusCode.FailedPrecondition, "Guild storage does not hold those items."),
                    "storage_full" => new Status(StatusCode.FailedPrecondition, "Guild storage cannot hold that many."),
                    "inventory_full" => new Status(StatusCode.FailedPrecondition, "Your inventory has no free slot."),
                    "no_inventory" => new Status(StatusCode.FailedPrecondition, "Your character has no inventory."),
                    _ => new Status(StatusCode.Internal, "Guild storage transfer failed.")
                });
            }

            var update = new GuildToSend
            {
                CustomerGuid = customerGuid.ToString(),
                GuildAction = request.GuildAction,
                // Same dash-less form GetInitialGuildSettings sends.
                GuildInfo = new GuildInfo { GuildGuid = guildGuid.ToString("N") },
                GuildBank = new GuildStorage()
            };
            update.GuildBank.GuildStorageItems.Add(items.Select(i => new GuildStorageItem
            {
                GuildItemId = i.ItemIDTag,
                GuildItemQuantity = i.Quantity,
                GuildItemCustomData = i.CustomData
            }));
            update.GuildBank.GuildCurrentStoredItems.Add(ToProto(result.StoredItems));
            update.GuildMembers.Add(result.Members.Select(m => new GuildMemberInfo
            {
                CharName = m.CharName ?? "",
                GuildRank = (GuildRank)m.GuildRank
            }));

            var members = new HashSet<string>(result.Members.Select(m => m.CharName), StringComparer.OrdinalIgnoreCase);
            List<Task> writes = _guildClients
                .ToArray()
                .Where(c => c.CustomerGuid == customerGuid && c.UserName != null && members.Contains(c.UserName))
                .Select(c => c.serverStreamWriter.WriteAsync(update))
                .ToList();
            await Task.WhenAll(writes);
        }

        private static IEnumerable<GuildStorageItem> ToProto(IEnumerable<GuildStorageEntry> stored) =>
            stored.Select(s => new GuildStorageItem
            {
                GuildItemId = s.ItemIDTag,
                GuildItemQuantity = s.Quantity,
                GuildItemCustomData = s.CustomData ?? ""
            });

        /// <summary>
        /// Parses the customer_guid the message carries; a malformed or empty one is InvalidArgument. Session auth is gated
        /// like Chat and Party (on unless OWS_GRPC_REQUIRE_SESSION_AUTH is "false").
        /// When it is on, the customerguid/usersessionguid metadata must resolve to a live session of that same customer,
        /// and, when characterName is given, the session's selected character must be that character.
        /// </summary>
        private async Task<Guid> ResolveCustomerGuid(string requestCustomerGuid, ServerCallContext context, string characterName) =>
            (await ResolveCaller(requestCustomerGuid, context, characterName)).CustomerGuid;

        /// <summary>
        /// <see cref="ResolveCustomerGuid"/>, plus the session's selected character: null when session auth is off, since
        /// then nothing about the caller is verified.
        /// </summary>
        private async Task<(Guid CustomerGuid, string SessionCharacter)> ResolveCaller(string requestCustomerGuid, ServerCallContext context, string characterName)
        {
            if (!Guid.TryParse(requestCustomerGuid, out Guid customerGuid) || customerGuid == Guid.Empty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid customer_guid is required."));
            }

            if (!GrpcSessionAuth.IsRequired)
            {
                return (customerGuid, null);
            }

            GetUserSession session = await GetAuthorizedSession(context, customerGuid, characterName);
            if (session == null)
            {
                GrpcSessionAuth.LogRejected(context, "guild");
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for guild."));
            }

            return (customerGuid, session.SelectedCharacterName?.Trim());
        }

        private async Task<GetUserSession> GetAuthorizedSession(ServerCallContext context, Guid requestCustomerGuid, string characterName)
        {
            if (!GrpcSessionAuth.TryReadCredentials(context, out Guid metaCustomerGuid, out Guid userSessionGuid))
            {
                return null;
            }

            // The message may only speak for the customer the session belongs to.
            if (metaCustomerGuid != requestCustomerGuid)
            {
                return null;
            }

            var session = await _usersRepository.GetUserSession(metaCustomerGuid, userSessionGuid);
            if (session == null || !session.UserGuid.HasValue)
            {
                return null;
            }

            bool characterMatches = characterName == null
                || string.Equals(session.SelectedCharacterName?.Trim(), characterName.Trim(), StringComparison.OrdinalIgnoreCase);
            return characterMatches ? session : null;
        }

    }

    public class ClientInfo
    {
        public string Id { get; set; }
        public string UserName { get; set; }

        /// <summary>The tenant the stream registered under; storage updates go only to streams of the same tenant.</summary>
        public Guid CustomerGuid { get; set; }

        public IServerStreamWriter<GuildToSend> serverStreamWriter { get; set; }
    }
}