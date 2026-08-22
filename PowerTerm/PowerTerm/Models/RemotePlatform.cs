using System;
using System.Collections.Generic;

namespace PowerTerm.Models
{
    /// <summary>What the far end turned out to be running. Unknown is the honest default.</summary>
    public enum RemoteOs
    {
        Unknown,
        Linux,
        Ubuntu,
        Debian,
        Fedora,
        RedHat,
        Suse,
        Arch,
        Alpine,
        Raspbian,
        Bsd,
        MacOs,
        Windows,
    }

    /// <summary>
    /// The platform behind a session, worked out on connect. Two sources feed it: /etc/os-release
    /// and uname when the host lets us run a command, and the SSH banner when it does not — a BMC
    /// or a locked-down shell often refuses the first but always sends the second.
    /// </summary>
    public sealed class RemotePlatform
    {
        public static readonly RemotePlatform Unknown = new(RemoteOs.Unknown, string.Empty);

        private RemotePlatform(RemoteOs os, string name)
        {
            Os = os;
            Name = name;
        }

        public RemoteOs Os { get; }

        /// <summary>Human-readable name, e.g. "Ubuntu 22.04.4 LTS". Empty when nothing was learned.</summary>
        public string Name { get; }

        public bool IsKnown => Os != RemoteOs.Unknown;

        /// <summary>
        /// Reads the output of <c>cat /etc/os-release; uname -sr</c>. os-release wins because it
        /// names the distribution; uname only ever gives the kernel.
        /// </summary>
        public static RemotePlatform FromProbe(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return Unknown;
            }

            Dictionary<string, string> fields = ParseOsRelease(output);

            fields.TryGetValue("ID", out string? id);
            fields.TryGetValue("ID_LIKE", out string? idLike);
            fields.TryGetValue("PRETTY_NAME", out string? pretty);
            fields.TryGetValue("NAME", out string? name);
            fields.TryGetValue("VERSION", out string? version);

            RemoteOs os = Classify(id) is { } fromId and not RemoteOs.Unknown
                ? fromId
                : Classify(idLike) is { } fromLike and not RemoteOs.Unknown
                    ? fromLike
                    : FromText(output);

            string label = !string.IsNullOrWhiteSpace(pretty)
                ? pretty!
                : !string.IsNullOrWhiteSpace(name)
                    ? string.Join(' ', new[] { name, version }, 0, version is null ? 1 : 2).Trim()
                    : FirstLine(output);

            return os == RemoteOs.Unknown && label.Length == 0
                ? Unknown
                : new RemotePlatform(os, label);
        }

        /// <summary>
        /// Falls back to the SSH identification string, e.g.
        /// "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.6", which names the distribution often enough
        /// to be worth reading when a command channel is not available.
        /// </summary>
        public static RemotePlatform FromServerVersion(string? serverVersion)
        {
            if (string.IsNullOrWhiteSpace(serverVersion))
            {
                return Unknown;
            }

            RemoteOs os = FromText(serverVersion);
            return os == RemoteOs.Unknown ? Unknown : new RemotePlatform(os, Describe(os));
        }

        /// <summary>The label to show when nothing better is known.</summary>
        public static string Describe(RemoteOs os) => os switch
        {
            RemoteOs.Ubuntu => "Ubuntu",
            RemoteOs.Debian => "Debian",
            RemoteOs.Fedora => "Fedora",
            RemoteOs.RedHat => "Red Hat family",
            RemoteOs.Suse => "SUSE",
            RemoteOs.Arch => "Arch Linux",
            RemoteOs.Alpine => "Alpine Linux",
            RemoteOs.Raspbian => "Raspberry Pi OS",
            RemoteOs.Linux => "Linux",
            RemoteOs.Bsd => "BSD",
            RemoteOs.MacOs => "macOS",
            RemoteOs.Windows => "Windows",
            _ => string.Empty,
        };

        private static Dictionary<string, string> ParseOsRelease(string output)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                int split = line.IndexOf('=');
                if (split <= 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string key = line[..split].Trim();
                string value = line[(split + 1)..].Trim().Trim('"').Trim('\'');
                if (key.Length > 0 && value.Length > 0)
                {
                    fields[key] = value;
                }
            }

            return fields;
        }

        /// <summary>Matches an os-release ID (or ID_LIKE, which may list several).</summary>
        private static RemoteOs Classify(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return RemoteOs.Unknown;
            }

            foreach (string token in id.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                RemoteOs os = token.Trim().ToLowerInvariant() switch
                {
                    "ubuntu" => RemoteOs.Ubuntu,
                    "debian" => RemoteOs.Debian,
                    "raspbian" => RemoteOs.Raspbian,
                    "fedora" => RemoteOs.Fedora,
                    "rhel" or "redhat" or "centos" or "rocky" or "almalinux" or "ol" or "amzn"
                        => RemoteOs.RedHat,
                    "opensuse" or "opensuse-leap" or "opensuse-tumbleweed" or "sles" or "suse"
                        => RemoteOs.Suse,
                    "arch" or "archarm" or "manjaro" => RemoteOs.Arch,
                    "alpine" => RemoteOs.Alpine,
                    "freebsd" or "openbsd" or "netbsd" => RemoteOs.Bsd,
                    "darwin" or "macos" => RemoteOs.MacOs,
                    _ => RemoteOs.Unknown,
                };

                if (os != RemoteOs.Unknown)
                {
                    return os;
                }
            }

            return RemoteOs.Unknown;
        }

        /// <summary>Last resort: look for a distribution or kernel name anywhere in the text.</summary>
        private static RemoteOs FromText(string text)
        {
            string lower = text.ToLowerInvariant();

            // Ordered so a distribution wins over the generic kernel name it also contains.
            (string needle, RemoteOs os)[] hints =
            {
                ("ubuntu", RemoteOs.Ubuntu),
                ("raspbian", RemoteOs.Raspbian),
                ("raspberry", RemoteOs.Raspbian),
                ("debian", RemoteOs.Debian),
                ("fedora", RemoteOs.Fedora),
                ("red hat", RemoteOs.RedHat),
                ("rhel", RemoteOs.RedHat),
                ("centos", RemoteOs.RedHat),
                ("rocky", RemoteOs.RedHat),
                ("almalinux", RemoteOs.RedHat),
                ("amazon linux", RemoteOs.RedHat),
                ("suse", RemoteOs.Suse),
                ("arch linux", RemoteOs.Arch),
                ("manjaro", RemoteOs.Arch),
                ("alpine", RemoteOs.Alpine),
                ("freebsd", RemoteOs.Bsd),
                ("openbsd", RemoteOs.Bsd),
                ("netbsd", RemoteOs.Bsd),
                ("darwin", RemoteOs.MacOs),
                ("mac os", RemoteOs.MacOs),
                ("windows", RemoteOs.Windows),
                ("microsoft", RemoteOs.Windows),
                ("linux", RemoteOs.Linux),
            };

            foreach ((string needle, RemoteOs os) in hints)
            {
                if (lower.Contains(needle, StringComparison.Ordinal))
                {
                    return os;
                }
            }

            return RemoteOs.Unknown;
        }

        private static string FirstLine(string text)
        {
            string line = text.Split('\n')[0].Trim();
            return line.Length > 60 ? line[..60] : line;
        }
    }
}
