using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Triviela.Domain;
using Triviela.Providers;

namespace Triviela.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTriviela(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ApiFootballOptions>(config.GetSection(ApiFootballOptions.SectionName));
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.SectionName));
        services.Configure<ClaudeOptions>(config.GetSection(ClaudeOptions.SectionName));
        services.Configure<OpenAiOptions>(config.GetSection(OpenAiOptions.SectionName));
        services.Configure<RedditOptions>(config.GetSection(RedditOptions.SectionName));
        services.Configure<TriviaelaOptions>(config.GetSection(TriviaelaOptions.SectionName));

        services.AddSingleton<SnapshotStore>();
        services.AddSingleton<RateLimitGovernor>();
        services.AddSingleton<FocusState>();

        services.AddSingleton<FreeModeState>();

        services.AddSingleton(sp =>
        {
            var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            return new LlmCostMeter { BudgetUsd = llm.BudgetUsdPerMatch };
        });

        services.AddHttpClient<ApiFootballDataSource>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ApiFootballOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
            if (opts.IsConfigured)
            {
                client.DefaultRequestHeaders.Add("x-apisports-key", opts.ApiKey);
            }
        }).AddStandardResilienceHandler();

        services.AddHttpClient<OpenMeteoWeatherSource>(client =>
        {
            client.BaseAddress = new Uri("https://api.open-meteo.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
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

        services.AddSingleton<LlmNarrativeSource>();
        services.AddSingleton<LlmMatchAnalyst>();
        services.AddSingleton<LlmMatchIntel>();
        services.AddSingleton<LlmMatchFactbook>();

        services.AddHttpClient<RedditPulseSource>(client =>
        {
            client.BaseAddress = new Uri("https://www.reddit.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TrivielaTerminal/0.1 (football terminal)");
        }).AddStandardResilienceHandler();

        services.AddSingleton<DemoDataSource>();

        services.AddScoped<IFootballDataSource, CompositeFootballDataSource>();
        services.AddScoped<IWeatherSource, CompositeWeatherSource>();
        services.AddScoped<INarrativeSource>(sp => sp.GetRequiredService<LlmNarrativeSource>());
        services.AddScoped<IFootballReference>(sp => sp.GetRequiredService<ApiFootballReference>());
        services.AddScoped<IMatchAnalyst>(sp => sp.GetRequiredService<LlmMatchAnalyst>());
        services.AddScoped<IMatchIntel>(sp => sp.GetRequiredService<LlmMatchIntel>());
        services.AddScoped<IMatchFactbook>(sp => sp.GetRequiredService<LlmMatchFactbook>());
        services.AddScoped<ISocialPulse>(sp => sp.GetRequiredService<RedditPulseSource>());

        services.AddSingleton<CompositeFootballDataSource>();
        services.AddSingleton<CompositeWeatherSource>();
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
            sp.GetRequiredService<IOptions<TriviaelaOptions>>(),
            sp.GetRequiredService<IOptions<Triviela.Providers.ApiFootballOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MatchPoller>>()));

        return services;
    }
}
