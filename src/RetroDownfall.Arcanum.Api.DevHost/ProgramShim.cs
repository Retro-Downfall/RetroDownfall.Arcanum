// Enables WebApplicationFactory<Program> in the test project without duplicating DevHost wiring.
public partial class Program
{
    /// <summary>
    /// Why the DevHost will not start in <paramref name="environmentName"/>, or <see langword="null"/> when it
    /// may: only Development and Testing are allowed.
    /// </summary>
    /// <remarks>
    /// The DevHost prints the master API key it generates, and a development convenience that starts anywhere
    /// is one that leaks that key into whatever captures a service's output. Kept apart from the top-level
    /// statements so the decision is tested in-process: a test that launched the DevHost to watch it refuse
    /// would, the day the guard regressed, start a real host that reaches the operator's credential store.
    /// </remarks>
    public static string? RefusalForEnvironment(string environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Arcanum DevHost is intended for Development or Testing environments and will not start in '{environmentName}'. Use `arcanum serve` instead.";
}
