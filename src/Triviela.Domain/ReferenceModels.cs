namespace Triviela.Domain;

public record FormResult(
    DateTimeOffset Date,
    string Opponent,
    bool Home,
    int GoalsFor,
    int GoalsAgainst,
    string Competition)
{
    public char Outcome => GoalsFor > GoalsAgainst ? 'W' : GoalsFor < GoalsAgainst ? 'L' : 'D';
    public string Score => $"{GoalsFor}-{GoalsAgainst}";
}

public record TeamProfile(
    Team Team,
    string? Competition,
    int? Rank,
    int? Points,
    int? Played,
    int? Win,
    int? Draw,
    int? Loss,
    int? GoalsFor,
    int? GoalsAgainst,
    Manager? Manager,
    IReadOnlyList<FormResult> Recent,
    IReadOnlyList<string> Insights);

public record PlayerStatLine(string Competition, string Team, int Apps, int Goals, int Assists);

public record RatedPlayer(string Name, string? Position, double Rating, int Goals, int Assists, int? Number);

public record PlayerProfile(
    Player Player,
    string? Club,
    int Appearances,
    int Goals,
    int Assists,
    IReadOnlyList<PlayerStatLine> Competitions);

public record Meeting(
    DateTimeOffset Date,
    string Home,
    string Away,
    int HomeGoals,
    int AwayGoals,
    string Competition)
{
    public string Score => $"{HomeGoals}-{AwayGoals}";
}

public record HeadToHead(
    string TeamA,
    string TeamB,
    int Played,
    int AWins,
    int Draws,
    int BWins,
    int AGoals,
    int BGoals,
    Meeting? Biggest,
    IReadOnlyList<Meeting> Recent);
