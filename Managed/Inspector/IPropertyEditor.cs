using Avalonia.Controls;

namespace ArisenEditorFramework.Inspector;

/// <summary>
/// Interface for defining custom property editors.
/// </summary>
public interface IPropertyEditor
{
    /// <summary>
    /// Determines if this editor can handle the specified property.
    /// </summary>
    bool CanHandle(PropertyItemViewModel property);

    /// <summary>
    /// Builds the visual control for the property.
    /// </summary>
    Control CreateControl(PropertyItemViewModel property);
}
