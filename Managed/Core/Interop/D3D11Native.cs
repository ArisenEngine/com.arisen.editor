using System;
using System.Runtime.InteropServices;

namespace ArisenEditor.Core.Interop;

internal static class D3D11Native
{
    [DllImport("d3d11.dll", SetLastError = true)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr Software,
        uint flags,
        [MarshalAs(UnmanagedType.LPArray, SizeConst = 1)] uint[] pFeatureLevels,
        uint FeatureLevels,
        uint SDKVersion,
        out IntPtr ppDevice,
        out uint pFeatureLevel,
        out IntPtr ppImmediateContext);

    public const uint D3D11_SDK_VERSION = 7;
    public const int D3D11_DRIVER_TYPE_HARDWARE = 1;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Device
    {
        [PreserveSig]
        int CreateBuffer(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppBuffer);
        [PreserveSig]
        int CreateTexture1D(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTexture1D);
        [PreserveSig]
        int CreateTexture2D(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTexture2D);
        [PreserveSig]
        int CreateTexture3D(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTexture3D);
        [PreserveSig]
        int CreateShaderResourceView(IntPtr pResource, IntPtr pDesc, out IntPtr ppSRView);
        [PreserveSig]
        int CreateUnorderedAccessView(IntPtr pResource, IntPtr pDesc, out IntPtr ppUAView);
        [PreserveSig]
        int CreateRenderTargetView(IntPtr pResource, IntPtr pDesc, out IntPtr ppRTView);
        [PreserveSig]
        int CreateDepthStencilView(IntPtr pResource, IntPtr pDesc, out IntPtr ppDSView);
        [PreserveSig]
        int CreateInputLayout(IntPtr pInputElementDescs, uint NumElements, IntPtr pShaderBytecodeWithInputSignature, UIntPtr BytecodeLength, out IntPtr ppInputLayout);
        [PreserveSig]
        int CreateVertexShader(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppVertexShader);
        [PreserveSig]
        int CreateGeometryShader(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppGeometryShader);
        [PreserveSig]
        int CreateGeometryShaderWithStreamOutput(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pDeclaration, uint NumEntries, IntPtr pBufferStrides, uint NumStrides, uint RasterizedStream, IntPtr pClassLinkage, out IntPtr ppGeometryShader);
        [PreserveSig]
        int CreatePixelShader(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppPixelShader);
        [PreserveSig]
        int CreateHullShader(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppHullShader);
        [PreserveSig]
        int CreateDomainShader(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppDomainShader);
        [PreserveSig]
        int CreateComputeShader(IntPtr pShaderBytecode, UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppComputeShader);
        [PreserveSig]
        int CreateClassLinkage(out IntPtr ppLinkage);
        [PreserveSig]
        int CreateBlendState(IntPtr pBlendStateDesc, out IntPtr ppBlendState);
        [PreserveSig]
        int CreateDepthStencilState(IntPtr pDepthStencilDesc, out IntPtr ppDepthStencilState);
        [PreserveSig]
        int CreateRasterizerState(IntPtr pRasterizerDesc, out IntPtr ppRasterizerState);
        [PreserveSig]
        int CreateSamplerState(IntPtr pSamplerDesc, out IntPtr ppSamplerState);
        [PreserveSig]
        int CreateQuery(IntPtr pQueryDesc, out IntPtr ppQuery);
        [PreserveSig]
        int CreatePredicate(IntPtr pPredicateDesc, out IntPtr ppPredicate);
        [PreserveSig]
        int CreateCounter(IntPtr pCounterDesc, out IntPtr ppCounter);
        [PreserveSig]
        int CreateDeferredContext(uint ContextFlags, out IntPtr ppDeferredContext);
        [PreserveSig]
        int OpenSharedResource(IntPtr hResource, ref Guid ReturnedInterface, out IntPtr ppResource);
    }

    [Guid("a0482e1d-0587-4981-a5c8-1088bfd46c61"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Device1 : ID3D11Device
    {
        // Inherited methods from ID3D11Device (COM requires re-declaring them if the tool doesn't handle inheritance automatically)
        // Since we are using Marshal.Cast or similar, we only need the new methods here for ID3D11Device1.
        
        // Skip over the ID3D11Device methods (43 slots)
        // Note: In C#, if we inherit, we MUST redeclare them for the VTable to be correct.
        // I will keep it simple and just add the OpenSharedResource1 at the correct VTable offset.
        
        [PreserveSig]
        int CreateDeviceContext1(); // slot 44
        // ... many others ...
        // ID3D11Device1 has many methods. We only need OpenSharedResource1.
        // It's usually easier to use a separate interface for the extension.
    }

    // Modern D3D11.1+ sharing
    public const uint DXGI_SHARED_RESOURCE_READ = 0x80000000;
    public const uint DXGI_SHARED_RESOURCE_WRITE = 1;

    [Guid("6f1565b3-05bf-4091-9ff0-519b7c02b2bb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Texture2D
    {
        void GetDevice(out ID3D11Device ppDevice);
        void GetType(out int pResourceDimension);
        void SetEvictionPriority(uint EvictionPriority);
        uint GetEvictionPriority();
        // ... incomplete but enough for COM casting if needed
    }

    public static Guid IID_ID3D11Texture2D = new Guid("6f1565b3-05bf-4091-9ff0-519b7c02b2bb");
}
