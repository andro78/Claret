using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Claret.Models
{
    /// <summary>How the session authenticates against the SSH server.</summary>
    public enum SshAuthMode
    {
        Password,
        PrivateKey,
    }

    /// <summary>
    /// A saved SSH endpoint. Secrets are never held here in plaintext: <see cref="ProtectedSecret"/>
    /// holds a DPAPI blob that only the current Windows user can unwrap.
    /// </summary>
    public sealed class ConnectionProfile : INotifyPropertyChanged
    {
        private RemoteOs _lastOs = RemoteOs.Unknown;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        public int Port { get; set; } = 22;

        public string Username { get; set; } = string.Empty;

        public SshAuthMode AuthMode { get; set; } = SshAuthMode.Password;

        /// <summary>Path to an OpenSSH or PEM private key. Only used when <see cref="AuthMode"/> is <see cref="SshAuthMode.PrivateKey"/>.</summary>
        public string PrivateKeyPath { get; set; } = string.Empty;

        /// <summary>Whether the password / passphrase is persisted (DPAPI-protected) with the profile.</summary>
        public bool RememberSecret { get; set; }

        /// <summary>Base64 DPAPI blob of the password or key passphrase. Null when nothing is remembered.</summary>
        public string? ProtectedSecret { get; set; }

        /// <summary>Terminal type advertised to the server.</summary>
        public string TerminalType { get; set; } = "xterm-256color";

        /// <summary>
        /// What this host turned out to be running, last time it was connected to. Remembered so the
        /// list can show it before connecting — the icon is only ever an answer the host gave, not
        /// a guess from the name.
        /// <para>
        /// The one property here that changes while a row is on screen, so it is the one that has to
        /// announce itself. Replacing the item in the list would redraw it too, but it would also
        /// drop the selection — and this arrives a second after the double-click that made it.
        /// </para>
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RemoteOs LastOs
        {
            get => _lastOs;
            set
            {
                if (_lastOs == value)
                {
                    return;
                }

                _lastOs = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastOs)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isOpen;

        /// <summary>Whether a tab is connected to this profile right now. Never saved.</summary>
        [JsonIgnore]
        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen == value)
                {
                    return;
                }

                _isOpen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
            }
        }

        [JsonIgnore]
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Name) ? Endpoint : Name;

        // A name that just repeats the host (the connection dialog's default) is not a name.
        private bool HasOwnName =>
            !string.IsNullOrWhiteSpace(Name)
            && !string.Equals(Name.Trim(), Host, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Name.Trim(), Endpoint, StringComparison.OrdinalIgnoreCase);

        /// <summary>First line of the profile list: the name, or the host when there is none.</summary>
        [JsonIgnore]
        public string ListTitle => HasOwnName ? Name.Trim() : Host;

        /// <summary>Second line: whatever the first line left out, never a repeat of it.</summary>
        [JsonIgnore]
        public string ListSubtitle => HasOwnName
            ? Endpoint
            : Port == 22 ? Username : $"{Username} · port {Port}";

        [JsonIgnore]
        public string Endpoint =>
            Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

        public ConnectionProfile Clone() => new()
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthMode = AuthMode,
            PrivateKeyPath = PrivateKeyPath,
            RememberSecret = RememberSecret,
            ProtectedSecret = ProtectedSecret,
            TerminalType = TerminalType,
            LastOs = LastOs,
        };
    }
}
