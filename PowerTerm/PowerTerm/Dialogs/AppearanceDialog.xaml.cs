using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using PowerTerm.Controls;
using PowerTerm.Models;

namespace PowerTerm.Dialogs
{
    /// <summary>
    /// Picks the terminal colours. A scheme sets the background, the text colour and all sixteen
    /// ANSI colours in one go; the picker below it still edits the background or the text by hand,
    /// which drops the scheme to "Custom". The preview shows the pair together and the ANSI colours
    /// where a shell would actually use them — whether they stay readable is the whole question.
    /// </summary>
    public sealed partial class AppearanceDialog : ContentDialog
    {
        private const string CustomLabel = "Custom";

        private readonly List<Border> _swatches = new();

        private Color _background;
        private Color _foreground;
        private List<string> _ansi;
        private string _fontFamily;
        private bool _loading;

        public AppearanceDialog(TerminalAppearance current)
        {
            InitializeComponent();

            _background = current.BackgroundColor;
            _foreground = current.ForegroundColor;
            _ansi = current.AnsiColors.ToList();
            _fontFamily = current.FontFamily;

            _loading = true;

            foreach (TerminalScheme scheme in TerminalScheme.All)
            {
                SchemeBox.Items.Add(scheme.Name);
            }

            SchemeBox.Items.Add(CustomLabel);
            BuildFontList();
            TargetButtons.SelectedIndex = 0;

            BuildSwatches();
            _loading = false;

            SyncSchemeSelection();
            LoadPicker();
            UpdatePreviewFont();
            UpdatePreview();

            // Colours that match no preset were tuned by hand, so show where they came from.
            CustomExpander.IsExpanded = IsCustomSelected;

            SecondaryButtonClick += (_, args) =>
            {
                // "Defaults" restores the shipped scheme without closing, so it can be previewed.
                args.Cancel = true;
                Apply(TerminalScheme.Default);
            };
        }

        /// <summary>The chosen colours. Meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
        public TerminalAppearance Result => new()
        {
            Background = TerminalAppearance.ToHex(_background),
            Foreground = TerminalAppearance.ToHex(_foreground),
            SchemeName = SchemeBox.SelectedItem as string ?? CustomLabel,
            FontFamily = _fontFamily,
            Ansi = _ansi.ToArray(),
        };

        private bool EditingBackground => TargetButtons.SelectedIndex != 1;

        /// <summary>True while the scheme box points at its last entry, "Custom".</summary>
        private bool IsCustomSelected => SchemeBox.SelectedIndex == SchemeBox.Items.Count - 1;

        private void OnTargetChanged(object sender, SelectionChangedEventArgs e) => LoadPicker();

        private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            // The last entry is "Custom", which is a statement about the colours rather than a
            // palette to load; picking it leaves them alone.
            if (SchemeBox.SelectedIndex >= 0 && SchemeBox.SelectedIndex < TerminalScheme.All.Count)
            {
                Apply(TerminalScheme.All[SchemeBox.SelectedIndex]);
            }
        }

        /// <summary>
        /// Automatic first, then the candidates, each labelled with what it is and whether it is
        /// actually installed — a missing family would otherwise just silently do nothing.
        /// </summary>
        private void BuildFontList()
        {
            string? automatic = FontProbe.ResolveAutomatic(TerminalFont.AutomaticOrder);
            FontBox.Items.Add(automatic is null
                ? "Automatic (no Hangul-aligned font found)"
                : $"Automatic — {automatic}");

            foreach (TerminalFont.Candidate candidate in TerminalFont.Candidates)
            {
                FontProbe.Metrics metrics = FontProbe.Measure(candidate.Family);

                string note = !metrics.Installed
                    ? "not installed"
                    : metrics.AlignsHangul
                        ? $"{candidate.Note} · Hangul aligned"
                        : $"{candidate.Note} · Hangul narrower than its cells";

                FontBox.Items.Add($"{candidate.Family} — {note}");
            }

            int index = TerminalFont.Candidates
                .ToList()
                .FindIndex(c => string.Equals(c.Family, _fontFamily, StringComparison.OrdinalIgnoreCase));

            FontBox.SelectedIndex = index < 0 ? 0 : index + 1;
        }

        private void OnFontChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            int index = FontBox.SelectedIndex - 1;
            _fontFamily = index >= 0 && index < TerminalFont.Candidates.Count
                ? TerminalFont.Candidates[index].Family
                : TerminalFont.Automatic;

            UpdatePreviewFont();
        }

        /// <summary>
        /// Renders the sample in the font the terminal will use, so the Hangul-over-digits pair
        /// shows the real alignment rather than a promise about it.
        /// </summary>
        private void UpdatePreviewFont()
        {
            string family = _fontFamily.Length > 0
                ? _fontFamily
                : FontProbe.ResolveAutomatic(TerminalFont.AutomaticOrder) ?? "Cascadia Mono";

            var font = new FontFamily($"{family}, Cascadia Mono, Consolas, monospace");

            PromptLine.FontFamily = font;
            LogLine.FontFamily = font;
            HangulLine.FontFamily = font;
            RulerLine.FontFamily = font;
        }

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

            SyncSchemeSelection();
            UpdatePreview();
        }

        /// <summary>Loads a preset into the working colours and reflects it everywhere.</summary>
        private void Apply(TerminalScheme scheme)
        {
            TerminalAppearance appearance = TerminalAppearance.FromScheme(scheme);

            _background = appearance.BackgroundColor;
            _foreground = appearance.ForegroundColor;
            _ansi = appearance.AnsiColors.ToList();

            SyncSchemeSelection();
            LoadPicker();
            UpdatePreview();
        }

        /// <summary>
        /// Points the scheme box at whichever preset the current colours match, or at "Custom" once
        /// they match none — so hand-editing a colour never leaves a preset name claiming it.
        /// </summary>
        private void SyncSchemeSelection()
        {
            TerminalScheme? match = TerminalScheme.Match(
                TerminalAppearance.ToHex(_background),
                TerminalAppearance.ToHex(_foreground),
                _ansi);

            int index = match is null
                ? SchemeBox.Items.Count - 1
                : TerminalScheme.All.ToList().IndexOf(match);

            if (SchemeBox.SelectedIndex == index)
            {
                return;
            }

            bool wasLoading = _loading;
            _loading = true;
            SchemeBox.SelectedIndex = index;
            _loading = wasLoading;
        }

        private void LoadPicker()
        {
            bool wasLoading = _loading;
            _loading = true;
            Picker.Color = EditingBackground ? _background : _foreground;
            _loading = wasLoading;
        }

        private void BuildSwatches()
        {
            for (int i = 0; i < _ansi.Count; i++)
            {
                var swatch = new Border
                {
                    Width = 14,
                    Height = 10,
                    CornerRadius = new CornerRadius(2),
                    // A hairline keeps the near-background slots (ANSI black, say) visible.
                    BorderThickness = new Thickness(1),
                };

                _swatches.Add(swatch);
                SwatchRow.Children.Add(swatch);
            }
        }

        private void UpdatePreview()
        {
            PreviewBorder.Background = new SolidColorBrush(_background);

            var text = new SolidColorBrush(_foreground);
            HangulLine.Foreground = text;
            RulerLine.Foreground = text;
            PromptPunct1.Foreground = text;
            PromptPunct2.Foreground = text;
            LogGap1.Foreground = text;
            LogGap2.Foreground = text;
            LogGap3.Foreground = text;

            // The slots a prompt and a log line usually land on: bright green, blue, yellow, red.
            PromptUser.Foreground = Ansi(10);
            PromptPath.Foreground = Ansi(12);
            LogOk.Foreground = Ansi(10);
            LogWarn.Foreground = Ansi(11);
            LogError.Foreground = Ansi(9);
            LogInfo.Foreground = Ansi(14);

            var hairline = new SolidColorBrush(
                Color.FromArgb(0x40, _foreground.R, _foreground.G, _foreground.B));

            for (int i = 0; i < _swatches.Count; i++)
            {
                _swatches[i].Background = Ansi(i);
                _swatches[i].BorderBrush = hairline;
            }
        }

        private SolidColorBrush Ansi(int index)
        {
            string hex = index >= 0 && index < _ansi.Count
                ? _ansi[index]
                : TerminalScheme.Default.Ansi[index];

            var probe = new TerminalAppearance { Foreground = hex };
            return new SolidColorBrush(probe.ForegroundColor);
        }
    }
}
