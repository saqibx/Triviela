namespace Triviela.Domain;

public enum MatchStatus
{
    Scheduled,
    Live,
    HalfTime,
    Finished,
    Postponed,
    Suspended,
    Unknown
}

public enum MatchEventType
{
    Goal,
    OwnGoal,
    PenaltyGoal,
    MissedPenalty,
    YellowCard,
    SecondYellow,
    RedCard,
    Substitution,
    Var,
    Other
}

public enum Side
{
    Home,
    Away
}
