using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Cli.Commands.Guild;

internal sealed partial class GuildCommands
{
    private async Task<int> CohortAsync(string path, GuildCommandInput input, Guid key, long revision, CancellationToken ct)
    {
        string? cursor = input.Text("cursor");

        int size = (int)(input.Number("page-size") ?? 100);

        switch (path)
        {
            case "cohort create":
            {
                GuildConvergenceKind kind = input.Text("policy") switch
                {
                    "all" => GuildConvergenceKind.All,

                    "first" => GuildConvergenceKind.First,

                    "quorum" => GuildConvergenceKind.Quorum,

                    _ => throw new GuildCliInputException("--policy must be all, first or quorum."),
                };

                long? requestedQuorum = input.Number("quorum");

                if ((kind == GuildConvergenceKind.Quorum) != (requestedQuorum is not null) || requestedQuorum > int.MaxValue)
                    throw new GuildCliInputException("--quorum is required only with --policy quorum and must be a positive Int32.");

                GuildRequestedGrant? grant = await Grant<GuildRequestedGrant>(input.Text("grant-file"), false, ct).ConfigureAwait(false);

                Guid issuer = await Select("task", input.Text("issuer"), ct).ConfigureAwait(false);

                return await Post<CreateCohortRequest, CohortDto>("api/guild/cohorts",
                    new(key, issuer, Required(input, "name"), new(kind, (int?)requestedQuorum), new(Required(input, "result-type")), grant), ct).ConfigureAwait(false);
            }

            case "cohort list":
            {
                Guid? root = await Optional("task", input.Text("root"), ct).ConfigureAwait(false);

                return await Get<CohortPageDto>(Query("api/guild/cohorts", cursor, size,
                    ("rootId", Id(root)), ("state", input.Text("state"))), ct).ConfigureAwait(false);
            }

            case "cohort show":
            case "cohort members":
            case "cohort contributions":
            case "cohort convergence":
            case "cohort close":
            case "cohort cancel":
            case "cohort admit":
            case "cohort remove":
            case "cohort member-work":
            {
                // Authored data is validated before selectors can perform any host request.
                string? goal = path == "cohort admit" ? await Text(input, "goal", ct).ConfigureAwait(false) : null;

                GuildRequestedGrant? grant = path == "cohort admit"
                    ? await Grant<GuildRequestedGrant>(input.Text("grant-file"), false, ct).ConfigureAwait(false) : null;

                Guid cohort = await Select("cohort", input.Argument(), ct).ConfigureAwait(false);

                string route = $"api/guild/cohorts/{cohort:D}";

                switch (path)
                {
                    case "cohort show": return await Get<CohortDto>(route, ct).ConfigureAwait(false);

                    case "cohort members": return await Get<CohortMemberPageDto>(Query(route + "/members", cursor, size), ct).ConfigureAwait(false);

                    case "cohort contributions": return await Get<CohortContributionPageDto>(Query(route + "/contributions", cursor, size,
                        ("selected", input.Flag("selected") ? "true" : null)), ct).ConfigureAwait(false);

                    case "cohort convergence": return await Get<CohortConvergenceDto>(route + "/convergence", ct).ConfigureAwait(false);

                    case "cohort close":
                    case "cohort cancel": return await Post<GuildRevisionRequest, CohortDto>(route + (path == "cohort close" ? "/close" : "/cancel"), new(key, revision), ct).ConfigureAwait(false);

                    case "cohort admit":
                    {
                        Guid? agent = await Optional("agent", input.Text("agent"), ct).ConfigureAwait(false);

                        return await Post<AdmitCohortMemberRequest, CohortMemberDto>(route + "/members",
                            new(key, revision, new(agent, input.Text("name"), goal!, grant)), ct).ConfigureAwait(false);
                    }

                    default:
                    {
                        Guid member = await Select("member", input.Argument(1), ct, cohort).ConfigureAwait(false);

                        string memberRoute = route + $"/members/{member:D}";

                        return path == "cohort remove"
                            ? await Post<GuildRevisionRequest, CohortMemberDto>(memberRoute + "/remove", new(key, revision), ct).ConfigureAwait(false)
                            : await Get<CohortMemberQuestPageDto>(Query(memberRoute + "/quests", cursor, size), ct).ConfigureAwait(false);
                    }
                }
            }

            case "offer post":
            {
                string goal = await Text(input, "goal", ct).ConfigureAwait(false);

                GuildRequestedGrant? grant = await Grant<GuildRequestedGrant>(input.Text("grant-file"), false, ct).ConfigureAwait(false);

                Guid cohort = await Select("cohort", input.Argument(), ct).ConfigureAwait(false);

                return await Post<PostWorkOfferRequest, WorkOfferDto>($"api/guild/cohorts/{cohort:D}/offers",
                    new(key, input.Number("expected-cohort-revision")!.Value, Required(input, "skill"), goal, grant,
                        new(Required(input, "result-type"))), ct).ConfigureAwait(false);
            }

            case "offer list":
            {
                Guid cohort = await Select("cohort", input.Argument(), ct).ConfigureAwait(false);

                return await Get<WorkOfferPageDto>(Query($"api/guild/cohorts/{cohort:D}/offers", cursor, size,
                    ("state", input.Text("state"))), ct).ConfigureAwait(false);
            }

            case "offer show":
            case "offer withdraw":
            case "offer award":
            case "bid list":
            {
                Guid offer = await Select("offer", input.Argument(), ct).ConfigureAwait(false);

                string route = $"api/guild/offers/{offer:D}";

                if (path == "offer show") return await Get<WorkOfferDto>(route, ct).ConfigureAwait(false);

                if (path == "offer withdraw") return await Post<GuildRevisionRequest, WorkOfferDto>(route + "/withdraw", new(key, revision), ct).ConfigureAwait(false);

                if (path == "bid list") return await Get<WorkBidPageDto>(Query(route + "/bids", cursor, size), ct).ConfigureAwait(false);

                Guid bid = await Select("bid", input.Text("bid"), ct, offer).ConfigureAwait(false);

                return await Post<AwardWorkRequest, WorkAwardDto>(route + "/award", new(key, revision,
                    input.Number("expected-cohort-revision")!.Value, bid), ct).ConfigureAwait(false);
            }

            case "note append":
            {
                string text = await Text(input, "text", ct).ConfigureAwait(false);

                GuildNoteKind kind = input.Text("kind") switch
                {
                    "observation" => GuildNoteKind.Observation,

                    "proposal" => GuildNoteKind.Proposal,

                    "correction" => GuildNoteKind.Correction,

                    _ => throw new GuildCliInputException("--kind must be observation, proposal or correction."),
                };

                Guid? subject = input.Text("subject") is { } rawSubject ? ExactGuid(rawSubject, "--subject") : null;

                Guid cohort = await Select("cohort", input.Argument(), ct).ConfigureAwait(false);

                Guid? corrects = input.Text("corrects") is { } note
                    ? await Select("note", note, ct, cohort).ConfigureAwait(false) : null;

                return await Post<AppendSharedNoteRequest, SharedNoteDto>($"api/guild/cohorts/{cohort:D}/notes",
                    new(key, revision, kind, subject, text, corrects), ct).ConfigureAwait(false);
            }

            case "note list":
            {
                Guid cohort = await Select("cohort", input.Argument(), ct).ConfigureAwait(false);

                return await Get<SharedNotePageDto>(Query($"api/guild/cohorts/{cohort:D}/notes", cursor, size), ct).ConfigureAwait(false);
            }

            case "lease acquire":
            {
                ImmutableArray<GuildPathScope>.Builder scopes = ImmutableArray.CreateBuilder<GuildPathScope>();

                foreach (string scope in input.Many("scope"))
                {
                    int separator = scope.LastIndexOf(':');

                    if (separator <= 0 || separator == scope.Length - 1) throw new GuildCliInputException("Each --scope must be relative-path:file or relative-path:subtree.");

                    GuildPathScopeKind kind = scope[(separator + 1)..] switch
                    {
                        "file" => GuildPathScopeKind.File,

                        "subtree" => GuildPathScopeKind.Subtree,

                        _ => throw new GuildCliInputException("Each --scope kind must be file or subtree."),
                    };

                    string relative = scope[..separator];

                    if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(static segment => segment == ".."))
                        throw new GuildCliInputException("A lease scope must be a contained relative path.");

                    scopes.Add(new(relative, kind));
                }

                if (scopes.Count == 0) throw new GuildCliInputException("At least one --scope is required.");

                Guid workspace = await ExistingResource("workspace", Required(input, "workspace"), ct).ConfigureAwait(false)
                    ?? throw new GuildCliInputException("A registered workspace is required.");

                return await Post<AcquireWorkspaceLeaseRequest, WorkspaceLeaseDto>("api/guild/workspace-leases", new(key, workspace, scopes.ToImmutable()), ct).ConfigureAwait(false);
            }

            case "lease list":
            {
                Guid? workspace = await ExistingResource("workspace", input.Text("workspace"), ct).ConfigureAwait(false);

                Guid? quest = await Optional("task", input.Text("quest"), ct).ConfigureAwait(false);

                return await Get<WorkspaceLeasePageDto>(Query("api/guild/workspace-leases", cursor, size,
                    ("workspaceId", Id(workspace)), ("questId", Id(quest)), ("state", input.Text("state"))), ct).ConfigureAwait(false);
            }

            case "lease renew":
            case "lease release":
            {
                Guid lease = await Select("lease", input.Argument(), ct).ConfigureAwait(false);

                long epoch = input.Number("epoch")!.Value;

                return path == "lease renew"
                    ? await Post<RenewWorkspaceLeaseRequest, WorkspaceLeaseDto>($"api/guild/workspace-leases/{lease:D}/renew", new(key, revision, epoch), ct).ConfigureAwait(false)
                    : await Post<ReleaseWorkspaceLeaseRequest, WorkspaceLeaseDto>($"api/guild/workspace-leases/{lease:D}/release", new(key, revision, epoch), ct).ConfigureAwait(false);
            }

            default: throw new GuildCliInputException("Unknown Guild operation.");
        }
    }

    private static string Required(GuildCommandInput input, string name) => !string.IsNullOrWhiteSpace(input.Text(name))
        ? input.Text(name)! : throw new GuildCliInputException("--" + name + " is required.");
}
