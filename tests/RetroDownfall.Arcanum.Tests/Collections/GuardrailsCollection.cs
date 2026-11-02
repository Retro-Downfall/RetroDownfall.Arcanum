namespace RetroDownfall.Arcanum.Tests.Collections;

/// <summary>
/// Isolates semantic guardrail controls that exercise the real finite-deadline regex detectors.
/// Unrelated parallel fixture work can consume their wall-clock match budgets under coverage.
/// The production deadlines and exact detection assertions remain in force inside this fixture.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GuardrailsCollection
{
    public const string Name = "Guardrails";
}
