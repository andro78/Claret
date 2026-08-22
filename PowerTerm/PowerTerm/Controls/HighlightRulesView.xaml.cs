using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PowerTerm.Dialogs;
using PowerTerm.Models;

namespace PowerTerm.Controls
{
    /// <summary>
    /// Sidebar "Tools" tab: the list of text-highlight rules. Editing only changes the collection;
    /// persisting and pushing to the terminals is the shell's job, driven by <see cref="RulesChanged"/>.
    /// </summary>
    public sealed partial class HighlightRulesView : UserControl
    {
        public HighlightRulesView()
        {
            InitializeComponent();
        }

        /// <summary>Raised after any edit, so the shell can save and re-apply.</summary>
        public event EventHandler? RulesChanged;

        /// <summary>Binds the list to the loaded rules. Called once, before the first layout.</summary>
        public void Initialize(ObservableCollection<HighlightRule> rules)
        {
            RuleList.ItemsSource = rules;
            rules.CollectionChanged += (_, _) => UpdateEmptyState();
            UpdateEmptyState();
        }

        private ObservableCollection<HighlightRule>? Source =>
            RuleList.ItemsSource as ObservableCollection<HighlightRule>;

        private HighlightRule? Selected => RuleList.SelectedItem as HighlightRule;

        private void UpdateEmptyState()
        {
            bool empty = Source is null || Source.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            RuleList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool has = Selected is not null;
            EditButton.IsEnabled = has;
            DeleteButton.IsEnabled = has;
        }

        private void OnEnabledClick(object sender, RoutedEventArgs e)
        {
            // The binding already wrote the new value into the rule; this only has to publish it.
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (Source is not { } rules)
            {
                return;
            }

            var dialog = new HighlightRuleDialog(autoColor: HighlightRule.PreviewAutoColor(rules, null))
            {
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Rule is not { } rule)
            {
                return;
            }

            rules.Add(rule);
            HighlightRule.ResolveColors(rules);
            RuleList.SelectedItem = rule;
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnEditClick(object sender, RoutedEventArgs e) => await EditSelectedAsync();

        private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => _ = EditSelectedAsync();

        private async Task EditSelectedAsync()
        {
            if (Source is not { } rules || Selected is not { } existing)
            {
                return;
            }

            var dialog = new HighlightRuleDialog(existing, HighlightRule.PreviewAutoColor(rules, existing))
            {
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Rule is not { } edited)
            {
                return;
            }

            // Replace rather than mutate: the list item template binds one-way, so a swap is what
            // makes the row redraw with the new pattern and colour.
            int index = rules.IndexOf(existing);
            if (index < 0)
            {
                return;
            }

            rules[index] = edited;
            HighlightRule.ResolveColors(rules);
            RuleList.SelectedItem = edited;
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (Source is not { } rules || Selected is not { } rule)
            {
                return;
            }

            rules.Remove(rule);
            HighlightRule.ResolveColors(rules);
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
