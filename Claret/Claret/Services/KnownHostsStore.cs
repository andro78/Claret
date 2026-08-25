using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Claret.Services
{
    internal enum HostKeyVerdict
    {
        /// <summary>The host was unknown; its key has now been pinned.</summary>
        Pinned,

        /// <summary>The key matches the pinned one.</summary>
        Trusted,

        /// <summary>The key differs from the pinned one — connection must be refused.</summary>
        Mismatch,
    }

    /// <summary>Outcome of <see cref="KnownHostsStore.Forget"/>, so a caller can tell the user what actually happened.</summary>
    internal enum HostKeyForgetResult
    {
        /// <summary>Nothing was pinned for that host:port — most likely the wrong profile was targeted.</summary>
        NotFound,

        /// <summary>Removed and persisted to disk.</summary>
        Removed,

        /// <summary>Removed from memory, but the write to disk failed — it will reappear after a restart.</summary>
        RemovedButNotSaved,
    }

    /// <summary>
    /// Trust-on-first-use host key pinning. The first key seen for an endpoint is stored; any later
    /// change is reported as a mismatch instead of being silently accepted.
    /// </summary>
    internal sealed class KnownHostsStore
    {
        private static readonly Lazy<KnownHostsStore> Lazy = new(() => new KnownHostsStore());

        public static KnownHostsStore Instance => Lazy.Value;

        private readonly object _gate = new();
        private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public HostKeyVerdict Verify(string host, int port, string keyName, string fingerprintSha256)
        {
            string key = $"{host}:{port}|{keyName}";

            lock (_gate)
            {
                EnsureLoaded();

                if (!_fingerprints.TryGetValue(key, out string? known))
                {
                    _fingerprints[key] = fingerprintSha256;
                    TrySave();
                    return HostKeyVerdict.Pinned;
                }

                return string.Equals(known, fingerprintSha256, StringComparison.Ordinal)
                    ? HostKeyVerdict.Trusted
                    : HostKeyVerdict.Mismatch;
            }
        }

        /// <summary>Drops the pinned key for an endpoint so the next connect re-pins it.</summary>
        public HostKeyForgetResult Forget(string host, int port)
        {
            lock (_gate)
            {
                EnsureLoaded();

                string prefix = $"{host}:{port}|";
                var stale = new List<string>();
                foreach (string key in _fingerprints.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        stale.Add(key);
                    }
                }

                if (stale.Count == 0)
                {
                    return HostKeyForgetResult.NotFound;
                }

                foreach (string key in stale)
                {
                    _fingerprints.Remove(key);
                }

                return TrySave() ? HostKeyForgetResult.Removed : HostKeyForgetResult.RemovedButNotSaved;
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            try
            {
                if (!File.Exists(AppPaths.KnownHostsFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.KnownHostsFile);
                Dictionary<string, string>? loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (loaded is null)
                {
                    return;
                }

                foreach (KeyValuePair<string, string> pair in loaded)
                {
                    _fingerprints[pair.Key] = pair.Value;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged file must not block connecting; it gets rewritten on the next pin.
            }
        }

        private bool TrySave()
        {
            try
            {
                string json = JsonSerializer.Serialize(_fingerprints, new JsonSerializerOptions { WriteIndented = true });
                string temp = AppPaths.KnownHostsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.KnownHostsFile, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: pinning simply will not persist across restarts.
                return false;
            }
        }
    }
}
