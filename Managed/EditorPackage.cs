using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using System;
using ArisenEditor.Core.Services;
using ArisenEditorFramework.Services;
using ArisenKernel.Diagnostics;
using Avalonia;
using Avalonia.ReactiveUI;

namespace ArisenEditor;

public class EditorPackage : IPackageEntry, IApplicationHost
{
    public void OnLoad(IServiceRegistry registry)
    {
        EditorLog.Initialize(new EditorLogService("editor.log"));
        EditorLog.Info("[EditorPackage] Registering Arisen Editor Avalonia Host.");
        registry.RegisterService<IApplicationHost>(this);
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }

    public void Run(string[] args)
    {
        EditorLog.Info("[EditorPackage] Taking over Main Thread for Avalonia UI Loop...");
        
        // Emulate the original Program.cs startup logic
        AppBuilder.Configure<App>()
            .UsePlatformDetect() // Essential for desktop platform services
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI()
            .StartWithClassicDesktopLifetime(args);
    }
}
