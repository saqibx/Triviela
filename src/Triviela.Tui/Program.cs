using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Triviela.Core;
using Triviela.Domain;
using Triviela.Providers;
using Triviela.Tui;

var userConfigDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".triviela");
var userConfigFile = Path.Combine(userConfigDir, "appsettings.json");

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return;
}
if (args.Contains("--version") || args.Contains("-v"))
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    Console.WriteLine($"triviela {version}");
    return;
}

if (args.Contains("--init"))
{
    Directory.CreateDirectory(userConfigDir);

    TryRestrictUnix(userConfigDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    if (File.Exists(userConfigFile))
    {
        Console.WriteLine($"Config already exists: {userConfigFile}");
    }
    else
    {
        await File.WriteAllTextAsync(userConfigFile, ConfigTemplate);
        TryRestrictUnix(userConfigFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"Wrote config template: {userConfigFile}");
    }
    Console.WriteLine("Add your API keys there, or set TRIVIELA_ApiFootball__ApiKey / TRIVIELA_Claude__ApiKey.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddJsonFile(userConfigFile, optional: true)
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables("TRIVIELA_");

builder.Services.AddTriviela(builder.Configuration);

builder.Logging.ClearProviders();

using var host = builder.Build();
await host.StartAsync();

try
{
    var snapshots = host.Services.GetRequiredService<SnapshotStore>();

    if (args.Contains("--free"))
        host.Services.GetRequiredService<FreeModeState>().Enable();

    var dashboard = new TuiDashboard(
        snapshots,
        host.Services.GetRequiredService<FocusState>(),

        host.Services.GetRequiredService<ApiFootballReference>(),
        host.Services.GetRequiredService<IMatchAnalyst>(),
        host.Services.GetRequiredService<ILlmProvider>(),
        host.Services.GetRequiredService<LlmCostMeter>(),
        host.Services.GetRequiredService<FreeModeState>());

    if (args.Length >= 2 && args[0] == "--cmd")
    {
        await WaitForFirstSnapshotAsync(snapshots);
        dashboard.RunCommandOnce(string.Join(' ', args.Skip(1)));
    }
    else if (args.Contains("--once") || args.Contains("--picker"))
    {
        await WaitForFirstSnapshotAsync(snapshots);
        dashboard.RenderOnce(picker: args.Contains("--picker"));
    }
    else
    {
        dashboard.Run();
    }
}
finally
{
    await host.StopAsync(TimeSpan.FromSeconds(2));
}

static async Task WaitForFirstSnapshotAsync(SnapshotStore store)
{
    var timeout = TimeSpan.FromSeconds(20);
    Console.WriteLine("Waiting for first live snapshot…");
    var deadline = DateTime.UtcNow + timeout;
    while (store.All().Count == 0 && DateTime.UtcNow < deadline)
        await Task.Delay(TimeSpan.FromMilliseconds(250));
}

static void TryRestrictUnix(string path, UnixFileMode mode)
{
    if (OperatingSystem.IsWindows())
        return;
    try { File.SetUnixFileMode(path, mode); }
    catch { }
}

static void PrintUsage()
{
    Console.WriteLine("""
triviela — a live football terminal (Triviela .NET TUI).

Usage: triviela [flags]

Flags:
  --init            Write a config template to ~/.triviela/appsettings.json and exit.
  --once            Render the dashboard once and exit (non-interactive).
  --picker          Render the fixture picker once and exit.
  --cmd <command>   Run a single in-app command, print the result, and exit.
  --free            Launch in FREE mode (API-Football free-tier pacing, 100 req/day).
  --help, -h        Show this help and exit.
  --version, -v     Print the tool version and exit.

In-app commands:
  LIVE              List the matches currently being polled.
  MATCH <id>        Focus a fixture and show its live snapshot.
  TEAM <name>       Look up a team.
  PLAYER <name>     Look up a player.
  H2H <a> <b>       Head-to-head between two teams.
  ASK <question>    Ask the match analyst a question.
  FREE              Toggle FREE mode (free-tier pacing).
  CLOSE             Close the focused fixture.
  QUIT              Exit the terminal.
""");
}

partial class Program
{
    internal const string ConfigTemplate = """
{
  "ApiFootball": {
    "ApiKey": "",
    "BaseUrl": "https://v3.football.api-sports.io/"
  },
  "Llm": {
    "Provider": "claude",
    "BudgetUsdPerMatch": 1.50
  },
  "Claude": {
    "ApiKey": "",
    "Model": "claude-haiku-4-5"
  },
  "OpenAI": {
    "ApiKey": "",
    "Model": "gpt-4o-mini"
  },
  "Reddit": {
    "ClientId": "",
    "ClientSecret": ""
  },
  "Triviela": {
    "LivePollSeconds": 12,
    "WeatherPollMinutes": 30,
    "NarrativePollMinutes": 3,
    "IntelPollSeconds": 300,
    "OddsPollMinutes": 5,
    "ApiFootballDailyBudget": 100
  }
}
""";
}
