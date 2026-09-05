using System;
using Microsoft.UI.Xaml.Controls;
using Claret.Services;

namespace Claret.Dialogs
{
    /// <summary>Picks how many lines of scrollback every pane keeps.</summary>
    public sealed partial class ScrollbackDialog : ContentDialog
    {
        public ScrollbackDialog(int current)
        {
            InitializeComponent();

            LinesBox.Value = current;

            SecondaryButtonClick += (_, args) =>
            {
                // "Default" restores the shipped value without closing, so it can be previewed.
                args.Cancel = true;
                LinesBox.Value = WorkspaceLayout.DefaultScrollbackLines;
            };
        }

        /// <summary>The chosen line count. Meaningful once the dialog returned <see cref="ContentDialogResult.Primary"/>.</summary>
        public int Lines => double.IsNaN(LinesBox.Value)
            ? WorkspaceLayout.DefaultScrollbackLines
            : Math.Clamp(
                (int)Math.Round(LinesBox.Value),
                WorkspaceLayout.MinScrollbackLines,
                WorkspaceLayout.MaxScrollbackLines);
    }
}
