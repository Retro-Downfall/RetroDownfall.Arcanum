namespace RetroDownfall.Arcanum.Core.Covenant;

public readonly record struct CovenantAttemptChain(ulong Count, CovenantDigest Head);

public readonly record struct CovenantBranchChain(Guid BranchId, ulong Ordinal, CovenantDigest Head);

public readonly record struct CovenantDisclosureChain(ulong Count, CovenantDigest Head);

public static class CovenantEvidenceChains
{
    private static readonly byte[] ZeroDigest = new byte[CovenantLimits.DigestBytes];

    public static CovenantAttemptChain SeedAttemptChain() =>
        new(0, Hash(CovenantDomainTag.AttemptChain, writer =>
        {
            writer.WriteFixed32(ZeroDigest);
            writer.WriteUInt64(0);
            writer.WriteFixed32(ZeroDigest);
        }));

    public static CovenantAttemptChain AppendAttempt(CovenantAttemptChain chain, CovenantDigest admissionDigest)
    {
        CovenantValidation.RequireDigest(chain.Head, nameof(chain));
        CovenantValidation.RequireDigest(admissionDigest, nameof(admissionDigest));
        ulong count = checked(chain.Count + 1);
        CovenantDigest head = Hash(CovenantDomainTag.AttemptChain, writer =>
        {
            writer.WriteFixed32(chain.Head.Bytes);
            writer.WriteUInt64(count);
            writer.WriteFixed32(admissionDigest.Bytes);
        });

        return new CovenantAttemptChain(count, head);
    }

    public static CovenantBranchChain SeedBranchChain(Guid branchId, CovenantDigest? forkParentAdmissionDigest, CovenantDigest? forkParentBranchChainDigest)
    {
        CovenantValidation.RequireNonEmpty(branchId, nameof(branchId));

        if (forkParentAdmissionDigest.HasValue != forkParentBranchChainDigest.HasValue)
        {
            throw new ArgumentException("Branch fork-parent digests must both be absent or both be present.");
        }

        ValidateOptional(forkParentAdmissionDigest, nameof(forkParentAdmissionDigest));
        ValidateOptional(forkParentBranchChainDigest, nameof(forkParentBranchChainDigest));
        CovenantDigest head = Hash(CovenantDomainTag.BranchChain, writer =>
        {
            writer.WriteGuid(branchId);
            WriteOptional(writer, forkParentAdmissionDigest);
            WriteOptional(writer, forkParentBranchChainDigest);
            writer.WriteUInt64(0);
            writer.WriteFixed32(ZeroDigest);
        });

        return new CovenantBranchChain(branchId, 0, head);
    }

    public static CovenantBranchChain AppendBranch(CovenantBranchChain chain, CovenantDigest admissionDigest)
    {
        CovenantValidation.RequireNonEmpty(chain.BranchId, nameof(chain));
        CovenantValidation.RequireDigest(chain.Head, nameof(chain));
        CovenantValidation.RequireDigest(admissionDigest, nameof(admissionDigest));
        ulong ordinal = checked(chain.Ordinal + 1);
        CovenantDigest head = Hash(CovenantDomainTag.BranchChain, writer =>
        {
            writer.WriteGuid(chain.BranchId);
            writer.WriteFixed32(chain.Head.Bytes);
            writer.WriteUInt64(ordinal);
            writer.WriteFixed32(admissionDigest.Bytes);
        });

        return new CovenantBranchChain(chain.BranchId, ordinal, head);
    }

    public static CovenantDisclosureChain SeedDisclosureChain() =>
        new(0, Hash(CovenantDomainTag.DisclosureChain, writer =>
        {
            writer.WriteFixed32(ZeroDigest);
            writer.WriteUInt64(0);
            writer.WriteFixed32(ZeroDigest);
        }));

    public static CovenantDisclosureChain AppendDisclosure(CovenantDisclosureChain chain, CovenantDigest receiptDigest)
    {
        CovenantValidation.RequireDigest(chain.Head, nameof(chain));
        CovenantValidation.RequireDigest(receiptDigest, nameof(receiptDigest));
        ulong count = checked(chain.Count + 1);
        CovenantDigest head = Hash(CovenantDomainTag.DisclosureChain, writer =>
        {
            writer.WriteFixed32(chain.Head.Bytes);
            writer.WriteUInt64(count);
            writer.WriteFixed32(receiptDigest.Bytes);
        });

        return new CovenantDisclosureChain(count, head);
    }

    private static CovenantDigest Hash(CovenantDomainTag domain, Action<CovenantCanonicalHashWriter> write)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteDomainTag(domain);
        write(writer);

        return writer.FinalizeDigest();
    }

    private static void ValidateOptional(CovenantDigest? digest, string parameterName)
    {
        if (digest is { } present)
        {
            CovenantValidation.RequireDigest(present, parameterName);
        }
    }

    private static void WriteOptional(CovenantCanonicalHashWriter writer, CovenantDigest? digest)
    {
        writer.WriteByte(digest.HasValue ? (byte)1 : (byte)0);

        if (digest is { } present)
        {
            writer.WriteFixed32(present.Bytes);
        }
    }
}
