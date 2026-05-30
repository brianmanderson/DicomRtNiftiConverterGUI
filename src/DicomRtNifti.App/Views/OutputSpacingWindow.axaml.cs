using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Output voxel-spacing dialog. Shown via <c>ShowDialog&lt;bool&gt;</c>; on Save it closes
    /// with <c>true</c> and exposes the parsed spacing. Validation errors are shown inline
    /// (the WPF version used MessageBox popups).
    /// </summary>
    public partial class OutputSpacingWindow : Window
    {
        public double SpacingX { get; private set; }
        public double SpacingY { get; private set; }
        public double SpacingZ { get; private set; }

        // Parameterless ctor for the Avalonia designer/XAML loader.
        public OutputSpacingWindow() : this(1.0, 1.0, 1.0) { }

        public OutputSpacingWindow(double initialX, double initialY, double initialZ)
        {
            InitializeComponent();
            SpacingX = initialX;
            SpacingY = initialY;
            SpacingZ = initialZ;

            XTextBox.Text = initialX.ToString(CultureInfo.InvariantCulture);
            YTextBox.Text = initialY.ToString(CultureInfo.InvariantCulture);
            ZTextBox.Text = initialZ.ToString(CultureInfo.InvariantCulture);

            Opened += (_, _) =>
            {
                XTextBox.Focus();
                XTextBox.SelectAll();
            };
        }

        private bool TryParsePositive(string text, string fieldName, out double value)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                ShowError($"{fieldName} must be a number.");
                return false;
            }
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                ShowError($"{fieldName} must be greater than 0.");
                return false;
            }
            return true;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.IsVisible = true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParsePositive(XTextBox.Text ?? "", "X", out double x)) return;
            if (!TryParsePositive(YTextBox.Text ?? "", "Y", out double y)) return;
            if (!TryParsePositive(ZTextBox.Text ?? "", "Z", out double z)) return;

            SpacingX = x;
            SpacingY = y;
            SpacingZ = z;
            Close(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}
