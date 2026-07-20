using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ArisenEditor.Core.Services;

internal readonly record struct WorkspaceManifestEditResult(bool Success, string Diagnostic);

internal readonly record struct WorkspaceProjectAssetSelection(Guid Guid, string PackageId);

internal static class WorkspaceManifestEditor
{
    private readonly record struct RootPropertySpan(
        string Name,
        int NameStart,
        int ValueStart,
        int ValueEnd);

    private readonly record struct AssetReferenceUpdate(
        string PropertyName,
        string DisplayName,
        Guid Guid,
        string PackageId);

    private readonly record struct SourceReplacement(int Start, int End, byte[] Value);

    private static readonly JsonDocumentOptions s_DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonReaderOptions s_ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static WorkspaceManifestEditResult SetStartupScene(
        string manifestPath,
        Guid sceneGuid,
        string packageId)
    {
        return SetAssetReferences(
            manifestPath,
            new AssetReferenceUpdate("StartupScene", "Startup scene", sceneGuid, packageId));
    }

    public static WorkspaceManifestEditResult SetRenderPipeline(
        string manifestPath,
        Guid settingsGuid,
        string packageId)
    {
        return SetAssetReferences(
            manifestPath,
            new AssetReferenceUpdate("RenderPipeline", "Render pipeline", settingsGuid, packageId));
    }

    public static WorkspaceManifestEditResult SetProjectAssets(
        string manifestPath,
        WorkspaceProjectAssetSelection startupScene,
        WorkspaceProjectAssetSelection renderPipeline)
    {
        return SetAssetReferences(
            manifestPath,
            new AssetReferenceUpdate(
                "StartupScene",
                "Startup scene",
                startupScene.Guid,
                startupScene.PackageId),
            new AssetReferenceUpdate(
                "RenderPipeline",
                "Render pipeline",
                renderPipeline.Guid,
                renderPipeline.PackageId));
    }

    private static WorkspaceManifestEditResult SetAssetReferences(
        string manifestPath,
        params AssetReferenceUpdate[] updates)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return new WorkspaceManifestEditResult(false, "Workspace manifest path is empty.");
        }

        foreach (var update in updates)
        {
            if (update.Guid == Guid.Empty)
            {
                return new WorkspaceManifestEditResult(
                    false,
                    $"{update.DisplayName} requires a valid GUID.");
            }

            if (string.IsNullOrWhiteSpace(update.PackageId))
            {
                return new WorkspaceManifestEditResult(
                    false,
                    $"{update.DisplayName} requires an owning package ID.");
            }
        }

        string fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            return new WorkspaceManifestEditResult(false, $"Workspace manifest was not found: {fullPath}");
        }

        try
        {
            byte[] sourceFile = File.ReadAllBytes(fullPath);
            bool hasUtf8Bom = sourceFile.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            byte[] source = hasUtf8Bom
                ? sourceFile.AsSpan(Encoding.UTF8.Preamble.Length).ToArray()
                : sourceFile;

            using var sourceDocument = JsonDocument.Parse(source, s_DocumentOptions);
            if (sourceDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new WorkspaceManifestEditResult(false, "Workspace manifest root must be an object.");
            }

            foreach (var update in updates)
            {
                if (!ContainsBasePackage(sourceDocument.RootElement, update.PackageId))
                {
                    return new WorkspaceManifestEditResult(
                        false,
                        $"{update.DisplayName} package '{update.PackageId}' is not selected in the workspace base Packages list.");
                }
            }

            if (!TryReadRootProperties(source, out var properties, out var parseError))
            {
                return new WorkspaceManifestEditResult(false, parseError);
            }

            var propertiesByUpdate = new Dictionary<string, RootPropertySpan[]>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var update in updates)
            {
                var matchingProperties = properties
                    .Where(property => string.Equals(
                        property.Name,
                        update.PropertyName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingProperties.Length > 1)
                {
                    return new WorkspaceManifestEditResult(
                        false,
                        $"Workspace manifest contains more than one top-level {update.PropertyName} property.");
                }

                propertiesByUpdate.Add(update.PropertyName, matchingProperties);
            }

            if (updates.All(update => AssetReferenceMatches(
                    sourceDocument.RootElement,
                    update.PropertyName,
                    update.Guid,
                    update.PackageId)))
            {
                string subject = updates.Length == 1
                    ? updates[0].DisplayName
                    : "Project asset selections";
                return new WorkspaceManifestEditResult(
                    true,
                    $"{subject} already matches the selected asset values.");
            }

            string newline = DetectNewline(source);
            string indentUnit = InferIndentUnit(properties, source);
            var replacements = new List<SourceReplacement>();
            var missingUpdates = new List<AssetReferenceUpdate>();
            foreach (var update in updates)
            {
                var matchingProperties = propertiesByUpdate[update.PropertyName];
                if (matchingProperties.Length == 0)
                {
                    missingUpdates.Add(update);
                    continue;
                }

                if (AssetReferenceMatches(
                        sourceDocument.RootElement,
                        update.PropertyName,
                        update.Guid,
                        update.PackageId))
                {
                    continue;
                }

                var property = matchingProperties[0];
                string propertyIndent = GetPropertyIndent(source, property.NameStart);
                replacements.Add(new SourceReplacement(
                    property.ValueStart,
                    property.ValueEnd,
                    BuildAssetReferenceValue(
                        update.Guid,
                        update.PackageId,
                        newline,
                        propertyIndent,
                        indentUnit)));
            }

            if (missingUpdates.Count > 0)
            {
                var packagesProperty = properties.FirstOrDefault(property =>
                    string.Equals(property.Name, "Packages", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(packagesProperty.Name))
                {
                    return new WorkspaceManifestEditResult(
                        false,
                        "Workspace manifest is missing the required top-level Packages property.");
                }

                string propertyIndent = GetPropertyIndent(source, packagesProperty.NameStart);
                var insertion = new StringBuilder();
                foreach (var update in missingUpdates)
                {
                    byte[] value = BuildAssetReferenceValue(
                        update.Guid,
                        update.PackageId,
                        newline,
                        propertyIndent,
                        indentUnit);
                    insertion.Append('"');
                    insertion.Append(update.PropertyName);
                    insertion.Append("\": ");
                    insertion.Append(Encoding.UTF8.GetString(value));
                    insertion.Append(',');
                    insertion.Append(newline);
                    insertion.Append(propertyIndent);
                }

                replacements.Add(new SourceReplacement(
                    packagesProperty.NameStart,
                    packagesProperty.NameStart,
                    Encoding.UTF8.GetBytes(insertion.ToString())));
            }

            byte[] updated = source;
            foreach (var replacement in replacements.OrderByDescending(replacement => replacement.Start))
            {
                updated = ReplaceRange(
                    updated,
                    replacement.Start,
                    replacement.End,
                    replacement.Value);
            }

            if (!ValidateUpdatedManifest(updated, updates, out var validationError))
            {
                return new WorkspaceManifestEditResult(false, validationError);
            }

            byte[] updatedFile;
            if (hasUtf8Bom)
            {
                byte[] preamble = Encoding.UTF8.GetPreamble();
                updatedFile = new byte[preamble.Length + updated.Length];
                preamble.CopyTo(updatedFile, 0);
                updated.CopyTo(updatedFile, preamble.Length);
            }
            else
            {
                updatedFile = updated;
            }

            WriteAtomically(fullPath, updatedFile);
            return new WorkspaceManifestEditResult(
                true,
                "Updated project asset selections in manifest.json.");
        }
        catch (JsonException ex)
        {
            return new WorkspaceManifestEditResult(false, $"Workspace manifest is invalid: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new WorkspaceManifestEditResult(false, $"Failed to update workspace manifest: {ex.Message}");
        }
    }

    private static bool TryReadRootProperties(
        ReadOnlySpan<byte> json,
        out List<RootPropertySpan> properties,
        out string error)
    {
        properties = new List<RootPropertySpan>();
        error = string.Empty;

        var reader = new Utf8JsonReader(json, s_ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "Workspace manifest root must be an object.";
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
            {
                return true;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            int nameStart = checked((int)reader.TokenStartIndex);
            if (!reader.Read())
            {
                error = $"Workspace manifest property '{name}' has no value.";
                return false;
            }

            int valueStart = checked((int)reader.TokenStartIndex);
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            int valueEnd = checked((int)reader.BytesConsumed);
            properties.Add(new RootPropertySpan(name, nameStart, valueStart, valueEnd));
        }

        error = "Workspace manifest root object is incomplete.";
        return false;
    }

    private static bool ContainsBasePackage(JsonElement root, string packageId)
    {
        if (!TryGetProperty(root, "Packages", out var packages) ||
            packages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind == JsonValueKind.Object &&
                TryGetProperty(package, "Id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), packageId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AssetReferenceMatches(
        JsonElement root,
        string propertyName,
        Guid assetGuid,
        string packageId)
    {
        return TryGetProperty(root, propertyName, out var assetReference) &&
               assetReference.ValueKind == JsonValueKind.Object &&
               TryGetProperty(assetReference, "Guid", out var guidElement) &&
               guidElement.ValueKind == JsonValueKind.String &&
               Guid.TryParse(guidElement.GetString(), out var currentGuid) &&
               currentGuid == assetGuid &&
               TryGetProperty(assetReference, "PackageId", out var packageElement) &&
               packageElement.ValueKind == JsonValueKind.String &&
               string.Equals(packageElement.GetString(), packageId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateUpdatedManifest(
        byte[] updated,
        IReadOnlyList<AssetReferenceUpdate> updates,
        out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(updated, s_DocumentOptions);
            foreach (var update in updates)
            {
                if (!AssetReferenceMatches(
                        document.RootElement,
                        update.PropertyName,
                        update.Guid,
                        update.PackageId))
                {
                    error =
                        $"Updated workspace manifest does not contain the requested {update.PropertyName} reference.";
                    return false;
                }

                if (!ContainsBasePackage(document.RootElement, update.PackageId))
                {
                    error = $"Updated workspace manifest lost base package '{update.PackageId}'.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Updated workspace manifest failed validation: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static byte[] BuildAssetReferenceValue(
        Guid assetGuid,
        string packageId,
        string newline,
        string propertyIndent,
        string indentUnit)
    {
        string childIndent = propertyIndent + indentUnit;
        string serializedPackageId = JsonSerializer.Serialize(packageId);
        string value =
            $"{{{newline}" +
            $"{childIndent}\"Guid\": \"{assetGuid:D}\",{newline}" +
            $"{childIndent}\"PackageId\": {serializedPackageId}{newline}" +
            $"{propertyIndent}}}";
        return Encoding.UTF8.GetBytes(value);
    }

    private static string DetectNewline(ReadOnlySpan<byte> source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != (byte)'\n')
            {
                continue;
            }

            return i > 0 && source[i - 1] == (byte)'\r' ? "\r\n" : "\n";
        }

        return Environment.NewLine;
    }

    private static string InferIndentUnit(
        IReadOnlyList<RootPropertySpan> properties,
        ReadOnlySpan<byte> source)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            string indent = GetPropertyIndent(source, properties[i].NameStart);
            if (indent.Length > 0)
            {
                return indent;
            }
        }

        return "  ";
    }

    private static string GetPropertyIndent(ReadOnlySpan<byte> source, int tokenStart)
    {
        int lineStart = tokenStart;
        while (lineStart > 0 && source[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        for (int i = lineStart; i < tokenStart; i++)
        {
            byte value = source[i];
            if (value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r')
            {
                return string.Empty;
            }
        }

        return Encoding.UTF8.GetString(source[lineStart..tokenStart]).TrimEnd('\r');
    }

    private static byte[] ReplaceRange(
        ReadOnlySpan<byte> source,
        int start,
        int end,
        ReadOnlySpan<byte> replacement)
    {
        int removedLength = end - start;
        var result = new byte[source.Length - removedLength + replacement.Length];
        source[..start].CopyTo(result);
        replacement.CopyTo(result.AsSpan(start));
        source[end..].CopyTo(result.AsSpan(start + replacement.Length));
        return result;
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Workspace manifest has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
