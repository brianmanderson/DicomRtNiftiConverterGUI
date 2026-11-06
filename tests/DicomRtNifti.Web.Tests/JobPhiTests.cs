using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;
using DicomRtNifti.Core.Tests;
using DicomRtNifti.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DicomRtNifti.Web.Tests
{
    /// <summary>
    /// No patient identifier and no server path reaches the application log (plan sections 6 and
    /// 7.1), modelled on Core's AnonymizedExportPhiAuditTests: synthesized patients with
    /// distinctive MRNs and known UIDs, one with a damaged structure set so a series fails with
    /// Core's message, run through a scan, a conversion and an anonymized conversion, then the
    /// Jobs and Audit pages are served. The job log does carry the identifiers (the scan is not
    /// vacuous), the application log carries none, and what the browser and the audit log get has
    /// no server path. The same holds of the raw log, before the redacting logger factory, so the
    /// job service never relies on it.
    /// </summary>
    public sealed class JobPhiTests : IDisposable
    {
        private const string PatientName = "Test^Patient"; // what DicomTestData writes

        private readonly JobFolders _folders = JobFolders.Create();
        private readonly List<string> _identifiers = new List<string> { PatientName };

        public JobPhiTests()
        {
            string cohort = Path.Combine(_folders.Data, "cohort");
            foreach (string mrn in new[] { "MRN-PHI-7101", "MRN-PHI-7102" })
            {
                string dir = Path.Combine(cohort, mrn);
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                string structureSet = DicomTestData.NewUid();
                DicomTestData.WriteCtSeriesWithPixels(dir, series, frame, sliceCount: 4, size: 16, studyUid: study, patientId: mrn);
                DicomTestData.WriteRtStructWithContours(dir, "rtstruct.dcm", study, structureSet, frame,
                    new[] { new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)) },
                    patientId: mrn);
                _identifiers.AddRange(new[] { mrn, study, series, frame, structureSet });
            }

            // Scans, and fails to convert with Core's message.
            DicomTestData.WritePatientWithStructureSet(cohort, "MRN-PHI-7103", damagedContours: true);
            _identifiers.Add("MRN-PHI-7103");
        }

        public void Dispose() => _folders.Dispose();

        private IEnumerable<string> Paths => new[] { _folders.Root, _folders.Root.Replace('\\', '/') };

        [Fact]
        public async Task Nothing_a_job_does_puts_an_identifier_or_a_path_in_the_application_log()
        {
            var capture = new CapturingLoggerProvider();
            using var factory = new WebAppFactory(JobWait.Settings(_folders), capture);
            var queue = factory.Services.GetRequiredService<JobQueue>();

            var scan = await JobWait.RunAsync(queue, ScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"));
            var convert = await JobWait.RunAsync(queue, CohortConvertJobHandler.KindName, new CohortJobParameters("archive", "cohort", "plain"));
            var anonymized = await JobWait.RunAsync(queue, CohortConvertJobHandler.KindName,
                new CohortJobParameters("archive", "cohort", "anon", new[] { "--anonymize" }));

            Assert.Equal(JobState.Succeeded, scan.State);
            Assert.True(convert.State == JobState.SucceededWithFailures, convert.State + ": " + convert.Reason);
            Assert.Equal("1 of 3 series failed.", convert.Reason);
            Assert.Equal(JobState.SucceededWithFailures, anonymized.State);

            using var client = factory.CreateClient();
            string jobsPage = await client.GetStringAsync("/jobs");
            string auditPage = await client.GetStringAsync("/audit");

            // Not vacuous: the jobs did see the identifiers, and the log provider did see the jobs.
            string jobLog = File.ReadAllText(Path.Combine(_folders.State, "jobs", convert.Id, JobStore.LogFile));
            Assert.Contains("MRN-PHI-7101", jobLog);
            Assert.Contains("  failed: ", jobLog);
            string application = string.Join("\n", capture.Logs.Select(l => l.Message + (l.Exception?.ToString() ?? "")));
            foreach (var job in new[] { scan, convert, anonymized })
                Assert.Contains(job.Id, application);

            foreach (string identifier in _identifiers.Concat(Paths))
                Assert.DoesNotContain(identifier, application, StringComparison.OrdinalIgnoreCase);

            // The browser and the audit log get root tokens, never a server path.
            string audit = string.Join("\n", Directory.GetFiles(Path.Combine(_folders.State, "audit")).Select(JobTestHost.ReadShared));
            Assert.Contains("output:plain", audit);
            foreach (string path in Paths)
            {
                Assert.DoesNotContain(path, jobsPage, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(path, auditPage, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(path, audit, StringComparison.OrdinalIgnoreCase);
            }

            // An anonymized job's document carries hashes only.
            string anonymizedResult = queue.ReadResult(anonymized.Id);
            foreach (string identifier in _identifiers.Concat(Paths))
                Assert.DoesNotContain(identifier, anonymizedResult, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_job_service_logs_no_identifier_even_before_redaction()
        {
            await using var host = await JobTestHost.StartAsync(_folders);

            var scan = await JobWait.RunAsync(host.Queue, ScanJobHandler.KindName, new ScanJobParameters("archive", "cohort"));
            var failing = await JobWait.RunAsync(host.Queue, TestJobHandlers.Heavy, new GatedParameters("auto-phi", Outcome: "throw",
                Message: $"cannot read {Path.Combine(_folders.Data, "cohort", "MRN-PHI-7101")} for MRN-PHI-7101"));

            Assert.Equal(JobState.Succeeded, scan.State);
            Assert.Equal(JobState.Failed, failing.State);
            Assert.Contains("MRN-PHI-7101", host.LogFile(failing.Id));

            string raw = string.Join("\n", host.Logs.Logs.Select(l => l.Message + (l.Exception?.ToString() ?? "")));
            Assert.Contains(failing.Id, raw);
            foreach (string identifier in _identifiers.Concat(Paths))
                Assert.DoesNotContain(identifier, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
