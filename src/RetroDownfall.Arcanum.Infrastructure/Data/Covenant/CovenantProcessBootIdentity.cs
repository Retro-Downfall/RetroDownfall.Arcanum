namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The identity of this host process, minted once at composition.
/// </summary>
/// <remarks>
/// Durable records that outlive a process — an open disclosure subject, a live turn claim — record
/// the boot that created them. That is what lets startup recovery separate a subject this process
/// owns from one abandoned by a process that is gone, without guessing from a timestamp.
/// </remarks>
internal sealed class CovenantProcessBootIdentity
{

    public Guid BootId { get; } = Guid.NewGuid();

}
