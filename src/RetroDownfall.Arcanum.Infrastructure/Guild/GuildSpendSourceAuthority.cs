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
            if (credential._type != AiProviderKind.OpenAICompatible || credential._source != GuildUsageReconciliationSource.OpenRouterGeneration || !ProviderSettings.IsCanonicalOpenRouterEndpoint(credential._endpoint)
                || !ValidSecret(credential._secret) || string.IsNullOrWhiteSpace(credential._provider) || string.IsNullOrWhiteSpace(credential._model)
                || generation == Guid.Empty) return Unavailable;

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                SELECT s.Provider,s.Model,s.GenerationId,s.CredentialFingerprint,a.CredentialKey
                FROM guild_spend_sources s JOIN guild_budget_effects e ON e.EffectId=s.EffectId
                JOIN guild_quests q ON q.Id=e.QuestId
                JOIN guild_spend_authority_state a ON a.StateKey=1 AND a.KeyId=s.AuthorityKeyId AND a.AuthorityEpoch=s.AuthorityEpoch
                WHERE s.EffectId=$effect AND s.SourceKindCode=1 AND s.SourceVersion=1
                  AND s.InstallationGeneration=$generation AND q.InstallationGeneration=$generation
                  AND s.Endpoint='https://openrouter.ai:443/api/v1' AND s.InitialSnapshot IS NOT NULL
                """;

            command.Parameters.AddWithValue("$effect", Id(effectId));

            command.Parameters.AddWithValue("$generation", Id(generation));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(2)
                || reader.GetString(0) != credential._provider || reader.GetString(1) != credential._model) return Unavailable;

            string generationId = reader.GetString(2);

            if (!ValidGenerationId(generationId)) return Unavailable;

            byte[] saved = (byte[])reader.GetValue(3);

            byte[] key = (byte[])reader.GetValue(4);

            try
            {
                if (key.Length != 32 || saved.Length != 32
                    || !CryptographicOperations.FixedTimeEquals(saved, Fingerprint(key, credential._secret!))) return Unavailable;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

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
