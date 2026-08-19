namespace XCENA_Terminal_Dev.Models
{
    /// <summary>
    /// One entry of a remote directory listing: a folder, a file when the tree is set to show them,
    /// or the placeholder row that reports a folder which could not be read.
    /// </summary>
    public sealed class RemoteEntry
    {
        public required string Name { get; init; }

        public required string FullPath { get; init; }

        public bool IsDirectory { get; init; }

        public bool IsSymbolicLink { get; init; }

        /// <summary>Size in bytes for files, 0 for folders. Drives the download progress bar.</summary>
        public long Length { get; init; }

        /// <summary>
        /// Set on the placeholder row that reports why a folder could not be listed. Such a row is
        /// neither a folder nor a file, so no command in the tree may act on it.
        /// </summary>
        public bool IsError { get; init; }

        // Segoe Fluent Icons code points, written as casts so a mangled \u escape cannot turn an
        // icon into a stray CJK character.
        private static readonly string LinkGlyph = ((char)0xE71B).ToString();
        private static readonly string FolderGlyph = ((char)0xE8B7).ToString();
        private static readonly string FileGlyph = ((char)0xE8A5).ToString();
        private static readonly string ErrorGlyph = ((char)0xE7BA).ToString();

        /// <summary>
        /// Symlinks win over folders: the expand chevron already shows that it opens, so the link
        /// marker is the more useful thing to draw.
        /// </summary>
        public string Glyph => IsError
            ? ErrorGlyph
            : IsSymbolicLink ? LinkGlyph : IsDirectory ? FolderGlyph : FileGlyph;

        /// <summary>Full path (the pane is narrow enough to trim names), plus the size for files.</summary>
        public string Tooltip => IsDirectory || IsError ? FullPath : $"{FullPath}\n{SizeText}";

        /// <summary>Human-readable size. Shown in the tooltip and while downloading, never in the row.</summary>
        public string SizeText => FormatSize(Length);

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }
    }
}
