using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DicomRtNifti.Web.Jobs;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>Waiting on a job service from a test, whichever host it lives in.</summary>
    internal static class JobWait
    {
        /// <summary>Polls until the job has finished, or fails the test.</summary>
        public static async Task<JobRecord> FinishedAsync(JobQueue queue, string jobId, int seconds = 120)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var job = queue.Find(jobId);
                if (job != null && job.State.IsFinished())
                    return job;
                if (clock.Elapsed > TimeSpan.FromSeconds(seconds))
                    throw new TimeoutException($"Job {jobId} is {job?.State} after {seconds} s.");
                await Task.Delay(20);
            }
        }

        /// <summary>Submits and waits, failing the test on a refusal.</summary>
        public static async Task<JobRecord> RunAsync(JobQueue queue, string kind, object parameters, int seconds = 120)
        {
            var submission = queue.Submit(kind, parameters, "local:tester");
            if (!submission.Accepted)
                throw new InvalidOperationException("Refused: " + submission.Refusal);
            return await FinishedAsync(queue, submission.Job.Id, seconds);
        }

        /// <summary>The settings that give a web host the folders of <paramref name="folders"/>.</summary>
        public static Dictionary<string, string> Settings(JobFolders folders) => new Dictionary<string, string>
        {
            ["Web:DataRoots:0:Id"] = "archive",
            ["Web:DataRoots:0:Path"] = folders.Data,
            ["Web:OutputRoot"] = folders.Output,
            ["Web:KeyRoot"] = folders.Keys,
            ["Web:StateRoot"] = folders.State,
        };
    }
}
