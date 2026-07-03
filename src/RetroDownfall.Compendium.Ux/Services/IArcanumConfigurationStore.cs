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

public sealed record ConfigurationWriteResult(
    bool IsSuccess,
    IReadOnlyList<ConfigurationValidationError> ValidationErrors,
    string? ErrorMessage);
