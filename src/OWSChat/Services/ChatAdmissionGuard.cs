using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using OWSShared.Grpc;

namespace OWSChat.Service
{
    /// <summary>Process-wide chat admission and budgets, shared by all service instances.</summary>
    internal static class ChatAdmissionGuard
    {
        private static readonly Registry<Guid> Users = new();
        private static readonly Registry<string> Addresses = new();

        internal static int Setting(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

        internal static Lease TryAcquireUser(Guid user) => Users.Acquire(user,
            Math.Min(2, Setting("OWS_CHAT_MAX_STREAMS_PER_USER", 2)), retainBudget: true);

        internal static Lease TryAcquireAddress(string peer)
        {
            // gRPC peers are ipv4:address:port or ipv6:[address]:port. Never key by port.
            int start = peer?.IndexOf(':') ?? -1;
            int end = peer?.LastIndexOf(':') ?? -1;
            string host = start >= 0 && end > start ? peer.Substring(start + 1, end - start - 1).Trim('[', ']') : "unknown";
            string key = IPAddress.TryParse(host, out var address) ? address.MapToIPv6().ToString() : "unknown";
            return Addresses.Acquire(key, Setting("OWS_CHAT_MAX_STREAMS_PER_IP", 32), retainBudget: false);
        }

        internal sealed class Lease : IDisposable
        {
            private Action _release;
            internal GrpcStreamGuard.MessageRateLimiter RateLimiter { get; }
            internal Lease(Action release, GrpcStreamGuard.MessageRateLimiter limiter)
            {
                _release = release;
                RateLimiter = limiter;
            }
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        private sealed class Registry<TKey>
        {
            private sealed class Entry
            {
                internal int Streams;
                internal DateTime RetainUntil;
                internal readonly TimeSpan Window = TimeSpan.FromSeconds(Setting("OWS_GRPC_RATE_WINDOW_SECONDS", 5));
                internal readonly GrpcStreamGuard.MessageRateLimiter Limiter;
                internal Entry() => Limiter = new GrpcStreamGuard.MessageRateLimiter(Setting("OWS_GRPC_MSGS_PER_WINDOW", 20), Window);
            }
            private readonly Dictionary<TKey, Entry> _entries = new();
            private DateTime _nextSweep;

            internal Lease Acquire(TKey key, int cap, bool retainBudget)
            {
                lock (_entries)
                {
                    var now = DateTime.UtcNow;
                    if (now >= _nextSweep)
                    {
                        foreach (var expired in _entries.Where(e => e.Value.Streams == 0 && e.Value.RetainUntil <= now).Select(e => e.Key).ToArray())
                            _entries.Remove(expired);
                        _nextSweep = now.AddSeconds(1);
                    }
                    if (!_entries.TryGetValue(key, out var entry))
                        _entries.Add(key, entry = new Entry());
                    if (entry.Streams >= cap) return null;
                    entry.Streams++;
                    return new Lease(() =>
                    {
                        lock (_entries)
                        {
                            if (--entry.Streams == 0)
                            {
                                if (retainBudget)
                                    entry.RetainUntil = DateTime.UtcNow.Add(entry.Window);
                                else
                                    _entries.Remove(key);
                            }
                        }
                    }, entry.Limiter);
                }
            }
        }
    }
}
