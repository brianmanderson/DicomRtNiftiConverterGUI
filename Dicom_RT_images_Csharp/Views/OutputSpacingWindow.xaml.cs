using System.Globalization;
using System.Windows;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Interaction logic for OutputSpacingWindow.xaml
    /// </summary>
    public partial class OutputSpacingWindow : Window
    {
        /// <summary>
        /// Output voxel spacing in mm along the X axis (set when Save succeeds).
        /// </summary>
        public double SpacingX { get; private set; }

        /// <summary>
        /// Output voxel spacing in mm along the Y axis (set when Save succeeds).
        /// </summary>
        public double SpacingY { get; private set; }

        /// <summary>
        /// Output voxel spacing in mm along the Z axis (set when Save succeeds).
        /// </summary>
        public double SpacingZ { get; private set; }

        /// <summary>
        /// Initializes the window with current spacing values.
        /// </summary>
        public OutputSpacingWindow(double initialX, double initialY, double initialZ)
        {
            InitializeComponent();
            SpacingX = initialX;
            SpacingY = initialY;
            SpacingZ = initialZ;

            XTextBox.Text = initialX.ToString(CultureInfo.InvariantCulture);
            YTextBox.Text = initialY.ToString(CultureInfo.InvariantCulture);
            ZTextBox.Text = initialZ.ToString(CultureInfo.InvariantCulture);

            XTextBox.Focus();
            XTextBox.SelectAll();
        }

        private bool TryParsePositive(string text, string fieldName, out double value)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                MessageBox.Show($"{fieldName} must be a number.", "Invalid input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                MessageBox.Show($"{fieldName} must be greater than 0.", "Invalid input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParsePositive(XTextBox.Text, "X", out double x)) return;
            if (!TryParsePositive(YTextBox.Text, "Y", out double y)) return;
            if (!TryParsePositive(ZTextBox.Text, "Z", out double z)) return;

            SpacingX = x;
            SpacingY = y;
            SpacingZ = z;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
