using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;
using System;
using System.Linq;
using ArisenEngine.Core.ECS;
using ArisenKernel.Services;

namespace ArisenEditorFramework.Hierarchy;

/// <summary>
/// A controller ViewModel representing the root of a hierarchical tree view.
/// Manages the top-level collection of items and commands for the overall tree.
/// </summary>
public class HierarchyViewModel : ReactiveObject
{
    private readonly IEntityManager _entityManager;
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

    public HierarchyViewModel(IEntityManager entityManager)
    {
        _entityManager = entityManager;

        // Initialize from existing entities
        foreach (var entity in _entityManager.GetAllEntities())
        {
            Items.Add(CreateHierarchyItem(entity));
        }

        AddRootItemCommand = ReactiveCommand.Create(() => {
            var entity = _entityManager.CreateEntity();
            var newItem = CreateHierarchyItem(entity);
            Items.Add(newItem);
        });

        AddChildItemCommand = ReactiveCommand.Create(() => {
            if (SelectedItem != null && !SelectedItem.IsLeaf)
            {
                var entity = _entityManager.CreateEntity();
                var newItem = CreateHierarchyItem(entity);
                newItem.Parent = SelectedItem;
                SelectedItem.Children.Add(newItem);
                SelectedItem.IsExpanded = true;
            }
            else if (SelectedItem == null)
            {
                var entity = _entityManager.CreateEntity();
                var newItem = CreateHierarchyItem(entity);
                Items.Add(newItem);
            }
        });

        DeleteSelectedItemCommand = ReactiveCommand.Create(() => {
            if (SelectedItem is HierarchyItemViewModel itemVm)
            {
                _entityManager.DestroyEntity(itemVm.Entity);

                if (itemVm.Parent != null)
                {
                    itemVm.Parent.Children.Remove(itemVm);
                }
                else
                {
                    Items.Remove(itemVm);
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

    private IHierarchyItem CreateHierarchyItem(Entity entity)
    {
        var vm = new HierarchyItemViewModel 
        { 
            Entity = entity,
            Name = $"Entity {entity.Id}"
        };
        vm.RequestDelete += OnItemDeletedRequest;
        return vm;
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
