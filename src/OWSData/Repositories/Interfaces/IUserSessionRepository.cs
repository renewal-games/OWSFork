using System;
using System.Threading.Tasks;
using OWSData.Models.StoredProcs;

namespace OWSData.Repositories.Interfaces
{
    /// <summary>
    /// Session cache for Valkey/Redis. Reads and writes degrade to the database on cache
    /// failure. Per-session invalidation throws if it cannot revoke a cached session;
    /// callers decide whether that makes their operation fail.
    /// </summary>
    public interface IUserSessionRepository
    {
        Task<GetUserSession> TryGetUserSession(Guid customerGuid, Guid userSessionGuid);
        Task SetUserSession(Guid customerGuid, Guid userSessionGuid, GetUserSession userSession, TimeSpan timeToLive);

        /// <summary>
        /// Evicts a session that is still live, and briefly blocks it from being re-cached, so its next
        /// read sees the change that was just committed.
        /// </summary>
        Task RemoveUserSession(Guid customerGuid, Guid userSessionGuid);

        /// <summary>
        /// Evicts a session that has ended for good, and blocks it from being re-cached for
        /// <paramref name="remainingLifetime"/>, so a reader that loaded the row before it ended cannot
        /// restore it however late that reader's write arrives. A lifetime shorter than the brief block
        /// of <see cref="RemoveUserSession"/> gets that block instead.
        /// </summary>
        Task RevokeUserSession(Guid customerGuid, Guid userSessionGuid, TimeSpan remainingLifetime);

        /// <summary>
        /// Evicts every cached session belonging to <paramref name="userGuid"/>, and briefly blocks
        /// them from being re-cached, so a change to the user row (for example Role) is seen by the
        /// user's existing sessions on their next read instead of after the session TTL.
        /// </summary>
        Task RemoveUserSessionsForUser(Guid customerGuid, Guid userGuid);
    }
}
