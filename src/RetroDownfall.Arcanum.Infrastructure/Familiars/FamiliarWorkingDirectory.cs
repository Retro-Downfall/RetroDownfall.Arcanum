namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// A private directory to run a Familiar in, created fresh per turn and removed after.
/// </summary>
/// <remarks>
/// <para>
/// Where a Familiar runs is a security decision, not a detail. Both CLIs read
/// <em>project</em> state from their working directory — Claude Code loads
/// <c>&lt;cwd&gt;/.claude/settings.json</c> (whose <c>hooks</c> block runs shell commands), and Codex
/// reads <c>AGENTS.md</c> and execpolicy <c>.rules</c> from its root. Inheriting the host's current
/// directory would therefore let any repository Arcanum happens to be started in steer, or execute
/// code during, every turn.
/// </para>
/// <para>
/// A shared temp root is not sufficient either: <c>/tmp</c> is mode 1777 on Linux, so any local
/// account could plant those files. <see cref="Directory.CreateTempSubdirectory"/> creates an
/// owner-only directory, which gives each turn a root nobody else can write.
/// </para>
/// </remarks>
public sealed class FamiliarWorkingDirectory : IDisposable
{

    private FamiliarWorkingDirectory(string path) => Path = path;

    /// <summary>Absolute path to the private directory, or the OS temp root if creation failed.</summary>
    public string Path { get; }

    private bool Owned { get; init; }

    public static FamiliarWorkingDirectory Create()
    {

        try
        {

            return new FamiliarWorkingDirectory(
                Directory.CreateTempSubdirectory("arcanum-familiar-").FullName)
            {
                Owned = true,
            };

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // Falling back to the temp root keeps the turn working; it is still not the operator's
            // repository, which is the exposure that matters.
            return new FamiliarWorkingDirectory(System.IO.Path.GetTempPath());

        }

    }

    public void Dispose()
    {

        if (!Owned)
        {
            return;
        }

        try
        {

            Directory.Delete(Path, recursive: true);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // A leftover empty temp directory is not worth failing a turn over.

        }

    }

}
