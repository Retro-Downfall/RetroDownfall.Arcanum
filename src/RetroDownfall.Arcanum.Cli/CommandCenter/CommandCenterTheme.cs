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

        TgAttr normal = new(Color.White, Color.Black);
        TgAttr muted = new(silver, Color.Black);
        TgAttr focus = new(Color.BrightYellow, Color.Black);
        TgAttr hot = new(blueBright, Color.Black);
        TgAttr header = new(blueBright, Color.Black);
        TgAttr banner = new(silverBright, Color.Black);
        TgAttr border = new(silver, Color.Black);
        TgAttr borderActive = new(blueBright, Color.Black);
        TgAttr input = new(Color.White, Color.DarkGray);
        TgAttr inputFocus = new(Color.Black, blueBright);
        TgAttr highlight = new(Color.Black, blue);
        TgAttr disabled = new(Color.DarkGray, Color.Black);

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
