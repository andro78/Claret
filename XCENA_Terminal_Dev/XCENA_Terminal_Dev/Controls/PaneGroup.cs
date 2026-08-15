using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// Shared state for a tab being dragged between panes. WinUI's TabView reorders within itself
    /// but does not move items between instances — the app has to carry the item across, and this
    /// is where the in-flight tab is parked while it happens.
    /// </summary>
    internal sealed class TabDragContext
    {
        public TabViewItem? Tab { get; set; }

        public PaneGroup? Source { get; set; }

        public void Clear()
        {
            Tab = null;
            Source = null;
        }
    }

    /// <summary>
    /// One region of the split layout. A group owns a real <see cref="TabView"/>, so it can hold
    /// several sessions and only shows the selected one — and because WinUI's TabView supports
    /// dragging tabs between instances, tabs can be moved from one pane to another for free.
    /// </summary>
    internal sealed class PaneGroup : Grid
    {
        private static readonly Windows.UI.Color Edge = Windows.UI.Color.FromArgb(0xFF, 0x4C, 0x50, 0x58);
        private static readonly Windows.UI.Color Fill = Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);

        /// <summary>Payload text; the drag is in-process, so the real handoff is <see cref="_drag"/>.</summary>
        private const string DragPayload = "xcena-terminal-tab";

        private readonly Border _frame;
        private readonly TabDragContext _drag;

        public PaneGroup(TabDragContext drag)
        {
            _drag = drag;

            Tabs = new TabView
            {
                TabWidthMode = TabViewWidthMode.SizeToContent,
                IsAddTabButtonVisible = true,
                CanDragTabs = true,
                CanReorderTabs = true,
                AllowDropTabs = true,
                // Without AllowDrop the XAML drop events never reach the strip, so a tab dragged
                // from another pane is silently rejected.
                AllowDrop = true,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            _frame = new Border
            {
                Child = Tabs,
                Background = new SolidColorBrush(Fill),
                // Always 1px so toggling the active outline cannot reflow (and resize) terminals.
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Edge),
            };

            // The terminals are dark; let the tab strip match instead of the app's light theme.
            RequestedTheme = ElementTheme.Dark;
            Children.Add(_frame);

            Tabs.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
            Tabs.AddTabButtonClick += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
            Tabs.TabCloseRequested += (_, args) =>
            {
                if (args.Tab.Content is TerminalView view)
                {
                    CloseRequested?.Invoke(this, view);
                }
            };
            Tabs.TabItemsChanged += (_, _) => ItemsChanged?.Invoke(this, EventArgs.Empty);
            Tabs.GotFocus += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
            Tabs.PointerPressed += (_, _) => Activated?.Invoke(this, EventArgs.Empty);

            Tabs.TabDragStarting += OnTabDragStarting;
            Tabs.TabStripDragOver += OnTabStripDragOver;
            Tabs.TabStripDrop += OnTabStripDrop;
            Tabs.TabDroppedOutside += (_, args) => TabDragEnded?.Invoke(this, args.Tab);
            Tabs.TabDragCompleted += (_, args) => TabDragEnded?.Invoke(this, args.Tab);
        }

        public TabView Tabs { get; }

        /// <summary>
        /// The "+" button inside the tab strip, so a flyout can be anchored to it. Falls back to the
        /// strip itself if the template has not been applied yet.
        /// </summary>
        public FrameworkElement AddAnchor =>
            FindDescendant<Button>(Tabs, "AddButton") ?? (FrameworkElement)Tabs;

        private static T? FindDescendant<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed && typed.Name == name)
                {
                    return typed;
                }

                if (FindDescendant<T>(child, name) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>The group was clicked or focused and should become the active one.</summary>
        public event EventHandler? Activated;

        public event EventHandler? SelectionChanged;

        public event EventHandler? ItemsChanged;

        /// <summary>The "+" button of this group's strip was pressed.</summary>
        public event EventHandler? AddRequested;

        public event EventHandler<TerminalView>? CloseRequested;

        /// <summary>
        /// A tab drag finished on this pane. The surface decides whether it landed on another pane
        /// and needs moving — WinUI's own cross-TabView drop events do not fire here, so the drop
        /// target is resolved from the cursor position instead.
        /// </summary>
        public event EventHandler<TabViewItem>? TabDragEnded;

        /// <summary>A tab drag began, so the surface can start previewing the drop target.</summary>
        public event EventHandler? TabDragStarted;

        public int Count => Tabs.TabItems.Count;

        public TerminalView? SelectedSession =>
            (Tabs.SelectedItem as TabViewItem)?.Content as TerminalView;

        public IEnumerable<TerminalView> Sessions
        {
            get
            {
                foreach (object item in Tabs.TabItems)
                {
                    if (item is TabViewItem { Content: TerminalView view })
                    {
                        yield return view;
                    }
                }
            }
        }

        public void Add(TabViewItem tab, bool select = true)
        {
            Tabs.TabItems.Add(tab);
            if (select)
            {
                Tabs.SelectedItem = tab;
            }
        }

        /// <summary>Detaches a tab without disposing it, so it can be re-hosted in another group.</summary>
        public TabViewItem? Detach(TerminalView view)
        {
            foreach (object item in Tabs.TabItems)
            {
                if (item is TabViewItem tab && ReferenceEquals(tab.Content, view))
                {
                    Tabs.TabItems.Remove(tab);
                    return tab;
                }
            }

            return null;
        }

        public TabViewItem? FindTab(TerminalView view)
        {
            foreach (object item in Tabs.TabItems)
            {
                if (item is TabViewItem tab && ReferenceEquals(tab.Content, view))
                {
                    return tab;
                }
            }

            return null;
        }

        public void Select(TerminalView view)
        {
            if (FindTab(view) is { } tab)
            {
                Tabs.SelectedItem = tab;
            }
        }

        /// <summary>Moves the selection by <paramref name="delta"/> within this group, wrapping.</summary>
        public void Cycle(int delta)
        {
            int count = Tabs.TabItems.Count;
            if (count < 2)
            {
                return;
            }

            int index = Tabs.SelectedIndex;
            Tabs.SelectedIndex = ((index + delta) % count + count) % count;
        }

        // ---------- moving tabs between panes ----------

        private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
        {
            _drag.Tab = args.Tab;
            _drag.Source = this;

            // A drop target only engages when the package carries something and asks for Move.
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(DragPayload);

            TabDragStarted?.Invoke(this, EventArgs.Empty);
        }

        private void OnTabStripDragOver(object sender, DragEventArgs args)
        {
            if (_drag.Tab is not null && !ReferenceEquals(_drag.Source, this))
            {
                args.AcceptedOperation = DataPackageOperation.Move;
            }
        }

        private void OnTabStripDrop(object sender, DragEventArgs args)
        {
            if (_drag.Tab is not { } tab
                || _drag.Source is not { } source
                || ReferenceEquals(source, this))
            {
                return;
            }

            int index = DropIndex(args);
            source.Tabs.TabItems.Remove(tab);
            Tabs.TabItems.Insert(Math.Clamp(index, 0, Tabs.TabItems.Count), tab);
            Tabs.SelectedItem = tab;

            PaneGroup emptied = source;
            _drag.Clear();

            Activated?.Invoke(this, EventArgs.Empty);
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            emptied.ItemsChanged?.Invoke(emptied, EventArgs.Empty);
        }

        /// <summary>Where in this strip the pointer released, so the tab lands under the cursor.</summary>
        private int DropIndex(DragEventArgs args)
        {
            for (int i = 0; i < Tabs.TabItems.Count; i++)
            {
                if (Tabs.ContainerFromIndex(i) is not TabViewItem item)
                {
                    continue;
                }

                Windows.Foundation.Point position = args.GetPosition(item);
                if (position.X - item.ActualWidth < 0)
                {
                    return i;
                }
            }

            return Tabs.TabItems.Count;
        }

        /// <summary>Keeps the pane's fill in step with the terminal background colour.</summary>
        public void ApplyBackground(Windows.UI.Color color) =>
            _frame.Background = new SolidColorBrush(color);

        public void SetActiveOutline(bool active, bool visible)
        {
            _frame.BorderBrush = visible && active
                ? AppAccent.Brush()
                : new SolidColorBrush(Edge);
        }
    }
}
