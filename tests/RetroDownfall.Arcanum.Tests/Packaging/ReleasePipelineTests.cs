using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Guards the release/packaging pipeline against the failure modes that only show up on a
/// signing runner: Actions script injection, credentials on a command line, and archives whose
/// signature covers less code than the operator believes.
/// </summary>
public sealed class ReleasePipelineTests
{

    /// <summary>
    /// Expression contexts an outside party can influence. Expanded by the Actions templating
    /// engine into the generated script's source text, so they may never appear inside a
    /// <c>run:</c> block — the value has to arrive through <c>env:</c> instead.
    /// </summary>
    private static readonly string[] UntrustedExpressionContexts =
    [
        "inputs.",

        "github.event.",

        "github.head_ref",
    ];

    [Fact]
    public void Workflow_run_blocks_never_interpolate_untrusted_expressions()
    {

        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles())
        {

            foreach ((int number, string text) in ShellScriptLines(File.ReadAllLines(workflow)))
            {

                foreach (string context in UntrustedExpressionContexts)
                {

                    if (ContainsExpression(text, context))
                    {

                        offenders.Add($"{Path.GetFileName(workflow)}:{number}: {text.Trim()}");

                    }

                }

            }

        }

        Assert.True(
            offenders.Count == 0,
            "Untrusted workflow expressions are expanded into shell source before any validation "
            + "runs (Actions script injection). Pass them through the step's env: mapping and read "
            + "the environment variable instead:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// Credentials that arrive from a repository secret. A process command line is world-readable
    /// on the build host (CWE-214), and log masking does not reach the process table, so these
    /// may never be handed to a tool as an argv element — feed them on stdin or via env instead.
    /// <c>KEYCHAIN_PASSWORD</c> is deliberately absent: it is derived from the public run id and
    /// guards a keychain that is created and deleted inside a single job.
    /// </summary>
    private static readonly string[] SecretVariables =
    [
        "APPLE_APP_SPECIFIC_PASSWORD",

        "APPLE_CERTIFICATE_PASSWORD",

        "WINDOWS_CERT_PASSWORD",
    ];

    [Fact]
    public void Packaging_never_passes_a_secret_as_a_command_line_argument()
    {

        List<string> offenders = [];

        foreach ((string file, int number, string text) in PackagingShellLines())
        {

            foreach (string secret in SecretVariables)
            {

                Regex option = new(
                    "(^|\\s)-{1,2}[A-Za-z][A-Za-z0-9-]*[=\\s]+\"?\\$(env:|\\{)?"
                    + Regex.Escape(secret)
                    + "\\b",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5));

                if (option.IsMatch(text))
                {

                    offenders.Add($"{file}:{number}: {text.Trim()}");

                }

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A repository secret is passed to a tool on its command line, where the whole process "
            + "table can read it. Feed it on stdin (both notarytool and security prompt for the "
            + "value when the option is omitted) or hand it over through the environment:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// Packaging flags that produce an artifact no one outside the build host can trust:
    /// <c>--skip-sign</c> leaves it unsigned, and <c>--local-sign</c> signs it with whatever
    /// certificate the operator has in Keychain Access and deliberately skips notarization.
    /// Both are local-verification tools. Either one in a release workflow uploads a draft-release
    /// asset that installs cleanly on the machine that built it and is refused by Gatekeeper
    /// everywhere else — a failure the release job itself cannot observe.
    /// </summary>
    private static readonly string[] NonReleaseSigningFlags =
    [
        "--skip-sign",

        "--local-sign",
    ];

    [Fact]
    public void Release_workflows_never_pass_a_non_release_signing_flag()
    {

        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles())
        {

            foreach ((int number, string text) in ShellScriptLines(File.ReadAllLines(workflow)))
            {

                foreach (string flag in NonReleaseSigningFlags)
                {

                    if (text.Contains(flag, StringComparison.Ordinal))
                    {

                        offenders.Add($"{Path.GetFileName(workflow)}:{number}: {text.Trim()}");

                    }

                }

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A workflow passes a packaging flag that suppresses release signing or notarization. "
            + "The artifact it produces is trusted only on the build host, and nothing later in "
            + "the release job can tell the difference:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// Local signing exists so the hardened runtime and the JIT entitlements can be exercised on a
    /// development certificate, which Apple will not notarize. If a notarization or stapling step
    /// were reachable from that path it would fail at the Apple end, late, and only on the one
    /// machine that runs it — so every call site stays behind an explicit <c>LOCAL_SIGN</c> guard.
    /// </summary>
    [Fact]
    public void MacOs_local_signing_never_reaches_notarization()
    {

        string[] notarizationCalls = ["notarize_submit", "staple_item", "spctl --assess"];

        foreach (string script in MacOsPackagingBuildScripts())
        {

            string text = File.ReadAllText(script);

            string name = Path.GetFileName(script);

            Assert.True(
                text.Contains("require_local_signing_identity", StringComparison.Ordinal),
                $"{name} does not offer local signing; it must resolve the identity through "
                + "require_local_signing_identity so the keychain is the only source.");

            foreach (string call in notarizationCalls)
            {

                foreach (string block in GuardedBlocksContaining(text, call))
                {

                    Assert.True(
                        block.Contains("LOCAL_SIGN", StringComparison.Ordinal),
                        $"{name} reaches '{call}' from a branch that does not exclude "
                        + "--local-sign. Apple rejects a development certificate, so this fails "
                        + "at submission time rather than at the flag.");

                }

            }

        }

    }

    /// <summary>
    /// The <c>if</c>/<c>elif</c> conditions governing every line that contains
    /// <paramref name="needle"/>, taken as the condition text of each enclosing shell branch. A
    /// script with no occurrence yields nothing, which is a pass — the point is that a reachable
    /// call is guarded, not that one exists.
    /// </summary>
    private static IReadOnlyList<string> GuardedBlocksContaining(string script, string needle)
    {

        List<string> conditions = [];

        string[] lines = script.Split('\n');

        List<string> open = [];

        foreach (string line in lines)
        {

            string trimmed = line.Trim();

            if (trimmed.StartsWith("if ", StringComparison.Ordinal))
            {

                open.Add(trimmed);

            }
            else if (trimmed.StartsWith("elif ", StringComparison.Ordinal) && open.Count > 0)
            {

                open[^1] = open[^1] + " " + trimmed;

            }
            else if (trimmed == "fi" && open.Count > 0)
            {

                open.RemoveAt(open.Count - 1);

            }

            if (!line.Contains(needle, StringComparison.Ordinal))
            {

                continue;

            }

            if (trimmed.StartsWith('#'))
            {

                continue;

            }

            conditions.Add(open.Count == 0 ? string.Empty : string.Join(" ", open));

        }

        return conditions;

    }

    private static IReadOnlyList<string> MacOsPackagingBuildScripts()
    {

        string directory = Path.Combine(
            RepositoryRoot(),
            "scripts",
            "packaging",
            "macos");

        Assert.True(Directory.Exists(directory), $"Missing macOS packaging directory: {directory}");

        string[] scripts =
        [
            Path.Combine(directory, "build-arcanum.sh"),

            Path.Combine(directory, "build-app-dmg.sh"),
        ];

        foreach (string script in scripts)
        {

            Assert.True(File.Exists(script), $"Missing macOS packaging script: {script}");

        }

        return scripts;

    }

    [Fact]
    public void Windows_packaging_signs_every_portable_executable_it_ships()
    {

        string script = File.ReadAllText(WindowsPackagingScript());

        // Every product that gets staged and archived must be signed as a whole tree. Signing an
        // individually named file leaves the first-party managed assemblies and the
        // SQLCipher/oniguruma native libraries unsigned inside a zip that looks signed, so
        // WDAC/AppLocker publisher rules cannot cover the code that actually executes.
        string[] publishers = ["Publish-Cli", "Publish-Gui"];

        foreach (string publisher in publishers)
        {

            string body = Assert.Single(BracedBlocksAfter(script, $"function {publisher}"));

            Assert.True(
                body.Contains("Invoke-StageAuthenticodeSign", StringComparison.Ordinal),
                $"{publisher} archives a stage directory without a full-tree Authenticode pass "
                + "over it.");

            Assert.False(
                body.Contains("Invoke-AuthenticodeSign ", StringComparison.Ordinal),
                $"{publisher} signs an individually named file; sign the staged tree instead so "
                + "every shipped .exe and .dll is covered.");

        }

        string helper = Assert.Single(BracedBlocksAfter(script, "function Invoke-StageAuthenticodeSign"));

        Assert.Contains("-Recurse", helper, StringComparison.Ordinal);

        Assert.Contains(".exe", helper, StringComparison.Ordinal);

        Assert.Contains(".dll", helper, StringComparison.Ordinal);

        Assert.Contains("signtool verify", helper, StringComparison.Ordinal);

    }

    /// <summary>
    /// Bodies of every brace-delimited block that follows <paramref name="header"/>, brace-matched
    /// so a nested block does not terminate its parent.
    /// </summary>
    private static IReadOnlyList<string> BracedBlocksAfter(string script, string header)
    {

        List<string> blocks = [];

        int search = 0;

        while (true)
        {

            int start = script.IndexOf(header, search, StringComparison.Ordinal);

            if (start < 0)
            {

                return blocks;

            }

            search = start + header.Length;

            int open = script.IndexOf('{', search);

            if (open < 0)
            {

                return blocks;

            }

            int depth = 0;

            for (int i = open; i < script.Length; i++)
            {

                if (script[i] == '{')
                {

                    depth++;

                }
                else if (script[i] == '}')
                {

                    depth--;

                    if (depth == 0)
                    {

                        blocks.Add(script[(open + 1)..i]);

                        search = i;

                        break;

                    }

                }

            }

        }

    }

    private static string WindowsPackagingScript()
    {

        string path = Path.Combine(
            RepositoryRoot(),
            "scripts",
            "packaging",
            "windows",
            "package-windows.ps1");

        Assert.True(File.Exists(path), $"Missing Windows packaging script: {path}");

        return path;

    }

    /// <summary>
    /// Every line of shell the packaging pipeline executes: the packaging scripts themselves plus
    /// the inline scripts the workflows run.
    /// </summary>
    private static IEnumerable<(string File, int Number, string Text)> PackagingShellLines()
    {

        string root = RepositoryRoot();

        string scripts = Path.Combine(root, "scripts");

        foreach (string file in Directory.EnumerateFiles(scripts, "*.*", SearchOption.AllDirectories))
        {

            if (Path.GetExtension(file) is not (".sh" or ".ps1"))
            {

                continue;

            }

            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {

                yield return (Path.GetRelativePath(root, file), i + 1, lines[i]);

            }

        }

        foreach (string workflow in WorkflowFiles())
        {

            foreach ((int number, string text) in ShellScriptLines(File.ReadAllLines(workflow)))
            {

                yield return (Path.GetRelativePath(root, workflow), number, text);

            }

        }

    }

    private static bool ContainsExpression(string line, string context)
    {

        int index = 0;

        while (true)
        {

            index = line.IndexOf("${{", index, StringComparison.Ordinal);

            if (index < 0)
            {

                return false;

            }

            index += 3;

            int end = line.IndexOf("}}", index, StringComparison.Ordinal);

            if (end < 0)
            {

                return false;

            }

            if (line[index..end].TrimStart().StartsWith(context, StringComparison.Ordinal))
            {

                return true;

            }

        }

    }

    /// <summary>
    /// Yields every line that ends up inside a step's shell script, whether it was written as a
    /// block scalar (<c>run: |</c>) or as an inline value (<c>run: echo hi</c>).
    /// </summary>
    private static IEnumerable<(int Number, string Text)> ShellScriptLines(string[] lines)
    {

        int blockIndent = -1;

        for (int i = 0; i < lines.Length; i++)
        {

            string line = lines[i];

            if (blockIndent >= 0)
            {

                if (line.Trim().Length == 0)
                {

                    continue;

                }

                if (IndentOf(line) > blockIndent)
                {

                    yield return (i + 1, line);

                    continue;

                }

                blockIndent = -1;

            }

            int indent = IndentOf(line);

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {

                indent += 2;

                trimmed = trimmed[2..].TrimStart();

            }

            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {

                continue;

            }

            string value = trimmed[4..].Trim();

            if (value.Length == 0 || value[0] is '|' or '>')
            {

                blockIndent = indent;

                continue;

            }

            yield return (i + 1, line);

        }

    }

    private static int IndentOf(string line)
    {

        int indent = 0;

        while (indent < line.Length && line[indent] == ' ')
        {

            indent++;

        }

        return indent;

    }

    private static IReadOnlyList<string> WorkflowFiles()
    {

        string directory = Path.Combine(RepositoryRoot(), ".github", "workflows");

        Assert.True(Directory.Exists(directory), $"Missing workflow directory: {directory}");

        string[] files = Directory.GetFiles(directory, "*.yml");

        Assert.NotEmpty(files);

        return files;

    }

    private static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

    /// <summary>
    /// A <c>uses:</c> reference to a tag is a mutable ref: whoever controls the upstream repository
    /// decides what code it resolves to on the next run (the tj-actions/changed-files class of
    /// attack). In a workflow that decodes the Developer ID Application private key into a keychain
    /// and holds <c>contents: write</c>, that is a signing key an upstream compromise can walk off
    /// with — and a key that signs anything Gatekeeper then trusts on every operator's machine. A
    /// commit SHA is immutable, so the reference means one specific tree forever.
    /// </summary>
    /// <remarks>
    /// Every workflow, not only the ones that read <c>secrets.</c>. A holder of no secret still
    /// checks out this repository's source and runs a third-party binary over it on a runner that
    /// reaches the network, so an upstream compromise gets arbitrary code execution against the
    /// tree the release is cut from — and the workflow one commit later may be the one that gains
    /// a secret. The single-maintainer third-party action is the reference the tj-actions threat
    /// model applies to literally, and it lives in the workflow that builds the native runtime.
    /// </remarks>
    [Fact]
    public void Workflows_pin_every_third_party_action_to_a_commit()
    {

        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles())
        {

            string[] lines = File.ReadAllLines(workflow);

            for (int i = 0; i < lines.Length; i++)
            {

                string trimmed = lines[i].Trim();

                if (!trimmed.StartsWith("uses:", StringComparison.Ordinal))
                {

                    continue;

                }

                string reference = trimmed["uses:".Length..].Trim();

                // A local action is this repository's own reviewed code, not an upstream ref.
                if (reference.StartsWith("./", StringComparison.Ordinal))
                {

                    continue;

                }

                int at = reference.LastIndexOf('@');

                if (at >= 0 && IsCommitSha(reference[(at + 1)..]))
                {

                    continue;

                }

                offenders.Add($"{Path.GetFileName(workflow)}:{i + 1}: {reference}");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A workflow runs an action from a mutable tag, so upstream decides what code executes "
            + "against this repository's source on the next run. Pin the full 40-character commit "
            + "SHA and keep the version in a trailing comment:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    private static bool IsCommitSha(string reference)
    {

        string candidate = reference.Split('#')[0].Trim();

        return candidate.Length == 40
            && candidate.All(static character =>
                character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    }

}
