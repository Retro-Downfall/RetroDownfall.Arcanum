namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed record CovenantFinalReceipt
{
    public CovenantFinalReceipt(FinalReceiptDigestInput input)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Digest = CovenantDigests.FinalReceipt(input);
    }

    public FinalReceiptDigestInput Input { get; }

    public CovenantDigest Digest { get; }
}
