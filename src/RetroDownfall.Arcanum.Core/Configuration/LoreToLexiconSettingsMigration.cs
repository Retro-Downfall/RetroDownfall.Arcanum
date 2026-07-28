using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// One-shot normalize for Lore→Lexicon renames documented in the README: maps
/// <c>delete_lore</c> → <c>delete_lexicon</c> in ForbiddenArts, and when operators had
/// <c>EnableLoreSystem: false</c> without turning Lexicon off, mirrors that into
/// <c>EnableLexiconSystem</c>.
/// </summary>
public static class LoreToLexiconSettingsMigration
{

    public const string LegacyDeleteLoreTool = "delete_lore";

    public const string DeleteLexiconTool = "delete_lexicon";

    /// <summary>
    /// Applies in-memory migration onto the bound options instance. Returns <see langword="true"/>
    /// when any setting changed.
    /// </summary>
    public static bool TryMigrateInPlace(ArcanumSettings settings, ILogger? logger = null)
    {

        ArgumentNullException.ThrowIfNull(settings);

        bool changed = false;

        WardSettings ward = settings.Ward;

        IReadOnlyList<string> arts = ward.ForbiddenArts;

        bool hasLegacyDelete = arts.Any(static a =>
            string.Equals(a, LegacyDeleteLoreTool, StringComparison.OrdinalIgnoreCase));

        bool hasDeleteLexicon = arts.Any(static a =>
            string.Equals(a, DeleteLexiconTool, StringComparison.OrdinalIgnoreCase));

        if (hasLegacyDelete)
        {

            List<string> rewritten = new(arts.Count);

            foreach (string art in arts)
            {

                if (string.Equals(art, LegacyDeleteLoreTool, StringComparison.OrdinalIgnoreCase))
                {

                    if (!hasDeleteLexicon
                        && !rewritten.Exists(static a =>
                            string.Equals(a, DeleteLexiconTool, StringComparison.OrdinalIgnoreCase)))
                    {

                        rewritten.Add(DeleteLexiconTool);

                    }

                    continue;

                }

                rewritten.Add(art);

            }

            SetInitProperty(settings, nameof(ArcanumSettings.Ward), ward with { ForbiddenArts = rewritten });

            changed = true;

            logger?.LogWarning(
                "Migrated ForbiddenArts: '{Legacy}' → '{Current}'. Persist arcanum.json to keep this change.",
                LegacyDeleteLoreTool,
                DeleteLexiconTool);

        }

        IntelligenceSettings intelligence = settings.Intelligence;

        // Option A: Lore tools are gone. If the operator disabled Lore and Lexicon is still enabled,
        // treat that as intent to keep model-writable memory off.
        if (!intelligence.EnableLoreSystem && intelligence.EnableLexiconSystem)
        {

            SetInitProperty(
                settings,
                nameof(ArcanumSettings.Intelligence),
                intelligence with { EnableLexiconSystem = false });

            changed = true;

            logger?.LogWarning(
                "EnableLoreSystem is false but EnableLexiconSystem was true; disabling EnableLexiconSystem to honor the prior memory kill-switch. Set EnableLexiconSystem explicitly and persist arcanum.json.");

        }

        return changed;

    }

    private static void SetInitProperty(ArcanumSettings target, string propertyName, object value)
    {

        PropertyInfo? property = typeof(ArcanumSettings).GetProperty(propertyName);

        if (property is null)
        {

            throw new InvalidOperationException($"ArcanumSettings.{propertyName} was not found.");

        }

        property.SetValue(target, value);

    }

}

/// <summary>
/// Runs <see cref="LoreToLexiconSettingsMigration"/> after configuration bind.
/// </summary>
public sealed class LoreToLexiconSettingsPostConfigure : IPostConfigureOptions<ArcanumSettings>
{

    private readonly ILogger<LoreToLexiconSettingsPostConfigure> _logger;

    public LoreToLexiconSettingsPostConfigure(ILogger<LoreToLexiconSettingsPostConfigure> logger)
    {

        _logger = logger;

    }

    public void PostConfigure(string? name, ArcanumSettings options) =>
        LoreToLexiconSettingsMigration.TryMigrateInPlace(options, _logger);

}
