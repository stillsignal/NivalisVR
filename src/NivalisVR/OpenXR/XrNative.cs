using System;
using System.Runtime.InteropServices;
using System.Text;

// Minimal hand-written OpenXR 1.0 bindings: only what NivalisVR needs (D3D11, stereo, one projection layer).
// Struct layouts and constants are mirrored from the official headers (openxr.h / openxr_platform.h, SDK 1.1.63).
// Handles are 64-bit on all platforms, so they are plain ulongs here.

namespace NivalisVR.OpenXR;

internal enum XrStructureType
{
    ExtensionProperties = 2,
    InstanceCreateInfo = 3,
    SystemGetInfo = 4,
    SystemProperties = 5,
    ViewLocateInfo = 6,
    View = 7,
    SessionCreateInfo = 8,
    SwapchainCreateInfo = 9,
    SessionBeginInfo = 10,
    ViewState = 11,
    FrameEndInfo = 12,
    EventDataBuffer = 16,
    EventDataInstanceLossPending = 17,
    EventDataSessionStateChanged = 18,
    InstanceProperties = 32,
    CompositionLayerProjection = 35,
    CompositionLayerQuad = 36,
    ReferenceSpaceCreateInfo = 37,
    ViewConfigurationView = 41,
    FrameState = 44,
    CompositionLayerProjectionView = 48,
    SwapchainImageWaitInfo = 56,
    GraphicsBindingD3D11KHR = 1000027000,
    SwapchainImageD3D11KHR = 1000027001,
    GraphicsRequirementsD3D11KHR = 1000027002,
}

internal enum XrSessionState
{
    Unknown = 0,
    Idle = 1,
    Ready = 2,
    Synchronized = 3,
    Visible = 4,
    Focused = 5,
    Stopping = 6,
    LossPending = 7,
    Exiting = 8,
}

internal static class XrConst
{
    public const int Success = 0;
    public const int SessionLossPending = 3;
    public const int EventUnavailable = 4;
    public const int FrameDiscarded = 9;
    public const int ErrorFormFactorUnavailable = -35;

    public const ulong ApiVersion10 = 1UL << 48; // XR_MAKE_VERSION(1, 0, 0)
    public const int FormFactorHeadMountedDisplay = 1;
    public const int ViewConfigurationPrimaryStereo = 2;
    public const int ReferenceSpaceLocal = 2;
    public const int EnvironmentBlendModeOpaque = 1;
    public const long InfiniteDuration = 0x7fffffffffffffffL;

    public const ulong SwapchainUsageColorAttachment = 0x1;
    public const ulong SwapchainUsageTransferDst = 0x10;
    public const ulong SwapchainUsageSampled = 0x20;

    public const ulong CompositionLayerBlendTextureSourceAlpha = 0x2;
    public const ulong CompositionLayerUnpremultipliedAlpha = 0x4;
    public const int EyeVisibilityBoth = 0;

    public const ulong ViewStateOrientationValid = 0x1;
    public const ulong ViewStatePositionValid = 0x2;

    public const string D3D11ExtensionName = "XR_KHR_D3D11_enable";

    // DXGI formats we accept for the eye swapchains (same type group as Unity's ARGB32 render textures).
    public const long DxgiR8G8B8A8UnormSrgb = 29;
    public const long DxgiR8G8B8A8Unorm = 28;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrApplicationInfo
{
    public fixed byte applicationName[128];
    public uint applicationVersion;
    public fixed byte engineName[128];
    public uint engineVersion;
    public ulong apiVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrInstanceCreateInfo
{
    public XrStructureType type;
    public void* next;
    public ulong createFlags;
    public XrApplicationInfo applicationInfo;
    public uint enabledApiLayerCount;
    public byte** enabledApiLayerNames;
    public uint enabledExtensionCount;
    public byte** enabledExtensionNames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrExtensionProperties
{
    public XrStructureType type;
    public void* next;
    public fixed byte extensionName[128];
    public uint extensionVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrInstanceProperties
{
    public XrStructureType type;
    public void* next;
    public ulong runtimeVersion;
    public fixed byte runtimeName[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSystemGetInfo
{
    public XrStructureType type;
    public void* next;
    public int formFactor;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSystemProperties
{
    public XrStructureType type;
    public void* next;
    public ulong systemId;
    public uint vendorId;
    public fixed byte systemName[256];
    public uint maxSwapchainImageHeight;
    public uint maxSwapchainImageWidth;
    public uint maxLayerCount;
    public uint orientationTracking;
    public uint positionTracking;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrViewConfigurationView
{
    public XrStructureType type;
    public void* next;
    public uint recommendedImageRectWidth;
    public uint maxImageRectWidth;
    public uint recommendedImageRectHeight;
    public uint maxImageRectHeight;
    public uint recommendedSwapchainSampleCount;
    public uint maxSwapchainSampleCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrGraphicsRequirementsD3D11KHR
{
    public XrStructureType type;
    public void* next;
    public uint adapterLuidLow;
    public int adapterLuidHigh;
    public int minFeatureLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrGraphicsBindingD3D11KHR
{
    public XrStructureType type;
    public void* next;
    public IntPtr device;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSessionCreateInfo
{
    public XrStructureType type;
    public void* next;
    public ulong createFlags;
    public ulong systemId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrQuaternionf
{
    public float x, y, z, w;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrVector3f
{
    public float x, y, z;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrPosef
{
    public XrQuaternionf orientation;
    public XrVector3f position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrFovf
{
    public float angleLeft, angleRight, angleUp, angleDown;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrReferenceSpaceCreateInfo
{
    public XrStructureType type;
    public void* next;
    public int referenceSpaceType;
    public XrPosef poseInReferenceSpace;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainCreateInfo
{
    public XrStructureType type;
    public void* next;
    public ulong createFlags;
    public ulong usageFlags;
    public long format;
    public uint sampleCount;
    public uint width;
    public uint height;
    public uint faceCount;
    public uint arraySize;
    public uint mipCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainImageD3D11KHR
{
    public XrStructureType type;
    public void* next;
    public IntPtr texture;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainImageWaitInfo
{
    public XrStructureType type;
    public void* next;
    public long timeout;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSessionBeginInfo
{
    public XrStructureType type;
    public void* next;
    public int primaryViewConfigurationType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrFrameState
{
    public XrStructureType type;
    public void* next;
    public long predictedDisplayTime;
    public long predictedDisplayPeriod;
    public uint shouldRender;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrViewLocateInfo
{
    public XrStructureType type;
    public void* next;
    public int viewConfigurationType;
    public long displayTime;
    public ulong space;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrViewState
{
    public XrStructureType type;
    public void* next;
    public ulong viewStateFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrView
{
    public XrStructureType type;
    public void* next;
    public XrPosef pose;
    public XrFovf fov;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrSwapchainSubImage
{
    public ulong swapchain;
    public int offsetX, offsetY;
    public int extentWidth, extentHeight;
    public uint imageArrayIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerProjectionView
{
    public XrStructureType type;
    public void* next;
    public XrPosef pose;
    public XrFovf fov;
    public XrSwapchainSubImage subImage;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerProjection
{
    public XrStructureType type;
    public void* next;
    public ulong layerFlags;
    public ulong space;
    public uint viewCount;
    public XrCompositionLayerProjectionView* views;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerQuad
{
    public XrStructureType type;
    public void* next;
    public ulong layerFlags;
    public ulong space;
    public int eyeVisibility;
    public XrSwapchainSubImage subImage;
    public XrPosef pose;
    public float width, height; // XrExtent2Df size, in metres
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrFrameEndInfo
{
    public XrStructureType type;
    public void* next;
    public long displayTime;
    public int environmentBlendMode;
    public uint layerCount;
    public void** layers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrEventDataBuffer
{
    public XrStructureType type;
    public void* next;
    public fixed byte varying[4000];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrEventDataSessionStateChanged
{
    public XrStructureType type;
    public void* next;
    public ulong session;
    public XrSessionState state;
    public long time;
}

/// <summary>OpenXR function pointers, resolved from openxr_loader.dll via xrGetInstanceProcAddr.</summary>
internal static unsafe class Xr
{
    public static delegate* unmanaged[Stdcall]<ulong, byte*, IntPtr*, int> GetInstanceProcAddr;

    // Callable before an instance exists.
    public static delegate* unmanaged[Stdcall]<byte*, uint, uint*, XrExtensionProperties*, int> EnumerateInstanceExtensionProperties;
    public static delegate* unmanaged[Stdcall]<XrInstanceCreateInfo*, ulong*, int> CreateInstance;

    // Instance-level.
    public static delegate* unmanaged[Stdcall]<ulong, int> DestroyInstance;
    public static delegate* unmanaged[Stdcall]<ulong, XrInstanceProperties*, int> GetInstanceProperties;
    public static delegate* unmanaged[Stdcall]<ulong, int, byte*, int> ResultToString;
    public static delegate* unmanaged[Stdcall]<ulong, XrSystemGetInfo*, ulong*, int> GetSystem;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, XrSystemProperties*, int> GetSystemProperties;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, XrViewConfigurationView*, int> EnumerateViewConfigurationViews;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, XrGraphicsRequirementsD3D11KHR*, int> GetD3D11GraphicsRequirementsKHR;
    public static delegate* unmanaged[Stdcall]<ulong, XrEventDataBuffer*, int> PollEvent;

    // Session-level.
    public static delegate* unmanaged[Stdcall]<ulong, XrSessionCreateInfo*, ulong*, int> CreateSession;
    public static delegate* unmanaged[Stdcall]<ulong, XrSessionBeginInfo*, int> BeginSession;
    public static delegate* unmanaged[Stdcall]<ulong, int> EndSession;
    public static delegate* unmanaged[Stdcall]<ulong, int> RequestExitSession;
    public static delegate* unmanaged[Stdcall]<ulong, XrReferenceSpaceCreateInfo*, ulong*, int> CreateReferenceSpace;
    public static delegate* unmanaged[Stdcall]<ulong, uint, uint*, long*, int> EnumerateSwapchainFormats;
    public static delegate* unmanaged[Stdcall]<ulong, XrSwapchainCreateInfo*, ulong*, int> CreateSwapchain;
    public static delegate* unmanaged[Stdcall]<ulong, int> DestroySwapchain;
    public static delegate* unmanaged[Stdcall]<ulong, uint, uint*, XrSwapchainImageD3D11KHR*, int> EnumerateSwapchainImages;
    public static delegate* unmanaged[Stdcall]<ulong, void*, uint*, int> AcquireSwapchainImage;
    public static delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageWaitInfo*, int> WaitSwapchainImage;
    public static delegate* unmanaged[Stdcall]<ulong, void*, int> ReleaseSwapchainImage;
    public static delegate* unmanaged[Stdcall]<ulong, void*, XrFrameState*, int> WaitFrame;
    public static delegate* unmanaged[Stdcall]<ulong, void*, int> BeginFrame;
    public static delegate* unmanaged[Stdcall]<ulong, XrFrameEndInfo*, int> EndFrame;
    public static delegate* unmanaged[Stdcall]<ulong, XrViewLocateInfo*, XrViewState*, uint, uint*, XrView*, int> LocateViews;

    private static IntPtr _library;

    public static void LoadLoader(string dllPath)
    {
        if (_library != IntPtr.Zero) return;
        _library = NativeLibrary.Load(dllPath);
        GetInstanceProcAddr = (delegate* unmanaged[Stdcall]<ulong, byte*, IntPtr*, int>)NativeLibrary.GetExport(_library, "xrGetInstanceProcAddr");
        EnumerateInstanceExtensionProperties = (delegate* unmanaged[Stdcall]<byte*, uint, uint*, XrExtensionProperties*, int>)Proc(0, "xrEnumerateInstanceExtensionProperties");
        CreateInstance = (delegate* unmanaged[Stdcall]<XrInstanceCreateInfo*, ulong*, int>)Proc(0, "xrCreateInstance");
    }

    public static void LoadInstanceFunctions(ulong instance)
    {
        DestroyInstance = (delegate* unmanaged[Stdcall]<ulong, int>)Proc(instance, "xrDestroyInstance");
        GetInstanceProperties = (delegate* unmanaged[Stdcall]<ulong, XrInstanceProperties*, int>)Proc(instance, "xrGetInstanceProperties");
        ResultToString = (delegate* unmanaged[Stdcall]<ulong, int, byte*, int>)Proc(instance, "xrResultToString");
        GetSystem = (delegate* unmanaged[Stdcall]<ulong, XrSystemGetInfo*, ulong*, int>)Proc(instance, "xrGetSystem");
        GetSystemProperties = (delegate* unmanaged[Stdcall]<ulong, ulong, XrSystemProperties*, int>)Proc(instance, "xrGetSystemProperties");
        EnumerateViewConfigurationViews = (delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, XrViewConfigurationView*, int>)Proc(instance, "xrEnumerateViewConfigurationViews");
        GetD3D11GraphicsRequirementsKHR = (delegate* unmanaged[Stdcall]<ulong, ulong, XrGraphicsRequirementsD3D11KHR*, int>)Proc(instance, "xrGetD3D11GraphicsRequirementsKHR");
        PollEvent = (delegate* unmanaged[Stdcall]<ulong, XrEventDataBuffer*, int>)Proc(instance, "xrPollEvent");
        CreateSession = (delegate* unmanaged[Stdcall]<ulong, XrSessionCreateInfo*, ulong*, int>)Proc(instance, "xrCreateSession");
        BeginSession = (delegate* unmanaged[Stdcall]<ulong, XrSessionBeginInfo*, int>)Proc(instance, "xrBeginSession");
        EndSession = (delegate* unmanaged[Stdcall]<ulong, int>)Proc(instance, "xrEndSession");
        RequestExitSession = (delegate* unmanaged[Stdcall]<ulong, int>)Proc(instance, "xrRequestExitSession");
        CreateReferenceSpace = (delegate* unmanaged[Stdcall]<ulong, XrReferenceSpaceCreateInfo*, ulong*, int>)Proc(instance, "xrCreateReferenceSpace");
        EnumerateSwapchainFormats = (delegate* unmanaged[Stdcall]<ulong, uint, uint*, long*, int>)Proc(instance, "xrEnumerateSwapchainFormats");
        CreateSwapchain = (delegate* unmanaged[Stdcall]<ulong, XrSwapchainCreateInfo*, ulong*, int>)Proc(instance, "xrCreateSwapchain");
        DestroySwapchain = (delegate* unmanaged[Stdcall]<ulong, int>)Proc(instance, "xrDestroySwapchain");
        EnumerateSwapchainImages = (delegate* unmanaged[Stdcall]<ulong, uint, uint*, XrSwapchainImageD3D11KHR*, int>)Proc(instance, "xrEnumerateSwapchainImages");
        AcquireSwapchainImage = (delegate* unmanaged[Stdcall]<ulong, void*, uint*, int>)Proc(instance, "xrAcquireSwapchainImage");
        WaitSwapchainImage = (delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageWaitInfo*, int>)Proc(instance, "xrWaitSwapchainImage");
        ReleaseSwapchainImage = (delegate* unmanaged[Stdcall]<ulong, void*, int>)Proc(instance, "xrReleaseSwapchainImage");
        WaitFrame = (delegate* unmanaged[Stdcall]<ulong, void*, XrFrameState*, int>)Proc(instance, "xrWaitFrame");
        BeginFrame = (delegate* unmanaged[Stdcall]<ulong, void*, int>)Proc(instance, "xrBeginFrame");
        EndFrame = (delegate* unmanaged[Stdcall]<ulong, XrFrameEndInfo*, int>)Proc(instance, "xrEndFrame");
        LocateViews = (delegate* unmanaged[Stdcall]<ulong, XrViewLocateInfo*, XrViewState*, uint, uint*, XrView*, int>)Proc(instance, "xrLocateViews");
    }

    private static IntPtr Proc(ulong instance, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        IntPtr fn;
        int result;
        fixed (byte* p = bytes)
            result = GetInstanceProcAddr(instance, p, &fn);
        if (result < 0 || fn == IntPtr.Zero)
            throw new XrException($"xrGetInstanceProcAddr({name})", result, Describe(instance, result));
        return fn;
    }

    /// <summary>Human-readable name for an XrResult (falls back to the number before an instance exists).</summary>
    public static string Describe(ulong instance, int result)
    {
        if (instance == 0 || ResultToString == null) return result.ToString();
        var buffer = stackalloc byte[64];
        return ResultToString(instance, result, buffer) >= 0 ? Utf8(buffer, 64) : result.ToString();
    }

    public static string Utf8(byte* text, int maxLength)
    {
        var length = 0;
        while (length < maxLength && text[length] != 0) length++;
        return Encoding.UTF8.GetString(text, length);
    }

    public static void WriteUtf8(byte* destination, int capacity, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var count = Math.Min(bytes.Length, capacity - 1);
        for (var i = 0; i < count; i++) destination[i] = bytes[i];
        destination[count] = 0;
    }
}

internal class XrException : Exception
{
    public int Result { get; }

    public XrException(string call, int result, string description)
        : base($"{call} failed: {description} ({result})")
    {
        Result = result;
    }
}
