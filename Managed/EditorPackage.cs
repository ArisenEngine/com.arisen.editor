using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using System;
using System.Runtime.InteropServices;
using ArisenEditor.Core.Services;
using ArisenEditorFramework.Services;
using ArisenKernel.Diagnostics;
using Avalonia;
using Avalonia.ReactiveUI;
using ArisenEngine.Rendering;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Automation;
using ArisenEngine.Resources.Serialization;
using ArisenEditorFramework.Extensions;

namespace ArisenEditor;

public class EditorPackage : IPackageEntry, IApplicationHost
{
    private IEditorSceneDocumentService? m_SceneDocumentService;
    private IEditorWorldDocumentService? m_WorldDocumentService;
    private EditorSceneViewFocusController? m_SceneViewFocusController;
    private EditorExtensionRegistry? m_ExtensionRegistry;

    public void OnLoad(IServiceRegistry registry)
    {
        EditorLog.Initialize(new EditorLogService("editor.log"));
        EditorLog.Info("[EditorPackage] Registering Arisen Editor Avalonia Host.");

        m_ExtensionRegistry = new EditorExtensionRegistry();
        registry.RegisterService<IEditorExtensionRegistry>(m_ExtensionRegistry);

        IAssetDatabase assetDatabase = registry.GetService<IAssetDatabase>();
        m_SceneDocumentService = new EditorSceneDocumentService(
            assetDatabase,
            registry.GetService<IRuntimeSceneService>(),
            registry.GetService<ICommandManager>());
        m_SceneDocumentService.OperationFailed += OnSceneDocumentOperationFailed;
        registry.RegisterService<IEditorSceneDocumentService>(m_SceneDocumentService);
        m_WorldDocumentService = new EditorWorldDocumentService(
            assetDatabase,
            registry.GetService<IRuntimeWorldStreamingService>(),
            registry.GetService<ICommandManager>(),
            m_SceneDocumentService);
        m_WorldDocumentService.OperationFailed += OnWorldDocumentOperationFailed;
        registry.RegisterService<IEditorWorldDocumentService>(m_WorldDocumentService);
        m_SceneViewFocusController = new EditorSceneViewFocusController(
            m_WorldDocumentService,
            registry.GetService<RenderSubsystem>(),
            assetDatabase);
        registry.RegisterService<IApplicationHost>(this);
    }


    public void OnUnload(IServiceRegistry registry)
    {
        m_SceneViewFocusController?.Dispose();
        m_SceneViewFocusController = null;
        if (m_WorldDocumentService != null)
        {
            m_WorldDocumentService.OperationFailed -= OnWorldDocumentOperationFailed;
        }
        m_WorldDocumentService?.Dispose();
        m_WorldDocumentService = null;
        if (m_SceneDocumentService != null)
        {
            m_SceneDocumentService.OperationFailed -= OnSceneDocumentOperationFailed;
        }
        m_SceneDocumentService?.Dispose();
        m_SceneDocumentService = null;
        m_ExtensionRegistry = null;
    }

    private static void OnSceneDocumentOperationFailed(string diagnostic)
    {
        EditorLog.Warning($"[EditorSceneDocument] {diagnostic}");
    }

    private static void OnWorldDocumentOperationFailed(string diagnostic)
    {
        EditorLog.Warning($"[EditorWorldDocument] {diagnostic}");
    }

    public void Run(string[] args)
    {
        EditorLog.Info("[EditorPackage] Taking over Main Thread for Avalonia UI Loop...");
        if (m_ExtensionRegistry == null)
        {
            throw new InvalidOperationException(
                "[Editor.Extensions] The extension registry was not initialized before Editor startup.");
        }

        EditorExtensionRegistry extensionRegistry = m_ExtensionRegistry;
        IEditorExtension[] activeExtensions = extensionRegistry.BeginEditorActivation();
        App.SetEditorExtensions(activeExtensions, extensionRegistry.EndEditorActivation);

        try
        {
            // The editor viewport consumes Vulkan external-memory images exported by the RHI.
            // Avalonia's default Windows backend is ANGLE/D3D11, whose compositor only accepts
            // D3D11 shared textures. In that mode the viewport can never import Arisen's
            // VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32 images and remains black.
            // Force Avalonia's Win32 compositor to Vulkan first so TryGetCompositionGpuInterop()
            // exposes VulkanOpaqueNtHandle, matching the native Vulkan swapchain export.
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                builder = builder.With(new Win32PlatformOptions
                {
                    RenderingMode = new[]
                    {
                        Win32RenderingMode.Vulkan,
                        Win32RenderingMode.AngleEgl,
                        Win32RenderingMode.Software
                    }
                });
            }

            builder
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            App.ClearEditorExtensions();
        }

        EditorLog.Info("[EditorPackage] UI Loop exited. Shutting down diagnostics...");
        Logger.Dispose();
    }
}
