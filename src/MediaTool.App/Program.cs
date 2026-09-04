using Velopack;

namespace MediaTool.App;

/// <summary>
/// The entry point, taken over from the one WPF generates.
///
/// The installer needs to run first. When Windows starts this executable during an install,
/// an update or an uninstall, it passes a hook argument that has to be handled and obeyed
/// before any window exists — <see cref="VelopackApp.Run"/> does that work and then exits the
/// process. Letting WPF start first would flash a review window during an uninstall.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
