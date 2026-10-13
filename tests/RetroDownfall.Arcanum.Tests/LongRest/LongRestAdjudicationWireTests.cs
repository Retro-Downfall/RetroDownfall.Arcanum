using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestAdjudicationWireTests
{
    [Theory]
    [InlineData(LongRestAdjudicationKind.EquivalentObservations, "null", true)]
    [InlineData(LongRestAdjudicationKind.EquivalentObservations, "input", false)]
    [InlineData(LongRestAdjudicationKind.EquivalentObservations, "missing", false)]
    [InlineData(LongRestAdjudicationKind.Supersession, "input", true)]
    [InlineData(LongRestAdjudicationKind.Supersession, "null", false)]
    [InlineData(LongRestAdjudicationKind.Supersession, "foreign", false)]
    [InlineData(LongRestAdjudicationKind.NoChange, "null", true)]
    [InlineData(LongRestAdjudicationKind.NoChange, "input", false)]
    public async Task Paid_adjudication_wire_matches_the_canonical_survivor_contract(
        LongRestAdjudicationKind kind, string declaration, bool valid)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(LongRestProviderPayloadKind.Adjudication);

        string? survivor = declaration switch
        {
            "input" => f.Payload.Inputs[0].Snapshot.VersionId,
            "foreign" => Guid.NewGuid().ToString("N"),
            _ => null,
        };

        string field = declaration == "missing" ? ""
            : ",\"survivorVersionId\":" + (survivor is null ? "null" : "\"" + JsonEncodedText.Encode(survivor) + "\"");

        f.Handler.ChatText = "{\"kind\":\"" + kind + "\"" + field + "}";

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "");

        Assert.Equal(valid ? LongRestProviderResultDisposition.Valid : LongRestProviderResultDisposition.Invalid, result.Value.Disposition);

        if (valid)
        {
            Assert.Equal(new LongRestAdjudicationDecision(kind, survivor), result.Value.Decision);

            if (kind != LongRestAdjudicationKind.NoChange)
            {
                LongRestTransformationKind transformation = kind == LongRestAdjudicationKind.Supersession
                    ? LongRestTransformationKind.Supersession : LongRestTransformationKind.EquivalentObservations;

                Assert.True(LongRestPolicy.ValidateRequest(new(transformation,
                    f.Payload.Inputs.Select(input => input.Target).ToArray(), survivor)).IsSuccess);
            }
        }
        else
        {
            Assert.Null(result.Value.Decision);

            Assert.Equal("LongRest.InvalidOutput", result.Value.InvalidReason);
        }

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(17, result.Value.Usage.InputTokens);

        Assert.Equal(3, result.Value.Usage.OutputTokens);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations WHERE Purpose = 'long-rest-adjudication';"));

        Assert.Equal(17L, await f.H.ScalarAsync("SELECT InputTokens FROM BillableOperations WHERE Purpose = 'long-rest-adjudication';"));

        Assert.Equal(3L, await f.H.ScalarAsync("SELECT OutputTokens FROM BillableOperations WHERE Purpose = 'long-rest-adjudication';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));
    }

    [Fact]
    public async Task Physical_adjudication_request_instructs_only_supersession_to_declare_a_survivor()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(LongRestProviderPayloadKind.Adjudication);

        f.Handler.ChatText = "{\"kind\":\"NoChange\",\"survivorVersionId\":null}";

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Valid, result.Value.Disposition);

        using JsonDocument body = JsonDocument.Parse(f.Handler.LastBody!);

        string system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;

        Assert.Contains("Select an exact supplied survivorVersionId only for Supersession.", system, StringComparison.Ordinal);

        Assert.Contains("Use null for EquivalentObservations and NoChange.", system, StringComparison.Ordinal);
    }
}
