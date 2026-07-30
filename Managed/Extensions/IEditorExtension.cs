namespace ArisenEditorFramework.Extensions;

/// <summary>
/// Setup-only contribution from an optional Editor package.
/// </summary>
public interface IEditorExtension
{
    string ExtensionId { get; }
    int Order { get; }

    void Configure(IEditorExtensionContext context);
}
