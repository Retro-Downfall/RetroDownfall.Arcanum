using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

public sealed class LexiconCurationContractTests
{
    private const string Digest = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Global_and_Campaign_scopes_validate_only_with_their_canonical_identity_shape()
    {

        Guid campaignId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.True(new LexiconCurationScope(LexiconScopeKind.Global, null).Validate().IsSuccess);
        Assert.True(new LexiconCurationScope(LexiconScopeKind.Campaign, campaignId).Validate().IsSuccess);

        Assert.Equal(
            ErrorCodes.Lexicon.InvalidScope,
            new LexiconCurationScope(LexiconScopeKind.Global, campaignId).Validate().Error.Code);
        Assert.Equal(
            ErrorCodes.Lexicon.InvalidScope,
            new LexiconCurationScope(LexiconScopeKind.Campaign, null).Validate().Error.Code);
        Assert.Equal(
            ErrorCodes.Lexicon.InvalidScope,
            new LexiconCurationScope(LexiconScopeKind.Campaign, Guid.Empty).Validate().Error.Code);

    }

    [Fact]
    public void Missing_or_zero_scope_kind_is_not_treated_as_Global()
    {

        LexiconCurationScope missing = JsonSerializer.Deserialize(
            "{\"campaignId\":null}",
            LexiconCurationContractJsonContext.Default.LexiconCurationScope)!;

        Assert.True(missing.Validate().IsFailure);
        Assert.True(new LexiconCurationScope(0, null).Validate().IsFailure);

    }

    [Fact]
    public void Unknown_scope_enum_text_is_refused_by_source_generated_serialization()
    {

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"kind\":\"Future\",\"campaignId\":null}",
            LexiconCurationContractJsonContext.Default.LexiconCurationScope));

    }

    [Fact]
    public void Omitted_evidence_presence_is_distinct_from_explicitly_absent_evidence()
    {

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            LexiconCurationContractJsonContext.Default.LexiconCurationAnnalHead));

        LexiconCurationAnnalHead absent = JsonSerializer.Deserialize(
            "{\"isPresent\":false}",
            LexiconCurationContractJsonContext.Default.LexiconCurationAnnalHead)!;

        Assert.False(absent.IsPresent);
        Assert.True(absent.Validate().IsSuccess);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            LexiconCurationContractJsonContext.Default.LexiconCurationSensitivityLabel));

        LexiconCurationSensitivityLabel absentLabel = JsonSerializer.Deserialize(
            "{\"isPresent\":false}",
            LexiconCurationContractJsonContext.Default.LexiconCurationSensitivityLabel)!;

        Assert.False(absentLabel.IsPresent);
        Assert.True(absentLabel.Validate().IsSuccess);

    }

    [Fact]
    public void Absent_evidence_arms_reject_partial_values()
    {

        LexiconCurationAnnalHead partialHead = JsonSerializer.Deserialize(
            "{\"isPresent\":false,\"claimId\":\"claim-1\"}",
            LexiconCurationContractJsonContext.Default.LexiconCurationAnnalHead)!;

        LexiconCurationSensitivityLabel partialLabel = JsonSerializer.Deserialize(
            "{\"isPresent\":false,\"artifactRevision\":1}",
            LexiconCurationContractJsonContext.Default.LexiconCurationSensitivityLabel)!;

        Assert.True(partialHead.Validate().IsFailure);
        Assert.True(partialLabel.Validate().IsFailure);

    }

    [Theory]
    [InlineData(AnnalOperation.Assert)]
    [InlineData(AnnalOperation.Correct)]
    public void Content_bearing_Annal_heads_require_a_format_and_sha256_digest(AnnalOperation operation)
    {

        LexiconCurationAnnalHead valid = PresentHead(operation, Digest);

        Assert.True(valid.Validate().IsSuccess);
        Assert.True((valid with { ContentHash = null }).Validate().IsFailure);
        Assert.True((valid with { ContentHash = "ABC" }).Validate().IsFailure);
        Assert.True((valid with { ContentHash = new string('G', 64) }).Validate().IsFailure);
        Assert.True((valid with { ContentHashFormat = 0 }).Validate().IsFailure);

    }

    [Fact]
    public void Retire_Annal_heads_require_format_but_forbid_a_content_digest()
    {

        LexiconCurationAnnalHead valid = PresentHead(AnnalOperation.Retire, null);

        Assert.True(valid.Validate().IsSuccess);
        Assert.True((valid with { ContentHash = Digest }).Validate().IsFailure);
        Assert.True((valid with { ContentHashFormat = null }).Validate().IsFailure);

    }

    [Fact]
    public void Present_sensitivity_evidence_requires_every_field_and_digest_shape()
    {

        LexiconCurationSensitivityLabel valid = new(
            true,
            Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"),
            1,
            Digest,
            GenerationProvenance.CreateExact([Guid.Parse("12345678-1234-1234-1234-123456789ABC")]));

        Assert.True(valid.Validate().IsSuccess);
        Assert.True((valid with { LabelId = Guid.Empty }).Validate().IsFailure);
        Assert.True((valid with { ArtifactRevision = 0 }).Validate().IsFailure);
        Assert.True((valid with { ArtifactContentDigest = "not-a-digest" }).Validate().IsFailure);
        Assert.True((valid with { GenerationProvenance = null }).Validate().IsFailure);

    }

    [Fact]
    public void Closed_contract_enums_leave_zero_and_unknown_values_undefined()
    {

        Assert.False(Enum.IsDefined((LexiconScopeKind)0));
        Assert.False(Enum.IsDefined((LexiconScopeKind)255));
        Assert.False(Enum.IsDefined((LexiconRetrievalEligibility)0));
        Assert.False(Enum.IsDefined((LexiconCurationOutcomeKind)0));
        Assert.False(Enum.IsDefined((AnnalContentHashFormat)0));
        Assert.False(Enum.IsDefined((AnnalContentHashFormat)255));

    }

    private static LexiconCurationAnnalHead PresentHead(
        AnnalOperation operation,
        string? digest) =>
        new(
            true,
            "claim-1",
            "version-1",
            1,
            operation,
            AnnalContentHashFormat.LexiconStructuredSnapshot,
            digest);

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LexiconCurationScope))]
[JsonSerializable(typeof(LexiconCurationAnnalHead))]
[JsonSerializable(typeof(LexiconCurationSensitivityLabel))]
internal sealed partial class LexiconCurationContractJsonContext : JsonSerializerContext;
