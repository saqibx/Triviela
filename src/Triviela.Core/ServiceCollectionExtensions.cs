using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Triviela.Domain;
using Triviela.Providers;

namespace Triviela.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Client wiring. Picks a mode ONCE from config (honouring the "no per-tick fallback" rule):
    /// <list type="bullet">
    /// <item>Relay:Url set → relay mode: subscribe to the hosted backend over SignalR; no local poller.</item>
    /// <item>else → local poller mode: the composite source picks API-Football (key) → ESPN (keyless) → demo.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddTriviela(this IServiceCollection services, IConfiguration config)
    {
        AddCommon(services, config);

        var relayUrl = config[$"{RelayOptions.SectionName}:Url"];
        if (!string.IsNullOrWhiteSpace(relayUrl))
        {
            AddRelayClient(services);
            return services;
        }

        AddPollerStack(services, driveFromFocus: true);
        return services;
    }

    /// <summary>
    /// Relay backend wiring: always runs the poller against the configured API-Football key and LLM,
    /// and never wires the relay client (so the backend can't connect to itself). The registry is
    /// driven by SignalR connections, so the focus→registry bridge is intentionally omitted.
    /// </summary>
    public static IServiceCollection AddTrivielaRelay(this IServiceCollection services, IConfiguration config)
    {
        AddCommon(services, config);
        AddPollerStack(services, driveFromFocus: false);
        return services;
    }

    // ---- building blocks ----

    private static void AddCommon(IServiceCollection services, IConfiguration config)
    {
        services.Configure<ApiFootballOptions>(config.GetSection(ApiFootballOptions.SectionName));
        services.Configure<EspnOptions>(config.GetSection(EspnOptions.SectionName));
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.SectionName));
        services.Configure<ClaudeOptions>(config.GetSection(ClaudeOptions.SectionName));
        services.Configure<OpenAiOptions>(config.GetSection(OpenAiOptions.SectionName));
        services.Configure<RedditOptions>(config.GetSection(RedditOptions.SectionName));
        services.Configure<TriviaelaOptions>(config.GetSection(TriviaelaOptions.SectionName));
        services.Configure<RelayOptions>(config.GetSection(RelayOptions.SectionName));

        services.AddSingleton<SnapshotStore>();
        services.AddSingleton<RateLimitGovernor>();
        services.AddSingleton<FocusState>();
        services.AddSingleton<FreeModeState>();
        services.AddSingleton<SubscriptionRegistry>();
        services.AddSingleton<RelayConnectionState>();

        services.AddSingleton(sp =>
        {
            var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            return new LlmCostMeter { BudgetUsd = llm.BudgetUsdPerMatch };
        });

        // LLM providers are registered in every mode so the status line can show the active provider
        // and spend; the relay client never calls them directly (ASK goes through the hub).
        services.AddHttpClient<ClaudeLlmProvider>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(120);
        });
        services.AddHttpClient<OpenAiLlmProvider>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OpenAiOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(120);
        });
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var choice = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider?.Trim().ToLowerInvariant();
            var claude = sp.GetRequiredService<ClaudeLlmProvider>();
            var openai = sp.GetRequiredService<OpenAiLlmProvider>();
            ILlmProvider chosen = choice == "openai" ? openai : claude;
            if (!chosen.IsEnabled)
            {
                if (claude.IsEnabled) chosen = claude;
                else if (openai.IsEnabled) chosen = openai;
            }
            return chosen;
        });

        services.AddHttpClient<OpenMeteoWeatherSource>(client =>
        {
            client.BaseAddress = new Uri("https://api.open-meteo.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
        }).AddStandardResilienceHandler();

        services.AddSingleton<DemoDataSource>();
        services.AddSingleton<CompositeWeatherSource>();
        services.AddScoped<IWeatherSource, CompositeWeatherSource>();
    }

    private static void AddPollerStack(IServiceCollection services, bool driveFromFocus)
    {
        services.AddHttpClient<ApiFootballDataSource>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ApiFootballOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
            if (opts.IsConfigured)
                client.DefaultRequestHeaders.Add("x-apisports-key", opts.ApiKey);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<EspnFootballDataSource>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<EspnOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TrivielaTerminal/0.1 (+https://github.com/saqib/triviela)");
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ApiFootballReference>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ApiFootballOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
            if (opts.IsConfigured)
                client.DefaultRequestHeaders.Add("x-apisports-key", opts.ApiKey);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<ApiFootballMatchDossier>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ApiFootballOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
            if (opts.IsConfigured)
                client.DefaultRequestHeaders.Add("x-apisports-key", opts.ApiKey);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<RedditPulseSource>(client =>
        {
            client.BaseAddress = new Uri("https://www.reddit.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TrivielaTerminal/0.1 (football terminal)");
        }).AddStandardResilienceHandler();

        services.AddSingleton<LlmNarrativeSource>();
        services.AddSingleton<LlmMatchAnalyst>();
        services.AddSingleton<LlmMatchIntel>();
        services.AddSingleton<LlmMatchFactbook>();

        services.AddSingleton<CompositeFootballDataSource>();
        services.AddScoped<IFootballDataSource>(sp => sp.GetRequiredService<CompositeFootballDataSource>());
        services.AddScoped<INarrativeSource>(sp => sp.GetRequiredService<LlmNarrativeSource>());
        services.AddScoped<IFootballReference>(sp => sp.GetRequiredService<ApiFootballReference>());
        services.AddScoped<IMatchAnalyst>(sp => sp.GetRequiredService<LlmMatchAnalyst>());
        services.AddScoped<IMatchIntel>(sp => sp.GetRequiredService<LlmMatchIntel>());
        services.AddScoped<IMatchFactbook>(sp => sp.GetRequiredService<LlmMatchFactbook>());
        services.AddScoped<ISocialPulse>(sp => sp.GetRequiredService<RedditPulseSource>());

        services.AddHostedService(sp => new MatchPoller(
            sp.GetRequiredService<CompositeFootballDataSource>(),
            sp.GetRequiredService<ApiFootballReference>(),
            sp.GetRequiredService<CompositeWeatherSource>(),
            sp.GetRequiredService<LlmNarrativeSource>(),
            sp.GetRequiredService<LlmMatchIntel>(),
            sp.GetRequiredService<LlmMatchFactbook>(),
            sp.GetRequiredService<RedditPulseSource>(),
            sp.GetRequiredService<LlmCostMeter>(),
            sp.GetRequiredService<SnapshotStore>(),
            sp.GetRequiredService<FocusState>(),
            sp.GetRequiredService<RateLimitGovernor>(),
            sp.GetRequiredService<FreeModeState>(),
            sp.GetRequiredService<SubscriptionRegistry>(),
            sp.GetRequiredService<IOptions<TriviaelaOptions>>(),
            sp.GetRequiredService<IOptions<ApiFootballOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MatchPoller>>()));

        if (driveFromFocus)
            services.AddHostedService<FocusSubscriptionBridge>();
    }

    private static void AddRelayClient(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var url = sp.GetRequiredService<IOptions<RelayOptions>>().Value.Url!;
            return new HubConnectionBuilder()
                .WithUrl(url)
                .WithAutomaticReconnect()
                .AddJsonProtocol(o => o.PayloadSerializerOptions = TrivielaJson.CreateOptions())
                .Build();
        });

        services.AddSingleton<RelayFootballReference>();
        services.AddSingleton<RelayMatchAnalyst>();
        services.AddScoped<IFootballReference>(sp => sp.GetRequiredService<RelayFootballReference>());
        services.AddScoped<IMatchAnalyst>(sp => sp.GetRequiredService<RelayMatchAnalyst>());

        services.AddHostedService<RelaySnapshotFeed>();
    }
}
