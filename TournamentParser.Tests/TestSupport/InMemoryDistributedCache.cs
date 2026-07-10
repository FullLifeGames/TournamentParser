#nullable enable
using Microsoft.Extensions.Caching.Distributed;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentParser.Tests.TestSupport
{
    /// <summary>Minimal IDistributedCache over a dictionary for offline cache-flow tests.</summary>
    public class InMemoryDistributedCache : IDistributedCache
    {
        public ConcurrentDictionary<string, byte[]> Store { get; } = new();

        public byte[]? Get(string key) => Store.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => Store.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Store[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
