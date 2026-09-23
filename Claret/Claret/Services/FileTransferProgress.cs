namespace Claret.Services
{
    /// <summary>A snapshot of how far a send has gotten, for a progress bar to read.</summary>
    public readonly record struct FileTransferProgress(long BytesSent, long TotalBytes, int RetryCount)
    {
        public double Fraction => TotalBytes <= 0 ? 0 : (double)BytesSent / TotalBytes;
    }
}
