# Triviela

Triviela is a live football terminal. You run `triviela`, pick a match, and watch it unfold in
your console: the score and clock, goals and cards as they happen, lineups, team stats, odds,
and a running read on how each set of fans is feeling. It refreshes a few times a second. It's
built around the World Cup and the other big competitions (the Champions League and the top
European leagues).

## Why you don't need an API key

Apps like this run on a paid data feed. The one Triviela uses, API-Football, costs about $19 a
month. The obvious way to build on it is to make every user sign up for their own key and pay.
We didn't want to do that. Asking someone to create an account and enter a card just to glance
at a score is a bad first experience, and most people would simply close the app instead.

So we built it the other way around. We run one backend that holds a single API-Football
subscription. It fetches the live data once and streams it out to every copy of the app over a
live connection. One subscription covers everyone. You install Triviela and it works straight
away: no signup, no key, nothing to pay.

The reason this is affordable is that pulling every live match is one request no matter how many
people are watching. The data cost stays flat whether one person is connected or a thousand, so
there's no reason to push it onto users. If you'd rather not use the shared backend, you can
point the app at your own API-Football key, or run it on ESPN's free public data with no key at
all, or use the built-in offline demo. More on that further down.

## Installing it from scratch

If you have never used .NET before, this is the full process. It takes a couple of minutes.

**1. Install the .NET SDK.** This is a free toolkit from Microsoft that lets you install and run
apps like Triviela. Go to https://dotnet.microsoft.com/download, download the SDK for your
operating system (Windows, macOS, or Linux), and run the installer. You want version 10 or
newer.

**2. Check it installed.** Open a terminal (Terminal on macOS, or PowerShell / Windows Terminal
on Windows) and run:

```bash
dotnet --version
```

If it prints something like `10.0.x`, you're set. If it says the command was not found, close
and reopen the terminal, or restart your computer so the installer's changes take effect.

**3. Install Triviela.**

```bash
dotnet tool install -g Triviela
```

**4. Run it.**

```bash
triviela
```

It connects to the backend and tunes into a live match within a few seconds. Make the terminal
window a decent size (at least 100 by 30 characters) or it will ask you to.

If you type `triviela` and the terminal says it can't find the command, the install folder isn't
on your PATH yet. On macOS or Linux, add this line to your `~/.zshrc` or `~/.bashrc`, then open a
new terminal:

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

On Windows this is usually set up for you, so just open a fresh terminal.

To update later, run `dotnet tool update -g Triviela`. To remove it, run
`dotnet tool uninstall -g Triviela`.

## Using it

Everything runs through the command bar at the top. Just start typing:

| Type | What it does |
|---|---|
| `LIVE` | show every live match. Use the arrow keys or a number to open one |
| `MATCH haiti` | jump to a match by team name |
| `TEAM morocco` | a team's form, standing, and manager |
| `PLAYER hakimi` | a player's season so far |
| `H2H brazil-argentina` | the head-to-head record between two teams |
| `ASK who has been the better side?` | ask the built-in analyst about the match you're on |
| `QUIT` | exit (or press Esc) |

In the default setup all of these work with no key, because the lookups run on the backend too.

## Running your own data instead (optional)

You don't need this. It's only for people who would rather not use the shared backend and want
to plug in their own keys.

Create a config file:

```bash
triviela --init
```

That writes a template to `~/.triviela/appsettings.json`. Fill in whatever you have:

```jsonc
{
  "ApiFootball": { "ApiKey": "your-key" },
  "Claude":      { "ApiKey": "sk-ant-..." }
}
```

A few things worth knowing:

- With your own API-Football key, the app polls the source directly instead of using the
  backend.
- The AI panels (the match story, fan sentiment, the `ASK` analyst, the news snippets) use
  Claude by default, or OpenAI if you set `Llm:Provider` to `openai`. Spend is capped per match
  and per day so it can't run away.
- Weather is always free and needs no key (Open-Meteo). Fan Pulse reads the Reddit match thread
  if you add Reddit credentials.
- You can force a mode when you launch: `--local` uses your own key (or keyless ESPN if you have
  no key), and `--demo` runs the offline match.

## For developers

Run it straight from the source tree:

```bash
dotnet run --project src/Triviela.Tui
dotnet test
```

How the code is laid out:

```
src/
  Triviela.Domain/     models and the data-source interfaces
  Triviela.Providers/  API-Football, ESPN, Open-Meteo, Claude/OpenAI, demo
  Triviela.Core/       the poller, snapshot store, mode selection, wiring
  Triviela.Tui/        the terminal dashboard
  Triviela.Relay/      the hosted backend (ASP.NET Core, SignalR, Redis)
tests/Triviela.Tests/
```

One background poller pulls the live data, builds a snapshot, and stores it. The dashboard reads
the latest snapshot on each frame. The relay is that same poller wrapped in a web app that
streams snapshots to every connected client over SignalR.

Want to run the backend yourself? It is a small ASP.NET Core app in
[src/Triviela.Relay](src/Triviela.Relay) that runs on a free Fly.io machine with an Upstash
Redis cache. One subscription serves all of your users. The full walkthrough is in
[plan.md](plan.md).
