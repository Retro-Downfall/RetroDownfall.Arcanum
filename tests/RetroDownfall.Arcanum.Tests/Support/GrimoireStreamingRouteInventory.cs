using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Api.Streaming;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The kind of authored construct that makes a response a stream.
/// </summary>
/// <remarks>
/// Five kinds rather than one, because a route can frame itself in more than one way and two of them
/// frame themselves through a shared writer. Without the two invocation kinds the three NDJSON routes
/// would collapse into a single discovery at the writer's own declaration, and the catalog could not
/// say which route was which — the same reason the acquisition inventory discovers a marked route's
/// invocations as well as its declaration.
/// </remarks>
internal enum StreamingConstructKind : byte
{

    /// <summary>A call to the shared SSE writer's response preparation — one per SSE route.</summary>
    SseWriterInvocation = 1,

    /// <summary>A call to the shared NDJSON writer — one per native inference route.</summary>
    NdjsonWriterInvocation = 2,

    /// <summary>A response content type assigned from a streaming media-type literal.</summary>
    StreamingContentTypeAssignment = 3,

    /// <summary>A raw byte body handed to the framework to copy out.</summary>
    ByteStreamResult = 4,

    /// <summary>A streaming surface mapped by a third-party package rather than authored here.</summary>
    ThirdPartyStreamingMap = 5,

}

/// <summary>
/// Why a catalogued construct is something other than a route with a pattern of its own.
/// </summary>
internal enum StreamingEntryProofKind : byte
{

    /// <summary>A shared writer, which serves whichever routes call it and has no route of its own.</summary>
    SharedWriterDeclaration = 1,

    /// <summary>Ends on its own in bounded time, holding no producer to stop.</summary>
    EndsWithoutAProducer = 2,

    /// <summary>A provider is already charging for it; cutting it bills for an unreceived answer.</summary>
    ProviderAlreadyBilling = 3,

    /// <summary>Framed by a third-party package whose writer this codebase does not own.</summary>
    ThirdPartyFraming = 4,

}

/// <summary>One authored streaming construct, normalized so a catalog can name it exactly.</summary>
/// <remarks>
/// The route literal is part of the identity rather than only of the catalog's description, because
/// three of the five SSE routes are lambdas inside one <c>MapEventEndpoints</c> and are otherwise
/// indistinguishable: same file, same enclosing member, same construct, same fingerprint. Without it
/// they would collapse into one discovery and the inventory would report a duplicate instead of three
/// routes. It is also what the parent design names first among the keys.
/// </remarks>
internal readonly record struct StreamingIdentity(
    string RelativePath,
    string EnclosingType,
    string EnclosingMember,
    StreamingConstructKind ConstructKind,
    string RouteLiteral,
    string Fingerprint);

/// <summary>One catalogued streaming surface and everything the inventory records about it.</summary>
internal sealed record StreamingCatalogEntry(
    StreamingIdentity Identity,
    string RoutePattern,
    string EndpointName,
    string Framing,
    GrimoireStreamAuthority Authority,
    GrimoireStreamClass Class,
    StreamingEntryProofKind? Proof);

internal enum StreamingInventoryFailureCode : byte
{

    UncataloguedDiscovery = 1,

    StaleCatalogEntry = 2,

    DuplicateCatalogEntry = 3,

    DuplicateDiscovery = 4,

    WildcardIdentity = 5,

    MissingDrainProof = 6,

    QuiesceableRouteNotDeclared = 7,

    ProofOnQuiesceableEntry = 8,

}

internal sealed record StreamingInventoryFailure(
    StreamingInventoryFailureCode Code,
    StreamingIdentity? Identity,
    string Detail);

/// <summary>A source file the streaming scanner reads.</summary>
internal readonly record struct StreamingSource(string RelativePath, string Text);

/// <summary>
/// Discovers every authored streaming response and checks it against the hand-authored catalog.
/// </summary>
/// <remarks>
/// Bidirectional on purpose, and that is the difference between an inventory and an allow-list. A
/// construct the scanner finds with no catalog entry is a streaming route nobody classified, and a
/// catalog entry the scanner no longer finds is a classification describing code that has moved —
/// both are failures, because a catalog that only grew would eventually describe a codebase that no
/// longer exists.
///
/// <para>Two constructs are deliberately out of reach and their absence is a decision rather than an
/// oversight. <c>GET /metrics</c> renders its whole body to a string and writes it once; it assigns no
/// streaming media type, is mapped outside <c>/api</c>, and holds no producer. The idempotency replay
/// result writes a cached body verbatim including its recorded content type, so a replayed hit on a
/// streaming route returns that media type as one buffered write — but it reads the type from the
/// cache rather than a literal, produces nothing, and ends immediately. Neither is a live stream and
/// neither can be quiesced, because there is nothing running to stop.</para>
/// </remarks>
internal static class GrimoireStreamingRouteScanner
{

    /// <summary>The complete positive quiesceable set, as the parent design declares it.</summary>
    internal static readonly string[] DeclaredQuiesceableRoutes =
    [
        "/api/apprentices/{id:guid}/chronicle",
        "/api/events/daemon",
        "/api/events/logs",
        "/api/events/mcp",
        "/api/sessions/{id:guid}/stream",
    ];

    /// <summary>
    /// Every authored production source, read the way the acquisition inventory reads them.
    /// </summary>
    /// <remarks>
    /// Comments are kept rather than stripped, because this scanner parses C# rather than matching
    /// text: a construct inside a comment is not a syntax node and cannot be discovered, so stripping
    /// would only risk changing line structure for no benefit.
    /// </remarks>
    internal static IReadOnlyList<StreamingSource> ProductionSources()
    {

        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        List<StreamingSource> sources = [];

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(repositoryRoot, "src"),
            "*.cs",
            SearchOption.AllDirectories))
        {

            // Generated intermediates are build output, not authored production code.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {

                continue;

            }

            sources.Add(new StreamingSource(
                Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'),
                File.ReadAllText(file)));

        }

        return sources;

    }

    private static readonly string[] StreamingMediaTypes =
    [
        "text/event-stream",
        "application/x-ndjson",
    ];

    internal static IReadOnlyList<StreamingIdentity> Discover(IEnumerable<StreamingSource> sources)
    {

        List<StreamingIdentity> identities = [];

        foreach ((StreamingSource source, CompilationUnitSyntax root) in Parse(sources))
        {

            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {

                StreamingConstructKind? kind = TerminalName(invocation.Expression) switch
                {
                    "PrepareResponse" => StreamingConstructKind.SseWriterInvocation,

                    "WriteStreamAsync" => StreamingConstructKind.NdjsonWriterInvocation,

                    "Stream" when TerminalName(ReceiverOf(invocation.Expression)) == "Results" =>
                        StreamingConstructKind.ByteStreamResult,

                    "MapA2A" => StreamingConstructKind.ThirdPartyStreamingMap,

                    _ => null,
                };

                if (kind is { } discovered)
                {

                    identities.Add(Identity(source.RelativePath, invocation, discovered));

                }

            }

            foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>())
            {

                if (TerminalName(assignment.Left) != "ContentType"
                    || assignment.Right is not LiteralExpressionSyntax literal
                    || literal.Token.ValueText is not { } media
                    || !StreamingMediaTypes.Any(candidate =>
                        media.StartsWith(candidate, StringComparison.Ordinal)))
                {

                    continue;

                }

                identities.Add(Identity(
                    source.RelativePath,
                    assignment,
                    StreamingConstructKind.StreamingContentTypeAssignment));

            }

        }

        return identities;

    }

    internal static IReadOnlyList<StreamingInventoryFailure> Validate(
        IReadOnlyList<StreamingIdentity> discoveries,
        IReadOnlyList<StreamingCatalogEntry> catalog)
    {

        List<StreamingInventoryFailure> failures = [];

        foreach (IGrouping<StreamingIdentity, StreamingIdentity> duplicate in
            discoveries.GroupBy(static identity => identity))
        {

            if (duplicate.Count() > 1)
            {

                failures.Add(new(
                    StreamingInventoryFailureCode.DuplicateDiscovery,
                    duplicate.Key,
                    "The syntax scanner resolved this construct more than once."));

            }

        }

        foreach (IGrouping<StreamingIdentity, StreamingCatalogEntry> duplicate in
            catalog.GroupBy(static entry => entry.Identity))
        {

            if (duplicate.Count() > 1)
            {

                failures.Add(new(
                    StreamingInventoryFailureCode.DuplicateCatalogEntry,
                    duplicate.Key,
                    "The catalog contains this construct more than once."));

            }

        }

        HashSet<StreamingIdentity> discovered = [.. discoveries];

        HashSet<StreamingIdentity> catalogued = [.. catalog.Select(static entry => entry.Identity)];

        foreach (StreamingIdentity discovery in discovered.Except(catalogued))
        {

            failures.Add(new(
                StreamingInventoryFailureCode.UncataloguedDiscovery,
                discovery,
                "The syntax scanner found a streaming construct with no exact catalog entry."));

        }

        foreach (StreamingIdentity entry in catalogued.Except(discovered))
        {

            failures.Add(new(
                StreamingInventoryFailureCode.StaleCatalogEntry,
                entry,
                "The catalog names a streaming construct the syntax scanner no longer finds."));

        }

        foreach (StreamingCatalogEntry entry in catalog)
        {

            if (HasWildcardIdentity(entry.Identity))
            {

                failures.Add(new(
                    StreamingInventoryFailureCode.WildcardIdentity,
                    entry.Identity,
                    "A catalog identity must name one exact authored construct, not a wildcard."));

            }

            if (entry.Class == GrimoireStreamClass.GrimoireQuiesceableStream)
            {

                if (!DeclaredQuiesceableRoutes.Contains(entry.RoutePattern, StringComparer.Ordinal))
                {

                    failures.Add(new(
                        StreamingInventoryFailureCode.QuiesceableRouteNotDeclared,
                        entry.Identity,
                        "Only the five routes the parent design declares may be quiesceable."));

                }

                if (entry.Proof is not null)
                {

                    failures.Add(new(
                        StreamingInventoryFailureCode.ProofOnQuiesceableEntry,
                        entry.Identity,
                        "A quiesceable stream needs no proof; the proof records why a stream is drained."));

                }

                continue;

            }

            if (entry.Proof is null)
            {

                failures.Add(new(
                    StreamingInventoryFailureCode.MissingDrainProof,
                    entry.Identity,
                    "Every stream that is drained rather than quiesced must record why."));

            }

        }

        return failures;

    }

    /// <summary>
    /// The catalog: every streaming construct this repository authors, and what it is.
    /// </summary>
    /// <remarks>
    /// Fifteen entries — thirteen surfaces and the two shared writers' own declarations. A writer
    /// serves whichever routes call it and carries no route pattern of its own, which is why its entry
    /// names one rather than inventing a fictional route for it; it is classified <c>FiniteDrain</c>
    /// because a declaration frames nothing on its own, and its proof records that it is a writer
    /// rather than a route.
    /// </remarks>
    internal static IReadOnlyList<StreamingCatalogEntry> Catalog() =>
    [

        // ── The five declared quiesceable SSE routes ────────────────────────────────────────────
        new(
            new("src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs", "EventEndpoints", "MapEventEndpoints(1)", StreamingConstructKind.SseWriterInvocation, "/events/daemon", "SseStreamWriter.PrepareResponse(httpContext)"),
            "/api/events/daemon",
            "GetDaemonEvents",
            "SSE",
            GrimoireStreamAuthority.NoGrimoireAuthority,
            GrimoireStreamClass.GrimoireQuiesceableStream,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs", "EventEndpoints", "MapEventEndpoints(1)", StreamingConstructKind.SseWriterInvocation, "/events/mcp", "SseStreamWriter.PrepareResponse(httpContext)"),
            "/api/events/mcp",
            "GetMcpEvents",
            "SSE",
            GrimoireStreamAuthority.NoGrimoireAuthority,
            GrimoireStreamClass.GrimoireQuiesceableStream,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs", "EventEndpoints", "MapEventEndpoints(1)", StreamingConstructKind.SseWriterInvocation, "/events/logs", "SseStreamWriter.PrepareResponse(httpContext)"),
            "/api/events/logs",
            "StreamLogs",
            "SSE",
            GrimoireStreamAuthority.NoGrimoireAuthority,
            GrimoireStreamClass.GrimoireQuiesceableStream,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs", "SessionEndpoints", "MapSessionEndpoints(1)", StreamingConstructKind.SseWriterInvocation, "/sessions/{id:guid}/stream", "SseStreamWriter.PrepareResponse(httpContext)"),
            "/api/sessions/{id:guid}/stream",
            "StreamSession",
            "SSE",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.GrimoireQuiesceableStream,
            null),

        new(
            new("src/RetroDownfall.Arcanum.Api/Conclave/ApprenticeEndpoints.cs", "ApprenticeEndpoints", "MapApprenticeEndpoints(1)", StreamingConstructKind.SseWriterInvocation, "/apprentices/{id:guid}/chronicle", "SseStreamWriter.PrepareResponse(httpContext)"),
            "/api/apprentices/{id:guid}/chronicle",
            "GetApprenticeChronicle",
            "SSE",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.GrimoireQuiesceableStream,
            null),


        // ── The billable inference streams ──────────────────────────────────────────────────────
        new(
            new("src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs", "IntelligenceEndpoints", "MapIntelligenceEndpoints(1)", StreamingConstructKind.NdjsonWriterInvocation, "/intelligence/ping-stream", "InferenceExecuteWriter.WriteStreamAsync(httpContext,intelligence,resolvedRequest.Value.Request,ct,pingStreamAuditContext,resolvedRequest.Value.Campaign)"),
            "/api/intelligence/ping-stream",
            "PostIntelligencePingStream",
            "NDJSON",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.BillableDrain,
            StreamingEntryProofKind.ProviderAlreadyBilling),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs", "PromptEndpoints", "MapPromptEndpoints(1)", StreamingConstructKind.NdjsonWriterInvocation, "/prompts/{id:guid}/execute-stream", "InferenceExecuteWriter.WriteStreamAsync(ctx,intelligence,resolvedPing.Value.Request,ctx.RequestAborted,auditContext:null,resolvedPing.Value.Campaign)"),
            "/api/prompts/{id:guid}/execute-stream",
            "Prompt_ExecuteStream",
            "NDJSON",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.BillableDrain,
            StreamingEntryProofKind.ProviderAlreadyBilling),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/SpellExecutionEndpoints.cs", "SpellExecutionEndpoints", "MapSpellExecutionEndpoints(1)", StreamingConstructKind.NdjsonWriterInvocation, "/spells/{name}/execute-stream", "InferenceExecuteWriter.WriteStreamAsync(ctx,intelligence,resolvedPing.Value.Request,ctx.RequestAborted,auditContext:null,resolvedPing.Value.Campaign)"),
            "/api/spells/{name}/execute-stream",
            "Spell_ExecuteStream",
            "NDJSON",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.BillableDrain,
            StreamingEntryProofKind.ProviderAlreadyBilling),

        new(
            new("src/RetroDownfall.Arcanum.Api/Intelligence/WebWorkflowEndpoints.cs", "WebWorkflowEndpoints", "HandleResearchAsync(4)", StreamingConstructKind.StreamingContentTypeAssignment, "<none>", "httpContext.Response.ContentType=\"application/x-ndjson\""),
            "/api/web/research",
            "PostWebResearch",
            "NDJSON",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.BillableDrain,
            StreamingEntryProofKind.ProviderAlreadyBilling),

        new(
            new("src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs", "OpenAiV1Endpoints", "HandleStreamingAsync(11)", StreamingConstructKind.StreamingContentTypeAssignment, "<none>", "httpContext.Response.ContentType=\"text/event-stream; charset=utf-8\""),
            "/v1/chat/completions",
            "PostOpenAiChatCompletions",
            "SSE",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.BillableDrain,
            StreamingEntryProofKind.ProviderAlreadyBilling),


        // ── The finite byte-body downloads ──────────────────────────────────────────────────────
        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs", "SessionEndpoints", "MapSessionEndpoints(1)", StreamingConstructKind.ByteStreamResult, "/sessions/{id:guid}/attachments/{attachmentId:guid}/content", "Results.Stream(plaintext,mimeType,fileDownloadName:downloadName,enableRangeProcessing:false)"),
            "/api/sessions/{id:guid}/attachments/{attachmentId:guid}/content",
            "DownloadSessionAttachment",
            "bytes",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.FiniteDrain,
            StreamingEntryProofKind.EndsWithoutAProducer),

        new(
            new("src/RetroDownfall.Arcanum.Api/OpenAiV1FilesEndpoints.cs", "OpenAiV1Endpoints", "HandleContentAsync(4)", StreamingConstructKind.ByteStreamResult, "<none>", "Results.Stream(plaintext,mimeType,fileDownloadName:record.Filename,enableRangeProcessing:false)"),
            "/v1/files/{id}/content",
            "GetOpenAiFileContent",
            "bytes",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.FiniteDrain,
            StreamingEntryProofKind.EndsWithoutAProducer),


        // ── The third-party streaming surface ───────────────────────────────────────────────────
        new(
            new("src/RetroDownfall.Arcanum.Api/A2A/A2AServerEndpoints.cs", "A2AServerEndpoints", "MapA2AServer(2)", StreamingConstructKind.ThirdPartyStreamingMap, "<none>", "apiGroup.MapA2A(server,relative)"),
            "/api/conclave/a2a",
            "<package-owned>",
            "SDK-owned SSE",
            GrimoireStreamAuthority.LiveGrimoire,
            GrimoireStreamClass.BillableDrain,
            StreamingEntryProofKind.ThirdPartyFraming),


        // ── The two shared writers' own declarations ────────────────────────────────────────────
        new(
            new("src/RetroDownfall.Arcanum.Api/Streaming/SseStreamWriter.cs", "SseStreamWriter", "PrepareResponse(1)", StreamingConstructKind.StreamingContentTypeAssignment, "<none>", "httpContext.Response.ContentType=\"text/event-stream; charset=utf-8\""),
            "<shared writer>",
            "<shared writer>",
            "SSE",
            GrimoireStreamAuthority.NoGrimoireAuthority,
            GrimoireStreamClass.FiniteDrain,
            StreamingEntryProofKind.SharedWriterDeclaration),

        new(
            new("src/RetroDownfall.Arcanum.Api/Tower/InferenceExecuteWriter.cs", "InferenceExecuteWriter", "WriteStreamAsync(6)", StreamingConstructKind.StreamingContentTypeAssignment, "<none>", "httpContext.Response.ContentType=\"application/x-ndjson; charset=utf-8\""),
            "<shared writer>",
            "<shared writer>",
            "NDJSON",
            GrimoireStreamAuthority.NoGrimoireAuthority,
            GrimoireStreamClass.FiniteDrain,
            StreamingEntryProofKind.SharedWriterDeclaration),

    ];

    private static bool HasWildcardIdentity(StreamingIdentity identity) =>
        identity.RelativePath.Contains('*', StringComparison.Ordinal)
        || identity.EnclosingType.Contains('*', StringComparison.Ordinal)
        || identity.EnclosingMember.Contains('*', StringComparison.Ordinal)
        || identity.RouteLiteral.Contains('*', StringComparison.Ordinal)
        || identity.Fingerprint.Contains('*', StringComparison.Ordinal);

    private static StreamingIdentity Identity(
        string relativePath,
        SyntaxNode node,
        StreamingConstructKind constructKind) =>
        new(
            relativePath.Replace('\\', '/'),
            EnclosingType(node),
            EnclosingMember(node),
            constructKind,
            EnclosingRouteLiteral(node),
            Tokens(node));

    /// <summary>
    /// The route literal of the nearest enclosing <c>Map*</c> call, or a marker when there is none.
    /// </summary>
    /// <remarks>
    /// Resolved syntactically rather than semantically, so it is the literal as authored — the group
    /// prefix is not applied and does not need to be, because the identity only has to be unique
    /// among constructs in the same member. A construct that is not inside a mapped lambda, such as
    /// either shared writer or a handler referenced by method group, correctly resolves to none and is
    /// distinguished by its enclosing member instead.
    /// </remarks>
    private static string EnclosingRouteLiteral(SyntaxNode node)
    {

        foreach (InvocationExpressionSyntax invocation in node.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>())
        {

            if (!TerminalName(invocation.Expression).StartsWith("Map", StringComparison.Ordinal))
            {

                continue;

            }

            if (invocation.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax literal }, ..]
                && literal.Token.ValueText is { Length: > 0 } route)
            {

                return route;

            }

        }

        return "<none>";

    }

    private static SyntaxNode ReceiverOf(SyntaxNode expression) =>
        expression is MemberAccessExpressionSyntax memberAccess ? memberAccess.Expression : expression;

    private static string EnclosingType(SyntaxNode node)
    {

        string[] types =
        [
            .. node.Ancestors().OfType<TypeDeclarationSyntax>()
                .Reverse()
                .Select(static type => type.Identifier.ValueText),
        ];

        return types.Length == 0 ? "<global>" : string.Join('.', types);

    }

    private static string EnclosingMember(SyntaxNode node)
    {

        LocalFunctionStatementSyntax? localFunction = node.AncestorsAndSelf()
            .OfType<LocalFunctionStatementSyntax>()
            .FirstOrDefault();

        if (localFunction is not null)
        {

            return localFunction.Identifier.ValueText + "(" + localFunction.ParameterList.Parameters.Count + ")";

        }

        MethodDeclarationSyntax? method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return method is not null
            ? method.Identifier.ValueText + "(" + method.ParameterList.Parameters.Count + ")"
            : "<global>";

    }

    private static string TerminalName(SyntaxNode node) => node switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,

        MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,

        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,

        GenericNameSyntax generic => generic.Identifier.ValueText,

        QualifiedNameSyntax qualified => TerminalName(qualified.Right),

        _ => node.GetLastToken().ValueText,
    };

    private static string Tokens(SyntaxNode node) => string.Concat(
        node.DescendantTokens().Select(static token => token.Text));

    private static List<(StreamingSource Source, CompilationUnitSyntax Root)> Parse(
        IEnumerable<StreamingSource> sources) =>
    [
        .. sources.Select(static source => (
            source,
            CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.Preview))
                .GetCompilationUnitRoot())),
    ];

}
