namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public sealed record WhisperNotification(

    Guid Id,

    WhisperSeverity Severity,

    string Message,

    string? Title,

    DateTimeOffset CreatedAtUtc,

    bool AutoDismiss);
