using Grpc.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PartyServiceApp.Protos;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Grpc;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using OWSParty.Requests.Party;

namespace OWSParty.Service
{
    public class PartyService : PartyApp.PartyAppBase
    {
        public static ConcurrentDictionary<string, ClientInfo> _partyClients = new ConcurrentDictionary<string, ClientInfo>(StringComparer.OrdinalIgnoreCase);

        private const string SnapshotHeader = "party-snapshot-v1";
        private const string FormRecoveryHeader = "party-form-recovery-v1";
        private const string MutationIdHeader = "party-mutation-id";
        private const string RegistrationHeader = "party-mutation-registration";
        private const string InviteeRegistrationHeader = "party-invitee-registration";

        private readonly ICharactersRepository _charactersRepository;
        private readonly IUsersRepository _usersRepository;
        private readonly IHeaderCustomerGUID _customerGuid;
        private readonly ILogger<PartyService> _logger;

        public PartyService(ICharactersRepository charactersRepository,
            IUsersRepository usersRepository,
            IHeaderCustomerGUID customerGuid,
            ILogger<PartyService> logger)
        {
            _charactersRepository = charactersRepository;
            _usersRepository = usersRepository;
            _customerGuid = customerGuid;
            _logger = logger;
        }

        public override async Task RegisterParty(PartyRegister request, IServerStreamWriter<PartyToSend> responseStream, ServerCallContext context)
        {
            // Bound concurrent party streams (each holds a long-lived registration).
            using GrpcStreamGuard.ConnectionLease lease = GrpcStreamGuard.TryAcquireConnection();
            if (lease == null)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Party connection limit reached."));
            }

            if (!Guid.TryParse(request.CustomerGuid, out Guid parsedCustomerGuid) || parsedCustomerGuid == Guid.Empty)
            {
                return;
            }

            string id = request.UserId;
            string userName = request.UserName?.Trim();

            if (string.IsNullOrWhiteSpace(userName))
            {
                return;
            }

            Guid? mutationId = ReadGuidHeader(context, MutationIdHeader);
            Guid? registration = ReadGuidHeader(context, RegistrationHeader);
            bool usesSnapshots = context.RequestHeaders.FirstOrDefault(entry => entry.Key == SnapshotHeader)?.Value == "1";
            bool recoversForms = context.RequestHeaders.FirstOrDefault(entry => entry.Key == FormRecoveryHeader)?.Value == "1";
            if (recoversForms && !registration.HasValue)
            {
                ThrowInvalidArgument("Party form recovery requires a registration.");
            }
            if ((mutationId.HasValue || registration.HasValue) && !usesSnapshots)
            {
                ThrowInvalidArgument("Party mutation recovery requires snapshot support.");
            }

            if (GrpcSessionAuth.IsRequired || mutationId.HasValue || registration.HasValue)
            {
                userName = await GetAuthorizedCharacter(context, parsedCustomerGuid, userName);
                if (userName == null)
                {
                    GrpcSessionAuth.LogRejected(context, "party");
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for party registration."));
                }
            }

            long registrationOrder = 0;
            if (registration.HasValue)
            {
                registrationOrder = await _charactersRepository.RegisterPartyMutationParticipant(
                    parsedCustomerGuid, userName, registration.Value, context.CancellationToken);
                if (registrationOrder == 0)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "A newer party registration is active."));
                }
            }

            var clientInfo = new ClientInfo
            {
                RegistrationOrder = registrationOrder,
                Id = id,
                UserName = userName,
                ServerStreamWriter = responseStream,
                ReadCurrentRoster = usesSnapshots ? () => _charactersRepository.GetInitialPartySettings(parsedCustomerGuid, userName) : null
            };
            context.CancellationToken.ThrowIfCancellationRequested();
            ClientInfo active = _partyClients.AddOrUpdate(userName, clientInfo, (_, current) =>
                registrationOrder != 0 && current.RegistrationOrder > registrationOrder ? current : clientInfo);
            if (active != clientInfo)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "A newer party registration is active."));
            }

            try
            {
                PartyAction snapshotAction = PartyAction.MessageTypeInitialRestore;
                if (mutationId.HasValue)
                {
                    bool committed = await _charactersRepository.ResolvePartyMutation(parsedCustomerGuid, mutationId.Value, userName);
                    snapshotAction = committed ? PartyAction.MessageTypeMutationCommitted : PartyAction.MessageTypeMutationCanceled;
                }

                PartyToSend party;
                do
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    party = await _charactersRepository.GetInitialPartySettings(parsedCustomerGuid, userName);
                }
                while (!clientInfo.TryFinishSnapshot());

                if (usesSnapshots)
                {
                    party ??= new PartyToSend { CustomerGuid = parsedCustomerGuid.ToString() };
                    party.PartyAction = snapshotAction;
                    await responseStream.WriteAsync(party);
                }
                else if (party != null && party.PartyMembers.Count != 0)
                {
                    await responseStream.WriteAsync(party);
                }

                if (recoversForms)
                {
                    foreach (Guid canceled in await _charactersRepository.GetCanceledPartyFormCreates(
                        parsedCustomerGuid, userName, registration.Value))
                    {
                        await responseStream.WriteAsync(new PartyToSend
                        {
                            CustomerGuid = parsedCustomerGuid.ToString(),
                            PartyAction = PartyAction.MessageTypeFormCanceled,
                            PartyInfo = new PartyInfo { PartyGuid = canceled.ToString() }
                        });
                    }
                }

                clientInfo.ReleaseSnapshotWriter();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                ((ICollection<KeyValuePair<string, ClientInfo>>)_partyClients).Remove(new(userName, clientInfo));
                clientInfo.ReleaseSnapshotWriter();
            }
        }

        private async Task<string> GetAuthorizedCharacter(ServerCallContext context, Guid requestCustomerGuid, string userName)
        {
            if (!GrpcSessionAuth.TryReadCredentials(context, out Guid metaCustomerGuid, out Guid userSessionGuid))
            {
                return null;
            }

            // The credential's customer must match the one the registration claims.
            if (metaCustomerGuid != requestCustomerGuid)
            {
                return null;
            }

            var session = await _usersRepository.GetUserSession(metaCustomerGuid, userSessionGuid);
            if (session == null || !session.UserGuid.HasValue)
            {
                return null;
            }

            // Bind the stream identity to the session: the registering character must be the
            // session's selected character, so a valid session can't register as someone else.
            string selectedName = session.SelectedCharacterName?.Trim();
            return string.Equals(selectedName, userName, StringComparison.OrdinalIgnoreCase) ? selectedName : null;
        }

        private static Guid? ReadGuidHeader(ServerCallContext context, string header)
        {
            string value = context.RequestHeaders.FirstOrDefault(entry => entry.Key == header)?.Value;
            if (value == null)
            {
                return null;
            }
            if (!Guid.TryParse(value, out Guid id) || id == Guid.Empty)
            {
                ThrowInvalidArgument($"A valid GUID is required in {header}.");
            }
            return id;
        }

        public override async Task<Empty> SendPartyMsg(PartyToSend request, ServerCallContext context)
        {
            if (request == null)
            {
                ThrowInvalidArgument("Party request is required.");
            }

            // Same gate as RegisterParty: when session auth is on, the character acting in the request
            // body must be the session's selected character, so a caller can't kick, dismiss or invite
            // on someone else's behalf by naming them.
            Guid? mutationId = ReadGuidHeader(context, MutationIdHeader);
            Guid? actorRegistration = ReadGuidHeader(context, RegistrationHeader);
            Guid? inviteeRegistration = ReadGuidHeader(context, InviteeRegistrationHeader);
            if ((!mutationId.HasValue && actorRegistration.HasValue) || (!actorRegistration.HasValue && inviteeRegistration.HasValue))
            {
                ThrowInvalidArgument("Party registration requires a mutation ID and an acting participant.");
            }
            bool isFormControl = request.PartyAction is PartyAction.MessageTypeFormPrepare or PartyAction.MessageTypeFormAcknowledge;
            bool requiresFormIntent = context.RequestHeaders.FirstOrDefault(entry => entry.Key == FormRecoveryHeader)?.Value == "1";
            if (requiresFormIntent && request.PartyAction != PartyAction.MessageTypeCreate)
            {
                ThrowInvalidArgument("A party form intent applies to create.");
            }
            if (isFormControl || requiresFormIntent)
            {
                if (!mutationId.HasValue || !actorRegistration.HasValue || inviteeRegistration.HasValue
                    || request.PartyMembers.Count != 1 || !request.PartyMembers[0].PartyLeader)
                {
                    ThrowInvalidArgument("Party form recovery requires one registered participant and a mutation ID.");
                }
            }
            if (!isFormControl && mutationId.HasValue
                && request.PartyAction is not (PartyAction.MessageTypeCreate or PartyAction.MessageTypeAdd))
            {
                ThrowInvalidArgument("Party mutation IDs apply to create, add, and form recovery.");
            }

            if (GrpcSessionAuth.IsRequired || mutationId.HasValue)
            {
                Guid.TryParse(request.CustomerGuid, out Guid requestCustomerGuid);
                string actorName = GetActingCharacter(request)?.CharName?.Trim();
                string authorizedName = string.IsNullOrWhiteSpace(actorName)
                    ? null
                    : await GetAuthorizedCharacter(context, requestCustomerGuid, actorName);
                if (authorizedName == null)
                {
                    GrpcSessionAuth.LogRejected(context, "party");
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for party action."));
                }

                if (mutationId.HasValue)
                {
                    GetActingCharacter(request).CharName = authorizedName;
                }
            }

            switch (request.PartyAction)
            {
                case PartyAction.MessageTypeAsk:
                    await ExecutePartyAction(request.PartyAction, () => HandleAsk(request));
                    break;

                case PartyAction.MessageTypeCreate:
                case PartyAction.MessageTypeAdd:
                    await ExecutePartyAction(request.PartyAction,
                        () => HandleCreateOrAdd(request, mutationId, actorRegistration, inviteeRegistration, requiresFormIntent));
                    break;

                case PartyAction.MessageTypeFormPrepare:
                    await ExecutePartyAction(request.PartyAction, () => _charactersRepository.PreparePartyFormCreate(
                        ValidateCustomerGuid(request), request.PartyMembers[0].CharName, actorRegistration.Value, mutationId.Value));
                    break;

                case PartyAction.MessageTypeFormAcknowledge:
                    await ExecutePartyAction(request.PartyAction, () => _charactersRepository.AcknowledgePartyFormCancellation(
                        ValidateCustomerGuid(request), request.PartyMembers[0].CharName, actorRegistration.Value, mutationId.Value));
                    break;

                case PartyAction.MessageTypeKick:
                case PartyAction.MessageTypeLeave:
                case PartyAction.MessageTypeDismiss:
                    await ExecutePartyAction(request.PartyAction, () => HandleRemoveOrDismiss(request));
                    break;

                case PartyAction.MessageTypeMakeLead:
                    await ExecutePartyAction(request.PartyAction, () => HandleMakeLeader(request));
                    break;

                case PartyAction.MessageTypeUpdateInfo:
                    await ExecutePartyAction(request.PartyAction, () => HandleUpdateInfo(request));
                    break;

                case PartyAction.MessageTypeRaid:
                case PartyAction.MessageTypeLoot:
                    _logger.LogWarning("Party action {PartyAction} is not implemented.", request.PartyAction);
                    ThrowInvalidArgument($"{request.PartyAction} is not implemented.");
                    break;

                default:
                    _logger.LogWarning("Unsupported party action {PartyAction}.", request.PartyAction);
                    ThrowInvalidArgument($"Unsupported party action: {request.PartyAction}.");
                    break;
            }

            return new Empty();
        }

        private async Task HandleAsk(PartyToSend request)
        {
            ValidateAsk(request);

            PartyMemberInfo target = request.PartyMembers[1];
            if (!_partyClients.TryGetValue(target.CharName, out ClientInfo client) || client == null)
            {
                _logger.LogWarning("Party ask for {TargetCharName} dropped: the target has no registered party stream.", target.CharName);
                return;
            }

            await client.WriteAsync(request);
        }

        private async Task HandleCreateOrAdd(PartyToSend request, Guid? mutationId, Guid? actorRegistration,
            Guid? inviteeRegistration, bool requiresFormIntent)
        {
            Guid customerGuid = ValidateCustomerGuid(request);

            if (request.PartyAction == PartyAction.MessageTypeCreate)
            {
                ValidateCreate(request);
            }
            else
            {
                ValidateAdd(request);
            }

            _customerGuid.CustomerGUID = customerGuid;
            CreatePartyOrAddMemberRequest createPartyOrAddMemberRequest = new CreatePartyOrAddMemberRequest();
            createPartyOrAddMemberRequest.SetData(_charactersRepository, _customerGuid, request);

            PartyToSend partyInformation = await createPartyOrAddMemberRequest.Handle(mutationId, actorRegistration, inviteeRegistration, requiresFormIntent);
            if (partyInformation != null)
            {
                await BroadcastToMembers(partyInformation.PartyMembers, partyInformation);
            }
        }

        private async Task HandleRemoveOrDismiss(PartyToSend request)
        {
            Guid customerGuid = ValidateCustomerGuid(request);

            switch (request.PartyAction)
            {
                case PartyAction.MessageTypeKick:
                    ValidateKick(request);
                    break;
                case PartyAction.MessageTypeLeave:
                    ValidateLeave(request);
                    break;
                case PartyAction.MessageTypeDismiss:
                    ValidateDismiss(request);
                    break;
            }

            _customerGuid.CustomerGUID = customerGuid;
            LeavePartyRequest leavePartyRequest = new LeavePartyRequest();
            leavePartyRequest.SetData(_charactersRepository, _customerGuid, request);

            PartyToSend party = await leavePartyRequest.Handle();
            PartyToSend removedParty = new PartyToSend
            {
                PartyAction = request.PartyAction
            };

            IEnumerable<PartyMemberInfo> removedPartyMembers = request.PartyAction switch
            {
                PartyAction.MessageTypeDismiss => party.PartyMembers,
                PartyAction.MessageTypeKick => request.PartyMembers.Where(partyMember => !partyMember.PartyLeader),
                _ => request.PartyMembers
            };

            await BroadcastToMembers(removedPartyMembers, removedParty);

            if (request.PartyAction == PartyAction.MessageTypeDismiss)
            {
                return;
            }

            await BroadcastToMembers(party.PartyMembers, party);
        }

        // Only party_info.party_name is applied. Description, exp and loot distribution reach the same
        // party through their own validated PUT endpoints and stay authoritative there, so a rename
        // cannot clobber a concurrent change to one of them.
        private async Task HandleUpdateInfo(PartyToSend request)
        {
            Guid customerGuid = ValidateCustomerGuid(request);
            ValidateUpdateInfo(request);

            _customerGuid.CustomerGUID = customerGuid;
            PartyMemberInfo leader = GetLeader(request);

            PartyToSend party = await _charactersRepository.UpdatePartyName(
                customerGuid,
                Guid.Parse(request.PartyInfo.PartyGuid),
                leader.CharName,
                leader.CharGuid,
                request.PartyInfo.PartyName);

            await BroadcastToMembers(party.PartyMembers, party);
        }

        private async Task HandleMakeLeader(PartyToSend request)
        {
            Guid customerGuid = ValidateCustomerGuid(request);
            ValidateMakeLeader(request);

            _customerGuid.CustomerGUID = customerGuid;
            ChangePartyLeaderRequest changePartyLeaderRequest = new ChangePartyLeaderRequest();
            changePartyLeaderRequest.SetData(_charactersRepository, _customerGuid, request);

            PartyToSend partyLeaderChanged = await changePartyLeaderRequest.Handle();
            await BroadcastToMembers(partyLeaderChanged.PartyMembers, partyLeaderChanged);
        }

        private async Task ExecutePartyAction(PartyAction partyAction, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (RpcException ex)
            {
                _logger.LogWarning("Party action {PartyAction} rejected: {StatusCode} {Detail}", partyAction, ex.StatusCode, ex.Status.Detail);
                throw;
            }
            catch (Exception ex) when (IsValidationException(ex))
            {
                string reason = GetClientSafeValidationMessage(ex);
                _logger.LogWarning(ex, "Party action {PartyAction} rejected: {Reason}", partyAction, reason);
                throw new RpcException(new Status(StatusCode.InvalidArgument, reason));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Party action {PartyAction} failed.", partyAction);
                throw new RpcException(new Status(StatusCode.Internal, "Party action failed."));
            }
        }

        private async Task BroadcastToMembers(IEnumerable<PartyMemberInfo> partyMembers, PartyToSend message)
        {
            await BroadcastPartyUpdate(partyMembers, message, _logger);
        }

        // A member with no registered RegisterParty stream is skipped, and the caller still sees the
        // action succeed — the roster simply never reaches them. Log it: silence here is the reason a
        // working party action looks like nothing happened.
        public static async Task BroadcastPartyUpdate(IEnumerable<PartyMemberInfo> partyMembers, PartyToSend message, ILogger logger = null)
        {
            List<Task> partyTasks = new List<Task>();
            List<string> unreachableMembers = new List<string>();

            foreach (PartyMemberInfo partyMember in partyMembers)
            {
                if (string.IsNullOrWhiteSpace(partyMember.CharName))
                {
                    continue;
                }

                if (!_partyClients.TryGetValue(partyMember.CharName, out ClientInfo client) || client == null)
                {
                    unreachableMembers.Add(partyMember.CharName);
                    continue;
                }

                partyTasks.Add(client.WriteAsync(message, rosterUpdate: true));
            }

            if (unreachableMembers.Count > 0)
            {
                logger?.LogWarning(
                    "Party {PartyAction} update not delivered to {UnreachableCount} of {MemberCount} member(s) with no registered party stream: {Members}.",
                    message.PartyAction,
                    unreachableMembers.Count,
                    unreachableMembers.Count + partyTasks.Count,
                    string.Join(", ", unreachableMembers));
            }

            await Task.WhenAll(partyTasks);
        }

        private static Guid ValidateCustomerGuid(PartyToSend request)
        {
            if (!Guid.TryParse(request.CustomerGuid, out Guid customerGuid) || customerGuid == Guid.Empty)
            {
                ThrowInvalidArgument("A valid customer_guid is required.");
            }

            return customerGuid;
        }

        private static void ValidatePartyGuid(PartyToSend request)
        {
            if (request.PartyInfo == null || !Guid.TryParse(request.PartyInfo.PartyGuid, out Guid partyGuid) || partyGuid == Guid.Empty)
            {
                ThrowInvalidArgument("A valid party_info.party_guid is required.");
            }
        }

        private static void ValidateAsk(PartyToSend request)
        {
            if (request.PartyMembers.Count < 2)
            {
                ThrowInvalidArgument("Ask requires requester and target party members.");
            }

            RequireMemberName(request.PartyMembers[0], "requester");
            RequireMemberName(request.PartyMembers[1], "target");
        }

        private static void ValidateCreate(PartyToSend request)
        {
            ValidateCustomerGuid(request);

            PartyMemberInfo leader = GetLeader(request);
            if (leader == null)
            {
                ThrowInvalidArgument("Create requires a party member with party_leader set to true.");
            }

            RequireMemberName(leader, "leader");
            RequireDistinctMemberNames(request.PartyMembers);
        }

        private static void ValidateAdd(PartyToSend request)
        {
            ValidateCustomerGuid(request);
            ValidatePartyGuid(request);

            PartyMemberInfo leader = GetLeader(request);
            if (leader == null)
            {
                ThrowInvalidArgument("Add requires the acting leader with party_leader set to true.");
            }

            RequireMemberName(leader, "leader");

            List<PartyMemberInfo> targets = GetNonLeaderMembers(request).ToList();
            if (!targets.Any())
            {
                ThrowInvalidArgument("Add requires at least one target member.");
            }

            foreach (PartyMemberInfo target in targets)
            {
                RequireMemberName(target, "target");
                RequireDifferentCharacters(leader, target, "Leader cannot add themself.");
            }

            RequireDistinctMemberNames(request.PartyMembers);
        }

        private static void ValidateKick(PartyToSend request)
        {
            ValidateCustomerGuid(request);
            ValidatePartyGuid(request);

            PartyMemberInfo leader = GetLeader(request);
            if (leader == null)
            {
                ThrowInvalidArgument("Kick requires the acting leader with party_leader set to true.");
            }

            RequireMemberName(leader, "leader");

            List<PartyMemberInfo> targets = GetNonLeaderMembers(request).ToList();
            if (!targets.Any())
            {
                ThrowInvalidArgument("Kick requires at least one target member.");
            }

            foreach (PartyMemberInfo target in targets)
            {
                RequireMemberName(target, "target");
                RequireDifferentCharacters(leader, target, "Leader cannot kick themself.");
            }

            RequireDistinctMemberNames(request.PartyMembers);
        }

        private static void ValidateLeave(PartyToSend request)
        {
            ValidateCustomerGuid(request);
            ValidatePartyGuid(request);

            if (request.PartyMembers.Count != 1)
            {
                ThrowInvalidArgument("Leave requires exactly the leaving member.");
            }

            RequireMemberName(request.PartyMembers[0], "member");
        }

        private static void ValidateDismiss(PartyToSend request)
        {
            ValidateCustomerGuid(request);
            ValidatePartyGuid(request);

            PartyMemberInfo leader = GetLeader(request);
            if (leader == null)
            {
                ThrowInvalidArgument("Dismiss requires the acting leader with party_leader set to true.");
            }

            RequireMemberName(leader, "leader");
        }

        private static void ValidateMakeLeader(PartyToSend request)
        {
            ValidateCustomerGuid(request);
            ValidatePartyGuid(request);

            PartyMemberInfo leader = GetLeader(request);
            if (leader == null)
            {
                ThrowInvalidArgument("Make leader requires the current leader with party_leader set to true.");
            }

            RequireMemberName(leader, "leader");

            List<PartyMemberInfo> targets = GetNonLeaderMembers(request).ToList();
            if (targets.Count != 1)
            {
                ThrowInvalidArgument("Make leader requires exactly one target member.");
            }

            PartyMemberInfo target = targets[0];
            RequireMemberName(target, "target");
            RequireDifferentCharacters(leader, target, "Leader cannot grant leader to themself.");
            RequireDistinctMemberNames(request.PartyMembers);
        }

        private static void ValidateUpdateInfo(PartyToSend request)
        {
            ValidateCustomerGuid(request);
            ValidatePartyGuid(request);

            PartyMemberInfo leader = GetLeader(request);
            if (leader == null)
            {
                ThrowInvalidArgument("Update info requires the acting leader with party_leader set to true.");
            }

            RequireMemberName(leader, "leader");

            if (string.IsNullOrWhiteSpace(request.PartyInfo?.PartyName))
            {
                ThrowInvalidArgument("A party_info.party_name is required.");
            }
        }

        // The character performing the action: the requester for Ask, the leaving member for Leave,
        // and the leader for every leader action (matching who the handlers and repository act as).
        private static PartyMemberInfo GetActingCharacter(PartyToSend request)
        {
            return request.PartyAction switch
            {
                PartyAction.MessageTypeAsk or PartyAction.MessageTypeLeave => request.PartyMembers.FirstOrDefault(),
                _ => GetLeader(request)
            };
        }

        private static PartyMemberInfo GetLeader(PartyToSend request)
        {
            return request.PartyMembers.FirstOrDefault(partyMember => partyMember.PartyLeader);
        }

        private static IEnumerable<PartyMemberInfo> GetNonLeaderMembers(PartyToSend request)
        {
            return request.PartyMembers.Where(partyMember => !partyMember.PartyLeader);
        }

        private static void RequireMemberName(PartyMemberInfo member, string role)
        {
            if (member == null || string.IsNullOrWhiteSpace(member.CharName))
            {
                ThrowInvalidArgument($"A valid {role} char_name is required.");
            }
        }

        private static void RequireDifferentCharacters(PartyMemberInfo actor, PartyMemberInfo target, string message)
        {
            if (string.Equals(actor.CharName, target.CharName, StringComparison.OrdinalIgnoreCase))
            {
                ThrowInvalidArgument(message);
            }
        }

        private static void RequireDistinctMemberNames(IEnumerable<PartyMemberInfo> partyMembers)
        {
            bool hasDuplicate = partyMembers
                .Where(partyMember => !string.IsNullOrWhiteSpace(partyMember.CharName))
                .GroupBy(partyMember => partyMember.CharName, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1);

            if (hasDuplicate)
            {
                ThrowInvalidArgument("Party member char_name values must be unique within the request.");
            }
        }

        private static bool IsValidationException(Exception ex)
        {
            if (ex is ArgumentException || ex is InvalidOperationException)
            {
                return true;
            }

            string exceptionName = ex.GetType().FullName;
            if (!string.Equals(exceptionName, "Npgsql.PostgresException", StringComparison.Ordinal))
            {
                return false;
            }

            string sqlState = ex.GetType().GetProperty("SqlState")?.GetValue(ex)?.ToString();
            return sqlState == "P0001" || sqlState == "23505";
        }

        private static string GetClientSafeValidationMessage(Exception ex)
        {
            string message = ex.Message;
            int detailStart = message.IndexOf("DETAIL:", StringComparison.OrdinalIgnoreCase);
            if (detailStart > 0)
            {
                message = message.Substring(0, detailStart).Trim();
            }

            return string.IsNullOrWhiteSpace(message) ? "Invalid party request." : message;
        }

        private static void ThrowInvalidArgument(string message)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, message));
        }
    }

    public class ClientInfo
    {
        public long RegistrationOrder { get; init; }
        public string Id { get; set; }
        public string UserName { get; set; }
        public IServerStreamWriter<PartyToSend> ServerStreamWriter { get; set; }
        public Func<Task<PartyToSend>> ReadCurrentRoster { get; init; }

        private readonly SemaphoreSlim _writer = new SemaphoreSlim(0, 1);
        private readonly object _snapshotLock = new object();
        private bool _snapshotDirty;
        private bool _snapshotFinished;
        private int _snapshotWriterReleased;

        public bool TryFinishSnapshot()
        {
            lock (_snapshotLock)
            {
                if (_snapshotDirty)
                {
                    _snapshotDirty = false;
                    return false;
                }
                _snapshotFinished = true;
                return true;
            }
        }

        public void ReleaseSnapshotWriter()
        {
            if (Interlocked.Exchange(ref _snapshotWriterReleased, 1) == 0)
            {
                _writer.Release();
            }
        }

        public async Task WriteAsync(PartyToSend message, bool rosterUpdate = false)
        {
            lock (_snapshotLock)
            {
                if (rosterUpdate && !_snapshotFinished)
                {
                    _snapshotDirty = true;
                    return;
                }
            }

            await _writer.WaitAsync();
            try
            {
                // Party writes commit in database order, but their broadcasts can arrive out of order.
                if (rosterUpdate && ReadCurrentRoster != null)
                {
                    PartyToSend current = await ReadCurrentRoster();
                    if (current == null || current.PartyMembers.Count == 0)
                    {
                        message = new PartyToSend { CustomerGuid = message.CustomerGuid, PartyAction = PartyAction.MessageTypeLeave };
                    }
                    else
                    {
                        if (current.PartyInfo.PartyGuid != message.PartyInfo?.PartyGuid)
                        {
                            return;
                        }

                        current.PartyAction = message.PartyAction == PartyAction.MessageTypeCreate
                            ? PartyAction.MessageTypeCreate
                            : PartyAction.MessageTypeAdd;
                        message = current;
                    }
                }

                await ServerStreamWriter.WriteAsync(message);
            }
            finally
            {
                _writer.Release();
            }
        }
    }
}
