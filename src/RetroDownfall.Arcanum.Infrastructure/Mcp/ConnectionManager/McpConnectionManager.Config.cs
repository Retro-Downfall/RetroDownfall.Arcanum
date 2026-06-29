using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed partial class McpConnectionManager
{

    private async Task EnsureGlobalRegistryLoadedAsync(CancellationToken cancellationToken)
    {
        if (_globalRegistryLoaded)
        {
            return;
        }

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_globalRegistryLoaded)
            {
                return;
            }

            string globalPath = GetGlobalMcpConfigPath();

            if (File.Exists(globalPath))
            {
                McpConfig? config = await ReadMcpConfigAsync(globalPath, cancellationToken).ConfigureAwait(false);

                if (config?.McpServers is { Count: > 0 })
                {
                    RegisterFromConfigCore(config, scopeWorkingDirectory: null);
                }
            }

            _globalRegistryLoaded = true;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private void RegisterFromConfigCore(McpConfig config, string? scopeWorkingDirectory)
    {
        int maxServers = GetClampedMcpMaxServers();

        foreach (KeyValuePair<string, McpServerConfig> pair in config.McpServers!)
        {
            if (_registry.Count >= maxServers)
            {

                logger.LogWarning(
                    "MCP server registry at MaxServers cap ({MaxServers}); skipping remaining entries in {Scope}.",
                    maxServers,
                    scopeWorkingDirectory ?? "global");

                break;

            }

            string serverName = pair.Key;

            McpServerConfig cfg = pair.Value;

            if (string.IsNullOrWhiteSpace(cfg.Command) && string.IsNullOrWhiteSpace(cfg.Url))
            {
                logger.LogWarning(
                    "Skipping MCP server {ServerName} ({Scope}): missing command and url.",
                    serverName,
                    scopeWorkingDirectory ?? "global");

                continue;
            }

            McpServerTransport transport = InferTransport(cfg);

            (string Name, string? WorkingDirectory) key = (serverName, scopeWorkingDirectory);

            if (_registry.ContainsKey(key))
            {
                continue;
            }

            ManagedMcpServerEntry entry = new(
                serverName,
                scopeWorkingDirectory,
                cfg,
                transport,
                cfg.AlwaysOn);

            _registry[key] = entry;
        }
    }

    // W3.3 Fix 2: the count-check + TryAdd must be serialized across concurrent
    // registrations. The global path already holds _registryLock (see
    // EnsureGlobalRegistryLoadedAsync); the workspace-build path did not, so
    // parallel workspace loads could both pass the count check and overshoot
    // MaxServers. This wrapper acquires _registryLock for the entire register+
    // count-check so the cap is enforced atomically. Registration is synchronous
    // (no awaits inside RegisterFromConfigCore), so the lock is never held across
    // async work. Callers already holding _registryLock call RegisterFromConfigCore
    // directly to avoid a non-re-entrant SemaphoreSlim deadlock.
    internal async Task RegisterFromConfigAsync(McpConfig config, string? scopeWorkingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            RegisterFromConfigCore(config, scopeWorkingDirectory);

        }
        finally
        {

            _registryLock.Release();

        }
    }

    private async Task<McpConfig?> ReadMcpConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            FileInfo fileInfo = new(configPath);

            if (fileInfo.Exists && fileInfo.Length > MaxMcpConfigBytes)
            {

                logger.LogError(
                    "MCP config at {ConfigPath} exceeds the maximum size of {MaxBytes} bytes.",
                    configPath,
                    MaxMcpConfigBytes);

                return null;

            }

            byte[] utf8 = await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false);

            if (utf8.Length > MaxMcpConfigBytes)
            {

                logger.LogError(
                    "MCP config at {ConfigPath} exceeds the maximum size of {MaxBytes} bytes.",
                    configPath,
                    MaxMcpConfigBytes);

                return null;

            }

            return System.Text.Json.JsonSerializer.Deserialize(utf8, McpConfigJsonSerializerContext.Default.McpConfig);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read or parse MCP config at {ConfigPath}.", configPath);

            return null;
        }
    }

}
