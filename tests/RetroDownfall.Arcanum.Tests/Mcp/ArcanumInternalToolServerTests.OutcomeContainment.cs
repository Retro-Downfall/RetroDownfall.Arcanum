using System.Reflection;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_directory_outcome_revalidates_a_parent_changed_during_worker_dispatch(bool outsideFileExists)
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX link replacement and runs on macOS and Linux only.");

        string outside = Path.Combine(Path.GetTempPath(), $"arcanum-outcome-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outside);

        string parent = Path.Combine(_workspace.Root, "raced-list-parent");

        Directory.CreateDirectory(parent);

        string target = Path.Combine(parent, "target");

        try
        {
            await using TestMcpSession session = await CreateSessionAsync();

            Assert.True(WorkspacePathPolicy.RevalidatePathBeforeIo(_workspace.Root, target));

            using SandboxedFileIo.MetadataProbeAnchor? anchor = SandboxedFileIo.CaptureMetadataAnchor(_workspace.Root, target);

            Assert.NotNull(anchor);

            // The async entrypoint queues ListDirectoryCore after this path check. Invoke that exact
            // worker boundary after a deterministic swap, without depending on Task.Run scheduling.
            Directory.Move(parent, parent + "-original");

            Directory.CreateSymbolicLink(parent, outside);

            if (outsideFileExists)
            {
                File.WriteAllText(Path.Combine(outside, "target"), "OUTSIDE_PRIVATE_CANARY");
            }

            MethodInfo worker = typeof(ArcanumInternalToolServer).GetMethod(
                "ListDirectoryCore", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.NotNull(worker);

            McpToolsCallResultWire result = Assert.IsType<McpToolsCallResultWire>(worker.Invoke(
                session.Server,
                [new ListDirectoryParams { RelativePath = "raced-list-parent/target" }, target, CancellationToken.None, anchor]));

            Assert.True(result.IsError);

            Assert.True(result.StructuredContent.HasValue);

            Assert.Equal("workspacePolicyDenied", result.StructuredContent.Value.GetProperty("code").GetString());

            Assert.DoesNotContain("OUTSIDE_PRIVATE_CANARY", Assert.Single(result.Content).Text, StringComparison.Ordinal);

            Assert.Equal(outsideFileExists, File.Exists(Path.Combine(outside, "target")));
        }
        finally
        {
            if (new DirectoryInfo(parent).LinkTarget is not null)
            {
                Directory.Delete(parent);
            }

            Directory.Delete(outside, recursive: true);
        }
    }
}
