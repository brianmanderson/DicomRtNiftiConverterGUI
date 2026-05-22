using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Dicom_RT_images_Csharp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _autoScrollLog = true;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_autoScrollLog)
            {
                LogTextBox.ScrollToEnd();
            }
        }

        private void LogTextBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Ignore scroll events caused by appended content; only react to user-driven scrolls.
            if (e.ExtentHeightChange != 0) return;

            _autoScrollLog = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1.0;
        }
    }
}
