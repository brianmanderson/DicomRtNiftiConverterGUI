using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dicom_RT_images_Csharp.ViewModels;
using Dicom_RT_images_Csharp.Views;

namespace Dicom_RT_images_Csharp
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new LauncherWindow
                {
                    DataContext = new LauncherViewModel()
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
