namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Which kind of caller is driving one inference invocation.
/// </summary>
/// <remarks>
/// Deliberately not serializable and deliberately not inferred. Covenant eligibility is a property of
/// who is calling, and every previous way of answering that question — a null service, an absent
/// Session, a missing working directory — answered it by accident. A required enum makes a new
/// internal caller choose, at compile time, which of these it is (§10.12).
///
/// <para>The three operator surfaces are the only ones that may ever carry Covenant read authority.
/// Apprentice and daemon callers have no surface of their own on purpose: they are
/// <see cref="InternalBackground"/>, and an authority-bearing Apprentice surface would be a way to
/// give unattended work the operator's reach.</para>
///
/// <para>This runtime classification is distinct from the persisted three-code
/// <c>SessionTurnSurface</c> digest enum, which records what a durable claim was for.</para>
/// </remarks>
public enum ArcanumExecutionSurface : byte
{

    /// <summary>An operator-facing turn with a durable Session, Entry placeholder, and turn claim.</summary>
    SessionBackedOperatorTurn = 1,

    /// <summary>An operator-facing turn with no durable Session, and therefore no mutation tool.</summary>
    StatelessOperatorTurn = 2,

    /// <summary>An authenticated preview or inspection that builds a plan but dispatches no turn.</summary>
    ContextInspection = 3,

    /// <summary>A delegated child task. It inherits no Covenant content by contract.</summary>
    Subagent = 4,

    /// <summary>An agent-to-agent task accepted from a peer.</summary>
    A2A = 5,

    /// <summary>Queued batch inference with no attending operator.</summary>
    Batch = 6,

    /// <summary>Startup or long-running-operation recovery work.</summary>
    Recovery = 7,

    /// <summary>Every other unattended in-process caller: daemons, Apprentice, summarizers, extractors.</summary>
    InternalBackground = 8,

}
