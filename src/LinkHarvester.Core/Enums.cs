namespace LinkHarvester.Core;

public enum TitleKind
{
    Movie = 0,
    Season = 1,
    Anime = 2,
    Other = 99
}

public enum ArticleStatus
{
    Discovered = 0,
    Parsed = 1,
    Resolved = 2,
    InInbox = 3,
    Submitted = 4,
    Skipped = 5,
    Superseded = 6,
    Failed = 9
}

public enum TitleStatus
{
    New = 0,
    InInbox = 1,
    Submitted = 2,
    Skipped = 3
}

public enum ResolutionAttemptResult
{
    Success = 0,
    BotDetected = 1,
    NoLinksFound = 2,
    NetworkError = 3,
    Timeout = 4,
    Unknown = 9
}

public enum SubmissionStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}
