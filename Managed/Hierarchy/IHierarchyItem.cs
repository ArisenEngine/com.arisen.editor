using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace ArisenEditorFramework.Hierarchy;

/// <summary>
/// Represents a generic item within a hierarchical tree structure.
/// Provides base properties for display, interaction, and tree manipulation.
/// </summary>
public interface IHierarchyItem
{
    /// <summary>
    /// Displays name of the node in the tree.
    /// </summary>
    string Name { get; set; }

    /// <summary>
    /// Optional icon to display next to the node's name.
    /// </summary>
    Bitmap? Icon { get; set; }

    /// <summary>
    /// Indicates whether the node is currently expanded to show its children.
    /// </summary>
    bool IsExpanded { get; set; }

    /// <summary>
    /// Indicates whether the node is currently selected in the UI.
    /// </summary>
    bool IsSelected { get; set; }

    /// <summary>
    /// Indicates if the node is currently in editing mode (e.g. being renamed).
    /// </summary>
    bool IsEditing { get; set; }

    /// <summary>
    /// Indicates if this item acts as a leaf node (cannot have children).
    /// </summary>
    bool IsLeaf { get; }

    /// <summary>
    /// User-defined data attached to the node for contextual use cases.
    /// </summary>
    object? Tag { get; set; }

    /// <summary>
    /// The parent item. Null if this is a root node.
    /// </summary>
    IHierarchyItem? Parent { get; set; }

    /// <summary>
    /// Collection of child items under this node.
    /// </summary>
    ObservableCollection<IHierarchyItem> Children { get; }

    /// <summary>
    /// Command to trigger renaming mode on the node.
    /// </summary>
    ICommand? BeginRenameCommand { get; }

    /// <summary>
    /// Command to finalize or cancel renaming mode.
    /// </summary>
    ICommand? EndRenameCommand { get; }

    /// <summary>
    /// Command to request deletion of this node.
    /// </summary>
    ICommand? DeleteCommand { get; }

    /// <summary>
    /// Determines if a specific item can be dropped onto this node.
    /// </summary>
    bool CanAcceptDrop(IHierarchyItem sourceItem);

    /// <summary>
    /// Handles the logic when another item is dropped onto this node.
    /// </summary>
    void AcceptDrop(IHierarchyItem sourceItem);
}
