using System;
using System.Text.Json.Serialization;

namespace ArisenEditorFramework.Core;

public class ProjectMetadataBase
{
    public string Name { get; set; } = "New Project";
    public string Description { get; set; } = string.Empty;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    
    [JsonIgnore]
    public string ProjectPath { get; set; } = string.Empty; // Full path to .arisenproj
    
    // UI Metadata
    public string PreviewImageURL { get; set; } = string.Empty;
    public string IconURL { get; set; } = string.Empty;
}
