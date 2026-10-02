using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DicomRtNifti.App.Tests.Support;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.App.Views;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// Both Help windows, opened the way a user opens them (the Help command of the workflow
    /// window the launcher opened), render and carry the research-use statement.
    /// </summary>
    public class HelpWindowTests
    {
        public const string ResearchUseSentence = "not a medical device";

        [AvaloniaFact]
        public void Forward_help_opens_from_the_forward_window_and_states_research_use()
        {
            using (new UserSettingsGuard())
            using (var launcher = new LauncherSession())
            {
                launcher.Open<DicomToNiftiWindow, MainViewModel>("DICOM → NIfTI", out var vm);
                var help = launcher.OpenedBy<DicomToNiftiHelpWindow>(() => vm.OpenHelpCommand.Execute(null), "the forward Help command");
                Assert.True(help.IsVisible);
                AssertStatesResearchUse(help);
                Screenshots.Capture(help, "04_help_dicom_to_nifti");
            }
        }

        [AvaloniaFact]
        public void Reverse_help_opens_from_the_reverse_window_and_states_research_use()
        {
            using (var launcher = new LauncherSession())
            {
                launcher.Open<NiftiToDicomWindow, NiftiToDicomViewModel>("NIfTI → DICOM", out var vm);
                var help = launcher.OpenedBy<NiftiToDicomHelpWindow>(() => vm.OpenHelpCommand.Execute(null), "the reverse Help command");
                Assert.True(help.IsVisible);
                AssertStatesResearchUse(help);
                Screenshots.Capture(help, "05_help_nifti_to_dicom");
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
