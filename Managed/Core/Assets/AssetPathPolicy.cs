using System;
using System.IO;

namespace ArisenEditor.Core.Assets;

internal static class AssetPathPolicy
{
    public static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsGeneratedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var directory = Directory.Exists(path)
            ? new DirectoryInfo(NormalizeFullPath(path))
            : new DirectoryInfo(Path.GetDirectoryName(NormalizeFullPath(path)) ?? NormalizeFullPath(path));

        while (directory != null)
        {
            if (string.Equals(directory.Name, ".arisen", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    public static bool IsAssetsRoot(string path)
    {
        return string.Equals(Path.GetFileName(NormalizeFullPath(path)), "Assets", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrChildPath(string path, string potentialParent)
    {
        var normalizedPath = NormalizeFullPath(path);
        var normalizedParent = NormalizeFullPath(potentialParent);

        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderAssetsRoot(string path, string assetsRoot)
    {
        return IsAssetsRoot(assetsRoot)
            && !IsGeneratedPath(path)
            && IsSameOrChildPath(path, assetsRoot);
    }

    public static bool IsEditableAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsGeneratedPath(path))
        {
            return false;
        }

        var normalizedPath = NormalizeFullPath(path);
        var current = new DirectoryInfo(Directory.Exists(path)
            ? normalizedPath
            : Path.GetDirectoryName(normalizedPath) ?? string.Empty);

        while (current != null)
        {
            if (string.Equals(current.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return !normalizedPath.Equals(current.FullName, StringComparison.OrdinalIgnoreCase);
            }

            current = current.Parent;
        }

        return false;
    }
}
