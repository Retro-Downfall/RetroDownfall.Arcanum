namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// The one thing an <see cref="OperatorAuthorityContext"/> was issued to do.
/// </summary>
/// <remarks>
/// Closed and exhaustive. An endpoint declares exactly one of these, the authenticated boundary issues
/// a context bound to it, and the service compares. That comparison is the point: a context issued for
/// a protected read cannot be handed to a mutation service, so a route that forgot to declare its
/// requirement fails closed rather than inheriting a neighbour's reach (§10.12).
///
/// <para>No member is zero, so a default-initialized field can never read as a granted requirement.</para>
/// </remarks>
public enum CovenantAuthorityRequirement : byte
{

    /// <summary>Reading Covenant content, keys, provenance, or Covenant-derived history.</summary>
    ProtectedRead = 1,

    /// <summary>Operator Set and retirement of Covenant entries.</summary>
    CovenantManage = 2,

    /// <summary>Registering, updating, repairing, or deregistering a Campaign's physical root.</summary>
    CampaignPathManage = 3,

    /// <summary>Resolving a legacy-unresolved Session's immutable Campaign binding.</summary>
    SessionBindingResolve = 4,

    /// <summary>Reset, restore, reinitialize, erasure, and other destructive lifecycle operations.</summary>
    LifecycleManage = 5,

    /// <summary>
    /// Purging Covenant-derived artifacts under retention. Distinct from the Infrastructure
    /// connection-local SQL authorization that shares its semantic purpose.
    /// </summary>
    SensitivityRetentionPurge = 6,

}
