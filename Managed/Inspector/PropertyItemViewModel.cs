using System;
using System.ComponentModel;
using System.Reflection;
using ReactiveUI;

namespace ArisenEditorFramework.Inspector;

/// <summary>
/// Represents a single editable property discovered via reflection.
/// Binds to a specific PropertyInfo of a given Target object.
/// Implements IDisposable to clean up event subscriptions and prevent memory leaks.
/// </summary>
public class PropertyItemViewModel : ReactiveObject, IDisposable
{
    protected readonly PropertyInfo? _propertyInfo;
    protected readonly object _target;
    private PropertyChangedEventHandler? _targetPropertyChangedHandler;
    private bool _disposed;

    public string PropertyName { get; protected set; }
    public string DisplayName { get; protected set; }
    public string Description { get; protected set; }
    public string Category { get; protected set; }
    public Type PropertyType { get; protected set; }
    
    public bool IsReadOnly { get; protected set; }

    /// <summary>
    /// Gets or sets the value of the property on the underlying object.
    /// Notifies the UI when changed.
    /// </summary>
    public virtual object? Value
    {
        get => _propertyInfo?.GetValue(_target);
        set
        {
            if (!IsReadOnly && _propertyInfo != null)
            {
                // Simple attempt to convert if needed, e.g., string from a TextBox to numeric
                object? convertedValue = TryConvert(value, PropertyType);
                _propertyInfo.SetValue(_target, convertedValue);
                this.RaisePropertyChanged(nameof(Value));
            }
        }
    }

    protected object? TryConvert(object? value, Type targetType)
    {
        if (value == null || targetType == value.GetType())
            return value;

        try
        {
            var converter = TypeDescriptor.GetConverter(targetType);
            if (converter.CanConvertFrom(value.GetType()))
            {
                return converter.ConvertFrom(value);
            }
            return Convert.ChangeType(value, targetType);
        }
        catch
        {
            return value; // Or log error
        }
    }

    public PropertyItemViewModel(object target, PropertyInfo propertyInfo)
    {
        _target = target;
        _propertyInfo = propertyInfo;
        
        PropertyName = _propertyInfo.Name;
        PropertyType = _propertyInfo.PropertyType;
        IsReadOnly = !_propertyInfo.CanWrite;

        // Default metadata
        DisplayName = PropertyName;
        Description = string.Empty;
        Category = "Misc";

        ApplyAttributes(_propertyInfo);
        SubscribeToTarget();
    }

    protected PropertyItemViewModel(object target, string name, Type type, bool isReadOnly, string category = "Misc")
    {
        _target = target;
        PropertyName = name;
        DisplayName = name;
        PropertyType = type;
        IsReadOnly = isReadOnly;
        Category = category;
        Description = string.Empty;
        SubscribeToTarget();
    }

    protected void ApplyAttributes(MemberInfo member)
    {
        var displayAttributes = member.GetCustomAttributes(typeof(DisplayNameAttribute), true);
        if (displayAttributes.Length > 0 && displayAttributes[0] is DisplayNameAttribute display)
            DisplayName = display.DisplayName;

        var descriptionAttributes = member.GetCustomAttributes(typeof(DescriptionAttribute), true);
        if (descriptionAttributes.Length > 0 && descriptionAttributes[0] is DescriptionAttribute desc)
            Description = desc.Description;

        var categoryAttributes = member.GetCustomAttributes(typeof(CategoryAttribute), true);
        if (categoryAttributes.Length > 0 && categoryAttributes[0] is CategoryAttribute cat)
            Category = cat.Category;
    }

    private void SubscribeToTarget()
    {
        if (_target is INotifyPropertyChanged npc)
        {
             _targetPropertyChangedHandler = (s, e) => {
                 if (e.PropertyName == PropertyName)
                 {
                     this.RaisePropertyChanged(nameof(Value));
                 }
             };
             npc.PropertyChanged += _targetPropertyChangedHandler;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_targetPropertyChangedHandler != null && _target is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= _targetPropertyChangedHandler;
        }
    }
}

