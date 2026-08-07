using SentientDesk.Services;
using Velopack;

namespace SentientDesk;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                DesktopIconHider.ForceShowDesktopIcons();
                StartupService.SetEnabled(false);
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
