using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Claret.Services
{
    /// <summary>How a check came out, for whatever asked to show something about it.</summary>
    public enum UpdateCheckResult
    {
        /// <summary>Already on the latest version.</summary>
        UpToDate,

        /// <summary>A newer version was found and is downloaded, waiting for a restart to apply.</summary>
        UpdateReady,

        /// <summary>The check itself failed — no network, GitHub unreachable, nothing published yet.</summary>
        CheckFailed,
    }

    /// <summary>
    /// Checks GitHub Releases for a newer build. A run outside a Velopack install — from Visual
    /// Studio, or the plain publish folder — has nowhere to apply an update to, so it is treated
    /// as "nothing to do" rather than an error.
    /// </summary>
    internal sealed class AppUpdater
    {
        private const string RepoUrl = "https://github.com/andro78/Claret";

        private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
        private UpdateInfo? _pending;

        /// <summary>Whether this process is running from an installed (Velopack-managed) copy.</summary>
        public bool IsInstalled => _manager.IsInstalled;

        /// <summary>The version already downloaded and waiting for a restart, once one exists.</summary>
        public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

        /// <summary>Raised once a version has finished downloading and is ready to apply.</summary>
        public event EventHandler? UpdateReady;

        public async Task<UpdateCheckResult> CheckAsync()
        {
            if (!IsInstalled)
            {
                return UpdateCheckResult.UpToDate;
            }

            if (_pending is not null)
            {
                return UpdateCheckResult.UpdateReady;
            }

            UpdateInfo? info;
            try
            {
                info = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                return UpdateCheckResult.CheckFailed;
            }

            if (info is null)
            {
                return UpdateCheckResult.UpToDate;
            }

            try
            {
                await _manager.DownloadUpdatesAsync(info).ConfigureAwait(true);
            }
            catch (Exception)
            {
                return UpdateCheckResult.CheckFailed;
            }

            _pending = info;
            UpdateReady?.Invoke(this, EventArgs.Empty);
            return UpdateCheckResult.UpdateReady;
        }

        /// <summary>Installs the already-downloaded update and restarts. Only meaningful after a check returned <see cref="UpdateCheckResult.UpdateReady"/>.</summary>
        public void ApplyAndRestart()
        {
            if (_pending is { } info)
            {
                _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
            }
        }
    }
}
