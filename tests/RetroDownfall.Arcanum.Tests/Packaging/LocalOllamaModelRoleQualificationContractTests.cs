using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection("ProcessEnvironment")]
public sealed class LocalOllamaModelRoleQualificationContractTests
{
    [Theory]
    [InlineData("flint:40b", "qwen3.8:27b", false)]
    [InlineData("flint:40b", "qwen3.8:27b", true)]
    [InlineData("Tag:a", "tag:a", false)]
    [InlineData("qwen3.8:27b", "qwen3.8:27b", false)]
    [InlineData("qwen3.8:27b", null, false)]
    public async Task Configuration_declares_role_capabilities_and_deduplicates_shared_model(
        string textModel, string? visionModel, bool perplexity)
    {
        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("model-roles-config");

        try
        {
            isolation.Prepare();

            string path = Path.Combine(isolation.TestHome, "arcanum.json");

            await PublishedArcanumOllamaQualificationTests.WriteConfigurationAsync(
                path, isolation.TestHome, 12345, new Uri("http://127.0.0.1:11434/v1/"),
                textModel, perplexity, visionModel);

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));

            JsonElement settings = json.RootElement.GetProperty("Arcanum");

            Assert.Equal(textModel, settings.GetProperty("defaultModel").GetString());

            JsonElement[] models = settings.GetProperty("providers")[0].GetProperty("models").EnumerateArray().ToArray();

            bool shared = visionModel is null || visionModel == textModel;

            Assert.Equal(shared ? 1 : 2, models.Length);

            JsonElement text = Assert.Single(models, item => item.GetProperty("name").GetString() == textModel);

            Assert.True(text.GetProperty("supportsTools").GetBoolean());

            Assert.Equal(shared, text.GetProperty("supportsVision").GetBoolean());

            JsonElement vision = Assert.Single(models, item => item.GetProperty("name").GetString() == (visionModel ?? textModel));

            Assert.True(vision.GetProperty("supportsVision").GetBoolean());

            Assert.True(vision.GetProperty("supportsTools").GetBoolean());

            Assert.Equal(perplexity, settings.GetProperty("features").GetProperty("webBrowsing").GetBoolean());

            Assert.False(settings.GetProperty("features").GetProperty("embeddings").GetBoolean());
        }
        finally
        {
            isolation.Delete();
        }
    }

    [SkippableTheory]
    [InlineData("default", "gemma4:e4b", "gemma4:e4b")]
    [InlineData("legacy", "legacy-model:1", "legacy-model:1")]
    [InlineData("split", "flint:40b", "qwen3.8:27b")]
    [InlineData("text-only", "flint:40b", "legacy-model:1")]
    [InlineData("vision-only", "legacy-model:1", "qwen3.8:27b")]
    [InlineData("legacy-last", "flint:40b", "qwen3.8:27b")]
    [InlineData("text-legacy-last", "flint:40b", "legacy-model:1")]
    [InlineData("vision-legacy-last", "legacy-model:1", "qwen3.8:27b")]
    public async Task Wrapper_passes_explicit_model_roles_with_order_independent_legacy_fallback(
        string mode, string expectedText, string expectedVision)
    {
        Skip.If(OperatingSystem.IsWindows(), "The Bash behavior fixture requires native POSIX process semantics.");

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama");

        fixture.EnvironmentOverrides["ARCANUM_OLLAMA_QUALIFICATION_MODEL"] = "inherited-text:must-not-run";

        fixture.EnvironmentOverrides["ARCANUM_OLLAMA_QUALIFICATION_VISION_MODEL"] = "inherited-vision:must-not-run";

        List<string> arguments = ["--executable", fixture.Executable, "--image", fixture.Image];

        if (mode is not ("default" or "legacy-last" or "text-legacy-last" or "vision-legacy-last"))
        {
            arguments.AddRange(["--model", "legacy-model:1"]);
        }

        if (mode is "split" or "text-only" or "legacy-last" or "text-legacy-last")
        {
            arguments.AddRange(["--text-model", "flint:40b"]);
        }

        if (mode is "split" or "vision-only" or "legacy-last" or "vision-legacy-last")
        {
            arguments.AddRange(["--vision-model", "qwen3.8:27b"]);
        }

        if (mode is "legacy-last" or "text-legacy-last" or "vision-legacy-last")
        {
            arguments.AddRange(["--model", "legacy-model:1"]);
        }

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-local-ollama-aot.sh"),
            arguments.ToArray());

        Assert.True(result.ExitCode == 0, result.StandardError);

        string[] log = File.ReadAllLines(fixture.DotnetLog);

        Assert.Contains($"ollama-text-model={expectedText}", log);

        Assert.Contains($"ollama-vision-model={expectedVision}", log);
    }

    [Theory]
    [InlineData(null, null, "gemma4:e4b", "gemma4:e4b")]
    [InlineData("flint:40b", null, "flint:40b", "flint:40b")]
    [InlineData("flint:40b", "qwen3.8:27b", "flint:40b", "qwen3.8:27b")]
    [InlineData(null, "qwen3.8:27b", "gemma4:e4b", "qwen3.8:27b")]
    [InlineData(" flint:40b ", " qwen3.8:27b ", "flint:40b", "qwen3.8:27b")]
    public void Managed_roles_preserve_legacy_fallback_and_independent_vision_override(
        string? text, string? vision, string expectedText, string expectedVision)
    {
        var roles = PublishedArcanumOllamaQualificationTests.ResolveModelRoles(text, vision);

        Assert.Equal(expectedText, roles.Text);

        Assert.Equal(expectedVision, roles.Vision);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" \t ", null)]
    [InlineData("flint:40b", "")]
    [InlineData("flint:40b", " \r\n ")]
    public void Managed_roles_reject_explicit_blank_names(string text, string? vision)
    {
        Assert.Throws<ArgumentException>(() => PublishedArcanumOllamaQualificationTests.ResolveModelRoles(text, vision));
    }

    [SkippableTheory]
    [InlineData("--model", null)]
    [InlineData("--text-model", null)]
    [InlineData("--vision-model", null)]
    [InlineData("--model", "--configuration")]
    [InlineData("--text-model", "--configuration")]
    [InlineData("--vision-model", "--configuration")]
    [InlineData("--model", "-h")]
    [InlineData("--text-model", "-h")]
    [InlineData("--vision-model", "-h")]
    public async Task Wrapper_rejects_missing_role_names_before_launch(string option, string? nextOption)
    {
        Skip.If(OperatingSystem.IsWindows(), "The Bash behavior fixture requires native POSIX process semantics.");

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama");

        List<string> arguments = ["--executable", fixture.Executable, "--image", fixture.Image, option];

        if (nextOption is not null) arguments.Add(nextOption);

        if (nextOption == "--configuration") arguments.Add("Release");

        var result = await fixture.RunAsync(Path.Combine(fixture.RepositoryRoot, "scripts", "verify-local-ollama-aot.sh"), arguments.ToArray());

        Assert.Equal(2, result.ExitCode);

        Assert.Contains($"{option} requires a tag", result.StandardError, StringComparison.Ordinal);

        Assert.False(File.Exists(fixture.DotnetLog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stored_reply_model_oracle_requires_exact_role_order_and_complete_text_turns(bool perplexity)
    {
        string[] expected = perplexity
            ? ["flint:40b", "flint:40b", "flint:40b", "qwen3.8:27b", "flint:40b"]
            : ["flint:40b", "flint:40b", "qwen3.8:27b", "flint:40b"];

        Assert.True(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch(expected, "flint:40b", "qwen3.8:27b", perplexity));

        Assert.True(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch(
            [.. Enumerable.Repeat("shared-model", expected.Length)], "shared-model", "shared-model", perplexity));

        foreach (int index in Enumerable.Range(0, expected.Length))
        {
            string?[] changed = [.. expected];

            changed[index] = index == expected.Length - 2 ? "flint:40b" : "qwen3.8:27b";

            Assert.False(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch(changed, "flint:40b", "qwen3.8:27b", perplexity));

            changed[index] = string.Empty;

            Assert.False(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch(changed, "flint:40b", "qwen3.8:27b", perplexity));

            changed[index] = null;

            Assert.False(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch(changed, "flint:40b", "qwen3.8:27b", perplexity));
        }

        Assert.False(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch(expected[..^1], "flint:40b", "qwen3.8:27b", perplexity));

        Assert.False(PublishedArcanumOllamaQualificationTests.ReplyModelsMatch([.. expected, "flint:40b"], "flint:40b", "qwen3.8:27b", perplexity));
    }

    [Fact]
    public void Opt_in_harness_routes_each_role_and_checks_installed_tags_before_launch()
    {
        string repository = global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();

        string path = Path.Combine(repository, "tests", "RetroDownfall.Arcanum.Tests", "Packaging", "PublishedArcanumOllamaQualificationTests.cs");

        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        MethodDeclarationSyntax method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(item =>
            item.Identifier.ValueText == nameof(PublishedArcanumOllamaQualificationTests.Published_native_aot_preserves_corrected_file_context_and_runs_vision_across_restart));

        InvocationExpressionSyntax Invocation(string name) => method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(item => item.Expression.ToString() == name);

        VariableDeclaratorSyntax Variable(string name) => method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(item => item.Identifier.ValueText == name);

        void AssertCliRole(string variable, string expected)
        {
            InvocationExpressionSyntax call = Variable(variable).Initializer!.Value.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(item => item.Expression.ToString() == "RunCliAsync");

            var arguments = call.ArgumentList.Arguments;

            int flag = arguments.IndexOf(arguments.Single(argument => argument.Expression is LiteralExpressionSyntax literal
                && literal.Token.ValueText == "--model"));

            Assert.Equal(expected, arguments[flag + 1].Expression.ToString());
        }

        AssertCliRole("firstTurn", "model");

        AssertCliRole("secondTurn", "model");

        AssertCliRole("thirdTurn", "visionModel");

        Assert.Equal("model", Invocation("RunNativeSearchAsync").ArgumentList.Arguments[2].Expression.ToString());

        Assert.Equal("model", Invocation("RunNativeMemoryRecallAsync").ArgumentList.Arguments[3].Expression.ToString());

        Assert.Equal("models.Text", Variable("model").Initializer!.Value.ToString());

        Assert.Equal("models.Vision", Variable("visionModel").Initializer!.Value.ToString());

        foreach (var field in new[]
        {
            (Name: "ModelVariable", Value: "ARCANUM_OLLAMA_QUALIFICATION_MODEL"),
            (Name: "VisionModelVariable", Value: "ARCANUM_OLLAMA_QUALIFICATION_VISION_MODEL"),
        })
        {
            var value = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(item => item.Identifier.ValueText == field.Name);

            Assert.Equal(field.Value, Assert.IsType<LiteralExpressionSyntax>(value.Initializer!.Value).Token.ValueText);
        }

        var resolution = Invocation("ResolveModelRoles");

        Assert.Contains("GetEnvironmentVariable(ModelVariable)", resolution.ToString(), StringComparison.Ordinal);

        Assert.Contains("GetEnvironmentVariable(VisionModelVariable)", resolution.ToString(), StringComparison.Ordinal);

        int optIn = Invocation("Skip.IfNot").SpanStart;

        Assert.True(resolution.SpanStart > optIn);

        var installed = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(item => item.Expression.ToString() == "AssertModelIsInstalledAsync").ToArray();

        Assert.Equal(new[] { "model", "visionModel", "pdfOptions.Model" }, installed.Select(item => item.ArgumentList.Arguments[1].Expression.ToString()));

        Assert.Equal("pdfOptions is not null", Assert.Single(installed[2].Ancestors().OfType<IfStatementSyntax>()).Condition.ToString());

        int firstLaunch = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(item => item.Expression.ToString() == "PublishedHost.Start").Min(item => item.SpanStart);

        Assert.All(installed, call => Assert.True(call.SpanStart < firstLaunch));

        Assert.All(installed, call => Assert.True(call.SpanStart > optIn));

        Assert.Equal("visionModel", Invocation("WriteConfigurationAsync").ArgumentList.Arguments[6].Expression.ToString());

        Assert.True(Invocation("ReplyModelsMatch").SpanStart > Invocation("RunNativeMemoryRecallAsync").SpanStart);

        Assert.Contains("modelEvidence.ReadReplyModelsAsync(sessionId, modelReadDeadline.Token)", Invocation("ReplyModelsMatch").ToString(), StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("--model", " \t ")]
    [InlineData("--text-model", " \t ")]
    [InlineData("--vision-model", " \t ")]
    [InlineData("--model", "")]
    [InlineData("--text-model", "")]
    [InlineData("--vision-model", "")]
    public async Task Wrapper_rejects_blank_model_roles_before_launching_managed_harness(string option, string value)
    {
        Skip.If(OperatingSystem.IsWindows(), "The Bash behavior fixture requires native POSIX process semantics.");

        using PublishedApphostVerificationScriptTests.ScriptFixture fixture = new(receiptKind: "ollama");

        PublishedApphostVerificationScriptTests.ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-local-ollama-aot.sh"),
            "--executable", fixture.Executable, "--image", fixture.Image, option, value);

        Assert.Equal(2, result.ExitCode);

        Assert.Contains($"{option} must not be blank", result.StandardError, StringComparison.Ordinal);

        Assert.False(File.Exists(fixture.DotnetLog));
    }
}
