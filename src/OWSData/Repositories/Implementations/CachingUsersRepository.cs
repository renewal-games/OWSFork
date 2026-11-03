using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OWSData.Models;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Models.Tables;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using Serilog;

namespace OWSData.Repositories.Implementations
{
    /// <summary>
    /// Decorator over <see cref="IUsersRepository"/> that serves session validation from a
    /// best-effort cache (Valkey/Redis) to remove a database round trip from the hottest
    /// authenticated path. Only <see cref="GetUserSession"/> is cached; the operations that
    /// change what it returns (logout, ending earlier sessions, selected-character change, role change) invalidate it.
    /// Everything else passes straight through. Logout reports a cache invalidation failure to its caller.
    /// When caching is disabled the decorator is a no-op
    /// pass-through, so behaviour is unchanged unless a cache is explicitly provisioned.
    /// </summary>
    public class CachingUsersRepository : IUsersRepository
    {
        private readonly IUsersRepository _inner;
        private readonly IUserSessionRepository _cache;
        private readonly UserSessionCacheOptions _options;

        public CachingUsersRepository(IUsersRepository inner, IUserSessionRepository cache, IOptions<UserSessionCacheOptions> options)
        {
            _inner = inner;
            _cache = cache;
            _options = options?.Value ?? new UserSessionCacheOptions();
        }

        public async Task<GetUserSession> GetUserSession(Guid customerGUID, Guid userSessionGUID)
        {
            if (!_options.Enabled)
            {
                return await _inner.GetUserSession(customerGUID, userSessionGUID);
            }

            // A cached session is served only until its ExpiresAt; one without a deadline (cached
            // before sessions expired) is revalidated against the DB.
            GetUserSession cached = await _cache.TryGetUserSession(customerGUID, userSessionGUID);
            if (cached != null && cached.UserGuid.HasValue && RemainingLifetime(cached) > TimeSpan.Zero)
            {
                return cached;
            }

            GetUserSession fromDb = await _inner.GetUserSession(customerGUID, userSessionGUID);

            // Only cache valid sessions, so a not-yet-created or just-invalidated session is
            // never poisoned by a negative result. The TTL never outlives the session itself.
            if (fromDb != null && fromDb.UserGuid.HasValue)
            {
                TimeSpan ttl = TimeSpan.FromSeconds(_options.SessionTtlSeconds);
                TimeSpan remaining = RemainingLifetime(fromDb);
                if (remaining < ttl)
                {
                    ttl = remaining;
                }
                if (ttl > TimeSpan.Zero)
                {
                    await _cache.SetUserSession(customerGUID, userSessionGUID, fromDb, ttl);
                }
            }

            return fromDb;
        }

        private static TimeSpan RemainingLifetime(GetUserSession session)
            => session.ExpiresAt.HasValue ? session.ExpiresAt.Value.ToUniversalTime() - DateTime.UtcNow : TimeSpan.Zero;

        public async Task<SuccessAndErrorMessage> Logout(Guid customerGuid, Guid userSessionGuid)
        {
            // Invalidate after the DB write, even a failed one: the write may have committed, and
            // evicting a live session only costs its next read a DB round trip. Report a cache
            // failure without throwing, so login refusal paths can still return their intended
            // result. Logging out again retries the eviction.
            SuccessAndErrorMessage result = await _inner.Logout(customerGuid, userSessionGuid);
            if (_options.Enabled)
            {
                try
                {
                    await _cache.RemoveUserSession(customerGuid, userSessionGuid);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "user_logout could not invalidate cached session {UserSessionGuid}", userSessionGuid);
                    if (result.Success)
                    {
                        return new SuccessAndErrorMessage
                        {
                            Success = false,
                            ErrorMessage = "Logout could not be confirmed because session cache invalidation failed."
                        };
                    }
                }
            }
            return result;
        }

        // The ended sessions are evicted like a logout, so a cached copy cannot outlive its row.
        public async Task<IReadOnlyList<Guid>> EndEarlierSessions(Guid customerGUID, Guid userSessionGUID)
        {
            IReadOnlyList<Guid> ended = await _inner.EndEarlierSessions(customerGUID, userSessionGUID);
            if (_options.Enabled)
            {
                foreach (Guid session in ended)
                {
                    try
                    {
                        await _cache.RemoveUserSession(customerGUID, session);
                    }
                    catch (Exception ex)
                    {
                        // Keep trying the remaining sessions. A cache outage must not turn
                        // an admitted login into an unavailable response.
                        Log.Error(ex, "user_login could not invalidate earlier cached session {UserSessionGuid}", session);
                    }
                }
            }
            return ended;
        }

        public async Task<SuccessAndErrorMessage> UserSessionSetSelectedCharacter(Guid customerGUID, Guid userSessionGUID, string selectedCharacterName)
        {
            SuccessAndErrorMessage result = await _inner.UserSessionSetSelectedCharacter(customerGUID, userSessionGUID, selectedCharacterName);
            if (_options.Enabled)
            {
                try
                {
                    await _cache.RemoveUserSession(customerGUID, userSessionGUID);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "set_selected_character could not invalidate cached session {UserSessionGuid}", userSessionGUID);
                }
            }
            return result;
        }

        // ---- Pass-through members (no session-cache interaction) ----

        public Task<CreateCharacter> CreateCharacter(Guid customerGUID, Guid userSessionGUID, string characterName, string className)
            => _inner.CreateCharacter(customerGUID, userSessionGUID, characterName, className);

        public Task<CreateCharacter> CreateSamsaraCharacter(Guid customerGUID, Guid userSessionGUID, string characterName, string className, string initialPersistentData = null)
            => _inner.CreateSamsaraCharacter(customerGUID, userSessionGUID, characterName, className, initialPersistentData);

        public Task<SuccessAndErrorMessage> CreateCharacterUsingDefaultCharacterValues(Guid customerGUID, Guid userGUID, string characterName, string defaultSetName)
            => _inner.CreateCharacterUsingDefaultCharacterValues(customerGUID, userGUID, characterName, defaultSetName);

        public Task<IEnumerable<GetAllCharacters>> GetAllCharacters(Guid customerGUID, Guid userSessionGUID)
            => _inner.GetAllCharacters(customerGUID, userSessionGUID);

        public Task<IEnumerable<GetAllSamsaraCharacters>> GetAllSamsaraCharacters(Guid customerGUID, Guid userSessionGUID)
            => _inner.GetAllSamsaraCharacters(customerGUID, userSessionGUID);

        public Task<User> GetUser(Guid customerGuid, Guid userGuid)
            => _inner.GetUser(customerGuid, userGuid);

        public Task<IEnumerable<User>> GetUsers(Guid customerGuid)
            => _inner.GetUsers(customerGuid);

        public Task<IEnumerable<AdminUserSummary>> SearchUsers(Guid customerGuid, string searchText)
            => _inner.SearchUsers(customerGuid, searchText);

        public Task<SuccessAndErrorMessage> SetNetworkTestFlagForUser(Guid customerGuid, Guid userGuid, bool isInternalNetworkTestUser)
            => _inner.SetNetworkTestFlagForUser(customerGuid, userGuid, isInternalNetworkTestUser);

        public Task<GetUserSession> GetUserSessionORM(Guid customerGUID, Guid userSessionGUID)
            => _inner.GetUserSessionORM(customerGUID, userSessionGUID);

        public Task<PlayerLoginAndCreateSession> LoginAndCreateSession(Guid customerGUID, string email, string password, bool dontCheckPassword = false)
            => _inner.LoginAndCreateSession(customerGUID, email, password, dontCheckPassword);

        public Task<PlayerLoginAndCreateSession> SteamLoginAndCreateSession(Guid customerGUID, string steamId, string personaName)
            => _inner.SteamLoginAndCreateSession(customerGUID, steamId, personaName);

        public Task<SuccessAndErrorMessage> RegisterUser(Guid customerGUID, string userName, string password, string firstName, string lastName, string role = null)
            => _inner.RegisterUser(customerGUID, userName, password, firstName, lastName, role);

        public Task<GetUserSession> GetUserFromEmail(Guid customerGUID, string email)
            => _inner.GetUserFromEmail(customerGUID, email);

        public Task<SuccessAndErrorMessage> RemoveCharacter(Guid customerGUID, Guid userSessionGUID, string characterName)
            => _inner.RemoveCharacter(customerGUID, userSessionGUID, characterName);

        public Task<SuccessAndErrorMessage> UpdateUser(Guid customerGuid, Guid userGuid, string firstName, string lastName, string email)
            => _inner.UpdateUser(customerGuid, userGuid, firstName, lastName, email);

        // GetUserSession carries Role, and the PublicAPI authorizes on it (LoginGate lets staff
        // past maintenance and the player cap), so a demoted or promoted user's cached sessions
        // are evicted at once rather than left to their TTL. OWSManagement registers this
        // decorator too, so both console role routes evict.
        public async Task<SuccessAndErrorMessage> UpdateUserRole(Guid customerGuid, Guid userGuid, string role)
        {
            SuccessAndErrorMessage result = await _inner.UpdateUserRole(customerGuid, userGuid, role);
            if (_options.Enabled)
            {
                await _cache.RemoveUserSessionsForUser(customerGuid, userGuid);
            }
            return result;
        }

        // No session invalidation: GetUserSession does not carry PreferredServerRegion, and the routing
        // path reads the column straight from Users on every join rather than from the cached session.
        public Task<SuccessAndErrorMessage> SetPreferredServerRegion(Guid customerGuid, Guid userGuid, string region)
            => _inner.SetPreferredServerRegion(customerGuid, userGuid, region);
    }
}
