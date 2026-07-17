using System.Numerics;
using ArisenEditor.Core.Assets;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Automation;
using ArisenEngine.Rendering.Resources;

namespace ArisenEditor.Core.Commands;

public sealed class ModifyMaterialTexturePropertyCommand
    : ModifyMaterialAssetPropertyCommand<MaterialTextureSourceReference>
{
    private readonly string m_BindingName;

    public ModifyMaterialTexturePropertyCommand(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        string bindingName,
        MaterialTextureSourceReference oldValue,
        MaterialTextureSourceReference newValue,
        Action<MaterialTextureSourceReference>? onApplied = null)
        : base(
            assetDatabase,
            sourceAsset,
            oldValue,
            newValue,
            $"Modify material texture '{bindingName}'",
            onApplied)
    {
        m_BindingName = bindingName;
    }

    protected override MaterialSourceEditResult UpdateSource(
        string sourcePath,
        MaterialTextureSourceReference value)
    {
        return MaterialSourceAssetEditor.UpdateTexture2DRef(sourcePath, m_BindingName, value);
    }
}

public sealed class ModifyMaterialScalarPropertyCommand
    : ModifyMaterialAssetPropertyCommand<float>
{
    private readonly string m_PropertyName;

    public ModifyMaterialScalarPropertyCommand(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        string propertyName,
        float oldValue,
        float newValue,
        Action<float>? onApplied = null)
        : base(
            assetDatabase,
            sourceAsset,
            oldValue,
            newValue,
            $"Modify material scalar '{propertyName}'",
            onApplied)
    {
        m_PropertyName = propertyName;
    }

    protected override MaterialSourceEditResult UpdateSource(string sourcePath, float value)
    {
        return MaterialSourceAssetEditor.UpdateScalarProperty(sourcePath, m_PropertyName, value);
    }
}

public sealed class ModifyMaterialVector4PropertyCommand
    : ModifyMaterialAssetPropertyCommand<Vector4>
{
    private readonly string m_PropertyName;

    public ModifyMaterialVector4PropertyCommand(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        string propertyName,
        Vector4 oldValue,
        Vector4 newValue,
        Action<Vector4>? onApplied = null)
        : base(
            assetDatabase,
            sourceAsset,
            oldValue,
            newValue,
            $"Modify material Vector4 '{propertyName}'",
            onApplied)
    {
        m_PropertyName = propertyName;
    }

    protected override MaterialSourceEditResult UpdateSource(string sourcePath, Vector4 value)
    {
        return MaterialSourceAssetEditor.UpdateVector4Property(sourcePath, m_PropertyName, value);
    }
}

public abstract class ModifyMaterialAssetPropertyCommand<T> : ICommand
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly AssetRecord m_SourceAsset;
    private readonly T m_OldValue;
    private readonly T m_NewValue;
    private readonly Action<T>? m_OnApplied;

    protected ModifyMaterialAssetPropertyCommand(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        T oldValue,
        T newValue,
        string description,
        Action<T>? onApplied)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_SourceAsset = sourceAsset ?? throw new ArgumentNullException(nameof(sourceAsset));
        m_OldValue = oldValue;
        m_NewValue = newValue;
        Description = description;
        m_OnApplied = onApplied;
    }

    public string Description { get; }

    public void Execute()
    {
        Apply(m_NewValue);
    }

    public void Undo()
    {
        Apply(m_OldValue);
    }

    protected abstract MaterialSourceEditResult UpdateSource(string sourcePath, T value);

    private void Apply(T value)
    {
        if (!MaterialAssetEditPolicy.CanEdit(m_SourceAsset, out var editDiagnostic))
        {
            throw new InvalidOperationException(editDiagnostic);
        }

        var result = UpdateSource(m_SourceAsset.SourcePath, value);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Diagnostic);
        }

        m_AssetDatabase.InvalidateCookedAssets(m_SourceAsset.Guid);
        m_AssetDatabase.NotifyAssetChanged(new AssetChangeEvent(
            AssetChangeKind.Changed,
            m_SourceAsset.Guid,
            m_SourceAsset.AssetType,
            m_SourceAsset.SourcePath,
            string.Empty,
            m_SourceAsset.PackageId));
        m_OnApplied?.Invoke(value);
    }
}
