using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

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

    [Fact]
    public async Task WriteCodexAsync_symlinked_codex_escaping_the_root_leaves_the_target_untouched()
    {

        string target = Path.Combine(_outside, "authorized_keys");

        await File.WriteAllTextAsync(target, "original-secret");

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.CreateSymbolicLink(codexPath, target);

        IResult? failure = await CodexEndpoints.WriteCodexAsync(
            _root,
            codexPath,
            "attacker-content",
            CreateSettings(),
            "trace",
            CancellationToken.None);

        Assert.Equal("original-secret", await File.ReadAllTextAsync(target));

        Assert.NotNull(failure);

    }

    [Fact]
    public async Task ReadCodexDtoAsync_symlinked_codex_escaping_the_root_does_not_disclose_the_target()
    {

        string target = Path.Combine(_outside, "credentials");

        await File.WriteAllTextAsync(target, "aws_secret_access_key = hunter2");

        string codexPath = Path.Combine(_root, "CODEX.md");

        File.CreateSymbolicLink(codexPath, target);

        Result<CodexContentDto> codex = await CodexEndpoints.ReadCodexDtoAsync(
            _root,
            codexPath,
            CreateSettings(),
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
            CreateSettings(),
            "trace",
            CancellationToken.None);

        Assert.Null(failure);

        Result<CodexContentDto> codex = await CodexEndpoints.ReadCodexDtoAsync(
            _root,
            codexPath,
            CreateSettings(),
            CancellationToken.None);

        Assert.True(codex.IsSuccess);

        Assert.True(codex.Value.Exists);

        Assert.Equal("# Codex\n\nSpells go here.", codex.Value.Content);

    }

    [Fact]
    public async Task ReadCodexDtoAsync_reports_a_missing_codex_as_absent_rather_than_failing()
    {

        Result<CodexContentDto> codex = await CodexEndpoints.ReadCodexDtoAsync(
            _root,
            Path.Combine(_root, "CODEX.md"),
            CreateSettings(),
            CancellationToken.None);

        Assert.True(codex.IsSuccess);

        Assert.False(codex.Value.Exists);

        Assert.Equal(string.Empty, codex.Value.Content);

    }

    private static IOptionsSnapshot<ArcanumSettings> CreateSettings() =>
        new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings());

}
