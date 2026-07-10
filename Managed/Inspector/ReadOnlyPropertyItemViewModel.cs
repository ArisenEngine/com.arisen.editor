using System;

namespace ArisenEditorFramework.Inspector;

public sealed class ReadOnlyPropertyItemViewModel : PropertyItemViewModel
{
    private readonly object? m_Value;

    public ReadOnlyPropertyItemViewModel(
        string name,
        object? value,
        string category = "Misc",
        string description = "",
        string? displayName = null,
        Type? valueType = null)
        : base(value ?? string.Empty, name, valueType ?? value?.GetType() ?? typeof(string), true, category)
    {
        m_Value = value;
        Description = description;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
    }

    public override object? Value
    {
        get => m_Value;
        set { }
    }
}
