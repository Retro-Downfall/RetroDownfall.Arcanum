using System.Collections.Generic;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class PrimitiveGuardTests
{

    [Fact]
    public void ListPageResult_NullItems_NormalizesToEmpty()
    {

        ListPageResult<string> page = new(null!, HasMore: false);

        Assert.NotNull(page.Items);

        Assert.Empty(page.Items);

    }

    [Fact]
    public void ListPageResult_NullItems_ViaInit_NormalizesToEmpty()
    {

        ListPageResult<string> page = new ListPageResult<string>(["a"], HasMore: false) with { Items = null! };

        Assert.NotNull(page.Items);

        Assert.Empty(page.Items);

    }

    [Fact]
    public void Error_Details_IsNonDowncastableReadOnly()
    {

        // W4.1: a plain List exposed as IReadOnlyList could be cast back and mutated; the defensive
        // copy must be a non-downcastable read-only collection.
        Error error = new("Validation.Failed", "boom", new List<ConfigurationValidationError>());

        Assert.NotNull(error.Details);

        Assert.IsNotType<List<ConfigurationValidationError>>(error.Details);

    }

    [Fact]
    public void Error_NullDetails_StaysNull()
    {

        Error error = new("X", "msg");

        Assert.Null(error.Details);

    }

}
