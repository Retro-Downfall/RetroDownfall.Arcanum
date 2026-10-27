using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed partial class SessionRepositoryTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForkAsync_retains_copy_time_identity_across_reconstructed_plans(bool replaceVerifiedCopy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateTempSubdirectory("arcanum-fork-authority-").FullName;

        Guid sourceId = Guid.NewGuid();

        _db!.Sessions.Add(NewSession(sourceId, "fork authority", DateTimeOffset.UtcNow));

        await _db.SaveChangesAsync();

        SessionAttachmentStore sourceStore = new(
            _db, Options.Create(new ArcanumSettings()), root, TestEncryptedBlobStore.Create());

        try
        {
            for (int index = 0; index < 2; index++)
            {
                await sourceStore.PersistNewAsync(sourceId, null, null,
                    "source-" + index, "source.txt", "source bytes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);
            }

            int readsInWriterTransaction = 0;

            int readsOutsideWriterTransaction = 0;

            TestEncryptedBlobStore.ReadFaultStore observedBlobs = new(
                TestEncryptedBlobStore.Create(),
                (_, _) =>
                {
                    if (_db.Database.CurrentTransaction is null)
                    {
                        readsOutsideWriterTransaction++;
                    }
                    else
                    {
                        readsInWriterTransaction++;
                    }
                });

            SessionAttachmentStore store = new(_db, Options.Create(new ArcanumSettings()), root, observedBlobs);

            List<string> copiedPaths = [];

            byte[]? replacedCiphertext = null;

            store.BeforeForkCopyForTesting = async (destination, token) =>
            {
                copiedPaths.Add(destination);

                if (!replaceVerifiedCopy || copiedPaths.Count != 2)
                {
                    return;
                }

                string firstCopy = copiedPaths[0];

                Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(firstCopy,
                    FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact original));

                replacedCiphertext = await File.ReadAllBytesAsync(firstCopy, token);

                string replacement = firstCopy + ".replacement";

                await File.WriteAllBytesAsync(replacement, replacedCiphertext, token);

                File.Move(replacement, firstCopy, overwrite: true);

                Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(firstCopy,
                    FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact swapped));

                Assert.NotEqual(original.Metadata.Identity, swapped.Metadata.Identity);
            };

            SessionRepository repository = new(
                _db, store, _fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

            await SessionBindingWriters.BackfillExistingAsync(_db);

            if (replaceVerifiedCopy)
            {
                IOException refused = await Assert.ThrowsAsync<IOException>(() => repository.ForkAsync(
                    sourceId, new ForkSessionRequest(), CancellationToken.None));

                Assert.Contains("missing or replaced bytes", refused.Message, StringComparison.Ordinal);

                Assert.False(await _db.Sessions.AsNoTracking().AnyAsync(session => session.ForkedFromSessionId == sourceId));

                Assert.Equal(replacedCiphertext, await File.ReadAllBytesAsync(copiedPaths[0]));

                Assert.False(File.Exists(copiedPaths[1]));
            }
            else
            {
                Result<Session> result = await repository.ForkAsync(sourceId, new ForkSessionRequest(), CancellationToken.None);

                Assert.True(result.IsSuccess, result.Error.Code);

                Assert.Equal(2, (await store.ListBoundAsync(result.Value.Id)).Count);
            }

            Assert.Equal(2, copiedPaths.Count);

            Assert.Equal(2, readsOutsideWriterTransaction);

            Assert.Equal(0, readsInWriterTransaction);

            Assert.Equal(2, (await sourceStore.ListBoundAsync(sourceId)).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
