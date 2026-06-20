namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

public interface IProvingGroundsArbiter
{

    Task<IReadOnlyList<InquisitorVerdict>> AdjudicateAsync(
        string output,
        IReadOnlyList<Inquisitor> inquisitors,
        string? judgeModel,
        CancellationToken cancellationToken = default);

}
