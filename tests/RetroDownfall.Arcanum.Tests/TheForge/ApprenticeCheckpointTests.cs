using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

public sealed class ApprenticeCheckpointTests
{

    [Fact]
    public void CompletedToolCallIds_IsReadOnly_NotADowncastableList()
    {

        ApprenticeCheckpoint checkpoint = new() { CompletedToolCallIds = ["call-1"] };

        Assert.Equal(["call-1"], checkpoint.CompletedToolCallIds);

        // The getter must not hand back the internal List<string>: a consumer could downcast it and
        // mutate a checkpoint that is supposed to be frozen once persisted.
        Assert.IsNotType<List<string>>(checkpoint.CompletedToolCallIds);

        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)checkpoint.CompletedToolCallIds).Add("call-2"));

    }

    [Fact]
    public void Deserialize_explicit_null_completedToolCallIds_yields_empty_list()
    {

        const string json = """{"currentStep":1,"completedToolCallIds":null}""";

        ApprenticeCheckpoint? checkpoint =
            JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApprenticeCheckpoint);

        Assert.NotNull(checkpoint);

        Assert.Equal(1, checkpoint.CurrentStep);

        Assert.Empty(checkpoint.CompletedToolCallIds);

    }

    [Fact]
    public void Deserialize_absent_completedToolCallIds_yields_empty_list()
    {

        const string json = """{"currentStep":2}""";

        ApprenticeCheckpoint? checkpoint =
            JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApprenticeCheckpoint);

        Assert.NotNull(checkpoint);

        Assert.Equal(2, checkpoint.CurrentStep);

        Assert.Empty(checkpoint.CompletedToolCallIds);

    }

    [Fact]
    public void Deserialize_empty_object_yields_empty_checkpoint()
    {

        ApprenticeCheckpoint? checkpoint =
            JsonSerializer.Deserialize("{}", TheForgeJsonContext.Default.ApprenticeCheckpoint);

        Assert.NotNull(checkpoint);

        Assert.Empty(checkpoint.CompletedToolCallIds);

    }

    [Fact]
    public void Deserialize_populated_completedToolCallIds_round_trips()
    {

        const string json = """{"currentStep":3,"completedToolCallIds":["a","b"]}""";

        ApprenticeCheckpoint? checkpoint =
            JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApprenticeCheckpoint);

        Assert.NotNull(checkpoint);

        Assert.Equal(["a", "b"], checkpoint.CompletedToolCallIds);

    }

}
