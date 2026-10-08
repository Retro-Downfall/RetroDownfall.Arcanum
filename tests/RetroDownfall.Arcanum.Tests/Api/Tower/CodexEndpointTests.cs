using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// Containment coverage for the codex read/write helpers behind
/// <c>GET|PUT /api/campaigns/{id}/codex</c> and <c>GET|PUT /api/codex</c>. A campaign root can be an
/// untrusted repository the operator cloned, so a <c>CODEX.md</c> symlink that points outside the root
/// must never be read through or written through.
/// </summary>
public sealed class CodexEndpointTests : IDisposable
{
    private readonly string _base;

    private readonly string _root;

    private readonly string _outside;

    public CodexEndpointTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"codex-endpoint-{Guid.NewGuid():N}");

        _root = Path.Combine(_base, "campaign");

        _outside = Path.Combine(_base, "outside");

        Directory.CreateDirectory(_root);

        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [SkippableFact]
    public async Task WriteCodexAsync_symlinked_codex_escaping_the_root_leaves_the_target_untouched()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_outside, "authorized_keys");

        await File.WriteAllTextAsync(target, "original-secret");

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.CreateSymbolicLink(codexPath, target);

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "attacker-content",
            "trace",
            CancellationToken.None);

        Assert.Equal("original-secret", await File.ReadAllTextAsync(target));

        Assert.NotNull(failure);
    }

    [SkippableFact]
    public async Task ReadCodexDtoAsync_symlinked_codex_escaping_the_root_does_not_disclose_the_target()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_outside, "credentials");

        await File.WriteAllTextAsync(target, "aws_secret_access_key = hunter2");

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.CreateSymbolicLink(codexPath, target);

        Result<CodexContentDto> codex = await CodexEndpoints.ReadCodexDtoAsync(
            _root,
            codexPath,
            CancellationToken.None);

        Assert.True(codex.IsFailure);

        Assert.Equal("Codex.PathNotContained", codex.Error.Code);
    }

    [Fact]
    public async Task WriteCodexAsync_then_ReadCodexDtoAsync_round_trips_a_contained_codex()
    {
        string codexPath = Path.Combine(_root, "CODEX.md");

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "# Codex\n\nSpells go here.",
            "trace",
            CancellationToken.None);

        Assert.Null(failure);

        Result<CodexContentDto> codex = await CodexEndpoints.ReadCodexDtoAsync(
            _root,
            codexPath,
            CancellationToken.None);

        Assert.True(codex.IsSuccess);

        Assert.True(codex.Value.Exists);

        Assert.Equal("# Codex\n\nSpells go here.", codex.Value.Content);
    }

    /// <summary>
    /// A body whose <c>content</c> is missing or null is the caller's mistake, not a server fault.
    /// </summary>
    [Fact]
    public async Task WriteCodexAsync_with_null_content_answers_400_and_writes_nothing()
    {
        string codexPath = Path.Combine(_root, "CODEX.md");

        await File.WriteAllTextAsync(codexPath, "previous");

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            null!,
            "trace",
            CancellationToken.None);

        Assert.NotNull(failure);

        (int status, string body) = await ExecuteAsync(failure);

        Assert.Equal(StatusCodes.Status400BadRequest, status);

        Assert.Contains(ErrorCodes.Validation.InvalidBody, body, StringComparison.Ordinal);

        Assert.Equal("previous", await File.ReadAllTextAsync(codexPath));
    }

    /// <summary>
    /// The write goes through a same-directory temporary file and an atomic replace, so a write that stops
    /// part-way can never leave the codex truncated: the destination is a new file or the old one.
    /// </summary>
    [Fact]
    public async Task WriteCodexAsync_replaces_the_file_rather_than_rewriting_it_in_place()
    {
        string codexPath = Path.Combine(_root, "CODEX.md");

        await File.WriteAllTextAsync(codexPath, "previous");

        Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(codexPath, out FileHandleIdentity before));

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "replacement",
            "trace",
            CancellationToken.None);

        Assert.Null(failure);

        Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(codexPath, out FileHandleIdentity after));

        Assert.False(FileHandleIdentity.IdentitiesMatch(before, after), "The write rewrote the file in place.");

        Assert.Equal("replacement", await File.ReadAllTextAsync(codexPath));

        Assert.Equal([codexPath], Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A request that was already cancelled when the write began leaves a whole codex and no temporary file.
    /// </summary>
    /// <remarks>
    /// This pins only what its name says: the token is cancelled before the write starts, which was
    /// harmless before the atomic replace too, so it would pass with the replace reverted. The guarantee
    /// for a write that stops part-way is carried by
    /// <see cref="WriteCodexAsync_replaces_the_file_rather_than_rewriting_it_in_place"/>, which fails if
    /// the codex is rewritten in place, and by the replace running on a non-cancelable token once it has
    /// begun, which a test cannot time and so is not claimed here.
    /// </remarks>
    [Fact]
    public async Task WriteCodexAsync_with_an_already_cancelled_request_leaves_a_whole_codex_and_no_temporary_file()
    {
        string codexPath = Path.Combine(_root, "CODEX.md");

        await File.WriteAllTextAsync(codexPath, "previous");

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        try
        {
            _ = await CodexEndpoints.WriteCodexAsync(
                _root,
                codexPath,
                "replacement",
                "trace",
                cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            // A request that was already gone is allowed to stop before it starts.
        }

        string content = await File.ReadAllTextAsync(codexPath);

        Assert.True(
            content is "previous" or "replacement",
            $"The codex was left holding neither its old nor its new content: '{content}'.");

        Assert.Equal([codexPath], Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// The containment check runs before anything is created, so a write refused for escaping the root
    /// has not already made a directory outside it.
    /// </summary>
    [SkippableFact]
    public async Task WriteCodexAsync_through_a_symlinked_parent_creates_nothing_outside_the_root()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), _outside);

        string codexPath = Path.Combine(_root, "linked", "nested", "CODEX.md");

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "attacker-content",
            "trace",
            CancellationToken.None);

        Assert.NotNull(failure);

        (int status, string body) = await ExecuteAsync(failure);

        Assert.Equal(StatusCodes.Status400BadRequest, status);

        Assert.Contains(ErrorCodes.Codex.PathNotContained, body, StringComparison.Ordinal);

        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [SkippableFact]
    public async Task DeleteCodex_symlinked_parent_is_refused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_outside, "CODEX.md");

        await File.WriteAllTextAsync(target, "outside-codex");

        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), _outside);

        IResult result = CodexEndpoints.DeleteCodex(
            _root,
            Path.Combine(_root, "linked", "CODEX.md"),
            "trace");

        (int status, string body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status400BadRequest, status);

        Assert.Contains(ErrorCodes.Codex.PathNotContained, body, StringComparison.Ordinal);

        Assert.True(File.Exists(target));
    }

    /// <summary>
    /// A <c>CODEX.md</c> that is itself a link is unlinked, never followed: the documented behaviour of the
    /// delete routes, and the one way an operator removes a hostile link through the API.
    /// </summary>
    [SkippableFact]
    public async Task DeleteCodex_symlinked_codex_removes_the_link_and_leaves_its_target()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_outside, "authorized_keys");

        await File.WriteAllTextAsync(target, "original-secret");

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.CreateSymbolicLink(codexPath, target);

        IResult result = CodexEndpoints.DeleteCodex(_root, codexPath, "trace");

        (int status, _) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status204NoContent, status);

        Assert.False(File.Exists(codexPath) || new FileInfo(codexPath).LinkTarget is not null);

        Assert.Equal("original-secret", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// A <c>CODEX.md</c> that is itself a link is never written through, even when its target lies inside the
    /// root, and the refusal says that rather than claiming the path escapes the root.
    /// </summary>
    [SkippableFact]
    public async Task WriteCodexAsync_symlinked_codex_inside_the_root_is_refused_as_a_link_and_left_alone()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_root, "notes.md");

        await File.WriteAllTextAsync(target, "inside-target");

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.CreateSymbolicLink(codexPath, target);

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "replacement",
            "trace",
            CancellationToken.None);

        Assert.NotNull(failure);

        (int status, string body) = await ExecuteAsync(failure);

        Assert.Equal(StatusCodes.Status400BadRequest, status);

        Assert.Contains(ErrorCodes.Workspace.SymbolicLinkEscape, body, StringComparison.Ordinal);

        Assert.DoesNotContain(ErrorCodes.Codex.PathNotContained, body, StringComparison.Ordinal);

        Assert.Contains("link", body, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("inside-target", await File.ReadAllTextAsync(target));

        Assert.NotNull(new FileInfo(codexPath).LinkTarget);
    }

    /// <summary>
    /// A hard-linked <c>CODEX.md</c> is the other shape the atomic replace refuses, and is named the same way.
    /// </summary>
    [Fact]
    public async Task WriteCodexAsync_hard_linked_codex_is_refused_as_a_link_and_left_alone()
    {
        string other = Path.Combine(_root, "other-name.md");

        await File.WriteAllTextAsync(other, "shared-inode");

        string codexPath = Path.Combine(_root, "CODEX.md");

        Assert.True(HardLinkTestSupport.TryCreate(codexPath, other));

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "replacement",
            "trace",
            CancellationToken.None);

        Assert.NotNull(failure);

        (int status, string body) = await ExecuteAsync(failure);

        Assert.Equal(StatusCodes.Status400BadRequest, status);

        Assert.Contains(ErrorCodes.Workspace.SymbolicLinkEscape, body, StringComparison.Ordinal);

        Assert.Equal("shared-inode", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task DeleteCodex_of_a_missing_file_is_still_a_204()
    {
        IResult result = CodexEndpoints.DeleteCodex(_root, Path.Combine(_root, "CODEX.md"), "trace");

        (int status, _) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status204NoContent, status);
    }

    [Fact]
    public async Task DeleteCodex_that_cannot_unlink_answers_a_mapped_error_instead_of_throwing()
    {
        // A directory where the file should be: File.Delete refuses it with an UnauthorizedAccessException.
        string codexPath = Path.Combine(_root, "CODEX.md");

        Directory.CreateDirectory(codexPath);

        IResult result = CodexEndpoints.DeleteCodex(_root, codexPath, "trace");

        (int status, string body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status500InternalServerError, status);

        Assert.Contains(ErrorCodes.Workspace.DeleteFailed, body, StringComparison.Ordinal);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        DefaultHttpContext context = new()
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        MemoryStream body = new();

        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task ReadCodexDtoAsync_reports_a_missing_codex_as_absent_rather_than_failing()
    {
        Result<CodexContentDto> codex = await CodexEndpoints.ReadCodexDtoAsync(
            _root,
            Path.Combine(_root, "CODEX.md"),
            CancellationToken.None);

        Assert.True(codex.IsSuccess);

        Assert.False(codex.Value.Exists);

        Assert.Equal(string.Empty, codex.Value.Content);
    }
}
