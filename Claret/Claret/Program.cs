using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Claret
{
    /// <summary>
    /// Stands in for the WinUI template's generated Main (see DISABLE_XAML_GENERATED_MAIN in
    /// Claret.csproj), for one reason: <see cref="VelopackApp"/> has to run before anything else
    /// does. A launch that is really Velopack invoking install/update/uninstall hooks (creating
    /// the desktop shortcut, swapping in a new version, cleaning up an old one) exits right there
    /// — the XAML template's generated Main starts the UI immediately and leaves no room for that.
    /// </summary>
    public static class Program
    {
        [System.STAThread]
        private static void Main(string[] args)
        {
            VelopackApp.Build().Run();

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}
