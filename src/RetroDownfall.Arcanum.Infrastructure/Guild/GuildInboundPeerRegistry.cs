using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>A host-validated inbound principal; copied fields never authenticate a caller.</summary>
internal sealed class GuildInboundPeer(string peerId, string grantRevision, GuildGrant grant, Guid? campaignId,
    Guid? sessionId, Guid? workspaceId, long authorityVersion)
{
    internal string PeerId { get; } = peerId;

    internal string GrantRevision { get; } = grantRevision;

    internal GuildGrant Grant { get; } = grant;

    internal Guid? CampaignId { get; } = campaignId;

    internal Guid? SessionId { get; } = sessionId;

    internal Guid? WorkspaceId { get; } = workspaceId;

    internal long AuthorityVersion { get; } = authorityVersion;
}

/// <summary>Credential snapshots never store or expose reusable secret presentations.</summary>
internal sealed class GuildInboundPeerRegistry : IDisposable
{
    private readonly Lock _sync = new();

    private readonly Lock _refresh = new();

    private readonly HashSet<string> _unpublishedSecurityChanges = new(StringComparer.Ordinal);

    private volatile bool _publicationReady = true;

    private bool _stopped;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly IApiKeyDigestCache _master;

    private readonly GuildInboundPeerDenialJournal? _journal;

    internal bool HasPendingDenials => _journal?.HasPending == true;

    private readonly Func<string, string?> _readEnvironment;

    private readonly IDisposable? _subscription;

    private readonly Dictionary<string, AuthorityState> _authority = new(StringComparer.Ordinal);

    private ArcanumSettings? _source;

    private Entry[] _entries = [];

    private readonly ConditionalWeakTable<Watch, WatchAuthority> _watches = new();

    private readonly Dictionary<string, Watch> _currentWatches = new(StringComparer.Ordinal);

    private bool _disposed;

    internal GuildInboundPeerRegistry(IOptionsMonitor<ArcanumSettings> settings, IApiKeyDigestCache master,
        Func<string, string?>? readEnvironment = null, GuildInboundPeerDenialJournal? journal = null)
    {
        _settings = settings;

        _master = master;

        _journal = journal;

        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;

        Refresh(settings.CurrentValue);

        _subscription = settings.OnChange((value, _) => OnChanged(value));
    }

    internal Guid HostInstanceId { get; } = Guid.NewGuid();

    internal event Action? Changed;

    internal IReadOnlyList<GuildInboundPeer> Peers
    {
        get
        {
            EnsureSnapshot();

            lock (_sync) return AvailableWithin().Select(static item => item.Peer).ToArray();
        }
    }

    internal GuildInboundPeer? Resolve(string presentation)
    {
        if (!TryDigest(presentation, out byte[]? digest)) return null;

        try
        {
            EnsureSnapshot();

            lock (_sync)
            {
                GuildInboundPeer? matched = null;

                foreach (Entry entry in AvailableWithin())
                {
                    if (CryptographicOperations.FixedTimeEquals(digest, entry.Digest)) matched = entry.Peer;
                }

                return matched;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest!);
        }
    }

    internal bool Authenticates(GuildInboundPeer peer)
    {
        EnsureSnapshot();

        lock (_sync) return AvailableWithin().Any(item => ReferenceEquals(item.Peer, peer));
    }

    /// <summary>Only a currently authenticated reference may create a standing callback observer.</summary>
    internal Watch? CaptureWatch(GuildInboundPeer peer)
    {
        EnsureSnapshot();

        lock (_sync)
        {
            if (!AvailableWithin().Any(item => ReferenceEquals(item.Peer, peer))) return null;

            if (_currentWatches.TryGetValue(peer.PeerId, out Watch? prior)
                && _watches.TryGetValue(prior, out WatchAuthority? authority)
                && authority.GrantRevision == peer.GrantRevision && authority.AuthorityVersion == peer.AuthorityVersion)
                return prior;

            Watch watch = new();

            _watches.Add(watch, new(peer.PeerId, peer.GrantRevision, peer.AuthorityVersion));

            _currentWatches[peer.PeerId] = watch;

            return watch;
        }
    }

    /// <summary>Credential rotation preserves this host-issued capability; authority changes never do.</summary>
    internal GuildInboundPeer? ResolveWatch(Watch watch)
    {
        EnsureSnapshot();

        lock (_sync)
        {
            if (!_watches.TryGetValue(watch, out WatchAuthority? authority)) return null;

            return AvailableWithin().FirstOrDefault(item => item.Peer.PeerId == authority.PeerId
                && item.Peer.GrantRevision == authority.GrantRevision
                && item.Peer.AuthorityVersion == authority.AuthorityVersion)?.Peer;
        }
    }

    internal bool IsCurrent(string peerId, string grantRevision)
    {
        EnsureSnapshot();

        lock (_sync) return AvailableWithin().Any(item => item.Peer.PeerId == peerId && item.Peer.GrantRevision == grantRevision);
    }

    internal bool IsCurrent(string peerId, string grantRevision, long authorityVersion)
    {
        EnsureSnapshot();

        lock (_sync) return AvailableWithin().Any(item => item.Peer.PeerId == peerId && item.Peer.GrantRevision == grantRevision
            && item.Peer.AuthorityVersion == authorityVersion);
    }

    internal long GetAuthorityVersion(string peerId)
    {
        EnsureSnapshot();

        lock (_sync) return _authority.TryGetValue(peerId, out AuthorityState? state) ? state.Version : 0;
    }

    internal void RememberPeerId(string peerId)
    {
        EnsureSnapshot();

        lock (_sync) _authority.TryAdd(peerId, new("absent", false, 0));
    }

    internal bool MatchesAuthority(string peerId, string revision)
    {
        EnsureSnapshot();

        lock (_sync) return _authority.TryGetValue(peerId, out AuthorityState? state)
            && state.Present && state.Fingerprint == "enabled:" + revision;
    }

    private void EnsureSnapshot()
    {
        ArcanumSettings current = _settings.CurrentValue;

        if (ReferenceEquals(Volatile.Read(ref _source), current)) return;

        // Production must never journal while an authority reader holds a root/database gate.
        // The monitor/retry publisher owns filesystem work; an unpublished generation refuses now.
        if (_journal is not null) { _publicationReady = false; return; }

        RefreshUnpersisted(current);
    }

    internal ImmutableArray<string> CapturePendingDenials() => _journal?.Snapshot ?? ImmutableArray<string>.Empty;

    internal bool IsPendingDenial(string peerId) => _journal?.IsDenied(peerId) == true;

    internal Result PersistDenial(string peerId) => _journal?.Publish([peerId]) ?? Result.Success();

    internal Result PersistUncertainDenial() => _journal?.PublishAll() ?? Result.Success();

    internal Result ClearReconciledDenials(ImmutableArray<string> captured) => _journal?.Clear(captured) ?? Result.Success();

    internal Result RetryPublication()
    {
        if (_journal is null) return Result.Success();

        Refresh(_settings.CurrentValue);

        return _publicationReady ? Result.Success() : new Error(ErrorCodes.Guild.AuthorityUnavailable,
            "The inbound peer policy has not been durably published.");
    }

    internal void StopPublishing()
    {
        _subscription?.Dispose();

        lock (_refresh)
        {
            _stopped = true;

            _publicationReady = false;
        }
    }

    private void OnChanged(ArcanumSettings settings)
    {
        Refresh(settings);

        Changed?.Invoke();
    }

    private void Refresh(ArcanumSettings settings)
    {
        lock (_refresh)
        {
            Entry[] next;

            string[] changed;

            lock (_sync)
            {
                if (_disposed || _stopped) return;

                _publicationReady = false;

                (next, changed) = PrepareWithin(settings);
            }

            // No database or root gate participates in this pre-publication durability boundary.
            Result persisted = _journal is null || changed.Length == 0 && _journal.IsHealthy
                ? Result.Success() : _journal.Publish(changed);

            lock (_sync)
            {
                Volatile.Write(ref _source, settings);

                if (persisted.IsSuccess && !_disposed && !_stopped)
                {
                    foreach (Entry prior in _entries) CryptographicOperations.ZeroMemory(prior.Digest);

                    _entries = next;

                    _unpublishedSecurityChanges.Clear();

                    _publicationReady = true;
                }
                else
                {
                    foreach (Entry entry in next) CryptographicOperations.ZeroMemory(entry.Digest);

                    _publicationReady = false;
                }
            }
        }
    }

    private void RefreshUnpersisted(ArcanumSettings settings)
    {
        lock (_refresh)
        {
            lock (_sync)
            {
                if (_disposed || _stopped) return;

                var prepared = PrepareWithin(settings);

                foreach (Entry prior in _entries) CryptographicOperations.ZeroMemory(prior.Digest);

                _entries = prepared.Entries;

                Volatile.Write(ref _source, settings);

                _publicationReady = true;
            }
        }
    }

    private (Entry[] Entries, string[] Changed) PrepareWithin(ArcanumSettings settings)
    {
        Dictionary<string, A2AInboundPeerSettings[]> configured = (settings.ResolveA2A().InboundPeers ?? [])
            .Where(static peer => peer is not null && !string.IsNullOrWhiteSpace(peer.PeerId))
            .GroupBy(static peer => peer.PeerId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        List<Entry> entries = [];

        foreach ((string id, A2AInboundPeerSettings[] peers) in configured)
        {
            A2AInboundPeerSettings peer = peers[0];

            Result<GuildGrant> grant = peer.Grant?.ToGrant() ?? Result<GuildGrant>.Failure(new(ErrorCodes.Guild.InvalidRequest, "A peer grant is required."));

            bool valid = peers.Length == 1 && id == id.Trim() && !id.Any(char.IsControl)
                && peer.HasExplicitCampaignId && peer.CampaignId != Guid.Empty && peer.SessionId != Guid.Empty
                && peer.WorkspaceId != Guid.Empty && grant.IsSuccess
                && grant.Value.Workspaces.All(item => item.WorkspaceId == peer.WorkspaceId);

            Result<string> revision = valid ? GuildQuestIntent.Compute(new GuildIssueRootRequest(
                Guid.Empty, null, id, string.Empty, grant.Value, peer.SessionId, peer.CampaignId, peer.WorkspaceId))
                : Result<string>.Failure(new(ErrorCodes.Guild.InvalidRequest, "Invalid peer binding."));

            string fingerprint = (peer.Enabled ? "enabled:" : "disabled:") + (revision.IsSuccess ? revision.Value : "invalid");

            long version = ObserveAuthorityWithin(id, fingerprint, true);

            if (!peer.Enabled || revision.IsFailure || !ValidEnvironmentName(peer.CredentialEnvironmentVariable)) continue;

            string? credential;

            try { credential = _readEnvironment(peer.CredentialEnvironmentVariable); }
            catch (Exception exception) when (exception is System.Security.SecurityException or ArgumentException) { continue; }

            if (!TryDigest(credential, out byte[]? digest)) continue;

            GuildInboundPeer? unchanged = _entries.FirstOrDefault(item => item.Peer.PeerId == id
                && item.Peer.GrantRevision == revision.Value && item.Peer.AuthorityVersion == version
                && CryptographicOperations.FixedTimeEquals(item.Digest, digest!))?.Peer;

            entries.Add(new(unchanged ?? new(id, revision.Value, grant.Value, peer.CampaignId,
                peer.SessionId, peer.WorkspaceId, version), digest!));
        }

        foreach (string missing in _authority.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
            _ = ObserveAuthorityWithin(missing, "absent", false);

        return (entries.ToArray(), _unpublishedSecurityChanges.ToArray());
    }

    private long ObserveAuthorityWithin(string id, string fingerprint, bool present)
    {
        if (!_authority.TryGetValue(id, out AuthorityState? prior))
        {
            _authority.Add(id, new(fingerprint, present, 0));

            return 0;
        }

        if (prior.Fingerprint == fingerprint && prior.Present == present) return prior.Version;

        long version = checked(prior.Version + 1);

        _authority[id] = new(fingerprint, present, version);

        if (_journal is not null) _unpublishedSecurityChanges.Add(id);

        return version;
    }

    private Entry[] AvailableWithin()
    {
        if (_disposed || _stopped || !_publicationReady || _journal is { IsHealthy: false } || !_master.TryGetPresenceDigest(out byte[]? master) || master is null) return [];

        try
        {
            return _entries.Where(entry => _journal?.IsDenied(entry.Peer.PeerId) != true
                && !CryptographicOperations.FixedTimeEquals(entry.Digest, master)
                && _entries.Count(other => CryptographicOperations.FixedTimeEquals(other.Digest, entry.Digest)) == 1).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static bool ValidEnvironmentName(string? name) => !string.IsNullOrWhiteSpace(name)
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool TryDigest(string? presentation, out byte[]? digest)
    {
        digest = null;

        if (presentation is null || presentation.Length != 43
            || presentation.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) return false;

        Span<byte> raw = stackalloc byte[32];

        try
        {
            if (!Convert.TryFromBase64String(presentation.Replace('-', '+').Replace('_', '/') + "=", raw, out int written)
                || written != 32 || Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_') != presentation) return false;

            Span<byte> bytes = stackalloc byte[43];

            Encoding.UTF8.GetBytes(presentation, bytes);

            digest = SHA256.HashData(bytes);

            CryptographicOperations.ZeroMemory(bytes);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public void Dispose()
    {
        StopPublishing();

        lock (_sync)
        {
            _disposed = true;

            foreach (Entry entry in _entries) CryptographicOperations.ZeroMemory(entry.Digest);

            _entries = [];
        }
    }

    internal sealed class Watch { }

    private sealed record WatchAuthority(string PeerId, string GrantRevision, long AuthorityVersion);

    private sealed record Entry(GuildInboundPeer Peer, byte[] Digest);

    private sealed record AuthorityState(string Fingerprint, bool Present, long Version);
}
