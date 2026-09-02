using System.Text;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Compensating work — a rollback, a detach, a lease surrender, a post-commit proof — must run on
/// <c>CancellationToken.None</c>, because the caller's token is often the reason the compensation is
/// running. This inventory finds every <c>RollbackAsync</c>, <c>ExecuteNonQueryAsync</c> and
/// <c>TryTransitionAsync</c> call inside a <c>catch</c> or <c>finally</c> block that forwards a caller
/// token, and pins the sites that are still allowed while their owning packets remove them.
/// </summary>
public sealed class CompensationCancellationTokenTests
{

    /// <summary>Sites still on the caller's token. Every packet that fixes one deletes its line.</summary>
    internal static readonly string[] AllowedSites =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnBegin.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStore.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureKernel.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileWriteIntentRecoveryService.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantLocalErasureStorageHealth.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreCovenantCoordinator.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Backup/RestoreStagingManagedAuthoritySanitizationCapability.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs",

        "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs",
    ];

    private static readonly Regex CompensationOnCallerToken = new(
        @"(?:RollbackAsync|ExecuteNonQueryAsync|TryTransitionAsync)\((?:[^()]*,\s*)?(?:cancellationToken|ct|token)\)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Compensation_runs_on_no_token_outside_the_allowed_sites()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {

            foreach (string block in CompensationBlocks.Of(source.Text))
            {

                if (CompensationOnCallerToken.IsMatch(block) && !AllowedSites.Contains(source.RelativePath))
                {

                    offenders.Add($"{source.RelativePath} compensates on the caller's token");

                }

            }

        }

        // Named rather than counted, and de-duplicated: one file can hold many compensating handlers,
        // and Assert.Empty would spend its five-entry budget printing the same truncated path over and
        // over while the file that actually regressed sat below the ellipsis.
        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

    }

}

/// <summary>
/// The <c>catch</c> and <c>finally</c> block bodies of one authored source file.
/// </summary>
/// <remarks>
/// Compensation is not a property of a call, it is a property of where the call sits. The same
/// <c>ExecuteNonQueryAsync(cancellationToken)</c> is correct in the body of an operation and wrong in
/// the <c>catch</c> that is unwinding it, so an inventory that matched the call anywhere in a file
/// would report every ordinary write in the tree and be switched off within a week.
/// </remarks>
internal static class CompensationBlocks
{

    private static readonly Regex Handler = new(
        @"\b(?:catch|finally)\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Yields the braced body of every <c>catch</c> and <c>finally</c> in the supplied source.
    /// </summary>
    internal static IReadOnlyList<string> Of(string text)
    {

        List<string> blocks = [];

        foreach (Match handler in Handler.Matches(text))
        {

            int opening = text.IndexOf('{', handler.Index + handler.Length);

            if (opening < 0)
            {

                continue;

            }

            blocks.Add(BracedRun(text, opening));

        }

        return blocks;

    }

    /// <summary>
    /// The text from an opening brace to the one that closes it.
    /// </summary>
    /// <remarks>
    /// Depth-counted rather than read to the next <c>}</c>: a handler that opens a transaction scope,
    /// a <c>using</c> body or a nested <c>try</c> would otherwise be cut off at its first inner close,
    /// and every compensating call after that point would go unseen — which is exactly where the
    /// interesting ones live.
    /// </remarks>
    private static string BracedRun(string text, int opening)
    {

        StringBuilder run = new();

        int depth = 0;

        for (int index = opening; index < text.Length; index++)
        {

            _ = run.Append(text[index]);

            if (text[index] == '{')
            {

                depth++;

            }
            else if (text[index] == '}')
            {

                depth--;

                if (depth == 0)
                {

                    break;

                }

            }

        }

        return run.ToString();

    }

}
