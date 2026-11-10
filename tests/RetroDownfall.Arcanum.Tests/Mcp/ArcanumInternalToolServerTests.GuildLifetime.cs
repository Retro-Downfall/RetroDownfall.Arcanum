using System.Reflection;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [Fact]
    public async Task Guild_failed_ordinary_same_id_send_cannot_remove_the_original_private_authority_binding()
    {
        await using TestMcpSession session = await CreateSessionAsync(maxJsonRpcLineBytes: 4096);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ManualResetEventSlim release = new();

        int paused = 0;

        session.Server.LineHandlerFaultForTesting = _ =>
        {
            if (Interlocked.Exchange(ref paused, 1) == 0)
            {
                entered.TrySetResult();

                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }

            return null;
        };

        using JsonDocument original = JsonDocument.Parse("{\"name\":\"read_file\",\"arguments\":{\"relativePath\":\"notes/alpha.txt\",\"startLine\":1,\"endLine\":2}}");

        const int requestId = 7711;

        using (GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext()))
        {
            await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", original.RootElement);
        }

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            using JsonDocument oversized = JsonDocument.Parse("{\"name\":\"read_file\",\"arguments\":{\"junk\":\"" + new string('x', 8192) + "\"}}");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.WriteRequestWithFixedIdAsync(requestId, "tools/call", oversized.RootElement));
        }
        finally
        {
            release.Set();
        }

        JsonRpcResponse response = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        McpToolsCallResultWire result = JsonSerializer.Deserialize(response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(result.IsError);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result.Content[0].Text);

        Assert.DoesNotContain("line one", result.Content[0].Text);
    }

    [Fact]
    public async Task Guild_early_cancel_releases_only_the_registered_request_so_its_id_can_be_reused()
    {
        await using TestMcpSession session = await CreateSessionAsync();

        const int requestId = 7712;

        await session.SendCancelNotificationAsync(requestId);

        FieldInfo tombstonesField = typeof(ArcanumInternalToolServer).GetField("_earlyCancelTombstones", BindingFlags.Instance | BindingFlags.NonPublic)!;

        FieldInfo connectionField = typeof(ArcanumInternalToolServer).GetField("_ambientConnectionKey", BindingFlags.Instance | BindingFlags.NonPublic)!;

        ExpiringRequestBindingStore<bool> tombstones = Assert.IsType<ExpiringRequestBindingStore<bool>>(tombstonesField.GetValue(session.Server));

        string connection = Assert.IsType<string>(connectionField.GetValue(session.Server));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        while (!tombstones.TryResolve(connection, requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), out _))
        {
            await Task.Delay(1, deadline.Token);
        }

        using JsonDocument parameters = JsonDocument.Parse("{\"name\":\"read_file\",\"arguments\":{\"relativePath\":\"notes/alpha.txt\",\"startLine\":1,\"endLine\":2}}");

        using IDisposable scope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext());

        await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", parameters.RootElement);

        JsonRpcResponse cancelled = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        McpToolsCallResultWire cancelledResult = JsonSerializer.Deserialize(cancelled.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(cancelledResult.IsError);

        Assert.Equal("Tool call was cancelled.", cancelledResult.Content[0].Text);

        await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", parameters.RootElement);

        JsonRpcResponse reused = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        McpToolsCallResultWire reusedResult = JsonSerializer.Deserialize(reused.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(reusedResult.IsError);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, reusedResult.Content[0].Text);
    }
    [Fact]
    public async Task Guild_malformed_raw_call_releases_its_private_binding_before_request_id_reuse()
    {
        await using TestMcpSession session = await CreateSessionAsync();

        using IDisposable scope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext());

        const int requestId = 7713;

        using JsonDocument malformed = JsonDocument.Parse("{\"name\":\"read_file\"}");

        await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", malformed.RootElement);

        JsonRpcResponse rejected = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        McpToolsCallResultWire rejectedResult = JsonSerializer.Deserialize(rejected.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(rejectedResult.IsError);

        Assert.Equal("tools/call params were not valid JSON.", rejectedResult.Content[0].Text);

        using JsonDocument valid = JsonDocument.Parse("{\"name\":\"read_file\",\"arguments\":{\"relativePath\":\"notes/alpha.txt\",\"startLine\":1,\"endLine\":2}}");

        await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", valid.RootElement);

        JsonRpcResponse reused = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        McpToolsCallResultWire result = JsonSerializer.Deserialize(reused.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(result.IsError);

        Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, result.Content[0].Text);
    }
    [Fact]
    public async Task Guild_scope_cannot_replace_an_ordinary_inflight_request_identity_before_it_settles()
    {
        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string target = Path.Combine(_workspace.Root, "notes", "alpha.txt");

        SecureFileReader.AfterRegularFileOpenedForTests = path =>
        {
            if (string.Equals(path, target, StringComparison.Ordinal))
            {
                opened.TrySetResult();

                release.Task.GetAwaiter().GetResult();
            }
        };

        try
        {
            await using TestMcpSession session = await CreateSessionAsync();

            using JsonDocument parameters = JsonDocument.Parse("{\"name\":\"read_file\",\"arguments\":{\"relativePath\":\"notes/alpha.txt\",\"startLine\":1,\"endLine\":2}}");

            const int requestId = 7714;

            await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", parameters.RootElement);

            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

            try
            {
                using IDisposable guildScope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext());

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    session.WriteRequestWithFixedIdAsync(requestId, "tools/call", parameters.RootElement));
            }
            finally
            {
                release.TrySetResult();
            }

            JsonRpcResponse ordinary = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

            McpToolsCallResultWire ordinaryResult = JsonSerializer.Deserialize(ordinary.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

            Assert.False(ordinaryResult.IsError);

            Assert.Contains("line one", ordinaryResult.Content[0].Text);

            using IDisposable retryScope = GuildToolInvocationAmbient.Begin(GuildToolBoundaryTests.CreateContext());

            await session.WriteRequestWithFixedIdAsync(requestId, "tools/call", parameters.RootElement);

            JsonRpcResponse retry = await session.ReadNextResponseAsync().WaitAsync(TimeSpan.FromSeconds(10));

            McpToolsCallResultWire retryResult = JsonSerializer.Deserialize(retry.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

            Assert.True(retryResult.IsError);

            Assert.Contains(ErrorCodes.Guild.ToolBoundaryUnavailable, retryResult.Content[0].Text);
        }
        finally
        {
            release.TrySetResult();

            SecureFileReader.AfterRegularFileOpenedForTests = null;
        }
    }
}
