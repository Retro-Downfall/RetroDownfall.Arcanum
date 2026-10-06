using System.Xml.Linq;
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
    /// and verified (<see cref="EnsureDirectory"/>) before the first key is read or written, rather
    /// than by the framework on its first key write with the umask's permissions. Every composition
    /// root registers the ring through this one call, so no stack can leave it group- or world-readable.
    /// </summary>
    /// <remarks>
    /// The posture is established on the ring's first use, not while the options are built: building
    /// it there made resolving the secret store throw, so <c>arcanum doctor</c> — whose checks take the
    /// secret store — could not run in exactly the state its key-ring and permission checks report.
    /// Every key read or write still fails closed until the posture is established.
    /// </remarks>
    public static IDataProtectionBuilder PersistKeysToOwnerOnlyKeyRing(this IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(static services =>
        {
            ILoggerFactory loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            return new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new OwnerOnlyKeyRingRepository(loggerFactory));
        });

        return builder;
    }

    /// <summary>
    /// The key ring's file-system repository, created only once <see cref="EnsureDirectory"/> has
    /// established the directory's owner-only posture. Until it has, every access throws the posture
    /// failure, which Data Protection reports as a failed protect or unprotect; a failed attempt is not
    /// remembered, so the next access tries again.
    /// </summary>
    private sealed class OwnerOnlyKeyRingRepository(ILoggerFactory loggerFactory) : IDeletableXmlRepository
    {
        private readonly Lock _sync = new();

        private FileSystemXmlRepository? _repository;

        public IReadOnlyCollection<XElement> GetAllElements() => Repository.GetAllElements();

        public void StoreElement(XElement element, string friendlyName) => Repository.StoreElement(element, friendlyName);

        public bool DeleteElements(Action<IReadOnlyCollection<IDeletableElement>> chooseElements) =>
            Repository.DeleteElements(chooseElements);

        private FileSystemXmlRepository Repository
        {
            get
            {
                lock (_sync)
                {
                    return _repository ??= new FileSystemXmlRepository(EnsureDirectory(), loggerFactory);
                }
            }
        }
    }
}
