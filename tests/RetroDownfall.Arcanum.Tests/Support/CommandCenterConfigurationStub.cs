using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class CommandCenterConfigurationStub(string path) : IConfigurationCommandService
{
    public string ConfigurationPath => path;

    public Task<Result<ConfigurationCommandSnapshot>> ReadAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Command Center must not read configuration to report its path.");

    public Task<Result> ValidateAsync(ConfigurationCommandSnapshot snapshot, ArcanumSettings settings, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Command Center must not validate configuration to report its path.");

    public Task<Result> WriteAsync(ConfigurationCommandSnapshot snapshot, ArcanumSettings settings, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Command Center must not write configuration to report its path.");
}
