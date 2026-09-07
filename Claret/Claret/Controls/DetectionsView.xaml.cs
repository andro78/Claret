using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Claret.Controls
{
    /// <summary>One row of the detections panel. A class, because x:Bind wants a bindable type.</summary>
    public sealed class DetectionRow
    {
        public DetectionRow(TerminalView source, string pane, string time, string line)
        {
            Source = source;
            Pane = pane;
            Time = time;
            Line = line;
        }

        /// <summary>The pane that printed it, so clicking the row can go there.</summary>
        public TerminalView Source { get; }

        public string Pane { get; }

        public string Time { get; }

        public string Line { get; }
    }

    /// <summary>
    /// The strip along the bottom of the window: every line that carried watched text, and which
    /// pane printed it.
    ///
    /// Panes are read one at a time and there can be several, so a line worth knowing about arrives
    /// in one you are not looking at and scrolls away unseen. This is where it is kept instead.
    /// </summary>
    public sealed partial class DetectionsView : UserControl
    {
        /// <summary>
        /// How many rows are kept. A rule matching something a board prints every second would grow
        /// without a limit, and the oldest of thousands is not what anyone came to read.
        /// </summary>
        private const int MaxRows = 500;

        private readonly ObservableCollection<DetectionRow> _rows = new();

        public DetectionsView()
        {
            InitializeComponent();
            HitList.ItemsSource = _rows;
            UpdateEmptyState();
        }

        /// <summary>The user clicked a row and wants to see the pane it came from.</summary>
        public event EventHandler<TerminalView>? PaneRequested;

        /// <summary>The user asked to put the panel away.</summary>
        public event EventHandler? CloseRequested;

        public void Add(TerminalView source, string pane, DateTime at, string line)
        {
            // Newest first: a watched line is news, and news that has scrolled off the top of the
            // panel meant to report it is no use to whoever asked.
            _rows.Insert(0, new DetectionRow(source, pane, at.ToString("HH:mm:ss"), line));

            while (_rows.Count > MaxRows)
            {
                _rows.RemoveAt(_rows.Count - 1);
            }

            UpdateEmptyState();
        }

        public void Clear()
        {
            _rows.Clear();
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            HitList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            CountText.Text = empty ? string.Empty : $"{_rows.Count}";
        }

        private void OnHitClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is DetectionRow row)
            {
                PaneRequested?.Invoke(this, row.Source);
            }
        }

        private void OnClearClick(object sender, RoutedEventArgs e) => Clear();

        private void OnCloseClick(object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
