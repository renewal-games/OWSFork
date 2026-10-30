using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using OWSShared;
using OWSShared.Implementations;
using OWSShared.Middleware;
using Xunit;

namespace OWSTests.Middleware
{
    /// <summary>
    /// InstanceManagement and CharacterPersistence run StoreCustomerGUIDMiddleware then RequireServiceKeyMiddleware.
    /// X-CustomerGUID is the tenant id every game client sends, so knowing it must not reach the internal routes:
    /// those also need X-Samsara-Service-Key.
    /// </summary>
    public class RequireServiceKeyMiddlewareTests
    {
        private const string ServiceKey = "test-service-key";
        private static readonly string TenantGuid = Guid.NewGuid().ToString();

        private static ServiceKeyGateSettings Production(bool required = true, string key = ServiceKey) =>
            new ServiceKeyGateSettings { Required = required, ConfiguredKey = key, IsDevelopment = false };

        // The pipeline as the internal services build it; returns the status code and whether the endpoint ran.
        private static async Task<(int Status, bool Reached)> Send(ServiceKeyGateSettings settings, string path,
            string serviceKey = null, string customerGuid = null)
        {
            bool reached = false;
            RequestDelegate endpoint = _ => { reached = true; return Task.CompletedTask; };
            var gate = new RequireServiceKeyMiddleware(endpoint, settings);
            var customerGuidMiddleware = new StoreCustomerGUIDMiddleware(new HeaderCustomerGUID());

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = path;
            context.Request.Headers["X-CustomerGUID"] = customerGuid ?? TenantGuid;
            if (serviceKey != null) context.Request.Headers[SamsaraServiceKeyCheck.Header] = serviceKey;

            await customerGuidMiddleware.InvokeAsync(context, gate.InvokeAsync);
            return (context.Response.StatusCode, reached);
        }

        [Theory]
        [InlineData("/api/Zones/AddZone")]
        [InlineData("/api/Instance/ShutDownServerInstance")]
        [InlineData("/api/Characters/GetSaveDataByName")]
        public async Task TenantGuidAlone_IsRejected(string path)
        {
            var (status, reached) = await Send(Production(), path);

            Assert.Equal(401, status);
            Assert.False(reached);
        }

        [Theory]
        [InlineData("/api/Zones/AddZone")]
        [InlineData("/api/Instance/ShutDownServerInstance")]
        [InlineData("/api/Characters/GetSaveDataByName")]
        public async Task TenantGuidAndServiceKey_IsAllowed(string path)
        {
            var (status, reached) = await Send(Production(), path, ServiceKey);

            Assert.Equal(200, status);
            Assert.True(reached);
        }

        [Fact]
        public async Task ServiceKeyWithoutTenantGuid_IsStillRejectedByTheTenantCheck()
        {
            var (status, reached) = await Send(Production(), "/api/Zones/AddZone", ServiceKey, customerGuid: "");

            Assert.Equal(401, status);
            Assert.False(reached);
        }

        [Fact]
        public async Task WrongKey_IsRejected_EvenWhenNotRequired()
        {
            var (status, reached) = await Send(Production(required: false), "/api/Zones/AddZone", "wrong-key");

            Assert.Equal(401, status);
            Assert.False(reached);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task MissingKey_IsAllowed_OnlyWhenTheGateIsSwitchedOff(string serviceKey)
        {
            Assert.Equal(200, (await Send(Production(required: false), "/api/Zones/AddZone", serviceKey)).Status);
            Assert.Equal(401, (await Send(Production(required: true), "/api/Zones/AddZone", serviceKey)).Status);
        }

        [Fact]
        public async Task RequiredWithNoConfiguredKey_FailsClosedOutsideDevelopment()
        {
            var production = new ServiceKeyGateSettings { Required = true, ConfiguredKey = null, IsDevelopment = false };
            var development = new ServiceKeyGateSettings { Required = true, ConfiguredKey = null, IsDevelopment = true };

            Assert.Equal(401, (await Send(production, "/api/Zones/AddZone", "anything")).Status);
            Assert.Equal(200, (await Send(development, "/api/Zones/AddZone")).Status);
        }

        [Theory]
        [InlineData("/health")]
        [InlineData("/api/System/Status")]
        [InlineData("/swagger/v1/swagger.json")]
        public async Task AnonymousPaths_DoNotNeedTheKey(string path)
        {
            var (status, reached) = await Send(Production(), path);

            Assert.Equal(200, status);
            Assert.True(reached);
        }

        [Fact]
        public void PostureWarning_NamesTheSwitchWhenTheGateIsOff()
        {
            Assert.Contains(ServiceKeyGateSettings.RequireEnvVar, Production(required: false).PostureWarning("InstanceManagement"));
            Assert.Contains("OWS_SAMSARA_SERVICE_KEY", Production(key: null).PostureWarning("InstanceManagement"));
            Assert.Null(Production().PostureWarning("InstanceManagement"));
        }

        [Fact]
        public void AddHeader_SendsTheKeyOnOutgoingInternalCalls()
        {
            using var request = new HttpRequestMessage();

            SamsaraServiceKeyCheck.AddHeader(request.Headers, ServiceKey);

            Assert.Equal(ServiceKey, Assert.Single(request.Headers.GetValues(SamsaraServiceKeyCheck.Header)));
        }

        [Fact]
        public void AddHeader_IsANoOpWithoutAKey()
        {
            using var request = new HttpRequestMessage();

            SamsaraServiceKeyCheck.AddHeader(request.Headers, "");

            Assert.False(request.Headers.Contains(SamsaraServiceKeyCheck.Header));
        }
    }
}
