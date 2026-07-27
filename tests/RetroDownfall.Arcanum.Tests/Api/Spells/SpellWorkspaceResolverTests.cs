using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Spells;

public sealed class SpellWorkspaceResolverTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void Resolve_ExplicitWorkspace_ReturnsNormalizedPath()
    {
        SpellWorkspaceResolver resolver = CreateResolver(allowedRoots: [_workspace.Root]);

        Result<string?> result = resolver.Resolve(_workspace.Root);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(_workspace.Root), result.Value);
    }

    [Fact]
    public void Resolve_MissingDirectory_Fails()
    {
        string missing = Path.Combine(_workspace.Root, "missing-dir");

        SpellWorkspaceResolver resolver = CreateResolver(allowedRoots: [_workspace.Root]);

        Result<string?> result = resolver.Resolve(missing);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.InvalidWorkspace", result.Error.Code);
    }

    [Fact]
    public void Resolve_OutsideAllowlist_Fails()
    {
        SpellWorkspaceResolver resolver = CreateResolver(allowedRoots: [Path.Combine(_workspace.Root, "allowed")]);

        Directory.CreateDirectory(Path.Combine(_workspace.Root, "allowed"));

        Result<string?> result = resolver.Resolve(_workspace.Root);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.PathNotAllowed", result.Error.Code);
    }

    [Fact]
    public void ResolveRequired_EmptyAllowlist_FailsPathNotAllowed()
    {
        SpellWorkspaceResolver resolver = CreateResolver(allowedRoots: []);

        Result<string> result = resolver.ResolveRequired(null);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.PathNotAllowed", result.Error.Code);
    }

    [Fact]
    public void Resolve_UsesHostContextWhenPresent()
    {
        string sub = _workspace.CreateSubdir("host");

        SpellWorkspaceResolver resolver = CreateResolver(
            allowedRoots: [_workspace.Root],
            hostPath: sub);

        Result<string?> result = resolver.Resolve(null);

        Assert.True(result.IsSuccess);

        Assert.Equal(Path.GetFullPath(sub), result.Value);
    }

    private SpellWorkspaceResolver CreateResolver(string[] allowedRoots, string? hostPath = null)
    {
        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings { SpellWorkspaceRoots = allowedRoots },
        };

        IHostWorkspaceContext host = new FakeHostWorkspaceContext(hostPath);

        return new SpellWorkspaceResolver(host, Options.Create(settings));
    }

    private sealed class FakeHostWorkspaceContext(string? path) : IHostWorkspaceContext
    {

        public string? WorkspacePath => path;

    }

}

public sealed class SpellApiResultsTests
{

    [Fact]
    public void MapOptionalWorkspaceFailure_PathNotAllowed_Returns403()
    {
        Error error = new("Spell.PathNotAllowed", "denied");

        Result<string?> failure = Result<string?>.Failure(error);

        IResult? mapped = SpellApiResults.MapOptionalWorkspaceFailure<string>(
            failure,
            traceId: "trace-1",
            ArcanumJsonContext.Default.ApiResponseString,
            out string? workspace);

        Assert.Null(workspace);

        Assert.NotNull(mapped);

        Assert.IsType<JsonHttpResult<ApiResponse<string>>>(mapped);
    }

    [Fact]
    public void MapRequiredWorkspaceFailure_InvalidWorkspace_Returns400()
    {
        Error error = new("Spell.InvalidWorkspace", "bad path");

        Result<string> failure = Result<string>.Failure(error);

        IResult? mapped = SpellApiResults.MapRequiredWorkspaceFailure<string>(
            failure,
            traceId: "trace-2",
            ArcanumJsonContext.Default.ApiResponseString,
            out string workspace);

        Assert.Equal(string.Empty, workspace);

        Assert.NotNull(mapped);

        Assert.IsType<JsonHttpResult<ApiResponse<string>>>(mapped);
    }

    [Fact]
    public void MapOptionalWorkspaceFailure_Success_ReturnsNullAndWorkspace()
    {
        Result<string?> success = Result<string?>.Success("/tmp/ws");

        IResult? mapped = SpellApiResults.MapOptionalWorkspaceFailure<string>(
            success,
            traceId: "trace-3",
            ArcanumJsonContext.Default.ApiResponseString,
            out string? workspace);

        Assert.Null(mapped);

        Assert.Equal("/tmp/ws", workspace);
    }

}
