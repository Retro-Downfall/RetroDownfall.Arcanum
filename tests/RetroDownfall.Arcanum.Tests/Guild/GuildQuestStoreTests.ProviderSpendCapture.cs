using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    private const string SpendModel = "provider/model";

    private const string SpendSecret = "private-provider-key-sentinel";

    [Fact]
    public async Task Source_verification_survives_reopened_connection_and_keeps_credential_private()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        GuildSpendSourceCredential credential = scenario.Credential();

        var verified = await scenario.VerifyAsync(credential);

        Assert.True(verified.IsSuccess);

        Assert.DoesNotContain(SpendSecret, credential.ToString());

        Assert.DoesNotContain(SpendSecret, verified.Value.ToString());
    }

    [Theory]
    [InlineData("credential", "Guild.SpendCredentialChanged")]
    [InlineData("missingCredential", "Guild.SpendCredentialChanged")]
    [InlineData("missingSource", "Guild.SpendSourceMissing")]
    [InlineData("model", "Guild.SpendConfigurationChanged")]
    [InlineData("type", "Guild.SpendConfigurationChanged")]
    [InlineData("provider", "Guild.SpendConfigurationChanged")]
    [InlineData("endpoint", "Guild.SpendConfigurationChanged")]
    public async Task Source_verification_reports_ineligibility_without_sensitive_values(string change, string expected)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        ProviderSettings provider = scenario.Settings.Providers[0] with { };

        string model = SpendModel;

        string? secret = SpendSecret;

        Guid effect = scenario.EffectId;

        switch (change)
        {
            case "credential": secret = "different-key"; break;
            case "missingCredential": secret = null; break;
            case "missingSource": effect = Guid.NewGuid(); break;
            case "model": model = "other-model"; break;
            case "type": provider.Type = AiProviderKind.CodexCli; break;
            case "provider": provider.Name = "other-provider"; break;
            case "endpoint": provider.Endpoint = "https://other.example/v1"; break;
            default: throw new InvalidOperationException("Unexpected fixture change.");
        }

        var result = await scenario.VerifyAsync(new GuildSpendSourceCredential(provider, model, secret), effect);

        Assert.True(result.IsFailure);

        Assert.Equal(expected, result.Error.Code);

        Assert.DoesNotContain("different-key", result.Error.Message);

        Assert.DoesNotContain(effect.ToString(), result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("rotated")]
    [InlineData("missing")]
    public async Task Source_verification_refuses_retired_or_missing_authority(string change)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection reopened = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = reopened.BeginTransaction();

        if (change is "rotated" or "missing")
        {
            await using SqliteCommand command = reopened.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = change == "rotated"
                ? "UPDATE guild_spend_authority_state SET AuthorityEpoch=$epoch WHERE StateKey=1"
                : "DELETE FROM guild_spend_authority_state WHERE StateKey=1";

            if (change == "rotated") command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D").ToUpperInvariant());

            await command.ExecuteNonQueryAsync();
        }

        Guid generation = change == "generation" ? Guid.NewGuid() : scenario.Fixture.Host.InstallationGeneration;

        var result = await scenario.Credential().VerifyWithinAsync(reopened, transaction, scenario.EffectId, generation, default);

        Assert.True(result.IsFailure);

        Assert.Equal("Guild.SpendAuthorityRetired", result.Error.Code);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Guild_sdk_call_completes_without_cost_authority_or_source_capture()
    {
        const string model = "provider/model";

        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteCommand removeAuthority = connection.CreateCommand())
        {
            removeAuthority.CommandText = "DELETE FROM guild_spend_authority_state";

            await removeAuthority.ExecuteNonQueryAsync();
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new()
        {
            DefaultModel = model,
            Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [model] }],
        };

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", model, [], 30);

        var bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

        using GuildWireCapture transport = new(model);

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new SpendCaptureKeys());

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients);

            var result = await runtime.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal(1, transport.Calls);

            await using SqliteCommand source = connection.CreateCommand();

            source.CommandText = """
                SELECT SourceKindCode, CredentialFingerprint, Endpoint, AuthorityEpoch, GenerationId,
                       InitialSnapshot IS NOT NULL AND InitialOutcome IS NOT NULL AND InitialEventSequence IS NOT NULL
                FROM guild_spend_sources
                """;

            await using SqliteDataReader row = await source.ExecuteReaderAsync();

            Assert.True(await row.ReadAsync());
            Assert.Equal(0, row.GetInt32(0));
            Assert.True(row.IsDBNull(1) && row.IsDBNull(2) && row.IsDBNull(3) && row.IsDBNull(4));
            Assert.True(row.GetBoolean(5));
            Assert.False(await row.ReadAsync());
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    /// <summary>Stores a historical Guild provider effect and source for preservation and compatibility tests.</summary>
    private sealed class SpendScenario : IAsyncDisposable
    {
        internal Fixture Fixture { get; private set; } = null!;

        internal SqliteConnection Connection { get; private set; } = null!;

        private ArcanumDbContext _db = null!;

        internal ArcanumSettings Settings { get; private set; } = null!;

        internal Guid EffectId { get; private set; }

        internal GuildQuestDriver Driver { get; private set; } = null!;

        internal Guid QuestId { get; private set; }

        internal Guid DispatchId { get; private set; }

        internal Guid ApprenticeId { get; private set; }

        internal static async Task<SpendScenario> CreateAsync(bool enableDailyBudget = false, Fixture? fixture = null)
        {
            SpendScenario scenario = new();

            try
            {
                scenario.Fixture = fixture ?? await Fixture.CreateAsync();

                scenario.Connection = await scenario.Fixture.OpenAsync();

                scenario._db = GrimoireFixture.CreateContext(scenario.Connection);

                scenario.Settings = new()
                {
                    DefaultModel = SpendModel,
                    Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [SpendModel] }],
                };

                BudgetReservationService daily = new(scenario._db);

                scenario.Fixture.UseBudgetParticipant(new GuildBudgetLedger(daily));

                GuildQuestStored quest = (await scenario.Fixture.Store.IssueAsync(scenario.Fixture.Caller, scenario.Fixture.Identity,
                    scenario.Fixture.Request, default)).Value;

                GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", SpendModel, [], 30);

                var bound = await scenario.Fixture.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
                    (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

                Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

                scenario.ApprenticeId = bound.Value.ApprenticeId;

                GuildQuestDriver driver = await scenario.Fixture.DriverAsync(quest);

                scenario.Driver = driver;

                scenario.QuestId = quest.Quest.Id;

                GuildQuestDispatchRecord dispatch = (await scenario.Fixture.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
                    ReceiptDigest("provider-spend"), quest.Quest.RunId, false, default)).Value;

                scenario.DispatchId = dispatch.DispatchId;

                GuildLocalInferenceExecution execution = new(scenario.Fixture.Store, driver, dispatch);

                DateTimeOffset started = DateTimeOffset.UtcNow;

                Guid? reservation = enableDailyBudget
                    ? (await LegacyBudgetFixture.SeedAsync(scenario._db,
                        new(quest.Quest.RunId, 1m, started.AddHours(1), BudgetReservationService.UtcBudgetPeriod(started)))).Id
                    : null;

                GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
                    GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), reservation, 1m, 1000);

                BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "router", SpendModel, "guild",
                    started, started.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

                var captured = await scenario.Fixture.Store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
                    _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                    spendSource: scenario.Credential(), spendPricing: "{}");

                Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

                scenario.EffectId = effect.EffectId;

                return scenario;
            }
            catch
            {
                await scenario.DisposeAsync();

                throw;
            }
        }

        internal GuildSpendSourceCredential Credential() => new(Settings.Providers[0], SpendModel, SpendSecret);

        internal async Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendSourceCredential.VerifiedSource>> VerifyAsync(
            GuildSpendSourceCredential credential, Guid? effect = null)
        {
            await using SqliteConnection reopened = await Fixture.OpenAsync();

            await using SqliteTransaction transaction = reopened.BeginTransaction();

            var result = await credential.VerifyWithinAsync(reopened, transaction, effect ?? EffectId, Fixture.Host.InstallationGeneration, default);

            await transaction.RollbackAsync();

            return result;
        }

        internal async Task<GuildSpendSourceCredential.VerifiedSource> VerifiedAsync()
        {
            var result = await VerifyAsync(Credential());

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            return result.Value;
        }

        public async ValueTask DisposeAsync()
        {
            if (_db is not null) await _db.DisposeAsync();

            if (Connection is not null) await Connection.DisposeAsync();

            if (Fixture is not null) await Fixture.DisposeAsync();
        }
    }

    private const string DefaultHistoricalObservation = """
        {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
        "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
        "native_tokens_prompt":100,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
        "num_media_completion":0,"num_media_prompt":0,"total_cost":0.25,
        "tokens_prompt":888,"tokens_completion":999,"usage":14}}
        """;

    private static Result<GuildSpendObservation> ReadArchivedSpendObservation(
        GuildSpendSourceCredential.VerifiedSource source, string? bytes = null)
    {
        Result<GuildSpendAmountsRequest> parsed = GuildOpenRouterSpendParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(bytes ?? DefaultHistoricalObservation), source.GenerationId, source.Model);

        if (parsed.IsFailure) return parsed.Error;

        GuildSpendAmountsRequest amounts = parsed.Value;

        string canonical = FormattableString.Invariant(
            $"Arcanum.Guild.OpenRouterGeneration.Facts.v1\n{amounts.InputTokens}\n{amounts.OutputTokens}\n{amounts.CachedTokens}\n{amounts.ReasoningTokens}\n{amounts.Usd!.Value:G29}");

        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));

        return new GuildSpendObservation(source, amounts, digest);
    }

    private sealed class SpendCaptureKeys(string? secret = "private-provider-key-sentinel") : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(secret);
    }
}
