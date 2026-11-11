using System.Text;
using System.Text.Json;
using A2A;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Core.Guild;
using A2ATaskStatus = A2A.TaskStatus;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Fact]
    public void Guild_task_observation_preserves_one_designated_text_part()
    {
        AgentTask task = new() { Status = new A2ATaskStatus { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "final", Parts = [Part.FromText("  exact\ntext  ")] }] };

        A2AGuildTaskObservation observation = A2AClientService.GuildObservation(task);

        Assert.Equal(1, observation.DesignatedResultCount);

        Assert.Equal("  exact\ntext  ", Encoding.UTF8.GetString(Assert.IsType<byte[]>(observation.DesignatedResultUtf8)));

        Assert.Equal("text/plain", observation.DesignatedResultMimeType);
    }

    [Fact]
    public void Guild_task_observation_rejects_multiple_artifacts_without_changing_inspection_text()
    {
        AgentTask task = new() { Status = new A2ATaskStatus { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "first", Parts = [Part.FromText("one")] },
                new Artifact { ArtifactId = "second", Parts = [Part.FromText("two")] }] };

        A2AGuildTaskObservation observation = A2AClientService.GuildObservation(task);

        Assert.Null(observation.DesignatedResultCount);

        Assert.Equal("two", observation.ResponseText);
    }

    [Fact]
    public void Guild_task_observation_preserves_exact_structured_data_bytes()
    {
        using JsonDocument json = JsonDocument.Parse("{ \"b\": 2, \"a\": [1] }");

        AgentTask task = new() { Status = new A2ATaskStatus { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "final", Parts = [Part.FromData(json.RootElement)] }] };

        A2AGuildTaskObservation observation = A2AClientService.GuildObservation(task);

        Assert.Equal(1, observation.DesignatedResultCount);

        Assert.Equal("{ \"b\": 2, \"a\": [1] }", Encoding.UTF8.GetString(Assert.IsType<byte[]>(observation.DesignatedResultUtf8)));

        Assert.Equal("application/json", observation.DesignatedResultMimeType);
    }

    [Fact]
    public void Guild_task_status_message_and_mixed_parts_are_not_designated_outputs()
    {
        AgentTask statusOnly = new() { Status = new A2ATaskStatus { State = TaskState.Completed,
            Message = new Message { Parts = [Part.FromText("status text")] } } };

        A2AGuildTaskObservation status = A2AClientService.GuildObservation(statusOnly);

        Assert.Null(status.DesignatedResultCount);

        Assert.Equal("status text", status.ResponseText);

        AgentTask mixed = new() { Status = new A2ATaskStatus { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "mixed", Parts = [Part.FromText("one"), Part.FromText("two")] }] };

        Assert.Null(A2AClientService.GuildObservation(mixed).DesignatedResultCount);
    }

    [Fact]
    public void Guild_direct_message_requires_one_bounded_part_and_rejects_structured_scalar()
    {
        GuildFinalOutput? exact = GuildFinalOutput.FromParts([Part.FromText("direct result")]);

        Assert.NotNull(exact);

        Assert.Equal("direct result", Encoding.UTF8.GetString(exact.Utf8));

        Assert.Equal("text/plain", exact.MimeType);

        Assert.Null(GuildFinalOutput.FromParts([Part.FromText(new string('x', 65_537))]));

        Assert.Null(GuildFinalOutput.FromParts([Part.FromText("\uD800")]));

        Assert.Null(GuildFinalOutput.FromParts([Part.FromText("first"), Part.FromText("second")]));

        using JsonDocument scalar = JsonDocument.Parse("42");

        Assert.Null(GuildFinalOutput.FromParts([Part.FromData(scalar.RootElement)]));

        Part ambiguous = Part.FromText("text");

        ambiguous.Url = "https://example.test/value";

        Assert.Null(GuildFinalOutput.FromParts([ambiguous]));
    }

    [Fact]
    public async Task Guild_remote_transport_preserves_final_artifact_proof_in_observation()
    {
        using var server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler(" exact result "));

        using HttpMessageHandler handler = server.CreateHandler();

        var settings = EnabledSettings();

        settings.Features.Guild = true;

        GuildSendingLifecycle lifecycle = new();

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "final-proof",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), lifecycle, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.NotNull(lifecycle.Observation);

        Assert.Equal(1, lifecycle.Observation.DesignatedResultCount);

        Assert.Equal(" exact result ", Encoding.UTF8.GetString(Assert.IsType<byte[]>(lifecycle.Observation.DesignatedResultUtf8)));

        Assert.Equal("text/plain", lifecycle.Observation.DesignatedResultMimeType);

        Assert.Equal(" exact result ", lifecycle.Observation.ResponseText);
    }

    [Fact]
    public void Guild_remote_null_artifact_or_part_has_no_designated_result()
    {
        AgentTask task = new() { Status = new A2ATaskStatus { State = TaskState.Completed }, Artifacts = [null!] };

        Assert.Null(A2AClientService.GuildObservation(task).DesignatedResultCount);

        Assert.Null(GuildFinalOutput.FromParts([null!]));

        AgentTask missingPart = new() { Status = new A2ATaskStatus { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "invalid", Parts = [null!] }] };

        Assert.Null(A2AClientService.GuildObservation(missingPart).DesignatedResultCount);
    }

}
