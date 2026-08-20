using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using XCENA_Terminal_Dev.Controls;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Dialogs
{
    /// <summary>
    /// Picks the terminal font and its size. The preview is the point: it renders the sample in the
    /// chosen face and size, including a Hangul line over a digit ruler, so CJK alignment can be
    /// judged before applying rather than discovered in a session.
    /// </summary>
    public sealed partial class FontDialog : ContentDialog
    {
        private string _family;
        private int _size;

        // Starts true: a Slider or NumberBox raises ValueChanged while the dialog is still being
        // parsed, and those events must not reach handlers that touch later controls.
        private bool _loading = true;

        public FontDialog(TerminalAppearance current)
        {
            InitializeComponent();

            _family = current.FontFamily;
            _size = current.SafeFontSize;

            BuildFamilyList();
            SizeBox.Value = _size;
            SizeSlider.Value = _size;
            _loading = false;

            UpdatePreview();

            SecondaryButtonClick += (_, args) =>
            {
                // "Defaults" goes back to automatic at the shipped size, without closing.
                args.Cancel = true;

                _family = TerminalFont.Automatic;
                _size = TerminalAppearance.DefaultFontSize;

                _loading = true;
                FamilyBox.SelectedIndex = 0;
                SizeBox.Value = _size;
                SizeSlider.Value = _size;
                _loading = false;

                UpdatePreview();
            };
        }

        /// <summary>The chosen font. Meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
        public string Family => _family;

        public int Size => _size;

        private void BuildFamilyList()
        {
            string? automatic = FontProbe.ResolveAutomatic(TerminalFont.AutomaticOrder);
            FamilyBox.Items.Add(automatic is null
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

                FamilyBox.Items.Add($"{candidate.Family} — {note}");
            }

            int index = TerminalFont.Candidates
                .ToList()
                .FindIndex(c => string.Equals(c.Family, _family, StringComparison.OrdinalIgnoreCase));

            FamilyBox.SelectedIndex = index < 0 ? 0 : index + 1;
        }

        private void OnFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            int index = FamilyBox.SelectedIndex - 1;
            _family = index >= 0 && index < TerminalFont.Candidates.Count
                ? TerminalFont.Candidates[index].Family
                : TerminalFont.Automatic;

            UpdatePreview();
        }

        private void OnSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading)
            {
                return;
            }

            // A cleared box reads as NaN; keep the last good size rather than collapsing to zero.
            int next = double.IsNaN(args.NewValue) ? _size : (int)Math.Round(args.NewValue);
            SetSize(next, fromBox: true);
        }

        private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            SetSize((int)Math.Round(e.NewValue), fromBox: false);
        }

        private void SetSize(int size, bool fromBox)
        {
            int clamped = Math.Clamp(size, TerminalAppearance.MinFontSize, TerminalAppearance.MaxFontSize);
            if (clamped == _size)
            {
                return;
            }

            _size = clamped;

            _loading = true;
            if (fromBox)
            {
                if (SizeSlider is not null)
                {
                    SizeSlider.Value = _size;
                }
            }
            else if (SizeBox is not null)
            {
                SizeBox.Value = _size;
            }

            _loading = false;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            // Belt and braces: a parse-time event could still arrive before the tree is complete.
            if (PromptLine is null || HangulLine is null || RulerLine is null
                || MixedLine is null || Note is null)
            {
                return;
            }

            string family = _family.Length > 0
                ? _family
                : FontProbe.ResolveAutomatic(TerminalFont.AutomaticOrder) ?? "Cascadia Mono";

            var font = new FontFamily($"{family}, Cascadia Mono, Consolas, monospace");

            foreach (TextBlock line in new[] { PromptLine, HangulLine, RulerLine, MixedLine })
            {
                line.FontFamily = font;
                line.FontSize = _size;
            }

            FontProbe.Metrics metrics = FontProbe.Measure(family);
            Note.Text = metrics.AlignsHangul
                ? $"{family} at {_size}pt — Hangul takes exactly two cells, so CJK columns line up."
                : $"{family} at {_size}pt — Hangul comes from a fallback face and sits narrower than its two cells.";
        }
    }
}
