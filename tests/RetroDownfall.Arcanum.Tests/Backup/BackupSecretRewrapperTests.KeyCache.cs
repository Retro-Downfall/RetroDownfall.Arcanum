using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupSecretRewrapperTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Restore_key_mutations_and_rollback_refresh_live_reads_without_revoking_borrowed_material(
        bool replace, bool priorKeyExists)
    {
        byte[] prior = RandomNumberGenerator.GetBytes(32);

        byte[] imported = RandomNumberGenerator.GetBytes(32);

        RecordingSecretStore secrets = new();

        secrets.SeedGrimoireSecret("original-grimoire");

        if (priorKeyExists)
        {
            secrets.Seed(Convert.ToBase64String(prior));
        }

        using FileEncryptionKeyProvider keys = new(secrets);

        FileEncryptionKeyMaterial? borrowed = priorKeyExists
            ? await keys.GetForReadAsync(KeyId(prior))
            : null;

        BackupSecretRewrapper rewrapper = new(secrets, keys);

        BackupSecretSnapshot snapshot = await rewrapper.CaptureAsync();

        string path = WriteRecovery("restored-grimoire", KeyId(imported), [(KeyId(imported), imported)], null);

        BackupSecretRewrapResult changed = replace
            ? await rewrapper.RewrapAsync(path, false, CancellationToken.None)
            : await rewrapper.MergeFileEncryptionKeysAsync(path, CancellationToken.None);

        Assert.Empty(changed.Issues);

        FileEncryptionKeyMaterial restored = await keys.PeekForReadAsync(KeyId(imported));

        Assert.Equal(imported, restored.MasterKey.ToArray());

        Assert.Equal(priorKeyExists && !replace ? KeyId(prior) : KeyId(imported),
            (await keys.GetForWriteAsync()).KeyId);

        if (borrowed is not null)
        {
            Assert.Equal(prior, borrowed.MasterKey.ToArray());
        }

        Assert.True((await rewrapper.RestoreAsync(snapshot)).IsSuccess);

        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => keys.GetForReadAsync(KeyId(imported)).AsTask());

        if (priorKeyExists)
        {
            Assert.Equal(prior, (await keys.GetForReadAsync(KeyId(prior))).MasterKey.ToArray());
        }

        Assert.Equal(imported, restored.MasterKey.ToArray());

        keys.Dispose();

        Assert.All(restored.MasterKey.ToArray(), value => Assert.Equal(0, value));

        if (borrowed is not null)
        {
            Assert.All(borrowed.MasterKey.ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_write_that_lands_then_throws_still_invalidates_the_previous_live_ring(bool cancelled)
    {
        byte[] prior = RandomNumberGenerator.GetBytes(32);

        byte[] imported = RandomNumberGenerator.GetBytes(32);

        RecordingSecretStore secrets = new();

        secrets.Seed(Convert.ToBase64String(prior));

        using FileEncryptionKeyProvider keys = new(secrets);

        _ = await keys.GetForReadAsync(KeyId(prior));

        BackupSecretRewrapper rewrapper = new(secrets, keys);

        BackupSecretSnapshot snapshot = await rewrapper.CaptureAsync();

        string path = WriteRecovery("restored-grimoire", KeyId(imported), [(KeyId(imported), imported)], null);

        secrets.FileEncryptionWriteFailure = cancelled
            ? new OperationCanceledException(new CancellationToken(true))
            : new IOException("The write landed before the failure.");

        Exception? failure = await Record.ExceptionAsync(
            () => rewrapper.MergeFileEncryptionKeysAsync(path, CancellationToken.None));

        Assert.NotNull(failure);

        Assert.Equal(imported, (await keys.PeekForReadAsync(KeyId(imported))).MasterKey.ToArray());

        secrets.FileEncryptionWriteFailure = null;

        Assert.True((await rewrapper.RestoreAsync(snapshot)).IsSuccess);

        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => keys.GetForReadAsync(KeyId(imported)).AsTask());
    }
}
