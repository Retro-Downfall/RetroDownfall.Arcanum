using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class ApprenticeServiceReliabilityTests
{
    [Theory]
    [InlineData("operator")]
    [InlineData("legacy")]
    [InlineData("remote")]
    public async Task Guidance_replacement_keeps_exact_origin_across_checkpoint_reload(string route)
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = ApprenticeStatus.Escalated.ToString();

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
        {
            GoalAuthority = GuildAuthorityKind.Parent,
            OperatorGuidance = "older operator answer",
            ParentGuidance = "older parent answer",
            DmGuidance = "older unknown answer",
            DelegationChain = ["origin", "local"],
        });

        InMemoryApprenticeRepository repository = new(apprentice);

        using ApprenticeService service = CreateService(repository, new ArcanumSettings(), new CapturingLogger<ApprenticeService>());

        Result<string> accepted = route switch
        {
            "operator" => await service.InterveneOperatorAsync(apprentice.Id, "new answer", false,
                new ApprenticeOperatorInterventionContext(apprentice.Id)),
            "remote" => await service.ContinueAsync(apprentice.Id, "new answer", false),
            _ => await service.InterveneAsync(apprentice.Id, "new answer", false),
        };

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        ApprenticeCheckpoint restored = ApprenticeRepository.DeserializeCheckpoint(repository.Get(apprentice.Id).CheckpointData)!;

        Assert.Equal(GuildAuthorityKind.Parent, restored.GoalAuthority);

        Assert.Equal(new[] { "origin", "local" }, restored.DelegationChain);

        Assert.Null(restored.ParentGuidance);

        Assert.Equal(route == "operator" ? "new answer" : null, restored.OperatorGuidance);

        Assert.Equal(route == "operator" ? null : "new answer", restored.DmGuidance);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Operator_guidance_requires_exact_host_target_without_mutation(bool missingContext)
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = ApprenticeStatus.Escalated.ToString();

        apprentice.CheckpointData = "{\"dmGuidance\":\"original\"}";

        InMemoryApprenticeRepository repository = new(apprentice);

        using ApprenticeService service = CreateService(repository, new ArcanumSettings(), new CapturingLogger<ApprenticeService>());

        ApprenticeOperatorInterventionContext context = missingContext ? null! : new(Guid.NewGuid());

        Result<string> refused = await service.InterveneOperatorAsync(apprentice.Id, "unbound answer", false, context);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Apprentice.InvalidGuidance, refused.Error.Code);

        Assert.Equal("{\"dmGuidance\":\"original\"}", repository.Get(apprentice.Id).CheckpointData);

        Assert.Equal(ApprenticeStatus.Escalated.ToString(), repository.Get(apprentice.Id).Status);
    }
}
