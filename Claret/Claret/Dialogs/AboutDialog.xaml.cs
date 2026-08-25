using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Claret.Services;

namespace Claret.Dialogs
{
    /// <summary>
    /// What this build is, what it is standing on, and where it keeps things. Every version is read
    /// from what is actually loaded rather than written down, because a list that can go stale is
    /// worse than no list when someone is trying to explain a rendering fault.
    /// </summary>
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog()
        {
            InitializeComponent();

            VersionText.Text = $"Version {Version()}";
            BuiltText.Text = $"Built {BuildTime()}";
            RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";
            WinUiText.Text = AssemblyVersion(typeof(Application).Assembly);
            WebViewText.Text = WebViewVersion();
            SshText.Text = AssemblyVersion(typeof(Renci.SshNet.SshClient).Assembly);
            WindowsText.Text = RuntimeInformation.OSDescription;
            BaseText.Text = AppContext.BaseDirectory;
            DataText.Text = AppPaths.DataDirectory;
        }

        /// <summary>Raised when the user asks for the settings folder in Explorer.</summary>
        public event EventHandler? OpenDataFolderRequested;

        private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) =>
            OpenDataFolderRequested?.Invoke(this, EventArgs.Empty);

        private static string Version()
        {
            Assembly assembly = typeof(AboutDialog).Assembly;

            // The informational version carries whatever the build stamped; the file version is the
            // fallback, and both can be absent in an odd build rather than being worth failing over.
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // A source-stamped version carries a "+commit" suffix that means nothing to a reader.
            if (informational is { Length: > 0 })
            {
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>
        /// When this binary was compiled, in the reader's own time zone. The build writes the
        /// attribute as round-trip UTC (see the AssemblyMetadata item in Claret.csproj), so a build
        /// handed to someone in another zone still names the right moment.
        ///
        /// Two people on the same version number can be running different binaries, which is the
        /// whole reason this line exists — so an unstamped build says so rather than showing
        /// nothing and letting the gap read as "same build".
        /// </summary>
        private static string BuildTime()
        {
            string? stamp = typeof(AboutDialog).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "BuildTime")?.Value;

            if (stamp is { Length: > 0 }
                && DateTimeOffset.TryParse(
                    stamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset built))
            {
                return built.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return "unknown";
        }

        private static string AssemblyVersion(Assembly assembly)
        {
            string? file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return file ?? assembly.GetName().Version?.ToString() ?? "unknown";
        }

        private static string WebViewVersion()
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not found";
            }
            catch (Exception)
            {
                // No runtime installed, or it cannot be queried: the terminal would have said so
                // long before anyone opened this dialog.
                return "not found";
            }
        }
    }
}
