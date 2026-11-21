using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence;

internal sealed class GitHubHostDefectTracker(IHttpClientFactory clients)
{
    internal const string ClientName = "GitHubHostDefectTracker";

    internal const string RepositoryVariable = "ARCANUM_HOST_DEFECT_GITHUB_REPOSITORY";

    internal const string TokenVariable = "ARCANUM_HOST_DEFECT_GITHUB_TOKEN";

    private const string ParentVariable = "ARCANUM_HOST_DEFECT_GITHUB_PARENT_ISSUE";

    private const string ProjectVariable = "ARCANUM_HOST_DEFECT_GITHUB_PROJECT_ID";

    private const string ProjectMutation = """
        mutation ArcanumLinkDefect($project: ID!, $issue: ID!) {
          addProjectV2ItemById(input: {projectId: $project, contentId: $issue}) {
            item { id project { id } content { ... on Issue { id } } }
          }
        }
        """;

    private const int PageSize = 40;

    private const int MaxPages = 25;

    internal sealed class Capability(string repository, string token, int? parentIssue, string? projectId)
    {
        internal string Repository { get; } = repository;

        internal string Token { get; } = token;

        internal int? ParentIssue { get; } = parentIssue;

        internal string? ProjectId { get; } = projectId;
    }

    internal readonly record struct IssueLookup(bool Complete, int? Issue, bool EvidenceInIssue);

    internal readonly record struct EvidenceLookup(bool Complete, bool Found);

    internal readonly record struct MutationReceipt(long Id, bool Rejected);

    internal readonly record struct Membership(bool ParentLinked, bool ProjectLinked);

    internal static Capability? ReadCapability()
    {
        string? repository = System.Environment.GetEnvironmentVariable(RepositoryVariable);
        string? token = System.Environment.GetEnvironmentVariable(TokenVariable);

        if (repository is null || token is null || token.Length is < 1 or > 2048
            || token.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            return null;

        if (repository.Split('/') is not [string owner, string name]
            || owner.Length is < 1 or > 39 || name.Length is < 1 or > 100 || name is "." or ".."
            || owner.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-')
            || name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return null;

        int? parent = int.TryParse(System.Environment.GetEnvironmentVariable(ParentVariable), NumberStyles.None,
            CultureInfo.InvariantCulture, out int number) && number > 0 ? number : null;

        string? project = System.Environment.GetEnvironmentVariable(ProjectVariable);

        return new(repository.ToLowerInvariant(), token, parent, IsNodeId(project) ? project : null);
    }

    internal async Task<IssueLookup> FindIssueAsync(Capability capability, string marker, string evidence,
        CancellationToken cancellationToken, int? retainedIssue = null)
    {
        if (retainedIssue is { } number)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            deadline.CancelAfter(TimeSpan.FromSeconds(15));

            using HttpClient client = clients.CreateClient(ClientName);

            var exact = await MembershipRequestAsync(client, NewRequest(capability, HttpMethod.Get, $"/issues/{number}"),
                deadline.Token, cancellationToken).ConfigureAwait(false);

            return exact.Status == HttpStatusCode.OK && IsIssue(exact.Body, capability, number, marker)
                ? new(true, number, BodyStartsWith(exact.Body, marker + "\n" + evidence)) : default;
        }

        var lookup = await FindAsync(capability, "/issues", "state=all&sort=created&direction=desc",
            item => !item.TryGetProperty("pull_request", out _) && BodyStartsWith(item, marker), cancellationToken).ConfigureAwait(false);

        if (lookup.Item is not { } issue) return new(lookup.Complete, null, false);

        int? located = PositiveIssueNumber(issue);

        return new(located is not null, located, BodyStartsWith(issue, marker + "\n" + evidence));
    }

    internal async Task<EvidenceLookup> FindEvidenceAsync(Capability capability, int issue, string evidence,
        CancellationToken cancellationToken)
    {
        var lookup = await FindAsync(capability, $"/issues/{issue}/comments", "",
            item => BodyStartsWith(item, evidence), cancellationToken).ConfigureAwait(false);

        return new(lookup.Complete, lookup.Item is not null);
    }

    internal async Task<MutationReceipt> CreateAsync(Capability capability, string title, string body, CancellationToken cancellationToken)
    {
        var post = await PostAsync(capability, "/issues", title, body, cancellationToken).ConfigureAwait(false);

        using JsonDocument? response = post.Body;

        return new(response is null ? 0 : PositiveIssueNumber(response.RootElement) ?? 0, post.Rejected);
    }

    internal async Task<MutationReceipt> AddEvidenceAsync(Capability capability, int issue, string body, CancellationToken cancellationToken)
    {
        var post = await PostAsync(capability, $"/issues/{issue}/comments", null, body, cancellationToken).ConfigureAwait(false);

        using JsonDocument? response = post.Body;

        long number = response?.RootElement is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out long item) && item > 0 ? item : 0;

        return new(number, post.Rejected);
    }

    internal async Task<Membership> EnsureMembershipAsync(Capability capability, int issue, string marker, CancellationToken cancellationToken)
    {
        if (capability.ParentIssue is null && capability.ProjectId is null) return default;

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        using HttpClient client = clients.CreateClient(ClientName);

        var lookedUp = await MembershipRequestAsync(client, NewRequest(capability, HttpMethod.Get, $"/issues/{issue}"),
            deadline.Token, cancellationToken).ConfigureAwait(false);

        JsonElement child = lookedUp.Body;

        if (lookedUp.Status != HttpStatusCode.OK || !IsIssue(child, capability, issue, marker)) return default;

        JsonElement id = Property(child, "id");

        bool parentLinked = capability.ParentIssue is { } parent && parent != issue && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out long childId) && childId > 0
            && await EnsureParentAsync(client, capability, issue, parent, childId, deadline.Token, cancellationToken).ConfigureAwait(false);

        string? nodeId = Text(child, "node_id");

        bool projectLinked = capability.ProjectId is { } project && IsNodeId(nodeId)
            && await EnsureProjectAsync(client, capability, project, nodeId!, deadline.Token, cancellationToken).ConfigureAwait(false);

        return new(parentLinked, projectLinked);
    }

    private static async Task<bool> EnsureParentAsync(HttpClient client, Capability capability, int issue, int parent,
        long childId, CancellationToken deadline, CancellationToken cancellationToken)
    {
        string parentPath = $"/issues/{issue}/parent";

        var existing = await MembershipRequestAsync(client, NewRequest(capability, HttpMethod.Get, parentPath),
            deadline, cancellationToken).ConfigureAwait(false);

        if (existing.Status == HttpStatusCode.OK) return IsRepositoryIssue(existing.Body, capability, parent);

        if (existing.Status != HttpStatusCode.NotFound) return false;

        HttpRequestMessage attach = NewRequest(capability, HttpMethod.Post, $"/issues/{parent}/sub_issues");

        attach.Content = JsonContent(writer =>
        {
            writer.WriteNumber("sub_issue_id", childId);
            writer.WriteBoolean("replace_parent", false);
        });

        _ = await MembershipRequestAsync(client, attach, deadline, cancellationToken).ConfigureAwait(false);

        var observed = await MembershipRequestAsync(client, NewRequest(capability, HttpMethod.Get, parentPath),
            deadline, cancellationToken).ConfigureAwait(false);

        return observed.Status == HttpStatusCode.OK && IsRepositoryIssue(observed.Body, capability, parent);
    }

    private static async Task<bool> EnsureProjectAsync(HttpClient client, Capability capability, string project, string issue,
        CancellationToken deadline, CancellationToken cancellationToken)
    {
        HttpRequestMessage attach = NewRequest(capability, HttpMethod.Post, "/graphql", graphQl: true);

        attach.Content = JsonContent(writer =>
        {
            writer.WriteString("query", ProjectMutation);
            writer.WriteStartObject("variables");
            writer.WriteString("project", project);
            writer.WriteString("issue", issue);
            writer.WriteEndObject();
        });

        // The GitHub mutation returns the existing item when this exact content is already linked.
        var observed = await MembershipRequestAsync(client, attach, deadline, cancellationToken).ConfigureAwait(false);

        JsonElement item = Property(Property(Property(observed.Body, "data"), "addProjectV2ItemById"), "item");

        return observed.Status == HttpStatusCode.OK && Property(observed.Body, "errors").ValueKind == JsonValueKind.Undefined
            && IsNodeId(Text(item, "id")) && Text(Property(item, "project"), "id") == project
            && Text(Property(item, "content"), "id") == issue;
    }

    private static async Task<(HttpStatusCode? Status, JsonElement Body)> MembershipRequestAsync(HttpClient client,
        HttpRequestMessage request, CancellationToken deadline, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, default);

                using JsonDocument body = await ReadBodyAsync(response, deadline).ConfigureAwait(false);

                return (response.StatusCode, body.RootElement.Clone());
            }
            catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
            {
                return default;
            }
        }
    }

    private static bool IsIssue(JsonElement item, Capability capability, int issue, string marker) =>
        IsRepositoryIssue(item, capability, issue) && BodyStartsWith(item, marker);

    private static bool IsRepositoryIssue(JsonElement item, Capability capability, int number) =>
        PositiveIssueNumber(item) == number && !item.TryGetProperty("pull_request", out _) && InRepository(item, capability);

    private static bool InRepository(JsonElement item, Capability capability) =>
        string.Equals(Text(item, "repository_url"), $"https://api.github.com/repos/{capability.Repository}", StringComparison.OrdinalIgnoreCase);

    private static bool IsNodeId(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+' or '/' or '=');

    private static JsonElement Property(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var result) ? result : default;

    private static string? Text(JsonElement item, string key) => Property(item, key) is { ValueKind: JsonValueKind.String } value
        ? value.GetString() : null;

    private async Task<(bool Complete, JsonElement? Item)> FindAsync(Capability capability, string path,
        string query, Func<JsonElement, bool> matches, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using HttpClient client = clients.CreateClient(ClientName);

            for (int page = 1; page <= MaxPages; page++)
            {
                using HttpRequestMessage request = NewRequest(capability, HttpMethod.Get,
                    $"{path}?{query}{(query.Length == 0 ? "" : "&")}per_page={PageSize}&page={page}");

                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK) return (false, null);

                using JsonDocument list = await ReadBodyAsync(response, deadline.Token).ConfigureAwait(false);

                if (list.RootElement.ValueKind != JsonValueKind.Array) return (false, null);

                foreach (JsonElement item in list.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) return (false, null);

                    if (matches(item)) return (true, item.Clone());
                }

                bool next = response.Headers.TryGetValues("Link", out var links)
                    && links.Any(static link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));

                if (list.RootElement.GetArrayLength() < PageSize && !next) return (true, null);
            }
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            return (false, null);
        }

        return (false, null);
    }

    private async Task<(JsonDocument? Body, bool Rejected)> PostAsync(Capability capability, string path, string? title, string body,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using HttpClient client = clients.CreateClient(ClientName);
            using HttpRequestMessage request = NewRequest(capability, HttpMethod.Post, path);
            request.Content = JsonContent(writer =>
            {
                if (title is not null) writer.WriteString("title", title);

                writer.WriteString("body", body);
            });

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Created)
                return (await ReadBodyAsync(response, deadline.Token).ConfigureAwait(false), false);

            return (null, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            return default;
        }
    }

    private static async Task<JsonDocument> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static ByteArrayContent JsonContent(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return new ByteArrayContent(stream.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
    }

    private static HttpRequestMessage NewRequest(Capability capability, HttpMethod method, string path, bool graphQl = false)
    {
        HttpRequestMessage request = new(method, graphQl ? "https://api.github.com/graphql"
            : $"https://api.github.com/repos/{capability.Repository}{path}");

        request.Headers.Authorization = new("Bearer", capability.Token);
        request.Headers.Accept.Add(new("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Arcanum-Host-Diagnostics/1");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        return request;
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException or JsonException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static bool BodyStartsWith(JsonElement item, string marker) =>
        item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
        && body.GetString() is { } text
        && (string.Equals(text, marker, StringComparison.Ordinal) || text.StartsWith(marker + "\n", StringComparison.Ordinal));

    private static int? PositiveIssueNumber(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("number", out var number)
        && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int value) && value > 0
            ? value : null;
}
