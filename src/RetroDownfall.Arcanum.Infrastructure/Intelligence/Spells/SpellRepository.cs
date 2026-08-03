using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal sealed partial class SpellRepository : ISpellRepository
{

    private static readonly Regex ValidNameRegex = ValidNamePattern();

    private readonly ILogger<SpellRepository> _logger;

    private readonly KeyedLock<string> _workspaceLocks = new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IMcpConnectionManager _mcpManager;

    private readonly SpellSearchService _searchService;

    private readonly IOptionsMonitor<ArcanumSettings> _settingsMonitor;

    public SpellRepository(
        ILogger<SpellRepository> logger,
        IServiceScopeFactory scopeFactory,
        IMcpConnectionManager mcpManager,
        IOptionsMonitor<ArcanumSettings> settingsMonitor)
    {
        _logger = logger;

        _scopeFactory = scopeFactory;

        _mcpManager = mcpManager;

        _settingsMonitor = settingsMonitor;

        _searchService = new SpellSearchService(settingsMonitor);
    }

    private long GetMaxSpellFileSizeBytes() =>
        ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(_settingsMonitor.CurrentValue);

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

        string workspaceRoot = workingDirectory.Trim();

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

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        string? stagingDir = null;

        try
        {
            Directory.CreateDirectory(spellsRoot);

            stagingDir = Path.Combine(spellsRoot, $".staging-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingDir);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create spell {SpellName} at {SpellPath}", trimmedName, spellFile);

            return Result.Failure(new Error("Spell.WriteFailed", ex.Message));
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

        string workspaceRoot = workingDirectory.Trim();

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        try
        {
            await SpellAtomicFile.WriteAllTextAsync(workspaceSpell.FilePath, content, ct).ConfigureAwait(false);

            if (SkillJsonIO.HasStructuredFields(request) || workspaceSpell.SkillMetadata is not null)
            {
                SkillMetadata metadata = SkillJsonIO.MergeMetadata(workspaceSpell, request);

                await SkillJsonIO.WriteAsync(workspaceSpell.DirectoryPath, metadata, ct).ConfigureAwait(false);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update spell {SpellName} at {SpellPath}", trimmedName, workspaceSpell.FilePath);

            return Result.Failure(new Error("Spell.WriteFailed", ex.Message));
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

        string workspaceRoot = workingDirectory.Trim();

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete spell {SpellName} at {SpellPath}", trimmedName, workspaceSpell.FilePath);

            return Result.Failure(new Error("Spell.WriteFailed", ex.Message));
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

        ArcanumSettings settings = _settingsMonitor.CurrentValue;

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings);

        // Spell export has a code-owned aggregate script-byte envelope. Reuse the clamped workspace
        // read-size cap so a single export cannot stream unbounded content.
        long aggregateScriptCap = ArcanumSettingClamps.MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        SkillMetadata? metadata = null;

        string? sidecarPath = SkillJsonIO.ResolveSidecarPath(dir);

        if (sidecarPath is not null)
        {
            if (TryGetFileLength(sidecarPath, out long sidecarLength) && sidecarLength > perFileCap)
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
                    string json = await File.ReadAllTextAsync(sidecarPath, ct).ConfigureAwait(false);

                    metadata = JsonSerializer.Deserialize(json, Core.Serialization.TheForgeJsonContext.Default.SkillMetadata);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }

        string fullContent = await File.ReadAllTextAsync(detail.FilePath, ct).ConfigureAwait(false);

        var scripts = new List<SpellExportScriptDto>();

        string scriptsDir = Path.Combine(dir, "scripts");

        if (Directory.Exists(scriptsDir))
        {
            long totalScriptBytes = 0;

            foreach (string path in Directory.EnumerateFiles(scriptsDir))
            {
                if (!TryGetFileLength(path, out long fileLength))
                {
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

                    break;
                }

                byte[] bytes;

                try
                {
                    bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                scripts.Add(new SpellExportScriptDto(Path.GetFileName(path), Convert.ToBase64String(bytes)));

                totalScriptBytes += fileLength;
            }
        }

        return new SpellExportDto(metadata, fullContent, scripts);
    }

    public async Task<Result<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken ct)
    {
        string? workspace = request.Workspace;

        if (string.IsNullOrWhiteSpace(workspace))
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required to import spells."));
        }

        string name = request.Payload.Metadata?.Name
            ?? SpellFileParser.Parse(request.Payload.FullContent, "imported").Name;

        SpellDetail? existing = await GetAsync(name, workspace, ct).ConfigureAwait(false);

        if (existing is not null && existing.Source != SpellSource.Builtin)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.NameCollision, "A spell with that name already exists in the target workspace."));
        }

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

        Result<SpellSummary> createResult = await ImportCreateStagedAsync(workspace, create, request.Payload.Scripts, ct).ConfigureAwait(false);

        if (createResult.IsFailure)
        {
            return Result<SpellSummary>.Failure(createResult.Error);
        }

        return createResult;
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

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        string? stagingDir = null;

        try
        {
            Directory.CreateDirectory(spellsRoot);

            stagingDir = Path.Combine(spellsRoot, $".staging-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingDir);

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

            SpellSummary[] list = await ListAsync(workspaceRoot, ct).ConfigureAwait(false);

            SpellSummary? summary = list.FirstOrDefault(s => string.Equals(s.Name, trimmedNewName, StringComparison.OrdinalIgnoreCase));

            if (summary is null)
            {
                return Result<SpellSummary>.Failure(new Error("Spell.ImportFailed", "Spell was cloned but could not be listed."));
            }

            return Result<SpellSummary>.Success(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clone spell {SourceName} to {NewName} in {Workspace}", name, trimmedNewName, workspaceRoot);

            return Result<SpellSummary>.Failure(new Error("Spell.WriteFailed", ex.Message));
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

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        try
        {
            if (File.Exists(versionPath))
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.DuplicateVersion, $"Version \"{label}\" already exists for spell \"{trimmedName}\"."));
            }

            string content = SpellFileParser.FormatWithBody(workspaceSpell, request.Body);

            await SpellAtomicFile.WriteAllTextAsync(versionPath, content, ct).ConfigureAwait(false);

            DateTimeOffset createdAt = File.GetLastWriteTimeUtc(versionPath);

            return Result<SpellVersionDto>.Success(new SpellVersionDto(label, false, createdAt, workspaceSpell.Description));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create version {Version} for spell {SpellName} at {VersionPath}", label, trimmedName, versionPath);

            return Result<SpellVersionDto>.Failure(new Error("Spell.WriteFailed", ex.Message));
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

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        try
        {
            if (!File.Exists(versionPath))
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.NotFound, $"Version \"{label}\" does not exist for spell \"{trimmedName}\"."));
            }

            string content = SpellFileParser.FormatWithBody(workspaceSpell, request.Body);

            await SpellAtomicFile.WriteAllTextAsync(versionPath, content, ct).ConfigureAwait(false);

            DateTimeOffset createdAt = File.GetLastWriteTimeUtc(versionPath);

            return Result<SpellVersionDto>.Success(new SpellVersionDto(label, false, createdAt, workspaceSpell.Description));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update version {Version} for spell {SpellName} at {VersionPath}", label, trimmedName, versionPath);

            return Result<SpellVersionDto>.Failure(new Error("Spell.WriteFailed", ex.Message));
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

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        try
        {
            if (!File.Exists(versionPath))
            {
                return Result<SpellVersionDto>.Failure(
                    new Error(ErrorCodes.Spell.NotFound, $"Version \"{label}\" does not exist for spell \"{trimmedName}\"."));
            }

            string? recordedActiveVersion = workspaceSpell.SkillMetadata?.ActiveVersion;

            string previousLabel = recordedActiveVersion
                ?? (File.Exists(Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName("0")))
                    ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
                    : "0");

            string previousBackupPath = Path.Combine(workspaceSpell.DirectoryPath, SpellVersionPathPolicy.BuildVersionFileName(previousLabel));

            await SpellAtomicFile.WriteAllTextAsync(previousBackupPath, workspaceSpell.FullContent, ct).ConfigureAwait(false);

            string newActiveContent = await File.ReadAllTextAsync(versionPath, ct).ConfigureAwait(false);

            await SpellAtomicFile.WriteAllTextAsync(workspaceSpell.FilePath, newActiveContent, ct).ConfigureAwait(false);

            SkillMetadata updatedMetadata = SkillJsonIO.SetActiveVersion(workspaceSpell, label);

            await SkillJsonIO.WriteAsync(workspaceSpell.DirectoryPath, updatedMetadata, ct).ConfigureAwait(false);

            DateTimeOffset activatedAt = File.GetLastWriteTimeUtc(workspaceSpell.FilePath);

            return Result<SpellVersionDto>.Success(new SpellVersionDto(label, true, activatedAt, workspaceSpell.Description, previousLabel));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to activate version {Version} for spell {SpellName} at {VersionPath}", label, trimmedName, versionPath);

            return Result<SpellVersionDto>.Failure(new Error("Spell.WriteFailed", ex.Message));
        }
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
        IReadOnlyList<SpellExportScriptDto> scripts,
        CancellationToken ct)
    {
        string trimmedName = create.Name.Trim();

        string? nameError = ValidateName(trimmedName);

        if (nameError is not null)
        {
            return Result<SpellSummary>.Failure(new Error(ErrorCodes.Spell.InvalidName, nameError));
        }

        string workspaceRoot = workspace.Trim();

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

        using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(GetWorkspaceLockKey(workspaceRoot), ct).ConfigureAwait(false);

        string? stagingDir = null;

        try
        {
            Directory.CreateDirectory(spellsRoot);

            stagingDir = Path.Combine(spellsRoot, $".staging-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingDir);

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

                foreach (SpellExportScriptDto script in scripts)
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

                    byte[] bytes = Convert.FromBase64String(script.Base64Content);

                    await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
                }
            }

            if (Directory.Exists(spellDir))
            {
                return Result<SpellSummary>.Failure(
                    new Error(ErrorCodes.Spell.NameCollision, "A spell with that name already exists in the target workspace."));
            }

            Directory.Move(stagingDir, spellDir);

            stagingDir = null;

            SpellSummary[] list = await ListAsync(workspace, ct).ConfigureAwait(false);

            SpellSummary? summary = list.FirstOrDefault(s => string.Equals(s.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            if (summary is null)
            {
                return Result<SpellSummary>.Failure(new Error("Spell.ImportFailed", "Spell was created but could not be listed."));
            }

            return Result<SpellSummary>.Success(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import spell {SpellName} into {Workspace}", trimmedName, workspaceRoot);

            return Result<SpellSummary>.Failure(new Error("Spell.WriteFailed", ex.Message));
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDir);
        }
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

    private static bool TryGetFileLength(string filePath, out long length)
    {
        try
        {
            length = new FileInfo(filePath).Length;

            return true;
        }
        catch (IOException)
        {
            length = 0L;

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            length = 0L;

            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidNamePattern();

}
