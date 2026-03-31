using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace ArisenEditorFramework.Inspector;

/// <summary>
/// Central registry for property editors.
/// </summary>
public static class PropertyEditorRegistry
{
    private static readonly List<IPropertyEditor> _editors = new();

    static PropertyEditorRegistry()
    {
        RegisterEditor(new BooleanPropertyEditor());
        RegisterEditor(new NumericPropertyEditor());
        RegisterEditor(new EnumPropertyEditor());
        RegisterEditor(new FlagPropertyEditor());
        RegisterEditor(new StringPropertyEditor());
        RegisterEditor(new Vector3PropertyEditor());
        RegisterEditor(new QuaternionPropertyEditor());
        RegisterEditor(new ColorPropertyEditor());
        RegisterEditor(new ObjectReferencePropertyEditor());
    }

    public static void RegisterEditor(IPropertyEditor editor)
    {
        _editors.Insert(0, editor); // Insert at beginning to allow overrides
    }

    public static Control? CreateEditor(PropertyItemViewModel property)
    {
        foreach (var editor in _editors)
        {
            if (editor.CanHandle(property))
            {
                return editor.CreateControl(property);
            }
        }
        return null;
    }
}
