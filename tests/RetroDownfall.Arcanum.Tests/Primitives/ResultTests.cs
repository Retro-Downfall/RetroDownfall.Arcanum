using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class ResultTests
{
    [Fact]
    public void InventoryFailureMetadata_is_value_compared_and_absent_from_source_generated_wire_shape()
    {
        CovenantInventoryFailureDetails detail = new(CovenantInventoryFailureReason.MalformedLabel, Guid.NewGuid(), Guid.NewGuid());

        Error error = Error.None with { CovenantInventoryFailure = detail };

        Assert.False(error.IsNone);

        Assert.NotEqual(Error.None, error);

        Assert.Equal(error, Error.None with { CovenantInventoryFailure = detail });

        Assert.NotEqual(error, error with { CovenantInventoryFailure = detail with { Reason = CovenantInventoryFailureReason.UnknownArtifactKind } });

        Assert.Equal(JsonSerializer.Serialize(Error.None, ArcanumJsonContext.Default.Error),
            JsonSerializer.Serialize(error, ArcanumJsonContext.Default.Error));
    }

    [Fact]
    public void StartupFailureMetadata_is_value_compared_and_absent_from_source_generated_wire_shape()
    {
        CovenantStartupFailureDetails detail = new(CovenantStartupFailureReason.MalformedRow, Guid.NewGuid());

        Error error = Error.None with { CovenantStartupFailure = detail };

        Assert.False(error.IsNone);

        Assert.NotEqual(Error.None, error);

        Assert.Equal(error, Error.None with { CovenantStartupFailure = detail });

        Assert.NotEqual(error, error with { CovenantStartupFailure = detail with { Reason = CovenantStartupFailureReason.SecondOwner } });

        string json = JsonSerializer.Serialize(error, ArcanumJsonContext.Default.Error);

        Assert.Equal(JsonSerializer.Serialize(Error.None, ArcanumJsonContext.Default.Error), json);
    }

    [Fact]
    public void ConnectionFailureMetadata_is_value_compared_ignored_and_closed()
    {
        GrimoireConnectionFailureDetails detail = new(GrimoireConnectionFailureStage.Open, GrimoireConnectionFailureKind.Sqlite, 14, 526);

        Error error = Error.None with { ConnectionFailure = detail };

        Assert.False(error.IsNone);

        Assert.NotEqual(Error.None, error);

        Assert.Equal(error, Error.None with { ConnectionFailure = detail });

        Assert.NotEqual(error, error with { ConnectionFailure = detail with { SqliteErrorCode = 26 } });

        Assert.Equal(JsonSerializer.Serialize(Error.None, ArcanumJsonContext.Default.Error),
            JsonSerializer.Serialize(error, ArcanumJsonContext.Default.Error));

        Assert.Equal("Grimoire connection failed: Exception.",
            new GrimoireConnectionFailureDetails((GrimoireConnectionFailureStage)999, (GrimoireConnectionFailureKind)999).ToPublicMessage());
    }

    [Fact]
    public void ContextOverflowMetadata_IsValueComparedAndNeverSerialized()
    {
        ContextOverflowDetails overflow = new("provider", "model", 9000, 1024, 10024, 8192);

        Error metadataOnly = Error.None with { ContextOverflow = overflow };

        Assert.False(metadataOnly.IsNone);

        Assert.NotEqual(Error.None, metadataOnly);

        Assert.Equal(metadataOnly, Error.None with { ContextOverflow = new("provider", "model", 9000, 1024, 10024, 8192) });

        Assert.NotEqual(metadataOnly, metadataOnly with { ContextOverflow = overflow with { WindowTokens = 4096 } });

        Assert.Equal(JsonSerializer.Serialize(Error.None, ArcanumJsonContext.Default.Error),
            JsonSerializer.Serialize(metadataOnly, ArcanumJsonContext.Default.Error));

        var frame = new RetroDownfall.Arcanum.Core.Intelligence.Models.IntelligenceEvent(
            RetroDownfall.Arcanum.Core.Intelligence.Models.IntelligenceEventType.Error, "safe", ErrorCodes.Hub.ContextBudgetExceeded);

        Assert.Equal(JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent),
            JsonSerializer.Serialize(frame with { ContextOverflow = overflow }, ArcanumJsonContext.Default.IntelligenceEvent));
    }

    [Fact]
    public void Success_HasNoError()
    {
        Result result = Result.Success();

        Assert.True(result.IsSuccess);

        Assert.False(result.IsFailure);

        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_CarriesError()
    {
        Error error = new("Test.Failed", "Something went wrong.");

        Result result = Result.Failure(error);

        Assert.False(result.IsSuccess);

        Assert.True(result.IsFailure);

        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void ImplicitConversion_FromError_CreatesFailure()
    {
        Error error = new("Test.Failed", "Implicit failure.");

        Result result = error;

        Assert.True(result.IsFailure);

        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void GenericSuccess_ExposesValue()
    {
        Result<string> result = Result<string>.Success("ok");

        Assert.True(result.IsSuccess);

        Assert.Equal("ok", result.Value);
    }

    [Fact]
    public void GenericFailure_ValueAccessorThrows()
    {
        Result<string> result = Result<string>.Failure(new Error("Test.Failed", "Nope."));

        Assert.True(result.IsFailure);

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void GenericFailure_SerializesWithoutThrowing_ValueIsIgnored()
    {
        Result<int> result = Result<int>.Failure(new Error("Test.Failed", "Nope."));

        // W3.6: [JsonIgnore] on Value means a stray direct serialization no longer invokes the
        // throwing getter and turns a domain failure into an unhandled serialization exception.
        string json = JsonSerializer.Serialize(result, AdHocJson.Options);

        Assert.False(string.IsNullOrEmpty(json));

        Assert.DoesNotContain("\"Value\"", json, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("\"IsSuccess\":false", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericImplicitConversion_FromValue_CreatesSuccess()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);

        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void GenericImplicitConversion_FromError_CreatesFailure()
    {
        Error error = new("Test.Failed", "Bad value.");

        Result<int> result = error;

        Assert.True(result.IsFailure);

        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void ErrorIsNone_IsTheSameAnswerAsRecordEqualityWithNone()
    {
        Error[] errors =
        [
            Error.None,
            new(string.Empty, string.Empty),
            default,
            new("Test.Failed", string.Empty),
            new(string.Empty, "Message only."),
            new("Test.Failed", "Nope."),
            new(string.Empty, string.Empty, Array.Empty<ConfigurationValidationError>()),
            new(string.Empty, string.Empty, [new ConfigurationValidationError("/pointer", "detail")]),
        ];

        foreach (Error error in errors)
        {
            Assert.Equal(error == Error.None, error.IsNone);
        }

        Assert.True(Error.None.IsNone);

        Assert.False(default(Error).IsNone);
    }

    // Error ships through the source-generated ArcanumJsonContext under Native AOT, not through the
    // reflection resolver, so the wire shape that matters is the one that context writes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ErrorIsNone_IsNotPartOfTheArcanumSourceGeneratedWireShape(bool withDetails)
    {
        Error error = withDetails
            ? new("Test.Failed", "Nope.", [new ConfigurationValidationError("/pointer", "detail")])
            : new("Test.Failed", "Nope.");

        string json = JsonSerializer.Serialize(error, ArcanumJsonContext.Default.Error);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.DoesNotContain(document.RootElement.EnumerateObject(),
            static property => property.Name.Equals("isNone", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Test.Failed", document.RootElement.GetProperty("code").GetString());

        Assert.Equal("Nope.", document.RootElement.GetProperty("message").GetString());

        Error? roundTripped = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.Error);

        Assert.NotNull(roundTripped);

        Assert.Equal(error.Code, roundTripped.Value.Code);

        Assert.Equal(error.Message, roundTripped.Value.Message);

        Assert.Equal(error.Details ?? [], roundTripped.Value.Details ?? []);

        Assert.False(roundTripped.Value.IsNone);

        Assert.Equal(json, JsonSerializer.Serialize(roundTripped.Value, ArcanumJsonContext.Default.Error));
    }
}
