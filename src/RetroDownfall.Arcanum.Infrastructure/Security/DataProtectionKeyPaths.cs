using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static class DataProtectionKeyPaths
{
    /// <summary>
    /// The key-ring location, without creating it. Diagnostics need the path but must not
    /// materialize the directory they are reporting on, and they must not recompute it — the ring
    /// lives under the Grimoire directory, which on macOS and Windows is a different root from the
    /// secret store beside it.
    /// </summary>
    public static string Directory => System.IO.Path.Combine(ArcanumPaths.GrimoireDirectory, "keys");

    /// <summary>
    /// Creates the key ring owner-only and verifies it. The ring wraps every encrypted mirror, so a
    /// ring whose posture cannot be established fails Data Protection closed rather than warning.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The posture could not be established.</exception>
    public static DirectoryInfo EnsureDirectory()
    {
        string path = Directory;

        SecureFilePermissions.RequireOwnerOnlyDirectory(path);

        return new DirectoryInfo(path);
    }

    /// <summary>
    /// <c>PersistKeysToFileSystem</c> for the Arcanum key ring, with the directory created owner-only
    /// (<see cref="EnsureDirectory"/>) when Data Protection is first used rather than by the framework
    /// on its first key write with the umask's permissions. Every composition root registers the ring
    /// through this one call, so no stack can leave it group- or world-readable.
    /// </summary>
    public static IDataProtectionBuilder PersistKeysToOwnerOnlyKeyRing(this IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(static services =>
        {
            ILoggerFactory loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            return new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new FileSystemXmlRepository(EnsureDirectory(), loggerFactory));
        });

        return builder;
    }
}
