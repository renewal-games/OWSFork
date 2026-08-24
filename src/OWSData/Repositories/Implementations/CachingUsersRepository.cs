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

namespace OWSData.Repositories.Implementations
{
    /// <summary>
    /// Decorator over <see cref="IUsersRepository"/> that serves session validation from a
    /// best-effort cache (Valkey/Redis) to remove a database round trip from the hottest
    /// authenticated path. Only <see cref="GetUserSession"/> is cached; the two operations
    /// that mutate a session (logout, selected-character change) invalidate it. Everything
    /// else passes straight through. When caching is disabled the decorator is a no-op
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

            GetUserSession cached = await _cache.TryGetUserSession(customerGUID, userSessionGUID);
            if (cached != null && cached.UserGuid.HasValue)
            {
                return cached;
            }

            GetUserSession fromDb = await _inner.GetUserSession(customerGUID, userSessionGUID);

            // Only cache valid sessions, so a not-yet-created or just-invalidated session is
            // never poisoned by a negative result.
            if (fromDb != null && fromDb.UserGuid.HasValue)
            {
                await _cache.SetUserSession(customerGUID, userSessionGUID, fromDb, TimeSpan.FromSeconds(_options.SessionTtlSeconds));
            }

            return fromDb;
        }

        public async Task<SuccessAndErrorMessage> Logout(Guid customerGuid, Guid userSessionGuid)
        {
            // Invalidate after the DB write commits. Note this ordering alone does NOT defeat the
            // read-repopulate race (a reader that already read the still-valid row can re-cache it
            // after this delete); RemoveUserSession writes a short-lived tombstone that blocks that
            // repopulate. Any residual staleness is bounded by the tombstone TTL.
            SuccessAndErrorMessage result = await _inner.Logout(customerGuid, userSessionGuid);
            if (_options.Enabled)
            {
                await _cache.RemoveUserSession(customerGuid, userSessionGuid);
            }
            return result;
        }

        public async Task<SuccessAndErrorMessage> UserSessionSetSelectedCharacter(Guid customerGUID, Guid userSessionGUID, string selectedCharacterName)
        {
            SuccessAndErrorMessage result = await _inner.UserSessionSetSelectedCharacter(customerGUID, userSessionGUID, selectedCharacterName);
            if (_options.Enabled)
            {
                await _cache.RemoveUserSession(customerGUID, userSessionGUID);
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

        public Task<GetUserSession> GetUserSessionORM(Guid customerGUID, Guid userSessionGUID)
            => _inner.GetUserSessionORM(customerGUID, userSessionGUID);

        public Task<GetUserSessionComposite> GetUserSessionParallel(Guid customerGUID, Guid userSessionGUID)
            => _inner.GetUserSessionParallel(customerGUID, userSessionGUID);

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

        // GetUserSession carries Role, and the cache is keyed by UserSessionGUID, so there is
        // no way to invalidate by UserGUID without a scan. A role change is therefore visible
        // to new sessions at once but to an already-cached session only after its TTL. Harmless
        // while nothing authorizes on Role; revisit if that changes.
        public Task<SuccessAndErrorMessage> UpdateUserRole(Guid customerGuid, Guid userGuid, string role)
            => _inner.UpdateUserRole(customerGuid, userGuid, role);
    }
}
