using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The structured progress a front-end draws its bar from (W4-C): one SeriesStarted and one
    /// SeriesFinished per series, then Completed, in plan order with running counts, whether series
    /// run one at a time or two at once. The string log is the same with or without it.
    ///
    /// The cohort is built so the order is worth checking: PAT_A, first in the plan, is a 96x96x24
    /// CT with a good structure set; PAT_B is a 16x16x4 CT whose structure set is damaged, so it
    /// fails, and run beside PAT_A it is finished long before PAT_A is.
    /// </summary>
    public class CohortProgressTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();
        private readonly string _input;

        public CohortProgressTests()
        {
            _input = Path.Combine(_dir, "cohort");
            string study = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
            string dirA = Path.Combine(_input, "PAT_A");
            DicomTestData.WriteCtSeriesWithPixels(dirA, DicomTestData.NewUid(), frame,
                sliceCount: 24, size: 96, studyUid: study, patientId: "PAT_A");
            DicomTestData.WriteRtStructWithContours(dirA, "rtstruct.dcm", study, DicomTestData.NewUid(), frame,
                new[] { new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(10, 60, 5)) },
                patientId: "PAT_A");
            DicomTestData.WritePatientWithStructureSet(_input, "PAT_B", damagedContours: true);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task TwoSeriesAtOnce_SendTheirEventsInPlanOrder_WithTheRightCounts()
        {
            var (plan, options) = Plan("parallel");
            var service = Service(parallelism: 2);
            Assert.True(service.RunsSeriesAtOnce(plan, options, writeFiles: true));

            var events = new CollectingEvents();
            var result = await service.ExecuteAsync(plan, options, null, null, CancellationToken.None, events);

            string a = Label(plan.Series[0]), b = Label(plan.Series[1]);
            Assert.StartsWith("PAT_A/", a);
            Assert.StartsWith("PAT_B/", b);
            Assert.Equal(new[]
            {
                $"SeriesStarted 1/2 {a} None (0 ok, 0 failed)",
                $"SeriesFinished 1/2 {a} Succeeded (1 ok, 0 failed)",
                $"SeriesStarted 2/2 {b} None (1 ok, 0 failed)",
                $"SeriesFinished 2/2 {b} Failed (1 ok, 1 failed)",
                "Completed 2/2  None (1 ok, 1 failed)",
            }, events.Items.Select(e => e.ToString()));

            // The events agree with the result they describe.
            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(Assert.Single(result.Errors).Message, events.Items[3].Error);
            Assert.All(events.Items.Where(e => e.Outcome != CohortSeriesOutcome.Failed), e => Assert.Null(e.Error));
        }

        [Fact]
        public async Task OneAtATime_SendsTheSameEvents()
        {
            var (parallelPlan, parallelOptions) = Plan("parallel");
            var parallel = new CollectingEvents();
            await Service(2).ExecuteAsync(parallelPlan, parallelOptions, null, null, CancellationToken.None, parallel);

            var (plan, options) = Plan("sequential");
            var sequential = new CollectingEvents();
            await Service(1).ExecuteAsync(plan, options, null, null, CancellationToken.None, sequential);

            Assert.Equal(5, sequential.Items.Count);
            Assert.Equal(parallel.Items.Select(e => e.ToString()), sequential.Items.Select(e => e.ToString()));
        }

        /// <summary>
        /// The string channel is the one the CLI prints: asking for events must not change a line
        /// of it, in either schedule.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task TheLog_IsTheSameWithOrWithoutEvents(int parallelism)
        {
            var (planWith, optionsWith) = Plan("with");
            var withEvents = new CollectingProgress();
            await Service(parallelism).ExecuteAsync(planWith, optionsWith, null, withEvents, CancellationToken.None, new CollectingEvents());

            var (planWithout, optionsWithout) = Plan("without");
            var withoutEvents = new CollectingProgress();
            await Service(parallelism).ExecuteAsync(planWithout, optionsWithout, null, withoutEvents, CancellationToken.None);

            Assert.Equal(Masked(withoutEvents, optionsWithout), Masked(withEvents, optionsWith));
            Assert.Contains(withEvents.Messages, m => m.StartsWith("[1/2] PAT_A/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task TheSurvey_SendsTheSameShapeOfEvents()
        {
            var (plan, options) = Plan("survey");
            var events = new CollectingEvents();
            await Service(2).ComputeManifestAsync(plan, options, null, null, CancellationToken.None, events);

            Assert.Equal(new[]
            {
                "SeriesStarted 1 None 0/0", "SeriesFinished 1 Succeeded 1/0",
                "SeriesStarted 2 None 1/0", "SeriesFinished 2 Failed 1/1",
                "Completed 2 None 1/1",
            }, events.Items.Select(e => $"{e.Phase} {e.SeriesIndex} {e.Outcome} {e.SucceededCount}/{e.FailedCount}"));
        }

        /// <summary>
        /// Under --fail-fast the run throws on the failing series: its SeriesFinished is sent first,
        /// so a front-end shows which series stopped the run, and no Completed follows.
        /// </summary>
        [Fact]
        public async Task FailFast_SendsTheFailure_ThenNoCompleted()
        {
            var (plan, options) = Plan("failfast");
            options.FailFast = true;
            var events = new CollectingEvents();

            await Assert.ThrowsAnyAsync<Exception>(() =>
                Service(2).ExecuteAsync(plan, options, null, null, CancellationToken.None, events));

            var last = events.Items.Last();
            Assert.Equal(CohortProgressPhase.SeriesFinished, last.Phase);
            Assert.Equal(CohortSeriesOutcome.Failed, last.Outcome);
            Assert.Equal(2, last.SeriesIndex);
            Assert.DoesNotContain(events.Items, e => e.Phase == CohortProgressPhase.Completed);
        }

        /// <summary>
        /// A consumer that throws from Report is a bug in the consumer, not a failure of the series
        /// it was told about: the run ends with its exception, and the series that had succeeded is
        /// not then recorded as failed.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task AConsumerThatThrows_EndsTheRun_WithoutFailingTheSeries(int parallelism)
        {
            var (plan, options) = Plan("throwing");
            var events = new ThrowsOnFirstFinished();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(parallelism).ExecuteAsync(plan, options, null, null, CancellationToken.None, events));

            Assert.Equal("consumer", ex.Message);
            Assert.DoesNotContain(events.Received, e => e.Outcome == CohortSeriesOutcome.Failed);
        }

        private (CohortExportPlan, CohortExportOptions) Plan(string output)
        {
            var scan = new DicomScannerService()
                .ScanFolderAsync(_input, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var options = new CohortExportOptions { InputRoot = _input, OutputRoot = Path.Combine(_dir, output) };
            var plan = CohortExportService.BuildPlan(scan, options, null);
            Assert.Equal(2, plan.Series.Count);
            return (plan, options);
        }

        private static CohortExportService Service(int parallelism) =>
            new CohortExportService(new NiftiConversionService(new RtStructMaskService()))
            {
                SeriesParallelism = parallelism,
            };

        private static string Label(PlannedSeries planned) =>
            planned.ExportPatientId + "/" + planned.ExportSeriesUid.Substring(0, 8);

        private static List<string> Masked(CollectingProgress log, CohortExportOptions options)
        {
            string root = Path.GetFullPath(options.OutputRoot);
            return log.Messages.Select(m => m.Replace(root, "<ROOT>")).ToList();
        }

        private sealed class ThrowsOnFirstFinished : IProgress<CohortProgress>
        {
            private bool _thrown;

            public List<CohortProgress> Received { get; } = new List<CohortProgress>();

            public void Report(CohortProgress value)
            {
                if (value.Phase == CohortProgressPhase.SeriesFinished && !_thrown)
                {
                    _thrown = true;
                    throw new InvalidOperationException("consumer");
                }
                Received.Add(value);
            }
        }

        /// <summary>A synchronous collector: every event in the order it was sent.</summary>
        private sealed class CollectingEvents : IProgress<CohortProgress>
        {
            private readonly List<CohortProgress> _items = new List<CohortProgress>();

            public void Report(CohortProgress value)
            {
                lock (_items) _items.Add(value);
            }

            public List<CohortProgress> Items
            {
                get { lock (_items) return new List<CohortProgress>(_items); }
            }
        }
    }
}
