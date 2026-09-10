using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Issues short-lived bearer capabilities that are valid only for this server process. Tokens are
/// self-authenticating and fixed-size, so validation requires neither a database nor per-client
/// state. Restarting the server replaces the signing secret and invalidates every prior token.
/// </summary>
public sealed class ArcanumProcessCapabilityService : IDisposable
{
    internal const int TokenBytes = 81;

    internal const int RandomBytes = 32;

    internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    private const byte TokenVersion = 1;

    private const int IssuedOffset = 1;

    private const int ExpiresOffset = IssuedOffset + sizeof(long);

    private const int RandomOffset = ExpiresOffset + sizeof(long);

    private const int MacOffset = RandomOffset + RandomBytes;

    private const int MacBytes = 32;

    private static ReadOnlySpan<byte> Domain =>
        "Arcanum.ProcessCapability.v1\0"u8;

    private readonly TimeProvider _timeProvider;

    private readonly TimeSpan _lifetime;

    private readonly byte[] _signingKey;

    private readonly DateTimeOffset _processUtcAnchor;

    private readonly long _processTimestampAnchor;

    private int _disposed;

    public ArcanumProcessCapabilityService()
        : this(
            TimeProvider.System,
            RandomNumberGenerator.GetBytes(32),
            DefaultLifetime,
            takeOwnership: true)
    {
    }

    internal ArcanumProcessCapabilityService(
        TimeProvider timeProvider,
        ReadOnlySpan<byte> signingKey,
        TimeSpan lifetime)
        : this(timeProvider, signingKey.ToArray(), lifetime, takeOwnership: true)
    {
    }

    private ArcanumProcessCapabilityService(
        TimeProvider timeProvider,
        byte[] signingKey,
        TimeSpan lifetime,
        bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (signingKey.Length != 32)
        {
            if (takeOwnership)
            {
                CryptographicOperations.ZeroMemory(signingKey);
            }

            throw new ArgumentException(
                "The process-capability signing key must contain exactly 32 bytes.",
                nameof(signingKey));
        }

        if (lifetime <= TimeSpan.Zero
            || lifetime.Ticks % TimeSpan.TicksPerSecond != 0
            || lifetime > TimeSpan.FromHours(24))
        {
            if (takeOwnership)
            {
                CryptographicOperations.ZeroMemory(signingKey);
            }

            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "The process-capability lifetime must be one second through 24 hours.");
        }

        _timeProvider = timeProvider;
        _lifetime = lifetime;

        try
        {
            _processTimestampAnchor = timeProvider.GetTimestamp();
            _processUtcAnchor = timeProvider.GetUtcNow().ToUniversalTime();
            _signingKey = takeOwnership ? signingKey : signingKey.ToArray();
        }
        catch
        {
            if (takeOwnership)
            {
                CryptographicOperations.ZeroMemory(signingKey);
            }

            throw;
        }
    }

    internal byte[] Issue()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        long issued = GetProcessUtcNow().ToUnixTimeSeconds();
        long expires = checked(issued + (long)_lifetime.TotalSeconds);

        byte[] token = new byte[TokenBytes];
        token[0] = TokenVersion;

        BinaryPrimitives.WriteInt64BigEndian(
            token.AsSpan(IssuedOffset, sizeof(long)),
            issued);

        BinaryPrimitives.WriteInt64BigEndian(
            token.AsSpan(ExpiresOffset, sizeof(long)),
            expires);

        RandomNumberGenerator.Fill(token.AsSpan(RandomOffset, RandomBytes));

        WriteMac(token.AsSpan(0, MacOffset), token.AsSpan(MacOffset, MacBytes));

        return token;
    }

    internal bool IsValidEncoded(string? encoded)
    {
        if (!ArcanumPresenceProofProtocol.TryDecode(
                encoded,
                TokenBytes,
                out byte[]? token))
        {
            return false;
        }

        try
        {
            return IsValid(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    internal static bool TryReadValidityWindow(
        ReadOnlySpan<byte> token,
        out DateTimeOffset issuedAt,
        out DateTimeOffset expiresAt)
    {
        issuedAt = default;
        expiresAt = default;

        if (token.Length != TokenBytes
            || token[0] != TokenVersion)
        {
            return false;
        }

        long issued = BinaryPrimitives.ReadInt64BigEndian(
            token.Slice(IssuedOffset, sizeof(long)));

        long expires = BinaryPrimitives.ReadInt64BigEndian(
            token.Slice(ExpiresOffset, sizeof(long)));

        if (expires <= issued
            || expires - issued > (long)TimeSpan.FromHours(24).TotalSeconds)
        {
            return false;
        }

        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(issued);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);

            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            issuedAt = default;
            expiresAt = default;

            return false;
        }
    }

    private bool IsValid(ReadOnlySpan<byte> token)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !TryReadValidityWindow(
                token,
                out DateTimeOffset issued,
                out DateTimeOffset expires))
        {
            return false;
        }

        DateTimeOffset now = GetProcessUtcNow();

        if (issued > now
            || expires <= now
            || expires - issued != _lifetime)
        {
            return false;
        }

        Span<byte> expectedMac = stackalloc byte[MacBytes];

        try
        {
            WriteMac(token[..MacOffset], expectedMac);

            return CryptographicOperations.FixedTimeEquals(
                expectedMac,
                token[MacOffset..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedMac);
        }
    }

    private void WriteMac(
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        byte[] message = new byte[Domain.Length + payload.Length];

        try
        {
            Domain.CopyTo(message);
            payload.CopyTo(message.AsSpan(Domain.Length));
            _ = HMACSHA256.HashData(_signingKey, message, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private DateTimeOffset GetProcessUtcNow() =>
        _processUtcAnchor + _timeProvider.GetElapsedTime(
            _processTimestampAnchor,
            _timeProvider.GetTimestamp());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(_signingKey);
        }
    }
}
