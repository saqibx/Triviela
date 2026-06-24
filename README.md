# Triviela Terminal

A Bloomberg-style, real-time football intelligence terminal. Pick a live match and
get a dense, constantly-updating grid of panels: score & clock, event ticker, lineups,
team stats, venue & weather, and an optional AI story/sentiment readout.

> **It runs with zero API keys.** Out of the box it streams *real* live data from ESPN's
> public API (World Cup + top leagues) — no key, no cost. Point it at a hosted **Triviela relay**
> (`Relay:Url`) for the full feed (xG, player ratings, odds) with still no key, or add your own
> API-Football key to poll directly. With no network it falls back to a simulated demo match.
>
> The client picks a mode once at startup: **relay** (`Relay:Url` set) → **local** (your
> API-Football key) → **ESPN** (keyless) → **demo**. `--local` / `--demo` force a mode.

---

## Install the CLI (anyone)

The console client ships as a **.NET global tool**. With the .NET 10 SDK/runtime installed:

```bash
dotnet tool install -g Triviela
triviela           # live, refreshing dashboard
triviela --once    # print a single static frame and exit
```

Update or remove it with `dotnet tool update -g Triviela` / `dotnet tool uninstall -g Triviela`.

It runs immediately on the built-in demo match — no keys required. To go live, see
[Configure keys](#configure-keys-installed-tool) below.

> Building the package yourself: `dotnet pack src/Triviela.Tui -c Release -o ./artifacts`,
> then `dotnet tool install -g Triviela --add-source ./artifacts`.

---

## Quick start (from source)

```bash
# .NET 10 SDK required (you have 10.0.201)
dotnet run --project src/Triviela.Tui
```

The console dashboard auto-tunes to a live match within a few seconds and refreshes in
place (~3×/second). Live weather for the venue is already real (Open-Meteo needs no key).

It has a typed command bar — `LIVE` (selectable list of every live game: ↑/↓ or number keys,
Enter to open), `MATCH <team>`, `TEAM <name>`, `PLAYER <name>`, `H2H <a>-<b>`,
`ASK <question>`, `CLOSE`, `QUIT` (or Esc). Reference/AI lookups open a result drawer. Panels:
Score, Match Odds, Team Stats, Live Insight, Match Events, Fan Pulse, Story & Fan Sentiment,
Top Performers. `--once` prints a single static frame and exits.

Run the tests:

```bash
dotnet test
```

---

## Project layout

```
Triviela.slnx
├─ src/
│  ├─ Triviela.Domain/      # models, enums, IFootballDataSource / IWeatherSource / INarrativeSource
│  ├─ Triviela.Providers/   # API-Football, Open-Meteo, Claude, and the keyless Demo source
│  ├─ Triviela.Core/        # composites, rate-limit governor, snapshot store, background poller, DI
│  └─ Triviela.Tui/         # Spectre.Console terminal client (command bar, panel grid, status bar)
└─ tests/Triviela.Tests/    # xUnit
```

**Flow:** one `MatchPoller` background service discovers the live match, assembles a
normalized `MatchSnapshot` on a tight cadence, and publishes it to a `SnapshotStore`.
The TUI reads the latest snapshot from the store on each render. Providers are never
called from the UI.

---

## Configure keys (installed tool)

The installed `triviela` command reads config from (lowest → highest precedence):
bundled defaults → `~/.triviela/appsettings.json` → environment variables.

**Option A — config file.** Scaffold it once, then fill in your keys:

```bash
triviela --init        # writes ~/.triviela/appsettings.json (a blank template)
```

```jsonc
// ~/.triviela/appsettings.json
{
  "ApiFootball": { "ApiKey": "YOUR_API_FOOTBALL_KEY" },
  "Claude":      { "ApiKey": "sk-ant-..." }
}
```

**Option B — environment variables.** Nested keys use `__` (double underscore) and a
`TRIVIELA_` prefix:

```bash
export TRIVIELA_ApiFootball__ApiKey="YOUR_API_FOOTBALL_KEY"
export TRIVIELA_Claude__ApiKey="sk-ant-..."
```

Without `ApiFootball:ApiKey` the demo match is used; without an LLM key the
"STORY & FAN SENTIMENT" panel stays `off`.

### Choosing the AI provider (Claude or OpenAI)

The AI features (story/sentiment, fact-book, analyst, news intel) run through a swappable
provider. Pick one with `Llm:Provider` and supply that provider's key; the cheapest model
for each is used by default (`claude-haiku-4-5` / `gpt-4o-mini`) and is overridable.

```jsonc
{
  "Llm":    { "Provider": "openai", "BudgetUsdPerMatch": 1.50 },
  "OpenAI": { "ApiKey": "sk-...", "Model": "gpt-4o-mini" }
}
```

- `Llm:Provider` = `claude` (default) or `openai`. If the chosen provider has no key, it
  falls back to whichever one does.
- `Llm:BudgetUsdPerMatch` is a **hard per-match spend cap** (default $1.50) enforced across
  every AI call — once reached, calls are skipped until the next match. The status bar shows
  the active provider and running spend (e.g. `openai $0.12/$1.50`).
- **News intel uses web search, which only Claude supports.** On OpenAI that panel falls back
  to locally-generated stat insights rather than fabricating "news."

## Going live from source — add your API keys

When running from the repo, keys live in **User Secrets** (dev) or environment variables —
never in source.

```bash
cd src/Triviela.Tui
dotnet user-secrets init

# Live football data (api-sports.io). Without this, the demo match is used.
dotnet user-secrets set "ApiFootball:ApiKey" "YOUR_API_FOOTBALL_KEY"

# Optional: AI match narrative + fan-sentiment. Without this, that panel says "off".
dotnet user-secrets set "Claude:ApiKey" "sk-ant-..."
```

Then `dotnet run --project src/Triviela.Tui` again. The status bar shows your running AI
spend; the "STORY & FAN SENTIMENT" panel flips from `off` to a live readout once a Claude
key is present.

### Provider summary

| Provider | Key needed? | Used for | Get a key |
|---|---|---|---|
| **Demo source** | No | Out-of-the-box simulated match | built in |
| **Open-Meteo** | No | Venue weather | built in (free, non-commercial) |
| **API-Football** (api-sports.io) | Yes (paid for live polling) | Score, events, stats, lineups, venue | https://www.api-football.com/ |
| **Claude** (Anthropic) | Optional (default LLM) | Story/sentiment, fact-book, analyst, news intel (web search) | https://console.anthropic.com/ |
| **OpenAI** | Optional (`Llm:Provider=openai`) | Story/sentiment, fact-book, analyst (no web search) | https://platform.openai.com/ |

> Using API-Football via **RapidAPI** instead of direct? Set `ApiFootball:BaseUrl` to the
> RapidAPI host and swap the auth header in `ServiceCollectionExtensions.cs`
> (`x-apisports-key` → `X-RapidAPI-Key` / `X-RapidAPI-Host`).

---

## Configuration (`appsettings.json` → `Triviela` section)

| Key | Default | Meaning |
|---|---|---|
| `LivePollSeconds` | 12 | Cadence for score/events/stats |
| `WeatherPollMinutes` | 30 | Weather refresh cadence |
| `NarrativePollMinutes` | 3 | How often the LLM is asked for a fresh story (cost lever) |
| `OddsPollMinutes` | 5 | How often match-winner (1X2) odds are refreshed |
| `ApiFootballDailyBudget` | 100 | Token-bucket budget; matches your plan's daily request cap |

---

## Commands

| Command | Does |
|---|---|
| `LIVE` / `WC` | List all live matches; click to tune |
| `MATCH <team>` | Tune to a live match by team name (`MATCH HAITI`) |
| `TEAM <name>` | Team profile: standing, form, manager, generated insights |
| `PLAYER <name>` | Player profile: bio + season apps/goals/assists |
| `H2H <a>-<b>` | Head-to-head record (`H2H Brazil-Argentina`) |
| `ASK <question>` | AI analyst answers about the current match (needs Claude key) |
| `FREE` | Toggle free-tier pacing — slows polling to ~1 refresh/3 min so a full match stays within API-Football's 100-calls/day free plan (or launch with `--free`) |
| `CLOSE` | Dismiss the reference drawer |

In **relay** mode `TEAM`/`PLAYER`/`H2H`/`ASK` all work with no key (they run on the relay's
keys, server-side, rate-limited). In **local/ESPN** mode `TEAM`/`PLAYER`/`H2H` need an
API-Football key and `ASK` needs a Claude/OpenAI key.

### Data sources / modes

| Source | Key? | Used for |
|---|---|---|
| **ESPN** (site.api.espn.com) | No (default) | Keyless live data: score, clock, events, lineups, team stats, venue. No xG/ratings/odds. |
| **Triviela relay** (SignalR) | No (set `Relay:Url`) | Full feed streamed from one hosted backend — incl. xG, ratings, odds, AI — at the host's cost. |
| **API-Football** (api-sports.io) | Yes (own key) | Full direct polling — score, events, stats, lineups, venue, odds, ratings. |
| **Demo** | No | Offline simulated match (`--demo`). |
| **Open-Meteo** / **Reddit** / **Claude/OpenAI** | mixed | Weather (no key) · Fan Pulse (Reddit key) · AI story/analyst (LLM key, or via relay). |

Self-hosting the relay: see [plan.md](plan.md) — one ASP.NET Core app (`src/Triviela.Relay`)
on Fly.io + Upstash Redis, one API-Football subscription serves every client.

## What's built vs. what's next

**Done (Phase 0–1):** solution scaffold, domain + provider abstraction, API-Football
integration, keyless demo fallback, rate-limit governor, single-poller/snapshot-store
pipeline, and the Spectre.Console terminal with Score, Events, Lineups, Team Stats,
Venue/Weather, and (optional) AI narrative panels.

**Live odds, the fact-book, and real weather.** Three additions on the live path:

- **Match Odds panel** — 1X2 (home/draw/away) decimal prices from API-Football, with
  implied-probability bars, refreshed every `OddsPollMinutes` (default 5). Shows demo
  prices with no key.
- **Pre-match fact-book** — when a match is first tuned, the poller pulls each side's
  **coaches + trophies, full squads, and injuries** from API-Football, sends the dossier
  to Claude, and asks for **60–70 short facts/stats**. They're cached in-memory for the
  match and rotated through the **Live Insight** panel. Needs *both* an API-Football and a
  Claude key; falls back to the local stat-insight generator otherwise.
- **Real venue weather** — API-Football venues carry no coordinates, so the venue's city is
  geocoded (Open-Meteo, no key) once per match and the live reading is fetched from there.

**Next:** TheSportsDB assets, maps, managers, H2H, standings/bracket,
player spotlight; news wire, richer sentiment, momentum/xG chart;
draggable/savable layouts, odds, Redis hardening.
