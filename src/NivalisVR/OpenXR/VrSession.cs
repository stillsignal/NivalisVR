using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NivalisVR.OpenXR;

/// <summary>
/// Owns the OpenXR instance/session/swapchains.
///
/// Threading model:
///  - Main thread: Initialize, PollEvents, WaitAndLocate (xrWaitFrame + xrLocateViews), eye rendering.
///  - Render thread (via GL.IssuePluginEvent): xrBeginFrame, swapchain acquire/copy/release, xrEndFrame, xrEndSession,
///    and xrDestroyInstance when VR is switched off (RequestStop), so it runs after every frame already queued.
///    Everything that touches the D3D11 immediate context runs there, in order with Unity's own rendering commands.
///    OpenXR explicitly allows xrBeginFrame/xrEndFrame on a different thread from xrWaitFrame.
/// The render-thread callback must never touch IL2CPP/Unity objects; it only uses the plain fields below.
/// </summary>
internal static unsafe class VrSession
{
    public struct FrameSlot
    {
        public long DisplayTime;
        public bool ShouldRender;
        public XrView Left, Right;

        // Optional UI panel (quad layer), in LOCAL space.
        public bool UiVisible;
        public XrPosef UiPose;
        public float UiWidth, UiHeight;
    }

    private const int SlotCount = 4;
    private const int EventSubmitBase = 1;           // 1..SlotCount: submit that slot
    private const int EventEndSession = 100;
    private const int EventDestroy = 101;

    public static bool IsInitialized { get; private set; }
    public static bool IsRunning => _sessionRunning;  // xrBeginSession done and not stopping
    public static int EyeWidth { get; private set; }
    public static int EyeHeight { get; private set; }
    public static float RenderScale { get; private set; }
    public static XrSessionState State { get; private set; }

    /// <summary>True when the eye swapchains are new (start or resolution change) and need SetSourceTextures.</summary>
    public static bool NeedsSourceTextures => IsInitialized && (_pendingEyes != null || _eyes == null);

    private static ulong _instance, _system, _session, _localSpace;
    private static IntPtr _device, _context;
    private static long _swapchainFormat;
    private static uint _recommendedWidth, _recommendedHeight, _maxWidth, _maxHeight;

    // Eye swapchains and the textures copied into them, swapped as one reference so a resolution change can't
    // tear: the render thread reads _eyes once per frame. New swapchains wait in _pendingEyes (main thread only)
    // until the renderer hands in textures of the new size.
    private sealed class EyeTargets
    {
        public ulong[] Swapchains;
        public IntPtr[][] Images;
        public IntPtr[] Sources;
        public int Width, Height;
    }

    private static volatile EyeTargets _eyes;
    private static EyeTargets _pendingEyes;

    // UI panel: everything the render thread needs, swapped as one reference so a resize can't tear it.
    private sealed class UiTarget
    {
        public ulong Swapchain;
        public IntPtr[] Images;
        public IntPtr Source;
        public int Width, Height;
    }

    private static volatile UiTarget _uiTarget;
    // Replaced swapchains, destroyed on the render thread (the only thread that uses them).
    private static readonly ConcurrentQueue<ulong> RetiredSwapchains = new();

    private static volatile bool _sessionRunning;

    // Switching VR off (Shift+F11): exit requested -> STOPPING (xrEndSession) -> IDLE -> destroy on the render thread
    // -> FinishShutdown on the main thread. The main thread makes no OpenXR calls while _shuttingDown.
    private static volatile bool _exitRequested;
    private static volatile bool _shuttingDown;
    private static volatile bool _shutdownDone;
    private static volatile bool _rtSessionEnded; // render thread: no frames may be submitted (ended or destroyed)
    private static readonly FrameSlot[] Slots = new FrameSlot[SlotCount];
    private static int _nextSlot;
    private static IntPtr _renderEventFunc;

    // Render-thread diagnostics, drained to the BepInEx log on the main thread.
    private static readonly ConcurrentQueue<string> RenderThreadMessages = new();
    private static int _renderThreadErrorCount;
    private static int _framesSubmitted;
    private static bool _loggedFirstSubmit;
    private static bool _loggedFirstUiSubmit;

    public static int FramesSubmitted => _framesSubmitted;

    // ---------------------------------------------------------------- initialization (main thread)

    public static void Initialize(float renderScale)
    {
        if (IsInitialized) return;
        var log = Plugin.Logger;
        _rtSessionEnded = false; // nothing is queued on the render thread before the session begins

        try
        {
            var pluginDir = Path.GetDirectoryName(typeof(VrSession).Assembly.Location)!;
            var loaderPath = Path.Combine(pluginDir, "openxr_loader.dll");
            log.LogInfo($"OpenXR: loading {loaderPath}");
            Xr.LoadLoader(loaderPath);

            RequireExtension(XrConst.D3D11ExtensionName);
            CreateInstance();
            Xr.LoadInstanceFunctions(_instance);
            LogInstanceProperties();
            GetSystem();
            var view = GetViewConfiguration();
            CheckGraphicsRequirements();
            AcquireUnityDevice();
            CreateSession();
            CreateLocalSpace();
            ChooseSwapchainFormat();

            _recommendedWidth = view.recommendedImageRectWidth;
            _recommendedHeight = view.recommendedImageRectHeight;
            _maxWidth = view.maxImageRectWidth;
            _maxHeight = view.maxImageRectHeight;
            CreateEyeSwapchains(renderScale);

            _renderEventFunc = (IntPtr)(delegate* unmanaged[Stdcall]<int, void>)&OnRenderEvent;
            IsInitialized = true;
            log.LogInfo($"OpenXR: initialized, eye swapchains {EyeWidth}x{EyeHeight} (render scale {renderScale}), waiting for session READY");
        }
        catch (Exception e)
        {
            log.LogError($"OpenXR: initialization failed: {e.Message}");
            if (e is not XrException) log.LogError(e.ToString());
            Shutdown();
        }
    }

    private static void Check(int result, string call)
    {
        if (result < 0) throw new XrException(call, result, Xr.Describe(_instance, result));
    }

    private static void RequireExtension(string name)
    {
        uint count;
        Check(Xr.EnumerateInstanceExtensionProperties(null, 0, &count, null), "xrEnumerateInstanceExtensionProperties");
        var props = new XrExtensionProperties[count];
        for (var i = 0; i < count; i++) props[i].type = XrStructureType.ExtensionProperties;
        fixed (XrExtensionProperties* p = props)
            Check(Xr.EnumerateInstanceExtensionProperties(null, count, &count, p), "xrEnumerateInstanceExtensionProperties");

        var found = false;
        for (var i = 0; i < count; i++)
        {
            fixed (byte* n = props[i].extensionName)
                if (Xr.Utf8(n, 128) == name) found = true;
        }
        Plugin.Logger.LogInfo($"OpenXR: runtime offers {count} instance extensions; {name} {(found ? "available" : "MISSING")}");
        if (!found) throw new Exception($"The active OpenXR runtime does not support {name}");
    }

    private static void CreateInstance()
    {
        var extensionName = Marshal.StringToHGlobalAnsi(XrConst.D3D11ExtensionName);
        try
        {
            var extensions = stackalloc byte*[1];
            extensions[0] = (byte*)extensionName;

            var info = new XrInstanceCreateInfo
            {
                type = XrStructureType.InstanceCreateInfo,
                enabledExtensionCount = 1,
                enabledExtensionNames = extensions,
            };
            Xr.WriteUtf8(info.applicationInfo.applicationName, 128, "Nivalis Nights VR");
            info.applicationInfo.applicationVersion = 1;
            Xr.WriteUtf8(info.applicationInfo.engineName, 128, "Unity");
            info.applicationInfo.engineVersion = 2020;
            info.applicationInfo.apiVersion = XrConst.ApiVersion10;

            ulong instance;
            Check(Xr.CreateInstance(&info, &instance), "xrCreateInstance");
            _instance = instance;
        }
        finally
        {
            Marshal.FreeHGlobal(extensionName);
        }
    }

    private static void LogInstanceProperties()
    {
        var props = new XrInstanceProperties { type = XrStructureType.InstanceProperties };
        Check(Xr.GetInstanceProperties(_instance, &props), "xrGetInstanceProperties");
        var v = props.runtimeVersion;
        Plugin.Logger.LogInfo($"OpenXR: runtime '{Xr.Utf8(props.runtimeName, 128)}' version {v >> 48}.{(v >> 32) & 0xFFFF}.{v & 0xFFFFFFFF}");
    }

    private static void GetSystem()
    {
        var info = new XrSystemGetInfo { type = XrStructureType.SystemGetInfo, formFactor = XrConst.FormFactorHeadMountedDisplay };
        ulong system;
        var result = Xr.GetSystem(_instance, &info, &system);
        if (result == XrConst.ErrorFormFactorUnavailable)
            throw new XrException("xrGetSystem", result, "headset not available (is SteamVR running and the headset connected?)");
        Check(result, "xrGetSystem");
        _system = system;

        var props = new XrSystemProperties { type = XrStructureType.SystemProperties };
        Check(Xr.GetSystemProperties(_instance, _system, &props), "xrGetSystemProperties");
        Plugin.Logger.LogInfo($"OpenXR: system '{Xr.Utf8(props.systemName, 256)}' vendor 0x{props.vendorId:X}, max swapchain {props.maxSwapchainImageWidth}x{props.maxSwapchainImageHeight}, " +
                              $"tracking orientation={props.orientationTracking != 0} position={props.positionTracking != 0}");
    }

    private static XrViewConfigurationView GetViewConfiguration()
    {
        uint count;
        Check(Xr.EnumerateViewConfigurationViews(_instance, _system, XrConst.ViewConfigurationPrimaryStereo, 0, &count, null), "xrEnumerateViewConfigurationViews");
        if (count != 2) throw new Exception($"Expected 2 stereo views, runtime reports {count}");
        var views = stackalloc XrViewConfigurationView[2];
        views[0].type = views[1].type = XrStructureType.ViewConfigurationView;
        Check(Xr.EnumerateViewConfigurationViews(_instance, _system, XrConst.ViewConfigurationPrimaryStereo, 2, &count, views), "xrEnumerateViewConfigurationViews");
        for (var i = 0; i < 2; i++)
            Plugin.Logger.LogInfo($"OpenXR: view {i} recommended {views[i].recommendedImageRectWidth}x{views[i].recommendedImageRectHeight}, max {views[i].maxImageRectWidth}x{views[i].maxImageRectHeight}");
        return views[0];
    }

    private static void CheckGraphicsRequirements()
    {
        var req = new XrGraphicsRequirementsD3D11KHR { type = XrStructureType.GraphicsRequirementsD3D11KHR };
        Check(Xr.GetD3D11GraphicsRequirementsKHR(_instance, _system, &req), "xrGetD3D11GraphicsRequirementsKHR");
        Plugin.Logger.LogInfo($"OpenXR: D3D11 requirements adapter LUID {req.adapterLuidHigh:X8}:{req.adapterLuidLow:X8}, min feature level 0x{req.minFeatureLevel:X}");
    }

    private static void AcquireUnityDevice()
    {
        // Any Unity texture's native pointer is an ID3D11Resource created on Unity's device.
        var probe = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32);
        probe.Create();
        var nativeTexture = probe.GetNativeTexturePtr();
        _device = D3D11.GetDevice(nativeTexture);
        probe.Release();
        UnityEngine.Object.Destroy(probe);

        if (_device == IntPtr.Zero) throw new Exception("Could not get Unity's D3D11 device");
        _context = D3D11.GetImmediateContext(_device);
        Plugin.Logger.LogInfo($"OpenXR: Unity D3D11 device 0x{_device.ToInt64():X}, feature level 0x{D3D11.GetFeatureLevel(_device):X}, color space {QualitySettings.activeColorSpace}");
    }

    private static void CreateSession()
    {
        var binding = new XrGraphicsBindingD3D11KHR { type = XrStructureType.GraphicsBindingD3D11KHR, device = _device };
        var info = new XrSessionCreateInfo { type = XrStructureType.SessionCreateInfo, next = &binding, systemId = _system };
        ulong session;
        Check(Xr.CreateSession(_instance, &info, &session), "xrCreateSession");
        _session = session;
        Plugin.Logger.LogInfo("OpenXR: session created");
    }

    private static void CreateLocalSpace()
    {
        var info = new XrReferenceSpaceCreateInfo
        {
            type = XrStructureType.ReferenceSpaceCreateInfo,
            referenceSpaceType = XrConst.ReferenceSpaceLocal,
            poseInReferenceSpace = new XrPosef { orientation = new XrQuaternionf { w = 1 } },
        };
        ulong space;
        Check(Xr.CreateReferenceSpace(_session, &info, &space), "xrCreateReferenceSpace(LOCAL)");
        _localSpace = space;
    }

    private static void ChooseSwapchainFormat()
    {
        uint count;
        Check(Xr.EnumerateSwapchainFormats(_session, 0, &count, null), "xrEnumerateSwapchainFormats");
        var formats = new long[count];
        fixed (long* p = formats)
            Check(Xr.EnumerateSwapchainFormats(_session, count, &count, p), "xrEnumerateSwapchainFormats");

        Plugin.Logger.LogInfo($"OpenXR: swapchain formats offered (DXGI): {string.Join(", ", formats)}");
        if (Array.IndexOf(formats, XrConst.DxgiR8G8B8A8UnormSrgb) >= 0) _swapchainFormat = XrConst.DxgiR8G8B8A8UnormSrgb;
        else if (Array.IndexOf(formats, XrConst.DxgiR8G8B8A8Unorm) >= 0) _swapchainFormat = XrConst.DxgiR8G8B8A8Unorm;
        else throw new Exception("Runtime offers no R8G8B8A8 swapchain format");
        Plugin.Logger.LogInfo($"OpenXR: using swapchain format {_swapchainFormat}");
    }

    private static (int Width, int Height) EyeSizeFor(float renderScale) =>
        (Math.Clamp((int)Math.Round(_recommendedWidth * renderScale), 64, (int)_maxWidth),
         Math.Clamp((int)Math.Round(_recommendedHeight * renderScale), 64, (int)_maxHeight));

    /// <summary>Creates both eye swapchains for this scale as the pending set (main thread).</summary>
    private static void CreateEyeSwapchains(float renderScale)
    {
        var (width, height) = EyeSizeFor(renderScale);
        var targets = new EyeTargets { Swapchains = new ulong[2], Images = new IntPtr[2][], Width = width, Height = height };
        try
        {
            for (var eye = 0; eye < 2; eye++)
                targets.Swapchains[eye] = CreateColorSwapchain(width, height, $"eye {eye}", out targets.Images[eye]);
        }
        catch
        {
            foreach (var swapchain in targets.Swapchains)
                if (swapchain != 0) Xr.DestroySwapchain(swapchain);
            throw;
        }

        // A pending set the render thread never saw (two changes within one frame) can go right away.
        if (_pendingEyes != null)
            foreach (var swapchain in _pendingEyes.Swapchains) Xr.DestroySwapchain(swapchain);
        _pendingEyes = targets;
        EyeWidth = width;
        EyeHeight = height;
        RenderScale = renderScale;
    }

    /// <summary>
    /// Changes the eye resolution while VR runs (main thread). The new swapchains take over once the renderer hands
    /// in textures of the new size (SetSourceTextures); the old ones are then destroyed on the render thread.
    /// </summary>
    public static bool SetRenderScale(float renderScale)
    {
        if (!IsInitialized || _shuttingDown) return false;
        var (width, height) = EyeSizeFor(renderScale);
        if (width == EyeWidth && height == EyeHeight)
        {
            RenderScale = renderScale;
            return true;
        }

        try
        {
            CreateEyeSwapchains(renderScale);
            Plugin.Logger.LogInfo($"OpenXR: eye resolution changed to {width}x{height} (render scale {renderScale:F2})");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"OpenXR: changing the eye resolution failed, keeping {EyeWidth}x{EyeHeight}: {e.Message}");
            return false;
        }
    }

    private static ulong CreateColorSwapchain(int width, int height, string label, out IntPtr[] textures)
    {
        var info = new XrSwapchainCreateInfo
        {
            type = XrStructureType.SwapchainCreateInfo,
            usageFlags = XrConst.SwapchainUsageColorAttachment | XrConst.SwapchainUsageTransferDst,
            format = _swapchainFormat,
            sampleCount = 1,
            width = (uint)width,
            height = (uint)height,
            faceCount = 1,
            arraySize = 1,
            mipCount = 1,
        };
        ulong swapchain;
        Check(Xr.CreateSwapchain(_session, &info, &swapchain), $"xrCreateSwapchain({label})");

        uint count;
        Check(Xr.EnumerateSwapchainImages(swapchain, 0, &count, null), "xrEnumerateSwapchainImages");
        var images = new XrSwapchainImageD3D11KHR[count];
        for (var i = 0; i < count; i++) images[i].type = XrStructureType.SwapchainImageD3D11KHR;
        fixed (XrSwapchainImageD3D11KHR* p = images)
            Check(Xr.EnumerateSwapchainImages(swapchain, count, &count, p), "xrEnumerateSwapchainImages");

        textures = new IntPtr[count];
        for (var i = 0; i < count; i++) textures[i] = images[i].texture;
        Plugin.Logger.LogInfo($"OpenXR: {label} swapchain has {count} images; image 0 = {D3D11.GetDesc(textures[0])}");
        return swapchain;
    }

    /// <summary>
    /// Sets the native texture copied into the UI panel each frame (main thread). Creates the panel swapchain,
    /// or replaces it if the size changed. The source must be width x height and R8G8B8A8 like the eye textures.
    /// </summary>
    public static bool SetUiSource(IntPtr texture, int width, int height)
    {
        if (!IsInitialized || _shuttingDown) return false;

        try
        {
            var current = _uiTarget;
            if (current != null && current.Width == width && current.Height == height)
            {
                _uiTarget = new UiTarget { Swapchain = current.Swapchain, Images = current.Images, Source = texture, Width = width, Height = height };
            }
            else
            {
                var swapchain = CreateColorSwapchain(width, height, "UI panel", out var images);
                _uiTarget = new UiTarget { Swapchain = swapchain, Images = images, Source = texture, Width = width, Height = height };
                if (current != null) RetiredSwapchains.Enqueue(current.Swapchain);
            }
            Plugin.Logger.LogInfo($"OpenXR: UI source texture = {D3D11.GetDesc(texture)}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"OpenXR: UI panel setup failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Native ID3D11Texture2D pointers of the RenderTextures to copy into the swapchains (must match EyeWidth x EyeHeight).
    /// Also switches over to swapchains created by a resolution change. Getting a native pointer waits for Unity's render
    /// thread, so every frame queued before this call has already been submitted with the previous set.
    /// </summary>
    public static void SetSourceTextures(IntPtr left, IntPtr right)
    {
        if (_shuttingDown) return;
        var current = _eyes;
        var swapchains = _pendingEyes ?? current;
        if (swapchains == null) return;

        _eyes = new EyeTargets
        {
            Swapchains = swapchains.Swapchains,
            Images = swapchains.Images,
            Sources = new[] { left, right },
            Width = swapchains.Width,
            Height = swapchains.Height,
        };
        if (_pendingEyes != null && current != null)
            foreach (var swapchain in current.Swapchains) RetiredSwapchains.Enqueue(swapchain);
        _pendingEyes = null;
        Plugin.Logger.LogInfo($"OpenXR: source eye textures left = {D3D11.GetDesc(left)}, right = {D3D11.GetDesc(right)}");
    }

    public static void Shutdown()
    {
        _sessionRunning = false;
        if (_instance != 0 && Xr.DestroyInstance != null)
            Xr.DestroyInstance(_instance); // destroys the session, spaces and swapchains too
        _instance = _system = _session = _localSpace = 0;
        _uiTarget = null;
        _eyes = null;
        _pendingEyes = null;
        if (_context != IntPtr.Zero) D3D11.Release(_context);
        if (_device != IntPtr.Zero) D3D11.Release(_device);
        _context = _device = IntPtr.Zero;
        IsInitialized = false;
    }

    // ---------------------------------------------------------------- per frame (main thread)

    public static void PollEvents()
    {
        if (_shuttingDown)
        {
            DrainRenderThreadMessages();
            if (_shutdownDone) FinishShutdown();
            return;
        }
        if (_instance == 0) return;

        while (true)
        {
            var buffer = new XrEventDataBuffer { type = XrStructureType.EventDataBuffer };
            var result = Xr.PollEvent(_instance, &buffer);
            if (result == XrConst.EventUnavailable) break;
            if (result < 0)
            {
                Plugin.Logger.LogError($"OpenXR: xrPollEvent failed: {Xr.Describe(_instance, result)}");
                break;
            }

            switch (buffer.type)
            {
                case XrStructureType.EventDataSessionStateChanged:
                    var changed = (XrEventDataSessionStateChanged*)&buffer;
                    OnSessionStateChanged(changed->state);
                    break;
                case XrStructureType.EventDataInstanceLossPending:
                    Plugin.Logger.LogWarning("OpenXR: instance loss pending (runtime shutting down)");
                    StopFrames();
                    break;
            }
        }

        DrainRenderThreadMessages();
    }

    private static void OnSessionStateChanged(XrSessionState state)
    {
        State = state;
        Plugin.Logger.LogInfo($"OpenXR: session state -> {state}");

        switch (state)
        {
            case XrSessionState.Ready:
                _rtSessionEnded = false; // the previous session's xrEndSession has run by now
                var info = new XrSessionBeginInfo { type = XrStructureType.SessionBeginInfo, primaryViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo };
                var result = Xr.BeginSession(_session, &info);
                if (result < 0)
                {
                    Plugin.Logger.LogError($"OpenXR: xrBeginSession failed: {Xr.Describe(_instance, result)}");
                    return;
                }
                _sessionRunning = true;
                Plugin.Logger.LogInfo("OpenXR: session running");
                break;

            case XrSessionState.Stopping:
                StopFrames();
                break;

            case XrSessionState.Idle:
                // After our own exit request the session is ended now; without one the runtime may start it again.
                if (_exitRequested) BeginShutdown();
                break;

            case XrSessionState.LossPending:
            case XrSessionState.Exiting:
                BeginShutdown();
                break;
        }
    }

    /// <summary>
    /// Switches VR off while the game keeps running (main thread). Asks the runtime to end the session; the rest
    /// follows from the state changes. F11 (Initialize) starts VR again once IsInitialized is false.
    /// </summary>
    public static void RequestStop()
    {
        if (!IsInitialized || _shuttingDown || _exitRequested) return;

        if (_sessionRunning)
        {
            var result = Xr.RequestExitSession(_session);
            if (result >= 0)
            {
                _exitRequested = true;
                Plugin.Logger.LogInfo("OpenXR: leaving VR (exit requested)");
                return;
            }
            Plugin.Logger.LogWarning($"OpenXR: xrRequestExitSession failed ({Xr.Describe(_instance, result)}); shutting down directly");
        }
        BeginShutdown();
    }

    /// <summary>Ends the session (if running) and destroys the instance on the render thread, after queued frames.</summary>
    private static void BeginShutdown()
    {
        if (_shuttingDown) return;
        StopFrames(); // queues xrEndSession if the session is still running
        _sessionRunning = false;
        _shuttingDown = true;
        _shutdownDone = false;
        GL.IssuePluginEvent(_renderEventFunc, EventDestroy);
        Plugin.Logger.LogInfo("OpenXR: shutting down the VR session");
    }

    // Render thread.
    private static void DestroyOnRenderThread()
    {
        _rtSessionEnded = true;
        while (RetiredSwapchains.TryDequeue(out _)) { } // destroyed together with the instance
        if (_instance != 0 && Xr.DestroyInstance != null)
        {
            var result = Xr.DestroyInstance(_instance); // destroys the session, spaces and swapchains too
            RenderThreadMessages.Enqueue($"xrDestroyInstance: {(result >= 0 ? "ok" : result.ToString())}");
        }
        _shutdownDone = true;
    }

    /// <summary>Main thread, after the render thread destroyed the instance: back to the not-initialized state.</summary>
    private static void FinishShutdown()
    {
        _instance = _system = _session = _localSpace = 0;
        _uiTarget = null;
        _eyes = null;
        _pendingEyes = null;
        if (_context != IntPtr.Zero) D3D11.Release(_context);
        if (_device != IntPtr.Zero) D3D11.Release(_device);
        _context = _device = IntPtr.Zero;
        State = XrSessionState.Unknown;
        _exitRequested = false;
        _shutdownDone = false;
        _shuttingDown = false;
        IsInitialized = false;
        Plugin.Logger.LogInfo("OpenXR: VR stopped; the game continues without VR (F11 starts VR again)");
    }

    private static void StopFrames()
    {
        if (!_sessionRunning) return;
        _sessionRunning = false;
        // Ends the session on the render thread, after any frame submissions already queued there.
        GL.IssuePluginEvent(_renderEventFunc, EventEndSession);
    }

    /// <summary>
    /// xrWaitFrame (blocks to pace the game to the headset) + xrLocateViews in LOCAL space.
    /// Returns the frame slot to fill and submit, or -1 if no frame should be started.
    /// </summary>
    public static int WaitAndLocate()
    {
        if (!_sessionRunning) return -1;

        var frameState = new XrFrameState { type = XrStructureType.FrameState };
        var result = Xr.WaitFrame(_session, null, &frameState);
        if (result < 0)
        {
            Plugin.Logger.LogError($"OpenXR: xrWaitFrame failed: {Xr.Describe(_instance, result)}");
            return -1;
        }

        var slot = _nextSlot;
        _nextSlot = (_nextSlot + 1) % SlotCount;
        ref var s = ref Slots[slot];
        s.DisplayTime = frameState.predictedDisplayTime;
        s.ShouldRender = frameState.shouldRender != 0;
        s.UiVisible = false; // the renderer sets this (and the pose) each frame

        if (s.ShouldRender)
        {
            var locateInfo = new XrViewLocateInfo
            {
                type = XrStructureType.ViewLocateInfo,
                viewConfigurationType = XrConst.ViewConfigurationPrimaryStereo,
                displayTime = frameState.predictedDisplayTime,
                space = _localSpace,
            };
            var viewState = new XrViewState { type = XrStructureType.ViewState };
            var views = stackalloc XrView[2];
            views[0].type = views[1].type = XrStructureType.View;
            uint count;
            result = Xr.LocateViews(_session, &locateInfo, &viewState, 2, &count, views);
            if (result < 0 || (viewState.viewStateFlags & XrConst.ViewStateOrientationValid) == 0)
            {
                s.ShouldRender = false; // still submit an empty frame to keep the frame loop balanced
            }
            else
            {
                s.Left = views[0];
                s.Right = views[1];
            }
        }

        return slot;
    }

    public static ref FrameSlot GetSlot(int slot) => ref Slots[slot];

    /// <summary>Queues xrBeginFrame/copy/xrEndFrame for this slot on Unity's render thread.</summary>
    public static void Submit(int slot)
    {
        GL.IssuePluginEvent(_renderEventFunc, EventSubmitBase + slot);
    }

    private static void DrainRenderThreadMessages()
    {
        while (RenderThreadMessages.TryDequeue(out var message))
            Plugin.Logger.LogInfo($"OpenXR (render thread): {message}");
    }

    // ---------------------------------------------------------------- render thread

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnRenderEvent(int eventId)
    {
        try
        {
            if (eventId == EventEndSession)
            {
                if (_rtSessionEnded) return;
                _rtSessionEnded = true; // a frame located before the stop may still be queued; it's skipped
                var result = Xr.EndSession(_session);
                RenderThreadMessages.Enqueue($"xrEndSession: {Xr.Describe(_instance, result)}");
                return;
            }

            if (eventId == EventDestroy)
            {
                DestroyOnRenderThread();
                return;
            }

            var slot = eventId - EventSubmitBase;
            if (slot >= 0 && slot < SlotCount && !_rtSessionEnded)
                SubmitFrame(ref Slots[slot]);
        }
        catch (Exception e)
        {
            ReportRenderThreadError(e.ToString());
        }
    }

    private static void SubmitFrame(ref FrameSlot slot)
    {
        var result = Xr.BeginFrame(_session, null);
        if (result < 0)
        {
            ReportRenderThreadError($"xrBeginFrame failed: {Xr.Describe(_instance, result)}");
            return;
        }

        var views = stackalloc XrCompositionLayerProjectionView[2];
        var layer = new XrCompositionLayerProjection
        {
            type = XrStructureType.CompositionLayerProjection,
            space = _localSpace,
            viewCount = 2,
            views = views,
        };
        while (RetiredSwapchains.TryDequeue(out var retired))
            Xr.DestroySwapchain(retired);

        var eyes = _eyes;
        var ui = _uiTarget;
        var quad = new XrCompositionLayerQuad
        {
            type = XrStructureType.CompositionLayerQuad,
            // UI is rendered with alpha blending into a transparent texture, i.e. colour is premultiplied.
            layerFlags = XrConst.CompositionLayerBlendTextureSourceAlpha,
            space = _localSpace,
            eyeVisibility = XrConst.EyeVisibilityBoth,
            subImage = ui == null ? default : new XrSwapchainSubImage { swapchain = ui.Swapchain, extentWidth = ui.Width, extentHeight = ui.Height },
            pose = slot.UiPose,
            width = slot.UiWidth,
            height = slot.UiHeight,
        };

        var layers = stackalloc void*[2];
        var layerCount = 0u;
        if (slot.ShouldRender && eyes != null && eyes.Sources[0] != IntPtr.Zero && eyes.Sources[1] != IntPtr.Zero)
        {
            var eyesCopied = true;
            for (var eye = 0; eye < 2 && eyesCopied; eye++)
            {
                eyesCopied = CopyIntoSwapchain(eyes.Swapchains[eye], eyes.Images[eye], eyes.Sources[eye], eye == 0 ? "left eye" : "right eye");
                var view = eye == 0 ? slot.Left : slot.Right;
                views[eye] = new XrCompositionLayerProjectionView
                {
                    type = XrStructureType.CompositionLayerProjectionView,
                    pose = view.pose,
                    fov = view.fov,
                    subImage = new XrSwapchainSubImage
                    {
                        swapchain = eyes.Swapchains[eye],
                        extentWidth = eyes.Width,
                        extentHeight = eyes.Height,
                    },
                };
            }

            if (eyesCopied)
            {
                layers[layerCount++] = &layer;

                // The UI panel is drawn on top of the scene.
                if (slot.UiVisible && ui != null && ui.Source != IntPtr.Zero &&
                    CopyIntoSwapchain(ui.Swapchain, ui.Images, ui.Source, "UI panel"))
                    layers[layerCount++] = &quad;
            }
        }

        var endInfo = new XrFrameEndInfo
        {
            type = XrStructureType.FrameEndInfo,
            displayTime = slot.DisplayTime,
            environmentBlendMode = XrConst.EnvironmentBlendModeOpaque,
            layerCount = layerCount,
            layers = layerCount > 0 ? layers : null,
        };
        result = Xr.EndFrame(_session, &endInfo);
        if (result < 0)
        {
            ReportRenderThreadError($"xrEndFrame failed: {Xr.Describe(_instance, result)}");
            return;
        }

        if (layerCount > 0)
        {
            _framesSubmitted++;
            if (!_loggedFirstSubmit)
            {
                _loggedFirstSubmit = true;
                RenderThreadMessages.Enqueue("first frame submitted to the headset");
            }
            if (layerCount > 1 && !_loggedFirstUiSubmit)
            {
                _loggedFirstUiSubmit = true;
                RenderThreadMessages.Enqueue("first frame with the UI panel submitted");
            }
        }
    }

    private static bool CopyIntoSwapchain(ulong swapchain, IntPtr[] images, IntPtr source, string label)
    {
        uint index;
        var result = Xr.AcquireSwapchainImage(swapchain, null, &index);
        if (result < 0)
        {
            ReportRenderThreadError($"xrAcquireSwapchainImage({label}) failed: {Xr.Describe(_instance, result)}");
            return false;
        }

        var waitInfo = new XrSwapchainImageWaitInfo { type = XrStructureType.SwapchainImageWaitInfo, timeout = XrConst.InfiniteDuration };
        result = Xr.WaitSwapchainImage(swapchain, &waitInfo);
        if (result >= 0)
            D3D11.CopyResource(_context, images[index], source);
        else
            ReportRenderThreadError($"xrWaitSwapchainImage({label}) failed: {Xr.Describe(_instance, result)}");

        var releaseResult = Xr.ReleaseSwapchainImage(swapchain, null);
        if (releaseResult < 0)
            ReportRenderThreadError($"xrReleaseSwapchainImage({label}) failed: {Xr.Describe(_instance, releaseResult)}");

        return result >= 0 && releaseResult >= 0;
    }

    private static void ReportRenderThreadError(string message)
    {
        // Log the first few, then every 500th, so a persistent failure can't flood the log.
        var count = ++_renderThreadErrorCount;
        if (count <= 10 || count % 500 == 0)
            RenderThreadMessages.Enqueue($"ERROR #{count}: {message}");
    }
}
