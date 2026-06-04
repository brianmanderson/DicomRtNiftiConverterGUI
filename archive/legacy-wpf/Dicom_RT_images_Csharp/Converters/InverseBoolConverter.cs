using System;
using System.Globalization;
using System.Windows.Data;

namespace Dicom_RT_images_Csharp.Converters
{
    /// <summary>
    /// Inverts a boolean value.
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        /// <inheritdoc/>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return !b;
            }
            return value;
        }

        /// <inheritdoc/>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return !b;
            }
            return value;
        }
    }
}
