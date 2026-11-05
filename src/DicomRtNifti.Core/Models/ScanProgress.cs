namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// One structured event from <see cref="Services.DicomScannerService.ScanFolderAsync"/>, beside
    /// its string log, which is unchanged: how many files have been read, how many the directory
    /// walk has found, and whether it has finished, so a progress bar can show "read / found so
    /// far" while the walk runs and "read / total" once it is done.
    ///
    /// Sent with each <c>Scanned ...</c> log line (every 50 files read) and once more when every
    /// file has been read. Reads finish on many threads, so two events can arrive out of order; the
    /// last one is always the final count, sent after every read. Take <see cref="FilesProcessed"/>
    /// as a high-water mark.
    /// </summary>
    public sealed class ScanProgress
    {
        public ScanProgress(int filesProcessed, int filesFound, bool walkFinished)
        {
            FilesProcessed = filesProcessed;
            FilesFound = filesFound;
            WalkFinished = walkFinished;
        }

        /// <summary>Files whose header has been read (or whose read failed) so far.</summary>
        public int FilesProcessed { get; }

        /// <summary>Files the walk has found so far; the total once <see cref="WalkFinished"/>.</summary>
        public int FilesFound { get; }

        /// <summary>True once the directory walk has listed every file, so <see cref="FilesFound"/> is the total.</summary>
        public bool WalkFinished { get; }

        public override string ToString() =>
            $"{FilesProcessed}/{FilesFound}{(WalkFinished ? "" : "+")}";
    }
}
