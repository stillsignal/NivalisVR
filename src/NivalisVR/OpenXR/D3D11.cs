using System;
using System.Runtime.InteropServices;

namespace NivalisVR.OpenXR;

/// <summary>
/// The handful of Direct3D 11 COM calls we need, made directly through the vtables
/// (indices from d3d11.h: IUnknown 0-2, ID3D11DeviceChild 3-6, then the interface's own methods).
/// </summary>
internal static unsafe class D3D11
{
    // IUnknown
    private const int IUnknownRelease = 2;
    // ID3D11DeviceChild
    private const int DeviceChildGetDevice = 3;
    // ID3D11Device
    private const int DeviceGetFeatureLevel = 37;
    private const int DeviceGetImmediateContext = 40;
    // ID3D11Texture2D (after ID3D11Resource's GetType 7, SetEvictionPriority 8, GetEvictionPriority 9)
    private const int Texture2DGetDesc = 10;
    // ID3D11DeviceContext
    private const int ContextCopyResource = 47;

    private static void** Vtbl(IntPtr comObject) => *(void***)comObject;

    /// <summary>Returns the device that created a resource (AddRef'd).</summary>
    public static IntPtr GetDevice(IntPtr deviceChild)
    {
        IntPtr device;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(deviceChild)[DeviceChildGetDevice])(deviceChild, &device);
        return device;
    }

    /// <summary>Returns the device's immediate context (AddRef'd).</summary>
    public static IntPtr GetImmediateContext(IntPtr device)
    {
        IntPtr context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(device)[DeviceGetImmediateContext])(device, &context);
        return context;
    }

    public static int GetFeatureLevel(IntPtr device)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(device)[DeviceGetFeatureLevel])(device);

    public static uint Release(IntPtr comObject)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(comObject)[IUnknownRelease])(comObject);

    /// <summary>Must only be called on Unity's render thread (the immediate context is not thread-safe).</summary>
    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(context)[ContextCopyResource])(context, destination, source);

    public static Texture2DDesc GetDesc(IntPtr texture2D)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Vtbl(texture2D)[Texture2DGetDesc])(texture2D, &desc);
        return desc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public uint Format; // DXGI_FORMAT
        public uint SampleCount, SampleQuality;
        public uint Usage, BindFlags, CpuAccessFlags, MiscFlags;

        public override string ToString()
            => $"{Width}x{Height} fmt={Format} mips={MipLevels} array={ArraySize} samples={SampleCount} bind=0x{BindFlags:X} misc=0x{MiscFlags:X}";
    }
}
