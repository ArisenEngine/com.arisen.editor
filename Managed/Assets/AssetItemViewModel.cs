using System.IO;
using Avalonia.Media.Imaging;
using ReactiveUI;

namespace ArisenEditorFramework.Assets;

public class AssetItemViewModel : ReactiveObject, IAssetItem
{
    private string _name;
    private string _fullPath;
    private bool _isDirectory;
    private string _extension;
    private Bitmap? _icon;

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string FullPath
    {
        get => _fullPath;
        set => this.RaiseAndSetIfChanged(ref _fullPath, value);
    }

    public bool IsDirectory
    {
        get => _isDirectory;
        set => this.RaiseAndSetIfChanged(ref _isDirectory, value);
    }

    public string Extension
    {
        get => _extension;
        set => this.RaiseAndSetIfChanged(ref _extension, value);
    }

    public Bitmap? Icon
    {
        get => _icon;
        set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    public AssetItemViewModel(string fullPath)
    {
        _fullPath = fullPath;
        _name = Path.GetFileName(fullPath);
        _isDirectory = Directory.Exists(fullPath);
        _extension = Path.GetExtension(fullPath);
        
        // In a real implementation, we would load system icons or engine thumbnails here
        _icon = null; 
    }
}
