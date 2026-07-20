using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using TgAttr = Terminal.Gui.Drawing.Attribute;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Terminal.Gui color schemes for Command Center. Honors monochrome / NO_COLOR.
/// Accents: royal blue + silver chrome (double borders).
/// </summary>
internal static class CommandCenterTheme
{
    public const string BaseScheme = "ArcanumCC";

    public const string HeaderScheme = "ArcanumCC.Header";

    public const string BannerScheme = "ArcanumCC.Banner";

    public const string SessionScheme = "ArcanumCC.Session";

    public const string SidebarScheme = "ArcanumCC.Sidebar";

    public const string InputScheme = "ArcanumCC.Input";

    /// <summary>Modal overlay — blue field with silver lettering.</summary>
    public const string OverlayScheme = "ArcanumCC.Overlay";

    /// <summary>Pane chrome — silver double-line borders.</summary>
    public static LineStyle PaneBorderStyle { get; private set; } = LineStyle.Double;

    public static void Apply(bool monochrome)
    {
        if (monochrome)
        {
            PaneBorderStyle = LineStyle.Double;
            RegisterMono();
        }
        else
        {
            PaneBorderStyle = LineStyle.Double;
            RegisterColor();
        }
    }

    private static void RegisterColor()
    {
        // Silver chrome + royal blue accents (truecolor when the terminal supports it).
        Color silver = new(192, 192, 192, 255);
        Color silverBright = new(220, 220, 225, 255);
        Color blue = new(30, 58, 138, 255);       // #1E3A8A
        Color blueBright = new(96, 165, 250, 255); // #60A5FA
        // Slight warm charcoal — less harsh than pure #000000.
        Color bg = new(18, 18, 20, 255);

        TgAttr normal = new(Color.White, bg);
        TgAttr muted = new(silver, bg);
        TgAttr focus = new(Color.BrightYellow, bg);
        TgAttr hot = new(blueBright, bg);
        TgAttr header = new(blueBright, bg);
        TgAttr banner = new(silverBright, bg);
        TgAttr border = new(silver, bg);
        TgAttr borderActive = new(blueBright, bg);
        TgAttr input = new(Color.White, Color.DarkGray);
        TgAttr inputFocus = new(Color.Black, blueBright);
        TgAttr highlight = new(Color.Black, blue);
        TgAttr disabled = new(Color.DarkGray, bg);

        Upsert(BaseScheme, new Scheme
        {
            Normal = normal,
            HotNormal = hot,
            Focus = focus,
            HotFocus = focus,
            Active = borderActive,
            HotActive = borderActive,
            Highlight = highlight,
            Disabled = disabled,
            Editable = input,
            ReadOnly = muted,
        });

        Upsert(HeaderScheme, new Scheme
        {
            Normal = header,
            HotNormal = header,
            Focus = header,
            HotFocus = header,
            Active = border,
            HotActive = borderActive,
            Highlight = highlight,
            Disabled = disabled,
            Editable = header,
            ReadOnly = muted,
        });

        Upsert(BannerScheme, new Scheme
        {
            Normal = banner,
            HotNormal = banner,
            Focus = banner,
            HotFocus = banner,
            Active = border,
            HotActive = border,
            Highlight = highlight,
            Disabled = disabled,
            Editable = banner,
            ReadOnly = banner,
        });

        Upsert(SessionScheme, new Scheme
        {
            Normal = normal,
            HotNormal = hot,
            Focus = focus,
            HotFocus = focus,
            Active = border,
            HotActive = borderActive,
            Highlight = highlight,
            Disabled = disabled,
            Editable = normal,
            ReadOnly = muted,
        });

        Upsert(SidebarScheme, new Scheme
        {
            Normal = muted,
            HotNormal = hot,
            Focus = focus,
            HotFocus = focus,
            Active = border,
            HotActive = borderActive,
            Highlight = highlight,
            Disabled = disabled,
            Editable = muted,
            ReadOnly = muted,
        });

        Upsert(InputScheme, new Scheme
        {
            Normal = input,
            HotNormal = inputFocus,
            Focus = inputFocus,
            HotFocus = inputFocus,
            Active = border,
            HotActive = borderActive,
            Highlight = inputFocus,
            Disabled = disabled,
            Editable = input,
            ReadOnly = muted,
        });

        // Modal: banner-border blue (#60A5FA) ground, silver type.
        TgAttr overlayNormal = new(silverBright, blueBright);
        TgAttr overlayFocus = new(Color.White, blueBright);
        TgAttr overlayHot = new(silver, blueBright);
        TgAttr overlayBorder = new(silverBright, blueBright);
        TgAttr overlayHighlight = new(blue, silverBright);
        TgAttr overlayDisabled = new(silver, blueBright);
        TgAttr overlayEditable = new(silverBright, blueBright);

        Upsert(OverlayScheme, new Scheme
        {
            Normal = overlayNormal,
            HotNormal = overlayHot,
            Focus = overlayFocus,
            HotFocus = overlayFocus,
            Active = overlayBorder,
            HotActive = overlayBorder,
            Highlight = overlayHighlight,
            Disabled = overlayDisabled,
            Editable = overlayEditable,
            ReadOnly = overlayNormal,
        });
    }

    private static void RegisterMono()
    {
        TgAttr normal = new(Color.White, Color.Black);
        TgAttr muted = new(Color.Gray, Color.Black);
        TgAttr focus = new(Color.White, Color.Black);
        TgAttr highlight = new(Color.Black, Color.Gray);
        TgAttr disabled = new(Color.DarkGray, Color.Black);
        TgAttr input = new(Color.White, Color.DarkGray);
        TgAttr inputFocus = new(Color.Black, Color.White);
        TgAttr overlay = new(Color.White, Color.Blue);
        TgAttr overlayHighlight = new(Color.Blue, Color.White);

        Scheme mono = new()
        {
            Normal = normal,
            HotNormal = focus,
            Focus = focus,
            HotFocus = focus,
            Active = focus,
            HotActive = focus,
            Highlight = highlight,
            Disabled = disabled,
            Editable = input,
            ReadOnly = muted,
        };

        Upsert(BaseScheme, mono);
        Upsert(HeaderScheme, mono);
        Upsert(BannerScheme, mono);
        Upsert(SessionScheme, mono);
        Upsert(SidebarScheme, new Scheme
        {
            Normal = muted,
            HotNormal = focus,
            Focus = focus,
            HotFocus = focus,
            Active = focus,
            HotActive = focus,
            Highlight = highlight,
            Disabled = disabled,
            Editable = muted,
            ReadOnly = muted,
        });
        Upsert(InputScheme, new Scheme
        {
            Normal = input,
            HotNormal = inputFocus,
            Focus = inputFocus,
            HotFocus = inputFocus,
            Active = inputFocus,
            HotActive = inputFocus,
            Highlight = inputFocus,
            Disabled = disabled,
            Editable = input,
            ReadOnly = muted,
        });
        Upsert(OverlayScheme, new Scheme
        {
            Normal = overlay,
            HotNormal = overlay,
            Focus = overlay,
            HotFocus = overlay,
            Active = overlay,
            HotActive = overlay,
            Highlight = overlayHighlight,
            Disabled = overlay,
            Editable = overlay,
            ReadOnly = overlay,
        });
    }

    private static void Upsert(string name, Scheme scheme)
    {
        if (SchemeManager.TryGetScheme(name, out _))
        {
            SchemeManager.RemoveScheme(name);
        }

        SchemeManager.AddScheme(name, scheme);
    }
}
