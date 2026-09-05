using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Claret.Models;
using Claret.Services;

namespace Claret.Controls
{
    /// <summary>One row in the port list. A class, because x:Bind wants a public bindable type.</summary>
    public sealed class SerialPortItem
    {
        public SerialPortItem(string portName, string detail)
        {
            PortName = portName;
            Detail = detail;
        }

        public string PortName { get; }

        public string Detail { get; }
    }

    /// <summary>
    /// Sidebar "Serial" tab: the ports this machine has, the line settings to open one with, and
    /// the button that does it. Scanning is off the UI thread — WMI takes its time — and the
    /// settings are remembered by the shell, so the next port opens the same way as the last.
    /// </summary>
    public sealed partial class SerialPortsView : UserControl
    {
        private static readonly (string Label, int DataBits, Parity Parity, StopBits StopBits)[] Formats =
        {
            ("8N1", 8, Parity.None, StopBits.One),
            ("8E1", 8, Parity.Even, StopBits.One),
            ("8O1", 8, Parity.Odd, StopBits.One),
            ("7E1", 7, Parity.Even, StopBits.One),
            ("8N2", 8, Parity.None, StopBits.Two),
        };

        private static readonly (string Label, Handshake Handshake)[] Flows =
        {
            ("None", Handshake.None),
            ("RTS/CTS", Handshake.RequestToSend),
            ("XON/XOFF", Handshake.XOnXOff),
        };

        // Segoe Fluent glyphs the one button switches between: a plug to open, a cross to close.
        private const string PlugGlyph = "\uE839";

        private const string CloseGlyph = "\uE8BB";

        private readonly ObservableCollection<SerialPortItem> _ports = new();

        /// <summary>Ports with a console open right now, as last reported by the shell.</summary>
        private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

        private bool _loading;

        public SerialPortsView()
        {
            InitializeComponent();

            _loading = true;

            foreach (int baud in SerialConnection.CommonBaudRates)
            {
                BaudBox.Items.Add(baud.ToString());
            }

            foreach ((string label, _, _, _) in Formats)
            {
                FormatBox.Items.Add(label);
            }

            foreach ((string label, _) in Flows)
            {
                FlowBox.Items.Add(label);
            }

            PortList.ItemsSource = _ports;
            _loading = false;
        }

        /// <summary>Raised when the user asks to open a port with the settings shown.</summary>
        public event EventHandler<SerialConnection>? OpenRequested;

        /// <summary>
        /// Raised when the user asks to close the port that is already open. Carries the port name
        /// rather than the settings: what is being closed is the line, whatever it was opened with.
        /// </summary>
        public event EventHandler<string>? CloseRequested;

        /// <summary>Raised when the settings change, so the shell can remember them.</summary>
        public event EventHandler<SerialConnection>? SettingsChanged;

        /// <summary>The user pinned the port and settings currently shown.</summary>
        public event EventHandler<SerialConnection>? PinRequested;

        /// <summary>The user asked to rename a pinned board; the shell owns the dialog.</summary>
        public event EventHandler<SerialProfile>? RenameRequested;

        /// <summary>The user unpinned a board.</summary>
        public event EventHandler<SerialProfile>? UnpinRequested;

        /// <summary>Binds the pinned list. Called once, before the first layout.</summary>
        public void BindPinned(ObservableCollection<SerialProfile> pinned)
        {
            PinnedList.ItemsSource = pinned;
            pinned.CollectionChanged += (_, _) => UpdatePinnedVisibility(pinned);
            UpdatePinnedVisibility(pinned);
        }

        private void UpdatePinnedVisibility(ObservableCollection<SerialProfile> pinned)
        {
            Visibility show = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PinnedHeader.Visibility = show;
            PinnedList.Visibility = show;
        }

        /// <summary>Applies the remembered settings and lists the ports. Called once, at startup.</summary>
        public void Initialize(SerialConnection settings)
        {
            _loading = true;

            BaudBox.SelectedIndex = Math.Max(0, Array.IndexOf(SerialConnection.CommonBaudRates, settings.BaudRate));

            int format = Array.FindIndex(Formats, entry =>
                entry.DataBits == settings.DataBits
                && entry.Parity == settings.Parity
                && entry.StopBits == settings.StopBits);
            FormatBox.SelectedIndex = format < 0 ? 0 : format;

            int flow = Array.FindIndex(Flows, entry => entry.Handshake == settings.Handshake);
            FlowBox.SelectedIndex = flow < 0 ? 0 : flow;

            _loading = false;

            _ = RefreshAsync();
        }

        /// <summary>The settings as shown, with the selected port filled in when there is one.</summary>
        public SerialConnection Current
        {
            get
            {
                (string _, int dataBits, Parity parity, StopBits stopBits) =
                    Formats[Math.Max(0, FormatBox.SelectedIndex)];

                return new SerialConnection
                {
                    PortName = (PortList.SelectedItem as SerialPortItem)?.PortName ?? string.Empty,
                    BaudRate = SerialConnection.CommonBaudRates[Math.Max(0, BaudBox.SelectedIndex)],
                    DataBits = dataBits,
                    Parity = parity,
                    StopBits = stopBits,
                    Handshake = Flows[Math.Max(0, FlowBox.SelectedIndex)].Handshake,
                };
            }
        }

        /// <summary>
        /// Rescans. Keeps the selection if that port is still there, which matters when an adapter
        /// is replugged while the panel is open.
        /// </summary>
        public async Task RefreshAsync()
        {
            string? selected = (PortList.SelectedItem as SerialPortItem)?.PortName;

            IReadOnlyList<SerialPortInfo> found = await Task.Run(SerialPortScanner.Scan).ConfigureAwait(true);

            _ports.Clear();
            foreach (SerialPortInfo port in found)
            {
                _ports.Add(new SerialPortItem(port.PortName, port.Detail));
            }

            bool empty = _ports.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            PortList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            SerialPortItem? again = _ports.FirstOrDefault(item =>
                string.Equals(item.PortName, selected, StringComparison.OrdinalIgnoreCase));

            PortList.SelectedItem = again ?? _ports.FirstOrDefault();
        }

        /// <summary>
        /// Tells the panel which ports have a console open, so the button can offer to close the
        /// one selected. The shell owns the sessions, so it owns this answer; the panel only draws
        /// it. Cheap enough to call from the status tick, and it returns early unless something
        /// actually changed.
        /// </summary>
        public void ShowOpenPorts(IReadOnlyCollection<string> openPorts)
        {
            if (_open.Count == openPorts.Count && openPorts.All(_open.Contains))
            {
                return;
            }

            _open.Clear();
            foreach (string port in openPorts)
            {
                _open.Add(port);
            }

            UpdateOpenButton();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOpenButton();

        /// <summary>Whether the selected port is one of the ports currently open.</summary>
        private bool SelectionIsOpen =>
            PortList.SelectedItem is SerialPortItem item && _open.Contains(item.PortName);

        private void UpdateOpenButton()
        {
            var selected = PortList.SelectedItem as SerialPortItem;

            OpenButton.IsEnabled = selected is not null;
            PinButton.IsEnabled = selected is not null;

            if (selected is null)
            {
                OpenLabel.Text = "Open port";
                OpenIcon.Glyph = PlugGlyph;
                OpenButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                ToolTipService.SetToolTip(OpenButton, null);
                return;
            }

            bool open = _open.Contains(selected.PortName);

            OpenLabel.Text = open ? $"Close {selected.PortName}" : $"Open {selected.PortName}";
            OpenIcon.Glyph = open ? CloseGlyph : PlugGlyph;
            OpenButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

            ToolTipService.SetToolTip(
                OpenButton,
                open ? $"Close the console on {selected.PortName}" : null);
        }

        private void OnPinClick(object sender, RoutedEventArgs e)
        {
            SerialConnection settings = Current;
            if (settings.PortName.Length > 0)
            {
                PinRequested?.Invoke(this, settings);
            }
        }

        private void OnPinnedDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenPinned();

        private void OnOpenPinnedClick(object sender, RoutedEventArgs e) => OpenPinned();

        private void OnRenamePinnedClick(object sender, RoutedEventArgs e)
        {
            if (Pinned(sender) is { } profile)
            {
                RenameRequested?.Invoke(this, profile);
            }
        }

        private void OnUnpinClick(object sender, RoutedEventArgs e)
        {
            if (Pinned(sender) is { } profile)
            {
                UnpinRequested?.Invoke(this, profile);
            }
        }

        private void OpenPinned()
        {
            if (PinnedList.SelectedItem is SerialProfile profile)
            {
                OpenRequested?.Invoke(this, profile.Settings);
            }
        }

        /// <summary>Menu items carry the right-clicked row in DataContext; fall back to the selection.</summary>
        private SerialProfile? Pinned(object sender) =>
            sender is FrameworkElement { DataContext: SerialProfile fromContext }
                ? fromContext
                : PinnedList.SelectedItem as SerialProfile;

        private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            SettingsChanged?.Invoke(this, Current);
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

        /// <summary>
        /// Double-tapping a row always means open, never close. The button says which of the two it
        /// would do; a double-tap says nothing, and closing a live console by accident is not a
        /// mistake a gesture should be able to make. Opening one that is already open is answered
        /// by the shell, which offers to go to it.
        /// </summary>
        private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Open();

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            if (PortList.SelectedItem is SerialPortItem selected && SelectionIsOpen)
            {
                CloseRequested?.Invoke(this, selected.PortName);
                return;
            }

            Open();
        }

        private void Open()
        {
            SerialConnection settings = Current;
            if (settings.PortName.Length == 0)
            {
                return;
            }

            OpenRequested?.Invoke(this, settings);
        }
    }
}
