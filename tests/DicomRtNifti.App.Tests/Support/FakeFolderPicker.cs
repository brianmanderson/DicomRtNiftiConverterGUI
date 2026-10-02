using System.Threading.Tasks;
using DicomRtNifti.App.Services;

namespace DicomRtNifti.App.Tests.Support
{
    /// <summary>
    /// Stands in for the OS folder dialog, which cannot open headless. Returns a scripted path
    /// (null means "cancelled") and counts the prompts so a test can assert on them.
    /// </summary>
    public sealed class FakeFolderPicker : IFolderPicker
    {
        public string NextResult { get; set; }
        public int Calls { get; private set; }
        public string LastTitle { get; private set; }

        public Task<string> PickFolderAsync(string title, string suggestedPath)
        {
            Calls++;
            LastTitle = title;
            return Task.FromResult(NextResult);
        }
    }
}
