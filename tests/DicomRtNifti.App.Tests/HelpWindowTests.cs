using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>Both Help windows open, render, and carry the research-use statement.</summary>
    public class HelpWindowTests
    {
        public const string ResearchUseSentence = "not a medical device";

        [AvaloniaFact]
        public void Forward_help_window_opens_and_states_research_use()
        {
            var help = new DicomToNiftiHelpWindow();
            try
            {
                help.Show();
                Dispatcher.UIThread.RunJobs();
                AssertStatesResearchUse(help);
                Screenshots.Capture(help, "04_help_dicom_to_nifti");
            }
            finally
            {
                help.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void Reverse_help_window_opens_and_states_research_use()
        {
            var help = new NiftiToDicomHelpWindow();
            try
            {
                help.Show();
                Dispatcher.UIThread.RunJobs();
                AssertStatesResearchUse(help);
                Screenshots.Capture(help, "05_help_nifti_to_dicom");
            }
            finally
            {
                help.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static void AssertStatesResearchUse(Window help)
        {
            var texts = help.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? t.Inlines?.Text ?? "")
                .ToList();
            Assert.Contains(texts, t => t.Contains(ResearchUseSentence));
        }
    }
}
