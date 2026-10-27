using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CovenantRetentionTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_counts_unfolded_disclosure_history_even_without_the_folded_state_table(bool removeFoldedState)
    {
        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await PreFoldDisclosureHistory.InsertAsync(connection, new CovenantDisclosureDraft(
            Guid.NewGuid(), CovenantDisclosureSubjectKind.Turn, Guid.NewGuid(),
            CovenantTask6Fixture.D(76), CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable, CovenantTask6Fixture.D(80),
            PreFoldDisclosureHistory.Sensitivity.Digest, null, CovenantTask6Fixture.D(82), null,
            new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            1, CancellationToken.None);

        if (removeFoldedState) await ExecuteAsync(connection, "DROP TABLE external_disclosure_state;", CancellationToken.None);

        var status = await CreateService(covenantGate: new RecordingCovenantOperationGate()).GetStatusAsync();

        Assert.NotNull(status.Covenant);

        Assert.Equal(removeFoldedState ? 1 : 4, status.Covenant.PossibleDisclosures);

        Assert.Equal(CovenantDisclosureCountKind.LowerBound, status.Covenant.DisclosureCountKind);
    }

    [SkippableTheory]
    [InlineData("Admission")]
    [InlineData("Revalidation")]
    [InlineData("DisclosureExposure")]
    public async Task Status_preserves_Covenant_inventory_refusal_without_inventing_zero_counts(string phase)
    {
        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        if (phase == "DisclosureExposure") await DamageDisclosureExposureAsync();

        Error failure = new(ErrorCodes.Covenant.Unavailable, "PRIVATE-inventory /private/secret/source");

        RecordingCovenantOperationGate gate = new()
        {
            InstallationReadFailure = phase == "Admission" ? failure : null,
            RevalidationFailure = phase == "Revalidation" ? failure : null,
        };

        TestCapturingLogger<DataRetentionService> log = new();

        DataRetentionStatus status = await CreateService(covenantGate: gate, logger: log).GetStatusAsync();

        Assert.Null(status.Covenant);

        Assert.DoesNotContain(status.Items, item => item.DataClass == RetentionDataClass.Covenant);

        Assert.NotEmpty(status.Items);

        string code = phase == "DisclosureExposure" ? ErrorCodes.Covenant.IntegrityFailure : failure.Code;

        string json = JsonSerializer.Serialize(status, ArcanumJsonContext.Default.DataRetentionStatus);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("covenantInventoryErrorCode", out JsonElement recorded), json);

        Assert.Equal(code, recorded.GetString());

        TestLogEntry diagnostic = Assert.Single(log.Entries, entry => entry.Properties!.Any(property => property.Key == "DiagnosticStage"));

        Assert.Equal(phase, Assert.Single(diagnostic.Properties!, property => property.Key == "DiagnosticStage").Value);

        Assert.Equal(code, Assert.Single(diagnostic.Properties!, property => property.Key == "ErrorCode").Value);

        Assert.Null(diagnostic.Exception);

        Assert.DoesNotContain("PRIVATE-inventory", json + diagnostic.Message);

        Assert.DoesNotContain("/private/secret/source", json + diagnostic.Message);

        Assert.Equal(0, gate.LiveLeases);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_inventory_refusal_preserves_the_actual_disclosure_failure(bool apply)
    {
        RequireSqlCipher();

        await SeedCovenantFamilyAsync(CancellationToken.None);

        await DamageDisclosureExposureAsync();

        RecordingCovenantOperationGate gate = new();

        DataRetentionService service = CreateService(covenantGate: gate);

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        Error failure;

        if (apply)
        {
            var result = await service.ApplyAsync(new DataRetentionApplyRequest(request));

            Assert.True(result.IsFailure);

            failure = result.Error;
        }
        else
        {
            var result = await service.PlanAdmissionAsync(request);

            if (result.IsSuccess && result.Value.ReadLease is { } held) await held.DisposeAsync();

            Assert.True(result.IsFailure);

            failure = result.Error;
        }

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, failure.Code);

        Assert.Equal("The nonrevocable Covenant disclosure exposure could not be folded safely.", failure.Message);

        Assert.Equal(0, gate.LiveLeases);

        Assert.True(await ScalarAsync("SELECT COUNT(*) FROM covenant_entries", CancellationToken.None) > 0);
    }

    private Task DamageDisclosureExposureAsync() => ExecuteAsync(
        (SqliteConnection)_db!.Database.GetDbConnection(),
        "PRAGMA ignore_check_constraints = ON; UPDATE external_disclosure_state SET CountKindCode = 99 WHERE RevocabilityCode = 2; PRAGMA ignore_check_constraints = OFF;",
        CancellationToken.None);
}
