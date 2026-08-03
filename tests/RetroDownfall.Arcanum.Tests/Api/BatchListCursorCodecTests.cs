using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class BatchListCursorCodecTests
{

    [Fact]

    public void Cursor_RoundTripsPositionWithoutExposingRawBatchId()

    {

        BatchListPosition position = new(

            new DateTimeOffset(2026, 8, 3, 12, 34, 56, TimeSpan.Zero),

            Guid.Parse("c8afca8f-5364-4c2b-b37d-52fc80a851a8"));

        string cursor = BatchListCursorCodec.Encode("completed", position);

        Assert.DoesNotContain(position.Id.ToString("N"), cursor, StringComparison.OrdinalIgnoreCase);

        Assert.True(BatchListCursorCodec.TryDecode(

            cursor,

            "completed",

            out BatchListPosition? decoded));

        Assert.Equal(position, decoded);

    }

    [Fact]

    public void Cursor_IsBoundToExactStatusQuery()

    {

        BatchListPosition position = new(DateTimeOffset.UtcNow, Guid.NewGuid());

        string cursor = BatchListCursorCodec.Encode("completed", position);

        Assert.False(BatchListCursorCodec.TryDecode(

            cursor,

            "failed",

            out BatchListPosition? decoded));

        Assert.Null(decoded);

    }

}
