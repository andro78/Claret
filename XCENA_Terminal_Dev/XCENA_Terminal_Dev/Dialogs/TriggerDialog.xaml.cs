using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Dialogs
{
    /// <summary>Editor for one output trigger.</summary>
    public sealed partial class TriggerDialog : ContentDialog
    {
        /// <param name="existing">The trigger being edited, or null when adding one.</param>
        public TriggerDialog(TriggerRule? existing = null)
        {
            InitializeComponent();

            // Set here rather than in markup: a SelectionChanged or Checked raised while the dialog
            // is still being parsed would reach the handlers before the rest of the tree exists.
            IgnoreCaseCheck.IsChecked = existing?.IgnoreCase ?? true;
            SendReturnCheck.IsChecked = existing?.SendReturn ?? true;
            OnceCheck.IsChecked = existing?.Once ?? false;

            if (existing is not null)
            {
                PatternBox.Text = existing.Pattern;
                ResponseBox.Text = existing.Response;
            }

            Select(existing?.Action ?? TriggerEffect.Notify);
            ApplyAction();

            PrimaryButtonClick += OnPrimaryButtonClick;
        }

        /// <summary>The edited trigger, or null when the user cancelled.</summary>
        public TriggerRule? Rule { get; private set; }

        private TriggerEffect Action =>
            ActionBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out TriggerEffect action)
                ? action
                : TriggerEffect.Notify;

        private void Select(TriggerEffect action)
        {
            ActionBox.SelectedItem = ActionBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == action.ToString())
                ?? ActionBox.Items.OfType<ComboBoxItem>().First();
        }

        private void OnActionChanged(object sender, SelectionChangedEventArgs e) => ApplyAction();

        private void OnFormChanged(object sender, TextChangedEventArgs e) => Describe();

        private void ApplyAction()
        {
            // Guard: SelectionChanged can arrive while the rest of the dialog is still being built.
            if (SendPanel is null || Explanation is null || Problem is null)
            {
                return;
            }

            SendPanel.Visibility = Action == TriggerEffect.Send ? Visibility.Visible : Visibility.Collapsed;
            Describe();
        }

        /// <summary>Says in a sentence what the trigger will do, so the form reads back as intent.</summary>
        private void Describe()
        {
            if (Explanation is null || Problem is null)
            {
                return;
            }

            string text = PatternBox.Text.Length == 0 ? "that text" : $"“{PatternBox.Text}”";

            Explanation.Text = Action switch
            {
                TriggerEffect.StartLog =>
                    $"When {text} appears, this session starts being recorded to a file, if it is not already.",
                TriggerEffect.StopLog =>
                    $"When {text} appears, recording of this session stops.",
                TriggerEffect.Send =>
                    $"When {text} appears, the text above is typed back at the far end.",
                _ =>
                    $"When {text} appears, the app beeps and prints a line saying so.",
            };

            Problem.IsOpen = false;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string pattern = PatternBox.Text;
            string response = ResponseBox.Text;
            TriggerEffect action = Action;

            if (TriggerRule.Validate(pattern, action, response) is { } problem)
            {
                Problem.Message = problem;
                Problem.IsOpen = true;
                args.Cancel = true;
                return;
            }

            Rule = new TriggerRule
            {
                Pattern = pattern,
                IgnoreCase = IgnoreCaseCheck.IsChecked == true,
                Action = action,
                Response = response,
                SendReturn = SendReturnCheck.IsChecked == true,
                Once = OnceCheck.IsChecked == true,
                Enabled = true,
            };
        }
    }
}
