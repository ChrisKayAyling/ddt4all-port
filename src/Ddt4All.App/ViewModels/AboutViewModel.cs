using System.Reflection;
using System.Runtime.InteropServices;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

public sealed record Contributor(string Name, string Role, string Url);

public sealed class AboutViewModel : PageViewModel
{
    public override PageId Id => PageId.About;
    public override string Title => Loc.T("About");

    public string AppName => "DDT4All";
    public string Version => typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    public string Tagline => Loc.T("Cross-platform diagnostic application for CAN bus vehicle networks.");
    public string PortNote => "Native .NET port of DDT4ALL 3.1.3 \"Celestial Nemean\" by Cedric Paille.";
    public string Runtime => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.RuntimeIdentifier}";
    public string Os => RuntimeInformation.OSDescription;
    public string ConfigDir => AppPaths.ConfigDir;
    public string LogDir => AppPaths.LogDir;
    public string License => "GPL-3.0-or-later";
    public string Disclaimer => Loc.T("Maintenance, testing and research only. The author declines responsibility for any use. You are responsible.");

    public IReadOnlyList<Contributor> Contributors { get; } =
        new[]
        {
            ("Furtif", "Collaborator"), ("jyte", "Contributor"), ("bovirus", "Contributor"), ("not-jan", "Contributor"),
            ("joeyave", "Contributor"), ("josefe17", "Contributor"), ("shrlnm", "Contributor"), ("jneuhauser", "Contributor"),
            ("gobo-ws", "Contributor"), ("Dante383", "Contributor"), ("jan-stanek", "Contributor"), ("mrechte", "Contributor"),
            ("frederic34", "Contributor"), ("memleketim05", "Contributor"), ("landswellsong", "Contributor"), ("krzeminskim", "Contributor"),
            ("Jodaille", "Contributor"), ("TA1GI", "Translator"), ("walkman2021", "Translator"),
        }.Select(c => new Contributor(c.Item1, c.Item2, "https://github.com/" + c.Item1)).ToArray();
}
