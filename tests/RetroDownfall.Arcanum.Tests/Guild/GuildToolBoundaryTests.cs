using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using ModelContextProtocol.Protocol;
using Result = RetroDownfall.Arcanum.Core.Primitives.Result;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildToolBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_direct_native_entry_refuses_without_disclosing_host_information(bool systemInfo)
    {
        AIFunction actualNative = systemInfo ? new ArcanumSystemInfoTool() : new ArcanumLocalTimeTool();

        using IDisposable scope = GuildToolInvocationAmbient.Begin(CreateContext());

        object? output = await actualNative.InvokeAsync(new AIFunctionArguments());

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, output?.ToString());

        Assert.DoesNotContain("OS:", output?.ToString());
    }

    [Fact]
    public async Task Retained_native_function_refuses_before_effect_without_a_resource_adapter()
    {
        CountingFunction native = new();

        GuildToolInvocationContext context = CreateContext();

        AIFunction retained = context.Bind(native, new(GuildToolSourceKind.BuiltIn, "arcanum", native.Name));

        object? result = await retained.InvokeAsync(new AIFunctionArguments());

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result?.ToString());

        Assert.Equal(0, native.Calls);

        Assert.DoesNotContain("signet", retained.JsonSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Advertisement_omits_tools_until_a_host_effect_adapter_exists()
    {
        CountingFunction native = new();

        GuildToolInvocationContext context = CreateContext();

        AIFunction? advertised = await context.AdvertiseAsync(native,
            new(GuildToolSourceKind.BuiltIn, "arcanum", native.Name), default);

        Assert.Null(advertised);

        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public async Task Retained_wrapper_rechecks_current_host_eligibility_before_dispatch()
    {
        bool eligible = true;

        CountingFunction native = new();

        GuildToolInvocationContext context = CreateContext(_ => eligible);

        AIFunction retained = context.Bind(native, new(GuildToolSourceKind.BuiltIn, "arcanum", native.Name));

        eligible = false;

        object? result = await retained.InvokeAsync(new AIFunctionArguments());

        Assert.Contains(ErrorCodes.Guild.ToolNotGranted, result?.ToString());

        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void Exact_binding_refuses_alias_instead_of_reinterpreting_the_function_name()
    {
        GuildToolInvocationContext context = CreateContext();

        Assert.Throws<ArgumentException>(() => context.Bind(new CountingFunction(),
            new(GuildToolSourceKind.BuiltIn, "arcanum", "alias")));
    }

    [Fact]
    public async Task Mcp_bridge_clones_keep_private_binding_and_cannot_use_another_registration_fallback()
    {
        CountingMcpClient client = new();

        CountingMcpClient fallback = new();

        McpBridgeTool retained = new McpBridgeTool("lookup", "lookup", EmptySchema(), client, 4096,
                fallbackClient: fallback)
            .WithGuildAuthority(CreateContext(), new(GuildToolSourceKind.Adapter, "local-registration", "lookup"))
            .WithRequestTimeout(TimeSpan.FromSeconds(1))
            .WithTrustedStructuredResult(TrustedStructuredToolResultKind.WorkspacePatch)
            .WithClient(client);

        object? result = await retained.InvokeAsync(new AIFunctionArguments());

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result?.ToString());

        Assert.Equal(0, client.Calls);

        Assert.Equal(0, fallback.Calls);
    }

    internal static GuildToolInvocationContext CreateContext(Func<GuildToolGrant, bool>? eligibility = null)
    {
        Guid root = Guid.NewGuid();

        GuildExecutionHandle execution = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), root, root, 1, 0);

        return new(execution, new UnavailableToolAuthority(), eligibility ?? (_ => true));
    }

    private static JsonElement EmptySchema()
    {
        using JsonDocument schema = JsonDocument.Parse("{}");

        return schema.RootElement.Clone();
    }

    private sealed class CountingFunction : AIFunction
    {
        public int Calls { get; private set; }

        public override string Name => "lookup";

        public override JsonElement JsonSchema => EmptySchema();

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult<object?>("effect");
        }
    }

    private sealed class CountingMcpClient : IMcpClient
    {
        public int Calls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments,
            TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new McpTransportUnavailableException("not dispatched", McpRequestDispatchState.NotDispatched);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableToolAuthority : IGuildToolAuthority
    {
        public Task<Result> VerifyToolAsync(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable,
            CancellationToken cancellationToken) => Task.FromResult(Result.Failure(
                new(ErrorCodes.Guild.ToolBoundaryUnavailable, "No effect adapter is installed.")));

        public Task<Result<T>> AdmitToolAsync<T>(GuildExecutionHandle execution, GuildToolGrant tool, bool boundaryAvailable,
            Func<GuildVerifiedAuthority, CancellationToken, Task<Result<T>>> effect, CancellationToken cancellationToken) =>
            Task.FromResult(Result<T>.Failure(new(ErrorCodes.Guild.ToolBoundaryUnavailable, "No effect adapter is installed.")));
    }
}
