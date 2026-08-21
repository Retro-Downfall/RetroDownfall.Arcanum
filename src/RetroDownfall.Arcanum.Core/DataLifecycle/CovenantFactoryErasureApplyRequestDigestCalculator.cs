using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

public sealed record CovenantFactoryErasureApplyRequestDigestInput(string PlanId);

public interface ICovenantFactoryErasureApplyRequestDigestCalculator
{

    Result<CovenantDigest> Compute(
        CovenantFactoryErasureApplyRequestDigestInput input);

}

public sealed class CovenantFactoryErasureApplyRequestDigestCalculator
    : ICovenantFactoryErasureApplyRequestDigestCalculator
{

    public const string Domain =
        "Arcanum.Covenant.HealthyCatalogFactoryErasure.ApplyRequest.v1";

    public Result<CovenantDigest> Compute(
        CovenantFactoryErasureApplyRequestDigestInput input)
    {

        if (input is null || string.IsNullOrWhiteSpace(input.PlanId))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A factory-erasure apply digest requires an identified confirmed plan.");

        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(Domain));

        hash.AppendData([0x00]);

        byte[] planId = Encoding.UTF8.GetBytes(input.PlanId);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)planId.Length));

        hash.AppendData(length);

        hash.AppendData(planId);

        return new CovenantDigest(hash.GetHashAndReset());

    }

}
