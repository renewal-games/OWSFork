using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GuildServiceApp.Protos;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSGuild.Service;
using OWSShared.Grpc;
using OWSShared.Implementations;

namespace OWSTests.Guild
{
    /// <summary>Doubles shared by the guild storage tests.</summary>
    internal static class GuildStorageTestDoubles
    {
        public static GuildService CreateService(IGuildStorageRepository storage, string sessionCharacter)
        {
            var users = DispatchProxy.Create<IUsersRepository, SessionStub>();
            ((SessionStub)(object)users).Session = sessionCharacter == null
                ? null
                : new GetUserSession { UserGuid = Guid.NewGuid(), SelectedCharacterName = sessionCharacter };
            return new GuildService(DispatchProxy.Create<ICharactersRepository, Unused>(), users, new HeaderCustomerGUID(), storage);
        }

        public static GuildToSend StorageMessage(Guid customer, Guid guildGuid, bool deposit, string itemId, int quantity,
            params string[] claimedMembers)
        {
            var message = new GuildToSend
            {
                CustomerGuid = customer.ToString(),
                GuildAction = deposit ? GuildAction.MessageTypeStorageDeposit : GuildAction.MessageTypeStorageWithdraw,
                GuildInfo = new GuildInfo { GuildGuid = guildGuid.ToString("N") },
                GuildBank = new GuildStorage()
            };
            message.GuildBank.GuildStorageItems.Add(new GuildStorageItem { GuildItemId = itemId, GuildItemQuantity = quantity, GuildItemCustomData = "" });
            foreach (string name in claimedMembers)
            {
                message.GuildMembers.Add(new GuildMemberInfo { CharName = name });
            }
            return message;
        }

        /// <summary>Adds a registered guild stream; dispose to remove it from the static client list.</summary>
        public static RegisteredStream Register(Guid customer, string userName)
        {
            var stream = new RegisteredStream(new ClientInfo
            {
                Id = Guid.NewGuid().ToString(),
                UserName = userName,
                CustomerGuid = customer
            });
            lock (GuildService._guildClients)
            {
                GuildService._guildClients.Add(stream.Client);
            }
            return stream;
        }

        public sealed class RegisteredStream : IServerStreamWriter<GuildToSend>, IDisposable
        {
            public RegisteredStream(ClientInfo client)
            {
                Client = client;
                client.serverStreamWriter = this;
            }

            public ClientInfo Client { get; }
            public List<GuildToSend> Written { get; } = new List<GuildToSend>();
            public WriteOptions WriteOptions { get; set; }

            public Task WriteAsync(GuildToSend message)
            {
                lock (Written)
                {
                    Written.Add(message.Clone());
                }
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                lock (GuildService._guildClients)
                {
                    GuildService._guildClients.Remove(Client);
                }
            }
        }

        public class SessionStub : DispatchProxy
        {
            public GetUserSession Session { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IUsersRepository.GetUserSession))
                {
                    return Task.FromResult(Session);
                }

                throw new NotSupportedException(targetMethod.Name);
            }
        }

        public class Unused : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                throw new NotSupportedException(targetMethod.Name);
        }

        public sealed class CallContext : ServerCallContext
        {
            private readonly Metadata _headers = new Metadata();

            public CallContext(Guid customerGuid)
            {
                _headers.Add(GrpcSessionAuth.CustomerGuidHeader, customerGuid.ToString());
                _headers.Add(GrpcSessionAuth.UserSessionGuidHeader, Guid.NewGuid().ToString());
            }

            protected override string MethodCore => "/guild.GuildApp/SendGuildMsg";
            protected override string HostCore => "localhost";
            protected override string PeerCore => "ipv4:127.0.0.1:1";
            protected override DateTime DeadlineCore => DateTime.MaxValue;
            protected override Metadata RequestHeadersCore => _headers;
            protected override CancellationToken CancellationTokenCore => CancellationToken.None;
            protected override Metadata ResponseTrailersCore { get; } = new Metadata();
            protected override Status StatusCore { get; set; }
            protected override WriteOptions WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore => null;

            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
                throw new NotSupportedException();

            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        }
    }
}
