using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-owned source capability for the exact credential used to construct one physical client.</summary>
internal sealed class GuildSpendSourceCredential
{
    private readonly string? _secret;

    private readonly AiProviderKind _type;

    private readonly string _endpoint;

    private readonly string _provider;

    private readonly string _model;

    private readonly GuildUsageReconciliationSource _source;

    internal GuildSpendSourceCredential(ProviderSettings provider, string model, string? secret)
    {
        _type = provider.Type;

        _endpoint = provider.Endpoint;

        _provider = provider.Name;

        _model = model;

        _source = provider.GuildUsageReconciliation;

        _secret = secret;
    }

    internal async Task<Result> CaptureWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetEffectRequest effect, Guid generation, string pricing, CancellationToken ct)
    {
        if (_type != AiProviderKind.OpenAICompatible || _source != GuildUsageReconciliationSource.OpenRouterGeneration
            || !ProviderSettings.IsCanonicalOpenRouterEndpoint(_endpoint) || !ValidSecret(_secret)
            || string.IsNullOrWhiteSpace(_provider) || string.IsNullOrWhiteSpace(_model) || generation == Guid.Empty)
            return Unavailable;

        await using SqliteCommand authority = connection.CreateCommand();

        authority.Transaction = transaction;

        authority.CommandText = "SELECT KeyId,AuthorityEpoch,CredentialKey FROM guild_spend_authority_state WHERE StateKey=1";

        await using SqliteDataReader reader = await authority.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

        string keyId = reader.GetString(0);

        string epoch = reader.GetString(1);

        byte[] key = (byte[])reader.GetValue(2);

        await reader.DisposeAsync().ConfigureAwait(false);

        byte[] fingerprint;

        try
        {
            if (key.Length != 32) return Unavailable;

            fingerprint = Fingerprint(key, _secret!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        await using SqliteCommand insert = connection.CreateCommand();

        insert.Transaction = transaction;

        insert.CommandText = """
            INSERT INTO guild_spend_sources(EffectId,SourceId,SourceKindCode,SourceVersion,InstallationGeneration,
                AuthorityEpoch,AuthorityKeyId,Endpoint,Provider,Model,PricingSnapshotJson,CredentialFingerprint)
            VALUES($effect,$source,1,1,$generation,$epoch,$key,$endpoint,$provider,$model,$pricing,$fingerprint)
            """;

        insert.Parameters.AddWithValue("$effect", Id(effect.EffectId));

        insert.Parameters.AddWithValue("$source", Id(effect.SourceId));

        insert.Parameters.AddWithValue("$generation", Id(generation));

        insert.Parameters.AddWithValue("$epoch", epoch);

        insert.Parameters.AddWithValue("$key", keyId);

        insert.Parameters.AddWithValue("$endpoint", "https://openrouter.ai:443/api/v1");

        insert.Parameters.AddWithValue("$provider", _provider);

        insert.Parameters.AddWithValue("$model", _model);

        insert.Parameters.AddWithValue("$pricing", pricing);

        insert.Parameters.AddWithValue("$fingerprint", fingerprint);

        return await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Result.Failure(Unavailable);
    }

    internal Task<Result<VerifiedSource>> VerifyWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid effectId, Guid generation, CancellationToken ct) =>
        VerifiedSource.VerifyWithinAsync(this, connection, transaction, effectId, generation, ct);

    private static bool ValidSecret(string? secret) => secret is { Length: > 0 }
        && secret.All(static character => character is >= '!' and <= '~');

    private static byte[] Fingerprint(byte[] key, string secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);

        try
        {
            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);

            hmac.AppendData("Arcanum.Guild.OpenRouterGeneration.Credential.v1\0"u8);

            hmac.AppendData(bytes);

            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ValidGenerationId(string id)
    {
        if (id.Length is < 5 or > 128 || !id.StartsWith("gen-", StringComparison.Ordinal)) return false;

        foreach (char c in id.AsSpan(4))
        {
            if (c is not (>= '0' and <= '9') and not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not '-') return false;
        }

        return true;
    }

    internal sealed class VerifiedSource
    {
        internal static async Task<Result<VerifiedSource>> VerifyWithinAsync(GuildSpendSourceCredential credential, SqliteConnection connection, SqliteTransaction transaction,
            Guid effectId, Guid generation, CancellationToken ct)
        {
            if (credential._type != AiProviderKind.OpenAICompatible
                || credential._source != GuildUsageReconciliationSource.OpenRouterGeneration
                || !ProviderSettings.IsCanonicalOpenRouterEndpoint(credential._endpoint)
                || string.IsNullOrWhiteSpace(credential._provider) || string.IsNullOrWhiteSpace(credential._model))
                return GuildSpendSourceFailure.ConfigurationChanged;

            if (!ValidSecret(credential._secret)) return GuildSpendSourceFailure.CredentialChanged;

            if (generation == Guid.Empty) return GuildSpendSourceFailure.AuthorityRetired;

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                SELECT s.SourceKindCode,s.SourceVersion,s.InstallationGeneration,q.InstallationGeneration,
                    s.Endpoint,s.Provider,s.Model,s.GenerationId,s.CredentialFingerprint,s.InitialSnapshot,
                    s.AuthorityKeyId,s.AuthorityEpoch,a.KeyId,a.AuthorityEpoch,a.CredentialKey,
                    s.SourceId,e.SourceId,e.KindCode
                FROM guild_spend_sources s JOIN guild_budget_effects e ON e.EffectId=s.EffectId
                JOIN guild_quests q ON q.Id=e.QuestId
                LEFT JOIN guild_spend_authority_state a ON a.StateKey=1
                WHERE s.EffectId=$effect
                """;

            command.Parameters.AddWithValue("$effect", Id(effectId));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return GuildSpendSourceFailure.SourceMissing;

            if (reader.GetValue(0) is not long sourceKind) return GuildSpendSourceFailure.SourceInvalid;

            if (sourceKind != 1) return GuildSpendSourceFailure.SourceMissing;

            if (reader.GetValue(1) is not long sourceVersion || sourceVersion != 1 || reader.GetString(15) != reader.GetString(16)
                || reader.GetValue(17) is not long kind || kind != 0)
                return GuildSpendSourceFailure.SourceInvalid;

            if (reader.GetString(2) != Id(generation) || reader.GetString(3) != Id(generation)
                || reader.IsDBNull(12) || reader.IsDBNull(13) || reader.IsDBNull(14)
                || reader.GetString(10) != reader.GetString(12) || reader.GetString(11) != reader.GetString(13))
                return GuildSpendSourceFailure.AuthorityRetired;

            if (reader.GetString(4) != "https://openrouter.ai:443/api/v1"
                || reader.GetString(5) != credential._provider || reader.GetString(6) != credential._model)
                return GuildSpendSourceFailure.ConfigurationChanged;

            if (reader.IsDBNull(7)) return GuildSpendSourceFailure.GenerationMissing;

            string generationId = reader.GetString(7);

            if (!ValidGenerationId(generationId)) return GuildSpendSourceFailure.GenerationInvalid;

            if (reader.IsDBNull(9)) return GuildSpendSourceFailure.InitialEvidenceUnavailable;

            byte[] saved = (byte[])reader.GetValue(8);

            byte[] key = (byte[])reader.GetValue(14);

            try
            {
                if (key.Length != 32 || saved.Length != 32) return GuildSpendSourceFailure.SourceInvalid;

                if (!CryptographicOperations.FixedTimeEquals(saved, Fingerprint(key, credential._secret!)))
                    return GuildSpendSourceFailure.CredentialChanged;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            string sourceId = reader.GetString(15);

            await reader.DisposeAsync().ConfigureAwait(false);

            Result<GuildStoredInitialSpend?> initial = await GuildSpendSourceStore.LoadInitialWithinAsync(
                connection, transaction, effectId, ct).ConfigureAwait(false);

            if (initial.IsFailure || initial.Value is not { } stored
                || Id(stored.Snapshot.SourceId) != sourceId || stored.Snapshot.ProviderRequestId != generationId)
                return GuildSpendSourceFailure.SourceInvalid;

            return new VerifiedSource(generationId, credential._model, credential._secret!);
        }

        private readonly string _secret;

        private VerifiedSource(string generationId, string model, string secret)
        {
            GenerationId = generationId;

            Model = model;

            _secret = secret;
        }

        internal string GenerationId { get; }

        internal string Model { get; }

        internal async Task<HttpResponseMessage> SendAsync(HttpClient client, CancellationToken ct)
        {
            using HttpRequestMessage request = new(HttpMethod.Get,
                "https://openrouter.ai/api/v1/generation?id=" + Uri.EscapeDataString(GenerationId));

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secret);

            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
    }

    private static string Id(Guid id) => id.ToString("D").ToUpperInvariant();

    private static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable, "The provider spend source is unavailable.");
}

/// <summary>Stable secret-free eligibility reasons for an authorized refresh caller.</summary>
internal static class GuildSpendSourceFailure
{
    internal static Error SourceMissing => new("Guild.SpendSourceMissing", "The provider spend source is missing.");

    internal static Error SourceInvalid => new("Guild.SpendSourceInvalid", "The provider spend source is invalid.");

    internal static Error GenerationMissing => new("Guild.SpendGenerationMissing", "The provider generation is missing.");

    internal static Error GenerationInvalid => new("Guild.SpendGenerationInvalid", "The provider generation is invalid.");

    internal static Error ConfigurationChanged => new("Guild.SpendConfigurationChanged", "The provider source configuration changed.");

    internal static Error CredentialChanged => new("Guild.SpendCredentialChanged", "The provider credential is unavailable or changed.");

    internal static Error AuthorityRetired => new("Guild.SpendAuthorityRetired", "The provider spend authority is unavailable or retired.");

    internal static Error InitialEvidenceUnavailable => new("Guild.InitialEvidenceUnavailable", "The initial spend evidence is unavailable.");
}
