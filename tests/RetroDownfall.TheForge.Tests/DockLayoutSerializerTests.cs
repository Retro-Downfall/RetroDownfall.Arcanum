using System.Text.Json;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.ViewModels.Docking;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class DockLayoutSerializerTests
{

    [Fact]
    public void RoundTrip_PreservesDefaultLayout()
    {

        TheForgeDockLayoutDto original = DockLayoutSerializer.CreateDefaultDto();

        string json = DockLayoutSerializer.Serialize(original);

        TheForgeDockLayoutDto restored = DockLayoutSerializer.DeserializeOrDefault(json);

        Assert.Equal(DockLayoutDefaults.SchemaVersion, restored.SchemaVersion);

        Assert.Equal(original.Tools.Count, restored.Tools.Count);

        Assert.Equal(original.ActiveLeftToolId, restored.ActiveLeftToolId);

        Assert.Equal(original.LeftWidth, restored.LeftWidth);

    }

    [Fact]
    public void Deserialize_IgnoresUnknownToolIds()
    {

        TheForgeDockLayoutDto dto = DockLayoutSerializer.CreateDefaultDto() with
        {
            Tools = DockLayoutSerializer.CreateDefaultDto().Tools
                .Append(new TheForgeDockToolLayoutDto("floatingPane", "Left", "Left", true, 99))
                .ToList(),
        };

        string json = DockLayoutSerializer.Serialize(dto);

        TheForgeDockLayoutDto restored = DockLayoutSerializer.DeserializeOrDefault(json);

        Assert.DoesNotContain(restored.Tools, t => t.ToolId == "floatingPane");

        Assert.Contains(restored.Tools, t => t.ToolId == DockToolId.Atelier);

    }

    [Fact]
    public void Deserialize_InsertsMissingKnownTools()
    {

        TheForgeDockLayoutDto dto = new(
            1,
            [new TheForgeDockToolLayoutDto(DockToolId.Atelier, "Left", "Left", true, 0)],
            DockToolId.Atelier,
            null,
            null,
            260,
            330,
            190);

        TheForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        foreach (string id in DockToolId.All)
        {

            Assert.Contains(restored.Tools, t => t.ToolId == id);

        }

    }

    [Fact]
    public void Deserialize_CorruptString_FallsBackToDefaults()
    {

        TheForgeDockLayoutDto restored = DockLayoutSerializer.DeserializeOrDefault("{ not json");

        Assert.Equal(DockLayoutDefaults.SchemaVersion, restored.SchemaVersion);

        Assert.Equal(DockToolId.All.Count, restored.Tools.Count);

    }

    [Fact]
    public void Normalize_ClampsInvalidSizes()
    {

        TheForgeDockLayoutDto dto = DockLayoutSerializer.CreateDefaultDto() with
        {
            LeftWidth = double.NaN,
            RightWidth = double.PositiveInfinity,
            BottomHeight = -50,
        };

        TheForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        Assert.Equal(DockLayoutDefaults.DefaultLeftWidth, restored.LeftWidth);

        Assert.Equal(DockLayoutDefaults.DefaultRightWidth, restored.RightWidth);

        Assert.Equal(DockLayoutDefaults.DefaultBottomHeight, restored.BottomHeight);

        TheForgeDockLayoutDto huge = DockLayoutSerializer.Normalize(
            DockLayoutSerializer.CreateDefaultDto() with { LeftWidth = 10_000 });

        Assert.Equal(DockLayoutDefaults.DefaultLeftWidth, huge.LeftWidth);

    }

    [Fact]
    public void Normalize_InvalidRegions_FallBackWithoutCrash()
    {

        TheForgeDockLayoutDto dto = new(
            1,
            [
                new TheForgeDockToolLayoutDto(DockToolId.Gatehouse, "Floating", "Document", true, 0),
                new TheForgeDockToolLayoutDto(DockToolId.Atelier, "Left", "Left", true, 0),
            ],
            DockToolId.Atelier,
            DockToolId.Gatehouse,
            null,
            260,
            330,
            190);

        TheForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        TheForgeDockToolLayoutDto gatehouse = Assert.Single(restored.Tools, t => t.ToolId == DockToolId.Gatehouse);

        Assert.Equal("Right", gatehouse.Region);

    }

    [Fact]
    public void Normalize_DuplicateToolIds_KeepsOne()
    {

        TheForgeDockLayoutDto dto = new(
            1,
            [
                new TheForgeDockToolLayoutDto(DockToolId.Atelier, "Left", "Left", true, 0),
                new TheForgeDockToolLayoutDto(DockToolId.Atelier, "Right", "Right", true, 1),
            ],
            DockToolId.Atelier,
            null,
            null,
            260,
            330,
            190);

        TheForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        Assert.Equal(1, restored.Tools.Count(t => t.ToolId == DockToolId.Atelier));

        Assert.Equal("Left", restored.Tools.Single(t => t.ToolId == DockToolId.Atelier).Region);

    }

    [Fact]
    public void Serialize_UsesSourceGeneratedContext()
    {

        TheForgeDockLayoutDto dto = DockLayoutSerializer.CreateDefaultDto();

        string viaHelper = DockLayoutSerializer.Serialize(dto);

        string viaContext = JsonSerializer.Serialize(dto, TheForgeSettingsJsonContext.Default.TheForgeDockLayoutDto);

        Assert.Equal(viaContext, viaHelper);

        Assert.Contains("\"schemaVersion\":1", viaHelper);

    }

}
