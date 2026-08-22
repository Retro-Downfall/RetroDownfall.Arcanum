using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class GenericSettingsPreservationTests : IDisposable
{

    private readonly ArcanumTestHomeScope _home;

    private readonly string _tempRoot;

    public GenericSettingsPreservationTests()
    {

        _home = new ArcanumTestHomeScope("compendium-generic");

        _tempRoot = _home.Root;

    }

    [Fact]
    public async Task Editing_polished_host_preserves_every_generic_minimal_section()
    {

        await SeedAsync(new ArcanumSettings
        {
            Host = new HostSettings { Port = 5001 },
            Security = new SecuritySettings
            {
                CampaignRoots = ["/campaigns"],
                Ward = new WardPolicySettings { UnattendedMode = true },
            },
            Workspaces = new WorkspaceSettings
            {
                DefaultRoot = "/workspace",
                EnableFileWrite = true,
            },
            Features = new FeatureSettings
            {
                WebBrowsing = true,
                Apprentices = true,
            },
            Integrations = new IntegrationSettings
            {
                Mcp = new McpIntegrationSettings { AllowedHttpHosts = ["localhost"] },
            },
            Execution = new ExecutionSettings
            {
                MaxConcurrentApprentices = 7,
                MaxSseConnections = 80,
            },
            Cost = new CostSettings
            {
                Budget = new BudgetPolicySettings
                {
                    Enabled = true,
                    DailyLimitUsd = 25m,
                },
            },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        vm.Host.Port = 5100;

        ArcanumSettings built = vm.BuildSettings();

        Assert.Equal(5100, built.Host.Port);
        Assert.Equal(["/campaigns"], built.Security.CampaignRoots);
        Assert.True(built.Security.Ward.UnattendedMode);
        Assert.Equal("/workspace", built.Workspaces.DefaultRoot);
        Assert.True(built.Workspaces.EnableFileWrite);
        Assert.True(built.Features.WebBrowsing);
        Assert.True(built.Features.Apprentices);
        Assert.Equal(["localhost"], built.Integrations.Mcp.AllowedHttpHosts);
        Assert.Equal(7, built.Execution.MaxConcurrentApprentices);
        Assert.Equal(80, built.Execution.MaxSseConnections);
        Assert.True(built.Cost.Budget.Enabled);
        Assert.Equal(25m, built.Cost.Budget.DailyLimitUsd);

    }

    [Fact]
    public async Task Editing_generic_fields_updates_new_sections()
    {

        await SeedAsync(new ArcanumSettings());

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        Field(vm, ConfigSection.Features, "features.webBrowsing").BoolValue = true;
        Field(vm, ConfigSection.Security, "security.campaignRoots").StringValue =
            "C:\\campaigns, D:\\archives";
        Field(vm, ConfigSection.Execution, "execution.maxConcurrentApprentices").NumericValue = 9;
        Field(vm, ConfigSection.Integrations, "integrations.embeddings.provider").StringValue =
            "local-embeddings";

        ArcanumSettings built = vm.BuildSettings();

        Assert.True(built.Features.WebBrowsing);
        Assert.Equal(["C:\\campaigns", "D:\\archives"], built.Security.CampaignRoots);
        Assert.Equal(9, built.Execution.MaxConcurrentApprentices);
        Assert.Equal("local-embeddings", built.Integrations.Embeddings.Provider);

    }

    [Fact]

    public void Advertised_a2a_skill_keys_are_collection_templates_and_never_overwrite_a_declared_skill()
    {

        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    Skills = [new A2ASkillSettings { Id = "code-review" }],
                },
            },
        };

        string[] templateKeys =
        [
            "integrations.a2A.skills.id",
            "integrations.a2A.skills.name",
            "integrations.a2A.skills.description",
            "integrations.a2A.skills.inputModes",
            "integrations.a2A.skills.outputModes",
        ];

        List<GenericSettingFieldViewModel> fields = [];

        foreach (string key in templateKeys)
        {

            SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
                SettingDescriptors.Find(key));

            GenericSettingFieldViewModel field = new(
                descriptor,
                GenericSettingsUpdater.ReadValue(settings, key));

            Assert.True(field.IsCollectionTemplate, key);

            Assert.Equal("integrations.a2A.skills", field.CollectionTemplatePath);

            field.StringValue = "typed-into-a-template";

            fields.Add(field);

        }

        ArcanumSettings updated = GenericSettingsUpdater.ApplyFields(settings, fields);

        A2ASkillSettings skill = Assert.Single(updated.Integrations.A2A.Skills);

        Assert.Equal("code-review", skill.Id);

        Assert.Null(skill.Name);

    }

    [Fact]

    public void Protected_session_ids_use_the_string_array_editor_without_losing_guid_values()
    {

        Guid protectedId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid addedId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        ArcanumSettings settings = new()
        {
            Retention = new RetentionSettings
            {
                ProtectedSessionIds = [protectedId],
            },
        };

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("retention.protectedSessionIds"));

        Assert.Equal(ConfigSection.Retention, descriptor.Section);

        Assert.Equal(SettingKind.StringArray, descriptor.Kind);

        GenericSettingFieldViewModel field = new(
            descriptor,
            settings.Retention.ProtectedSessionIds);

        Assert.Equal(protectedId.ToString(), field.StringValue);

        field.StringValue = $"{protectedId}, {addedId}";

        ArcanumSettings updated = GenericSettingsUpdater.ApplyFields(settings, [field]);

        Assert.Equal([protectedId, addedId], updated.Retention.ProtectedSessionIds);

    }

    [Fact]

    public void Protected_session_ids_validate_each_guid_before_save()
    {

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("retention.protectedSessionIds"));

        GenericSettingFieldViewModel field = new(descriptor, Array.Empty<Guid>());

        field.StringValue =
            "11111111-1111-1111-1111-111111111111, not-a-session-id";

        Assert.True(field.HasError);

        Assert.Contains(
            "GUID",
            field.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);

        field.StringValue =
            "11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222";

        Assert.False(field.HasError);

    }

    [Fact]

    public void Malformed_protected_session_id_does_not_throw_or_replace_saved_ids()
    {

        Guid protectedId = Guid.Parse(
            "11111111-1111-1111-1111-111111111111");

        ArcanumSettings settings = new()
        {

            Retention = new RetentionSettings
            {

                ProtectedSessionIds = [protectedId],

            },

        };

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("retention.protectedSessionIds"));

        GenericSettingFieldViewModel field = new(
            descriptor,
            settings.Retention.ProtectedSessionIds);

        field.StringValue = "not-a-session-id";

        Assert.True(field.HasError);

        ArcanumSettings updated = GenericSettingsUpdater.ApplyFields(
            settings,
            [field]);

        Assert.Equal(
            [protectedId],
            updated.Retention.ProtectedSessionIds);

    }

    [Fact]
    public async Task Nullable_reasoning_price_preserves_null_and_can_set_then_clear_zero()
    {

        await SeedAsync(new ArcanumSettings
        {
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry
                    {
                        OutputPer1M = 5m,
                        ReasoningPer1M = null,
                    },
                },
            },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        GenericSettingFieldViewModel reasoning = Field(
            vm,
            ConfigSection.Cost,
            "cost.pricing.defaultPricing.reasoningPer1M");

        Assert.True(reasoning.Descriptor.AllowUnset);
        Assert.False(reasoning.IsSet);
        Assert.Null(vm.BuildSettings().Cost.Pricing.DefaultPricing.ReasoningPer1M);

        reasoning.IsSet = true;
        reasoning.NumericValue = 0;

        Assert.Equal(0m, vm.BuildSettings().Cost.Pricing.DefaultPricing.ReasoningPer1M);

        reasoning.IsSet = false;

        ArcanumSettings cleared = vm.BuildSettings();

        Assert.Null(cleared.Cost.Pricing.DefaultPricing.ReasoningPer1M);

        using ArcanumConfigurationStore store = new(enableWatcher: true);

        ConfigurationWriteResult writeResult = await store.WriteAsync(cleared, CancellationToken.None);

        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings saved = await store.ReadAsync(CancellationToken.None);

        Assert.Null(saved.Cost.Pricing.DefaultPricing.ReasoningPer1M);

    }

    [Fact]
    public async Task Provider_reasoning_facts_and_credential_reference_round_trip()
    {
        await SeedAsync(new ArcanumSettings
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "reasoning-provider",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://reasoning.example/v1",
                    CredentialEnvironmentVariable = "REASONING_PROVIDER_API_KEY",
                    Models =
                    [
                        new ModelEntry(
                            "reasoner",
                            SupportsVision: true,
                            Reasoning: new ModelReasoningSettings
                            {
                                WireDialect = ReasoningWireDialect.OpenRouter,
                                MaxBudgetTokens = 32768,
                            }),
                    ],
                },
            ],
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        ProvidersSectionViewModel.ProviderViewModel provider = Assert.Single(vm.Providers.Providers);
        ProvidersSectionViewModel.ModelEntryViewModel model = Assert.Single(provider.Models);

        Assert.True(model.HasReasoning);
        Assert.Equal(ReasoningWireDialect.OpenRouter, model.ReasoningDialect);
        Assert.Equal(32768, model.ReasoningMaxBudgetTokens);
        Assert.Equal(
            "REASONING_PROVIDER_API_KEY",
            provider.CredentialEnvironmentVariable);

        model.Name = "reasoner-v2";
        model.ReasoningDialect = ReasoningWireDialect.TopLevelReasoningBudget;
        model.ReasoningMaxBudgetTokens = 65536;

        ArcanumSettings built = vm.BuildSettings();
        ProviderSettings builtProvider = Assert.Single(built.Providers);
        ModelEntry builtModel = Assert.Single(builtProvider.Models);

        Assert.Equal("reasoner-v2", builtModel.Name);
        Assert.Equal(ReasoningWireDialect.TopLevelReasoningBudget, builtModel.Reasoning?.WireDialect);
        Assert.Equal(65536, builtModel.Reasoning?.MaxBudgetTokens);
        Assert.Equal(
            "REASONING_PROVIDER_API_KEY",
            builtProvider.CredentialEnvironmentVariable);

        using ArcanumConfigurationStore store = new(enableWatcher: true);

        ConfigurationWriteResult writeResult = await store.WriteAsync(built, CancellationToken.None);

        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings saved = await store.ReadAsync(CancellationToken.None);
        ProviderSettings savedProvider = Assert.Single(saved.Providers);
        ModelEntry savedModel = Assert.Single(savedProvider.Models);

        Assert.Equal("reasoner-v2", savedModel.Name);
        Assert.Equal(ReasoningWireDialect.TopLevelReasoningBudget, savedModel.Reasoning?.WireDialect);
        Assert.Equal(65536, savedModel.Reasoning?.MaxBudgetTokens);
        Assert.Equal(
            "REASONING_PROVIDER_API_KEY",
            savedProvider.CredentialEnvironmentVariable);

    }

    [Fact]
    public async Task Authored_dictionary_editors_use_source_generated_contracts_and_round_trip()
    {

        WorkspaceCheckProfileSettings original = new()
        {
            ExecutableId = "dotnet",
            Kind = WorkspaceCheckKind.Test,
            Parser = WorkspaceCheckDiagnosticParserKind.VsTest,
            FixedArguments = ["test"],
        };

        await SeedAsync(new ArcanumSettings
        {
            Integrations = new IntegrationSettings
            {
                WorkspaceChecks = new WorkspaceCheckIntegrationSettings
                {
                    CustomProfiles = new Dictionary<string, WorkspaceCheckProfileSettings>
                    {
                        ["ci-test"] = original,
                    },
                },
            },
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    ModelPricing = new Dictionary<string, ModelPricingEntry>
                    {
                        ["reasoner"] = new() { InputPer1M = 1m, OutputPer1M = 2m },
                    },
                },
            },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        GenericSettingFieldViewModel profiles = Field(
            vm,
            ConfigSection.Integrations,
            "integrations.workspaceChecks.customProfiles");

        GenericSettingFieldViewModel pricing = Field(
            vm,
            ConfigSection.Cost,
            "cost.pricing.modelPricing");

        Assert.Contains("\"ci-test\"", profiles.StringValue, StringComparison.Ordinal);
        Assert.Contains("\"reasoner\"", pricing.StringValue, StringComparison.Ordinal);

        Dictionary<string, WorkspaceCheckProfileSettings> updatedProfiles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["release-build"] = new WorkspaceCheckProfileSettings
                {
                    ExecutableId = "dotnet",
                    Kind = WorkspaceCheckKind.Build,
                    Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                    FixedArguments = ["build", "--configuration", "Release"],
                },
            };

        profiles.StringValue = JsonSerializer.Serialize(
            updatedProfiles,
            typeof(Dictionary<string, WorkspaceCheckProfileSettings>),
            ConfigurationJsonContext.Default);

        ArcanumSettings built = vm.BuildSettings();

        Assert.DoesNotContain("ci-test", built.Integrations.WorkspaceChecks.CustomProfiles.Keys);
        Assert.Equal(
            ["build", "--configuration", "Release"],
            built.Integrations.WorkspaceChecks.CustomProfiles["release-build"].FixedArguments);
        Assert.Equal(
            1m,
            built.Cost.Pricing.ModelPricing["reasoner"].InputPer1M);

        using ArcanumConfigurationStore store = new(enableWatcher: true);

        ConfigurationWriteResult writeResult = await store.WriteAsync(built, CancellationToken.None);

        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings saved = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(
            WorkspaceCheckKind.Build,
            saved.Integrations.WorkspaceChecks.CustomProfiles["release-build"].Kind);
        Assert.Equal(
            2m,
            saved.Cost.Pricing.ModelPricing["reasoner"].OutputPer1M);

    }

    [Fact]

    public void Dictionary_setter_failure_names_the_field_that_could_not_be_applied()
    {

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.pricing.modelPricing"));

        GenericSettingFieldViewModel field = new(
            descriptor,
            new Dictionary<string, ModelPricingEntry>());

        // System.Text.Json builds an ordinal-comparer dictionary, so both casings validate; the
        // case-insensitive rebuild inside the PricingSettings setter is what rejects them.
        field.StringValue = """{"gpt-4o":{"inputPer1M":1},"GPT-4o":{"inputPer1M":2}}""";

        Assert.False(field.HasError);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => GenericSettingsUpdater.ApplyFields(new ArcanumSettings(), [field]));

        Assert.Contains(
            "cost.pricing.modelPricing",
            failure.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "target of an invocation",
            failure.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "same key",
            failure.Message,
            StringComparison.OrdinalIgnoreCase);

    }

    private static GenericSettingFieldViewModel Field(
        ConfigurationViewModel vm,
        ConfigSection section,
        string key) =>
        Assert.Single(
            vm.GetOrCreateGenericSection(section).Fields,
            field => field.Descriptor.Key == key);

    private static ConfigurationViewModel CreateViewModel()
    {

        ArcanumConfigurationStore store = new(enableWatcher: true);

        return new ConfigurationViewModel(
            store,
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance,
            ImmediateArcanumClientMutationBoundary.Instance);

    }

    private static async Task SeedAsync(ArcanumSettings settings)
    {

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new ArcanumConfigurationFile { Arcanum = settings },
                ConfigurationJsonContext.Default.ArcanumConfigurationFile));

    }

    private static async Task WaitForLoadAsync(ConfigurationViewModel vm)
    {

        for (int i = 0; i < 50; i++)
        {

            if (!string.IsNullOrEmpty(vm.StatusMessage)
                && vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal))
            {

                return;

            }

            await Task.Delay(50);

        }

        Assert.Fail($"Timed out waiting for load. Status={vm.StatusMessage}");

    }

    public void Dispose() => _home.Dispose();

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(
            string title,
            string message,
            string cancel = "OK") =>
            Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string accept = "Yes",
            string cancel = "No") =>
            Task.FromResult(true);

    }

}
