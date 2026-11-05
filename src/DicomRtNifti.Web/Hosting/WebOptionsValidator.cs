using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DicomRtNifti.Web.Hosting;

/// <summary>
/// Refuses a configuration the guards could not honour, at startup (the options are registered
/// with <c>ValidateOnStart</c>). Messages name configuration keys and root ids, never paths, so
/// they are safe to print and log.
/// </summary>
public sealed partial class WebOptionsValidator : IValidateOptions<WebOptions>
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex RootIdPattern();

    public ValidateOptionsResult Validate(string? name, WebOptions options)
    {
        var failures = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < options.DataRoots.Count; i++)
        {
            var root = options.DataRoots[i];
            string key = $"{WebOptions.SectionName}:DataRoots:{i}";
            if (root is null)
            {
                failures.Add($"{key}: the entry is empty.");
                continue;
            }

            if (!RootIdPattern().IsMatch(root.Id ?? ""))
                failures.Add($"{key}:Id must be 1-32 lower-case letters, digits or hyphens, starting with a letter.");
            else if (DataRoots.ReservedIds.Contains(root.Id!))
                failures.Add($"{key}:Id '{root.Id}' is reserved for a server folder.");
            else if (!ids.Add(root.Id!))
                failures.Add($"{key}:Id '{root.Id}' is used by another data root.");

            if (!DataRootResolver.IsUsableRootPath(root.Path))
                failures.Add($"{key}:Path must be an absolute drive, UNC or POSIX path.");

            if (!Enum.IsDefined(root.Access))
                failures.Add($"{key}:Access must be Read or ReadWrite.");
        }

        CheckFolder(options.OutputRoot, nameof(WebOptions.OutputRoot), failures);
        CheckFolder(options.KeyRoot, nameof(WebOptions.KeyRoot), failures);
        CheckFolder(options.UploadRoot, nameof(WebOptions.UploadRoot), failures);
        CheckFolder(options.StateRoot, nameof(WebOptions.StateRoot), failures);

        // The key root and the state root hold what must never be listed or served (keys, job
        // logs, audit), so neither may overlap a folder the browser can name.
        var browsable = options.DataRoots
            .Where(r => r is not null && DataRootResolver.IsUsableRootPath(r.Path))
            .Select(r => (Label: $"data root '{r.Id}'", r.Path))
            .ToList();
        if (DataRootResolver.IsUsableRootPath(options.OutputRoot))
            browsable.Add(("OutputRoot", options.OutputRoot!));

        CheckNotBrowsable(options.KeyRoot, nameof(WebOptions.KeyRoot), browsable, failures);
        CheckNotBrowsable(options.StateRoot, nameof(WebOptions.StateRoot), browsable, failures);

        if (options.Limits is null)
            failures.Add($"{WebOptions.SectionName}:Limits is empty.");
        else
        {
            if (options.Limits.UploadBytes <= 0) failures.Add($"{WebOptions.SectionName}:Limits:UploadBytes must be positive.");
            if (options.Limits.DownloadZipBytes <= 0) failures.Add($"{WebOptions.SectionName}:Limits:DownloadZipBytes must be positive.");
            if (options.Limits.UploadRetentionHours <= 0) failures.Add($"{WebOptions.SectionName}:Limits:UploadRetentionHours must be positive.");
        }

        if (options.Lanes is null)
            failures.Add($"{WebOptions.SectionName}:Lanes is empty.");
        else
        {
            if (options.Lanes.MaxHeavyWorkers < 1) failures.Add($"{WebOptions.SectionName}:Lanes:MaxHeavyWorkers must be at least 1.");
            if (options.Lanes.MaxLightWorkers < 1) failures.Add($"{WebOptions.SectionName}:Lanes:MaxLightWorkers must be at least 1.");
            if (options.Lanes.QueueCapacity < 1) failures.Add($"{WebOptions.SectionName}:Lanes:QueueCapacity must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckFolder(string? path, string key, List<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(path) && !DataRootResolver.IsUsableRootPath(path))
            failures.Add($"{WebOptions.SectionName}:{key} must be an absolute drive, UNC or POSIX path.");
    }

    private static void CheckNotBrowsable(
        string? path, string key, List<(string Label, string Path)> browsable, List<string> failures)
    {
        if (!DataRootResolver.IsUsableRootPath(path))
            return;
        foreach (var (label, rootPath) in browsable)
        {
            if (DataRootResolver.Overlaps(path!, rootPath))
                failures.Add($"{WebOptions.SectionName}:{key} overlaps {label}; it must be outside every folder the browser can name.");
        }
    }
}
