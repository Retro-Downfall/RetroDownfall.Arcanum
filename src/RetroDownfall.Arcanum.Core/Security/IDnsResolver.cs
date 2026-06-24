using System.Net;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Resolves hostnames to IP addresses. Production implementation uses real DNS;
/// tests can substitute a deterministic fake to avoid real network lookups.
/// </summary>
public interface IDnsResolver
{

    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default);

}
