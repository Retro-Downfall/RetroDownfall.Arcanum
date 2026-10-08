using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// A member of the internal turn request that nothing reads is a promise the type does not keep: a
/// purpose, a human-interaction flag, an idempotency flag and an accounting handle were written by the
/// coordinator at every call and read by no one, which a test could fill and production could not.
/// </summary>
public sealed class TurnExecutionRequestMemberTests
{
    [Fact]
    public void Every_turn_request_member_is_read_by_production_code()
    {
        string[] names =
        [
            .. typeof(TurnExecutionRequest)
                .GetProperties()
                .Select(static property => property.Name)
                .Where(static name => name != "EqualityContract"),
        ];

        Assert.NotEmpty(names);

        ProductionSource[] sources =
        [
            .. ProductionSourceInventory.Sources().Where(static source =>
                !source.Is("TurnExecutionRequest.cs")
                && source.RelativePath.StartsWith("src/RetroDownfall.Arcanum.Api/", StringComparison.Ordinal)),
        ];

        // The receiver is spelled the way the engine, coordinator and provider spell it, so an unrelated
        // member of the same name (HttpContext.Request) is not counted as a read.
        string[] unread =
        [
            .. names.Where(name =>
            {
                Regex read = new(
                    @"\b(request|turnRequest)\." + Regex.Escape(name) + @"\b(?!\s*=[^=])",
                    RegexOptions.CultureInvariant);

                return !sources.Any(source => read.IsMatch(source.Text));
            }),
        ];

        Assert.True(
            unread.Length == 0,
            "TurnExecutionRequest members no production code reads (delete them or read them): "
            + string.Join(", ", unread));
    }
}
