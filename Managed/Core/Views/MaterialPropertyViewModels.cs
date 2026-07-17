using System.Numerics;
using ArisenEditor.Core.Commands;
using ArisenEditorFramework.Inspector;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Automation;
using ArisenEngine.Rendering.Resources;
using ReactiveUI;

namespace ArisenEditor.ViewModels;

public sealed record MaterialTextureAssetOption(
    MaterialTextureSourceReference Reference,
    string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class MaterialTexturePropertyViewModel : PropertyItemViewModel
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly AssetRecord m_SourceAsset;
    private readonly IReadOnlyList<MaterialTextureAssetOption> m_Options;
    private MaterialTextureSourceReference m_Current;
    private MaterialTextureAssetOption m_Selected;

    public MaterialTexturePropertyViewModel(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        MaterialTexture2DRef textureRef,
        IReadOnlyList<MaterialTextureAssetOption> options,
        bool isReadOnly)
        : base(sourceAsset, textureRef.Name, typeof(MaterialTextureAssetOption), isReadOnly, "Texture2D Refs")
    {
        m_AssetDatabase = assetDatabase;
        m_SourceAsset = sourceAsset;
        m_Current = new MaterialTextureSourceReference(
            textureRef.Texture.Guid,
            textureRef.Texture.Name,
            textureRef.Texture.SourceFormat);

        var optionList = options.ToList();
        var selected = optionList.FirstOrDefault(option => option.Reference.Guid == textureRef.Texture.Guid);
        if (selected == null)
        {
            selected = new MaterialTextureAssetOption(
                m_Current,
                $"Missing: {textureRef.Texture.Name} ({textureRef.Texture.Guid:D})");
            optionList.Insert(0, selected);
        }

        m_Options = optionList;
        m_Selected = selected;
        Description =
            $"Texture binding slot {textureRef.Slot}. Reassignment preserves its variant, sampler, and UV transform metadata.";
    }

    public IReadOnlyList<MaterialTextureAssetOption> Options => m_Options;

    public override object? Value
    {
        get => m_Selected;
        set
        {
            if (IsReadOnly || value is not MaterialTextureAssetOption selected ||
                selected.Reference.Guid == m_Current.Guid)
            {
                return;
            }

            Execute(new ModifyMaterialTexturePropertyCommand(
                m_AssetDatabase,
                m_SourceAsset,
                PropertyName,
                m_Current,
                selected.Reference,
                OnApplied));
        }
    }

    private void OnApplied(MaterialTextureSourceReference value)
    {
        m_Current = value;
        m_Selected = m_Options.FirstOrDefault(option => option.Reference.Guid == value.Guid)
            ?? new MaterialTextureAssetOption(value, $"Missing: {value.Name} ({value.Guid:D})");
        this.RaisePropertyChanged(nameof(Value));
    }

    private static void Execute(ArisenEngine.Core.Automation.ICommand command)
    {
        var commandManager = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ICommandManager>()
            ?? throw new InvalidOperationException("Editor command manager service is unavailable.");
        commandManager.Execute(command);
    }
}

public sealed class MaterialScalarPropertyViewModel : PropertyItemViewModel
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly AssetRecord m_SourceAsset;
    private float m_Value;

    public MaterialScalarPropertyViewModel(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        MaterialScalarProperty property,
        bool isReadOnly)
        : base(sourceAsset, property.Name, typeof(float), isReadOnly, "Scalar Properties")
    {
        m_AssetDatabase = assetDatabase;
        m_SourceAsset = sourceAsset;
        m_Value = property.Value;
        Description = "Authored material scalar. Changes invalidate cooked material data and support undo/redo.";
    }

    public override object? Value
    {
        get => m_Value;
        set
        {
            if (IsReadOnly)
            {
                return;
            }

            var converted = TryConvert(value, typeof(float));
            if (converted is not float newValue || !float.IsFinite(newValue) || newValue.Equals(m_Value))
            {
                return;
            }

            Execute(new ModifyMaterialScalarPropertyCommand(
                m_AssetDatabase,
                m_SourceAsset,
                PropertyName,
                m_Value,
                newValue,
                OnApplied));
        }
    }

    private void OnApplied(float value)
    {
        m_Value = value;
        this.RaisePropertyChanged(nameof(Value));
    }

    private static void Execute(ArisenEngine.Core.Automation.ICommand command)
    {
        var commandManager = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ICommandManager>()
            ?? throw new InvalidOperationException("Editor command manager service is unavailable.");
        commandManager.Execute(command);
    }
}

public sealed class MaterialVector4PropertyViewModel : PropertyItemViewModel
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly AssetRecord m_SourceAsset;
    private Vector4 m_Value;

    public MaterialVector4PropertyViewModel(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        MaterialVector4Property property,
        bool isReadOnly)
        : base(sourceAsset, property.Name, typeof(Vector4), isReadOnly, "Vector4 Properties")
    {
        m_AssetDatabase = assetDatabase;
        m_SourceAsset = sourceAsset;
        m_Value = property.Value;
        Description = "Authored four-component material property. Changes invalidate cooked material data and support undo/redo.";
    }

    public override object? Value
    {
        get => m_Value;
        set
        {
            if (IsReadOnly || value is not Vector4 newValue || newValue == m_Value || !IsFinite(newValue))
            {
                return;
            }

            Execute(new ModifyMaterialVector4PropertyCommand(
                m_AssetDatabase,
                m_SourceAsset,
                PropertyName,
                m_Value,
                newValue,
                OnApplied));
        }
    }

    private void OnApplied(Vector4 value)
    {
        m_Value = value;
        this.RaisePropertyChanged(nameof(Value));
    }

    private static bool IsFinite(Vector4 value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               float.IsFinite(value.W);
    }

    private static void Execute(ArisenEngine.Core.Automation.ICommand command)
    {
        var commandManager = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ICommandManager>()
            ?? throw new InvalidOperationException("Editor command manager service is unavailable.");
        commandManager.Execute(command);
    }
}
