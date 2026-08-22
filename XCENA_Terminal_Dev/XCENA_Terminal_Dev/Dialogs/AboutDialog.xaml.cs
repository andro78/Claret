using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using XCENA_Terminal_Dev.Services;

namespace XCENA_Terminal_Dev.Dialogs
{
    /// <summary>
    /// What this build is and where it keeps things. The WebView2 version is here because the
    /// terminal is drawn by it: when rendering misbehaves, that number is the first question.
    /// </summary>
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog()
        {
            InitializeComponent();

            VersionText.Text = Version();
            WebViewText.Text = WebViewVersion();
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

            return informational
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
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
