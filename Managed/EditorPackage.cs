using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace ArisenEditor;

public class EditorPackage : IPackageEntry, IApplicationHost
{
    public void OnLoad(IServiceRegistry registry)
    {
        Console.WriteLine("[EditorPackage] Registering Arisen Editor Avalonia Host.");
        registry.RegisterService<IApplicationHost>(this);
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }

    public void Run(string[] args)
    {
        Console.WriteLine("[EditorPackage] Taking over Main Thread for Avalonia UI Loop...");
        
        // Emulate the original Program.cs startup logic
        AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI()
            .StartWithClassicDesktopLifetime(args);
    }
}
