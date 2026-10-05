using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A theory's parameters have to be something xunit can serialize.
/// </summary>
/// <remarks>
/// A delegate parameter (the shape <c>TheoryData&lt;Func&lt;...&gt;&gt;</c> produces) cannot be serialized, so
/// xunit falls back to one opaque test case for the whole theory and prints a "Non-serializable data" notice
/// on every run: a failing row is not named, and the rows cannot be run or filtered one by one. Pass an index
/// or a name and look the delegate up in the test instead.
/// </remarks>
public sealed class TheoryParameterContractTests
{
    [Fact]
    public void No_theory_takes_a_delegate_parameter()
    {
        string[] violations = typeof(TheoryParameterContractTests).Assembly
            .GetTypes()
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(static method => method.IsDefined(typeof(TheoryAttribute), inherit: true))
            .SelectMany(static method => method.GetParameters()
                .Where(static parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType))
                .Select(parameter => $"{method.DeclaringType!.FullName}.{method.Name} takes the delegate parameter '{parameter.Name}' ({parameter.ParameterType.Name})."))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }
}
