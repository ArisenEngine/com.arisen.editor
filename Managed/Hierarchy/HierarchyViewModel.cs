using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;
using System;
using System.Linq;

namespace ArisenEditorFramework.Hierarchy;

/// <summary>
/// A controller ViewModel representing the root of a hierarchical tree view.
/// Manages the top-level collection of items and commands for the overall tree.
/// </summary>
public class HierarchyViewModel : ReactiveObject
{
    private IHierarchyItem? _selectedItem;

    public ObservableCollection<IHierarchyItem> Items { get; } = new();

    /// <summary>
    /// The currently selected item in the tree view.
    /// </summary>
    public IHierarchyItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != value)
            {
                if (_selectedItem != null) _selectedItem.IsSelected = false;
                this.RaiseAndSetIfChanged(ref _selectedItem, value);
                if (_selectedItem != null) _selectedItem.IsSelected = true;
                
                SelectedItemChanged?.Invoke(this, _selectedItem);
            }
        }
    }

    /// <summary>
    /// Event fired when the selected item changes so external windows can react.
    /// </summary>
    public event EventHandler<IHierarchyItem?>? SelectedItemChanged;

    public ICommand AddRootItemCommand { get; }
    public ICommand AddChildItemCommand { get; }
    public ICommand DeleteSelectedItemCommand { get; }
    public ICommand RenameSelectedItemCommand { get; }

    public HierarchyViewModel()
    {
        AddRootItemCommand = ReactiveCommand.Create(() => {
            var newItem = CreateNewItem("New Item");
            Items.Add(newItem);
        });

        AddChildItemCommand = ReactiveCommand.Create(() => {
            if (SelectedItem != null && !SelectedItem.IsLeaf)
            {
                var newItem = CreateNewItem("New Child Item");
                newItem.Parent = SelectedItem;
                SelectedItem.Children.Add(newItem);
                SelectedItem.IsExpanded = true;
            }
            else if (SelectedItem == null)
            {
                var newItem = CreateNewItem("New Item");
                Items.Add(newItem);
            }
        });

        DeleteSelectedItemCommand = ReactiveCommand.Create(() => {
            if (SelectedItem != null)
            {
                if (SelectedItem.Parent != null)
                {
                    SelectedItem.Parent.Children.Remove(SelectedItem);
                }
                else
                {
                    Items.Remove(SelectedItem);
                }
                SelectedItem = null;
            }
        });

        RenameSelectedItemCommand = ReactiveCommand.Create(() => {
            if (SelectedItem != null && SelectedItem.BeginRenameCommand != null && SelectedItem.BeginRenameCommand.CanExecute(null))
            {
                SelectedItem.BeginRenameCommand.Execute(null);
            }
        });
    }

    /// <summary>
    /// Override or subscribe to this mapping inside specific instances to create specific item types.
    /// </summary>
    protected virtual IHierarchyItem CreateNewItem(string name)
    {
        var vm = new HierarchyItemViewModel { Name = name };
        vm.RequestDelete += OnItemDeletedRequest;
        return vm;
    }

    private void OnItemDeletedRequest(object? sender, IHierarchyItem item)
    {
         if (item.Parent == null)
         {
             Items.Remove(item);
         }
         else
         {
             item.Parent.Children.Remove(item);
         }
         if (SelectedItem == item)
         {
             SelectedItem = null;
         }
    }
    
    public void SelectItem(IHierarchyItem item)
    {
        SelectedItem = item;
    }
}
