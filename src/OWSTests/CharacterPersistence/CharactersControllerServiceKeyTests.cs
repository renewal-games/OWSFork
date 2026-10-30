using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using OWSCharacterPersistence.Controllers;
using OWSData.Models.Composites;
using OWSShared;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    [Collection(ServiceKeyEnvironmentCollection.Name)]
    public class CharactersControllerServiceKeyTests
    {
        private const string ServiceKey = "test-only-service-key";
        private const string CustomerGuid = "11111111-2222-3333-4444-555555555555";
        private const string UpdateCurrency = "api/Characters/UpdateCharacterCurrency";

        public static IEnumerable<object[]> Routes() =>
            typeof(CharactersController).GetMethods()
                .Select(method => method.GetCustomAttribute<RouteAttribute>()?.Template)
                .Where(template => template != null)
                .Select(template => new object[] { "api/Characters/" + template });

        [Fact]
        public async Task Production_RefusesCustomerGuidAlone()
        {
            using var app = new CharacterPersistenceApp("Production", ServiceKey);

            HttpResponseMessage response = await Post(app, UpdateCurrency, serviceKey: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(app.Characters.Calls);
            Assert.Empty(app.Owners.Calls);
        }

        [Fact]
        public async Task Production_RefusesAWrongKey()
        {
            using var app = new CharacterPersistenceApp("Production", ServiceKey);

            HttpResponseMessage response = await Post(app, UpdateCurrency, serviceKey: "not-the-key");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(app.Characters.Calls);
        }

        [Fact]
        public async Task Production_RefusesAnEmptyKeyHeader()
        {
            using var app = new CharacterPersistenceApp("Production", ServiceKey);

            HttpResponseMessage response = await Post(app, UpdateCurrency, serviceKey: "");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Production_TheRightKeyReachesTheOwnerCheckAndTheWrite()
        {
            using var app = new CharacterPersistenceApp("Production", ServiceKey);
            app.Characters.Replies["UpdateCharacterCurrency"] = new UpdateCharacterCurrencyResponse { Success = true };

            HttpResponseMessage response = await Post(app, UpdateCurrency, ServiceKey);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("IsStaleOwnerWrite", app.Owners.Calls);
            Assert.Contains("UpdateCharacterCurrency", app.Characters.Calls);
        }

        [Fact]
        public async Task Production_WithNoKeyConfigured_RefusesEveryone()
        {
            using var app = new CharacterPersistenceApp("Production", serviceKey: null);

            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(app, UpdateCurrency, serviceKey: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(app, UpdateCurrency, serviceKey: "any-guess")).StatusCode);
            Assert.Empty(app.Characters.Calls);
        }

        [Theory]
        [MemberData(nameof(Routes))]
        public async Task Production_EveryRouteNeedsTheKey(string route)
        {
            using var app = new CharacterPersistenceApp("Production", ServiceKey);

            HttpResponseMessage response = await Post(app, route, serviceKey: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(app.Characters.Calls);
            Assert.Empty(app.Owners.Calls);
        }

        [Fact]
        public async Task Development_WithNoKeyConfigured_LetsCallsThrough()
        {
            using var app = new CharacterPersistenceApp("Development", serviceKey: null);
            app.Characters.Replies["UpdateCharacterCurrency"] = new UpdateCharacterCurrencyResponse { Success = true };

            HttpResponseMessage response = await Post(app, UpdateCurrency, serviceKey: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("UpdateCharacterCurrency", app.Characters.Calls);
        }

        [Fact]
        public async Task Development_WithAKeyConfigured_StillRequiresIt()
        {
            using var app = new CharacterPersistenceApp("Development", ServiceKey);

            HttpResponseMessage response = await Post(app, UpdateCurrency, serviceKey: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        private static async Task<HttpResponseMessage> Post(CharacterPersistenceApp app, string route, string serviceKey)
        {
            HttpClient client = app.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(new { CharacterName = "Tester", Gold = 100 })
            };
            request.Headers.Add("X-CustomerGUID", CustomerGuid);
            if (serviceKey != null)
            {
                request.Headers.Add(SamsaraServiceKeyCheck.Header, serviceKey);
            }
            return await client.SendAsync(request);
        }
    }
}
