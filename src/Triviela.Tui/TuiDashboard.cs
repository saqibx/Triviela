using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Triviela.Core;
using Triviela.Domain;

namespace Triviela.Tui;

public sealed class TuiDashboard(
    SnapshotStore store,
    FocusState focus,
    IFootballReference reference,
    IMatchAnalyst analyst,
    Triviela.Providers.ILlmProvider llm,
    Triviela.Providers.LlmCostMeter costs,
    FreeModeState free,
    RelayConnectionState relay)
{
    private volatile bool _quit;

    private volatile string _command = "";
    private volatile string? _message;

    private bool _showPicker;
    private int _pickerIndex;

    private volatile string? _drawer;
    private volatile bool _drawerLoading;
    private int _drawerSeq;
    private volatile TeamProfile? _team;
    private volatile PlayerProfile? _player;
    private volatile HeadToHead? _h2h;
    private volatile string _askQ = "";
    private volatile string? _askA;

    private static readonly Color Amber = Color.Orange1;
    private static readonly Color Cyan = Color.DeepSkyBlue1;
    private const string CEmpty = "grey23";

    public void Run()
    {
        var interactive = !Console.IsInputRedirected;
        var headlessStop = DateTime.UtcNow.AddSeconds(6);

        if (interactive) Console.CursorVisible = false;
        AnsiConsole.Clear();

        AnsiConsole.Live(BuildRoot())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
            {
                var last = DateTime.MinValue;
                while (!_quit)
                {
                    var dirty = false;
                    if (interactive)
                        while (Console.KeyAvailable) { HandleKey(Console.ReadKey(intercept: true)); dirty = true; }

                    if (dirty || DateTime.UtcNow - last > TimeSpan.FromMilliseconds(350))
                    {
                        try { ctx.UpdateTarget(BuildRoot()); } catch { }
                        last = DateTime.UtcNow;
                    }
                    if (!interactive && DateTime.UtcNow > headlessStop) break;
                    Thread.Sleep(20);
                }
            });

        if (interactive) { Console.CursorVisible = true; AnsiConsole.Clear(); }
    }

    public void RenderOnce(bool picker = false) { _showPicker = picker; WideConsole().Write(BuildRoot()); }

    public void RunCommandOnce(string command)
    {
        ExecuteCommand(command.Trim());
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (_drawerLoading && DateTime.UtcNow < deadline) Thread.Sleep(100);
        WideConsole().Write(BuildRoot());
    }

    private static IAnsiConsole WideConsole()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.Yes });
        console.Profile.Width = 150;
        console.Profile.Height = 50;
        return console;
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_showPicker) { HandlePickerKey(key); return; }
        switch (key.Key)
        {
            case ConsoleKey.Enter: ExecuteCommand(_command.Trim()); _command = ""; break;
            case ConsoleKey.Backspace: if (_command.Length > 0) _command = _command[..^1]; break;
            case ConsoleKey.Escape:
                if (_drawer is not null) _drawer = null;
                else if (_command.Length > 0) _command = "";
                else _quit = true;
                break;

            default: if (!char.IsControl(key.KeyChar) && _command.Length < 200) _command += key.KeyChar; break;
        }
    }

    private void HandlePickerKey(ConsoleKeyInfo key)
    {
        var live = focus.Live;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: _pickerIndex = Math.Max(0, _pickerIndex - 1); break;
            case ConsoleKey.DownArrow or ConsoleKey.J: _pickerIndex = Math.Min(live.Count - 1, _pickerIndex + 1); break;
            case ConsoleKey.Enter:
                if (_pickerIndex >= 0 && _pickerIndex < live.Count) focus.Select(live[_pickerIndex].Id);
                _showPicker = false; break;
            case ConsoleKey.Escape or ConsoleKey.Q: _showPicker = false; break;
            default:

                if (key.KeyChar is >= '1' and <= '9')
                {
                    var (start, _) = Window(_pickerIndex, live.Count, PickerCapacity());
                    var idx = start + (key.KeyChar - '1');
                    if (idx < live.Count) { focus.Select(live[idx].Id); _showPicker = false; }
                }
                break;
        }
    }

    private void ExecuteCommand(string cmd)
    {
        _message = null;
        if (cmd.Length == 0) return;
        var space = cmd.IndexOf(' ');
        var verb = (space < 0 ? cmd : cmd[..space]).ToUpperInvariant();
        var arg = space < 0 ? "" : cmd[(space + 1)..].Trim();

        switch (verb)
        {
            case "LIVE" or "WC":
                _showPicker = true; _drawer = null;
                _pickerIndex = Math.Max(0, focus.Live.ToList().FindIndex(f => f.Id == focus.SelectedId));
                break;
            case "QUIT" or "EXIT" or "Q": _quit = true; break;
            case "CLOSE": _drawer = null; _showPicker = false; break;
            case "MATCH": MatchCommand(arg); break;
            case "TEAM": LaunchDrawer("team", arg, "TEAM <name>", async (a, ct) => _team = await reference.GetTeamProfileAsync(a, ct), () => _team is null); break;
            case "PLAYER": LaunchDrawer("player", arg, "PLAYER <name>", async (a, ct) => _player = await reference.GetPlayerProfileAsync(a, focus.SelectedId, ct), () => _player is null); break;
            case "H2H": H2HCommand(arg); break;
            case "ASK": AskCommand(arg); break;
            case "FREE":
                _message = free.Toggle()
                    ? $"FREE mode ON — pacing to ~1 refresh/{free.LivePollSeconds / 60} min to stay within the API-Football free tier ({FreeModeState.FreeDailyCallBudget} calls/day)."
                    : "FREE mode OFF — full-speed updates.";
                break;
            default: _message = $"Unknown '{verb}'."; break;
        }
    }

    private void MatchCommand(string query)
    {
        if (query.Length == 0) { _message = "Usage: MATCH <team>"; return; }
        var tokens = query.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries);
        var hit = focus.Live.FirstOrDefault(f => tokens.All(t =>
            f.Home.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            f.Away.Name.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (hit is null) _message = $"No live match for '{query}'. Type LIVE.";
        else { focus.Select(hit.Id); _message = $"Tuned to {hit.Home.Name} v {hit.Away.Name}."; }
    }

    private void H2HCommand(string query)
    {
        var parts = query.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { _message = "Usage: H2H <a>-<b>"; return; }
        LaunchDrawer("h2h", query, "H2H <a>-<b>", async (_, ct) => _h2h = await reference.GetHeadToHeadAsync(parts[0], parts[1], ct), () => _h2h is null);
    }

    private void AskCommand(string question)
    {
        if (question.Length == 0) { _message = "Usage: ASK <question>"; return; }
        if (!analyst.IsEnabled) { _message = "ASK needs a Claude API key."; return; }
        var snap = focus.SelectedId is { } id ? store.Get(id) : null;
        if (snap is null) { _message = "No match selected — type LIVE first."; return; }
        _askQ = question; _askA = null;
        LaunchDrawer("ask", question, "ASK <question>", async (_, ct) => _askA = await analyst.AskAsync(snap, question, ct) ?? "(no answer)", () => false);
    }

    private void LaunchDrawer(string kind, string arg, string usage, Func<string, CancellationToken, Task> fetch, Func<bool> isEmpty)
    {
        if (kind != "ask" && arg.Length == 0) { _message = $"Usage: {usage}"; return; }
        if (!reference.IsLive && kind != "ask") { _message = "Reference lookups need an API-Football key."; return; }
        _showPicker = false; _drawer = kind; _drawerLoading = true;
        _team = null; _player = null; _h2h = null;

        var seq = Interlocked.Increment(ref _drawerSeq);
        _ = Task.Run(async () =>
        {
            try { await fetch(arg, CancellationToken.None); }
            catch { }
            if (seq != Interlocked.CompareExchange(ref _drawerSeq, 0, 0)) return;
            if (isEmpty()) { _message = $"Nothing found for '{arg}'."; _drawer = null; }
            _drawerLoading = false;
        });
    }

    private IRenderable BuildRoot()
    {
        var (cols, rows) = ConsoleSize();
        if (cols < 100 || rows < 30)
            return Align.Center(
                new Markup($"[orange1]Terminal too small[/]\n[grey]resize to at least 100×30 (now {cols}×{rows})[/]"),
                VerticalAlignment.Middle);

        var s = focus.SelectedId is { } id ? store.Get(id) : null;
        var hasStrip = _showPicker || _drawer is not null;

        var children = new List<Layout> { new Layout("header").Size(3) };
        if (hasStrip) children.Add(new Layout("strip").Ratio(2));
        children.Add(new Layout("body").Ratio(3));
        var root = new Layout("root").SplitRows(children.ToArray());

        root["header"].Update(CommandBar(s));
        if (_showPicker) root["strip"].Update(PickerPanel());
        else if (_drawer is not null) root["strip"].Update(DrawerPanel());

        var body = root["body"];
        if (s is null)
            body.Update(Box(new Markup("[grey]tuning to a live match… type [white]LIVE[/] to choose one[/]")));
        else
        {
            body.SplitColumns(
                new Layout("left").Ratio(34).SplitRows(
                    new Layout("score").Size(6),
                    new Layout("odds").Size(5),
                    new Layout("insight").Size(9),
                    new Layout("stats").Size(14),
                    new Layout("venue").Size(6),
                    new Layout("leftgap")),
                new Layout("mid").Ratio(33).SplitRows(
                    new Layout("events").Size(9),
                    new Layout("rated").Size(9),
                    new Layout("pulse").Size(18),
                    new Layout("midgap")),
                new Layout("right").Ratio(33).SplitRows(
                    new Layout("story").Size(15),
                    new Layout("lineups").Size(14),
                    new Layout("rightgap")));
            root["score"].Update(ScorePanel(s));
            root["odds"].Update(OddsPanel(s));
            root["insight"].Update(InsightPanel(s));
            root["stats"].Update(StatsPanel(s));
            root["venue"].Update(VenuePanel(s));
            root["events"].Update(EventsPanel(s));
            root["rated"].Update(RatedPanel(s));
            root["pulse"].Update(FanPulsePanel(s));
            root["story"].Update(StoryPanel(s));
            root["lineups"].Update(LineupsPanel(s));
            root["leftgap"].Update(new Text(""));
            root["midgap"].Update(new Text(""));
            root["rightgap"].Update(new Text(""));
        }
        return root;
    }

    private IRenderable CommandBar(MatchSnapshot? s)
    {
        string left;
        if (_showPicker) left = "[black on orange1] TRIVIELA [/]  [grey]↑/↓ select · 1-9 jump · Enter open · Esc close[/]";
        else if (_drawer is not null) left = "[black on orange1] TRIVIELA [/]  [grey]type [white]CLOSE[/] (or Esc) to dismiss[/]";
        else
        {
            var hint = _message is not null
                ? $"[orange1]{E(_message)}[/]"
                : "[grey]LIVE · MATCH <t> · TEAM <t> · PLAYER <p> · H2H <a>-<b> · ASK <q> · FREE · CLOSE[/]";
            left = $"[black on orange1] TRIVIELA [/]  [deepskyblue1]>[/] [white]{E(_command)}[/][green]_[/]   {hint}";
        }

        var freeBadge = free.Enabled ? "[black on green] FREE [/]  " : "";
        var relayBadge = relay.Status switch
        {
            "connected" => "[black on green] RELAY [/]  ",
            "connecting" or "reconnecting" => "[black on yellow] RELAY… [/]  ",
            "offline" => "[white on red] RELAY OFFLINE [/]  ",
            _ => ""
        };
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(new Markup(left), Align.Right(new Markup(
            $"{relayBadge}{freeBadge}[grey]{E(llm.Name)} ${costs.SpentUsd:0.00}/${costs.BudgetUsd:0.00}[/]  [grey]{focus.Live.Count} live[/]  [orange1]<GO>[/]")));
        return new Panel(grid).Expand().Border(BoxBorder.Heavy).BorderColor(Amber).Padding(1, 0);
    }

    private IRenderable PickerPanel()
    {
        var live = focus.Live;
        if (live.Count == 0) return Framed("LIVE MATCHES (0)", new Markup("[grey]no live matches right now[/]"));
        _pickerIndex = Math.Clamp(_pickerIndex, 0, live.Count - 1);

        var capacity = PickerCapacity();
        var (start, end) = Window(_pickerIndex, live.Count, capacity);

        var rows = new List<IRenderable>();
        if (start > 0) rows.Add(new Markup($"[grey]↑ {start} more above[/]"));
        for (int i = start; i < end; i++)
        {
            var f = live[i];
            var min = f.Status == MatchStatus.Live ? $"{f.Minute}'" : ShortStatus(f.Status);

            var rowNo = i - start + 1;
            var label = rowNo <= 9 ? $"{rowNo}." : "  ";
            var line = $"{label,3}  {min,4}  {Trunc(f.Home.Name, 18),-18} {f.Score.Home}-{f.Score.Away} {Trunc(f.Away.Name, 18),-18}  {f.Competition}";
            rows.Add(new Markup(i == _pickerIndex ? $"[black on orange1]{E(line)}[/]" : $"[white]{E(line)}[/]"));
        }
        if (end < live.Count) rows.Add(new Markup($"[grey]↓ {live.Count - end} more below[/]"));
        return Framed($"LIVE MATCHES ({live.Count})", new Rows(rows));
    }

    private int PickerCapacity()
    {
        var h = Console.IsInputRedirected ? 50 : Console.WindowHeight;
        var avail = Math.Max(0, h - 3);
        var strip = avail * 2 / 5;
        return Math.Max(3, strip - 5);
    }

    private static (int start, int end) Window(int index, int count, int capacity)
    {
        if (count <= capacity) return (0, count);
        var start = Math.Clamp(index - capacity / 2, 0, count - capacity);
        return (start, start + capacity);
    }

    private IRenderable DrawerPanel()
    {
        if (_drawerLoading) return Framed(_drawer!.ToUpperInvariant(), new Markup("[grey]loading…[/]"));
        return _drawer switch
        {
            "team" when _team is not null => TeamDrawer(_team),
            "player" when _player is not null => PlayerDrawer(_player),
            "h2h" when _h2h is not null => H2HDrawer(_h2h),
            "ask" => AskDrawer(),
            _ => Framed("—", new Markup("[grey]nothing to show[/]")),
        };
    }

    private static IRenderable TeamDrawer(TeamProfile p)
    {
        var rows = new List<IRenderable>();
        var rec = p.Played is { } pl
            ? $"[grey]P[/]{pl} [green]{p.Win}W[/] [grey]{p.Draw}D[/] [red]{p.Loss}L[/]  [grey]GF[/]{p.GoalsFor} [grey]GA[/]{p.GoalsAgainst}" + (p.Points is { } pts ? $"  [orange1]{pts} pts[/]" : "") : "";
        if (p.Rank is { } rank) rows.Add(new Markup($"[orange1 bold]#{rank}[/] [grey]{E(p.Competition ?? "")}[/]   {rec}"));
        else if (rec.Length > 0) rows.Add(new Markup(rec));
        if (p.Manager is { } m) rows.Add(new Markup($"[grey]Manager:[/] [white]{E(m.Name)}[/]{(m.Nationality is not null ? $" [grey]({E(m.Nationality)})[/]" : "")}"));
        foreach (var i in FormAnalyzer.Analyze(p.Recent)) rows.Add(new Markup($"[green]▸[/] [white]{E(i)}[/]"));
        if (p.Recent.Count > 0)
        {
            rows.Add(new Markup("[deepskyblue1]RECENT[/]"));
            foreach (var r in p.Recent.Take(6))
                rows.Add(new Markup($"{OutcomeBadge(r.Outcome)} {(r.Home ? "v" : "@")} [white]{E(Trunc(r.Opponent, 22)),-22}[/] [orange1]{r.Score}[/]  [grey]{E(r.Competition)}[/]"));
        }
        return Framed($"TEAM · {E(p.Team.Name.ToUpperInvariant())}", new Rows(rows));
    }

    private static IRenderable PlayerDrawer(PlayerProfile p)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[grey]{(p.Player.Age is {} a ? $"age {a}  " : "")}{E(p.Player.Nationality ?? "")}  {E(p.Player.Position ?? "")}  {E(p.Club ?? "")}[/]"),
            new Markup($"[orange1 bold]{p.Appearances}[/] [grey]apps[/]    [orange1 bold]{p.Goals}[/] [grey]goals[/]    [orange1 bold]{p.Assists}[/] [grey]assists[/]"),
        };
        if (p.Competitions.Count > 0)
        {
            rows.Add(new Markup("[deepskyblue1]BY COMPETITION[/]"));
            foreach (var c in p.Competitions.Where(c => c.Apps > 0).Take(7))
                rows.Add(new Markup($"[white]{E(Trunc(c.Competition, 24)),-24}[/] [grey]{E(Trunc(c.Team, 16)),-16}[/] [deepskyblue1]{c.Apps}a {c.Goals}g {c.Assists}as[/]"));
        }
        return Framed($"PLAYER · {E(p.Player.Name.ToUpperInvariant())}", new Rows(rows));
    }

    private static IRenderable H2HDrawer(HeadToHead h)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[orange1 bold]{h.AWins}[/] [grey]{E(h.TeamA)}[/]   [grey]{h.Draws} draws[/]   [deepskyblue1 bold]{h.BWins}[/] [grey]{E(h.TeamB)}[/]   [grey]over {h.Played} meetings[/]"),
            new Markup($"[grey]goals:[/] {E(h.TeamA)} [orange1]{h.AGoals}[/] - [deepskyblue1]{h.BGoals}[/] {E(h.TeamB)}"),
        };
        if (h.Biggest is { } b) rows.Add(new Markup($"[grey]biggest:[/] {E(b.Home)} [white]{b.Score}[/] {E(b.Away)} [grey]({b.Date:yyyy})[/]"));
        if (h.Recent.Count > 0)
        {
            rows.Add(new Markup("[deepskyblue1]LAST MEETINGS[/]"));
            foreach (var m in h.Recent.Take(6))
                rows.Add(new Markup($"[grey]{m.Date:yyyy-MM-dd}[/]  {E(Trunc(m.Home, 18))} [white]{m.Score}[/] {E(Trunc(m.Away, 18))}  [grey]{E(m.Competition)}[/]"));
        }
        return Framed($"H2H · {E(h.TeamA.ToUpperInvariant())} v {E(h.TeamB.ToUpperInvariant())}", new Rows(rows));
    }

    private IRenderable AskDrawer() =>
        Framed("AI MATCH ANALYST", new Rows(
            new Markup($"[deepskyblue1]Q:[/] [white]{E(_askQ)}[/]"),
            new Text(""),
            new Markup($"[white]{E(CleanText(_askA ?? "thinking…"))}[/]")));

    private static IRenderable ScorePanel(MatchSnapshot s)
    {
        var f = s.Fixture;
        return Framed("LIVE SCORE & CLOCK", new Rows(
            Align.Center(new Markup($"[white]{E(Trunc(f.Home.Name, 18))}[/]      [white]{E(Trunc(f.Away.Name, 18))}[/]")),
            Align.Center(new Markup($"[orange1 bold]{f.Score.Home}[/]   [green]{ClockText(f)}[/]   [orange1 bold]{f.Score.Away}[/]")),
            Align.Center(new Markup($"[red]{StatusText(f)}[/]")),
            Align.Center(new Markup($"[grey]{E(f.Competition ?? "")}[/]"))));
    }

    private static IRenderable StatsPanel(MatchSnapshot s)
    {
        var h = s.HomeStats; var a = s.AwayStats;
        if (h is null && a is null) return Framed("TEAM STATISTICS", new Markup("[grey]no stats yet[/]"));
        var rows = new List<IRenderable>();
        void Stat(string label, int? hv, int? av)
        {
            var line = new Grid().AddColumns(3);
            line.AddRow(
                new Markup($"[orange1]{hv?.ToString() ?? "—"}[/]"),
                Align.Center(new Markup($"[grey]{label}[/]")),
                Align.Right(new Markup($"[deepskyblue1]{av?.ToString() ?? "—"}[/]")));
            rows.Add(line);
            rows.Add(new Markup(SplitBar(hv ?? 0, av ?? 0)));
        }
        Stat("Possession %", h?.PossessionPercent, a?.PossessionPercent);
        Stat("Shots", h?.Shots, a?.Shots);
        Stat("On target", h?.ShotsOnTarget, a?.ShotsOnTarget);
        Stat("Corners", h?.Corners, a?.Corners);
        Stat("Fouls", h?.Fouls, a?.Fouls);
        Stat("Pass %", h?.PassingAccuracyPercent, a?.PassingAccuracyPercent);
        return Framed("TEAM STATISTICS", new Rows(rows));
    }

    private static IRenderable VenuePanel(MatchSnapshot s)
    {
        var rows = new List<IRenderable>();
        if (s.Venue is { } v)
        {
            rows.Add(new Markup($"[orange1]{E(v.Name)}[/]"));
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(v.City)) meta.Add(v.City! + (v.Country is not null ? $", {v.Country}" : ""));
            if (v.Capacity is { } c) meta.Add($"cap {c:N0}");
            if (!string.IsNullOrWhiteSpace(v.Surface)) meta.Add(v.Surface!);
            if (meta.Count > 0) rows.Add(new Markup($"[grey]{E(string.Join("  ·  ", meta))}[/]"));
        }
        if (s.Weather is { } w)
            rows.Add(new Markup($"[deepskyblue1]{w.TemperatureC:0}°C[/] [green]{E(w.Summary)}[/]  [grey]wind {w.WindSpeedKph:0} km/h[/]"));
        if (rows.Count == 0) rows.Add(new Markup("[grey]venue unknown[/]"));
        return Framed("VENUE & WEATHER", new Rows(rows));
    }

    private static IRenderable EventsPanel(MatchSnapshot s)
    {
        if (s.Events.Count == 0) return Framed("MATCH EVENTS", new Markup("[grey]no events yet[/]"));
        var rows = s.Events.OrderByDescending(e => e.Minute).ThenByDescending(e => e.MinuteExtra ?? 0)
            .Select(e =>
            {
                var side = e.Side == Side.Home ? "orange1" : "deepskyblue1";
                var detail = string.IsNullOrWhiteSpace(e.Detail) || e.Type is MatchEventType.Goal ? "" : $"  [grey]{E(e.Detail!)}[/]";
                var assist = string.IsNullOrWhiteSpace(e.AssistName) ? "" : $"  [grey]({E(e.AssistName!)})[/]";
                return (IRenderable)new Markup($"[green]{e.Clock,5}[/] {Icon(e.Type)}  [{side}]{E(e.PlayerName ?? "—")}[/]{detail}{assist}");
            });
        return Framed("MATCH EVENTS", new Rows(rows));
    }

    private static IRenderable FanPulsePanel(MatchSnapshot s)
    {
        if (s.Social.Count == 0) return Framed("FAN PULSE · Reddit", new Markup("[grey]no recent chatter[/]"));
        var rows = new List<IRenderable>();
        foreach (var p in s.Social.Take(8))
        {
            rows.Add(new Markup($"[deepskyblue1]{E(p.Source)}[/] [grey]{Ago(p.CreatedUtc)} ago[/]{(p.Score is { } sc ? $" [orange1]▲{sc}[/]" : "")}"));
            rows.Add(new Markup($"[white]{E(Trunc(CleanText(p.Text), 90))}[/]"));
        }
        return Framed("FAN PULSE · Reddit", new Rows(rows));
    }

    private IRenderable InsightPanel(MatchSnapshot s)
    {
        var items = new List<(string? Subject, string Text)>();
        foreach (var it in s.Intel)
            items.Add((it.Subject, CleanText(it.Insight)));
        foreach (var fct in s.Facts)
            items.Add((null, CleanText(fct.Text)));

        if (items.Count > 0)
        {
            var i = Rotate(items.Count);
            var (subject, text) = items[i];
            var rows = new List<IRenderable>();
            if (!string.IsNullOrWhiteSpace(subject)) rows.Add(new Markup($"[orange1 bold]{E(subject!)}[/]"));
            rows.Add(new Markup($"[green]{E(text)}[/]"));
            return Framed($"LIVE INSIGHT   [grey]{i + 1}/{items.Count}[/]", new Rows(rows));
        }

        var lines = InsightEngine.Generate(s);
        return Framed("LIVE INSIGHT", new Markup(lines.Count == 0 ? "[grey]gathering news…[/]" : $"[green]{E(lines[0])}[/]"));
    }

    private static IRenderable OddsPanel(MatchSnapshot s)
    {
        var o = s.Odds;
        var title = o?.Bookmaker is { } bk ? $"MATCH ODDS · {E(bk)}" : "MATCH ODDS";
        if (o is null || (o.HomeWin is null && o.Draw is null && o.AwayWin is null))
            return Framed(title, new Markup("[grey]no market available[/]"));

        var f = s.Fixture;
        var head = new Grid().AddColumns(3);
        head.AddRow(
            new Markup($"[orange1]{E(Trunc(f.Home.ShortName, 12))}[/]"),
            Align.Center(new Markup("[grey]Draw[/]")),
            Align.Right(new Markup($"[deepskyblue1]{E(Trunc(f.Away.ShortName, 12))}[/]")));
        var prices = new Grid().AddColumns(3);
        prices.AddRow(
            new Markup($"[white bold]{Odd(o.HomeWin)}[/] [grey]{Pct(o.HomeImplied)}[/]"),
            Align.Center(new Markup($"[white bold]{Odd(o.Draw)}[/] [grey]{Pct(o.DrawImplied)}[/]")),
            Align.Right(new Markup($"[grey]{Pct(o.AwayImplied)}[/] [white bold]{Odd(o.AwayWin)}[/]")));
        return Framed(title, new Rows(head, prices, new Markup(OddsBar(o))));
    }

    private static string Odd(double? o) => o is { } v ? v.ToString("0.00") : "—";
    private static string Pct(int? p) => p is { } v ? $"{v}%" : "";

    private static string OddsBar(MatchOdds o, int width = 26)
    {
        int h = o.HomeImplied ?? 0, d = o.DrawImplied ?? 0, a = o.AwayImplied ?? 0;
        var total = h + d + a;
        if (total == 0) return "";
        int hw = (int)Math.Round((double)h / total * width);
        int dw = (int)Math.Round((double)d / total * width);
        int aw = Math.Max(0, width - hw - dw);
        return $"[orange1]{new string('━', hw)}[/][grey]{new string('━', dw)}[/][deepskyblue1]{new string('━', aw)}[/]";
    }

    private static int Rotate(int count) =>
        count <= 0 ? 0 : (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 8 % count);

    private static IRenderable StoryPanel(MatchSnapshot s)
    {
        var n = s.Narrative;
        if (n is null) return Framed("STORY & FAN SENTIMENT", new Markup("[grey]narrative off — add a Claude key[/]"));
        var rows = new List<IRenderable> { new Markup($"[white italic]“{E(n.OneLiner)}”[/]"), new Text("") };
        if (!string.IsNullOrWhiteSpace(n.HomeViewpoint))
        {
            rows.Add(new Markup($"[orange1 bold]{E(s.Fixture.Home.ShortName)}[/]  {SentBar(n.HomeSentiment)}"));
            rows.Add(new Markup($"[grey]{E(n.HomeViewpoint!)}[/]"));
        }
        if (!string.IsNullOrWhiteSpace(n.AwayViewpoint))
        {
            rows.Add(new Markup($"[deepskyblue1 bold]{E(s.Fixture.Away.ShortName)}[/]  {SentBar(n.AwaySentiment)}"));
            rows.Add(new Markup($"[grey]{E(n.AwayViewpoint!)}[/]"));
        }
        return Framed("STORY & FAN SENTIMENT", new Rows(rows));
    }

    private static IRenderable RatedPanel(MatchSnapshot s)
    {
        if (s.HomeRatings.Count == 0 && s.AwayRatings.Count == 0)
            return Framed("TOP PERFORMERS", new Markup("[grey]ratings not available[/]"));
        var t = new Table().Border(TableBorder.None).Expand().HideHeaders();
        t.AddColumn(new TableColumn(""));
        t.AddColumn(new TableColumn("").RightAligned());
        t.AddRow(new Markup($"[orange1]{E(s.Fixture.Home.ShortName)}[/]"), Align.Right(new Markup($"[deepskyblue1]{E(s.Fixture.Away.ShortName)}[/]")));
        var home = s.HomeRatings.Take(5).ToList();
        var away = s.AwayRatings.Take(5).ToList();
        for (int i = 0; i < Math.Max(home.Count, away.Count); i++)
            t.AddRow(HomeRated(home.ElementAtOrDefault(i)), Align.Right(AwayRated(away.ElementAtOrDefault(i))));
        return Framed("TOP PERFORMERS", t);
    }

    private static IRenderable LineupsPanel(MatchSnapshot s)
    {
        if (s.HomeLineup is null && s.AwayLineup is null)
            return Framed("LINEUPS & FORMATION", new Markup("[grey]lineups not released[/]"));
        var t = new Table().Border(TableBorder.None).Expand().HideHeaders();
        t.AddColumn(new TableColumn(""));
        t.AddColumn(new TableColumn("").RightAligned());
        t.AddRow(new Markup($"[green]{E(s.HomeLineup?.Formation ?? "—")}[/]"), Align.Right(new Markup($"[green]{E(s.AwayLineup?.Formation ?? "—")}[/]")));
        var home = s.HomeLineup?.StartingXI ?? [];
        var away = s.AwayLineup?.StartingXI ?? [];
        for (int i = 0; i < Math.Max(home.Count, away.Count); i++)
            t.AddRow(HomePlayer(home.ElementAtOrDefault(i)), Align.Right(AwayPlayer(away.ElementAtOrDefault(i))));
        return Framed("LINEUPS & FORMATION", t);
    }

    private static IRenderable HomeRated(RatedPlayer? p) =>
        p is null ? new Text("") : new Markup($"[{Band(p.Rating)} bold]{p.Rating:0.0}[/] [white]{E(Trunc(p.Name, 18))}[/]{(p.Goals > 0 ? $" [orange1]{p.Goals}⚽[/]" : "")}");
    private static IRenderable AwayRated(RatedPlayer? p) =>
        p is null ? new Text("") : new Markup($"{(p.Goals > 0 ? $"[orange1]{p.Goals}⚽[/] " : "")}[white]{E(Trunc(p.Name, 18))}[/] [{Band(p.Rating)} bold]{p.Rating:0.0}[/]");

    private static IRenderable HomePlayer(Player? p) =>
        p is null ? new Text("") : new Markup($"[orange1]{p.Number,2}[/] [white]{E(Trunc(p.Name, 15))}[/] [grey]{E(Pos(p.Position))}[/]");
    private static IRenderable AwayPlayer(Player? p) =>
        p is null ? new Text("") : new Markup($"[grey]{E(Pos(p.Position))}[/] [white]{E(Trunc(p.Name, 15))}[/] [deepskyblue1]{p.Number,2}[/]");

    private static string SplitBar(int home, int away, int width = 22)
    {
        var total = home + away;
        var frac = total > 0 ? (double)home / total : 0.5;
        var hc = (int)Math.Round(frac * width);
        return $"[orange1]{new string('━', hc)}[/][{CEmpty}]{new string('━', width - hc)}[/]";
    }

    private static string SentBar(int sentiment, int width = 24)
    {
        var frac = Math.Clamp((sentiment + 100) / 200.0, 0, 1);
        var fc = (int)Math.Round(frac * width);
        var color = sentiment >= 25 ? "green" : sentiment <= -25 ? "red" : "yellow";
        return $"[{color}]{new string('━', fc)}[/][{CEmpty}]{new string('━', width - fc)}[/] [grey]{(sentiment >= 0 ? "+" : "")}{sentiment}[/]";
    }

    private static IRenderable Box(IRenderable body) => new Panel(body).Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey35);

    private static Panel Framed(string title, IRenderable body) =>
        new Panel(body)
        {
            Header = new PanelHeader($"[deepskyblue1] {title} [/]"),
            Border = BoxBorder.Rounded,
            Expand = true,
            BorderStyle = new Style(Color.Grey35),
            Padding = new Padding(1, 0, 1, 0),
        };

    private static string Band(double r) => r >= 7.5 ? "green" : r >= 6.8 ? "yellow" : "grey";
    private static string Pos(string? p) => string.IsNullOrEmpty(p) ? " " : p[..1];

    private static string OutcomeBadge(char o) => o switch
    {
        'W' => "[black on green] W [/]",
        'L' => "[white on red] L [/]",
        _ => "[grey on grey23] D [/]",
    };

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span.TotalMinutes < 1) return "now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    private static string ClockText(Fixture f) => f.Status switch
    {
        MatchStatus.HalfTime => "HT",
        MatchStatus.Finished => "FT",
        MatchStatus.Scheduled => f.KickoffUtc.ToLocalTime().ToString("HH:mm"),
        _ => $"{f.Minute}'"
    };

    private static string StatusText(Fixture f) => f.Status switch
    {
        MatchStatus.Live => "● LIVE",
        MatchStatus.HalfTime => "HALF TIME",
        MatchStatus.Finished => "FULL TIME",
        MatchStatus.Scheduled => "SCHEDULED",
        _ => f.Status.ToString().ToUpperInvariant()
    };

    private static string ShortStatus(MatchStatus s) => s switch
    {
        MatchStatus.HalfTime => "HT",
        MatchStatus.Finished => "FT",
        MatchStatus.Scheduled => "—",
        _ => s.ToString()
    };

    private static string Icon(MatchEventType t) => t switch
    {
        MatchEventType.Goal or MatchEventType.PenaltyGoal => "⚽",
        MatchEventType.OwnGoal => "⚽",
        MatchEventType.YellowCard => "[yellow]▮[/]",
        MatchEventType.SecondYellow => "[yellow]▮[/][red]▮[/]",
        MatchEventType.RedCard => "[red]▮[/]",
        MatchEventType.Substitution => "[grey]⇄[/]",
        MatchEventType.Var => "[grey]VAR[/]",
        _ => "•"
    };

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)].TrimEnd() + "…";

    private static string CleanText(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        s = Regex.Replace(s, @"</?cite\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[\d+\]", "");
        s = Regex.Replace(s, @"[*_`>#~]+", "");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    private static string E(string s) => Markup.Escape(s);

    private static (int cols, int rows) ConsoleSize()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return (150, 50);
        try { return (Console.WindowWidth, Console.WindowHeight); }
        catch { return (150, 50); }
    }
}
