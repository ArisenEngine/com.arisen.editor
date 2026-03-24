using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ArisenEditorFramework.Assets;

public class FolderOrFileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isDirectory = (bool)(value ?? false);
        return isDirectory ? "📁" : "📄"; 
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
