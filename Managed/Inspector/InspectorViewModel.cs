using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ArisenEditorFramework.Core;
using ReactiveUI;

namespace ArisenEditorFramework.Inspector;

/// <summary>
/// The main ViewModel for the Property Grid component.
/// Set the TargetObject to automatically generate UI layouts via Reflection.
/// </summary>
public class InspectorViewModel : EditorPanelBase
{
    public override string Title => "Inspector";
    public override string Id => "Inspector";
    public override object Content => new InspectorControl { DataContext = this };

    private object? _targetObject;
    private bool _canAddComponent;

    public ObservableCollection<InspectorCategoryViewModel> Categories { get; } = new();
    public ObservableCollection<Type> AvailableComponentTypes { get; } = new();

    public System.Windows.Input.ICommand? AddComponentCommand { get; protected set; }

    public bool CanAddComponent
    {
        get => _canAddComponent;
        protected set => this.RaiseAndSetIfChanged(ref _canAddComponent, value);
    }

    public Type? SelectedComponentToAdd
    {
        get => null;
        set
        {
            if (value != null && AddComponentCommand != null && AddComponentCommand.CanExecute(value))
            {
                AddComponentCommand.Execute(value);
                this.RaisePropertyChanged(nameof(SelectedComponentToAdd));
            }
        }
    }

    /// <summary>
    /// The object currently being inspected. Setting this triggers a full reflection pass.
    /// </summary>
    public object? TargetObject
    {
        get => _targetObject;
        set
        {
            if (_targetObject != value)
            {
                this.RaiseAndSetIfChanged(ref _targetObject, value);
                RebuildProperties();
            }
        }
    }

    protected virtual void RebuildProperties()
    {
        // Dispose existing property view models to unsubscribe event handlers and prevent leaks.
        foreach (var category in Categories)
        {
            foreach (var prop in category.Properties)
            {
                prop.Dispose();
            }
        }
        Categories.Clear();

        if (_targetObject == null)
            return;

        var type = _targetObject.GetType();
        
        // Find all public instance properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        
        foreach (var prop in properties)
        {
            // Skip properties that shouldn't be browsed
            var browsableAttr = prop.GetCustomAttribute<BrowsableAttribute>(true);
            if (browsableAttr != null && !browsableAttr.Browsable)
                continue;

            // Must be readable
            if (!prop.CanRead)
                continue;

            // Create our ViewModel wrapper for this property
            var propVm = new PropertyItemViewModel(_targetObject, prop);

            // Find or create the category group
            var category = Categories.FirstOrDefault(c => c.CategoryName == propVm.Category);
            if (category == null)
            {
                category = new InspectorCategoryViewModel(propVm.Category);
                Categories.Add(category);
            }

            category.Properties.Add(propVm);
        }
        
        // Sort Categories alphabetically but maybe keep "Misc" or "General" at bottom/top
        var sortedCategories = Categories.OrderBy(c => c.CategoryName == "Misc" ? 1 : 0).ThenBy(c => c.CategoryName).ToList();
        Categories.Clear();
        foreach (var cat in sortedCategories)
        {
            Categories.Add(cat);
        }
    }
}
