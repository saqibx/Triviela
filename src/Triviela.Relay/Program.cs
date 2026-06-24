using StackExchange.Redis;
using Triviela.Core;
using Triviela.Domain;
using Triviela.Relay;

var builder = WebApplication.CreateBuilder(args);

// Reuse Triviela's TRIVIELA_-prefixed, __-nested env-var convention (Fly secrets).
builder.Configuration.AddEnvironmentVariables("TRIVIELA_");

// Same poller/sources/registry as the local app, but always polling with the configured key.
builder.Services.AddTrivielaRelay(builder.Configuration);

builder.Services.Configure<RelayLimitsOptions>(builder.Configuration.GetSection(RelayLimitsOptions.SectionName));
builder.Services.AddSingleton<RelayRateLimiter>();

builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions = TrivielaJson.CreateOptions());

// Redis is optional: configured → durable cache + late-joiner backfill; absent → memory-only.
var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(RedisConfig.Parse(redisConnectionString)));
    builder.Services.AddSingleton<IRelaySnapshotCache, RedisRelaySnapshotCache>();
}
else
{
    builder.Services.AddSingleton<IRelaySnapshotCache, NullRelaySnapshotCache>();
}

builder.Services.AddHostedService<SnapshotBroadcaster>();

var app = builder.Build();

app.MapHub<MatchHub>("/hub/match");
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
app.MapGet("/", () => Results.Ok("Triviela relay. Connect the triviela CLI to /hub/match."));

app.Run();
