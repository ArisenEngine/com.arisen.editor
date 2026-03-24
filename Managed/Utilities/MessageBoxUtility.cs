using MsBox.Avalonia.Enums;
using MsBox.Avalonia;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace ArisenEditorFramework.Utilities
{
    public static class MessageBoxUtility
    {
        /// <summary>
        /// Shows a message box as a dialog of the given owner window.
        /// This is the preferred overload when you have a known visible window.
        /// </summary>
        public static async Task ShowMessageBoxStandard(Window owner, string title, string text, 
            ButtonEnum @enum = ButtonEnum.Ok, Icon icon = Icon.None)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(title, text, @enum);
            
            if (owner.IsVisible)
            {
                await box.ShowWindowDialogAsync(owner);
            }
            else
            {
                // Owner exists but isn't visible — show with a temporary window
                await ShowWithTemporaryOwner(box);
            }
        }

        /// <summary>
        /// Shows a message box, auto-detecting the owner window.
        /// Falls back to a temporary invisible owner if no MainWindow is available.
        /// </summary>
        public static async Task ShowMessageBoxStandard(string title, string text, ButtonEnum @enum = ButtonEnum.Ok, 
            Icon icon = Icon.None, WindowStartupLocation windowStartupLocation = WindowStartupLocation.CenterScreen)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(title, text, @enum);
            
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow != null && desktop.MainWindow.IsVisible)
                {
                    await box.ShowWindowDialogAsync(desktop.MainWindow);
                }
                else 
                {
                    await ShowWithTemporaryOwner(box);
                }
            }
            else
            {
                await ShowWithTemporaryOwner(box);
            }
        }

        private static async Task ShowWithTemporaryOwner(MsBox.Avalonia.Base.IMsBox<MsBox.Avalonia.Enums.ButtonResult> box)
        {
            // Create a temporary invisible owner window to ensure the message box displays reliably.
            var tempWindow = new Window
            {
                Opacity = 0,
                Width = 1,
                Height = 1,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false
            };

            try
            {
                tempWindow.Show();
                await box.ShowWindowDialogAsync(tempWindow);
            }
            finally
            {
                tempWindow.Close();
            }
        }
    }
}
