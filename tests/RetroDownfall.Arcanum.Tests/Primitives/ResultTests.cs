using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class ResultTests
{

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
        string json = JsonSerializer.Serialize(result);

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

}
