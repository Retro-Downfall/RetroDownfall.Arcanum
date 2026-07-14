using System.Text.Json;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>
/// Source-generated serialize/deserialize for <see cref="TheForgeDockLayoutDto"/> with validation,
/// unknown-id tolerance, and default insertion for missing tools.
/// </summary>
public static class DockLayoutSerializer
{

    public static string Serialize(TheForgeDockLayoutDto layout) =>
        JsonSerializer.Serialize(layout, TheForgeSettingsJsonContext.Default.TheForgeDockLayoutDto);

    public static TheForgeDockLayoutDto DeserializeOrDefault(string? layoutState)
    {

        if (string.IsNullOrWhiteSpace(layoutState))
        {

            return CreateDefaultDto();

        }

        TheForgeDockLayoutDto? dto;

        try
        {

            dto = JsonSerializer.Deserialize(layoutState, TheForgeSettingsJsonContext.Default.TheForgeDockLayoutDto);

        }
        catch (JsonException)
        {

            return CreateDefaultDto();

        }

        if (dto is null)
        {

            return CreateDefaultDto();

        }

        return Normalize(dto);

    }

    public static TheForgeDockLayoutDto CreateDefaultDto()
    {

        List<TheForgeDockToolLayoutDto> tools = DockLayoutDefaults.Tools
            .Select(static t => new TheForgeDockToolLayoutDto(
                t.ToolId,
                RegionToString(t.Region),
                RegionToString(t.Region),
                IsVisible: true,
                t.Order))
            .ToList();

        return new TheForgeDockLayoutDto(
            DockLayoutDefaults.SchemaVersion,
            tools,
            ActiveLeftToolId: DockToolId.Atelier,
            ActiveRightToolId: DockToolId.Gatehouse,
            ActiveBottomToolId: DockToolId.Output,
            DockLayoutDefaults.DefaultLeftWidth,
            DockLayoutDefaults.DefaultRightWidth,
            DockLayoutDefaults.DefaultBottomHeight);

    }

    public static TheForgeDockLayoutDto Normalize(TheForgeDockLayoutDto dto)
    {

        Dictionary<string, TheForgeDockToolLayoutDto> byId = new(StringComparer.Ordinal);

        foreach (TheForgeDockToolLayoutDto tool in dto.Tools ?? [])
        {

            if (string.IsNullOrWhiteSpace(tool.ToolId) || !DockToolId.All.Contains(tool.ToolId))
            {

                continue;

            }

            if (byId.ContainsKey(tool.ToolId))
            {

                continue;

            }

            DockRegion region = ParseRegion(tool.Region) ?? DockLayoutDefaults.DefaultRegionFor(tool.ToolId);

            DockRegion lastRegion = DockLayoutSerializer.ParseRegion(tool.LastRegion) is { } parsedLast && parsedLast != DockRegion.Hidden
                ? parsedLast
                : (region == DockRegion.Hidden ? DockLayoutDefaults.DefaultRegionFor(tool.ToolId) : region);

            if (region == DockRegion.Hidden)
            {

                // Keep LastRegion as non-hidden.
            }
            else
            {

                lastRegion = region;

            }

            byId[tool.ToolId] = new TheForgeDockToolLayoutDto(
                tool.ToolId,
                RegionToString(region),
                RegionToString(lastRegion),
                tool.IsVisible && region != DockRegion.Hidden,
                tool.Order);

        }

        foreach (var def in DockLayoutDefaults.Tools)
        {

            if (byId.ContainsKey(def.ToolId))
            {

                continue;

            }

            byId[def.ToolId] = new TheForgeDockToolLayoutDto(
                def.ToolId,
                RegionToString(def.Region),
                RegionToString(def.Region),
                IsVisible: true,
                def.Order);

        }

        List<TheForgeDockToolLayoutDto> tools = byId.Values
            .OrderBy(static t => ParseRegion(t.Region) ?? DockRegion.Hidden)
            .ThenBy(static t => t.Order)
            .ThenBy(static t => t.ToolId, StringComparer.Ordinal)
            .ToList();

        return new TheForgeDockLayoutDto(
            SchemaVersion: DockLayoutDefaults.SchemaVersion,
            tools,
            ClampActiveId(dto.ActiveLeftToolId, tools, DockRegion.Left, DockToolId.Atelier),
            ClampActiveId(dto.ActiveRightToolId, tools, DockRegion.Right, DockToolId.Gatehouse),
            ClampActiveId(dto.ActiveBottomToolId, tools, DockRegion.Bottom, DockToolId.Output),
            ClampSize(dto.LeftWidth, DockLayoutDefaults.MinLeftWidth, DockLayoutDefaults.MaxLeftWidth, DockLayoutDefaults.DefaultLeftWidth),
            ClampSize(dto.RightWidth, DockLayoutDefaults.MinRightWidth, DockLayoutDefaults.MaxRightWidth, DockLayoutDefaults.DefaultRightWidth),
            ClampSize(dto.BottomHeight, DockLayoutDefaults.MinBottomHeight, DockLayoutDefaults.MaxBottomHeight, DockLayoutDefaults.DefaultBottomHeight));

    }

    public static string RegionToString(DockRegion region) => region switch
    {
        DockRegion.Left => "Left",
        DockRegion.Right => "Right",
        DockRegion.Bottom => "Bottom",
        DockRegion.Hidden => "Hidden",
        _ => "Hidden",
    };

    public static DockRegion? ParseRegion(string? value)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return null;

        }

        return value.Trim() switch
        {
            "Left" or "left" => DockRegion.Left,
            "Right" or "right" => DockRegion.Right,
            "Bottom" or "bottom" => DockRegion.Bottom,
            "Hidden" or "hidden" => DockRegion.Hidden,
            _ => null,
        };

    }

    private static string? ClampActiveId(
        string? activeId,
        IReadOnlyList<TheForgeDockToolLayoutDto> tools,
        DockRegion region,
        string fallback)
    {

        string regionName = RegionToString(region);

        if (!string.IsNullOrWhiteSpace(activeId)
            && tools.Any(t => t.ToolId == activeId && t.IsVisible && t.Region == regionName))
        {

            return activeId;

        }

        TheForgeDockToolLayoutDto? first = tools
            .Where(t => t.IsVisible && t.Region == regionName)
            .OrderBy(t => t.Order)
            .FirstOrDefault();

        return first?.ToolId ?? fallback;

    }

    private static double ClampSize(double value, double min, double max, double fallback)
    {

        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > max * 4)
        {

            return fallback;

        }

        return Math.Clamp(value, min, max);

    }

}
