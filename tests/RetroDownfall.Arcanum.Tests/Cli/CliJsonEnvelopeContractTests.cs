using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The Command Reference says which verbs have a typed <c>--json</c> payload and which hand back the
/// text envelope, and the handlers say the same.
/// </summary>
/// <remarks>
/// <para>Every direct command answers <c>--json</c> with exactly one JSON document, and the agent
/// orientation file says so in one line. What that line leaves out is that a verb with no typed
/// payload does not produce a document a script can read fields from: its printed text is captured at
/// the process boundary and returned as <c>{ "output": "...", "exitCode": n }</c>. A script that
/// pipes <c>arcanum ward list --json</c> into <c>jq '.[0].id'</c> finds that out in production.</para>
/// <para>The reference names the families that emit the envelope in a table. This test pins that
/// table to the handler source, family by family: a handler that gains a typed payload makes its row
/// stale and fails here, which is the direction that matters, because the table is the only thing
/// telling a script author the shape is not typed. It does not claim the table is exhaustive and the
/// reference says it is not.</para>
/// </remarks>
[Collection("GlobalConsole")]
public sealed class CliJsonEnvelopeContractTests
{
    private const string SectionHeading = "### Typed and text `--json` payloads";

    /// <summary>
    /// The handler source that implements each family the reference lists, relative to the Cli project.
    /// </summary>
    private static readonly Dictionary<string, string[]> HandlerSources = new(StringComparer.Ordinal)
    {
        ["ward"] = ["Commands/Wards/WardCommands.cs"],
        ["model"] = ["Commands/Configuration/ModelProviderCommands.cs"],
        ["provider"] = ["Commands/Configuration/ModelProviderCommands.cs"],
        ["operation"] = ["Commands/OperationCommands.cs"],
        ["daemon"] = ["Commands/Daemon/DaemonCommands.cs"],
        ["saga"] = ["Commands/Tower/SagaCommands.cs"],
        ["data encryption"] = ["Commands/DataEncryptionCommands.cs"],
        ["look"] = ["Commands/LookCommand.cs"],
        ["apprentice"] = ["Commands/Conclave/ApprenticeCommands.cs"],
        ["trial"] = ["Commands/ProvingGrounds/TrialCommands.cs"],
        ["tool"] = ["Commands/ToolCommands.cs"],
        ["mcp"] = ["Commands/Configuration/ResourceBrowseCommands.cs"],
        ["prompt"] = ["Commands/Tower/PromptCommands.cs"],
        ["campaign"] = ["Commands/Tower/CampaignCommands.cs"],
        ["spell"] = ["Commands/Tower/SpellCommands.cs"],
        ["workspace"] = ["Commands/Configuration/WorkspaceCommands.cs"],
    };

    private static readonly Regex Backticked = new(
        @"`(?<token>[^`]+)`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex MethodDeclaration = new(
        @"^[ \t]*(?:public|internal|private|protected)[ \t]+(?:static[ \t]+)?(?:async[ \t]+)?[\w<>\[\],.? ]+?[ \t]+(?<name>\w+)[ \t]*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex PublicTaskMethod = new(
        @"^[ \t]*(?:public|internal)[ \t]+(?:static[ \t]+)?(?:async[ \t]+)?Task<int>[ \t]+(?<name>\w+)[ \t]*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex CalledName = new(
        @"\b(?<name>\w+)[ \t]*\(",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Verbs_without_a_typed_payload_are_the_ones_the_reference_lists()
    {
        HashSet<string> registered = new(
            CliSurfaceTests.Walk(CliSurfaceTests.BuildMap()).Select(static command => command.Path),
            StringComparer.Ordinal);

        List<(string[] Families, string[] TypedVerbs)> rows = [.. ReferenceRows()];

        Assert.True(rows.Count >= 10, $"Only {rows.Count} families were read from the reference table.");

        List<string> offenders = [];

        foreach ((string[] families, string[] typedVerbs) in rows)
        {
            foreach (string family in families)
            {
                if (!registered.Contains(family))
                {
                    offenders.Add($"the reference names `arcanum {family}`, which the command tree does not register");
                }
            }

            foreach (string verb in typedVerbs)
            {
                if (!registered.Contains(verb))
                {
                    offenders.Add($"the reference names `arcanum {verb}` as typed, which the command tree does not register");
                }
            }

            HashSet<string> typedLeaves = new(
                typedVerbs.Select(static verb => verb[(verb.LastIndexOf(' ') + 1)..]),
                StringComparer.OrdinalIgnoreCase);

            foreach (string family in families)
            {
                if (!HandlerSources.TryGetValue(family, out string[]? files))
                {
                    offenders.Add($"`arcanum {family}` is in the reference table but names no handler source in this test");

                    continue;
                }

                HashSet<string> typedHandlers = new(StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    typedHandlers.UnionWith(TypedHandlerMethods(ReadCliSource(file)));
                }

                foreach (string method in typedHandlers.Where(method => !typedLeaves.Contains(method)).Order(StringComparer.Ordinal))
                {
                    offenders.Add($"`arcanum {family}`: handler {method} writes a typed payload but the reference lists the family as text-envelope");
                }

                foreach (string leaf in typedLeaves.Where(leaf => !typedHandlers.Contains(leaf)).Order(StringComparer.Ordinal))
                {
                    offenders.Add($"`arcanum {family}`: the reference lists {leaf} as typed but no handler of that name writes a typed payload");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The Command Reference's text-envelope table disagrees with the handlers:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void The_reference_and_the_agent_orientation_name_the_text_envelope_shape()
    {
        string reference = File.ReadAllText(CommandReferencePath()).Replace("\r\n", "\n", StringComparison.Ordinal);

        string section = ReferenceSection(reference);

        Assert.Contains("{ \"output\": \"<text>\", \"exitCode\": <n> }", section, StringComparison.Ordinal);

        Assert.Contains("not exhaustive", section, StringComparison.Ordinal);

        string agents = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "AGENTS.md"));

        Assert.Contains("\"output\"", agents, StringComparison.Ordinal);

        Assert.Contains("\"exitCode\"", agents, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape the table promises is the shape an untyped verb emits: one document with exactly an
    /// <c>output</c> text and an <c>exitCode</c>, not the array the host returned.
    /// </summary>
    [Fact]
    public void An_untyped_verb_under_json_emits_the_text_envelope()
    {
        WardDto ward = new("ward-1", "execute_command", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WardDto[]>([ward], true, null),
            ArcanumJsonContext.Default.ApiResponseWardDtoArray));

        CliTestResult result = RunCommand(handler, ["--json", "ward", "list"]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        Assert.Equal(
            ["exitCode", "output"],
            document.RootElement.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));

        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.Contains("ward-1", document.RootElement.GetProperty("output").GetString(), StringComparison.Ordinal);
    }

    private static IEnumerable<(string[] Families, string[] TypedVerbs)> ReferenceRows()
    {
        string section = ReferenceSection(File.ReadAllText(CommandReferencePath()).Replace("\r\n", "\n", StringComparison.Ordinal));

        foreach (string line in section.Split('\n').Where(static line => line.StartsWith("| `arcanum ", StringComparison.Ordinal)))
        {
            string[] cells = line.Split('|');

            string[] families =
            [
                .. Backticked
                    .Matches(cells[1])
                    .Select(static match => match.Groups["token"].Value)
                    .Select(static token => token.StartsWith("arcanum ", StringComparison.Ordinal) ? token["arcanum ".Length..] : token),
            ];

            string[] typedVerbs =
            [
                .. Backticked
                    .Matches(cells[2])
                    .Select(static match => match.Groups["token"].Value)
                    .Select(static token => token.StartsWith("arcanum ", StringComparison.Ordinal) ? token["arcanum ".Length..] : token),
            ];

            yield return (families, typedVerbs);
        }
    }

    private static string ReferenceSection(string reference)
    {
        int start = reference.IndexOf(SectionHeading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"The Command Reference has no '{SectionHeading}' section.");

        int end = reference.IndexOf("\n## ", start, StringComparison.Ordinal);

        Assert.True(end > start, "The text-envelope section no longer ends at a chapter heading.");

        return reference[start..end];
    }

    /// <summary>
    /// The public command-handler methods whose body reaches a typed <c>WriteJson</c> call, directly or
    /// through another method in the same file.
    /// </summary>
    private static HashSet<string> TypedHandlerMethods(string source)
    {
        Dictionary<string, List<string>> bodies = new(StringComparer.Ordinal);

        foreach (Match match in MethodDeclaration.Matches(source))
        {
            string name = match.Groups["name"].Value;

            if (name is "if" or "while" or "for" or "foreach" or "switch" or "using" or "lock" or "return" or "new")
            {
                continue;
            }

            if (!bodies.TryGetValue(name, out List<string>? list))
            {
                list = [];

                bodies[name] = list;
            }

            list.Add(MethodBody(source, match.Index + match.Length));
        }

        bool IsTyped(string name, HashSet<string> visiting)
        {
            if (!bodies.TryGetValue(name, out List<string>? methodBodies) || !visiting.Add(name))
            {
                return false;
            }

            foreach (string body in methodBodies)
            {
                if (body.Contains("WriteJson(", StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (Match call in CalledName.Matches(body))
                {
                    string callee = call.Groups["name"].Value;

                    if (callee != name && bodies.ContainsKey(callee) && IsTyped(callee, visiting))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return new HashSet<string>(
            PublicTaskMethod
                .Matches(source)
                .Select(static match => match.Groups["name"].Value)
                .Where(name => IsTyped(name, new HashSet<string>(StringComparer.Ordinal))),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The text of a member from the opening parenthesis of its parameter list to the end of its body,
    /// whether the body is a block or an expression.
    /// </summary>
    private static string MethodBody(
        string source,
        int afterOpenParenthesis)
    {
        int depth = 1;

        int index = afterOpenParenthesis;

        while (index < source.Length && depth > 0)
        {
            depth += source[index] == '(' ? 1 : source[index] == ')' ? -1 : 0;

            index++;
        }

        int bodyStart = index;

        while (index < source.Length && source[index] is not '{' and not ';' && !(source[index] == '=' && index + 1 < source.Length && source[index + 1] == '>'))
        {
            index++;
        }

        if (index >= source.Length)
        {
            return string.Empty;
        }

        if (source[index] == ';')
        {
            return string.Empty;
        }

        if (source[index] == '=')
        {
            int end = source.IndexOf(';', index);

            return source[bodyStart..(end < 0 ? source.Length : end)];
        }

        int braces = 0;

        int cursor = index;

        while (cursor < source.Length)
        {
            braces += source[cursor] == '{' ? 1 : source[cursor] == '}' ? -1 : 0;

            cursor++;

            if (braces == 0)
            {
                break;
            }
        }

        return source[bodyStart..cursor];
    }

    private static string ReadCliSource(string relative) =>
        File.ReadAllText(
            Path.Combine(
                TestRepositoryPaths.RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Cli",
                relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string CommandReferencePath() =>
        Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.Command.Reference.md");

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        return CliTestHarness.Run(services, args);
    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };
    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
