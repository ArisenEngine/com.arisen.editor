using System;
using YamlDotNet.Serialization;

namespace ArisenEditor.Core.Assets;

/// <summary>
/// Represents the serialized data in a .meta file.
/// </summary>
public class AssetMetadata
{
    private string? m_ImporterType;

    public Guid Guid { get; set; } = Guid.NewGuid();

    public string AssetType { get; set; } = string.Empty;

    public string Importer { get; set; } = string.Empty;

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public string? ImporterType
    {
        get => m_ImporterType;
        set
        {
            m_ImporterType = value;
            HasLegacyImporterType = true;
        }
    }

    [YamlIgnore]
    public bool HasLegacyImporterType { get; private set; }

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public GeneratedAssetMetadata? Generated { get; set; }
}

public sealed class GeneratedAssetMetadata
{
    public Guid SourceGuid { get; set; }

    public string SourcePackageId { get; set; } = string.Empty;

    public string ChildKind { get; set; } = string.Empty;

    public string ChildKey { get; set; } = string.Empty;

    public string GeneratedByImporter { get; set; } = string.Empty;
}
