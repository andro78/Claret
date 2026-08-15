using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Dialogs
{
    /// <summary>
    /// Picks the terminal background and text colour. One picker edits whichever target is
    /// selected, which keeps the dialog small, and the preview shows the pair together — the only
    /// thing that really matters is whether they are readable against each other.
    /// </summary>
    public sealed partial class AppearanceDialog : ContentDialog
    {
        private Color _background;
        private Color _foreground;
        private bool _loading;

        public AppearanceDialog(TerminalAppearance current)
        {
            InitializeComponent();

            _background = current.BackgroundColor;
            _foreground = current.ForegroundColor;

            TargetButtons.SelectedIndex = 0;
            LoadPicker();
            UpdatePreview();

            SecondaryButtonClick += (_, args) =>
            {
                // "Defaults" restores the defaults without closing, so the result can be previewed.
                args.Cancel = true;
                _background = new TerminalAppearance().BackgroundColor;
                _foreground = new TerminalAppearance().ForegroundColor;
                LoadPicker();
                UpdatePreview();
            };
        }

        /// <summary>The chosen colours. Meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
        public TerminalAppearance Result => new()
        {
            Background = TerminalAppearance.ToHex(_background),
            Foreground = TerminalAppearance.ToHex(_foreground),
        };

        private bool EditingBackground => TargetButtons.SelectedIndex != 1;

        private void OnTargetChanged(object sender, SelectionChangedEventArgs e) => LoadPicker();

        private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (_loading)
            {
                return;
            }

            if (EditingBackground)
            {
                _background = args.NewColor;
            }
            else
            {
                _foreground = args.NewColor;
            }

            UpdatePreview();
        }

        private void LoadPicker()
        {
            _loading = true;
            Picker.Color = EditingBackground ? _background : _foreground;
            _loading = false;
        }

        private void UpdatePreview()
        {
            PreviewBorder.Background = new SolidColorBrush(_background);
            var text = new SolidColorBrush(_foreground);
            PreviewLine1.Foreground = text;
            PreviewLine2.Foreground = text;
        }
    }
}
