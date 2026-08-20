using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Dialogs
{
    /// <summary>Editor for one terminal highlight rule.</summary>
    public sealed partial class HighlightRuleDialog : ContentDialog
    {
        /// <summary>What "Auto" would hand this rule, so the preview can show the real colour.</summary>
        private readonly string _autoColor;

        /// <param name="existing">The rule being edited, or null when adding one.</param>
        /// <param name="autoColor">
        /// The colour "Auto" would assign, worked out from the rest of the list by the caller.
        /// </param>
        public HighlightRuleDialog(HighlightRule? existing = null, string? autoColor = null)
        {
            InitializeComponent();

            _autoColor = autoColor ?? HighlightRule.FirstAutoColor;

            // A new rule starts on Auto; an existing one keeps whatever it was saved with.
            bool auto = existing is null || existing.AutoColor;
            bool textOnly = existing?.TextOnly == true;

            if (existing is not null)
            {
                PatternBox.Text = existing.Pattern;
                IgnoreCaseCheck.IsChecked = existing.IgnoreCase;
            }

            // Set here rather than in markup: a Checked event raised while the dialog is still
            // being parsed would reach the handlers before the rest of the tree exists.
            BlockStyleRadio.IsChecked = !textOnly;
            TextStyleRadio.IsChecked = textOnly;
            AutoColorRadio.IsChecked = auto;
            CustomColorRadio.IsChecked = !auto;

            // Custom starts from the colour the rule already had, or from the auto pick, so
            // switching to Custom is a nudge away from something sensible rather than a blank slate.
            Picker.Color = ParseColor(existing is { AutoColor: false } ? existing.Color : _autoColor);

            ApplyColorMode();

            PrimaryButtonClick += OnPrimaryButtonClick;
        }

        /// <summary>The edited rule, or null when the user cancelled.</summary>
        public HighlightRule? Rule { get; private set; }

        private bool IsAuto => AutoColorRadio.IsChecked == true;

        private bool IsTextOnly => TextStyleRadio.IsChecked == true;

        private void OnPatternChanged(object sender, object e) => UpdatePreview();

        private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args) => UpdatePreview();

        private void OnColorModeChanged(object sender, RoutedEventArgs e) => ApplyColorMode();

        private void OnStyleChanged(object sender, RoutedEventArgs e) => UpdatePreview();

        /// <summary>Auto owns the colour, so the picker is out of the way while it is selected.</summary>
        private void ApplyColorMode()
        {
            // Guard: a Checked event can arrive while the rest of the dialog is still being built.
            if (Picker is null || AutoColorNote is null || PreviewChip is null || Problem is null)
            {
                return;
            }

            bool auto = IsAuto;

            Picker.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
            AutoColorNote.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (PreviewChip is null || Problem is null)
            {
                return;
            }

            PreviewText.Text = PatternBox.Text.Length == 0 ? "error" : PatternBox.Text;

            Windows.UI.Color color = IsAuto ? ParseColor(_autoColor) : Picker.Color;

            if (IsTextOnly)
            {
                // Nothing behind the characters, so the colour has to carry them on its own.
                PreviewChip.Background = null;
                PreviewText.Foreground = new SolidColorBrush(color);
            }
            else
            {
                PreviewChip.Background = new SolidColorBrush(color);
                PreviewText.Foreground = new SolidColorBrush(Readable(color));
            }

            Problem.IsOpen = false;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string pattern = PatternBox.Text;

            if (HighlightRule.Validate(pattern) is { } problem)
            {
                Problem.Message = problem;
                Problem.IsOpen = true;
                args.Cancel = true;
                return;
            }

            Rule = new HighlightRule
            {
                Pattern = pattern,
                IgnoreCase = IgnoreCaseCheck.IsChecked == true,
                // Keep the picked colour even on Auto: switching back to Custom later restores it.
                Color = ToHex(Picker.Color),
                AutoColor = IsAuto,
                TextOnly = IsTextOnly,
                Enabled = true,
            };
        }

        /// <summary>Black or white, whichever stays legible on a block of the chosen colour.</summary>
        private static Windows.UI.Color Readable(Windows.UI.Color background)
        {
            int brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
            return brightness >= 140 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        }

        private static string ToHex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Windows.UI.Color ParseColor(string hex)
        {
            string value = hex.TrimStart('#');
            if (value.Length == 6
                && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint parsed))
            {
                return Windows.UI.Color.FromArgb(
                    0xFF,
                    (byte)((parsed >> 16) & 0xFF),
                    (byte)((parsed >> 8) & 0xFF),
                    (byte)(parsed & 0xFF));
            }

            return Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xC5, 0x42);
        }
    }
}
