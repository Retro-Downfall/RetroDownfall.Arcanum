using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("expired-frame")]
    [InlineData("guild-disabled")]
    [InlineData("revoked-signet")]
    [InlineData("caller-supplied-workspace")]
    public async Task Retained_workspace_function_refuses_disclosure_after_loss_of_authority(string changed)
    {
        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await fixture.OperatorAsync();

        Guid workspace = await fixture.AddWorkspaceAsync();

        string folder = Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "allowed")).FullName;

        await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), "private figure 7241");

        GuildToolGrant read = new(GuildToolSourceKind.InternalMcp, "arcanum", "read_file");

        var issued = await fixture.IssueAsync(owner, fixture.Request with
        {
            WorkspaceId = workspace,
            Grant = fixture.Request.Grant with
            {
                Tools = [read],
                Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.Read)],
            },
        });

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(fixture, owner, issued.Value);

        var input = await fixture.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ConclaveArchmage coordinator = new(null!, fixture.Settings, fixture.Store, new ChildQueue());

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = fixture.Store.CreateToolContext(execution, registry, _ => true);

        AIFunction? bound = await context.AdvertiseAsync(new InertChildTool(read.Name), read, default);

        Assert.NotNull(bound);

        Assert.Null(await context.AdvertiseAsync(new InertChildTool("list_directory"),
            new(GuildToolSourceKind.InternalMcp, "arcanum", "list_directory"), default));

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), read);

        if (changed == "expired-frame")
        {
            frame.Dispose();
        }
        else if (changed == "guild-disabled")
        {
            fixture.Settings.CurrentValue.Features.Guild = false;
        }
        else if (changed == "revoked-signet")
        {
            var current = await fixture.Store.GetAsync(owner, issued.Value.Quest.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            Guid key = Guid.NewGuid();

            var revoked = await fixture.Authority.RevokeAsync(owner, new(key, new string('A', 64)),
                current.Value.Quest.SignetId, new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }

        AIFunctionArguments arguments = new()
        {
            ["relativePath"] = "allowed/report.txt", ["startLine"] = 1, ["endLine"] = 1,
        };

        if (changed == "caller-supplied-workspace")
        {
            arguments["workspaceId"] = workspace.ToString("D");
        }

        object? result = await context.InvokeBoundAsync(bound, arguments, default);

        string refusal = Assert.IsType<string>(result);

        Assert.StartsWith("Guild.", refusal, StringComparison.Ordinal);

        if (changed == "caller-supplied-workspace")
        {
            Assert.StartsWith("Guild.InvalidRequest", refusal, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("private figure 7241", refusal, StringComparison.Ordinal);
    }
}
