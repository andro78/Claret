using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XCENA_Terminal_Dev.Models;
using XCENA_Terminal_Dev.Services;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// The whole terminal area: a nested tree of panes. Each leaf is a <see cref="PaneGroup"/> with
    /// its own tab strip, so a pane holds several sessions and tabs can be dragged between panes.
    /// Splitting a pane rewrites only that pane's subtree — the rest of the layout is untouched.
    /// </summary>
    internal sealed class SessionSurface : Grid
    {
        private static readonly Windows.UI.Color TerminalBackground = Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);

        /// <summary>Rough height of a pane's tab strip; drops there mean "join this pane".</summary>
        private const double TabStripHeight = 44;

        private readonly TabDragContext _drag = new();

        private Border? _dropHint;
        private DispatcherQueueTimer? _dropHintTimer;

        private PaneNode? _root;
        private PaneLeafNode? _active;
        private TerminalAppearance? _appearance;
        private bool _pruneQueued;

        public SessionSurface()
        {
            // Transparent, so the window background shows through the gaps between panes. That gap
            // is what separates the cards — no drawn borders needed. The padding keeps the outer
            // cards from butting against the window edges.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Padding = new Thickness(4, 0, 8, 8);
        }

        /// <summary>Window-level chords (sidebar) that bubble out of a pane.</summary>
        public event EventHandler<TerminalCommand>? WindowCommandRequested;

        /// <summary>The user asked for a new session — via "+" or Ctrl+Shift+T.</summary>
        public event EventHandler? NewSessionRequested;

        /// <summary>A tab's "Duplicate" menu item was used; open another shell on the same host.</summary>
        public event EventHandler<TerminalView>? DuplicateRequested;

        /// <summary>Active session, its state, or its title changed.</summary>
        public event EventHandler? ActiveSessionChanged;

        /// <summary>Fired after the last session closes.</summary>
        public event EventHandler? Emptied;

        /// <summary>
        /// A session is about to be torn down. Anything holding a side channel for it — the SFTP
        /// file tree, for one — should let go now, while the connection details are still valid.
        /// </summary>
        public event EventHandler<TerminalView>? SessionClosing;

        /// <summary>Owning window handle, needed to map the cursor into client coordinates.</summary>
        public IntPtr WindowHandle { get; set; }

        public TerminalView? ActiveView => _active?.Group.SelectedSession;

        /// <summary>Element to anchor the "new session" flyout to — the active pane's "+" button.</summary>
        public FrameworkElement? ActiveAddAnchor => _active?.Group.AddAnchor;

        public int PaneCount => Leaves().Count();

        public int SessionCount => Leaves().Sum(l => l.Group.Count);

        // ---------- sessions ----------

        /// <summary>
        /// Creates a tab in the active pane without connecting, so it is on screen before the SSH
        /// handshake begins.
        /// </summary>
        public TerminalView AddSession(ConnectionProfile profile)
        {
            PaneLeafNode leaf = _active ?? EnsureRootLeaf();

            var view = new TerminalView();
            view.CommandRequested += OnPaneCommand;
            view.StateChanged += OnSessionStateChanged;
            view.TitleChanged += OnSessionTitleChanged;

            var tab = new TabViewItem
            {
                Header = profile.DisplayName,
                Content = view,
                IconSource = new SymbolIconSource { Symbol = Symbol.Sync },
            };
            ToolTipService.SetToolTip(tab, profile.Endpoint);
            tab.ContextFlyout = BuildTabMenu(view);

            leaf.Group.Add(tab);

            if (_appearance is not null)
            {
                view.ApplyAppearance(_appearance);
            }

            SetActive(leaf, notify: false);
            RefreshChrome();
            return view;
        }

        public async Task StartAsync(TerminalView view, ConnectionProfile profile, string? secret)
        {
            await view.ConnectAsync(profile, secret);
            RefreshChrome();
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void CloseSession(TerminalView view)
        {
            PaneLeafNode? leaf = LeafOf(view);
            if (leaf is null)
            {
                return;
            }

            SessionClosing?.Invoke(this, view);

            view.CommandRequested -= OnPaneCommand;
            view.StateChanged -= OnSessionStateChanged;
            view.TitleChanged -= OnSessionTitleChanged;

            TabViewItem? tab = leaf.Group.Detach(view);
            if (tab is not null)
            {
                tab.Content = null;
                tab.ContextFlyout = null;
            }

            view.Shutdown();
            Prune();
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);

            if (SessionCount == 0)
            {
                Emptied?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ShutdownAll()
        {
            foreach (PaneLeafNode leaf in Leaves().ToList())
            {
                foreach (TerminalView view in leaf.Group.Sessions.ToList())
                {
                    view.CommandRequested -= OnPaneCommand;
                    view.StateChanged -= OnSessionStateChanged;
                    view.TitleChanged -= OnSessionTitleChanged;
                    view.Shutdown();
                }

                leaf.Group.Tabs.TabItems.Clear();
            }

            Children.Clear();
            _root = null;
            _active = null;

            // Let healthy links finish disconnecting, but never hold the window open for a dead one.
            SshTeardown.WaitBriefly(TimeSpan.FromMilliseconds(250));
        }

        public void FocusActive() => _active?.Group.SelectedSession?.FocusTerminal();

        /// <summary>Pushes the user's colours to every open terminal and to the pane chrome.</summary>
        public void ApplyAppearance(TerminalAppearance appearance)
        {
            _appearance = appearance.Clone();

            foreach (PaneLeafNode leaf in Leaves())
            {
                leaf.Group.ApplyBackground(_appearance.BackgroundColor);

                foreach (TerminalView view in leaf.Group.Sessions)
                {
                    view.ApplyAppearance(_appearance);
                }
            }
        }

        /// <summary>Ctrl+Tab — moves through the tabs of the active pane.</summary>
        public void CycleActive(int delta)
        {
            _active?.Group.Cycle(delta);
            FocusActive();
        }

        // ---------- splitting ----------

        /// <summary>
        /// Moves the active tab into a new pane carved out of the active pane's own area. Panes
        /// elsewhere in the tree keep their geometry.
        /// </summary>
        public void SplitActive(Orientation orientation)
        {
            if (_active is not { } leaf || leaf.Group.SelectedSession is not { } view)
            {
                return;
            }

            // A pane holding a single tab has nothing to give away.
            if (leaf.Group.Count < 2)
            {
                return;
            }

            TabViewItem? tab = leaf.Group.Detach(view);
            if (tab is null)
            {
                return;
            }

            PaneLeafNode fresh = SplitLeaf(leaf, orientation);
            fresh.Group.Add(tab);
            SetActive(fresh, notify: true);
            RefreshChrome();
        }

        /// <summary>
        /// Inserts a sibling next to <paramref name="leaf"/>. When the parent already runs in the
        /// requested direction the new pane joins it, which keeps every pane the same size; only a
        /// change of direction introduces a nested node.
        /// </summary>
        private PaneLeafNode SplitLeaf(PaneLeafNode leaf, Orientation orientation, bool before = false)
        {
            var fresh = new PaneLeafNode(CreateGroup());

            if (leaf.Parent is { } parent && parent.Orientation == orientation)
            {
                int index = parent.Children.IndexOf(leaf);
                parent.Children.Insert(before ? index : index + 1, fresh);
                fresh.Parent = parent;
                RebuildSplit(parent);
                return fresh;
            }

            var split = new PaneSplitNode(orientation);
            PaneSplitNode? grandparent = leaf.Parent;

            // Detach the leaf from wherever it lives, then re-host it inside the new split.
            if (grandparent is null)
            {
                Children.Remove(leaf.Element);
            }
            else
            {
                grandparent.Panel.Children.Remove(leaf.Element);
                int index = grandparent.Children.IndexOf(leaf);
                grandparent.Children[index] = split;
            }

            split.Parent = grandparent;
            if (before)
            {
                split.Children.Add(fresh);
                split.Children.Add(leaf);
            }
            else
            {
                split.Children.Add(leaf);
                split.Children.Add(fresh);
            }

            leaf.Parent = split;
            fresh.Parent = split;

            RebuildSplit(split);

            if (grandparent is null)
            {
                _root = split;
                MountRoot();
            }
            else
            {
                RebuildSplit(grandparent);
            }

            return fresh;
        }

        /// <summary>Gives every open session its own pane, side by side or stacked.</summary>
        public void SpreadAll(Orientation orientation)
        {
            List<TabViewItem> tabs = CollectTabs();
            if (tabs.Count < 2)
            {
                return;
            }

            TearDownTree();

            var split = new PaneSplitNode(orientation);
            foreach (TabViewItem tab in tabs)
            {
                var leaf = new PaneLeafNode(CreateGroup()) { Parent = split };
                leaf.Group.Add(tab);
                split.Children.Add(leaf);
            }

            _root = split;
            RebuildSplit(split);
            MountRoot();

            SetActive((PaneLeafNode)split.Children[0], notify: true);
            RefreshChrome();
        }

        /// <summary>Merges every pane back into one, preserving tab order.</summary>
        public void MergeAll()
        {
            if (PaneCount < 2)
            {
                return;
            }

            TerminalView? selected = ActiveView;
            List<TabViewItem> tabs = CollectTabs();

            TearDownTree();

            var leaf = new PaneLeafNode(CreateGroup());
            foreach (TabViewItem tab in tabs)
            {
                leaf.Group.Add(tab, select: false);
            }

            _root = leaf;
            MountRoot();

            SetActive(leaf, notify: true);
            if (selected is not null)
            {
                leaf.Group.Select(selected);
            }
            else if (tabs.Count > 0)
            {
                leaf.Group.Tabs.SelectedIndex = 0;
            }

            RefreshChrome();
        }

        /// <summary>Pulls every tab out of the tree, leaving the panes empty.</summary>
        private List<TabViewItem> CollectTabs()
        {
            var tabs = new List<TabViewItem>();

            foreach (PaneLeafNode leaf in Leaves().ToList())
            {
                tabs.AddRange(leaf.Group.Tabs.TabItems.OfType<TabViewItem>());
                leaf.Group.Tabs.TabItems.Clear();
            }

            return tabs;
        }

        private void TearDownTree()
        {
            Children.Clear();
            _root = null;
            _active = null;
        }

        // ---------- tree plumbing ----------

        private PaneLeafNode EnsureRootLeaf()
        {
            if (_root is PaneLeafNode existing)
            {
                return existing;
            }

            var leaf = new PaneLeafNode(CreateGroup());
            _root = leaf;
            MountRoot();
            _active = leaf;
            return leaf;
        }

        private void MountRoot()
        {
            Children.Clear();
            ColumnDefinitions.Clear();
            RowDefinitions.Clear();

            if (_root is null)
            {
                return;
            }

            SetRow(_root.Element, 0);
            SetColumn(_root.Element, 0);
            Children.Add(_root.Element);
        }

        /// <summary>
        /// Lays a split node's children out in its own grid. Children already parented stay put —
        /// only slots and dividers are recomputed — so WebViews are not needlessly re-parented.
        /// </summary>
        private void RebuildSplit(PaneSplitNode node)
        {
            Grid panel = node.Panel;
            bool sideBySide = node.Orientation == Orientation.Horizontal;

            for (int i = panel.Children.Count - 1; i >= 0; i--)
            {
                UIElement child = panel.Children[i];
                if (child is PaneSplitter || !node.Children.Any(c => ReferenceEquals(c.Element, child)))
                {
                    panel.Children.RemoveAt(i);
                }
            }

            panel.ColumnDefinitions.Clear();
            panel.RowDefinitions.Clear();

            for (int i = 0; i < node.Children.Count; i++)
            {
                if (i > 0)
                {
                    if (sideBySide)
                    {
                        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PaneSplitter.Thickness) });
                    }
                    else
                    {
                        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PaneSplitter.Thickness) });
                    }
                }

                if (sideBySide)
                {
                    panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }
                else
                {
                    panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                }
            }

            if (sideBySide)
            {
                panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                FrameworkElement element = node.Children[i].Element;
                if (sideBySide)
                {
                    SetColumn(element, i * 2);
                    SetRow(element, 0);
                }
                else
                {
                    SetRow(element, i * 2);
                    SetColumn(element, 0);
                }

                if (!panel.Children.Contains(element))
                {
                    panel.Children.Add(element);
                }
            }

            for (int i = 0; i < node.Children.Count - 1; i++)
            {
                var splitter = new PaneSplitter(
                    panel,
                    node.Orientation,
                    i * 2,
                    (i + 1) * 2,
                    node.Children[i].Element,
                    node.Children[i + 1].Element);

                if (sideBySide)
                {
                    SetColumn(splitter, i * 2 + 1);
                    SetRow(splitter, 0);
                }
                else
                {
                    SetRow(splitter, i * 2 + 1);
                    SetColumn(splitter, 0);
                }

                panel.Children.Add(splitter);
            }
        }

        /// <summary>Drops empty panes and collapses split nodes left with a single child.</summary>
        private void Prune()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;

                foreach (PaneLeafNode leaf in Leaves().ToList())
                {
                    // The very last pane stays even when empty; the empty-state UI covers it.
                    if (leaf.Group.Count > 0 || PaneCount <= 1)
                    {
                        continue;
                    }

                    RemoveNode(leaf);
                    changed = true;
                    break;
                }
            }

            if (_active is null || !Leaves().Contains(_active))
            {
                _active = Leaves().FirstOrDefault();
            }

            RefreshChrome();
        }

        private void RemoveNode(PaneNode node)
        {
            PaneSplitNode? parent = node.Parent;

            if (parent is null)
            {
                Children.Remove(node.Element);
                _root = null;
                _active = null;
                return;
            }

            parent.Panel.Children.Remove(node.Element);
            parent.Children.Remove(node);

            if (parent.Children.Count >= 2)
            {
                RebuildSplit(parent);
                return;
            }

            // One child left: the split no longer serves a purpose, so lift the child into its place.
            PaneNode survivor = parent.Children[0];
            parent.Panel.Children.Remove(survivor.Element);
            PaneSplitNode? grandparent = parent.Parent;
            survivor.Parent = grandparent;

            if (grandparent is null)
            {
                _root = survivor;
                MountRoot();
                return;
            }

            grandparent.Panel.Children.Remove(parent.Panel);
            int index = grandparent.Children.IndexOf(parent);
            grandparent.Children[index] = survivor;
            RebuildSplit(grandparent);
        }

        private IEnumerable<PaneLeafNode> Leaves() => Leaves(_root);

        private static IEnumerable<PaneLeafNode> Leaves(PaneNode? node)
        {
            switch (node)
            {
                case null:
                    yield break;

                case PaneLeafNode leaf:
                    yield return leaf;
                    break;

                case PaneSplitNode split:
                    foreach (PaneNode child in split.Children)
                    {
                        foreach (PaneLeafNode leaf in Leaves(child))
                        {
                            yield return leaf;
                        }
                    }

                    break;
            }
        }

        private PaneLeafNode? LeafOf(TerminalView view) =>
            Leaves().FirstOrDefault(l => l.Group.FindTab(view) is not null);

        private PaneLeafNode? LeafOf(PaneGroup group) =>
            Leaves().FirstOrDefault(l => ReferenceEquals(l.Group, group));

        private PaneGroup CreateGroup()
        {
            var group = new PaneGroup(_drag);

            group.Activated += (s, _) =>
            {
                if (LeafOf((PaneGroup)s!) is { } leaf)
                {
                    SetActive(leaf, notify: true);
                }
            };
            group.SelectionChanged += (s, _) =>
            {
                if (LeafOf((PaneGroup)s!) is { } leaf)
                {
                    SetActive(leaf, notify: false);
                }

                ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            };
            group.AddRequested += (s, _) =>
            {
                if (LeafOf((PaneGroup)s!) is { } leaf)
                {
                    SetActive(leaf, notify: false);
                }

                NewSessionRequested?.Invoke(this, EventArgs.Empty);
            };
            group.CloseRequested += (_, view) => CloseSession(view);
            group.TabDragStarted += (_, _) => StartDropHint();
            group.TabDragEnded += (s, tab) => OnTabDragEnded((PaneGroup)s!, tab);
            group.ItemsChanged += (_, _) => QueuePrune();

            return group;
        }

        private void SetActive(PaneLeafNode leaf, bool notify)
        {
            if (!ReferenceEquals(_active, leaf))
            {
                _active = leaf;
                RefreshChrome();
            }

            if (notify)
            {
                ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Refreshes per-pane outlines and each tab's connection-state icon.</summary>
        private void RefreshChrome()
        {
            List<PaneLeafNode> leaves = Leaves().ToList();
            bool showOutline = leaves.Count > 1;

            foreach (PaneLeafNode leaf in leaves)
            {
                leaf.Group.SetActiveOutline(ReferenceEquals(leaf, _active), showOutline);

                foreach (object item in leaf.Group.Tabs.TabItems)
                {
                    if (item is not TabViewItem { Content: TerminalView view } tab)
                    {
                        continue;
                    }

                    tab.IconSource = new SymbolIconSource
                    {
                        Symbol = view.State switch
                        {
                            TerminalState.Connecting => Symbol.Sync,
                            TerminalState.Reconnecting => Symbol.Refresh,
                            TerminalState.Connected => Symbol.Globe,
                            _ => Symbol.Cancel,
                        },
                    };
                }
            }
        }

        // ---------- moving tabs between panes ----------

        /// <summary>
        /// Completes a tab drag. WinUI moves tabs only within one TabView, so when the tab is still
        /// in its source pane we resolve the drop from the cursor: near a pane's edge it becomes a
        /// new pane on that side, anywhere else it joins that pane's tab strip.
        /// </summary>
        private void OnTabDragEnded(PaneGroup source, TabViewItem tab)
        {
            _drag.Clear();
            StopDropHint();

            if (!source.Tabs.TabItems.Contains(tab))
            {
                QueuePrune();
                return;
            }

            if (ResolveDrop() is not { } drop || LeafOf(drop.Group) is not { } targetLeaf)
            {
                QueuePrune();
                return;
            }

            if (drop.Side == DropSide.Center)
            {
                if (!ReferenceEquals(drop.Group, source))
                {
                    source.Tabs.TabItems.Remove(tab);
                    drop.Group.Add(tab);
                    SetActive(targetLeaf, notify: true);
                }

                QueuePrune();
                return;
            }

            // An edge drop always makes a new pane. Dropping the only tab of a pane onto its own
            // edge would just move the pane around, so that is a no-op.
            if (ReferenceEquals(drop.Group, source) && source.Count < 2)
            {
                QueuePrune();
                return;
            }

            source.Tabs.TabItems.Remove(tab);

            bool horizontal = drop.Side is DropSide.Left or DropSide.Right;
            bool before = drop.Side is DropSide.Left or DropSide.Top;
            PaneLeafNode fresh = SplitLeaf(
                targetLeaf,
                horizontal ? Orientation.Horizontal : Orientation.Vertical,
                before);

            fresh.Group.Add(tab);
            SetActive(fresh, notify: true);
            QueuePrune();
        }

        private enum DropSide
        {
            Center,
            Left,
            Right,
            Top,
            Bottom,
        }

        /// <summary>Which pane the cursor is over, and whether it is close enough to an edge.</summary>
        private (PaneGroup Group, DropSide Side)? ResolveDrop()
        {
            if (!TryCursorPoint(out Windows.Foundation.Point point))
            {
                return null;
            }

            foreach (PaneLeafNode leaf in Leaves())
            {
                PaneGroup group = leaf.Group;
                double w = group.ActualWidth;
                double h = group.ActualHeight;
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                Windows.Foundation.Point origin = group
                    .TransformToVisual(null)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));

                double x = point.X - origin.X;
                double y = point.Y - origin.Y;
                if (x < 0 || y < 0 || x > w || y > h)
                {
                    continue;
                }

                // The tab strip is the natural "put it in this pane" target, never an edge.
                if (y < TabStripHeight)
                {
                    return (group, DropSide.Center);
                }

                double bandX = Math.Clamp(w * 0.25, 40, 160);
                double bandY = Math.Clamp(h * 0.25, 40, 160);

                double left = x;
                double right = w - x;
                double top = y - TabStripHeight;
                double bottom = h - y;

                double best = double.MaxValue;
                DropSide side = DropSide.Center;

                if (left < bandX && left < best)
                {
                    best = left;
                    side = DropSide.Left;
                }

                if (right < bandX && right < best)
                {
                    best = right;
                    side = DropSide.Right;
                }

                if (top < bandY && top < best)
                {
                    best = top;
                    side = DropSide.Top;
                }

                if (bottom < bandY && bottom < best)
                {
                    side = DropSide.Bottom;
                }

                return (group, side);
            }

            return null;
        }

        private bool TryCursorPoint(out Windows.Foundation.Point point)
        {
            point = default;

            if (XamlRoot is null || !NativeCursor.TryGetClientPosition(WindowHandle, out double cx, out double cy))
            {
                return false;
            }

            double scale = XamlRoot.RasterizationScale;
            if (scale <= 0)
            {
                scale = 1;
            }

            point = new Windows.Foundation.Point(cx / scale, cy / scale);
            return true;
        }

        // ---------- drop preview ----------

        /// <summary>
        /// Shows where the dragged tab would land. WinUI's drag/drop events never reach the panes,
        /// so the cursor is polled for the duration of the drag instead.
        /// </summary>
        private void StartDropHint()
        {
            _dropHint ??= new Border
            {
                Background = AppAccent.DropFillBrush(),
                BorderBrush = AppAccent.Brush(),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };

            if (!Children.Contains(_dropHint))
            {
                SetRow(_dropHint, 0);
                SetColumn(_dropHint, 0);
                Children.Add(_dropHint);
            }

            _dropHintTimer ??= CreateDropHintTimer();
            _dropHintTimer.Start();
        }

        private DispatcherQueueTimer CreateDropHintTimer()
        {
            DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(40);
            timer.Tick += (_, _) => UpdateDropHint();
            return timer;
        }

        private void StopDropHint()
        {
            _dropHintTimer?.Stop();

            if (_dropHint is not null)
            {
                _dropHint.Visibility = Visibility.Collapsed;
                Children.Remove(_dropHint);
            }
        }

        private void UpdateDropHint()
        {
            if (_dropHint is null)
            {
                return;
            }

            if (ResolveDrop() is not { } drop)
            {
                _dropHint.Visibility = Visibility.Collapsed;
                return;
            }

            PaneGroup group = drop.Group;
            Windows.Foundation.Point origin = group
                .TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            double w = group.ActualWidth;
            double h = group.ActualHeight;
            double x = origin.X;
            double y = origin.Y;

            switch (drop.Side)
            {
                case DropSide.Left:
                    w /= 2;
                    break;
                case DropSide.Right:
                    x += w / 2;
                    w /= 2;
                    break;
                case DropSide.Top:
                    h /= 2;
                    break;
                case DropSide.Bottom:
                    y += h / 2;
                    h /= 2;
                    break;
            }

            _dropHint.Margin = new Thickness(x, y, 0, 0);
            _dropHint.Width = Math.Max(0, w);
            _dropHint.Height = Math.Max(0, h);
            _dropHint.Visibility = Visibility.Visible;
        }

        /// <summary>Drag/drop finishes asynchronously, so pruning has to wait a turn.</summary>
        private void QueuePrune()
        {
            if (_pruneQueued)
            {
                return;
            }

            _pruneQueued = true;
            DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
            {
                _pruneQueued = false;
                Prune();
            }));
        }

        // ---------- tab menu ----------

        /// <summary>Right-click menu on a tab.</summary>
        private MenuFlyout BuildTabMenu(TerminalView view)
        {
            var menu = new MenuFlyout();

            var duplicate = new MenuFlyoutItem { Text = "Duplicate" };
            duplicate.Click += (_, _) =>
            {
                // Land the copy in the pane the tab lives in, not wherever focus happens to be.
                if (LeafOf(view) is { } leaf)
                {
                    SetActive(leaf, notify: false);
                }

                DuplicateRequested?.Invoke(this, view);
            };

            var splitRight = new MenuFlyoutItem
            {
                Text = "Split right",
                KeyboardAcceleratorTextOverride = "Alt+Shift++",
            };
            splitRight.Click += (_, _) => SplitTab(view, Orientation.Horizontal);

            var splitDown = new MenuFlyoutItem
            {
                Text = "Split down",
                KeyboardAcceleratorTextOverride = "Alt+Shift+-",
            };
            splitDown.Click += (_, _) => SplitTab(view, Orientation.Vertical);

            var close = new MenuFlyoutItem
            {
                Text = "Close",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+W",
            };
            close.Click += (_, _) => CloseSession(view);

            menu.Items.Add(duplicate);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(splitRight);
            menu.Items.Add(splitDown);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(close);

            return menu;
        }

        private void SplitTab(TerminalView view, Orientation orientation)
        {
            if (LeafOf(view) is not { } leaf)
            {
                return;
            }

            SetActive(leaf, notify: false);
            leaf.Group.Select(view);
            SplitActive(orientation);
        }

        // ---------- pane events ----------

        private void OnPaneCommand(object? sender, TerminalCommand command)
        {
            if (sender is not TerminalView view)
            {
                return;
            }

            if (LeafOf(view) is { } leaf)
            {
                SetActive(leaf, notify: false);
            }

            switch (command)
            {
                case TerminalCommand.PaneClicked:
                    ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case TerminalCommand.TileSideBySide:
                    SplitActive(Orientation.Horizontal);
                    break;

                case TerminalCommand.TileStacked:
                    SplitActive(Orientation.Vertical);
                    break;

                case TerminalCommand.CloseSession:
                    CloseSession(view);
                    break;

                case TerminalCommand.NextSession:
                    CycleActive(1);
                    break;

                case TerminalCommand.PreviousSession:
                    CycleActive(-1);
                    break;

                case TerminalCommand.NewTab:
                    NewSessionRequested?.Invoke(this, EventArgs.Empty);
                    break;

                default:
                    WindowCommandRequested?.Invoke(this, command);
                    break;
            }
        }

        private void OnSessionStateChanged(object? sender, TerminalState state)
        {
            RefreshChrome();

            if (sender is TerminalView view && ReferenceEquals(view, ActiveView))
            {
                ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnSessionTitleChanged(object? sender, string title)
        {
            if (sender is TerminalView view && ReferenceEquals(view, ActiveView))
            {
                ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
