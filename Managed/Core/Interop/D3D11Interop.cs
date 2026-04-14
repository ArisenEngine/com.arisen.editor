using System;
using System.Runtime.InteropServices;
using ArisenEditor.Core.Services;
using ArisenKernel.Diagnostics;

namespace ArisenEditor.Core.Interop;

public class D3D11Interop : IDisposable
{
    private static D3D11Interop? s_Instance;
    public static D3D11Interop Instance => s_Instance ??= new D3D11Interop();

    private IntPtr m_DevicePtr;
    private IntPtr m_ContextPtr;
    private D3D11Native.ID3D11Device? m_Device;

    private D3D11Interop()
    {
        InitializeDevice();
    }

    private void InitializeDevice()
    {
        uint[] featureLevels = { 0xb000, 0xb100 }; // 11.0, 11.1
        int hr = D3D11Native.D3D11CreateDevice(
            IntPtr.Zero,
            D3D11Native.D3D11_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            D3D11Native.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            featureLevels,
            (uint)featureLevels.Length,
            D3D11Native.D3D11_SDK_VERSION,
            out m_DevicePtr,
            out _,
            out m_ContextPtr);

        if (hr < 0)
        {
            EditorLog.Error($"[D3D11Interop] Failed to create D3D11 Device. HR: 0x{hr:X}");
            return;
        }

        m_Device = (D3D11Native.ID3D11Device)Marshal.GetObjectForIUnknown(m_DevicePtr);
        EditorLog.Info("[D3D11Interop] D3D11 Device initialized for interop.");
    }

    public IntPtr OpenSharedTexture(IntPtr sharedHandle)
    {
        if (m_Device == null || sharedHandle == IntPtr.Zero) return IntPtr.Zero;

        Guid iid = D3D11Native.IID_ID3D11Texture2D;
        int hr = m_Device.OpenSharedResource(sharedHandle, ref iid, out IntPtr texturePtr);

        if (hr < 0)
        {
            EditorLog.Error($"[D3D11Interop] Failed to open shared resource 0x{sharedHandle:X}. HR: 0x{hr:X}");
            return IntPtr.Zero;
        }

        return texturePtr;
    }

    public void ReleaseTexture(IntPtr texturePtr)
    {
        if (texturePtr != IntPtr.Zero)
        {
            Marshal.Release(texturePtr);
        }
    }

    public void Dispose()
    {
        if (m_Device != null)
        {
            Marshal.ReleaseComObject(m_Device);
            m_Device = null;
        }

        if (m_ContextPtr != IntPtr.Zero)
        {
            Marshal.Release(m_ContextPtr);
            m_ContextPtr = IntPtr.Zero;
        }

        if (m_DevicePtr != IntPtr.Zero)
        {
            Marshal.Release(m_DevicePtr);
            m_DevicePtr = IntPtr.Zero;
        }
    }
}
