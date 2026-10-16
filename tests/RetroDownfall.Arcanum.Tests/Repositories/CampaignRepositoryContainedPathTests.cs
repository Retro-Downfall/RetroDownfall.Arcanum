using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// <see cref="ICampaignRepository.GetByContainedPathAsync"/> against the real Campaign table: a directory is recognised as a registered
/// Campaign's workspace when the Campaign's root is the directory or one of its ancestors, matched by whole components.
/// </summary>
[Collection("Grimoire")]
public sealed class CampaignRepositoryContainedPathTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    public CampaignRepositoryContainedPathTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateTempSubdirectory("arcanum-contained-path-").FullName;

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(CampaignRepositoryContainedPathTests));
    }

    [SkippableFact]
    public async Task The_root_itself_and_every_directory_below_it_resolve_to_the_campaign()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "alpha")).FullName;

        string deep = Directory.CreateDirectory(Path.Combine(root, "src", "deep")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign alpha = await AddAsync(repository, "alpha", root);

        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(root))?.Id);

        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "src")))?.Id);

        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(deep))?.Id);

        // A directory that does not exist yet is still below the root by its spelling.
        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "not", "created", "yet")))?.Id);
    }

    [SkippableFact]
    public async Task A_trailing_separator_on_either_side_is_ignored()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "trailing")).FullName;

        ICampaignRepository repository = CreateRepository();

        // The registration keeps the spelling it was given, trailing separator included.
        Campaign registered = await AddAsync(repository, "trailing", root + Path.DirectorySeparatorChar);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(root))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(root + Path.DirectorySeparatorChar))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "src") + Path.DirectorySeparatorChar))?.Id);
    }

    [SkippableFact]
    public async Task A_sibling_sharing_a_name_prefix_is_not_contained()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string app = Directory.CreateDirectory(Path.Combine(_parent, "app")).FullName;

        string legacy = Directory.CreateDirectory(Path.Combine(_parent, "app-legacy")).FullName;

        ICampaignRepository repository = CreateRepository();

        _ = await AddAsync(repository, "app", app);

        Assert.Null(await repository.GetByContainedPathAsync(legacy));

        Assert.Null(await repository.GetByContainedPathAsync(Path.Combine(legacy, "src")));

        Assert.Null(await repository.GetByContainedPathAsync(_parent));
    }

    [SkippableFact]
    public async Task The_deepest_registered_root_wins()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string outer = Directory.CreateDirectory(Path.Combine(_parent, "outer")).FullName;

        string inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign outerCampaign = await AddAsync(repository, "outer", outer);

        Campaign innerCampaign = await AddAsync(repository, "inner", inner);

        Assert.Equal(innerCampaign.Id, (await repository.GetByContainedPathAsync(Path.Combine(inner, "src")))?.Id);

        Assert.Equal(outerCampaign.Id, (await repository.GetByContainedPathAsync(Path.Combine(outer, "docs")))?.Id);
    }

    [SkippableFact]
    public async Task An_unregistered_or_unusable_path_resolves_to_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ICampaignRepository repository = CreateRepository();

        _ = await AddAsync(repository, "only", Directory.CreateDirectory(Path.Combine(_parent, "only")).FullName);

        Assert.Null(await repository.GetByContainedPathAsync(Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName));

        Assert.Null(await repository.GetByContainedPathAsync("bad\u0000path"));
    }

    [SkippableFact]
    public async Task A_symlinked_ancestor_of_the_working_directory_resolves_to_the_campaign_it_points_into()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        // The registered root is spelled canonically (its own links resolved), the way a path the operating system reports is.
        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string root = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "proj")).FullName;

        string src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

        string link = Path.Combine(_parent, "link");

        Directory.CreateSymbolicLink(link, Path.Combine(_parent, "real"));

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "proj", root);

        string throughTheLink = Path.Combine(link, "proj", "src");

        Assert.True(Directory.Exists(throughTheLink));

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(throughTheLink))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(src))?.Id);
    }

    // GetBySpelledPathAsync answers for the way a path is written, not for where its links lead. A link inside a root is a name inside that root
    // whatever it points at, which GetByContainedPathAsync (the nearest root over every form of the path) cannot say once the link points into
    // a root that is nearer to its target than the first root is to the link.
    [SkippableFact]
    public async Task A_link_inside_a_root_is_spelled_inside_it_even_when_it_points_into_a_nearer_root()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string first = Directory.CreateDirectory(Path.Combine(canonicalParent, "first")).FullName;

        string second = Directory.CreateDirectory(Path.Combine(canonicalParent, "second")).FullName;

        string target = Path.Combine(second, "target.txt");

        await File.WriteAllTextAsync(target, "in the second root");

        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(first, "sub")).FullName, "deep.txt");

        File.CreateSymbolicLink(link, target);

        ICampaignRepository repository = CreateRepository();

        Campaign firstCampaign = await AddAsync(repository, "first", first);

        Campaign secondCampaign = await AddAsync(repository, "second", second);

        // The nearest root over every form of the path is the one the link points into, one component above its target, not two above the link.
        Assert.Equal(secondCampaign.Id, (await repository.GetByContainedPathAsync(link))?.Id);

        Assert.Equal(firstCampaign.Id, (await repository.GetBySpelledPathAsync(link))?.Id);

        Assert.Equal(secondCampaign.Id, (await repository.GetBySpelledPathAsync(target))?.Id);
    }

    [SkippableFact]
    public async Task A_directory_link_inside_a_root_is_spelled_inside_it_even_when_it_points_into_another_root()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string first = Directory.CreateDirectory(Path.Combine(canonicalParent, "first")).FullName;

        string second = Directory.CreateDirectory(Path.Combine(canonicalParent, "second")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(first, "dirlink"), second);

        ICampaignRepository repository = CreateRepository();

        Campaign firstCampaign = await AddAsync(repository, "first", first);

        Campaign secondCampaign = await AddAsync(repository, "second", second);

        string through = Path.Combine(first, "dirlink", "target.txt");

        Assert.Equal(secondCampaign.Id, (await repository.GetByContainedPathAsync(through))?.Id);

        Assert.Equal(firstCampaign.Id, (await repository.GetBySpelledPathAsync(through))?.Id);
    }

    [SkippableFact]
    public async Task A_path_spelled_through_a_link_to_a_root_is_not_spelled_inside_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string root = Directory.CreateDirectory(Path.Combine(canonicalParent, "root")).FullName;

        string alias = Path.Combine(canonicalParent, "alias");

        Directory.CreateSymbolicLink(alias, root);

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "root", root);

        // The spelling names the alias, which no root's text contains; the directory it resolves to is what GetByContainedPathAsync finds.
        Assert.Null(await repository.GetBySpelledPathAsync(Path.Combine(alias, "file.txt")));

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(alias))?.Id);
    }

    [SkippableFact]
    public async Task The_spelled_path_is_matched_by_whole_components_below_the_deepest_root_and_a_root_registered_through_a_link_is_found_by_its_physical_text()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string outer = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "outer")).FullName;

        string inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;

        string legacy = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "outer-legacy")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(_parent, "link"), Path.Combine(canonicalParent, "real"));

        ICampaignRepository repository = CreateRepository();

        // The outer root is registered through a link and the path is written with the physical spelling.
        Campaign outerCampaign = await AddAsync(repository, "outer", Path.Combine(_parent, "link", "outer"));

        Campaign innerCampaign = await AddAsync(repository, "inner", inner);

        Assert.Equal(outerCampaign.Id, (await repository.GetBySpelledPathAsync(Path.Combine(outer, "docs", "a.txt")))?.Id);

        Assert.Equal(innerCampaign.Id, (await repository.GetBySpelledPathAsync(Path.Combine(inner, "src", "a.txt")))?.Id);

        Assert.Null(await repository.GetBySpelledPathAsync(Path.Combine(legacy, "a.txt")));

        Assert.Null(await repository.GetBySpelledPathAsync("bad\u0000path"));
    }

    [SkippableFact]
    public async Task The_case_of_the_working_directory_does_not_hide_the_campaign_on_a_case_insensitive_platform()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Only the case-insensitive platforms treat a case variant as the same directory.");

        string root = Directory.CreateDirectory(Path.Combine(_parent, "CasedRoot")).FullName;

        Skip.IfNot(Directory.Exists(Path.Combine(_parent, "casedroot")), "This volume is case sensitive.");

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "cased", root);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(_parent, "CASEDROOT", "src")))?.Id);
    }

    // The operating system reports a working directory by its physical path (getcwd, Environment.CurrentDirectory), whatever spelling the
    // root was registered under. A root registered through a symbolic link, or through the /var -> /private/var link a macOS temporary
    // directory sits behind, is still the same directory.
    [SkippableFact]
    public async Task A_root_registered_through_a_symbolic_link_is_found_from_its_physical_working_directory()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string physicalRoot = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "proj")).FullName;

        string physicalSrc = Directory.CreateDirectory(Path.Combine(physicalRoot, "src", "deep")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(_parent, "link"), Path.Combine(canonicalParent, "real"));

        string registeredSpelling = Path.Combine(_parent, "link", "proj");

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "proj", registeredSpelling);

        Assert.Equal(registeredSpelling, registered.Path);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(physicalRoot))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(physicalRoot, "src")))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(physicalSrc))?.Id);

        // A sibling of the physical root is still not contained.
        string sibling = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "proj-legacy")).FullName;

        Assert.Null(await repository.GetByContainedPathAsync(sibling));
    }

    [SkippableFact]
    public async Task A_root_that_is_itself_a_symbolic_link_is_found_from_the_directory_it_points_to()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string target = Directory.CreateDirectory(Path.Combine(canonicalParent, "target", "proj")).FullName;

        string below = Directory.CreateDirectory(Path.Combine(target, "src")).FullName;

        string link = Path.Combine(_parent, "projlink");

        Directory.CreateSymbolicLink(link, target);

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "projlink", link);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(target))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(below))?.Id);
    }

    // APFS keeps a name as it was created and treats its canonically equivalent Unicode spellings as one directory.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_canonically_equivalent_unicode_spelling_of_the_root_is_the_same_campaign(bool registerComposed)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string composed = "café";

        string decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);

        Assert.NotEqual(composed, decomposed);

        string root = Directory.CreateDirectory(Path.Combine(_parent, composed)).FullName;

        Skip.IfNot(Directory.Exists(Path.Combine(_parent, decomposed)), "This volume tells the two spellings apart.");

        _ = Directory.CreateDirectory(Path.Combine(root, "src"));

        string registeredSpelling = registerComposed ? root : Path.Combine(_parent, decomposed);

        string otherSpelling = registerComposed ? Path.Combine(_parent, decomposed, "src") : Path.Combine(_parent, composed, "src");

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "cafe", registeredSpelling);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(otherSpelling))?.Id);
    }

    [SkippableFact]
    public async Task A_case_variant_that_is_not_ascii_is_the_same_directory_on_a_case_insensitive_platform()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Only the case-insensitive platforms treat a case variant as the same directory.");

        string root = Directory.CreateDirectory(Path.Combine(_parent, "Été")).FullName;

        Skip.IfNot(Directory.Exists(Path.Combine(_parent, "été")), "This volume is case sensitive.");

        _ = Directory.CreateDirectory(Path.Combine(root, "src"));

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "ete", root);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(_parent, "été", "src")))?.Id);
    }

    // Registrations that predate the duplicate check can name one directory twice. Whichever row is read first, the stricter Sanctum
    // governs the directory, and an exact-spelling lookup is no longer sent to a row with the Sanctum off.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_registrations_of_one_directory_are_governed_by_the_stricter_Sanctum_whichever_came_first(bool strictFirst)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "dup")).FullName;

        ICampaignRepository repository = CreateRepository();

        Guid open;

        Guid strict;

        if (strictFirst)
        {
            strict = await InsertWithoutDuplicateCheckAsync("dup-strict", root, SanctumMode.Strict, enabled: true);

            open = await InsertWithoutDuplicateCheckAsync("dup-open", root + Path.DirectorySeparatorChar, SanctumMode.Strict, enabled: false);
        }
        else
        {
            open = await InsertWithoutDuplicateCheckAsync("dup-open", root + Path.DirectorySeparatorChar, SanctumMode.Strict, enabled: false);

            strict = await InsertWithoutDuplicateCheckAsync("dup-strict", root, SanctumMode.Strict, enabled: true);
        }

        Assert.NotEqual(open, strict);

        Assert.Equal(strict, (await repository.GetByContainedPathAsync(root))?.Id);

        Assert.Equal(strict, (await repository.GetByContainedPathAsync(Path.Combine(root, "src")))?.Id);

        Assert.Equal(strict, (await repository.GetByContainedPathAsync(root + Path.DirectorySeparatorChar))?.Id);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_case_variant_registrations_of_one_directory_are_governed_by_the_stricter_Sanctum(bool strictFirst)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Only the case-insensitive platforms treat a case variant as the same directory.");

        string upper = Directory.CreateDirectory(Path.Combine(_parent, "Proj")).FullName;

        string lower = Path.Combine(_parent, "proj");

        Skip.IfNot(Directory.Exists(lower), "This volume is case sensitive.");

        ICampaignRepository repository = CreateRepository();

        Guid open;

        Guid strict;

        if (strictFirst)
        {
            strict = await InsertWithoutDuplicateCheckAsync("proj-strict", lower, SanctumMode.Strict, enabled: true);

            open = await InsertWithoutDuplicateCheckAsync("proj-open", upper, SanctumMode.Strict, enabled: false);
        }
        else
        {
            open = await InsertWithoutDuplicateCheckAsync("proj-open", upper, SanctumMode.Strict, enabled: false);

            strict = await InsertWithoutDuplicateCheckAsync("proj-strict", lower, SanctumMode.Strict, enabled: true);
        }

        Assert.Equal(strict, (await repository.GetByContainedPathAsync(upper))?.Id);

        Assert.Equal(strict, (await repository.GetByContainedPathAsync(Path.Combine(lower, "src")))?.Id);

        Assert.Equal(strict, (await repository.GetByContainedPathAsync(Path.Combine(upper, "src")))?.Id);
    }

    [SkippableFact]
    public async Task An_audit_only_registration_governs_over_a_disabled_one_for_the_same_directory()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "modes")).FullName;

        ICampaignRepository repository = CreateRepository();

        Guid off = await InsertWithoutDuplicateCheckAsync("modes-off", root + Path.DirectorySeparatorChar, SanctumMode.Strict, enabled: false);

        Guid audit = await InsertWithoutDuplicateCheckAsync("modes-audit", root, SanctumMode.AuditOnly, enabled: true);

        // Nothing stricter exists: audit-only governs over a disabled Sanctum.
        Assert.Equal(audit, (await repository.GetByContainedPathAsync(root))?.Id);

        Assert.NotEqual(off, audit);
    }

    // A registration that names a directory already registered is a duplicate however its trailing separator or case is spelled.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_registration_that_differs_only_by_a_trailing_separator_is_a_duplicate_path(bool separatorFirst)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "twice")).FullName;

        ICampaignRepository repository = CreateRepository();

        string first = separatorFirst ? root + Path.DirectorySeparatorChar : root;

        string second = separatorFirst ? root : root + Path.DirectorySeparatorChar;

        _ = await AddAsync(repository, "twice-one", first);

        Result<Campaign> duplicate = await repository.AddAsync(NewCampaign("twice-two", second), CancellationToken.None);

        Assert.True(duplicate.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.DuplicatePath, duplicate.Error.Code);

        // The registration pre-checks ask for the Campaign at a path: the other spelling is the same registration.
        Assert.NotNull(await repository.GetByPathAsync(second));
    }

    [SkippableFact]
    public async Task A_registration_that_differs_only_by_case_is_a_duplicate_path_on_a_case_insensitive_platform()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Only the case-insensitive platforms treat a case variant as the same directory.");

        string upper = Directory.CreateDirectory(Path.Combine(_parent, "CaseTwice")).FullName;

        string lower = Path.Combine(_parent, "casetwice");

        Skip.IfNot(Directory.Exists(lower), "This volume is case sensitive.");

        ICampaignRepository repository = CreateRepository();

        _ = await AddAsync(repository, "case-one", upper);

        Result<Campaign> duplicate = await repository.AddAsync(NewCampaign("case-two", lower), CancellationToken.None);

        Assert.True(duplicate.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.DuplicatePath, duplicate.Error.Code);
    }

    [SkippableFact]
    public async Task A_name_prefix_sibling_is_not_a_duplicate_path()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string app = Directory.CreateDirectory(Path.Combine(_parent, "app")).FullName;

        string legacy = Directory.CreateDirectory(Path.Combine(_parent, "app-legacy")).FullName;

        ICampaignRepository repository = CreateRepository();

        _ = await AddAsync(repository, "app", app);

        Result<Campaign> added = await repository.AddAsync(NewCampaign("app-legacy", legacy), CancellationToken.None);

        Assert.True(added.IsSuccess);

        Assert.Null(await repository.GetByPathAsync(Path.Combine(_parent, "app-")));
    }

    // The ancestor walk has no bound that turns into "no Campaign": a working directory any number of components below the root is
    // still the Campaign's.
    [SkippableTheory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(120)]
    public async Task A_working_directory_many_components_below_the_root_is_still_the_campaigns(int depth)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "deep-root")).FullName;

        string deepest = root;

        for (int index = 0; index < depth; index++)
        {
            deepest = Path.Combine(deepest, "d");
        }

        _ = Directory.CreateDirectory(deepest);

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "deep", root);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(deepest))?.Id);

        // The same depth through a spelling that does not exist on disk is still below the root.
        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(deepest, "not", "there")))?.Id);
    }

    [SkippableFact]
    public async Task A_path_longer_than_any_file_system_allows_is_still_below_the_root_by_its_spelling()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "huge-root")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "huge", root);

        string absurd = root + string.Concat(Enumerable.Repeat(Path.DirectorySeparatorChar + "a", 40_000));

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Campaign? found = await repository.GetByContainedPathAsync(absurd);

        watch.Stop();

        Assert.Equal(registered.Id, found?.Id);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The lookup took {watch.Elapsed}.");
    }

    private ICampaignRepository CreateRepository() =>
        new CampaignRepository(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));

    private static Campaign NewCampaign(string name, string path)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Writes a row the way an installation that predates the equivalent-path duplicate check could hold it.</summary>
    private async Task<Guid> InsertWithoutDuplicateCheckAsync(string name, string path, SanctumMode mode, bool enabled)
    {
        Campaign campaign = NewCampaign(name, path);

        campaign.NameLower = name.ToLowerInvariant();

        campaign.SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(
            new SanctumConfig { Enabled = enabled, Mode = mode });

        _ = _db!.Campaigns.Add(campaign);

        _ = await _db.SaveChangesAsync();

        return campaign.Id;
    }

    private static async Task<Campaign> AddAsync(ICampaignRepository repository, string name, string path)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Result<Campaign> added = await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

        return added.Value;
    }
}
