using System;

namespace ArisenEditor.Core.Assets;

/// <summary>
/// Represents the serialized data in a .meta file.
/// </summary>
public class AssetMetadata
{
    public Guid Guid { get; set; } = Guid.NewGuid();

    public string AssetType { get; set; } = string.Empty;

    public string Importer { get; set; } = string.Empty;

    public string? ImporterType { get; set; }
}
