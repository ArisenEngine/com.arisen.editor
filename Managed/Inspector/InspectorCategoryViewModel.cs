using System.Collections.ObjectModel;
using ReactiveUI;

namespace ArisenEditorFramework.Inspector;

/// <summary>
/// Organizes PropertyItemViewModels into a named category for the Inspector to display in groups or expanders.
/// </summary>
public class InspectorCategoryViewModel : ReactiveObject
{
    private string _categoryName;
    private bool _isExpanded = true;

    public string CategoryName
    {
        get => _categoryName;
        set => this.RaiseAndSetIfChanged(ref _categoryName, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    private System.Windows.Input.ICommand? _removeCommand;
    private bool _canRemove;
    
    public System.Windows.Input.ICommand? RemoveCommand
    {
        get => _removeCommand;
        set => this.RaiseAndSetIfChanged(ref _removeCommand, value);
    }

    public bool CanRemove
    {
        get => _canRemove;
        set => this.RaiseAndSetIfChanged(ref _canRemove, value);
    }

    public ObservableCollection<PropertyItemViewModel> Properties { get; } = new();

    public InspectorCategoryViewModel(string categoryName)
    {
        _categoryName = categoryName;
    }
}
