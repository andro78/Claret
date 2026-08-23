using System;
using System.Text.Json.Serialization;

namespace Claret.Models
{
    /// <summary>
    /// One entry of the connection history. Deliberately holds no secret — reconnecting looks the
    /// saved profile up by <see cref="ProfileId"/> (or endpoint) so DPAPI-protected credentials
    /// still apply, and otherwise asks for the password.
    /// </summary>
    public sealed class RecentConnection
    {
        public string Name { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        public int Port { get; set; } = 22;

        public string Username { get; set; } = string.Empty;

        public SshAuthMode AuthMode { get; set; } = SshAuthMode.Password;

        public string PrivateKeyPath { get; set; } = string.Empty;

        /// <summary>Id of the saved profile this came from, when it came from one.</summary>
        public string? ProfileId { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        [JsonIgnore]
        public string Endpoint => Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Endpoint : Name;

        /// <summary>Identity for de-duplication: one history row per endpoint.</summary>
        [JsonIgnore]
        public string Key => $"{Username}@{Host}:{Port}";

        public static RecentConnection From(ConnectionProfile profile) => new()
        {
            Name = profile.Name,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMode = profile.AuthMode,
            PrivateKeyPath = profile.PrivateKeyPath,
            ProfileId = profile.Id,
            LastUsed = DateTimeOffset.Now,
        };

        /// <summary>A transient profile for reconnecting when the saved one is gone.</summary>
        public ConnectionProfile ToProfile() => new()
        {
            Name = Name,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthMode = AuthMode,
            PrivateKeyPath = PrivateKeyPath,
            RememberSecret = false,
        };
    }
}
