using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using StackExchange.Redis;

namespace OWSData.Repositories.Implementations.ValKey
{
    public class UserSessionRepository : IUserSessionRepository
    {
        private static readonly ConcurrentDictionary<string, Lazy<ConnectionMultiplexer>> Connections = new();

        private readonly ConfigurationOptions _configurationOptions;
        private readonly string _connectionKey;
        private readonly int _databaseIndex;
        private readonly string _keyPrefix;

        public UserSessionRepository(IOptions<UserSessionCacheOptions> options)
        {
            UserSessionCacheOptions optionsValue = options?.Value ?? new UserSessionCacheOptions();
            _configurationOptions = BuildConfigurationOptions(optionsValue);
            _connectionKey = _configurationOptions.ToString(true);
            _databaseIndex = optionsValue.Database;
            _keyPrefix = string.IsNullOrWhiteSpace(optionsValue.KeyPrefix)
                ? UserSessionCacheOptions.DefaultKeyPrefix
                : optionsValue.KeyPrefix;
        }

        private IDatabase Database => Connections
            .GetOrAdd(_connectionKey, _ => new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(_configurationOptions)))
            .Value
            .GetDatabase(_databaseIndex);

        public async Task<GetUserSession> GetUserSession(Guid userGuid)
        {
            string key = ConstructUserSessionKey(userGuid);
            RedisValue userSessionJson = await Database.StringGetAsync(key);

            if (userSessionJson.IsNullOrEmpty)
            {
                return new GetUserSession();
            }

            try
            {
                return JsonSerializer.Deserialize<GetUserSession>(userSessionJson.ToString()) ?? new GetUserSession();
            }
            catch (JsonException)
            {
                return new GetUserSession();
            }
        }

        public async Task SetUserSession(Guid userGuid, GetUserSession userSession)
        {
            if (userSession == null)
            {
                throw new ArgumentNullException(nameof(userSession));
            }

            string key = ConstructUserSessionKey(userGuid);
            string userSessionJson = JsonSerializer.Serialize(userSession);
            await Database.StringSetAsync(key, userSessionJson);
        }

        private string ConstructUserSessionKey(Guid userGuid)
        {
            if (userGuid == Guid.Empty)
            {
                throw new ArgumentException("A non-empty user GUID is required.", nameof(userGuid));
            }

            return $"{_keyPrefix}{userGuid}";
        }

        private static ConfigurationOptions BuildConfigurationOptions(UserSessionCacheOptions options)
        {
            string connectionString = string.IsNullOrWhiteSpace(options.ConnectionString)
                ? UserSessionCacheOptions.DefaultConnectionString
                : options.ConnectionString;

            ConfigurationOptions configurationOptions = ConfigurationOptions.Parse(connectionString);
            configurationOptions.AbortOnConnectFail = false;

            if (!string.IsNullOrWhiteSpace(options.Password))
            {
                configurationOptions.Password = options.Password;
            }

            return configurationOptions;
        }
    }
}
