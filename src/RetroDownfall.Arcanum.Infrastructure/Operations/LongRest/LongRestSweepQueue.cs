using System.Threading.Channels;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

/// <summary>Coalesces post-persistence wake hints; durable selection determines which requests may run.</summary>
internal sealed class LongRestSweepQueue : ILongRestSweepWake
{
    private readonly Channel<Guid> _pending = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
    {
        SingleReader = true,

        FullMode = BoundedChannelFullMode.DropWrite,
    });

    internal void Signal(Guid operation) => _pending.Writer.TryWrite(operation);

    public void Wake(Guid operationId) => Signal(operationId);

    internal ValueTask<Guid> ReadAsync(CancellationToken ct) => _pending.Reader.ReadAsync(ct);
}
