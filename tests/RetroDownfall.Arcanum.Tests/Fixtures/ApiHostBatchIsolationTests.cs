using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ApiHost")]
public sealed class ApiHostBatchIsolationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Metadata_host_cannot_publish_a_batch_artifact_into_a_later_test_profile()
    {
        bool scheduled = false;

        await using ArcanumWebApplicationFactory first = new()
        {
            ServiceOverrides = services =>
            {
                ServiceDescriptor? registration = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(InstallationResetRecoveryAwareHostedService<BatchProcessingService>));

                scheduled = registration is not null;

                // Replace the timer with deterministic scheduling, without changing the worker,
                // admission, real encrypted storage or the first host's private database.
                if (registration is not null)
                {
                    services.Remove(registration);
                }
            },
        };

        using HttpClient client = first.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        ByteArrayContent file = new(Encoding.UTF8.GetBytes("{}\n"));

        file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");

        form.Add(file, "file", "input.jsonl");

        form.Add(new StringContent("batch"), "purpose");

        using HttpResponseMessage uploaded = await client.PostAsync("/v1/files", form);

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        using JsonDocument upload = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());

        string fileId = upload.RootElement.GetProperty("id").GetString()!;

        using StringContent body = new($$"""{"input_file_id":"{{fileId}}","endpoint":"/v1/chat/completions"}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage created = await client.PostAsync("/v1/batches", body);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using JsonDocument batch = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        Assert.Equal("validating", batch.RootElement.GetProperty("status").GetString());

        BatchProcessingService processor = first.Services.GetRequiredService<BatchProcessingService>();

        TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        processor.AfterBatchRegisteredTestSeam = async () =>
        {
            registered.TrySetResult();

            await release.Task;
        };

        Task? tick = scheduled ? processor.TickAsync(CancellationToken.None) : null;

        try
        {
            if (scheduled)
            {
                await registered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await using ArcanumWebApplicationFactory second = new();

            release.TrySetResult();

            if (tick is not null)
            {
                await tick.WaitAsync(TimeSpan.FromSeconds(15));

                await processor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            }

            string files = Path.Combine(second.TempHome, ".config", "arcanum", "files");

            string[] foreign = Directory.Exists(files) ? Directory.GetFiles(files) : [];

            int encrypted = foreign.Count(path => File.ReadAllBytes(path).AsSpan().StartsWith("ARCABLOB"u8));

            Exception? startup = Record.Exception(() =>
            {
                using HttpClient secondClient = second.CreateAuthenticatedClient();
            });

            output.WriteLine($"Batch scheduled={scheduled}; second-profile encrypted artifacts={encrypted}; startup failure={startup?.GetType().Name ?? "none"}.");

            Assert.Null(startup);

            Assert.Equal(0, encrypted);

            Assert.False(scheduled);
        }
        finally
        {
            release.TrySetResult();

            if (tick is not null)
            {
                await tick.WaitAsync(TimeSpan.FromSeconds(15));

                await processor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    [Fact]
    public async Task Restarted_profile_keeps_the_key_for_a_real_encrypted_upload()
    {
        await using RestartableArcanumProfileFixture profile = new();

        string fileId;

        const string plaintext = "{\"restart\":\"retained\"}\n";

        await using (ArcanumWebApplicationFactory first = profile.CreateFactory())
        {
            using HttpClient client = first.CreateAuthenticatedClient();

            using MultipartFormDataContent form = new();

            ByteArrayContent file = new(Encoding.UTF8.GetBytes(plaintext));

            file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");

            form.Add(file, "file", "restart.jsonl");

            form.Add(new StringContent("batch"), "purpose");

            using HttpResponseMessage uploaded = await client.PostAsync("/v1/files", form);

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

            using JsonDocument result = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());

            fileId = result.RootElement.GetProperty("id").GetString()!;

            string blob = Assert.Single(Directory.GetFiles(Path.Combine(profile.TempHome, ".config", "arcanum", "files")));

            Assert.True(File.ReadAllBytes(blob).AsSpan().StartsWith("ARCABLOB"u8));
        }

        await using ArcanumWebApplicationFactory second = profile.CreateFactory();

        using HttpClient restarted = second.CreateAuthenticatedClient();

        using HttpResponseMessage content = await restarted.GetAsync($"/v1/files/{fileId}/content");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);

        Assert.Equal(plaintext, await content.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_different_profile_does_not_inherit_the_file_encryption_secret()
    {
        await using ArcanumWebApplicationFactory first = new();

        using HttpClient firstClient = first.CreateAuthenticatedClient();

        _ = await first.Services.GetRequiredService<IFileEncryptionKeyProvider>().GetForWriteAsync();

        ISecretStore firstSecrets = first.Services.GetRequiredService<ISecretStore>();

        Assert.Equal(SecretStoreReadStatus.Ok, (await firstSecrets.GetFileEncryptionSecretReadResultAsync()).Status);

        await using ArcanumWebApplicationFactory second = new();

        using HttpClient secondClient = second.CreateAuthenticatedClient();

        ISecretStore secondSecrets = second.Services.GetRequiredService<ISecretStore>();

        Assert.NotSame(firstSecrets, secondSecrets);

        Assert.Equal(SecretStoreReadStatus.Missing, (await secondSecrets.GetFileEncryptionSecretReadResultAsync()).Status);
    }

    [Fact]
    public async Task Metadata_host_has_no_resolved_batch_hosted_service()
    {
        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        // Inspect actual instances independently of the registration-shape test scheduler.
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), service =>
            service is InstallationResetRecoveryAwareHostedService<BatchProcessingService>);
    }

    [Fact]
    public async Task Metadata_host_retains_encryption_startup_and_the_explicit_processor()
    {
        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Assert.Contains(factory.Services.GetServices<IHostedService>(), service =>
            service is InstallationResetRecoveryAwareHostedService<FileEncryptionKeyBootstrapHostedService>);

        Assert.NotNull(factory.Services.GetRequiredService<BatchProcessingService>());
    }
}
