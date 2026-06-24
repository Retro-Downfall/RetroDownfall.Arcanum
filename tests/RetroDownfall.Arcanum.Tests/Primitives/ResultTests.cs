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
