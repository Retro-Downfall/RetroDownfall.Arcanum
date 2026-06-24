using System.Net;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

public sealed class FakeDnsResolver : IDnsResolver
{

    private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string host, params IPAddress[] addresses)
    {

        _map[host] = addresses;

    }

    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
    {

        if (_map.TryGetValue(host, out IPAddress[]? addresses))
        {

            return Task.FromResult(addresses);

        }

        throw new SocketException((int)SocketError.HostNotFound);

    }

}
