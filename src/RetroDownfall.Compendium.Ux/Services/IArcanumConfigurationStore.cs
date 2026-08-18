using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.Services;

public interface IArcanumConfigurationStore : IDisposable
{

    string ConfigurationFilePath { get; }

    DateTimeOffset? GetLastWriteTimeUtc();

    Task<ArcanumSettings> ReadAsync(CancellationToken ct = default);

    Task<ConfigurationWriteResult> WriteAsync(ArcanumSettings settings, CancellationToken ct = default);

    event EventHandler? ExternalChange;

}

/// <summary>
/// The outcome of one write. <c>WarningMessage</c> carries what went wrong <em>after</em> the new
/// configuration was already durable — re-applying owner-only permissions to the replaced file is the
/// only such step — so a committed save is never reported as a failure while the operator is still
/// told that a security objective was not met.
/// </summary>
public sealed record ConfigurationWriteResult(
    bool IsSuccess,
    IReadOnlyList<ConfigurationValidationError> ValidationErrors,
    string? ErrorMessage,
    string? WarningMessage = null);
