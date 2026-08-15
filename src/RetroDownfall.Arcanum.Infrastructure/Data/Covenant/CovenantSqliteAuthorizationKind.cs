namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The privileged operations a Grimoire connection can be temporarily authorized to perform.
/// </summary>
/// <remarks>
/// Each value corresponds to a SQL scalar function that schema triggers consult. Every one begins
/// <c>false</c> on every connection, including a pooled connection handed back out, so a privileged
/// write is possible only inside an explicit, scoped, and short-lived authorization. The default is
/// refusal; authorization is the exception a caller has to ask for by name.
/// </remarks>
internal enum CovenantSqliteAuthorizationKind
{

    SessionBindingWrite = 0,

    SessionRetention = 1,

    OwnerCleanup = 2,

    ArtifactReplacement = 3,

    SensitivityRetentionPurge = 4,

    CovenantFamilyMaintenance = 5,

    AcceleratorSynchronization = 6,

    ProtectedSessionTransfer = 7,

    TurnCapacityMutation = 8,

    CampaignPathMarkerIntentMutation = 9,

    ManagedFileIntentMutation = 10,

    /// <summary>
    /// Sanitizing managed authority out of restore staging. Not reachable through the general
    /// <c>Authorize</c> entry point: it is granted only to the sealed restore-staging capability,
    /// because it is the one authorization that rewrites authority state rather than data.
    /// </summary>
    RestoreStagingManagedAuthoritySanitization = 11,

}
