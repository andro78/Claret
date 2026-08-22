using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using XCENA_Terminal_Dev.Dialogs;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// Sidebar "Tools" tab, lower half: the list of output triggers. Editing only changes the
    /// collection; saving and arming the sessions is the shell's job, driven by <see cref="TriggersChanged"/>.
    /// </summary>
    public sealed partial class TriggersView : UserControl
    {
        public TriggersView()
        {
            InitializeComponent();
        }

        /// <summary>Raised after any edit, so the shell can save and re-arm.</summary>
        public event EventHandler? TriggersChanged;

        /// <summary>Binds the list to the loaded triggers. Called once, before the first layout.</summary>
        public void Initialize(ObservableCollection<TriggerRule> triggers)
        {
            TriggerList.ItemsSource = triggers;
            triggers.CollectionChanged += (_, _) => UpdateEmptyState();
            UpdateEmptyState();
        }

        private ObservableCollection<TriggerRule>? Source =>
            TriggerList.ItemsSource as ObservableCollection<TriggerRule>;

        private TriggerRule? Selected => TriggerList.SelectedItem as TriggerRule;

        private void UpdateEmptyState()
        {
            bool empty = Source is null || Source.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            TriggerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool has = Selected is not null;
            EditButton.IsEnabled = has;
            DeleteButton.IsEnabled = has;
        }

        private void OnEnabledClick(object sender, RoutedEventArgs e)
        {
            // The binding already wrote the new value into the trigger; this only has to publish it.
            TriggersChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (Source is not { } triggers)
            {
                return;
            }

            var dialog = new TriggerDialog { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Rule is not { } rule)
            {
                return;
            }

            triggers.Add(rule);
            TriggerList.SelectedItem = rule;
            TriggersChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnEditClick(object sender, RoutedEventArgs e) => await EditSelectedAsync();

        private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => _ = EditSelectedAsync();

        private async Task EditSelectedAsync()
        {
            if (Source is not { } triggers || Selected is not { } existing)
            {
                return;
            }

            var dialog = new TriggerDialog(existing) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Rule is not { } edited)
            {
                return;
            }

            // Replace rather than mutate: the row binds one-way, so a swap is what redraws it.
            int index = triggers.IndexOf(existing);
            if (index < 0)
            {
                return;
            }

            triggers[index] = edited;
            TriggerList.SelectedItem = edited;
            TriggersChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (Source is not { } triggers || Selected is not { } rule)
            {
                return;
            }

            triggers.Remove(rule);
            TriggersChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
