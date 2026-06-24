using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Triviela.Domain;

namespace Triviela.Relay;

/// <summary>
/// Upstash/Redis-backed snapshot cache. Redis is a SOFT dependency: every call is guarded so an
/// unreachable Redis logs and continues (the poller still fills memory; late-joiners just wait for
/// the next tick) — matching the codebase's GuardedAsync ethos.
/// </summary>
public sealed class RedisRelaySnapshotCache(IConnectionMultiplexer redis, ILogger<RedisRelaySnapshotCache> logger) : IRelaySnapshotCache
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromHours(2);
    private static readonly TimeSpan LiveTtl = TimeSpan.FromHours(1);
    private const string LiveKey = "triviela:live:fixtures";

    private static string SnapshotKey(string fixtureId) => $"triviela:snapshot:{fixtureId}";

    public async Task SetSnapshotAsync(MatchSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, TrivielaJson.Options);
            await redis.GetDatabase().StringSetAsync(SnapshotKey(snapshot.Fixture.Id), json, SnapshotTtl);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Redis SetSnapshot failed"); }
    }

    public async Task<MatchSnapshot?> GetSnapshotAsync(string fixtureId)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(SnapshotKey(fixtureId));
            return value.HasValue ? JsonSerializer.Deserialize<MatchSnapshot>((string)value!, TrivielaJson.Options) : null;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Redis GetSnapshot failed"); return null; }
    }

    public async Task SetLiveAsync(IReadOnlyList<Fixture> live)
    {
        try
        {
            var json = JsonSerializer.Serialize(live, TrivielaJson.Options);
            await redis.GetDatabase().StringSetAsync(LiveKey, json, LiveTtl);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Redis SetLive failed"); }
    }

    public async Task<IReadOnlyList<Fixture>?> GetLiveAsync()
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(LiveKey);
            return value.HasValue ? JsonSerializer.Deserialize<List<Fixture>>((string)value!, TrivielaJson.Options) : null;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Redis GetLive failed"); return null; }
    }
}
