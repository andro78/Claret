using System;
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
    public sealed class ConnectionProfile
    {
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

        [JsonIgnore]
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Name) ? Endpoint : Name;

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
        };
    }
}
