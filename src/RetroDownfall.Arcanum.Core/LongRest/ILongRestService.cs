using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>Applies exact bounded declarations and reads their committed immutable evidence.</summary>
public interface ILongRestService
{
    Task<Result<LongRestReceipt>> ApplyAsync(LongRestRequest request, CancellationToken cancellationToken);

    Task<Result<LongRestReceipt>> GetReceiptAsync(string receiptId, CancellationToken cancellationToken);
}
