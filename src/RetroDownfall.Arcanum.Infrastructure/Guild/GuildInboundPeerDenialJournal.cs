using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Content-free pending denial, durable before a changed runtime peer policy is published.</summary>
internal sealed class GuildInboundPeerDenialJournal : IDisposable
{
    private const string All = "all";

    private static readonly byte[] Magic = "ARCPD001"u8.ToArray();

    private readonly string _path;

    private readonly IGrimoireConnectionAdmissionGate _admission;

    private readonly Lock _mutations = new();

    private readonly Dictionary<string, FileHandleIdentity> _files = new(StringComparer.Ordinal);

    private volatile DenialSnapshot _snapshot = new(ImmutableArray<string>.Empty);

    private GrimoireOfflineTransitionJournalFilePrimitives? _directory;

    private volatile bool _healthy = true;

    private bool _disposed;

    internal GuildInboundPeerDenialJournal(string directory, IGrimoireConnectionAdmissionGate admission)
    {
        _path = Path.GetFullPath(directory);

        _admission = admission;

        try
        {
            Result opened = EnsureDirectory(create: false);

            if (opened.IsFailure) { _healthy = false; return; }

            if (_directory is null) return;

            foreach (string path in Directory.EnumerateFileSystemEntries(_path))
            {
                string leaf = Path.GetFileName(path);

                string? key = KeyFromLeaf(leaf);

                if (key is null) { _healthy = false; return; }

                var read = ReadMarker(leaf, key);

                if (read.IsFailure) { _healthy = false; return; }

                using GrimoireOfflineTransitionJournalOpenedFile file = read.Value;

                _files.Add(leaf, file.Metadata.Identity);

                _snapshot = new(_snapshot.Keys.Add(key));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _healthy = false;
        }
    }

    internal bool IsHealthy => _healthy;

    internal bool HasPending => !_snapshot.Keys.IsEmpty;

    internal ImmutableArray<string> Snapshot => _snapshot.Keys;

    // Readers perform no filesystem work and never wait on a publisher while holding a root gate.
    internal bool IsDenied(string peerId)
    {
        ImmutableArray<string> denied = _snapshot.Keys;

        return !_healthy || denied.Contains(All) || denied.Contains(Key(peerId));
    }

    internal Result Publish(IEnumerable<string> peerIds) => PublishKeysAsync(peerIds.Select(Key)).GetAwaiter().GetResult();

    internal Result PublishAll() => PublishKeysAsync([All]).GetAwaiter().GetResult();

    private async Task<Result> PublishKeysAsync(IEnumerable<string> keys)
    {
        if (!_admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
        {
            _healthy = false;

            return Unavailable;
        }

        await using IGrimoireWorkLease work = acquired;

        if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null)
        {
            _healthy = false;

            return Unavailable;
        }

        await using IGrimoireExternalEffectGroup held = effect;

        lock (_mutations)
        {
            if (_disposed) return Unavailable;

            try
            {
                foreach (string key in keys)
                {
                    if (_snapshot.Keys.Contains(key)) continue;

                    Result opened = EnsureDirectory(create: true);

                    if (opened.IsFailure) { _healthy = false; return opened; }

                    string leaf = key + ".deny";

                    string workingLeaf = leaf + ".tmp." + Guid.NewGuid().ToString("N");

                    var working = _directory!.CreateWorkingExclusive(workingLeaf);

                    if (working.IsFailure) { _healthy = false; return working.Error; }

                    using GrimoireOfflineTransitionJournalOpenedFile file = working.Value;

                    byte[] bytes = new byte[40];

                    Magic.CopyTo(bytes, 0);

                    if (key != All) Convert.FromHexString(key).CopyTo(bytes, 8);

                    file.GetStream(FileAccess.ReadWrite).Write(bytes);

                    Result flushed = _directory.FlushWorking(file);

                    if (flushed.IsFailure) { _healthy = false; return flushed; }

                    Result published = _directory.PublishFirstNoReplace(leaf, workingLeaf);

                    if (published.IsFailure) { _healthy = false; return published; }

                    Result parent = _directory.FlushParent();

                    if (parent.IsFailure) { _healthy = false; return parent; }

                    var verified = ReadMarker(leaf, key);

                    if (verified.IsFailure) { _healthy = false; return verified.Error; }

                    using GrimoireOfflineTransitionJournalOpenedFile actual = verified.Value;

                    if (!FileHandleIdentity.IdentitiesMatch(file.Metadata.Identity, actual.Metadata.Identity))
                    {
                        _healthy = false;

                        return Unavailable;
                    }

                    _files.Add(leaf, actual.Metadata.Identity);

                    _snapshot = new(_snapshot.Keys.Add(key));
                }

                // A previous failed write must not become healthy merely because its caller supplied
                // no deltas on retry. Only a fresh instance can admit an unsafe/incomplete directory.
                return _healthy ? Result.Success() : Unavailable;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _healthy = false;

                return Unavailable;
            }
        }
    }

    internal Result Clear(ImmutableArray<string>? captured = null) => ClearAsync(captured ?? Snapshot).GetAwaiter().GetResult();

    private async Task<Result> ClearAsync(ImmutableArray<string> captured)
    {
        if (!_admission.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return Unavailable;

        await using IGrimoireWorkLease work = acquired;

        if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null) return Unavailable;

        await using IGrimoireExternalEffectGroup held = effect;

        lock (_mutations)
        {
            if (_disposed || !_healthy) return Unavailable;

            int removed = 0;

            while (removed < 100)
            {
                var expected = _files.FirstOrDefault(item => captured.Contains(KeyFromLeaf(item.Key)!));

                if (expected.Key is null) break;

                string key = KeyFromLeaf(expected.Key)!;

                var opened = ReadMarker(expected.Key, key);

                if (opened.IsFailure) return opened.Error;

                using GrimoireOfflineTransitionJournalOpenedFile actual = opened.Value;

                if (!FileHandleIdentity.IdentitiesMatch(expected.Value, actual.Metadata.Identity)) return Unavailable;

                Result deleted = _directory!.CompareUnlink(actual, expected.Key);

                if (deleted.IsFailure) return deleted;

                Result flushed = _directory.FlushParent();

                if (flushed.IsFailure) return flushed;

                _files.Remove(expected.Key);

                removed++;
            }

            var remaining = _snapshot.Keys;

            foreach (string key in captured)
                if (!_files.Keys.Any(leaf => KeyFromLeaf(leaf) == key)) remaining = remaining.Remove(key);

            _snapshot = new(remaining);

            return Result.Success();
        }
    }

    private Result EnsureDirectory(bool create)
    {
        if (_directory is not null) return Result.Success();

        var topology = NoFollowPathTopology.Classify(_path);

        if (topology.IsFailure) return topology.Error;

        if (topology.Value == NoFollowPathTopologyKind.Absent)
        {
            if (!create) return Result.Success();

            SecureFilePermissions.RequireOwnerOnlyDirectory(_path);

            string parent = Path.GetDirectoryName(_path)!;

            if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(parent, out SafeFileHandle handle, out _)) return Unavailable;

            using (handle)
                if (!BackupRestoreJournalNativeMethods.TryFlushDirectory(handle)) return Unavailable;
        }
        else if (topology.Value != NoFollowPathTopologyKind.Directory) return Unavailable;

        var opened = GrimoireOfflineTransitionJournalFilePrimitives.Open(_path);

        if (opened.IsFailure) return opened.Error;

        _directory = opened.Value;

        return Result.Success();
    }

    private Result<GrimoireOfflineTransitionJournalOpenedFile> ReadMarker(string leaf, string key)
    {
        if (_directory is null) return Unavailable;

        SecureFileOpenStatus status = _directory.OpenChild(leaf, createExclusive: false, writable: false, out SafeFileHandle? handle);

        if (status != SecureFileOpenStatus.Success || handle is null) return Unavailable;

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata)
            || metadata.Kind != FileSystemObjectKind.RegularFile || metadata.HardLinkCount != 1)
        {
            handle.Dispose();

            return Unavailable;
        }

        GrimoireOfflineTransitionJournalOpenedFile opened = new(handle, Path.Combine(_path, leaf), metadata);

        try
        {
            if (!GrimoireOfflineTransitionJournalFilePrimitives.VerifyOwnerControlledOpenedFileHandle(opened)) return Unavailable;

            FileStream stream = opened.GetStream(FileAccess.Read);

            if (stream.Length != 40) return Unavailable;

            Span<byte> bytes = stackalloc byte[40];

            stream.ReadExactly(bytes);

            if (!bytes[..8].SequenceEqual(Magic)
                || (key == All ? bytes[8..].IndexOfAnyExcept((byte)0) >= 0 : Convert.ToHexString(bytes[8..]) != key)) return Unavailable;

            GrimoireOfflineTransitionJournalOpenedFile result = opened;

            opened = null!;

            return result;
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private static string Key(string peerId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peerId)));

    private static string? KeyFromLeaf(string leaf)
    {
        int suffix = leaf.IndexOf(".deny", StringComparison.Ordinal);

        if (suffix < 0 || leaf[(suffix + 5)..] is { Length: > 0 } remainder
            && (!remainder.StartsWith(".tmp.", StringComparison.Ordinal) || remainder.Length != 37
                || !Guid.TryParseExact(remainder[5..], "N", out _))) return null;

        string key = leaf[..suffix];

        return key == All || key.Length == 64 && key.All(character => char.IsAsciiHexDigit(character) && !char.IsLower(character))
            ? key : null;
    }

    public void Dispose()
    {
        lock (_mutations)
        {
            _disposed = true;

            _healthy = false;

            _directory?.Dispose();
        }
    }

    // Volatile publication carries one immutable, non-enumerable holder. Authority readers need
    // only its exact immutable lookup, and never acquire a filesystem/mutation lock.
    private sealed class DenialSnapshot(ImmutableArray<string> keys)
    {
        internal ImmutableArray<string> Keys { get; } = keys;
    }

    private static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable, "The inbound authority denial journal is unavailable.");
}
