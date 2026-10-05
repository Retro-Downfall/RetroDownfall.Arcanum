using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal sealed partial class SpellRepository : ISpellRepository
{
    /// <summary>
    /// Operator-facing text for every <c>Spell.WriteFailed</c> envelope. Raw exception messages
    /// name absolute server paths, so the exception detail stays in the structured log the same
    /// catch block writes and the API answers with this fixed sentence instead. Mirrors
    /// <c>PhysicalFileSystemWriter</c>'s I/O write message.
    /// </summary>
    private const string WriteFailedMessage = "The spell could not be written. See server logs.";

    /// <summary>
    /// The most script files one spell bundle carries, for export and import alike, so a bundle one side can
    /// emit the other can take back. The byte envelope (per file and aggregate) already bounds what the
    /// scripts weigh; this bounds how many directory entries a bundle of tiny files can create.
    /// </summary>
    internal const int MaxSpellScriptCount = 64;

    /// <summary>The longest the summary read-back after a spell has been published may take.</summary>
    private static readonly TimeSpan PublishedSummaryReadTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex ValidNameRegex = ValidNamePattern();

    private readonly ILogger<SpellRepository> _logger;

    private readonly KeyedLock<string> _workspaceLocks = new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IMcpConnectionManager _mcpManager;

    private readonly SpellSearchService _searchService;

    /// <summary>
    /// Deterministic test seam invoked right after a staged spell directory has been moved into place and
    /// before anything is read back, so a test can cancel the caller at the one instant the spell is
    /// already published.
    /// </summary>
    internal Action? AfterSpellDirectoryPublishedForTests { get; set; }

    /// <summary>
    /// Deterministic test seam invoked once per mutating call, immediately before its first write: after the
    /// staging directory exists for create, clone and import, and before the in-place replace for update and
    /// for version create and update. A test cancels the caller here, while nothing has been published or
    /// replaced yet.
    /// </summary>
    internal Action? BeforeFirstSpellWriteForTests { get; set; }

    public SpellRepository(
        ILogger<SpellRepository> logger,
        IServiceScopeFactory scopeFactory,
        IMcpConnectionManager mcpManager,
        IOptionsMonitor<ArcanumSettings> settingsMonitor)
    {
        _logger = logger;

        _scopeFactory = scopeFactory;

        _mcpManager = mcpManager;

        _searchService = new SpellSearchService(settingsMonitor);
    }

    private long GetMaxSpellFileSizeBytes() =>
        ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();

    private int GetMaxSpellDeclaredTools() =>
        ArcanumSettingClamps.MaxDeclaredTools(ArcanumRuntimeDefaults.Spells.MaxDeclaredTools);

    public async Task<SpellSummary[]> ListAsync(string? workingDirectory, CancellationToken ct)
    {
        IReadOnlyList<SpellSummary> summaries = await SpellScanner
            .ScanSummariesAsync(
                workingDirectory,
                ct,
                GetMaxSpellFileSizeBytes())
            .ConfigureAwait(false);

        return summaries.ToArray();
    }

    public async Task<SpellDetail?> GetAsync(string name, string? workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        IReadOnlyList<SpellMetadata> metadata = await SpellScanner
            .ScanMetadataAsync(
                workingDirectory,
                ct,
                GetMaxSpellFileSizeBytes())
            .ConfigureAwait(false);

        SpellMetadata? match = metadata.FirstOrDefault(
            spell => string.Equals(
                spell.Name,
                name.Trim(),
                StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return null;
        }

        ParsedSpell? spell = await SpellScanner.LoadFullAsync(
            match.FilePath,
            ct,
            GetMaxSpellFileSizeBytes(),
            GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        return spell is null
            ? null
            : ToDetail(spell, workingDirectory);
    }

    public async Task<Result> CreateAsync(string? workingDirectory, CreateSpellRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to create spells."));
        }

        string? nameError = ValidateName(request.Name);

        if (nameError is not null)
        {
            return Result.Failure(new Error(ErrorCodes.Spell.InvalidName, nameError));
        }

        string trimmedName = request.Name.Trim();

        string workspaceRoot = workingDirectory.Trim();

        // The snapshot every decision below rests on is read under the write lock, so a concurrent mutator
        // cannot change the workspace between the read and the write that depends on it.
        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        if (FindByName(allSpells, trimmedName) is ParsedSpell existing)
        {
            if (IsBuiltinSpell(existing))
            {
                return Result.Failure(new Error("Spell.DuplicateName", "A built-in spell with that name already exists."));
            }

            return Result.Failure(new Error("Spell.DuplicateName", "A workspace spell with that name already exists."));
        }

        if (FindBuiltinByName(allSpells, trimmedName) is not null)
        {
            return Result.Failure(new Error("Spell.DuplicateName", "A built-in spell with that name already exists."));
        }

        string? frontmatterError = SpellFrontmatterValidator.ValidateCreate(request);

        if (frontmatterError is not null)
        {
            return Result.Failure(new Error("Spell.InvalidFrontmatter", frontmatterError));
        }

        SpellSettings spellSettings = ArcanumRuntimeDefaults.Spells;

        int maxDeclaredTools = ArcanumSettingClamps.MaxDeclaredTools(spellSettings.MaxDeclaredTools);

        string? skillBoundsError = SkillJsonBoundsValidator.ValidateCreate(request, maxDeclaredTools);

        if (skillBoundsError is not null)
        {
            return Result.Failure(new Error("Spell.InvalidSkillJson", skillBoundsError));
        }

        if (string.IsNullOrWhiteSpace(request.SystemPrompt) && string.IsNullOrWhiteSpace(request.Template))
        {
            _logger.LogWarning(
                "Creating spell '{SpellName}' without systemPrompt or template.",
                trimmedName);
        }

        string spellsRoot = Path.Combine(workspaceRoot, "spells");

        string spellDir = Path.Combine(spellsRoot, trimmedName);

        string spellFile = Path.Combine(spellDir, "SPELL.md");

        string content;

        bool hasStructured = SkillJsonIO.HasStructuredFields(request);

        if (hasStructured
            && string.IsNullOrWhiteSpace(request.Body)
            && string.IsNullOrWhiteSpace(request.SystemPrompt)
            && string.IsNullOrWhiteSpace(request.Template))
        {
            content = SpellMarkdownGenerator.GenerateFromCreateRequest(trimmedName, request);
        }
        else
        {
            content = SpellFileParser.FormatCreate(trimmedName, request);
        }

        string? stagingDir = null;

        try
        {
            Directory.CreateDirectory(spellsRoot);

            stagingDir = Path.Combine(spellsRoot, $".staging-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingDir);

            BeforeFirstSpellWriteForTests?.Invoke();

            await File.WriteAllTextAsync(Path.Combine(stagingDir, "SPELL.md"), content, ct).ConfigureAwait(false);

            if (hasStructured)
            {
                SkillMetadata metadata = SkillJsonIO.BuildMetadataFromCreate(trimmedName, request);

                await SkillJsonIO.WriteAsync(stagingDir, metadata, ct).ConfigureAwait(false);
            }

            if (Directory.Exists(spellDir))
            {
                return Result.Failure(new Error("Spell.DuplicateName", "A workspace spell with that name already exists."));
            }

            Directory.Move(stagingDir, spellDir);

            stagingDir = null;

            return Result.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create spell {SpellName} at {SpellPath}", trimmedName, spellFile);

            return Result.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDir);
        }
    }

    public async Task<Result> UpdateAsync(string name, string? workingDirectory, UpdateSpellRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to update spells."));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string trimmedName = name.Trim();

        string workspaceRoot = workingDirectory.Trim();

        // Read-modify-write: the spell is read under the write lock, so two updates that arrive together each
        // build on the other's result instead of both rewriting the same stale snapshot.
        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        ParsedSpell? workspaceSpell = FindWorkspaceSpell(allSpells, trimmedName, workingDirectory);

        if (workspaceSpell is null)
        {
            if (FindBuiltinByName(allSpells, trimmedName) is not null)
            {
                return Result.Failure(new Error(ErrorCodes.Spell.BuiltinReadOnly, "Built-in spells cannot be modified."));
            }

            return Result.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string? frontmatterError = SpellFrontmatterValidator.ValidateUpdate(request);

        if (frontmatterError is not null)
        {
            return Result.Failure(new Error("Spell.InvalidFrontmatter", frontmatterError));
        }

        SpellSettings spellSettings = ArcanumRuntimeDefaults.Spells;

        int maxDeclaredTools = ArcanumSettingClamps.MaxDeclaredTools(spellSettings.MaxDeclaredTools);

        string? skillBoundsError = SkillJsonBoundsValidator.ValidateUpdate(request, maxDeclaredTools);

        if (skillBoundsError is not null)
        {
            return Result.Failure(new Error("Spell.InvalidSkillJson", skillBoundsError));
        }

        string content = SpellFileParser.FormatUpdate(workspaceSpell, request);

        bool specReplaced = false;

        try
        {
            BeforeFirstSpellWriteForTests?.Invoke();

            await SpellAtomicFile.WriteAllTextAsync(workspaceSpell.FilePath, content, ct).ConfigureAwait(false);

            specReplaced = true;

            if (SkillJsonIO.HasStructuredFields(request) || workspaceSpell.SkillMetadata is not null)
            {
                SkillMetadata metadata = SkillJsonIO.MergeMetadata(workspaceSpell, request);

                // SPELL.md has been replaced, so the sidecar that describes it is finished whatever the caller
                // does now: a cancellation between the two files would leave one spell half updated.
                await SkillJsonIO.WriteAsync(workspaceSpell.DirectoryPath, metadata, CancellationToken.None).ConfigureAwait(false);
            }

            return Result.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update spell {SpellName} at {SpellPath}", trimmedName, workspaceSpell.FilePath);

            // The caller is being told the update did not happen, so a SPELL.md that already holds the new
            // content is put back rather than left disagreeing with the sidecar the failed write was to update.
            if (specReplaced)
            {
                await RestoreSpellFileAsync(workspaceSpell.FilePath, workspaceSpell.FullContent).ConfigureAwait(false);
            }

            return Result.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
    }

    public async Task<Result> DeleteAsync(string name, string? workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to delete spells."));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string trimmedName = name.Trim();

        string workspaceRoot = workingDirectory.Trim();

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        ParsedSpell? workspaceSpell = FindWorkspaceSpell(allSpells, trimmedName, workingDirectory);

        if (workspaceSpell is null)
        {
            if (FindBuiltinByName(allSpells, trimmedName) is not null)
            {
                return Result.Failure(new Error(ErrorCodes.Spell.BuiltinReadOnly, "Built-in spells cannot be deleted."));
            }

            return Result.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        try
        {
            if (!TryResolveDeleteTarget(workspaceRoot, trimmedName, workspaceSpell, out string spellDirectory, out Error deleteError))
            {
                return Result.Failure(deleteError);
            }

            if (Directory.Exists(spellDirectory))
            {
                Directory.Delete(spellDirectory, recursive: true);
            }
            else if (File.Exists(workspaceSpell.FilePath))
            {
                File.Delete(workspaceSpell.FilePath);
            }

            return Result.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete spell {SpellName} at {SpellPath}", trimmedName, workspaceSpell.FilePath);

            return Result.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
    }

    public async Task<SpellSummary[]> SearchAsync(SpellSearchQuery query, CancellationToken ct)
    {
        IReadOnlyList<Campaign> campaigns = query.Campaigns;

        if (campaigns.Count == 0 && query.CampaignId is null)
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ICampaignRepository campaignRepository =
                scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

            ListPageResult<Campaign> page = await campaignRepository
                .ListAsync(typeFilter: null, limit: ArcanumSettingClamps.ListQueryLimit(10_000), cancellationToken: ct)
                .ConfigureAwait(false);

            campaigns = page.Items;
        }

        SpellSearchQuery enriched = query with { Campaigns = campaigns };

        return await _searchService.SearchAsync(enriched, ct).ConfigureAwait(false);
    }

    public async Task<SpellValidationResultDto> ValidateAsync(string name, string? workingDirectory, CancellationToken ct)
    {
        var errors = new List<string>();

        var warnings = new List<string>();

        SpellDetail? detail = await GetAsync(name, workingDirectory, ct).ConfigureAwait(false);

        if (detail is null)
        {
            errors.Add("Spell was not found.");

            return new SpellValidationResultDto(false, errors.ToArray(), warnings.ToArray());
        }

        if (detail.InputSchema is not null && !IsValidJsonObject(detail.InputSchema))
        {
            errors.Add("InputSchema is not a valid JSON object.");
        }

        if (detail.OutputSchema is not null && !IsValidJsonObject(detail.OutputSchema))
        {
            errors.Add("OutputSchema is not a valid JSON object.");
        }

        if (detail.DeclaredTools is { Length: > 0 })
        {
            IReadOnlyList<string> toolNames = await GetMcpToolNamesAsync(ct).ConfigureAwait(false);

            HashSet<string> known = new(toolNames, StringComparer.OrdinalIgnoreCase);

            foreach (string tool in detail.DeclaredTools)
            {
                if (!known.Contains(tool)
                    && !ArcanumBuiltInToolNames.IsKnown(tool))
                {
                    warnings.Add($"Declared tool '{tool}' was not found in configured MCP servers.");
                }
            }
        }

        if (detail.Dependencies is { Length: > 0 })
        {
            SpellSummary[] all = await SearchAsync(
                new SpellSearchQuery(null, null, null, null, null, workingDirectory, []),
                ct).ConfigureAwait(false);

            HashSet<string> names = new(all.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

            foreach (string dep in detail.Dependencies)
            {
                if (!names.Contains(dep))
                {
                    errors.Add($"Dependency '{dep}' was not found in the spell catalog.");
                }
            }
        }

        return new SpellValidationResultDto(errors.Count == 0, errors.ToArray(), warnings.ToArray());
    }

    public async Task<SpellExportDto?> ExportAsync(string name, string? workingDirectory, CancellationToken ct)
    {
        SpellDetail? detail = await GetAsync(name, workingDirectory, ct).ConfigureAwait(false);

        if (detail is null || detail.FilePath is null)
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(detail.FilePath);

        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();

        // Spell export has a code-owned aggregate script-byte envelope. Reuse the clamped workspace
        // read-size cap so a single export cannot stream unbounded content.
        long aggregateScriptCap = ArcanumSettingClamps.MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        // Every file the export reads goes through the scanner's hardened path (DESIGN section 11.6):
        // workspace containment, a regular-file stat gate (a FIFO stats as length 0 and a blocking open
        // never returns), then SecureFileReader's no-follow open of the same object under a bounded read.
        // A workspace spell is revalidated against its workspace root; a built-in spell lives under the
        // owner-controlled global directory, which has no workspace root to contain it.
        string? workspaceRoot = detail.Source == SpellSource.Workspace && !string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetFullPath(workingDirectory.Trim())
            : null;

        int maxReadBytes = (int)Math.Min(perFileCap, int.MaxValue - 1);

        string? fullContent = null;

        if (workspaceRoot is null || WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, detail.FilePath))
        {
            SecureUtf8FileReadResult specRead = await SecureFileReader
                .ReadUtf8TextAsync(detail.FilePath, maxReadBytes, ct)
                .ConfigureAwait(false);

            if (specRead.Status is SecureFileReadStatus.Success)
            {
                fullContent = specRead.Text;
            }
        }

        if (fullContent is null)
        {
            _logger.LogWarning(
                "Spell {SpellName} export refused: {SpellFile} is not a readable regular file inside its root.",
                name,
                Path.GetFileName(detail.FilePath));

            return null;
        }

        SkillMetadata? metadata = null;

        string? sidecarPath = SkillJsonIO.ResolveSidecarPath(dir);

        if (sidecarPath is not null)
        {
            if (!TryGetExportableFileLength(sidecarPath, workspaceRoot, out long sidecarLength))
            {
                _logger.LogWarning(
                    "Skipping non-regular {SidecarFile} for spell {SpellName} export.",
                    Path.GetFileName(sidecarPath),
                    name);
            }
            else if (sidecarLength > perFileCap)
            {
                _logger.LogWarning(
                    "Skipping oversized {SidecarFile} for spell {SpellName} export: {Size} bytes exceeds {Cap} bytes.",
                    Path.GetFileName(sidecarPath),
                    name,
                    sidecarLength,
                    perFileCap);
            }
            else
            {
                try
                {
                    SecureUtf8FileReadResult sidecarRead = await SecureFileReader
                        .ReadUtf8TextAsync(sidecarPath, maxReadBytes, ct)
                        .ConfigureAwait(false);

                    if (sidecarRead.Status is SecureFileReadStatus.Success && sidecarRead.Text is not null)
                    {
                        metadata = JsonSerializer.Deserialize(sidecarRead.Text, Core.Serialization.ArcanumCoreJsonContext.Default.SkillMetadata);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        var scripts = new List<SpellExportScriptDto>();

        // The files a bundle leaves out are named, so a caller can tell a complete bundle from a partial one.
        // The list is bounded like the bundle itself; a spell with a directory full of unusable files still
        // answers with a short list, not an unbounded one.
        var omittedScripts = new List<string>();

        string scriptsDir = Path.Combine(dir, "scripts");

        if (Directory.Exists(scriptsDir))
        {
            long totalScriptBytes = 0;

            // Ordinal file-name order, the order the scanner lists a spell's scripts in, so which scripts a
            // capped bundle keeps does not depend on the order the filesystem hands them back in.
            string[] paths = Directory
                .EnumerateFiles(scriptsDir)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();

            bool stopped = false;

            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(path);

                if (stopped)
                {
                    RecordOmittedScript(omittedScripts, fileName);

                    continue;
                }

                if (scripts.Count >= MaxSpellScriptCount)
                {
                    _logger.LogWarning(
                        "Stopping script export for spell {SpellName}: a bundle carries at most {Cap} scripts.",
                        name,
                        MaxSpellScriptCount);

                    stopped = true;

                    RecordOmittedScript(omittedScripts, fileName);

                    continue;
                }

                if (!TryGetExportableFileLength(path, workspaceRoot, out long fileLength))
                {
                    _logger.LogWarning(
                        "Skipping non-regular script {ScriptPath} for spell {SpellName} export.",
                        path,
                        name);

                    RecordOmittedScript(omittedScripts, fileName);

                    continue;
                }

                if (fileLength > perFileCap)
                {
                    _logger.LogWarning(
                        "Skipping oversized script {ScriptPath} for spell {SpellName} export: {Size} bytes exceeds {Cap} bytes.",
                        path,
                        name,
                        fileLength,
                        perFileCap);

                    RecordOmittedScript(omittedScripts, fileName);

                    continue;
                }

                if (totalScriptBytes + fileLength > aggregateScriptCap)
                {
                    _logger.LogWarning(
                        "Stopping script export for spell {SpellName}: aggregate {Total} bytes + {Size} bytes would exceed cap {Cap} bytes.",
                        name,
                        totalScriptBytes,
                        fileLength,
                        aggregateScriptCap);

                    stopped = true;

                    RecordOmittedScript(omittedScripts, fileName);

                    continue;
                }

                // Scripts are binary, so the bytes go out as read: the secure read returns raw bytes and
                // nothing here decodes or re-encodes them as text.
                using SecureFileReadResult scriptRead = await SecureFileReader
                    .ReadBytesAsync(path, maxReadBytes, ct)
                    .ConfigureAwait(false);

                if (scriptRead.Status is not SecureFileReadStatus.Success)
                {
                    _logger.LogWarning(
                        "Skipping script {ScriptPath} for spell {SpellName} export: the secure read reported {Status}.",
                        path,
                        name,
                        scriptRead.Status);

                    RecordOmittedScript(omittedScripts, fileName);

                    continue;
                }

                scripts.Add(new SpellExportScriptDto(fileName, Convert.ToBase64String(scriptRead.Bytes.Span)));

                totalScriptBytes += scriptRead.Bytes.Length;
            }
        }

        return new SpellExportDto(metadata, fullContent, scripts, omittedScripts);
    }

    private static void RecordOmittedScript(List<string> omittedScripts, string fileName)
    {
        if (omittedScripts.Count < MaxSpellScriptCount)
        {
            omittedScripts.Add(fileName);
        }
    }

    public async Task<Result<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken ct)
    {
        string? workspace = request.Workspace;

        if (string.IsNullOrWhiteSpace(workspace))
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to import spells."));
        }

        // SpellImportRequest.Payload is a non-required positional parameter, so a body that omits
        // "payload" deserializes it as null. Refuse it as an invalid body rather than letting the
        // dereference below escape the Result flow as an unhandled 500.
        if (request.Payload is null)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "An import payload is required."));
        }

        // The bundle is bounded by the same envelope export emits (per-file and aggregate bytes, and the script
        // count) before anything is parsed, decoded into a staging directory, or scanned for.
        Result<IReadOnlyList<ImportedScript>> bundle = ReadImportBundle(request.Payload);

        if (bundle.IsFailure)
        {
            return Result<SpellSummary>.Failure(bundle.Error);
        }

        string name = request.Payload.Metadata?.Name
            ?? SpellFileParser.Parse(request.Payload.FullContent, "imported").Name;

        CreateSpellRequest create = new(
            name,
            request.Payload.Metadata?.Description,
            request.Payload.Metadata?.Tags.ToArray() ?? [],
            null,
            null,
            request.Payload.Metadata?.Model,
            request.Payload.Metadata?.Provider,
            [],
            [],
            Body: request.Payload.FullContent,
            Version: request.Payload.Metadata?.Version,
            InputSchema: request.Payload.Metadata?.InputSchema,
            OutputSchema: request.Payload.Metadata?.OutputSchema,
            DeclaredTools: request.Payload.Metadata?.DeclaredTools.ToArray(),
            Dependencies: request.Payload.Metadata?.Dependencies.ToArray(),
            DefaultParameters: request.Payload.Metadata?.DefaultParameters);

        return await ImportCreateStagedAsync(workspace, create, bundle.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A decoded script from an import bundle, held as raw bytes so it reaches disk exactly as exported.
    /// </summary>
    private readonly record struct ImportedScript(string FileName, byte[] Bytes);

    /// <summary>
    /// Checks the import payload against the limits export honours and decodes its scripts, so a bundle that
    /// export could not have produced is refused as <see cref="ErrorCodes.Validation.InvalidBody"/> before any
    /// staging directory exists. A script whose content is not base64 is the same refusal rather than a
    /// <see cref="FormatException"/> that would surface as a write failure.
    /// </summary>
    private static Result<IReadOnlyList<ImportedScript>> ReadImportBundle(SpellExportDto payload)
    {
        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();

        long aggregateScriptCap = ArcanumSettingClamps.MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        if (payload.FullContent is not null && System.Text.Encoding.UTF8.GetByteCount(payload.FullContent) > perFileCap)
        {
            return Result<IReadOnlyList<ImportedScript>>.Failure(
                new Error(ErrorCodes.Validation.InvalidBody, $"The spell file exceeds the {perFileCap} byte limit."));
        }

        IReadOnlyList<SpellExportScriptDto> scripts = payload.Scripts ?? [];

        if (scripts.Count > MaxSpellScriptCount)
        {
            return Result<IReadOnlyList<ImportedScript>>.Failure(
                new Error(ErrorCodes.Validation.InvalidBody, $"A spell bundle carries at most {MaxSpellScriptCount} scripts."));
        }

        List<ImportedScript> decoded = new(scripts.Count);

        long totalBytes = 0;

        foreach (SpellExportScriptDto script in scripts)
        {
            if (script?.FileName is null || script.Base64Content is null)
            {
                return Result<IReadOnlyList<ImportedScript>>.Failure(
                    new Error(ErrorCodes.Validation.InvalidBody, "Every script needs a file name and its base64 content."));
            }

            // Base64 spends four characters on three bytes, so a string this long cannot decode to a script
            // within the per-file limit even allowing for line breaks; refusing it spares the allocation.
            if (script.Base64Content.Length > perFileCap * 2)
            {
                return Result<IReadOnlyList<ImportedScript>>.Failure(
                    new Error(ErrorCodes.Validation.InvalidBody, $"A script exceeds the {perFileCap} byte limit."));
            }

            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(script.Base64Content);
            }
            catch (FormatException)
            {
                return Result<IReadOnlyList<ImportedScript>>.Failure(
                    new Error(ErrorCodes.Validation.InvalidBody, "A script's content is not valid base64."));
            }

            if (bytes.Length > perFileCap)
            {
                return Result<IReadOnlyList<ImportedScript>>.Failure(
                    new Error(ErrorCodes.Validation.InvalidBody, $"A script exceeds the {perFileCap} byte limit."));
            }

            totalBytes += bytes.Length;

            if (totalBytes > aggregateScriptCap)
            {
                return Result<IReadOnlyList<ImportedScript>>.Failure(
                    new Error(ErrorCodes.Validation.InvalidBody, $"The scripts together exceed the {aggregateScriptCap} byte limit."));
            }

            decoded.Add(new ImportedScript(script.FileName, bytes));
        }

        return Result<IReadOnlyList<ImportedScript>>.Success(decoded);
    }

    public async Task<Result<SpellSummary>> CloneAsync(string name, string? workingDirectory, CloneSpellRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to clone spells."));
        }

        string? newNameError = ValidateName(request.NewName);

        if (newNameError is not null)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.InvalidName, newNameError));
        }

        string trimmedNewName = request.NewName.Trim();

        string workspaceRoot = workingDirectory.Trim();

        string spellsRoot = Path.Combine(workspaceRoot, "spells");

        string spellDir = Path.Combine(spellsRoot, trimmedNewName);

        if (IsUnderGlobalSpellsDirectory(spellDir))
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.BuiltinReadOnly, "Cannot clone into the built-in spells directory."));
        }

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        ParsedSpell? source = FindByName(allSpells, name.Trim());

        if (source is null)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.NotFound, "No spell exists with that name in the resolved workspace."));
        }

        if (FindByName(allSpells, trimmedNewName) is not null)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.NameCollision, $"Spell \"{trimmedNewName}\" already exists in this workspace."));
        }

        string content = SpellFileParser.FormatRenamed(source, trimmedNewName);

        string? stagingDir = null;

        try
        {
            Directory.CreateDirectory(spellsRoot);

            stagingDir = Path.Combine(spellsRoot, $".staging-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingDir);

            BeforeFirstSpellWriteForTests?.Invoke();

            await File.WriteAllTextAsync(Path.Combine(stagingDir, "SPELL.md"), content, ct).ConfigureAwait(false);

            if (source.SkillMetadata is not null)
            {
                SkillMetadata clonedMetadata = source.SkillMetadata with { Name = trimmedNewName, ActiveVersion = null };

                await SkillJsonIO.WriteAsync(stagingDir, clonedMetadata, ct).ConfigureAwait(false);
            }

            if (Directory.Exists(spellDir))
            {
                return Result<SpellSummary>.Failure(
                    new Error(ErrorCodes.Spell.NameCollision, $"Spell \"{trimmedNewName}\" already exists in this workspace."));
            }

            Directory.Move(stagingDir, spellDir);

            stagingDir = null;

            AfterSpellDirectoryPublishedForTests?.Invoke();

            return Result<SpellSummary>.Success(
                await ReadPublishedSummaryAsync(workspaceRoot, trimmedNewName, content).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clone spell {SourceName} to {NewName} in {Workspace}", name, trimmedNewName, workspaceRoot);

            return Result<SpellSummary>.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDir);
        }
    }

    public async Task<Result<SpellVersionDto>> CreateVersionAsync(string name, string? workingDirectory, CreateSpellVersionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to create spell versions."));
        }

        if (!SpellVersionPathPolicy.IsValidLabel(request.Version))
        {
            return Result<SpellVersionDto>.Failure(
                new Error(ErrorCodes.Spell.InvalidVersion, $"Invalid version label \"{request.Version}\" \u2014 alphanumeric and dots only."));
        }

        string label = request.Version.Trim();

        string workspaceRoot = workingDirectory.Trim();

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        string trimmedName = name.Trim();

        ParsedSpell? workspaceSpell = FindWorkspaceSpell(allSpells, trimmedName, workingDirectory);

        if (workspaceSpell is null)
        {
            if (FindBuiltinByName(allSpells, trimmedName) is not null)
            {
                return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.BuiltinReadOnly, "Built-in spells cannot have versions written."));
            }

            return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string versionPath = Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName(label));

        try
        {
            if (File.Exists(versionPath))
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.DuplicateVersion, $"Version \"{label}\" already exists for spell \"{trimmedName}\"."));
            }

            string content = SpellFileParser.FormatWithBody(workspaceSpell, request.Body);

            BeforeFirstSpellWriteForTests?.Invoke();

            await SpellAtomicFile.WriteAllTextAsync(versionPath, content, ct).ConfigureAwait(false);

            DateTimeOffset createdAt = File.GetLastWriteTimeUtc(versionPath);

            return Result<SpellVersionDto>.Success(new SpellVersionDto(label, false, createdAt, workspaceSpell.Description));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create version {Version} for spell {SpellName} at {VersionPath}", label, trimmedName, versionPath);

            return Result<SpellVersionDto>.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
    }

    public async Task<Result<SpellVersionDto>> UpdateVersionAsync(string name, string version, string? workingDirectory, UpdateSpellVersionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to update spell versions."));
        }

        if (!SpellVersionPathPolicy.IsValidLabel(version))
        {
            return Result<SpellVersionDto>.Failure(
                new Error(ErrorCodes.Spell.InvalidVersion, $"Invalid version label \"{version}\" \u2014 alphanumeric and dots only."));
        }

        string label = version.Trim();

        string workspaceRoot = workingDirectory.Trim();

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        string trimmedName = name.Trim();

        ParsedSpell? workspaceSpell = FindWorkspaceSpell(allSpells, trimmedName, workingDirectory);

        if (workspaceSpell is null)
        {
            if (FindBuiltinByName(allSpells, trimmedName) is not null)
            {
                return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.BuiltinReadOnly, "Built-in spells cannot have versions written."));
            }

            return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string versionPath = Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName(label));

        try
        {
            if (!File.Exists(versionPath))
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.NotFound, $"Version \"{label}\" does not exist for spell \"{trimmedName}\"."));
            }

            string content = SpellFileParser.FormatWithBody(workspaceSpell, request.Body);

            BeforeFirstSpellWriteForTests?.Invoke();

            await SpellAtomicFile.WriteAllTextAsync(versionPath, content, ct).ConfigureAwait(false);

            DateTimeOffset createdAt = File.GetLastWriteTimeUtc(versionPath);

            return Result<SpellVersionDto>.Success(new SpellVersionDto(label, false, createdAt, workspaceSpell.Description));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update version {Version} for spell {SpellName} at {VersionPath}", label, trimmedName, versionPath);

            return Result<SpellVersionDto>.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
    }

    public async Task<Result<SpellVersionDetailDto>> GetVersionDetailAsync(string name, string version, string? workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<SpellVersionDetailDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string label = version.Trim();

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        string trimmedName = name.Trim();

        ParsedSpell? spell = FindByName(allSpells, trimmedName);

        if (spell is null)
        {
            return Result<SpellVersionDetailDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string? spellDir = string.IsNullOrWhiteSpace(spell.FilePath)
            ? null
            : Path.GetDirectoryName(spell.FilePath);

        if (string.IsNullOrWhiteSpace(spellDir) || !Directory.Exists(spellDir))
        {
            return Result<SpellVersionDetailDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "The spell directory does not exist."));
        }

        string? activeVersionLabel = spell.SkillMetadata?.ActiveVersion;

        string filePath;

        bool isActive;

        string responseVersion;

        if (string.Equals(label, "(active)", StringComparison.Ordinal))
        {
            filePath = Path.Combine(spellDir, "SPELL.md");

            isActive = true;

            responseVersion = activeVersionLabel ?? "(active)";
        }
        else if (!SpellVersionPathPolicy.IsValidLabel(label))
        {
            return Result<SpellVersionDetailDto>.Failure(
                new Error(ErrorCodes.Spell.InvalidVersion, $"Invalid version label \"{label}\" \u2014 alphanumeric and dots only."));
        }
        else
        {
            filePath = Path.Combine(spellDir, SpellVersionPathPolicy.BuildVersionFileName(label));

            if (!File.Exists(filePath))
            {
                return Result<SpellVersionDetailDto>.Failure(
                    new Error(ErrorCodes.Spell.NotFound, $"Version \"{label}\" does not exist for spell \"{trimmedName}\"."));
            }

            isActive = false;

            responseVersion = label;
        }

        if (!File.Exists(filePath))
        {
            return Result<SpellVersionDetailDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "The active spell file does not exist."));
        }

        ParsedSpell? parsed = await SpellScanner.LoadFullAsync(filePath, ct, GetMaxSpellFileSizeBytes()).ConfigureAwait(false);

        if (parsed is null)
        {
            return Result<SpellVersionDetailDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "The spell version file could not be read."));
        }

        DateTimeOffset createdAt;

        try
        {
            createdAt = File.GetLastWriteTimeUtc(filePath);
        }
        catch (IOException)
        {
            createdAt = DateTimeOffset.UtcNow;
        }
        catch (UnauthorizedAccessException)
        {
            createdAt = DateTimeOffset.UtcNow;
        }

        string? description = string.IsNullOrWhiteSpace(parsed.Description) ? null : parsed.Description;

        return Result<SpellVersionDetailDto>.Success(
            new SpellVersionDetailDto(responseVersion, isActive, createdAt, description, parsed.Body));
    }

    public async Task<Result<SpellVersionDto>> ActivateVersionAsync(string name, string version, string? workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to activate spell versions."));
        }

        if (!SpellVersionPathPolicy.IsValidLabel(version))
        {
            return Result<SpellVersionDto>.Failure(
                new Error(ErrorCodes.Spell.InvalidVersion, $"Invalid version label \"{version}\" \u2014 alphanumeric and dots only."));
        }

        string label = version.Trim();

        string workspaceRoot = workingDirectory.Trim();

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workingDirectory, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        string trimmedName = name.Trim();

        ParsedSpell? workspaceSpell = FindWorkspaceSpell(allSpells, trimmedName, workingDirectory);

        if (workspaceSpell is null)
        {
            if (FindBuiltinByName(allSpells, trimmedName) is not null)
            {
                return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.BuiltinReadOnly, "Built-in spells cannot have versions activated."));
            }

            return Result<SpellVersionDto>.Failure(new Error(ErrorCodes.Spell.NotFound, "Spell was not found."));
        }

        string versionPath = Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName(label));

        // What a failure part-way through has to undo: the prior-content backup this call created (one that
        // already existed is the archived snapshot of that label and is never removed) and SPELL.md once it holds
        // the activated version.
        string? createdBackupPath = null;

        bool specReplaced = false;

        try
        {
            if (!File.Exists(versionPath))
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.NotFound, $"Version \"{label}\" does not exist for spell \"{trimmedName}\"."));
            }

            // The version file is read first, through the scanner's hardened path, so a version that is a FIFO or
            // a link out of the workspace fails before the backup is written rather than leaving it half done.
            long maxSpellFileBytes = GetMaxSpellFileSizeBytes();

            string? newActiveContent = null;

            string refusal = "is not inside the workspace";

            if (WorkspacePathPolicy.RevalidatePathBeforeIo(Path.GetFullPath(workspaceRoot), versionPath))
            {
                SecureUtf8FileReadResult versionRead = await SecureFileReader
                    .ReadUtf8TextAsync(versionPath, (int)Math.Min(maxSpellFileBytes, int.MaxValue - 1), ct)
                    .ConfigureAwait(false);

                if (versionRead.Status is SecureFileReadStatus.Success)
                {
                    newActiveContent = versionRead.Text;
                }
                else
                {
                    refusal = DescribeVersionFileRefusal(versionRead.Status, maxSpellFileBytes);
                }
            }

            if (newActiveContent is null)
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.NotFound, $"Version \"{label}\" of spell \"{trimmedName}\" {refusal}."));
            }

            string? recordedActiveVersion = workspaceSpell.SkillMetadata?.ActiveVersion;

            string previousLabel = recordedActiveVersion
                ?? (File.Exists(Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName("0")))
                    ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
                    : "0");

            string previousBackupPath = Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName(previousLabel));

            // Re-activating the already-active label aliases the backup path onto the requested
            // version file. Writing the backup first would replace the archived snapshot with the
            // (possibly drifted) working copy and destroy it irrecoverably, so skip the backup and
            // report no previous label — the version -> SPELL.md copy still runs, because
            // `spell version update` deliberately leaves activeVersion pointing at an edited label.
            bool backupAliasesRequestedVersion = PathsReferToSameFile(previousBackupPath, versionPath);

            if (!backupAliasesRequestedVersion)
            {
                bool backupExisted = File.Exists(previousBackupPath);

                await SpellAtomicFile.WriteAllTextAsync(previousBackupPath, workspaceSpell.FullContent, ct).ConfigureAwait(false);

                createdBackupPath = backupExisted ? null : previousBackupPath;
            }

            await SpellAtomicFile.WriteAllTextAsync(workspaceSpell.FilePath, newActiveContent, ct).ConfigureAwait(false);

            specReplaced = true;

            SkillMetadata updatedMetadata = SkillJsonIO.SetActiveVersion(workspaceSpell, label);

            // SPELL.md now holds the activated version, so the sidecar that records which one is active is
            // finished whatever the caller does now: a cancellation between the two files would leave a spell
            // whose active content and recorded active version disagree.
            await SkillJsonIO.WriteAsync(workspaceSpell.DirectoryPath, updatedMetadata, CancellationToken.None).ConfigureAwait(false);

            DateTimeOffset activatedAt = File.GetLastWriteTimeUtc(workspaceSpell.FilePath);

            return Result<SpellVersionDto>.Success(
                new SpellVersionDto(label, true, activatedAt, workspaceSpell.Description, backupAliasesRequestedVersion ? null : previousLabel));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteCreatedBackup(createdBackupPath);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to activate version {Version} for spell {SpellName} at {VersionPath}", label, trimmedName, versionPath);

            // The caller is being told the activation did not happen: SPELL.md goes back to the working copy it
            // held, and the backup this call wrote of it is dropped so it cannot block a later activation.
            if (specReplaced)
            {
                await RestoreSpellFileAsync(workspaceSpell.FilePath, workspaceSpell.FullContent).ConfigureAwait(false);
            }

            TryDeleteCreatedBackup(createdBackupPath);

            return Result<SpellVersionDto>.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
    }

    /// <summary>
    /// Puts a spell file back to the content it held before an update or activation replaced it, after the
    /// sidecar write that completes the change failed. This is compensating work for a replace that already
    /// happened, so it does not run on the caller's token; if it cannot finish either, the spell is left holding
    /// the new content and the log says so.
    /// </summary>
    private async Task RestoreSpellFileAsync(string spellFilePath, string originalContent)
    {
        try
        {
            await SpellAtomicFile.WriteAllTextAsync(spellFilePath, originalContent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not restore {SpellPath} after a failed spell write; it is left holding the new content while its sidecar still describes the old.",
                spellFilePath);
        }
    }

    private void TryDeleteCreatedBackup(string? backupPath)
    {
        if (backupPath is null)
        {
            return;
        }

        try
        {
            File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the backup {BackupPath} a failed version activation wrote.", backupPath);
        }
    }

    /// <summary>
    /// Says why a version file could not be read for activation, so an oversize or non-UTF-8 file is not
    /// reported as though it were a link or a FIFO. The spell scanner applies the same limits, so a file these
    /// refuse is one the catalog could not load back after it had been activated.
    /// </summary>
    private static string DescribeVersionFileRefusal(SecureFileReadStatus status, long maxSpellFileBytes) =>
        status switch
        {
            SecureFileReadStatus.TooLarge => $"is larger than the {maxSpellFileBytes}-byte spell file limit",
            SecureFileReadStatus.InvalidUtf8 => "is not valid UTF-8 text",
            SecureFileReadStatus.NotFound => "does not exist",
            SecureFileReadStatus.Rejected => "is not a regular file inside the spell directory (a symbolic link, a hard-linked file, a FIFO or a device is refused)",
            _ => "could not be read",
        };

    /// <summary>
    /// Compares two spell file paths for filesystem identity. Version labels permit
    /// <c>[A-Za-z0-9.]</c>, so "1.0a" and "1.0A" name the same file on the case-insensitive
    /// defaults of macOS and Windows even though the raw labels differ ordinally.
    /// </summary>
    private static bool PathsReferToSameFile(string left, string right)
    {
        string fullLeft;

        string fullRight;

        try
        {
            fullLeft = Path.GetFullPath(left);

            fullRight = Path.GetFullPath(right);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        StringComparison cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullLeft.Equals(fullRight, cmp);
    }

    private static bool IsUnderGlobalSpellsDirectory(string candidateDir)
    {
        string globalRoot;

        try
        {
            globalRoot = Path.GetFullPath(ArcanumPaths.GlobalSpellsDirectory);
        }
        catch (Exception)
        {
            return false;
        }

        string fullCandidate;

        try
        {
            fullCandidate = Path.GetFullPath(candidateDir);
        }
        catch (Exception)
        {
            return false;
        }

        char sep = Path.DirectorySeparatorChar;

        string prefix = globalRoot.TrimEnd(sep) + sep;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullCandidate.Equals(globalRoot, cmp) || fullCandidate.StartsWith(prefix, cmp);
    }

    private async Task<Result<SpellSummary>> ImportCreateStagedAsync(
        string workspace,
        CreateSpellRequest create,
        IReadOnlyList<ImportedScript> scripts,
        CancellationToken ct)
    {
        string trimmedName = create.Name.Trim();

        string? nameError = ValidateName(trimmedName);

        if (nameError is not null)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.InvalidName, nameError));
        }

        // Import is the second writer of SPELL.md/SPELL.json and must clear the same gate as
        // CreateAsync. Without it a bundle's description/tags/model/provider reach the formatter
        // with only a Trim(), and an embedded newline becomes an extra frontmatter key — including
        // a second `name:`, which the parser lets win and which then shadows a built-in spell.
        string? frontmatterError = SpellFrontmatterValidator.ValidateCreate(create);

        if (frontmatterError is not null)
        {
            return Result<SpellSummary>.Failure(new Error("Spell.InvalidFrontmatter", frontmatterError));
        }

        int maxDeclaredTools = ArcanumSettingClamps.MaxDeclaredTools(ArcanumRuntimeDefaults.Spells.MaxDeclaredTools);

        string? skillBoundsError = SkillJsonBoundsValidator.ValidateCreate(create, maxDeclaredTools);

        if (skillBoundsError is not null)
        {
            return Result<SpellSummary>.Failure(new Error("Spell.InvalidSkillJson", skillBoundsError));
        }

        string workspaceRoot = workspace.Trim();

        // Name collisions are decided on a snapshot read under the write lock, as create does, and a built-in
        // spell's name is refused the same way: a workspace spell shadows a built-in one of the same name, so
        // importing one would let a bundle replace a built-in spell's behavior.
        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner.ScanAsync(workspace, ct, GetMaxSpellFileSizeBytes(), GetMaxSpellDeclaredTools()).ConfigureAwait(false);

        if (FindByName(allSpells, trimmedName) is ParsedSpell existing)
        {
            return Result<SpellSummary>.Failure(
                new Error(
                    ErrorCodes.Spell.NameCollision,
                    IsBuiltinSpell(existing)
                        ? "A built-in spell with that name already exists."
                        : "A spell with that name already exists in the target workspace."));
        }

        string spellsRoot = Path.Combine(workspaceRoot, "spells");

        string spellDir = Path.Combine(spellsRoot, trimmedName);

        bool hasStructured = SkillJsonIO.HasStructuredFields(create);

        string content;

        if (hasStructured
            && string.IsNullOrWhiteSpace(create.Body)
            && string.IsNullOrWhiteSpace(create.SystemPrompt)
            && string.IsNullOrWhiteSpace(create.Template))
        {
            content = SpellMarkdownGenerator.GenerateFromCreateRequest(trimmedName, create);
        }
        else
        {
            content = SpellFileParser.FormatCreate(trimmedName, create);
        }

        string? stagingDir = null;

        try
        {
            Directory.CreateDirectory(spellsRoot);

            stagingDir = Path.Combine(spellsRoot, $".staging-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingDir);

            BeforeFirstSpellWriteForTests?.Invoke();

            await File.WriteAllTextAsync(Path.Combine(stagingDir, "SPELL.md"), content, ct).ConfigureAwait(false);

            if (hasStructured)
            {
                SkillMetadata metadata = SkillJsonIO.BuildMetadataFromCreate(trimmedName, create);

                await SkillJsonIO.WriteAsync(stagingDir, metadata, ct).ConfigureAwait(false);
            }

            if (scripts.Count > 0)
            {
                string scriptsDir = Path.Combine(stagingDir, "scripts");

                Directory.CreateDirectory(scriptsDir);

                foreach (ImportedScript script in scripts)
                {
                    string safeFileName = Path.GetFileName(script.FileName);

                    if (string.IsNullOrWhiteSpace(safeFileName)
                        || !string.Equals(safeFileName, script.FileName.Trim(), StringComparison.Ordinal))
                    {
                        return Result<SpellSummary>.Failure(
                            new Error("Spell.InvalidScriptPath", "Script file names must be bare file names without path separators."));
                    }

                    string targetPath = Path.GetFullPath(Path.Combine(scriptsDir, safeFileName));

                    if (!targetPath.StartsWith(Path.GetFullPath(scriptsDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !string.Equals(targetPath, Path.GetFullPath(scriptsDir), StringComparison.Ordinal))
                    {
                        return Result<SpellSummary>.Failure(
                            new Error("Spell.InvalidScriptPath", "Script path would escape the scripts directory."));
                    }

                    await File.WriteAllBytesAsync(targetPath, script.Bytes, ct).ConfigureAwait(false);
                }
            }

            if (Directory.Exists(spellDir))
            {
                return Result<SpellSummary>.Failure(
                    new Error(ErrorCodes.Spell.NameCollision, "A spell with that name already exists in the target workspace."));
            }

            Directory.Move(stagingDir, spellDir);

            stagingDir = null;

            AfterSpellDirectoryPublishedForTests?.Invoke();

            return Result<SpellSummary>.Success(
                await ReadPublishedSummaryAsync(workspaceRoot, trimmedName, content).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import spell {SpellName} into {Workspace}", trimmedName, workspaceRoot);

            return Result<SpellSummary>.Failure(new Error("Spell.WriteFailed", WriteFailedMessage));
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDir);
        }
    }

    /// <summary>
    /// Reads the summary of a spell whose directory has just been moved into place. The spell is on disk by
    /// now, so this does not run on the caller's token: a caller that cancels at this point has still created
    /// the spell and is told so, rather than that the write failed. The read is bounded by a token of its own
    /// instead. A read-back that cannot finish, or that does not list the spell, never turns the write into a
    /// failure: the summary is built from the content that was written, which is what the catalog would have
    /// listed, and the log says the read-back could not confirm it.
    /// </summary>
    private async Task<SpellSummary> ReadPublishedSummaryAsync(string workspaceRoot, string spellName, string writtenContent)
    {
        using CancellationTokenSource bounded = new(PublishedSummaryReadTimeout);

        try
        {
            SpellSummary[] list = await ListAsync(workspaceRoot, bounded.Token).ConfigureAwait(false);

            SpellSummary? listed = list.FirstOrDefault(summary => string.Equals(summary.Name, spellName, StringComparison.OrdinalIgnoreCase));

            if (listed is not null)
            {
                return listed;
            }

            _logger.LogWarning(
                "Spell {SpellName} was written to {Workspace} but the catalog did not list it afterwards; answering from the written content.",
                spellName,
                workspaceRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Spell {SpellName} was written to {Workspace} but its summary could not be read back; answering from the written content.",
                spellName,
                workspaceRoot);
        }

        return SummarizeWrittenContent(writtenContent, spellName);
    }

    /// <summary>
    /// The summary the catalog lists for a spell, derived from the SPELL.md text that was written: the same
    /// frontmatter fields the metadata scan reads, with the spell's own name as the directory fallback.
    /// </summary>
    private static SpellSummary SummarizeWrittenContent(string writtenContent, string spellName)
    {
        SpellParseResult parsed = SpellFileParser.Parse(writtenContent, spellName);

        return new SpellSummary(
            parsed.Name,
            string.IsNullOrEmpty(parsed.Description) ? null : parsed.Description,
            SpellSource.Workspace,
            parsed.Tags,
            DeclaredTools: parsed.Tools is { Length: > 0 } tools ? tools : null);
    }

    private static void TryDeleteStagingDirectory(string? stagingDir)
    {
        if (string.IsNullOrWhiteSpace(stagingDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task<IReadOnlyList<string>> GetMcpToolNamesAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<Microsoft.Extensions.AI.AITool> tools =
                await _mcpManager.GetAvailableToolsAsync(null, ct).ConfigureAwait(false);

            return tools.Select(t => t.Name).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list MCP tools for spell validation.");

            return Array.Empty<string>();
        }
    }

    private static bool IsValidJsonObject(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object;

    private static SpellSummary ToSummary(ParsedSpell spell) =>
        MapToSummary(spell, IsBuiltinSpell(spell) ? SpellSource.Builtin : SpellSource.Workspace);

    private static SpellSummary ToSummaryWithValidity(ParsedSpell spell, HashSet<string> catalogNames)
    {
        SpellSummary summary = ToSummary(spell);

        string[]? dependencies = spell.SkillMetadata?.Dependencies?
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Select(static d => d.Trim())
            .ToArray();

        if (dependencies is not { Length: > 0 })
        {
            return summary;
        }

        string[] unresolved = dependencies
            .Where(dep => !catalogNames.Contains(dep))
            .ToArray();

        if (unresolved.Length == 0)
        {
            return summary with { IsValid = true };
        }

        return summary with { IsValid = false, UnresolvedDependencies = unresolved };
    }

    internal static SpellSummary MapToSummary(ParsedSpell spell, SpellSource source)
    {
        SkillMetadata? meta = spell.SkillMetadata;

        return new SpellSummary(
            spell.Name,
            string.IsNullOrEmpty(spell.Description) ? null : spell.Description,
            source,
            spell.Tags,
            meta?.Version,
            meta?.InputSchema,
            meta?.OutputSchema,
            meta?.DeclaredTools?.ToArray(),
            meta?.Dependencies?.ToArray());
    }

    private static SpellDetail ToDetail(ParsedSpell spell, string? workingDirectory)
    {
        bool isBuiltin = IsBuiltinSpell(spell);

        SpellSource source = isBuiltin ? SpellSource.Builtin : SpellSource.Workspace;

        SkillMetadata? meta = spell.SkillMetadata;

        return new SpellDetail(
            spell.Name,
            string.IsNullOrEmpty(spell.Description) ? null : spell.Description,
            source,
            spell.Tags,
            spell.SystemPrompt,
            spell.Template,
            string.IsNullOrEmpty(spell.Body) ? null : spell.Body,
            spell.Model,
            spell.Provider,
            spell.Tools,
            spell.RequiredMcpServers,
            isBuiltin ? null : workingDirectory,
            spell.FilePath,
            meta?.Version,
            meta?.InputSchema,
            meta?.OutputSchema,
            meta?.DeclaredTools?.ToArray(),
            meta?.Dependencies?.ToArray(),
            meta?.ActiveVersion);
    }

    internal static bool IsBuiltinSpell(ParsedSpell spell)
    {
        string globalRoot;

        try
        {
            globalRoot = Path.GetFullPath(ArcanumPaths.GlobalSpellsDirectory);
        }
        catch (Exception)
        {
            return false;
        }

        string filePath;

        try
        {
            filePath = Path.GetFullPath(spell.FilePath);
        }
        catch (Exception)
        {
            return false;
        }

        char sep = Path.DirectorySeparatorChar;

        string prefix = globalRoot.TrimEnd(sep) + sep;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return filePath.Equals(globalRoot, cmp) || filePath.StartsWith(prefix, cmp);
    }

    private static ParsedSpell? FindByName(IReadOnlyList<ParsedSpell> spells, string name)
    {
        for (int i = 0; i < spells.Count; i++)
        {
            if (string.Equals(spells[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return spells[i];
            }
        }

        return null;
    }

    private static ParsedSpell? FindBuiltinByName(IReadOnlyList<ParsedSpell> spells, string name)
    {
        for (int i = 0; i < spells.Count; i++)
        {
            ParsedSpell spell = spells[i];

            if (string.Equals(spell.Name, name, StringComparison.OrdinalIgnoreCase) && IsBuiltinSpell(spell))
            {
                return spell;
            }
        }

        return null;
    }

    private static ParsedSpell? FindWorkspaceSpell(IReadOnlyList<ParsedSpell> spells, string name, string workingDirectory)
    {
        for (int i = 0; i < spells.Count; i++)
        {
            ParsedSpell spell = spells[i];

            if (!string.Equals(spell.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsBuiltinSpell(spell))
            {
                return spell;
            }
        }

        return null;
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Spell name is required.";
        }

        string trimmed = name.Trim();

        if (!ValidNameRegex.IsMatch(trimmed))
        {
            return "Spell name must contain only letters, digits, hyphens, and underscores.";
        }

        return null;
    }

    internal static bool TryResolveDeleteTarget(
        string workingDirectory,
        string trimmedName,
        ParsedSpell workspaceSpell,
        out string spellDirectory,
        out Error error)
    {
        spellDirectory = string.Empty;

        error = Error.None;

        string workspaceRoot;

        try
        {
            workspaceRoot = Path.GetFullPath(workingDirectory.Trim());
        }
        catch (Exception)
        {
            error = new Error(ErrorCodes.Spell.InvalidWorkspace, "The workspace directory could not be resolved.");

            return false;
        }

        string candidateDir;

        try
        {
            candidateDir = Path.GetFullPath(workspaceSpell.DirectoryPath);
        }
        catch (Exception)
        {
            error = new Error("Spell.UnsafeDelete", "The spell directory could not be resolved.");

            return false;
        }

        if (WorkspaceRootPolicy.IsSamePath(candidateDir, workspaceRoot))
        {
            error = new Error(
                "Spell.UnsafeDelete",
                "Cannot delete a spell whose directory is the workspace root.");

            return false;
        }

        if (!WorkspaceRootPolicy.IsStrictChildPath(workspaceRoot, candidateDir))
        {
            error = new Error(
                "Spell.UnsafeDelete",
                "The spell directory is outside the workspace root.");

            return false;
        }

        string canonicalDir;

        try
        {
            canonicalDir = Path.GetFullPath(Path.Combine(workspaceRoot, "spells", trimmedName));
        }
        catch (Exception)
        {
            error = new Error("Spell.UnsafeDelete", "The managed spell directory could not be resolved.");

            return false;
        }

        if (WorkspaceRootPolicy.IsSamePath(candidateDir, canonicalDir))
        {
            spellDirectory = candidateDir;

            return true;
        }

        string folderName = Path.GetFileName(
            candidateDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(folderName, trimmedName, StringComparison.OrdinalIgnoreCase))
        {
            spellDirectory = candidateDir;

            return true;
        }

        error = new Error(
            "Spell.UnsafeDelete",
            "Spell can only be deleted from workspace/spells/{name} or a folder matching the spell name.");

        return false;
    }

    private static string GetWorkspaceLockKey(string workspaceRoot)
    {
        string key;

        try
        {
            key = Path.GetFullPath(workspaceRoot.Trim());
        }
        catch (Exception)
        {
            key = workspaceRoot.Trim();
        }

        return key;
    }

    /// <summary>
    /// Stat gate for a file the export is about to read: inside the workspace root when one applies, and an
    /// unaliased regular file. A FIFO or device stats as length 0, so the length alone proves nothing.
    /// </summary>
    private static bool TryGetExportableFileLength(string filePath, string? workspaceRoot, out long length)
    {
        length = 0L;

        return (workspaceRoot is null || WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, filePath))
            && SpellScanner.TryGetRegularFileLength(filePath, out length);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidNamePattern();
}
