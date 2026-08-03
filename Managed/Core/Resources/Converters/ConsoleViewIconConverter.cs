using System;
using System.Globalization;
using ArisenEditor.Models;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LogLevel = ArisenEngine.Core.Diagnostics.Logger.LogLevel;

namespace ArisenEditor.Converters;


internal class ConsoleViewIconConverter : IValueConverter
{
    private static Bitmap? s_Info;
    private static Bitmap InfoIcon => s_Info ??= LoadIcon("info.png");

    private static Bitmap? s_Log;
    private static Bitmap LogIcon => s_Log ??= LoadIcon("log.png");

    private static Bitmap? s_Warning;
    private static Bitmap WarningIcon => s_Warning ??= LoadIcon("warning.png");

    private static Bitmap? s_Error;
    private static Bitmap ErrorIcon => s_Error ??= LoadIcon("error.png");
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MessageItemNode messageItemNode)
        {
            switch (messageItemNode.LogLevel)
            {
                case LogLevel.Error:
                    return ErrorIcon;
                case LogLevel.Log:
                    return LogIcon;
                case LogLevel.Info:
                    return InfoIcon;
                case LogLevel.Warning:
                    return WarningIcon;
            }
        }
        
        return null;
    }

    private static Bitmap LoadIcon(string fileName)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Com.Arisen.Editor/Assets/Icons/{fileName}"));
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
