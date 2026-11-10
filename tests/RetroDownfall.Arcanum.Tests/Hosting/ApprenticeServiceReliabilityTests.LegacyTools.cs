using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed partial class ApprenticeServiceReliabilityTests
{
    [Theory]
    [InlineData("start", "read_file_chunk", "read_file")]
    [InlineData("resume", "DISPATCH_SENDING", "create_agent_task")]
    [InlineData("continue", "replace_text_block", "edit_file")]
    public async Task Removed_tool_plan_parks_before_execution_with_exact_historical_plan(string action, string removed, string replacement)
    {
        Apprentice apprentice = TestApprentice(Guid.NewGuid());

        apprentice.Status = action == "start" ? ApprenticeStatus.Idle.ToString()
            : action == "resume" ? ApprenticeStatus.Paused.ToString() : ApprenticeStatus.Escalated.ToString();

        apprentice.Plan = ApprenticeRepository.SerializePlan([new() { Description = $"Use `{removed}` to complete work." }]);

        string plan = apprentice.Plan;

        InMemoryApprenticeRepository repository = new(apprentice);

        using ApprenticeService service = CreateService(repository, new ArcanumSettings(), new CapturingLogger<ApprenticeService>());

        try
        {
            Result<string> result = action == "start" ? await service.StartAsync(apprentice.Id)
                : action == "resume" ? await service.ResumeAsync(apprentice.Id)
                : await service.ContinueAsync(apprentice.Id, "Continue", false);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Guild.LegacyToolName, result.Error.Code);

            Apprentice parked = repository.Get(apprentice.Id);

            Assert.Equal(ApprenticeStatus.Escalated.ToString(), parked.Status);

            Assert.Contains(removed, parked.ErrorMessage);

            Assert.Contains(replacement, parked.ErrorMessage);

            Assert.Equal(plan, parked.Plan);

            Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
        }
        finally
        {
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
