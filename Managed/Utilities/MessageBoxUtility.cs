using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace ArisenEditorFramework.Utilities;

public static class MessageBoxUtility
{
    public static async Task<ButtonResult> ShowMessageBoxStandard(
        Window owner,
        string title,
        string text,
        ButtonEnum buttons = ButtonEnum.Ok,
        Icon icon = Icon.None)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title, text, buttons, icon);
        if (owner.IsVisible)
        {
            return await box.ShowWindowDialogAsync(owner);
        }

        return await ShowWithTemporaryOwner(box);
    }

    public static async Task<ButtonResult> ShowMessageBoxStandard(
        string title,
        string text,
        ButtonEnum buttons = ButtonEnum.Ok,
        Icon icon = Icon.None,
        WindowStartupLocation windowStartupLocation = WindowStartupLocation.CenterScreen)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title, text, buttons, icon);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { IsVisible: true } mainWindow)
        {
            return await box.ShowWindowDialogAsync(mainWindow);
        }

        return await ShowWithTemporaryOwner(box, windowStartupLocation);
    }

    private static async Task<ButtonResult> ShowWithTemporaryOwner(
        MsBox.Avalonia.Base.IMsBox<ButtonResult> box,
        WindowStartupLocation windowStartupLocation = WindowStartupLocation.CenterScreen)
    {
        var temporaryOwner = new Window
        {
            Opacity = 0,
            Width = 1,
            Height = 1,
            WindowStartupLocation = windowStartupLocation,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false
        };

        try
        {
            temporaryOwner.Show();
            return await box.ShowWindowDialogAsync(temporaryOwner);
        }
        finally
        {
            temporaryOwner.Close();
        }
    }
}
