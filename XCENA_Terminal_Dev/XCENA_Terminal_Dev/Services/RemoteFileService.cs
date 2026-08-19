using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>
    /// Reads remote directories over SFTP. This is a second connection alongside the shell — SSH.NET
    /// cannot open an SFTP subsystem on the channel a <c>ShellStream</c> already owns — so it
    /// authenticates through <see cref="SshConnectionFactory"/> to keep host key pinning identical.
    /// </summary>
    internal sealed class RemoteFileService : IDisposable
    {
        private readonly object _gate = new();

        private SftpClient? _client;
        private bool _disposed;

        /// <summary>Directory the session lands in after login; the tree is rooted here.</summary>
        public string HomeDirectory { get; private set; } = "/";

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _client is { IsConnected: true };
                }
            }
        }

        public async Task ConnectAsync(ConnectionProfile profile, string? secret, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ConnectionInfo connectionInfo = SshConnectionFactory.Build(profile, secret);
            var client = new SftpClient(connectionInfo);
            HostKeyGate hostKey = SshConnectionFactory.GuardHostKey(client, profile);

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshConnectionException) when (hostKey.Failure is not null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(hostKey.Failure);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            if (hostKey.Failure is not null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(hostKey.Failure);
            }

            lock (_gate)
            {
                _client = client;
                HomeDirectory = string.IsNullOrEmpty(client.WorkingDirectory) ? "/" : client.WorkingDirectory;
            }
        }

        /// <summary>
        /// Contents of <paramref name="path"/>: folders first, then — only when
        /// <paramref name="includeFiles"/> is set — files, each group alphabetically. Folders alone
        /// is the default because the tree exists to pick a folder, and a home directory's worth of
        /// files buries the folders you are aiming for.
        /// </summary>
        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(
            string path,
            bool includeFiles,
            CancellationToken cancellationToken)
        {
            SftpClient client;
            lock (_gate)
            {
                client = _client ?? throw new InvalidOperationException("Not connected.");
            }

            var entries = new List<RemoteEntry>();

            await foreach (ISftpFile file in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                bool isDirectory = file.IsDirectory;

                // A symlink's own attributes describe the link, not its target, so a link to a
                // directory would otherwise be dropped as a file.
                if (!isDirectory && file.IsSymbolicLink)
                {
                    isDirectory = await ResolvesToDirectoryAsync(client, file.FullName, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!isDirectory && !includeFiles)
                {
                    continue;
                }

                entries.Add(new RemoteEntry
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = isDirectory,
                    IsSymbolicLink = file.IsSymbolicLink,
                    Length = isDirectory ? 0 : file.Length,
                });
            }

            return entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>True when something already occupies <paramref name="path"/>.</summary>
        public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
        {
            SftpClient client = Client();

            try
            {
                return await client.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
                // Some servers answer a stat on a missing parent with an error rather than "false".
                return false;
            }
        }

        /// <summary>
        /// Copies a local file to <paramref name="remotePath"/>. Progress is reported as bytes sent so
        /// the caller can show it against the local file length.
        /// </summary>
        public async Task UploadAsync(
            string localPath,
            string remotePath,
            bool overwrite,
            IProgress<ulong>? progress,
            CancellationToken cancellationToken)
        {
            SftpClient client = Client();

            await using var source = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            IProgress<UploadFileProgressReport>? report = progress is null
                ? null
                : new Progress<UploadFileProgressReport>(r => progress.Report(r.TotalBytesUploaded));

            await client
                .UploadFileAsync(source, remotePath, overwrite, report, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Copies a remote file to <paramref name="localPath"/>, reporting bytes received. The local
        /// file is written through a temporary name and moved into place at the end, so an aborted
        /// transfer cannot be mistaken for a complete download.
        /// </summary>
        public async Task DownloadAsync(
            string remotePath,
            string localPath,
            IProgress<ulong>? progress,
            CancellationToken cancellationToken)
        {
            SftpClient client = Client();
            string partial = localPath + ".part";

            IProgress<DownloadFileProgressReport>? report = progress is null
                ? null
                : new Progress<DownloadFileProgressReport>(r => progress.Report(r.TotalBytesDownloaded));

            try
            {
                await using (var destination = new FileStream(
                    partial,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    await client
                        .DownloadFileAsync(remotePath, destination, report, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(partial, localPath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception)
                {
                    // Nothing useful to do about a leftover .part file.
                }

                throw;
            }
        }

        private SftpClient Client()
        {
            lock (_gate)
            {
                return _client ?? throw new InvalidOperationException("Not connected.");
            }
        }

        private static async Task<bool> ResolvesToDirectoryAsync(
            SftpClient client,
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                SftpFileAttributes attributes = await client
                    .GetAttributesAsync(path, cancellationToken)
                    .ConfigureAwait(false);

                return attributes.IsDirectory;
            }
            catch (Exception ex) when (ex is SshException or SftpPathNotFoundException)
            {
                // Dangling link, or no permission to follow it: treat it as a plain entry.
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            SftpClient? client;
            lock (_gate)
            {
                client = _client;
                _client = null;
            }

            if (client is null)
            {
                return;
            }

            // Same reason as the shell: SFTP teardown blocks on the socket, never on the UI thread.
            SftpTeardown.DisposeInBackground(client);
        }
    }

    /// <summary>Disposes SFTP clients off the UI thread, mirroring <see cref="SshTeardown"/>.</summary>
    internal static class SftpTeardown
    {
        public static void DisposeInBackground(SftpClient client) => Task.Run(() =>
        {
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
                // Detached already; a failed teardown has nowhere to be reported.
            }
        });
    }
}
