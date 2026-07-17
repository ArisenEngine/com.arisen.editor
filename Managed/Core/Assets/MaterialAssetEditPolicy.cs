using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Serialization;

namespace ArisenEditor.Core.Assets;

internal static class MaterialAssetEditPolicy
{
    public static bool CanEdit(AssetRecord sourceAsset, out string diagnostic)
    {
        if (sourceAsset == null)
        {
            diagnostic = "Material asset record is unavailable.";
            return false;
        }

        if (!string.Equals(sourceAsset.AssetType, "Material", StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = $"Asset type '{sourceAsset.AssetType}' is not an editable material.";
            return false;
        }

        if (!AssetPathPolicy.IsEditableAssetPath(sourceAsset.SourcePath))
        {
            diagnostic = "Only source materials under workspace/package Assets roots can be edited.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceAsset.MetaPath) || !File.Exists(sourceAsset.MetaPath))
        {
            diagnostic = "Material metadata is missing; source editing is disabled until the asset is reimported.";
            return false;
        }

        try
        {
            var metadata = SerializationUtil.Deserialize<AssetMetadata>(
                sourceAsset.MetaPath,
                serializeIfNotExist: false);
            if (metadata.Guid != sourceAsset.Guid)
            {
                diagnostic =
                    $"Material metadata GUID '{metadata.Guid:D}' does not match indexed GUID '{sourceAsset.Guid:D}'.";
                return false;
            }

            if (metadata.Generated != null)
            {
                var importer = string.IsNullOrWhiteSpace(metadata.Generated.GeneratedByImporter)
                    ? metadata.Importer
                    : metadata.Generated.GeneratedByImporter;
                diagnostic =
                    $"Importer-generated material is read-only. Reimport source '{metadata.Generated.SourceGuid:D}' through '{importer}'.";
                return false;
            }
        }
        catch (Exception ex)
        {
            diagnostic = $"Material metadata could not be read safely: {ex.Message}";
            return false;
        }

        diagnostic = "Authored source material is editable with undo/redo.";
        return true;
    }
}
