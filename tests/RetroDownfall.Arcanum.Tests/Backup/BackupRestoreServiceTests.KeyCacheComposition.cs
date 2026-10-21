using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_restore_composition_invalidates_the_shared_key_provider_with_or_without_Covenant(bool covenant)
    {
        ServiceCollection services = new();

        services.AddLogging();

        services.AddArcanumCliClientStack();

        services.Configure<ArcanumSettings>(settings => settings.Features.Covenant = covenant);

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new TestApiKeySecretStore("unused"));

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        BackupRestoreService restore = Assert.IsType<BackupRestoreService>(
            scope.ServiceProvider.GetRequiredService<IBackupRestoreService>());

        FieldInfo optionsField = typeof(BackupRestoreService).GetField("_options",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        BackupRestoreServiceOptions options = Assert.IsType<BackupRestoreServiceOptions>(optionsField.GetValue(restore));

        Assert.Same(provider.GetRequiredService<FileEncryptionKeyProvider>(), options.FileEncryptionKeys);
    }
}
