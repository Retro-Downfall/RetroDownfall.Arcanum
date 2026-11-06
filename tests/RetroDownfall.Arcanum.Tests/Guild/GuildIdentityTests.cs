using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildIdentityTests
{
    [Theory]
    [InlineData("submitted", "Submitted")]
    [InlineData("working", "Working")]
    [InlineData("input-required", "InputRequired")]
    [InlineData("completed", "Completed")]
    [InlineData("failed", "Failed")]
    [InlineData("canceled", "Canceled")]
    [InlineData("rejected", "Rejected")]
    public void Quest_states_use_exact_closed_wire_literals(string wire, string member)
    {
        Type type = Require("QuestState");

        JsonTypeInfo metadata = Metadata(type);

        object value = Enum.Parse(type, member);

        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(value, metadata));

        Assert.Equal(value, JsonSerializer.Deserialize($"\"{wire}\"", metadata));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"newer-success-state\"")]
    public void Quest_state_refuses_unknown_and_integer_values(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, Metadata(Require("QuestState"))));
    }

    [Theory]
    [InlineData("submitted", "Submitted")]
    [InlineData("working", "Working")]
    [InlineData("input-required", "InputRequired")]
    [InlineData("completed", "Completed")]
    [InlineData("failed", "Failed")]
    [InlineData("canceled", "Canceled")]
    [InlineData("rejected", "Rejected")]
    public void Known_remote_state_preserves_observed_state_without_reconciliation(string wire, string expected)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(Require("GuildRemoteStateProjection").GetMethod("Observe", BindingFlags.Public | BindingFlags.Static));

        object result = Assert.IsAssignableFrom<object>(method.Invoke(null, [wire]));

        Assert.Equal(expected, result.GetType().GetProperty("State")!.GetValue(result)!.ToString());

        Assert.Equal(false, result.GetType().GetProperty("ReconciliationRequired")!.GetValue(result));
    }

    [Fact]
    public void Unknown_remote_state_is_bounded_nonterminal_reconciliation()
    {
        Type mapper = Require("GuildRemoteStateProjection");

        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(mapper.GetMethod("Observe", BindingFlags.Public | BindingFlags.Static));

        object result = Assert.IsAssignableFrom<object>(method.Invoke(null, [new string('x', 5000)]));

        Assert.Equal("Working", result.GetType().GetProperty("State")!.GetValue(result)!.ToString());

        Assert.Equal(true, result.GetType().GetProperty("ReconciliationRequired")!.GetValue(result));

        Assert.Equal("Guild.UnsupportedTransportState", result.GetType().GetProperty("ReasonCode")!.GetValue(result));

        Assert.True(((string)result.GetType().GetProperty("RemoteStateDiagnostic")!.GetValue(result)!).Length <= 128);
    }

    [Fact]
    public void Operator_and_receipt_identities_cannot_fabricate_Run_fields()
    {
        JsonTypeInfo metadata = Metadata(Require("GuildProducerIdentity"));

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("""{"kind":"operator","agentId":"11111111-1111-1111-1111-111111111111","runId":"22222222-2222-2222-2222-222222222222","questId":null,"operatorPrincipalId":"local-operator","domainOwnerId":null,"sourceKind":null,"receiptId":null}""", metadata));

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("""{"kind":"hostReceipt","agentId":null,"runId":null,"questId":null,"operatorPrincipalId":null,"domainOwnerId":"11111111-1111-1111-1111-111111111111","sourceKind":"result","receiptId":null}""", metadata));
    }

    private static Type Require(string name) => Assert.IsAssignableFrom<Type>(typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}"));

    private static JsonTypeInfo Metadata(Type type)
    {
        Type contextType = Assert.IsAssignableFrom<Type>(typeof(Result).Assembly.GetType("RetroDownfall.Arcanum.Core.Serialization.GuildJsonContext"));

        JsonSerializerContext context = Assert.IsAssignableFrom<JsonSerializerContext>(contextType.GetProperty("Default")!.GetValue(null));

        return Assert.IsAssignableFrom<JsonTypeInfo>(context.GetTypeInfo(type));
    }
}
