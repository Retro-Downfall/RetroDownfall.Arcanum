using System.Net;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// GET /api/scalar (W2-11) — the Content-Security-Policy the Scalar sub-group emits must actually
/// admit the inline bootstrap script Scalar's own rendered page ships, or the page silently renders
/// an empty shell with only a console CSP violation and no in-page explanation.
/// </summary>
/// <remarks>
/// Declares the "ApiHost" collection even though it builds its own dedicated
/// <see cref="ArcanumWebApplicationFactory"/> rather than the collection's shared instance:
/// <c>Arcanum:Features:ScalarUi</c> is read from raw <c>IConfiguration</c>
/// (<c>ApiBootstrapper.MapArcanumEndpoints</c>), which <see cref="ArcanumWebApplicationFactory.SettingsOverride"/>
/// does not reach (it only patches the bound <c>ArcanumSettings</c>), and two hosts sharing one
/// factory instance collide seeding the same Grimoire database (see that property's remarks) — so
/// this needs its own instance, enabled the same way an operator would: the
/// <c>ARCANUM_Arcanum__Features__ScalarUi</c> process environment variable
/// (<see cref="global::RetroDownfall.Arcanum.Core.Configuration.ConfigurationEnvironmentResolver"/>'s
/// general override prefix), set before the factory's host builds. Every
/// <see cref="ArcanumWebApplicationFactory"/> still mutates other process-global environment
/// variables in its constructor, so this has to serialize with every other test that constructs one.
/// </remarks>
[Collection("ApiHost")]
public sealed class ApiBootstrapperScalarTests
{

    private const string ScalarUiEnvironmentVariable = "ARCANUM_Arcanum__Features__ScalarUi";

    [SkippableFact]
    public async Task GetScalar_CspNonceMatchesTheNonceOnThePagesScriptTags()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string? original = global::System.Environment.GetEnvironmentVariable(ScalarUiEnvironmentVariable);

        global::System.Environment.SetEnvironmentVariable(ScalarUiEnvironmentVariable, bool.TrueString);

        try
        {

            await using ArcanumWebApplicationFactory factory = new();

            HttpClient client = factory.CreateAuthenticatedClient();

            HttpResponseMessage response = await client.GetAsync("/api/scalar");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(
                response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? cspValues),
                "Expected the Scalar sub-group's Content-Security-Policy header to be present.");

            string csp = Assert.Single(cspValues!);

            Match nonceMatch = Regex.Match(csp, "'nonce-([^']+)'");

            Assert.True(nonceMatch.Success, $"Expected a nonce source in the CSP's script-src; got: {csp}");

            string nonce = nonceMatch.Groups[1].Value;

            string body = await response.Content.ReadAsStringAsync();

            // The CSP's nonce is only useful if it is the exact value Scalar stamped onto its own
            // script tags — a nonce present in the header but absent from the page would still block
            // every script, which is the shape of the original defect (a CSP that does not admit what
            // the page actually ships).
            Assert.Contains($"nonce=\"{nonce}\"", body, StringComparison.Ordinal);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(ScalarUiEnvironmentVariable, original);

        }

    }

}
