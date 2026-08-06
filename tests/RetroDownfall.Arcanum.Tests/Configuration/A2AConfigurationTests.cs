using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class A2AConfigurationTests
{

    [Fact]
    public void ConclaveA2ASettings_defaults_are_disabled_and_conservative()
    {

        ConclaveA2ASettings a2a = ArcanumRuntimeDefaults.Conclave.A2A;

        Assert.False(a2a.Enabled);

        Assert.False(a2a.ServerEnabled);

        Assert.False(a2a.ClientEnabled);

        Assert.Equal("/api/conclave/a2a", a2a.ServerPath);

        Assert.Equal(50, new ArcanumSettings().Execution.MaxConcurrentA2ATasks);

        Assert.Empty(a2a.AllowedRemoteAgents);

        Assert.Equal(string.Empty, a2a.DefaultWorkspace);

        Assert.Null(a2a.AgentCardName);

        Assert.Null(a2a.AgentCardDescription);

    }

    [Fact]
    public void ConclaveSettings_A2A_defaults_to_a_new_disabled_block()
    {

        ConclaveSettings conclave = ArcanumRuntimeDefaults.Conclave;

        Assert.False(conclave.Enabled);

        Assert.NotNull(conclave.A2A);

        Assert.False(conclave.A2A.Enabled);

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(-5, 1)]
    public void MaxConcurrentA2ATasks_clamps_to_1_500(int input, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.MaxConcurrentA2ATasks(input));

    }

    [Theory]
    [InlineData(nameof(FeatureSettings.A2AServer))]
    [InlineData(nameof(FeatureSettings.A2AClient))]
    public void A2A_feature_implies_Conclave_availability(string featureName)
    {

        FeatureSettings features = featureName switch
        {
            nameof(FeatureSettings.A2AServer) => new FeatureSettings { A2AServer = true },
            nameof(FeatureSettings.A2AClient) => new FeatureSettings { A2AClient = true },
            _ => throw new ArgumentOutOfRangeException(nameof(featureName)),
        };

        ArcanumSettings settings = new()
        {
            Features = features,
        };

        ConclaveSettings conclave = settings.ResolveConclave();

        Assert.True(conclave.Enabled);

        Assert.True(conclave.A2A.Enabled);

        Assert.Equal(features.A2AServer, conclave.A2A.ServerEnabled);

        Assert.Equal(features.A2AClient, conclave.A2A.ClientEnabled);

    }

    [Fact]
    public void ConclaveA2ASettings_defaults_send_no_outbound_credential()
    {

        ConclaveA2ASettings a2a = ArcanumRuntimeDefaults.Conclave.A2A;

        Assert.Equal(string.Empty, a2a.OutboundCredentialEnvironmentVariable);

        Assert.Equal("X-Arcanum-Key", a2a.OutboundCredentialHeader);

    }

    [Fact]
    public void Validator_accepts_a_well_formed_A2A_block()
    {

        Result result = Validate(new A2AIntegrationSettings
        {
            ServerPath = "/conclave/inbound",
            AllowedRemoteAgents = ["https://partner.example.test", "https://other.example.test/agent"],
            OutboundCredentialEnvironmentVariable = "ARCANUM_A2A_PEER_KEY",
            OutboundCredentialHeader = "Authorization",
        });

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://partner.example.test")]
    [InlineData("partner.example.test")]
    public void Validator_rejects_an_allowlist_entry_that_can_never_match(string entry)
    {

        Result result = Validate(new A2AIntegrationSettings { AllowedRemoteAgents = [entry] });

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.a2A.allowedRemoteAgents[0]");

    }

    [Fact]
    public void Validator_rejects_an_absolute_url_as_the_server_path()
    {

        Result result = Validate(new A2AIntegrationSettings { ServerPath = "https://example.test/api/conclave/a2a" });

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.a2A.serverPath");

    }

    [Fact]
    public void Validator_rejects_a_credential_header_the_http_stack_would_refuse()
    {

        Result result = Validate(new A2AIntegrationSettings { OutboundCredentialHeader = "X Arcanum Key" });

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.a2A.outboundCredentialHeader");

    }

    [Fact]
    public void Validator_accepts_declared_skills_and_modalities()
    {

        Result result = Validate(new A2AIntegrationSettings
        {
            InputModes = ["text/plain", "text/markdown"],
            OutputModes = ["text/plain"],
            Skills =
            [
                new A2ASkillSettings { Id = "code-review", Name = "Code review", OutputModes = ["text/markdown"] },
                new A2ASkillSettings { Id = "apprentice-goal-execution" },
            ],
        });

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

    }

    [Fact]
    public void Validator_rejects_a_declared_skill_with_no_id()
    {

        // A skill with no id never reaches the card, so an operator who declares one would otherwise be
        // left wondering why it never appeared (issue #63).
        Result result = Validate(new A2AIntegrationSettings
        {
            Skills = [new A2ASkillSettings { Name = "nameless" }],
        });

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.a2A.skills[0].id");

    }

    [Fact]
    public void Validator_rejects_duplicate_declared_skill_ids()
    {

        Result result = Validate(new A2AIntegrationSettings
        {
            Skills =
            [
                new A2ASkillSettings { Id = "code-review" },
                new A2ASkillSettings { Id = "Code-Review" },
            ],
        });

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.a2A.skills[1].id");

    }

    [Theory]
    [InlineData("text")]
    [InlineData("text/")]
    [InlineData("/plain")]
    [InlineData("text / plain")]
    public void Validator_rejects_an_advertised_modality_that_is_not_a_media_type(string mode)
    {

        Result result = Validate(new A2AIntegrationSettings { OutputModes = [mode] });

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "integrations.a2A.outputModes");

    }

    private static Result Validate(A2AIntegrationSettings a2a) =>
        new ConfigurationValidator().Validate(new ArcanumSettings
        {
            Integrations = new IntegrationSettings { A2A = a2a },
        });

    [Fact]
    public void Conclave_feature_can_enable_Conclave_without_A2A()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Conclave = true },
        };

        ConclaveSettings conclave = settings.ResolveConclave();

        Assert.True(conclave.Enabled);

        Assert.False(conclave.A2A.Enabled);

        Assert.False(conclave.A2A.ServerEnabled);

        Assert.False(conclave.A2A.ClientEnabled);

    }

}
