namespace DicomRtNifti.Core.Models
{
    /// <summary>Where a cohort run is, as a <see cref="CohortProgress"/> reports it.</summary>
    public enum CohortProgressPhase
    {
        /// <summary>
        /// A series' turn has come: sent with the log's <c>[i/n] label</c> line. One at a time, that
        /// is when the series starts; several at once, it is when its held log is replayed, just
        /// before its <see cref="SeriesFinished"/>.
        /// </summary>
        SeriesStarted,

        /// <summary>A series has been recorded: converted or surveyed, or failed.</summary>
        SeriesFinished,

        /// <summary>
        /// Every series is recorded and the manifest is written; the counts are final. Not sent
        /// when the run throws (cancellation, or a failure under --fail-fast).
        /// </summary>
        Completed,
    }

    /// <summary>How a series came out, as of a <see cref="CohortProgress"/>.</summary>
    public enum CohortSeriesOutcome
    {
        /// <summary>Not decided: the event is not a <see cref="CohortProgressPhase.SeriesFinished"/>.</summary>
        None,
        Succeeded,
        Failed,
    }

    /// <summary>
    /// One structured event from <see cref="Services.CohortExportService.ExecuteAsync"/> or
    /// <see cref="Services.CohortExportService.ComputeManifestAsync"/>, beside the string log, which is
    /// unchanged. Events come in plan order whether series run one at a time or several at once,
    /// because they are sent where the run records a series (and replays its log), never from the
    /// workers: per series a <see cref="CohortProgressPhase.SeriesStarted"/> then a
    /// <see cref="CohortProgressPhase.SeriesFinished"/>, then one
    /// <see cref="CohortProgressPhase.Completed"/>.
    ///
    /// They are sent synchronously, on whatever thread the run is on. A
    /// <see cref="System.Progress{T}"/> with no synchronization context hands each one to the thread
    /// pool, where they can arrive out of order; a consumer that cares about order (a progress bar,
    /// a job log) should take them on its own synchronous <see cref="System.IProgress{T}"/>.
    /// </summary>
    public sealed class CohortProgress
    {
        public CohortProgress(
            CohortProgressPhase phase,
            int seriesIndex,
            int seriesCount,
            string seriesLabel,
            CohortSeriesOutcome outcome,
            int succeededCount,
            int failedCount,
            string error)
        {
            Phase = phase;
            SeriesIndex = seriesIndex;
            SeriesCount = seriesCount;
            SeriesLabel = seriesLabel;
            Outcome = outcome;
            SucceededCount = succeededCount;
            FailedCount = failedCount;
            Error = error;
        }

        public CohortProgressPhase Phase { get; }

        /// <summary>The series' 1-based position in the plan; <see cref="SeriesCount"/> on <see cref="CohortProgressPhase.Completed"/>.</summary>
        public int SeriesIndex { get; }

        /// <summary>How many series the plan holds.</summary>
        public int SeriesCount { get; }

        /// <summary>
        /// The label the log prints for the series: its export PatientID and the first eight
        /// characters of its export SeriesInstanceUID, so hashes when anonymizing. Null on
        /// <see cref="CohortProgressPhase.Completed"/>.
        /// </summary>
        public string SeriesLabel { get; }

        /// <summary>Set on <see cref="CohortProgressPhase.SeriesFinished"/>; <see cref="CohortSeriesOutcome.None"/> otherwise.</summary>
        public CohortSeriesOutcome Outcome { get; }

        /// <summary>Series recorded as succeeded so far, this one included.</summary>
        public int SucceededCount { get; }

        /// <summary>Series recorded as failed so far, this one included.</summary>
        public int FailedCount { get; }

        /// <summary>The failure's message on a failed series; null otherwise.</summary>
        public string Error { get; }

        public override string ToString() =>
            $"{Phase} {SeriesIndex}/{SeriesCount} {SeriesLabel} {Outcome} ({SucceededCount} ok, {FailedCount} failed)";
    }
}
