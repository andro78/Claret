using Microsoft.UI.Xaml.Controls;

namespace Claret.Dialogs
{
    /// <summary>Asks for a search term, once — the caller does the actual search.</summary>
    public sealed partial class FindDialog : ContentDialog
    {
        public FindDialog()
        {
            InitializeComponent();
        }

        /// <summary>The typed search term. Meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
        public string Query => QueryBox.Text.Trim();
    }
}
