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

        ForgeDockLayoutDto original = DockLayoutSerializer.CreateDefaultDto();

        string json = DockLayoutSerializer.Serialize(original);

        ForgeDockLayoutDto restored = DockLayoutSerializer.DeserializeOrDefault(json);

        Assert.Equal(DockLayoutDefaults.SchemaVersion, restored.SchemaVersion);

        Assert.Equal(original.Tools.Count, restored.Tools.Count);

        Assert.Equal(original.ActiveLeftToolId, restored.ActiveLeftToolId);

        Assert.Equal(original.LeftWidth, restored.LeftWidth);

    }

    [Fact]
    public void Deserialize_IgnoresUnknownToolIds()
    {

        ForgeDockLayoutDto dto = DockLayoutSerializer.CreateDefaultDto() with
        {
            Tools = DockLayoutSerializer.CreateDefaultDto().Tools
                .Append(new ForgeDockToolLayoutDto("floatingPane", "Left", "Left", true, 99))
                .ToList(),
        };

        string json = DockLayoutSerializer.Serialize(dto);

        ForgeDockLayoutDto restored = DockLayoutSerializer.DeserializeOrDefault(json);

        Assert.DoesNotContain(restored.Tools, t => t.ToolId == "floatingPane");

        Assert.Contains(restored.Tools, t => t.ToolId == DockToolId.Atelier);

    }

    [Fact]
    public void Deserialize_InsertsMissingKnownTools()
    {

        ForgeDockLayoutDto dto = new(
            1,
            [new ForgeDockToolLayoutDto(DockToolId.Atelier, "Left", "Left", true, 0)],
            DockToolId.Atelier,
            null,
            null,
            260,
            330,
            190);

        ForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        foreach (string id in DockToolId.All)
        {

            Assert.Contains(restored.Tools, t => t.ToolId == id);

        }

    }

    [Fact]
    public void Deserialize_CorruptString_FallsBackToDefaults()
    {

        ForgeDockLayoutDto restored = DockLayoutSerializer.DeserializeOrDefault("{ not json");

        Assert.Equal(DockLayoutDefaults.SchemaVersion, restored.SchemaVersion);

        Assert.Equal(DockToolId.All.Count, restored.Tools.Count);

    }

    [Fact]
    public void Normalize_ClampsInvalidSizes()
    {

        ForgeDockLayoutDto dto = DockLayoutSerializer.CreateDefaultDto() with
        {
            LeftWidth = double.NaN,
            RightWidth = double.PositiveInfinity,
            BottomHeight = -50,
        };

        ForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        Assert.Equal(DockLayoutDefaults.DefaultLeftWidth, restored.LeftWidth);

        Assert.Equal(DockLayoutDefaults.DefaultRightWidth, restored.RightWidth);

        Assert.Equal(DockLayoutDefaults.DefaultBottomHeight, restored.BottomHeight);

        ForgeDockLayoutDto huge = DockLayoutSerializer.Normalize(
            DockLayoutSerializer.CreateDefaultDto() with { LeftWidth = 10_000 });

        Assert.Equal(DockLayoutDefaults.DefaultLeftWidth, huge.LeftWidth);

    }

    [Fact]
    public void Normalize_InvalidRegions_FallBackWithoutCrash()
    {

        ForgeDockLayoutDto dto = new(
            1,
            [
                new ForgeDockToolLayoutDto(DockToolId.Gatehouse, "Floating", "Document", true, 0),
                new ForgeDockToolLayoutDto(DockToolId.Atelier, "Left", "Left", true, 0),
            ],
            DockToolId.Atelier,
            DockToolId.Gatehouse,
            null,
            260,
            330,
            190);

        ForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        ForgeDockToolLayoutDto gatehouse = Assert.Single(restored.Tools, t => t.ToolId == DockToolId.Gatehouse);

        Assert.Equal("Right", gatehouse.Region);

    }

    [Fact]
    public void Normalize_DuplicateToolIds_KeepsOne()
    {

        ForgeDockLayoutDto dto = new(
            1,
            [
                new ForgeDockToolLayoutDto(DockToolId.Atelier, "Left", "Left", true, 0),
                new ForgeDockToolLayoutDto(DockToolId.Atelier, "Right", "Right", true, 1),
            ],
            DockToolId.Atelier,
            null,
            null,
            260,
            330,
            190);

        ForgeDockLayoutDto restored = DockLayoutSerializer.Normalize(dto);

        Assert.Equal(1, restored.Tools.Count(t => t.ToolId == DockToolId.Atelier));

        Assert.Equal("Left", restored.Tools.Single(t => t.ToolId == DockToolId.Atelier).Region);

    }

    [Fact]
    public void Serialize_UsesSourceGeneratedContext()
    {

        ForgeDockLayoutDto dto = DockLayoutSerializer.CreateDefaultDto();

        string viaHelper = DockLayoutSerializer.Serialize(dto);

        string viaContext = JsonSerializer.Serialize(dto, ForgeSettingsJsonContext.Default.ForgeDockLayoutDto);

        Assert.Equal(viaContext, viaHelper);

        Assert.Contains("\"schemaVersion\":1", viaHelper);

    }

}
