using System.Reflection;

using Avalonia.Controls;

using RetroDownfall.Compendium.Ux.Models;

using RetroDownfall.Compendium.Ux.Views;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("AvaloniaBinding")]
public sealed class PresetsPageTests
{

    [Fact]

    public void Main_window_routes_Presets_to_a_dedicated_polished_page()
    {

        MethodInfo factory = typeof(MainWindow).GetMethod(
            "CreateSectionContent",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MainWindow section factory was not found.");

        Control content = Assert.IsAssignableFrom<Control>(
            factory.Invoke(null, [ConfigSection.Presets]));

        Assert.Equal("PresetsPage", content.GetType().Name);

    }

}
