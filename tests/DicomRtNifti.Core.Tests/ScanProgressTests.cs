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
    /// The scan's structured progress (W4-C): an event with every "Scanned ..." log line, then
    /// always one more after the last read, which is the final count. The log is unchanged.
    /// </summary>
    public class ScanProgressTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Theory]
        [InlineData(100)] // a multiple of 50: the log's last line is the 100th read, with no extra total line
        [InlineData(120)] // not a multiple: the log adds "Scanned 120/120"
        public async Task EveryLogLineHasAnEvent_AndTheLastEventIsTheFinalCount(int files)
        {
            WriteSlices(files);
            var log = new CollectingProgress();
            var events = new CollectingScanEvents();

            var scan = await new DicomScannerService().ScanFolderAsync(_dir, log, CancellationToken.None, events);

            Assert.Equal(files, scan.TotalFileCount);
            var items = events.Items;
            var last = items.Last();
            Assert.Equal(files, last.FilesProcessed);
            Assert.Equal(files, last.FilesFound);
            Assert.True(last.WalkFinished);

            // One event per 50 reads, as the log has a line, then the final one.
            Assert.Equal(files / 50 + 1, items.Count);
            Assert.Equal(log.Messages.Count(m => m.StartsWith("Scanned ", StringComparison.Ordinal)),
                files / 50 + (files % 50 == 0 ? 0 : 1));

            foreach (var e in items)
            {
                Assert.True(e.FilesFound >= e.FilesProcessed, e.ToString());
                if (e.WalkFinished)
                    Assert.Equal(files, e.FilesFound);
            }
            Assert.Equal(Enumerable.Range(1, files / 50).Select(i => i * 50),
                items.Take(files / 50).Select(e => e.FilesProcessed).OrderBy(n => n));
        }

        [Fact]
        public async Task AnEmptyFolder_StillGetsItsFinalEvent()
        {
            var events = new CollectingScanEvents();

            await new DicomScannerService().ScanFolderAsync(_dir, null, CancellationToken.None, events);

            var only = Assert.Single(events.Items);
            Assert.Equal("0/0", only.ToString());
            Assert.True(only.WalkFinished);
        }

        [Fact]
        public async Task TheLog_IsTheSameWithOrWithoutEvents()
        {
            WriteSlices(30);
            var withEvents = new CollectingProgress();
            var withoutEvents = new CollectingProgress();

            await new DicomScannerService().ScanFolderAsync(_dir, withEvents, CancellationToken.None, new CollectingScanEvents());
            await new DicomScannerService().ScanFolderAsync(_dir, withoutEvents, CancellationToken.None);

            Assert.Equal(withoutEvents.Messages, withEvents.Messages);
            Assert.Contains("Scanned 30/30 files...", withEvents.Messages);
        }

        private void WriteSlices(int count)
        {
            string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
            for (int i = 0; i < count; i++)
                DicomTestData.WriteImageSlice(_dir, $"s{i:000}.dcm", "CT", "PAT", study, series, frame, i * 2.0);
        }

        private sealed class CollectingScanEvents : IProgress<ScanProgress>
        {
            private readonly List<ScanProgress> _items = new List<ScanProgress>();

            public void Report(ScanProgress value)
            {
                lock (_items) _items.Add(value);
            }

            public List<ScanProgress> Items
            {
                get { lock (_items) return new List<ScanProgress>(_items); }
            }
        }
    }
}
