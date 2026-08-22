using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PowerTerm.Services
{
    /// <summary>One thing to transfer: a directory to create, or a file to copy.</summary>
    /// <param name="FullPath">Where it is now — a local path or a remote one.</param>
    /// <param name="Relative">Its path under the folder being transferred, in POSIX form.</param>
    internal sealed record ScanItem(string FullPath, string Relative, bool IsDirectory, long Length);

    /// <summary>
    /// What a folder holds, flattened. Directories come before anything inside them, so a caller can
    /// create each one just before it needs it.
    /// </summary>
    /// <param name="SkippedLinks">
    /// Symlinks and junctions left out. Following them can walk in a circle — a link pointing at its
    /// own parent is enough — and copying the link as a file would copy the wrong thing.
    /// </param>
    /// <param name="Truncated">The scan hit <see cref="FolderScan.MaxItems"/> and stopped.</param>
    internal sealed record ScanResult(
        IReadOnlyList<ScanItem> Items,
        int SkippedLinks,
        bool Truncated)
    {
        public int FileCount => Items.Count(item => !item.IsDirectory);

        public int DirectoryCount => Items.Count(item => item.IsDirectory);

        public long TotalBytes => Items.Where(item => !item.IsDirectory).Sum(item => item.Length);
    }

    /// <summary>
    /// Walks a local folder for a recursive upload. The remote side of this lives in
    /// <see cref="RemoteFileService"/>, but both stop at the same size and both refuse to follow
    /// links, so the two directions behave the same way.
    /// </summary>
    internal static class FolderScan
    {
        /// <summary>
        /// Most entries a single transfer will take on. A wrong click on a huge tree should stop
        /// with a message rather than fill memory and then start copying for an hour.
        /// </summary>
        public const int MaxItems = 20000;

        /// <summary>Reads a local folder, breadth first, without following links.</summary>
        public static ScanResult Local(string root)
        {
            var items = new List<ScanItem>();
            var pending = new Queue<(string Path, string Relative)>();
            int skipped = 0;

            pending.Enqueue((root, string.Empty));

            while (pending.Count > 0)
            {
                (string path, string relative) = pending.Dequeue();

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable subdirectory is not a reason to abandon the whole transfer.
                    continue;
                }

                foreach (string child in children)
                {
                    if (items.Count >= MaxItems)
                    {
                        return new ScanResult(items, skipped, Truncated: true);
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    string childRelative = relative.Length == 0 ? info.Name : relative + "/" + info.Name;

                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        skipped++;
                        continue;
                    }

                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        items.Add(new ScanItem(child, childRelative, IsDirectory: true, Length: 0));
                        pending.Enqueue((child, childRelative));
                        continue;
                    }

                    items.Add(new ScanItem(child, childRelative, IsDirectory: false, info.Length));
                }
            }

            return new ScanResult(items, skipped, Truncated: false);
        }

        /// <summary>
        /// Says how big a transfer is, for the line shown before it starts and the one after it
        /// finishes. Kept here so both directions word it the same way.
        /// </summary>
        public static string Describe(ScanResult scan)
        {
            string files = scan.FileCount == 1 ? "1 file" : $"{scan.FileCount} files";
            string folders = scan.DirectoryCount == 1 ? "1 folder" : $"{scan.DirectoryCount} folders";

            return $"{files} in {folders}, {Models.RemoteEntry.FormatSize(scan.TotalBytes)}";
        }
    }
}
