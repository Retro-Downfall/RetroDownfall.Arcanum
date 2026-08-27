using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The closed register of identity comparisons the Covenant tier has not normalised.
/// </summary>
/// <remarks>
/// <para><b>The defect this exists to catch.</b> One identity, two spellings. The object-relational
/// writer and the SQLite provider both render a <see cref="Guid"/> as uppercase dashed TEXT, while
/// <c>ToString()</c> and <c>ToString("D")</c> render it lowercase, and SQLite's BINARY collation makes
/// those two different strings. A statement that binds one form against a column holding the other
/// matches nothing — it does not throw, it does not warn, it returns an empty reader — so a purge
/// leaves content behind, a guard counts zero and permits what it exists to refuse, and a plan commits
/// to a manifest describing a Session with no rows in it.</para>
///
/// <para><b>Why a rule rather than another sweep.</b> Seven sites of this were fixed one at a time,
/// and every one of the six passes before the last looked like the last. Each was invisible to its own
/// suite for the same reason: a fixture seeds identities in whatever form it likes, and the form it
/// likes is usually the one the broken statement already matches — so the suite agrees with the defect
/// by accident and stays green over code that cannot read a real database. Nothing about a new site
/// looks wrong, which is why finding the next one cannot be left to someone noticing.</para>
///
/// <para><b>What to do when this test fails.</b> Read <see cref="Unregistered"/> below. It says it in
/// the failure message too, and the short version is: compare through
/// <c>CovenantIdentitySql.Keyed</c> with a parameter bound through <c>CovenantIdentitySql.Key</c>, or
/// — if the comparison is genuinely correct — add the site to the register with the reason it is
/// correct, so the next reader inherits the reasoning rather than the entry.</para>
///
/// <para><b>Scope, and what deliberately sits outside it.</b> Four namespaces, and only the tables
/// whose identity columns are filled by a writer outside the component reading them. That is what
/// makes the rule enforceable rather than merely broad — measured before it was chosen, the same scan
/// over every identity predicate in these namespaces reports 126 sites, and a rule whose first run
/// reports 126 correct call sites is a rule nobody follows:</para>
///
/// <list type="bullet">
/// <item><description><c>DataRetentionService</c> and its pruning partial spell this same predicate by
/// hand about thirty times, over tables Covenant owns as well as its own, and they are correct today.
/// They live in <c>Infrastructure.Data</c>, outside the four namespaces here, and that is deliberate:
/// a rule that reported thirty correct call sites on its first run is a rule somebody suppresses in
/// its first week, and a suppressed rule catches nothing at all.</description></item>
/// <item><description><c>artifact_sensitivity</c>'s own identities — <c>LabelId</c> and
/// <c>ArtifactId</c> — are outside for a different reason: <c>ArtifactSensitivityLedger</c> is the
/// sole INSERT into that table, so its spelling is internally consistent, normalising it would buy
/// nothing, and it would cost the primary-key seek that makes the label delete cheap. Its
/// <c>SessionId</c> is inside, because that column carries an identity some other writer
/// chose.</description></item>
/// <item><description>Every other Covenant-owned table — the journals, the intent stores, the claim
/// and quota ledgers — is outside on the same reasoning. Covenant writes them and Covenant reads them,
/// in one spelling chosen in one place, and a rule covering them would be a hundred entries about
/// nothing.</description></item>
/// </list>
///
/// <para><b>One stored spelling is not the same as a safe comparison, and this paragraph has now been
/// proved by events.</b> It used to say that two covered columns held a single spelling today —
/// <c>"Campaigns"."Id"</c> because one writer owns it, and <c>"SessionAttachments"."SessionId"</c>
/// because its three writers happened to render alike — and that a lowercase comparison against the
/// second "matches everything, until one of those three renderings changes". <b>Those renderings
/// changed.</b> The version-5 attachment backfill moved that column to the canonical form and converted
/// its writers, and the two readers relying on the coincidence became silent defects on the same day:
/// a session-scoped archive that omitted every attachment blob while reporting success, and an import
/// that copied no attachment from any archive taken after the upgrade. Both are fixed, by opposite
/// means — the archive reader compares exactly because it reads this installation's own database, the
/// importer normalises because it reads a foreign one whose vintage it cannot control. Neither fact was
/// visible from the statement doing the comparing, which was and is the whole point.</para>
///
/// <para><b>What this cannot see, stated precisely, because a rule whose own limits are wrong is the
/// failure mode it exists to prevent.</b> Four blind spots, and the first has live instances:</para>
///
/// <list type="number">
/// <item><description><b>A raw <c>Guid</c> handed to the provider.</b> <see cref="RenderedIdentity"/>
/// matches an explicit rendering — <c>ToString()</c>, <c>ToString("D")</c>, a <c>:D</c> format hole —
/// and nothing else. <c>AddWithValue("$id", someGuid)</c> renders uppercase just as surely, through
/// the provider's own type mapping, and is invisible here. <c>GrimoireRepository.TurnCommit</c> does
/// exactly that against <c>assistant_entry_finalizations.AssistantEntryId</c>, which is a genuinely
/// two-spelling column, so that is a live in-scope instance this rule does not report. Entity
/// Framework LINQ — <c>db.Entries.Where(e =&gt; e.SessionId == id)</c> — is the same blind spot with
/// no SQL text at all, and it too has live instances.</description></item>
/// <item><description><b>Join predicates.</b> <see cref="IsPredicate"/> accepts a comparison only when
/// <c>WHERE</c> is the nearest preceding clause keyword, so an identity compared in a <c>JOIN … ON</c>
/// is not reported at all.</description></item>
/// <item><description><b>A table resolved by proximity.</b> <see cref="ResolveTable"/> takes the
/// nearest preceding <c>FROM</c>/<c>JOIN</c>/<c>UPDATE</c>/<c>INTO</c>, so a predicate in an outer
/// query that follows a subquery is attributed to the subquery's table and may be dropped as
/// out-of-scope.</description></item>
/// <item><description><b>A rendering that crosses a method.</b> An identity rendered into a local in
/// one method and bound in another, or arriving as an already-rendered <c>string</c> parameter, has no
/// rendering for the scan to find beside the comparison.</description></item>
/// </list>
///
/// <para>A table name composed at runtime is <em>not</em> in that list: those are reported under
/// <c>{composed}</c> rather than skipped, because a comparison whose table nobody can read is exactly
/// where a member of this family hid longest.</para>
/// </remarks>
public sealed class CovenantIdentityComparisonInventoryTests
{

    /// <summary>
    /// The registered sites, each with the reason it is still here.
    /// </summary>
    /// <remarks>
    /// Closer to a defect register than an exemption list. Four of these are comparisons that match
    /// nothing they were meant to match and nobody has fixed; two match today only because writers
    /// that never agreed to agree happen to render alike. Every entry carries its own reason and
    /// opens by saying which of the two it is, so an entry appended later cannot inherit a
    /// justification written for a different site.
    ///
    /// <para>An entry leaves this list by being fixed, and the test fails if a fixed site is left
    /// registered — so the register cannot outlive what it names. Two entries left it that way when the
    /// reads they described were normalised, and a third left when the attachment importer's read was,
    /// which is also when this register caught a claim in its own prose going stale.</para>
    /// </remarks>
    private static readonly string[] Registered =
    [
        // Matching, and now by construction rather than by coincidence. This entry used to read
        // "matching on a coincidence ... listed rather than fixed because a fix changes no behaviour
        // today", and the coincidence it named was real: every writer of that column rendered lowercase
        // and this reader happened to agree. The version-5 attachment backfill moved the column to the
        // canonical form and converted its writers, and the coincidence became a silent defect - a
        // session-scoped archive omitted every attachment blob while reporting no failure and no
        // missing path. The reader is now bound canonically, so it agrees with the column deliberately.
        // It stays registered because the comparison is still exact rather than normalised, and it
        // should be: this reads the installation's own database, where exact is the correct shape.
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupInventoryPlanner.cs | SessionAttachments.SessionId",

        // Not matching. The unprotected merge path copies the archived Entry graph with the lowercase
        // rendering against a column the object-relational writer fills uppercase, so it copies no
        // entries for a Session it did manage to find.
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs | Entries.SessionId",

        // Not matching, and the one that decides the whole operation. The merge path probes
        // "Sessions"."Id" with the lowercase rendering, so it reports "The archive does not contain
        // every requested Session" for a Session the archive plainly holds. Confirmed by construction:
        // seeding its fixture the way an archive is written turns its currently green cases red. That
        // fixture is deliberately left alone — changing it would replace a suite that passes over a
        // known gap with one that fails over it, which belongs to whoever fixes the path.
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs | Sessions.Id",

        // Not matching. The same merge path's row probe, whose table is composed by its caller, so the
        // rule can name the column but not the table. Reported under {composed} rather than skipped,
        // because a comparison whose table nobody can read is where this family hid longest.
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs | {composed}.Id",

        // Not matching, and the mildest member. This probe only chooses between two error messages, so
        // an unmatched row reports "Session not found" where the truth is "this Session has no
        // Campaign binding". A wrong answer to an operator, and no data loss.
        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionCampaignBindingReader.cs | Sessions.Id",
    ];

    /// <summary>
    /// The four namespaces this rule covers.
    /// </summary>
    /// <remarks>
    /// Matched on the file's declared namespace rather than on its path, so a file that moves stays in
    /// or out of scope for the reason it was put there.
    /// </remarks>
    private static readonly string[] ScopedNamespaces =
    [
        "RetroDownfall.Arcanum.Infrastructure.Backup",
        "RetroDownfall.Arcanum.Infrastructure.Covenant",
        "RetroDownfall.Arcanum.Infrastructure.Data.Covenant",
        "RetroDownfall.Arcanum.Infrastructure.Repositories",
    ];

    /// <summary>
    /// The identity columns more than one writer fills, by the table that holds them.
    /// </summary>
    /// <remarks>
    /// Each of these carries a Session, Entry or Campaign identity that a writer outside the component
    /// reading it puts rows into. That is the whole basis of the rule: which spelling a column holds
    /// is a property of its writers, not of the statement that reads it, so a comparison is only worth
    /// reporting where a reader cannot see what it will meet.
    ///
    /// <para>Two spellings is the usual case — <c>"Sessions"."Id"</c> and <c>"Entries"."Id"</c> hold
    /// the object-relational writer's uppercase form beside the protected transfer store's lowercase
    /// one — but it is not the only one. <c>"Campaigns"."Id"</c> has a single writer and a single
    /// uppercase spelling, and a comparison that binds the default lowercase rendering against it
    /// still matches nothing at all. One stored spelling is not the same as a safe comparison.</para>
    ///
    /// <para>A table missing from here is a table one component writes and reads. Adding a writer in
    /// another component is what should bring it into this list.</para>
    /// </remarks>
    private static readonly Dictionary<string, string[]> SharedSpellingColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {

            ["Sessions"] = ["Id"],

            ["Entries"] = ["Id", "SessionId"],

            // Three writers in three components — the attachment store, the protected transfer store,
            // and the unprotected merge path — and all three currently render with ToString(), so this
            // column holds one lowercase spelling and a lowercase comparison finds its rows. Covered
            // anyway: that agreement is three independent renderings coinciding, not a structure, and
            // no reader of any one of them can see the other two.
            ["SessionAttachments"] = ["SessionId", "EntryId"],

            ["Campaigns"] = ["Id"],

            ["entry_embeddings"] = ["EntryId", "SessionId"],

            ["assistant_entry_finalizations"] = ["AssistantEntryId", "SessionId"],

            // Two spellings by design: the erasure kernel's projection repair writes the Session's own
            // stored text, while the label ledger writes what its caller handed it. Nothing structural
            // forces a future reader of this column to normalise, so the rule is what does.
            ["session_sensitivity_state"] = ["SessionId"],

        };

    private const string Identifier =
        "(?:\"[A-Za-z_][A-Za-z0-9_]*\"|\"?\\{[A-Za-z_][A-Za-z0-9_]*\\}\"?|[A-Za-z_][A-Za-z0-9_]*)";

    private static readonly Regex DeclaredNamespace =
        new(@"^namespace\s+(?<namespace>[\w.]+)\s*;", RegexOptions.Multiline);

    /// <summary>A comparison of a column against a bound parameter.</summary>
    private static readonly Regex BoundComparison = new(
        "(?:[A-Za-z_][A-Za-z0-9_]*\\.)?(?<column>" + Identifier
        + ")\\s*(?:=|IN\\s*\\()\\s*[$@](?<parameter>[A-Za-z_][A-Za-z0-9_]*)");

    /// <summary>A comparison of a column against a value composed into the statement itself.</summary>
    private static readonly Regex ComposedComparison = new(
        "(?:[A-Za-z_][A-Za-z0-9_]*\\.)?(?<column>" + Identifier + ")\\s*(?:=|IN\\s*\\()\\s*'?\\{");

    private static readonly Regex TableReference = new(
        "\\b(?:FROM|JOIN|UPDATE|INTO)\\s+(?<table>" + Identifier + ")",
        RegexOptions.IgnoreCase);

    private static readonly Regex ClauseKeyword = new("\\b(?<keyword>WHERE|SET)\\b", RegexOptions.IgnoreCase);

    private static readonly Regex ParameterBinding = new(
        "(?:AddWithValue|AddParameter)\\(\\s*(?:[A-Za-z_][A-Za-z0-9_]*\\s*,\\s*)?"
        + "\"[$@](?<parameter>[A-Za-z_][A-Za-z0-9_]*)\"\\s*,(?<value>[^;]*?)\\);",
        RegexOptions.Singleline);

    /// <summary>A <see cref="Guid"/> turned into text by this build rather than read out of a row.</summary>
    private static readonly Regex RenderedIdentity =
        new("ToString\\(\\s*(?:\"[A-Za-z]\"\\s*)?\\)|:[DdNnBbPp]\\}");

    private const string ComposedTable = "{composed}";

    [Fact]
    public void Every_shared_spelling_identity_comparison_is_normalised_or_registered()
    {

        IReadOnlyList<string> found = Comparisons();

        IReadOnlyList<string> unregistered =
            [.. found.Where(static site => !Registered.Contains(site, StringComparer.Ordinal))];

        Assert.True(unregistered.Count == 0, Unregistered(unregistered));

        IReadOnlyList<string> stale =
            [.. Registered.Where(site => !found.Contains(site, StringComparer.Ordinal))];

        Assert.True(stale.Count == 0, Stale(stale));

    }

    /// <summary>
    /// The message a contributor who has never met this defect reads first.
    /// </summary>
    /// <remarks>
    /// Long on purpose. The failure it reports is a statement that compiles, runs, matches nothing and
    /// says nothing, in code whose own tests pass because they seed the form it expects — so a terse
    /// "unexpected entry in list" would send the reader looking for a style violation instead of a
    /// silent empty result.
    /// </remarks>
    private static string Unregistered(IReadOnlyList<string> sites) =>
        $"""
        {sites.Count} SQL comparison(s) in the Covenant tier bind an identity this build rendered
        against a column some other component writes, without normalising it:

        {string.Join(System.Environment.NewLine + "        ", sites)}

        Why this matters. The object-relational writer and the SQLite provider render a Guid as
        uppercase dashed text; ToString() and ToString("D") render it lowercase; SQLite compares TEXT
        byte for byte. A comparison that mixes the two matches no row at all, and it does so silently:
        no exception, no warning, an empty reader. That is how a purge left tainted content behind, how
        a plaintext-export guard counted zero labels and permitted the export it exists to refuse, and
        how a selective import planned a manifest for a Session it could not see.

        Whether the column holds one spelling or two is not the question, and it is not something the
        statement doing the comparing can see. A column with a single writer still refuses every
        comparison spelled the other way, and a column whose writers agree today agrees only until one
        of them changes.

        It survives its own suite because a fixture chooses the spelling it seeds, and the spelling it
        chooses is usually the one the broken statement already matches. A green suite is not evidence
        here.

        What to do. Compare through CovenantIdentitySql.Keyed("<column>", "$key") and bind the
        parameter through CovenantIdentitySql.Key(identity). That reduces every stored spelling to one
        and costs a scan of the table instead of an index seek; state what that costs at the call site,
        as the existing call sites do. Where the comparison satisfies a foreign key rather than a
        predicate, a predicate cannot help — use CovenantIdentitySql.ResolveStoredSessionIdAsync and
        agree with the parent row's own text.

        If the comparison really is correct — you have read every writer of that column and this
        statement spells the identity the way all of them do — then either remove that table from
        SharedSpellingColumns with the reason, or add the site to Registered with the reason. Read the
        writers; do not take an earlier comment's word for which spelling a column holds, because one
        of the entries already in that register is there because somebody did.
        """;

    private static string Stale(IReadOnlyList<string> sites) =>
        $"""
        {sites.Count} registered site(s) no longer exist:

        {string.Join(System.Environment.NewLine + "        ", sites)}

        Remove them from Registered. The register is closed in both directions on purpose: a fixed site
        left listed is an exception nobody can justify any more, and the next reader would inherit it as
        precedent for the next one.
        """;

    private static IReadOnlyList<string> Comparisons()
    {

        List<string> found = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {

            Match declared = DeclaredNamespace.Match(source.Text);

            if (!declared.Success
                || !ScopedNamespaces.Contains(declared.Groups["namespace"].Value, StringComparer.Ordinal))
            {

                continue;

            }

            // An escaped quote inside a C# string literal spells the same SQL identifier as the bare
            // quote in a raw literal. Folding them first is what lets one pattern read both, and its
            // absence is why an earlier draft of this scan missed two of the eight sites it was
            // written for.
            string sql = source.Text.Replace("\\\"", "\"", StringComparison.Ordinal);

            HashSet<string> rendered = RenderedParameters(sql);

            foreach (Match comparison in BoundComparison.Matches(sql))
            {

                if (!rendered.Contains(comparison.Groups["parameter"].Value))
                {

                    continue;

                }

                Register(found, source, sql, comparison);

            }

            foreach (Match comparison in ComposedComparison.Matches(sql))
            {

                Register(found, source, sql, comparison);

            }

        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    }

    private static void Register(
        List<string> found,
        ProductionSource source,
        string sql,
        Match comparison)
    {

        if (!IsPredicate(sql, comparison.Index))
        {

            return;

        }

        string authored = comparison.Groups["column"].Value;

        bool composedColumn = authored.Contains('{', StringComparison.Ordinal);

        string column = Bare(authored);

        // A composed name is reported under the hole it was written as, so the register entry points
        // at the statement rather than at a variable name that means nothing on its own.
        string reported = composedColumn ? $"{{{column}}}" : column;

        if (ResolveTable(sql, comparison.Index) is not { } table)
        {

            return;

        }

        // A table composed at runtime cannot be resolved, so the column decides. Reported rather than
        // skipped: one of the eight sites this rule was written for was exactly that shape — a count
        // whose table and column were both handed in by its caller — and skipping it would have left
        // the rule silent about the very statement that hid longest.
        if (table == ComposedTable)
        {

            if (!composedColumn && !IsIdentityColumn(column))
            {

                return;

            }

            found.Add($"{source.RelativePath} | {ComposedTable}.{reported}");

            return;

        }

        if (!SharedSpellingColumns.TryGetValue(table, out string[]? columns))
        {

            return;

        }

        // A composed column on a table this rule covers is reported whatever it resolves to, because
        // the alternative is deciding it is safe on the strength of a name nobody can read.
        if (!composedColumn && !columns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {

            return;

        }

        found.Add($"{source.RelativePath} | {table}.{reported}");

    }

    /// <summary>
    /// The parameters this file binds to a <see cref="Guid"/> it rendered itself.
    /// </summary>
    /// <remarks>
    /// A parameter bound through <c>CovenantIdentitySql.Key</c> is excluded, because that is the
    /// normalised form the shape expects. A parameter bound from text read out of a row is not here at
    /// all — the rendering is what this rule can see.
    /// </remarks>
    private static HashSet<string> RenderedParameters(string sql)
    {

        HashSet<string> rendered = new(StringComparer.Ordinal);

        foreach (Match binding in ParameterBinding.Matches(sql))
        {

            string value = binding.Groups["value"].Value;

            if (value.Contains("CovenantIdentitySql.Key", StringComparison.Ordinal))
            {

                continue;

            }

            if (RenderedIdentity.IsMatch(value))
            {

                _ = rendered.Add(binding.Groups["parameter"].Value);

            }

        }

        return rendered;

    }

    /// <summary>
    /// Whether the comparison sits in a predicate rather than in an assignment.
    /// </summary>
    /// <remarks>
    /// Decided by which of <c>WHERE</c> and <c>SET</c> is nearer, so <c>UPDATE t SET SessionId = $a
    /// WHERE Id = $b</c> reports the second and not the first. An assignment writes the spelling; only
    /// a comparison has to agree with one somebody else wrote.
    /// </remarks>
    private static bool IsPredicate(string sql, int index)
    {

        Match nearest = Match.Empty;

        foreach (Match keyword in ClauseKeyword.Matches(sql))
        {

            if (keyword.Index >= index)
            {

                break;

            }

            nearest = keyword;

        }

        return nearest.Success
            && string.Equals(nearest.Groups["keyword"].Value, "WHERE", StringComparison.OrdinalIgnoreCase);

    }

    private static string? ResolveTable(string sql, int index)
    {

        Match nearest = Match.Empty;

        foreach (Match reference in TableReference.Matches(sql))
        {

            if (reference.Index >= index)
            {

                break;

            }

            nearest = reference;

        }

        if (!nearest.Success)
        {

            return null;

        }

        string table = nearest.Groups["table"].Value;

        return table.Contains('{', StringComparison.Ordinal) ? ComposedTable : Bare(table);

    }

    private static bool IsIdentityColumn(string column) =>
        column.EndsWith("Id", StringComparison.OrdinalIgnoreCase);

    private static string Bare(string identifier) =>
        identifier.Trim('"').Trim('{', '}').Trim('"');

}
