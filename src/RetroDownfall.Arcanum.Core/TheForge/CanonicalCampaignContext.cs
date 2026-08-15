using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.TheForge;

public readonly record struct CanonicalCampaignContext
{
    private readonly bool _isInitialized;

    private readonly SessionCampaignBinding _binding;

    private readonly long? _campaignAvailabilityGeneration;

    private readonly uint? _pathIdentityPolicyVersion;

    private readonly long? _pathIdentityRevision;

    private readonly CovenantDigest? _rootIdentityDigest;

    private CanonicalCampaignContext(
        SessionCampaignBinding binding,
        long? campaignAvailabilityGeneration,
        uint? pathIdentityPolicyVersion,
        long? pathIdentityRevision,
        CovenantDigest? rootIdentityDigest)
    {
        _isInitialized = true;
        _binding = binding;
        _campaignAvailabilityGeneration = campaignAvailabilityGeneration;
        _pathIdentityPolicyVersion = pathIdentityPolicyVersion;
        _pathIdentityRevision = pathIdentityRevision;
        _rootIdentityDigest = rootIdentityDigest;
    }

    public SessionCampaignBinding Binding
    {
        get
        {
            EnsureInitialized();

            return _binding;
        }
    }

    public Guid? CampaignId
    {
        get
        {
            EnsureInitialized();

            return _binding.CampaignId;
        }
    }

    public long? CampaignAvailabilityGeneration
    {
        get
        {
            EnsureInitialized();

            return _campaignAvailabilityGeneration;
        }
    }

    public uint? PathIdentityPolicyVersion
    {
        get
        {
            EnsureInitialized();

            return _pathIdentityPolicyVersion;
        }
    }

    public long? PathIdentityRevision
    {
        get
        {
            EnsureInitialized();

            return _pathIdentityRevision;
        }
    }

    public CovenantDigest? RootIdentityDigest
    {
        get
        {
            EnsureInitialized();

            return _rootIdentityDigest;
        }
    }

    public bool IsCampaignBound
    {
        get
        {
            EnsureInitialized();

            return _binding.Kind == SessionCampaignBindingKind.Campaign;
        }
    }

    public static CanonicalCampaignContext GlobalOnly =>
        Create(SessionCampaignBinding.GlobalOnly, null, null, null, null);

    public static CanonicalCampaignContext Create(
        SessionCampaignBinding binding,
        long? campaignAvailabilityGeneration,
        uint? pathIdentityPolicyVersion,
        long? pathIdentityRevision,
        CovenantDigest? rootIdentityDigest)
    {
        if (binding.Kind == SessionCampaignBindingKind.LegacyUnresolved)
        {
            throw new ArgumentException("A legacy-unresolved Session cannot establish canonical Campaign context.", nameof(binding));
        }

        bool hasAnyCampaignFact = campaignAvailabilityGeneration.HasValue
            || pathIdentityPolicyVersion.HasValue
            || pathIdentityRevision.HasValue
            || rootIdentityDigest.HasValue;

        if (binding.Kind == SessionCampaignBindingKind.GlobalOnly)
        {
            if (hasAnyCampaignFact)
            {
                throw new ArgumentException("Global-only context cannot carry Campaign availability or path-identity facts.", nameof(binding));
            }

            return new CanonicalCampaignContext(binding, null, null, null, null);
        }

        if (binding.CampaignId is null
            || campaignAvailabilityGeneration is null
            || pathIdentityPolicyVersion is null)
        {
            throw new ArgumentException("Campaign context requires Campaign availability and path-identity policy facts.", nameof(binding));
        }

        if (pathIdentityRevision.HasValue != rootIdentityDigest.HasValue)
        {
            throw new ArgumentException("Campaign path revision and root identity must be both absent or both present.", nameof(binding));
        }

        CovenantValidation.RequirePositive(campaignAvailabilityGeneration.Value, nameof(campaignAvailabilityGeneration));
        CovenantValidation.RequirePositive(pathIdentityPolicyVersion.Value, nameof(pathIdentityPolicyVersion));

        if (pathIdentityRevision is { } revision)
        {
            CovenantValidation.RequirePositive(revision, nameof(pathIdentityRevision));
            CovenantValidation.RequireDigest(rootIdentityDigest!.Value, nameof(rootIdentityDigest));
        }

        return new CanonicalCampaignContext(
            binding,
            campaignAvailabilityGeneration,
            pathIdentityPolicyVersion,
            pathIdentityRevision,
            rootIdentityDigest);
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("The canonical Campaign context is uninitialized.");
        }
    }
}
