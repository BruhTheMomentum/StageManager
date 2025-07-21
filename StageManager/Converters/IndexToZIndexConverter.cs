namespace StageManager.Converters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;

    /// <summary>
    /// Converts an <see cref="int"/> index to a negative ZIndex so that items with higher indices are rendered behind.
    /// </summary>
    public sealed class IndexToZIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index)
            {
                // Frontmost item (index 0) gets 0, next gets -1, etc.
                return -index;
            }

            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
} 