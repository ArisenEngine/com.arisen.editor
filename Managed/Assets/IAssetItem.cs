using Avalonia.Media.Imaging;

namespace ArisenEditorFramework.Assets;

/// <summary>
/// Interface for items displayed in the Asset Browser (files or folders).
/// </summary>
public interface IAssetItem
{
    string Name { get; }
    string FullPath { get; }
    bool IsDirectory { get; }
    string Extension { get; }
    Bitmap? Icon { get; }
}
