namespace RetroDownfall.Arcanum.Core.Cli;

/// <summary>
/// Structured output of the <c>arcanum doctor</c> command when invoked with <c>--json</c>.
///
/// <para>The first three members are the pre-#33 contract and keep their meaning exactly:
/// <paramref name="Name"/> is the display label and <paramref name="Status"/> is the
/// <c>ok</c>/<c>warn</c>/<c>fail</c> vocabulary, now derived from <paramref name="Outcome"/> by
/// <see cref="DoctorOutcomes.ToLegacyStatus"/>. Everything after them is additive, so an existing
/// consumer that reads only name/status/detail is unaffected, while a new one can key on the stable
/// <paramref name="Id"/> and act on <paramref name="Remedies"/>.</para>
/// </summary>
public sealed record DoctorCheck(
    string Name,
    string Status,
    string? Detail,
    string? Id = null,
    DoctorSubsystem? Subsystem = null,
    DoctorOutcome? Outcome = null,
    IReadOnlyList<DoctorRemedy>? Remedies = null);

/// <summary>
/// One <c>arcanum doctor</c> run. <paramref name="Healthy"/> is the exit-code signal and stays
/// <see langword="true"/> for a merely degraded installation unless <c>--strict</c> was requested;
/// <paramref name="Outcome"/> is the authoritative aggregate. <paramref name="Repairs"/> is empty
/// unless the invocation named one, and its entries are
/// <see cref="DoctorRepairState.Planned"/> unless <c>--apply</c> was given.
/// </summary>
public sealed record DoctorReport(
    bool Healthy,
    IReadOnlyList<DoctorCheck> Checks,
    DoctorOutcome? Outcome = null,
    IReadOnlyList<DoctorRepairResult>? Repairs = null);

/// <summary>
/// The <c>arcanum doctor list</c> document: every registered diagnostic and repair, so an operator
/// can discover the exact id to pass to <c>--only</c>, <c>--skip</c>, or <c>--repair</c>.
/// </summary>
public sealed record DoctorCatalogCheck(
    string Id,
    string Name,
    DoctorSubsystem Subsystem,
    bool RequiresNetwork);

public sealed record DoctorCatalogRepair(
    string Id,
    DoctorSubsystem Subsystem,
    string Description,
    IReadOnlyList<string> DetectorIds);

public sealed record DoctorCatalog(
    IReadOnlyList<DoctorCatalogCheck> Checks,
    IReadOnlyList<DoctorCatalogRepair> Repairs);
