using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSData.Repositories.Interfaces;
using OWSInstanceManagement.Requests.Instance;
using OWSShared.Interfaces;
using OWSShared.Options;
using OWSShared.RequestPayloads;
using Xunit;

namespace OWSTests.InstanceManagement
{
    /// <summary>
    /// A failed zone instance lookup must not read as "no such instance". ZoneProcessExited takes an empty row for a
    /// deliberate stop, so a database error there would log a crash as expected, with no zone_exit audit row and no alert.
    /// </summary>
    public class ZoneInstanceLookupFailureTests
    {
        //Nothing listens on port 1, so every query fails to connect.
        private static InstanceManagementRepository UnreachableRepository() =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBBackend = "postgres",
                OWSDBConnectionString = "Host=127.0.0.1;Port=1;Username=ows;Password=ows;Database=ows;Timeout=5"
            }));

        [Fact]
        public async Task GetZoneInstance_Throws_WhenTheDatabaseIsUnreachable()
        {
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => UnreachableRepository().GetZoneInstance(Guid.NewGuid(), 1));
        }

        [Fact]
        public async Task GetServerInstanceFromPort_Throws_WhenTheDatabaseIsUnreachable()
        {
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => UnreachableRepository().GetServerInstanceFromPort(Guid.NewGuid(), "127.0.0.1", 7777));
        }

        [Fact]
        public async Task ZoneProcessExited_ReportsAnUnexpectedExit_WhenTheInstanceCannotBeRead()
        {
            var audits = new List<string>();
            IInstanceManagementRepository repository = Stub<IInstanceManagementRepository>.Create(new()
            {
                [nameof(IInstanceManagementRepository.GetZoneInstance)] = _ =>
                    Task.FromException<GetServerInstanceFromPort>(new InvalidOperationException("database unreachable"))
            });

            IActionResult result = await Handle(repository, audits);

            Assert.True(Assert.IsType<SuccessAndErrorMessage>(Assert.IsType<OkObjectResult>(result).Value).Success);
            Assert.Equal(new[] { "zone_exit" }, audits);
        }

        //A deleted row (empty) and a row marked shutting down (status 3) are deliberate stops: no audit row.
        [Theory]
        [InlineData(0, 2)]
        [InlineData(5, 3)]
        public async Task ZoneProcessExited_AuditsNothing_ForADeliberateStop(int mapInstanceID, int status)
        {
            var audits = new List<string>();
            IInstanceManagementRepository repository = Stub<IInstanceManagementRepository>.Create(new()
            {
                [nameof(IInstanceManagementRepository.GetZoneInstance)] = _ =>
                    Task.FromResult(new GetServerInstanceFromPort { MapInstanceID = mapInstanceID, Status = status })
            });

            await Handle(repository, audits);

            Assert.Empty(audits);
        }

        [Fact]
        public async Task ZoneProcessExited_AuditsACrash_OfALiveInstance()
        {
            var audits = new List<string>();
            IInstanceManagementRepository repository = Stub<IInstanceManagementRepository>.Create(new()
            {
                [nameof(IInstanceManagementRepository.GetZoneInstance)] = _ =>
                    Task.FromResult(new GetServerInstanceFromPort { MapInstanceID = 5, Status = 2 })
            });

            await Handle(repository, audits);

            Assert.Equal(new[] { "zone_exit" }, audits);
        }

        private static Task<IActionResult> Handle(IInstanceManagementRepository repository, List<string> audits)
        {
            IAuditRepository audit = Stub<IAuditRepository>.Create(new()
            {
                [nameof(IAuditRepository.Add)] = args =>
                {
                    audits.Add((string)args[3]);
                    return Task.CompletedTask;
                }
            });
            IHeaderCustomerGUID customer = Stub<IHeaderCustomerGUID>.Create(new()
            {
                ["get_CustomerGUID"] = _ => Guid.NewGuid()
            });

            var request = new ZoneProcessExitedRequest
            {
                request = new ZoneProcessExitedRequestPayload { ZoneInstanceID = 5, ExitCode = 139, MapName = "/Game/Maps/L_Crash", Port = 7777 }
            };
            request.SetData(repository, customer, audit);
            return request.Handle();
        }

        public class Stub<T> : DispatchProxy
        {
            private Dictionary<string, Func<object[], object>> members;

            public static T Create(Dictionary<string, Func<object[], object>> members)
            {
                T proxy = Create<T, Stub<T>>();
                ((Stub<T>)(object)proxy).members = members;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                members.TryGetValue(targetMethod.Name, out var member)
                    ? member(args)
                    : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod.Name} is not stubbed.");
        }
    }
}
