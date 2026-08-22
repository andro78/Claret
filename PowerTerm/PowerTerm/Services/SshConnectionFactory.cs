using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using PowerTerm.Models;

namespace PowerTerm.Services
{
    /// <summary>
    /// Records whether the host key was refused. The SSH.NET callback cannot throw, so the reason is
    /// parked here and the caller turns it into a <see cref="HostKeyMismatchException"/>.
    /// </summary>
    internal sealed class HostKeyGate
    {
        public string? Failure { get; set; }
    }

    /// <summary>
    /// Builds connections from a <see cref="ConnectionProfile"/>. Shared so the shell and the SFTP
    /// browser authenticate identically and, more importantly, apply the same host key pinning —
    /// a second channel that skipped the check would defeat the point of pinning at all.
    /// </summary>
    internal static class SshConnectionFactory
    {
        public static ConnectionInfo Build(ConnectionProfile profile, string? secret)
        {
            if (string.IsNullOrWhiteSpace(profile.Host))
            {
                throw new ArgumentException("Enter a host.", nameof(profile));
            }

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                throw new ArgumentException("Enter a user name.", nameof(profile));
            }

            var methods = new List<AuthenticationMethod>();

            if (profile.AuthMode == SshAuthMode.PrivateKey)
            {
                if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                {
                    throw new ArgumentException("Enter the private key file path.", nameof(profile));
                }

                if (!File.Exists(profile.PrivateKeyPath))
                {
                    throw new FileNotFoundException($"Private key file not found: {profile.PrivateKeyPath}");
                }

                PrivateKeyFile keyFile = string.IsNullOrEmpty(secret)
                    ? new PrivateKeyFile(profile.PrivateKeyPath)
                    : new PrivateKeyFile(profile.PrivateKeyPath, secret);

                methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
            }
            else
            {
                string password = secret ?? string.Empty;
                methods.Add(new PasswordAuthenticationMethod(profile.Username, password));

                // Many sshd configurations only offer keyboard-interactive for passwords.
                var interactive = new KeyboardInteractiveAuthenticationMethod(profile.Username);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (AuthenticationPrompt prompt in e.Prompts)
                    {
                        // "암호" catches Korean-localised sshd prompts. This matches text sent by
                        // the remote server, not app UI, so it stays.
                        if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                            prompt.Request.Contains("암호", StringComparison.Ordinal))
                        {
                            prompt.Response = password;
                        }
                    }
                };
                methods.Add(interactive);
            }

            return new ConnectionInfo(profile.Host, profile.Port, profile.Username, methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(20),
                Encoding = Encoding.UTF8,
            };
        }

        /// <summary>
        /// Applies trust-on-first-use pinning to <paramref name="client"/>. <paramref name="notice"/>
        /// receives an informational line when a key is pinned for the first time.
        /// </summary>
        public static HostKeyGate GuardHostKey(
            BaseClient client,
            ConnectionProfile profile,
            Action<string>? notice = null)
        {
            var gate = new HostKeyGate();

            client.HostKeyReceived += (_, e) =>
            {
                HostKeyVerdict verdict = KnownHostsStore.Instance.Verify(
                    profile.Host, profile.Port, e.HostKeyName, e.FingerPrintSHA256);

                switch (verdict)
                {
                    case HostKeyVerdict.Pinned:
                        e.CanTrust = true;
                        notice?.Invoke(
                            $"Pinned a new host key.\n  {e.HostKeyName} SHA256:{e.FingerPrintSHA256}\n");
                        break;

                    case HostKeyVerdict.Trusted:
                        e.CanTrust = true;
                        break;

                    default:
                        e.CanTrust = false;
                        gate.Failure =
                            $"The host key differs from the pinned one ({e.HostKeyName} SHA256:{e.FingerPrintSHA256}). " +
                            "The server may have been reinstalled, or this may be a man-in-the-middle attack. " +
                            "Verify it, then use \"Forget host key\" on the profile and reconnect.";
                        break;
                }
            };

            return gate;
        }
    }
}
