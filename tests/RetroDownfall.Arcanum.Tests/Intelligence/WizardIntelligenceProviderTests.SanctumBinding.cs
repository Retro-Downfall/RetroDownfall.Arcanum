using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Which Campaign's Sanctum governs a turn. The turn is bound to one canonical Campaign (its Session, an explicit identifier, or the
/// registered root that contains its working directory), and the Sanctum policy, its mode and the disabled tools all come from that
/// Campaign, never from whether the working directory string happens to equal the registered path.
/// </summary>
public sealed partial class WizardIntelligenceProviderTests
{
    private const string SanctumBlockedMessage = "Sanctum Guard has blocked";

    /// <summary>A turn the Sanctum refuses every tool of, when it is in force.</summary>
    private sealed record SanctumProbe(bool Succeeded, IReadOnlyList<string> ToolMessages, int ToolChecks)
    {
        public bool Blocked => ToolMessages.Any(static message => message.Contains(SanctumBlockedMessage, StringComparison.Ordinal));

        public bool ToolRan => ToolMessages.Any(static message => string.Equals(message, "ok", StringComparison.Ordinal));
    }

    private async Task<SanctumProbe> ProbeSanctumAsync(
        ICampaignRepository repository,
        string? workingDirectory,
        ArcanumInvocationContext invocation,
        ICanonicalCampaignContextResolver? resolver = null,
        Guid? requestCampaignId = null,
        Guid? sessionId = null)
    {
        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            "read_file_chunk",
            "sanctum-probe",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "notes.txt",
                ["startLine"] = 1,
                ["endLine"] = 1,
            });

        chat.EnqueueText("done");

        int toolChecks = 0;

        ConfigurableSanctumGuard sanctum = new()
        {
            ToolValidator = (_, _, _) =>
            {
                _ = Interlocked.Increment(ref toolChecks);

                return Task.FromResult(
                    new SanctumResult
                    {
                        Allowed = false,
                        DenyReason = "the Sanctum disables this tool",
                        Breach = new SanctumBreach { BreachType = "DisabledTool" },
                    });
            },
        };

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(CreateMcpTool("read_file_chunk"));

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            campaignRepository: repository,
            sanctumGuard: sanctum,
            mcp: mcp,
            campaignResolver: resolver);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "read",
                WorkingDirectory = workingDirectory ?? string.Empty,
                SkipSpellRouting = true,
                CampaignId = requestCampaignId,
                SessionId = sessionId,
            },
            invocation,
            CancellationToken.None);

        string[] toolMessages =
        [
            .. chat.AllBufferedCalls
                .SelectMany(static batch => batch)
                .Where(static message => message.Role == ChatRole.Tool)
                .Select(GetMessageText),
        ];

        return new SanctumProbe(result.IsSuccess, toolMessages, toolChecks);
    }

    private static Campaign StrictSanctumCampaign(string root, bool enabled = true, SanctumMode mode = SanctumMode.Strict) =>
        BuildSanctumCampaign(root, enabled, mode);

    // A working directory that is not the registered path spelled exactly still belongs to the Campaign the turn is bound to.
    [Theory]
    [InlineData("")]
    [InlineData("src")]
    [InlineData("src/deep/er")]
    public async Task A_turn_bound_to_a_strict_campaign_is_governed_wherever_below_the_root_it_runs(string below)
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        string workingDirectory = below.Length == 0
            ? _workspace.Root
            : _workspace.CreateSubdir(Path.Combine(below.Split('/')));

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            workingDirectory,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.Equal(1, probe.ToolChecks);
    }

    [Fact]
    public async Task A_turn_bound_to_a_strict_campaign_is_governed_when_the_root_is_spelled_with_a_trailing_separator()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.Root + Path.DirectorySeparatorChar,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Blocked);
    }

    [Fact]
    public async Task A_session_bound_turn_with_no_working_directory_is_governed_by_its_campaign()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            workingDirectory: null,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.Equal(1, probe.ToolChecks);
    }

    [Fact]
    public async Task A_turn_bound_by_an_explicit_campaign_identifier_is_governed_by_that_campaign()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            workingDirectory: null,
            InvocationContexts.AttendedSession(campaign.Id),
            requestCampaignId: campaign.Id);

        Assert.True(probe.Blocked);
    }

    [Fact]
    public async Task A_case_variant_of_the_working_directory_is_governed_by_the_bound_campaign()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.Root.ToUpperInvariant(),
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Blocked);
    }

    [SkippableFact]
    public async Task A_symlink_variant_of_the_working_directory_is_governed_by_the_bound_campaign()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        string link = Path.Combine(Path.GetTempPath(), "arcanum-sanctum-link-" + Guid.NewGuid().ToString("N"));

        Directory.CreateSymbolicLink(link, _workspace.Root);

        try
        {
            SanctumProbe probe = await ProbeSanctumAsync(
                new FakeCampaignRepository(campaign),
                Path.Combine(link, "src"),
                InvocationContexts.AttendedSession(campaign.Id));

            Assert.True(probe.Blocked);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_campaign_whose_sanctum_is_off_leaves_the_turn_unrestricted_wherever_it_runs()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root, enabled: false);

        string below = _workspace.CreateSubdir("src");

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            below,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.ToolRan);

        Assert.False(probe.Blocked);

        Assert.Equal(0, probe.ToolChecks);
    }

    [Fact]
    public async Task A_directory_no_registered_campaign_contains_leaves_the_turn_unrestricted()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        string unregistered = Path.Combine(Path.GetTempPath(), "arcanum-unregistered-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(unregistered);

        try
        {
            SanctumProbe probe = await ProbeSanctumAsync(
                new FakeCampaignRepository(campaign),
                unregistered,
                InvocationContexts.AttendedGlobalOnlySession());

            Assert.True(probe.ToolRan);

            Assert.False(probe.Blocked);

            Assert.Equal(0, probe.ToolChecks);
        }
        finally
        {
            Directory.Delete(unregistered, recursive: true);
        }
    }

    [Fact]
    public async Task A_sibling_sharing_a_name_prefix_with_the_registered_root_is_not_governed()
    {
        string app = _workspace.CreateSubdir("app");

        string legacy = _workspace.CreateSubdir("app-legacy");

        Campaign campaign = StrictSanctumCampaign(app);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            legacy,
            InvocationContexts.AttendedGlobalOnlySession());

        Assert.True(probe.ToolRan);

        Assert.False(probe.Blocked);
    }

    // The canonical resolution can be empty for a directory that is nevertheless inside a registered root (a root whose identity is not
    // recorded). The registered root that contains the directory still governs it, whether or not the path is spelled exactly.
    [Theory]
    [InlineData("")]
    [InlineData("src")]
    [InlineData("trailing")]
    public async Task A_global_only_turn_inside_a_registered_root_is_governed_by_that_root(string variant)
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        string workingDirectory = variant switch
        {
            "src" => _workspace.CreateSubdir("src"),
            "trailing" => _workspace.Root + Path.DirectorySeparatorChar,
            _ => _workspace.Root,
        };

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            workingDirectory,
            InvocationContexts.AttendedGlobalOnlySession());

        Assert.True(probe.Blocked);

        Assert.Equal(1, probe.ToolChecks);
    }

    // Internal callers carry no canonical context at all. They are bound the same way, through the resolver, so a subagent or an
    // Apprentice that runs inside a contained Campaign is as contained as the operator's own turn.
    [Fact]
    public async Task A_turn_with_no_canonical_context_is_bound_through_the_resolver()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        FixedCanonicalResolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalFor(campaign.Id)));

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.CreateSubdir("src"),
            ArcanumInvocationContext.None,
            resolver);

        Assert.True(probe.Blocked);

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task A_turn_with_no_canonical_context_whose_resolution_fails_falls_back_to_the_root_that_contains_the_directory()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        FixedCanonicalResolver resolver = new(
            Result<CanonicalCampaignContext>.Failure(new Error(ErrorCodes.Covenant.CampaignBindingConflict, "conflict")));

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.CreateSubdir("src"),
            ArcanumInvocationContext.None,
            resolver);

        Assert.True(probe.Blocked);
    }

    [Fact]
    public async Task A_turn_with_no_canonical_context_whose_resolver_refuses_still_honours_an_explicit_campaign_identifier()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            workingDirectory: null,
            ArcanumInvocationContext.None,
            resolver: null,
            requestCampaignId: campaign.Id);

        Assert.True(probe.Blocked);
    }

    [Fact]
    public async Task A_turn_with_nothing_to_bind_does_not_consult_the_resolver()
    {
        FixedCanonicalResolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(),
            workingDirectory: null,
            ArcanumInvocationContext.None,
            resolver);

        Assert.True(probe.ToolRan);

        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task The_mode_comes_from_the_bound_campaign()
    {
        Campaign audit = StrictSanctumCampaign(_workspace.Root, mode: SanctumMode.AuditOnly);

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(audit),
            _workspace.CreateSubdir("src"),
            InvocationContexts.AttendedSession(audit.Id));

        // AuditOnly records the disabled tool and lets the call run.
        Assert.Equal(1, probe.ToolChecks);

        Assert.True(probe.ToolRan);

        Assert.False(probe.Blocked);
    }

    // Internal callers (an Apprentice step, a subagent) carry a Session and a working directory and no canonical context. In production no
    // Campaign root has a registered identity, so the canonical seam refuses a Session bound to a Campaign that also names a working
    // directory the Campaign does not contain. A refusal must not govern less strictly than the sources it refused.
    [Theory]
    [InlineData("outside")]
    [InlineData("inside-a-campaign-with-the-sanctum-off")]
    public async Task A_turn_with_no_canonical_context_for_a_session_bound_to_a_strict_campaign_is_governed_by_it_wherever_it_runs(string where)
    {
        Campaign strict = StrictSanctumCampaign(_workspace.Root);

        string openRoot = _workspace.CreateSubdir("open-campaign");

        Campaign open = StrictSanctumCampaign(openRoot, enabled: false);

        open.Id = Guid.NewGuid();

        string workingDirectory = where == "outside"
            ? Path.GetTempPath()
            : Directory.CreateDirectory(Path.Combine(openRoot, "src")).FullName;

        Guid session = Guid.NewGuid();

        SanctumProbe probe = await ProbeSanctumAsync(
            new MultiCampaignRepository(strict, open),
            workingDirectory,
            ArcanumInvocationContext.None,
            ProductionCampaignResolver.Over(new StubSessionBindings().Bind(session, strict.Id), new AvailableCampaigns(strict.Id, open.Id)),
            sessionId: session);

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.False(probe.ToolRan);

        Assert.Equal(1, probe.ToolChecks);
    }

    [Fact]
    public async Task A_turn_with_no_canonical_context_naming_a_strict_campaign_is_governed_by_it_when_it_runs_in_a_campaign_with_the_sanctum_off()
    {
        Campaign strict = StrictSanctumCampaign(_workspace.Root);

        string openRoot = _workspace.CreateSubdir("open-campaign");

        Campaign open = StrictSanctumCampaign(openRoot, enabled: false);

        open.Id = Guid.NewGuid();

        SanctumProbe probe = await ProbeSanctumAsync(
            new MultiCampaignRepository(strict, open),
            Directory.CreateDirectory(Path.Combine(openRoot, "src")).FullName,
            ArcanumInvocationContext.None,
            ProductionCampaignResolver.Over(new StubSessionBindings(), new AvailableCampaigns(strict.Id, open.Id)),
            requestCampaignId: strict.Id);

        Assert.True(probe.Blocked);

        Assert.False(probe.ToolRan);
    }

    [Fact]
    public async Task A_turn_with_no_canonical_context_for_a_session_bound_to_a_campaign_with_the_sanctum_off_is_governed_by_the_strict_root_it_runs_in()
    {
        Campaign strict = StrictSanctumCampaign(_workspace.Root);

        string openRoot = _workspace.CreateSubdir("open-campaign");

        Campaign open = StrictSanctumCampaign(openRoot, enabled: false);

        open.Id = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        SanctumProbe probe = await ProbeSanctumAsync(
            new MultiCampaignRepository(strict, open),
            _workspace.CreateSubdir("src"),
            ArcanumInvocationContext.None,
            ProductionCampaignResolver.Over(new StubSessionBindings().Bind(session, open.Id), new AvailableCampaigns(strict.Id, open.Id)),
            sessionId: session);

        Assert.True(probe.Blocked);
    }

    [Fact]
    public async Task A_turn_with_no_canonical_context_for_a_session_bound_to_a_campaign_with_the_sanctum_off_stays_unrestricted_outside_every_root()
    {
        Campaign strict = StrictSanctumCampaign(_workspace.Root);

        string openRoot = _workspace.CreateSubdir("open-campaign");

        Campaign open = StrictSanctumCampaign(openRoot, enabled: false);

        open.Id = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        SanctumProbe probe = await ProbeSanctumAsync(
            new MultiCampaignRepository(strict, open),
            Path.GetTempPath(),
            ArcanumInvocationContext.None,
            ProductionCampaignResolver.Over(new StubSessionBindings().Bind(session, open.Id), new AvailableCampaigns(strict.Id, open.Id)),
            sessionId: session);

        Assert.True(probe.ToolRan);

        Assert.False(probe.Blocked);

        Assert.Equal(0, probe.ToolChecks);
    }

    [Fact]
    public async Task A_turn_with_no_canonical_context_for_a_session_bound_to_a_campaign_whose_sanctum_cannot_be_read_refuses_every_tool_call()
    {
        Campaign unreadable = StrictSanctumCampaign(_workspace.Root);

        unreadable.SanctumConfigJson = "{ this is not json";

        Guid session = Guid.NewGuid();

        SanctumProbe probe = await ProbeSanctumAsync(
            new MultiCampaignRepository(unreadable),
            Path.GetTempPath(),
            ArcanumInvocationContext.None,
            ProductionCampaignResolver.Over(new StubSessionBindings().Bind(session, unreadable.Id), new AvailableCampaigns(unreadable.Id)),
            sessionId: session);

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.False(probe.ToolRan);
    }

    // A Campaign nested inside another, found by path only, is claimed alongside the canonical Campaign the outer registration binds; the
    // stricter Sanctum governs.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_nested_campaign_found_by_path_and_the_canonical_campaign_are_governed_by_the_stricter_of_the_two(
        bool canonicalIsStrict,
        bool nestedIsStrict)
    {
        Campaign outer = StrictSanctumCampaign(_workspace.Root, enabled: canonicalIsStrict);

        string nestedRoot = _workspace.CreateSubdir("nested");

        Campaign nested = StrictSanctumCampaign(nestedRoot, enabled: nestedIsStrict);

        nested.Id = Guid.NewGuid();

        SanctumProbe probe = await ProbeSanctumAsync(
            new MultiCampaignRepository(outer, nested),
            Directory.CreateDirectory(Path.Combine(nestedRoot, "src")).FullName,
            InvocationContexts.AttendedSession(outer.Id));

        Assert.True(probe.Blocked);

        Assert.False(probe.ToolRan);
    }

    // The bound Campaign has a Sanctum that cannot be read: its row is gone, the read fails, or the stored configuration does not parse.
    // Whether it was enabled is unknowable, so every tool call is refused; the turn itself, which needs no tool, still answers.
    [Fact]
    public async Task A_bound_campaign_whose_row_is_missing_refuses_every_tool_call()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new MissingCampaignRepository(),
            _workspace.Root,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.Contains(probe.ToolMessages, static message => message.Contains("could not be read", StringComparison.Ordinal));

        Assert.False(probe.ToolRan);

        Assert.Equal(0, probe.ToolChecks);
    }

    [Fact]
    public async Task A_bound_campaign_whose_stored_sanctum_does_not_parse_refuses_every_tool_call()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        campaign.SanctumConfigJson = "{ this is not json";

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.Root,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.False(probe.ToolRan);
    }

    [Fact]
    public async Task A_bound_campaign_whose_read_throws_refuses_every_tool_call_and_the_turn_still_answers()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.Root);

        SanctumProbe probe = await ProbeSanctumAsync(
            new MissingCampaignRepository(new IOException("simulated read failure")),
            _workspace.Root,
            InvocationContexts.AttendedSession(campaign.Id));

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.False(probe.ToolRan);
    }

    // Real directories, the real resolver and the real physical-identity reader: the canonical answer, not the string, decides.
    [SkippableTheory]
    [InlineData("subdirectory")]
    [InlineData("trailing-separator")]
    [InlineData("case-variant")]
    [InlineData("symlinked-ancestor")]
    public async Task A_registered_root_governs_the_working_directory_variants_the_real_resolver_recognises(string variant)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.IfNot(OperatingSystem.IsMacOS(), "The case-insensitive volume and symlinked ancestors this exercises are the macOS contract.");

        string parent = Directory.CreateTempSubdirectory("arcanum-sanctum-binding-").FullName;

        string link = Path.Combine(Path.GetTempPath(), "arcanum-sanctum-link-" + Guid.NewGuid().ToString("N"));

        try
        {
            string root = Directory.CreateDirectory(Path.Combine(parent, "Project")).FullName;

            string src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

            string workingDirectory = variant switch
            {
                "subdirectory" => src,
                "trailing-separator" => root + Path.DirectorySeparatorChar,
                "case-variant" => Path.Combine(parent, "PROJECT"),
                _ => LinkAncestor(),
            };

            string LinkAncestor()
            {
                Directory.CreateSymbolicLink(link, parent);

                return Path.Combine(link, "Project", "src");
            }

            Skip.IfNot(Directory.Exists(workingDirectory), "The volume does not treat the variant spelling as the same directory.");

            Guid campaignId = Guid.NewGuid();

            await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
                CancellationToken.None,
                coreObjects: ["campaign_path_identities", "campaign_registry_state_campaign_delete"]);

            await fixture.AddCampaignAsync(campaignId, "sanctum-bound", CancellationToken.None);

            PhysicalCampaignRootOpener opener = new(new FixedKey());

            await RegisterRootAsync(fixture, opener, campaignId, root);

            FixedCovenantConnectionSource connections = new(fixture.Connection);

            CanonicalCampaignContextResolver resolver = new(
                new SessionCampaignBindingReader(connections),
                new CampaignPathIdentityReader(connections, opener),
                new CampaignAvailabilityReader(connections));

            Result<CanonicalCampaignContext> canonical = await resolver.ResolveAsync(
                new CanonicalCampaignResolutionRequest(null, null, workingDirectory),
                CancellationToken.None);

            Assert.True(canonical.IsSuccess, canonical.IsFailure ? canonical.Error.Message : null);

            Assert.Equal(campaignId, canonical.Value.CampaignId);

            Campaign campaign = StrictSanctumCampaign(root);

            campaign.Id = campaignId;

            SanctumProbe probe = await ProbeSanctumAsync(
                new FakeCampaignRepository(campaign),
                workingDirectory,
                InvocationContexts.AttendedSession(canonical.Value));

            Assert.True(probe.Blocked);

            Assert.Equal(1, probe.ToolChecks);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            _ = TestDirectoryCleanup.TryDelete(parent, nameof(WizardIntelligenceProviderTests));
        }
    }

    private static CanonicalCampaignContext CanonicalFor(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    private static async Task RegisterRootAsync(
        CovenantCanonicalFixture fixture,
        PhysicalCampaignRootOpener opener,
        Guid campaignId,
        string directory)
    {
        CovenantDigest identity = opener.IdentifyExact(directory)
            ?? throw new InvalidOperationException("The test directory has no physical identity.");

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO campaign_path_identities
                (CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
            VALUES ($campaignId, $policyVersion, 1, $displayPath, $depth, $digest, $updated);
            """;

        _ = command.Parameters.AddWithValue("$campaignId", campaignId);

        _ = command.Parameters.AddWithValue("$policyVersion", (long)CampaignPathIdentityPolicy.Version);

        _ = command.Parameters.AddWithValue("$displayPath", directory);

        _ = command.Parameters.AddWithValue("$depth", Math.Max(1, directory.Split(Path.DirectorySeparatorChar).Length));

        _ = command.Parameters.AddWithValue("$digest", identity.Bytes);

        _ = command.Parameters.AddWithValue("$updated", "2026-08-15T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class FixedKey : ICampaignRootIdentityKeyProvider
    {
        private static readonly byte[] Key = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {
            Key.CopyTo(destination);

            return true;
        }
    }

    private sealed class FixedCanonicalResolver(Result<CanonicalCampaignContext> answer) : ICanonicalCampaignContextResolver
    {
        public int Calls { get; private set; }

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(answer);
        }
    }

    /// <summary>A store whose row for the bound Campaign is gone, or whose read fails.</summary>
    private sealed class MissingCampaignRepository(Exception? failure = null) : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult<Campaign?>(null) : Task.FromException<Campaign?>(failure);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        // This store has no row to contain a directory, and the exact match finds none either.
        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default) =>
            GetByPathAsync(path, cancellationToken);

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateHeaderAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSettingsAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSanctumAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>A Campaign table with several Campaigns, found by identifier and by the deepest registered root that contains a directory.</summary>
    private sealed class MultiCampaignRepository(params Campaign[] campaigns) : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaigns.FirstOrDefault(campaign => campaign.Id == id));

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
        {
            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

            return Task.FromResult(
                campaigns
                    .Where(campaign =>
                    {
                        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(campaign.Path.Trim()));

                        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(static campaign => campaign.Path.Length)
                    .FirstOrDefault());
        }

        public Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default) =>
            GetByContainedPathAsync(path, cancellationToken);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateHeaderAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSettingsAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateSanctumAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(campaigns.Length);
    }
}
