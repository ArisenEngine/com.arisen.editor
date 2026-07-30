using ArisenKernel.Diagnostics;

namespace ArisenEditorFramework.Extensions;

internal sealed class EditorExtensionRegistry : IEditorExtensionRegistry
{
    private readonly EditorExtensionRegistryCore<IEditorExtension> m_Core = new();

    public int Count => m_Core.Count;
    public bool IsEditorActive => m_Core.IsEditorActive;

    public void Register(IEditorExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        m_Core.Register(extension, extension.ExtensionId, extension.Order);
        KernelLog.InfoFormat(
            "[Editor.Extensions] Registered extension '{0}' at order {1}.",
            extension.ExtensionId,
            extension.Order);
    }

    public bool Unregister(IEditorExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        bool removed = m_Core.Unregister(extension, extension.ExtensionId);
        if (removed)
        {
            KernelLog.InfoFormat(
                "[Editor.Extensions] Unregistered extension '{0}'.",
                extension.ExtensionId);
        }

        return removed;
    }

    internal IEditorExtension[] BeginEditorActivation()
    {
        var activeExtensions = m_Core.BeginEditorActivation();
        KernelLog.InfoFormat(
            "[Editor.Extensions] Froze {0} extension(s) for Editor activation.",
            activeExtensions.Length);
        return activeExtensions;
    }

    internal void EndEditorActivation()
    {
        m_Core.EndEditorActivation();
        KernelLog.Info("[Editor.Extensions] Editor activation ended.");
    }
}
