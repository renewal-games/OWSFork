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

        internal static Lease TryAcquireUser(Guid user, Action evict) => Users.Acquire(user,
            Math.Min(2, Setting("OWS_CHAT_MAX_STREAMS_PER_USER", 2)), retainBudget: true, evict);

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
            private readonly Action _evict;
            internal GrpcStreamGuard.MessageRateLimiter RateLimiter { get; }
            internal Lease(Action release, GrpcStreamGuard.MessageRateLimiter limiter, Action evict = null)
            {
                _release = release;
                RateLimiter = limiter;
                _evict = evict;
            }
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
            internal void Evict() => _evict?.Invoke();
        }

        private sealed class Registry<TKey>
        {
            private sealed class Entry
            {
                internal readonly LinkedList<Lease> Streams = new();
                internal DateTime RetainUntil;
                internal readonly TimeSpan Window = TimeSpan.FromSeconds(Setting("OWS_GRPC_RATE_WINDOW_SECONDS", 5));
                internal readonly GrpcStreamGuard.MessageRateLimiter Limiter;
                internal Entry() => Limiter = new GrpcStreamGuard.MessageRateLimiter(Setting("OWS_GRPC_MSGS_PER_WINDOW", 20), Window);
            }
            private readonly Dictionary<TKey, Entry> _entries = new();
            private DateTime _nextSweep;

            internal Lease Acquire(TKey key, int cap, bool retainBudget, Action evict = null)
            {
                Lease oldest = null;
                Lease admitted;
                lock (_entries)
                {
                    var now = DateTime.UtcNow;
                    if (now >= _nextSweep)
                    {
                        foreach (var expired in _entries.Where(e => e.Value.Streams.Count == 0 && e.Value.RetainUntil <= now).Select(e => e.Key).ToArray())
                            _entries.Remove(expired);
                        _nextSweep = now.AddSeconds(1);
                    }
                    if (!_entries.TryGetValue(key, out var entry))
                        _entries.Add(key, entry = new Entry());
                    if (entry.Streams.Count >= cap)
                    {
                        if (evict == null) return null;
                        oldest = entry.Streams.First.Value;
                        entry.Streams.RemoveFirst();
                    }
                    LinkedListNode<Lease> node = null;
                    admitted = new Lease(() =>
                    {
                        lock (_entries)
                        {
                            // Eviction may have removed this node while normal disposal was pending.
                            if (node.List == null) return;
                            entry.Streams.Remove(node);
                            if (entry.Streams.Count == 0)
                            {
                                if (retainBudget)
                                    entry.RetainUntil = DateTime.UtcNow.Add(entry.Window);
                                else
                                    _entries.Remove(key);
                            }
                        }
                    }, entry.Limiter, evict);
                    node = entry.Streams.AddLast(admitted);
                }
                // Cancellation callbacks and ordinary disposal may re-enter chat cleanup.
                // Run them outside the registry lock after atomically replacing the oldest node.
                oldest?.Dispose();
                oldest?.Evict();
                return admitted;
            }
        }
    }
}
