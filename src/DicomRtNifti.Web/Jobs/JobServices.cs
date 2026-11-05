using DicomRtNifti.Web.Audit;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DicomRtNifti.Web.Jobs;

/// <summary>
/// Registers the job service: the store, the locks, the queue, the events, the built-in job
/// kinds, and the hosted services that recover the last run and work the two lanes. The Jobs
/// feature calls this from its <c>ConfigureServices</c>; a feature that adds a job kind registers
/// one more <see cref="IJobHandler"/> from its own (<see cref="AddHandler{T}"/>).
/// </summary>
public static class JobServices
{
    public static void Add(IServiceCollection services)
    {
        services.AddOptions();
        services.AddLogging();
        AuditServices.Add(services);

        if (services.Any(d => d.ServiceType == typeof(JobQueue)))
            return; // the hosted services below must be registered once

        services.TryAddSingleton<JobEvents>();
        services.TryAddSingleton<JobStore>();
        services.TryAddSingleton<ProjectLocks>();
        services.TryAddSingleton<RootPathPresenter>();
        services.TryAddSingleton<JobQueue>();
        AddHandler<ScanJobHandler>(services);
        AddHandler<CohortConvertJobHandler>(services);
        AddHandler<CohortManifestJobHandler>(services);

        // Recovery first: hosted services start in this order and stop in the reverse one.
        services.AddHostedService<JobRecovery>();
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<JobWorker>(sp, JobLane.Light));
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<JobWorker>(sp, JobLane.Heavy));
    }

    /// <summary>Adds a job kind.</summary>
    public static void AddHandler<T>(IServiceCollection services)
        where T : class, IJobHandler =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, T>());
}
