using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System;

namespace ArisenEditorFramework.Hierarchy;

/// <summary>
/// A ReactiveUI implementation of IHierarchyItem for binding to Avalonia views.
/// Provides default behaviors for renaming, selection, and drag/drop.
/// </summary>
public class HierarchyItemViewModel : ReactiveObject, IHierarchyItem
{
    private string _name = "New Item";
    private Bitmap? _icon;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isEditing;
    private bool _isLeaf = false;
    private object? _tag;
    private IHierarchyItem? _parent;
    
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public Bitmap? Icon
    {
        get => _icon;
        set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    public bool IsLeaf
    {
        get => _isLeaf;
        protected set => this.RaiseAndSetIfChanged(ref _isLeaf, value);
    }

    public object? Tag
    {
        get => _tag;
        set => this.RaiseAndSetIfChanged(ref _tag, value);
    }

    public IHierarchyItem? Parent
    {
        get => _parent;
        set => this.RaiseAndSetIfChanged(ref _parent, value);
    }

    public ObservableCollection<IHierarchyItem> Children { get; } = new();

    public ICommand? BeginRenameCommand { get; protected set; }
    public ICommand? EndRenameCommand { get; protected set; }
    public ICommand? DeleteCommand { get; protected set; }

    /// <summary>
    /// Event fired when delete is requested so the owner/parent can handle removal.
    /// </summary>
    public event EventHandler<IHierarchyItem>? RequestDelete;

    public HierarchyItemViewModel()
    {
        BeginRenameCommand = ReactiveCommand.Create(() => IsEditing = true);
        
        EndRenameCommand = ReactiveCommand.Create(() => {
            IsEditing = false;
            OnRenamed(Name);
        });

        DeleteCommand = ReactiveCommand.Create(() => {
            RequestDelete?.Invoke(this, this);
            // Removal from parent.Children is handled by the parent's RequestDelete handler.
        });
    }

    protected virtual void OnRenamed(string newName)
    {
        // Override in derived classes to process name changes, like renaming a file on disk
    }

    public virtual bool CanAcceptDrop(IHierarchyItem sourceItem)
    {
        if (IsLeaf) return false;
        
        // Prevent dropping a node into its own children
        var currentParent = this as IHierarchyItem;
        while (currentParent != null)
        {
            if (currentParent == sourceItem)
                return false;
            currentParent = currentParent.Parent;
        }

        return true;
    }

    public virtual void AcceptDrop(IHierarchyItem sourceItem)
    {
        if (sourceItem.Parent != null)
        {
            sourceItem.Parent.Children.Remove(sourceItem);
        }
        sourceItem.Parent = this;
        Children.Add(sourceItem);
        IsExpanded = true;
    }
}
