using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Uses a unique testing home so the counter's real JSONL and filesystem behavior never touches the
/// developer profile. The process-global environment is isolated by the collection.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class BatchRequestCounterTests : IDisposable
{

    private readonly string _ownedRoot = Path.Combine(
        Path.GetTempPath(),
        "arcanum-tests",
        $"batch-request-counter-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public BatchRequestCounterTests()
    {

        CaptureEnvironment("ASPNETCORE_ENVIRONMENT");
        CaptureEnvironment("DOTNET_ENVIRONMENT");
        CaptureEnvironment("ARCANUM_TEST_HOME");

        try
        {

            global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _ownedRoot);
            Directory.CreateDirectory(_ownedRoot);

        }
        catch
        {

            RestoreEnvironment();
            DeleteOwnedRoot();
            throw;

        }

    }

    [Fact]
    public void FileStorage_IsScopedToOwnedTestingHome()
    {

        Assert.Equal(
            Path.GetFullPath(_ownedRoot),
            Path.GetFullPath(global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME")!));
        Assert.Equal("Testing", global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
        Assert.Equal("Testing", global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal(
            Path.Combine(_ownedRoot, ".config", "arcanum", "files"),
            ArcanumPaths.FilesDirectory);

    }

    [Fact]
    public async Task ComputeAsync_MixedFiles_CountsEveryOutcome()
    {

        Guid inputFileId = await WriteFileAsync(
            """
            {"custom_id":"one"}

               
            {"custom_id":"two"}
            """);
        Guid outputFileId = await WriteFileAsync(
            """
            {"id":"response-1","custom_id":"ok","response":null,"error":null}

            {not-json
            {"id":"response-2","custom_id":"failed","response":null,"error":{"code":"bad_request","message":"failed"}}
            """);
        Guid errorFileId = await WriteFileAsync(
            """
            {"line":1,"error":"invalid"}
               
            {"line":2,"error":"also invalid"}
            """);
        BatchRecord record = CreateRecord(inputFileId, outputFileId, errorFileId);

        OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(
            record,
            CancellationToken.None);

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Completed);
        Assert.Equal(4, counts.Failed);

    }

    [Fact]
    public async Task ComputeAsync_MissingInputAndOptionalFiles_ReturnsZeroCounts()
    {

        BatchRecord record = CreateRecord(
            inputFileId: Guid.NewGuid(),
            outputFileId: null,
            errorFileId: null);

        OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(
            record,
            CancellationToken.None);

        Assert.Equal(OpenAiBatchRequestCounts.Empty, counts);

    }

    [Fact]
    public async Task ComputeAsync_MissingReferencedOutputAndErrorFiles_AreIgnored()
    {

        Guid inputFileId = await WriteFileAsync("""{"custom_id":"only"}""");
        BatchRecord record = CreateRecord(
            inputFileId,
            outputFileId: Guid.NewGuid(),
            errorFileId: Guid.NewGuid());

        OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(
            record,
            CancellationToken.None);

        Assert.Equal(new OpenAiBatchRequestCounts(Total: 1, Completed: 0, Failed: 0), counts);

    }

    [Fact]
    public async Task ComputeAsync_LockedFiles_AreTreatedAsBestEffortZero()
    {

        Guid inputFileId = await WriteFileAsync("""{"custom_id":"input"}""");
        Guid outputFileId = await WriteFileAsync(
            """{"id":"response","custom_id":"ok","response":null,"error":null}""");
        Guid errorFileId = await WriteFileAsync("""{"line":1,"error":"invalid"}""");
        using FileStream inputLock = LockFile(inputFileId);
        using FileStream outputLock = LockFile(outputFileId);
        using FileStream errorLock = LockFile(errorFileId);

        OpenAiBatchRequestCounts counts = await BatchRequestCounter.ComputeAsync(
            CreateRecord(inputFileId, outputFileId, errorFileId),
            CancellationToken.None);

        Assert.Equal(OpenAiBatchRequestCounts.Empty, counts);

    }

    [Fact]
    public async Task ComputeAsync_CancelledRead_PropagatesCancellation()
    {

        Guid inputFileId = await WriteFileAsync("""{"custom_id":"input"}""");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BatchRequestCounter.ComputeAsync(
                CreateRecord(inputFileId, outputFileId: null, errorFileId: null),
                cancellation.Token));

    }

    private async Task<Guid> WriteFileAsync(string content)
    {

        Guid id = Guid.NewGuid();
        string path = ResolveOwnedPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return id;

    }

    private FileStream LockFile(Guid id) =>
        new(
            ResolveOwnedPath(id),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

    private string ResolveOwnedPath(Guid id)
    {

        string path = UploadedFileStorage.ResolvePath(id);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_ownedRoot, ".config", "arcanum", "files")),
            Path.GetFullPath(Path.GetDirectoryName(path)!));
        return path;

    }

    private static BatchRecord CreateRecord(
        Guid inputFileId,
        Guid? outputFileId,
        Guid? errorFileId) =>
        new(
            Id: Guid.NewGuid(),
            InputFileId: inputFileId,
            Endpoint: "/v1/chat/completions",
            Status: BatchStatuses.Completed,
            CreatedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            OutputFileId: outputFileId,
            ErrorFileId: errorFileId);

    public void Dispose()
    {

        try
        {

            DeleteOwnedRoot();

        }
        finally
        {

            RestoreEnvironment();

        }

    }

    private void CaptureEnvironment(string name) =>
        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

    private void RestoreEnvironment()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

    }

    private void DeleteOwnedRoot()
    {

        if (Directory.Exists(_ownedRoot))
        {

            Directory.Delete(_ownedRoot, recursive: true);

        }

    }

}
