using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class FileEncryptionRuntimeStatusTests
{
    [Fact]
    public void Runtime_status_begins_pending_and_publishes_safe_fixed_details()
    {
        FileEncryptionRuntimeStatus status = new();

        Assert.Equal(FileEncryptionRuntimeState.Pending, status.Current.State);

        status.PublishDeferred();

        Assert.Equal(FileEncryptionRuntimeState.Deferred, status.Current.State);
        Assert.DoesNotContain("secret", status.Current.Detail, StringComparison.OrdinalIgnoreCase);

        status.PublishReady();

        Assert.Equal(FileEncryptionRuntimeState.Ready, status.Current.State);

        status.PublishUnavailable();

        Assert.Equal(FileEncryptionRuntimeState.Unavailable, status.Current.State);
    }

    [Fact]
    public void Deferred_publication_cannot_regress_a_ready_key_provider()
    {
        FileEncryptionRuntimeStatus status = new();
        status.PublishReady();

        status.PublishDeferred();

        Assert.Equal(FileEncryptionRuntimeState.Ready, status.Current.State);
    }
}
