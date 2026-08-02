using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Compendium.Ux.Models;

using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Services;

internal sealed record CompendiumStartupArguments(
    ApplicationDeepLink? DeepLink,
    string[] AvaloniaArguments);

internal static class CompendiumDeepLinkStartup
{

    public static CompendiumStartupArguments Parse(string[] arguments)
    {

        ApplicationDeepLink? deepLink;

        try
        {

            deepLink = ApplicationDeepLinkCodec.ParseArguments(
                arguments,
                DesktopApplication.Compendium);

        }
        catch
        {

            deepLink = null;

        }

        return new(
            deepLink,
            RemovePrivateArguments(arguments));

    }

    public static void Apply(
        ApplicationDeepLink? deepLink,
        ConfigurationViewModel viewModel)
    {

        ArgumentNullException.ThrowIfNull(viewModel);

        viewModel.SelectedSection = ConfigSection.Edition;

        if (deepLink is not
            {

                SchemaVersion: ApplicationDeepLink.CurrentSchemaVersion,

                TargetApplication: DesktopApplication.Compendium,

            })
        {

            return;

        }

        if (deepLink.InitialView is null or ApplicationInitialView.Settings)
        {

            viewModel.SelectedSection = ConfigSection.Edition;

        }

    }

    private static string[] RemovePrivateArguments(string[] arguments)
    {

        List<string> forwarded = new(arguments.Length);

        for (int index = 0; index < arguments.Length; index++)
        {

            if (!string.Equals(
                    arguments[index],
                    ApplicationDeepLinkCodec.ArgumentName,
                    StringComparison.Ordinal))
            {

                forwarded.Add(arguments[index]);

                continue;

            }

            if (index + 1 < arguments.Length)
            {

                index++;

            }

        }

        return [.. forwarded];

    }

}
