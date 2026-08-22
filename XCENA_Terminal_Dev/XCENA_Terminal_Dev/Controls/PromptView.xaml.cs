using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>What the prompt panel is asking the shell to do with the text.</summary>
    public enum PromptDelivery
    {
        /// <summary>Paste it and submit — the usual case.</summary>
        Send,

        /// <summary>Paste it and stop there, so it can be edited at the prompt first.</summary>
        Insert,
    }

    /// <summary>
    /// Sidebar "Prompt" tab: an editor for what you want to ask the AI CLI that is running in the
    /// active session. It talks to the terminal, not to any service — the agent is the process on
    /// the other end, so nothing here calls out to a model or holds a key.
    /// </summary>
    public sealed partial class PromptView : UserControl
    {
        public PromptView()
        {
            InitializeComponent();
        }

        /// <summary>Raised with the finished text and what to do with it.</summary>
        public event EventHandler<(string Text, PromptDelivery Delivery)>? PromptSubmitted;

        /// <summary>Binds the history list. Called once, before the first layout.</summary>
        public void BindHistory(ObservableCollection<string> history)
        {
            HistoryList.ItemsSource = history;
            history.CollectionChanged += (_, _) => UpdateHistoryVisibility(history);
            UpdateHistoryVisibility(history);
        }

        /// <summary>
        /// Names where the text would land, or says there is nowhere yet. A prompt panel that does
        /// not say which session it types into is a way to paste into the wrong console.
        /// </summary>
        public void ShowTarget(string? label)
        {
            bool ready = !string.IsNullOrEmpty(label);

            Target.Text = ready ? $"Sends to {label}" : "No session — open one to send a prompt";
            UpdateButtons();
        }

        private void UpdateHistoryVisibility(ObservableCollection<string> history)
        {
            Visibility show = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryHeader.Visibility = show;
            HistoryList.Visibility = show;
        }

        private bool HasTarget => Target.Text.StartsWith("Sends to", StringComparison.Ordinal);

        private void UpdateButtons()
        {
            bool ready = HasTarget && Editor.Text.Trim().Length > 0;
            SendButton.IsEnabled = ready;
            InsertButton.IsEnabled = ready;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdateButtons();

        /// <summary>
        /// Ctrl+Enter sends. Plain Enter has to stay a newline: this is an editor for text that is
        /// often several lines, and losing a draft to a stray Return would be the whole point gone.
        /// </summary>
        private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            if (!control.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                return;
            }

            e.Handled = true;
            Submit(PromptDelivery.Send);
        }

        private void OnSendClick(object sender, RoutedEventArgs e) => Submit(PromptDelivery.Send);

        private void OnInsertClick(object sender, RoutedEventArgs e) => Submit(PromptDelivery.Insert);

        private void Submit(PromptDelivery delivery)
        {
            string text = Editor.Text.Trim();
            if (text.Length == 0 || !HasTarget)
            {
                return;
            }

            PromptSubmitted?.Invoke(this, (text, delivery));

            // Clear only on send: an insert is followed by editing at the prompt, and the draft is
            // the thing being edited.
            if (delivery == PromptDelivery.Send)
            {
                Editor.Text = string.Empty;
            }
        }

        private void OnHistoryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => UseSelected();

        private void OnUseHistoryClick(object sender, RoutedEventArgs e) => UseSelected(Entry(sender));

        private void OnSendHistoryClick(object sender, RoutedEventArgs e)
        {
            if (Entry(sender) is { } text)
            {
                PromptSubmitted?.Invoke(this, (text, PromptDelivery.Send));
            }
        }

        private void OnRemoveHistoryClick(object sender, RoutedEventArgs e)
        {
            if (Entry(sender) is { } text
                && HistoryList.ItemsSource is ObservableCollection<string> history)
            {
                history.Remove(text);
            }
        }

        /// <summary>Puts an old prompt back in the editor to be tweaked rather than resent as is.</summary>
        private void UseSelected(string? text = null)
        {
            text ??= HistoryList.SelectedItem as string;
            if (text is null)
            {
                return;
            }

            Editor.Text = text;
            Editor.SelectionStart = text.Length;
            Editor.Focus(FocusState.Programmatic);
        }

        /// <summary>Menu items carry the right-clicked row; fall back to the selection.</summary>
        private string? Entry(object sender) =>
            sender is FrameworkElement { DataContext: string fromContext }
                ? fromContext
                : HistoryList.SelectedItem as string;
    }
}
