namespace ArisenEditor.Core.Models;

/// <summary>
/// Represents local, user-specific editor layout and preferences for a given project.
/// This file normally resides in the `Library/` directory and is NOT version controlled.
/// </summary>
public class EditorUserSettings
{
    public bool ShowWorldPartitionOverlay { get; set; } = true;
}
