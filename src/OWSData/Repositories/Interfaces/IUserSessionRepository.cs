using System;
using System.Threading.Tasks;
using OWSData.Models.StoredProcs;

namespace OWSData.Repositories.Interfaces
{
    /// <summary>
    /// Best-effort cache for user sessions (Valkey/Redis). All members degrade gracefully:
    /// a cache miss or backend outage returns null / completes silently rather than throwing,
    /// so the caller always falls back to the database and session validation never depends
    /// on the cache being reachable.
    /// </summary>
    public interface IUserSessionRepository
    {
        Task<GetUserSession> TryGetUserSession(Guid customerGuid, Guid userSessionGuid);
        Task SetUserSession(Guid customerGuid, Guid userSessionGuid, GetUserSession userSession, TimeSpan timeToLive);
        Task RemoveUserSession(Guid customerGuid, Guid userSessionGuid);

        /// <summary>
        /// Evicts every cached session belonging to <paramref name="userGuid"/>, and briefly blocks
        /// them from being re-cached, so a change to the user row (for example Role) is seen by the
        /// user's existing sessions on their next read instead of after the session TTL.
        /// </summary>
        Task RemoveUserSessionsForUser(Guid customerGuid, Guid userGuid);
    }
}
