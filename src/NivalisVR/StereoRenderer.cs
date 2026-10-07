using System;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Attributes;
using NivalisVR.OpenXR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// Renders the game's main camera once per eye, either for the headset (OpenXR, milestone 3) or as a
/// side-by-side preview on the flat screen (milestone 2, used when VR isn't running; F9, only with [Debug] PreviewKey).
///
/// This component lives on our own display camera (depth 100). When that camera is about to cull (after Cinemachine
/// has positioned MainCamera), we temporarily move MainCamera to each eye's pose, point it at an eye RenderTexture,
/// call Render(), then restore it. While VR runs, MainCamera's own render produces the left eye instead of the
/// monitor view (see OnFrameStart); gameplay code (Camera.main, ScreenPointToRay, etc.) never sees the change.
/// </summary>
public class StereoRenderer : MonoBehaviour
{
    internal static ConfigEntry<bool> PreviewKeyEnabled;
    internal static ConfigEntry<float> PreviewIpd;
    internal static ConfigEntry<bool> VrEnabled;
    internal static ConfigEntry<float> VrRenderScale;
    internal static ConfigEntry<bool> VrFlipY;
    internal static ConfigEntry<bool> CaptureKeyEnabled;

    private static StereoRenderer _instance;

    private Camera _display;
    private Camera.CameraCallback _preCullCallback;
    private Camera.CameraCallback _preRenderCallback;

    // One-time check that the per-eye projection survives until the camera actually renders.
    private Camera _verifyCamera;
    private Matrix4x4 _expectedProjection;
    private bool _projectionVerified;

    // Flat side-by-side preview.
    private RenderTexture _previewLeft, _previewRight, _previewSbs;
    private CommandBuffer _previewBlit;
    private bool _previewBlitAttached;
    private bool _loggedFirstPreviewFrame;
    private bool _previewOn;                   // F9, only with [Debug] PreviewKey; not saved

    // Headset rendering: the camera renders into _vrEye, which is flipped into _vrSubmit (copied to the swapchain).
    private readonly RenderTexture[] _vrEye = new RenderTexture[2];
    private readonly RenderTexture[] _vrSubmit = new RenderTexture[2];
    private bool _vrInitAttempted;
    // Switching between VR and flat (Shift+F11 / F11): see Update.
    private bool _wasVrRunning;
    private bool _releaseEffectsOnResize;
    private int _lastScreenWidth, _lastScreenHeight;
    private bool _loggedFirstVrFrame;

    // While VR runs, MainCamera's own render for the monitor is replaced by the eye renders. An empty camera of ours
    // renders before every other camera each frame (depth -1000, into a tiny texture); in its pre-cull we locate the
    // headset frame and point MainCamera at the left eye (pose, lens, eye texture). In practice (Unity 2020.3) Unity
    // then skips MainCamera's own render for that frame, apparently because its target changed after the frame's
    // camera list was set up, so our display camera renders both eyes and copies the left eye to the monitor. Should
    // Unity render it anyway, that render is used as the left eye (onPostRender). Either way MainCamera only renders
    // at eye size, and it's put back exactly as it was before game code runs again (verified: with Shift+F9 its own
    // render resumes immediately). (Application.onBeforeRender would be the natural hook, but it's stripped from
    // this game's build.)
    // Besides saving a whole render per frame, this keeps MainCamera at a single render size. Some of the game's
    // effects (the Hx light shafts and at least one other) build new render textures on every size change and never
    // destroy the old ones; switching between eye and monitor size twice per frame used up Unity's graphics resource
    // IDs after ~15 minutes (black scene until restart, "Resource ID out of range" in Player.log).
    internal static ConfigEntry<bool> VrSkipMonitorRender;
    private Camera _frameStart;                // the empty camera whose pre-cull starts each VR frame
    private RenderTexture _frameStartTarget;
    private int _frameStartFrame = -1;         // Time.frameCount of its last pre-cull
    private Camera.CameraCallback _postRenderCallback;
    private int _pendingSlot = -1;             // headset frame located at frame start, finished at our display camera
    private Camera _pendingCamera;             // MainCamera for that frame
    private Camera _leftEyeCamera;             // MainCamera while it's set up to render the left eye itself
    private Vector3 _leftEyeBasePosition;
    private Quaternion _leftEyeBaseRotation;
    private RenderTexture _leftEyeOriginalTarget;
    private LensState _leftEyeOriginalLens;
    private XrFovf _leftEyeFov;
    private bool _leftEyeCompanionsRendered;
    private bool _leftEyeRendered;             // MainCamera's own render produced this frame's left eye
    private CommandBuffer _monitorBlit;        // left eye -> monitor while MainCamera renders the left eye
    private bool _monitorBlitAttached;
    private Texture _monitorBlitSource;
    private int _monitorBlitScreenWidth, _monitorBlitScreenHeight;
    private bool _loggedLeftEyeByGame, _loggedLeftEyeMissed, _loggedFrameStartFallback;
    private static bool _manualRender;         // inside one of our own Camera.Render() calls (diagnostics)
    private bool _gameRenderForced;            // Shift+F9: game camera's own monitor render back on (diagnostics)
    private Camera _mainCameraThisFrame;       // Camera.main as of this frame's Update (diagnostics)

    // UI panel: the game's overlay UI is redirected into a texture (UiRedirect), flipped into _uiSubmit,
    // and shown in the headset as a quad layer fixed in tracking space in front of the recentered view.
    internal static ConfigEntry<float> UiPanelDistance;
    internal static ConfigEntry<float> UiPanelWidth;
    internal static ConfigEntry<float> UiPanelHeightOffset;
    private UiRedirect _ui;
    private ModMenu _menu;
    private RenderTexture _uiSubmit;
    private bool _uiSourceReady;
    private XrPosef _uiPose;

    // Tracking-space origin used to map head poses onto the game camera (set by recentering).
    private Vector3 _originPosition;
    private Quaternion _originRotation = Quaternion.identity;
    private bool _needsRecenter = true;
    private Vector3 _lastHeadPosition;
    private Quaternion _lastHeadRotation = Quaternion.identity;

    public StereoRenderer(IntPtr ptr) : base(ptr) { }

    [HideFromIl2Cpp]
    internal static void BindConfig(ConfigFile config)
    {
        VrEnabled = config.Bind("VR", "Enabled", true,
            "Start VR automatically when the game launches. With SteamVR this only happens if SteamVR is already running; " +
            "otherwise the game starts without VR. F11 starts VR at any time.");
        VrRenderScale = config.Bind("VR", "RenderScale", 1.0f,
            "Multiplier on the runtime's recommended per-eye resolution (1.0 = exactly what SteamVR recommends for this app; set the resolution in SteamVR or a SteamVR add-on instead).");
        VrFlipY = config.Bind("VR", "FlipY", true,
            "Flip eye images vertically before submitting. Unity stores D3D11 render textures bottom-up; turn off if the headset image is upside down.");
        VrSkipMonitorRender = config.Bind("VR", "SkipMonitorRender", true,
            "While VR runs, don't render the game a third time for the monitor; the monitor shows the left eye instead. " +
            "Faster, and avoids a texture leak in the game's effects that turned the picture black after ~15 minutes. " +
            "Only turn off for troubleshooting.");
        UiPanelDistance = config.Bind("UI", "PanelDistance", 2.0f,
            "Distance in metres from your head (at recenter) to the menu/HUD panel.");
        UiPanelWidth = config.Bind("UI", "PanelWidth", 2.6f,
            "Width of the menu/HUD panel in metres (height follows the screen's aspect ratio).");
        UiPanelHeightOffset = config.Bind("UI", "PanelHeightOffset", -0.1f,
            "Vertical offset of the panel centre from eye height, in metres (negative = lower).");
        PreviewKeyEnabled = config.Bind("Debug", "PreviewKey", false,
            "Lets F9 show the left and right eye views side by side on the monitor when VR isn't running (for testing). " +
            "Not for playing: while the preview is on, the game's effects leak textures and the picture turns black " +
            "after ~15 minutes, like the VR bug fixed in 0.4.1.");
        PreviewIpd = config.Bind("Debug", "PreviewIpd", 0.064f,
            "Eye distance in metres for the F9 preview only (the headset always uses its own IPD).");
        CaptureKeyEnabled = config.Bind("Debug", "CaptureKey", false,
            "F6 saves the current eye images (and the game's sky/glow helper textures) as PNG files in " +
            "BepInEx/NivalisVR-captures, for bug reports.");
        ResourceStats.BindConfig(config);
        VrWindow.BindConfig(config);
        ModMenu.BindConfig(config);
    }

    [HideFromIl2Cpp]
    internal static void EnsureCreated()
    {
        if (_instance != null) return;

        var go = new GameObject("NivalisVR_StereoRenderer");
        Object.DontDestroyOnLoad(go);
        _instance = go.AddComponent<StereoRenderer>();
    }

    private void Awake()
    {
        _display = gameObject.AddComponent<Camera>();
        _display.depth = 100;
        _display.cullingMask = 0;
        _display.clearFlags = CameraClearFlags.Nothing;
        _display.renderingPath = RenderingPath.Forward;
        _display.allowHDR = false;
        _display.allowMSAA = false;
        _display.useOcclusionCulling = false;

        // Unity doesn't deliver camera messages (OnPreCull etc.) to Il2CppInterop-injected components,
        // so hook the global per-camera callback instead and react only to our display camera.
        try
        {
            _preCullCallback = (Camera.CameraCallback)(Action<Camera>)OnAnyCameraPreCull;
            Camera.onPreCull = Camera.onPreCull == null ? _preCullCallback : Camera.onPreCull + _preCullCallback;
            _preRenderCallback = (Camera.CameraCallback)(Action<Camera>)OnAnyCameraPreRender;
            Camera.onPreRender = Camera.onPreRender == null ? _preRenderCallback : Camera.onPreRender + _preRenderCallback;
            Plugin.Logger.LogInfo("Stereo renderer: display camera created, onPreCull/onPreRender hooked");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Stereo renderer: failed to hook Camera.onPreCull: {e}");
        }

        try
        {
            _postRenderCallback = (Camera.CameraCallback)(Action<Camera>)OnAnyCameraPostRender;
            Camera.onPostRender = Camera.onPostRender == null ? _postRenderCallback : Camera.onPostRender + _postRenderCallback;

            var frameStartObject = new GameObject("NivalisVR_FrameStart");
            frameStartObject.transform.SetParent(transform, false);
            _frameStart = frameStartObject.AddComponent<Camera>();
            _frameStart.depth = -1000;
            _frameStart.cullingMask = 0;
            _frameStart.clearFlags = CameraClearFlags.Nothing;
            _frameStart.renderingPath = RenderingPath.Forward;
            _frameStart.allowHDR = false;
            _frameStart.allowMSAA = false;
            _frameStart.useOcclusionCulling = false;
            _frameStartTarget = CreateTarget("NivalisVR_FrameStartTarget", 4, 4, 0);
            _frameStart.targetTexture = _frameStartTarget;
            Plugin.Logger.LogInfo("Stereo renderer: frame-start camera created, onPostRender hooked");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Stereo renderer: frame-start setup failed (the game's own render can't become the left eye): {e}");
        }

        _ui = new UiRedirect();
        _ui.TextureCreated += OnUiTextureCreated;
        _menu = new ModMenu(_ui.RequestScan);
    }

    private void OnDestroy()
    {
        Plugin.Logger.LogInfo("Stereo renderer: shut down");
        _menu?.Close();
        if (_preCullCallback != null && Camera.onPreCull != null)
            Camera.onPreCull = Camera.onPreCull - _preCullCallback;
        if (_preRenderCallback != null && Camera.onPreRender != null)
            Camera.onPreRender = Camera.onPreRender - _preRenderCallback;
        if (_postRenderCallback != null && Camera.onPostRender != null)
            Camera.onPostRender = Camera.onPostRender - _postRenderCallback;
        RestoreLeftEyeCamera();
        ReleaseMonitorBlit();
        DestroyTarget(_frameStartTarget);
        ReleasePreviewTargets();
        for (var eye = 0; eye < 2; eye++)
        {
            DestroyTarget(_vrEye[eye]);
            DestroyTarget(_vrSubmit[eye]);
        }
        DestroyTarget(_uiSubmit);
    }

    private void Update()
    {
        // Normally restored right after MainCamera renders; never leave it set up for the eye into game logic.
        RestoreLeftEyeCamera();
        _mainCameraThisFrame = Camera.main;

        if (!_vrInitAttempted && VrEnabled.Value)
        {
            _vrInitAttempted = true;
            if (VrStartup.ShouldStartAutomatically())
                VrSession.Initialize(VrRenderScale.Value);
        }

        VrSession.PollEvents();
        HandleHotkeys();
        _menu.Update(VrSession.IsRunning);
        ResourceStats.Update(VrSession.IsRunning);

        try
        {
            _ui.Update(VrSession.IsRunning);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"UI redirect failed: {e}");
        }

        if (!VrSession.IsRunning)
        {
            VrWindow.Restore();
            HudToggle.RestoreForFlat();
        }

        HandleVrSwitch();

        // The flat preview is only drawn when the headset isn't being driven.
        SetPreviewBlitAttached(_previewOn && !VrSession.IsRunning && _previewBlit != null);
    }

    [HideFromIl2Cpp]
    private void HandleHotkeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (PreviewKeyEnabled.Value && keyboard.f9Key.wasPressedThisFrame && !keyboard.shiftKey.isPressed)
        {
            _previewOn = !_previewOn;
            Plugin.Logger.LogInfo($"Side-by-side preview {(_previewOn ? "enabled" : "disabled")}");
        }

        // VR only, like the menu: without VR the game should look and play like the unmodded game.
        if (keyboard.f5Key.wasPressedThisFrame && VrSession.IsRunning)
            HudToggle.Toggle();

        // Diagnostics (not saved): switch the game camera's own monitor render back on while VR runs, to compare.
        if (keyboard.f9Key.wasPressedThisFrame && keyboard.shiftKey.isPressed)
        {
            _gameRenderForced = !_gameRenderForced;
            Plugin.Logger.LogInfo(_gameRenderForced
                ? "VR: game camera's own monitor render switched back on (Shift+F9, diagnostics)"
                : "VR: game camera renders the left eye again; the monitor shows the left eye (Shift+F9)");
        }

        if (keyboard.f10Key.wasPressedThisFrame)
            RequestRecenter();

        if (CaptureKeyEnabled.Value && keyboard.f6Key.wasPressedThisFrame && VrSession.IsRunning)
        {
            _captureName = $"{DateTime.Now:yyyyMMdd-HHmmss}";
            Plugin.Logger.LogInfo($"Capture requested ({_captureName}), saving to {DebugCapture.Folder}");
        }

        if (keyboard.f8Key.wasPressedThisFrame && keyboard.shiftKey.isPressed)
            ResourceStats.Toggle();

        if (keyboard.f11Key.wasPressedThisFrame && !keyboard.shiftKey.isPressed && !VrSession.IsInitialized)
        {
            Plugin.Logger.LogInfo("VR: starting VR (F11)");
            VrSession.Initialize(VrRenderScale.Value);
        }

        if (keyboard.f11Key.wasPressedThisFrame && keyboard.shiftKey.isPressed && VrSession.IsInitialized)
        {
            Plugin.Logger.LogInfo("VR: switching VR off, the game keeps running (Shift+F11)");
            VrSession.RequestStop();
        }
    }

    /// <summary>
    /// Clean switches between VR and flat. MainCamera changes size when VR starts or stops (eye size vs window), and
    /// the game's Hx light shafts would leave a set of textures at the old size behind every time (see
    /// ReleaseGameEffectTextures). The window size also changes a few frames after VR stops (VrWindow.Restore), so
    /// that resize gets the same treatment. Once the OpenXR session is gone, our eye textures are freed too.
    /// </summary>
    [HideFromIl2Cpp]
    private void HandleVrSwitch()
    {
        var running = VrSession.IsRunning;
        if (running != _wasVrRunning)
        {
            _wasVrRunning = running;
            ReleaseGameEffectTextures();
            _releaseEffectsOnResize = !running;
        }
        else if (!running && _releaseEffectsOnResize && (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight))
        {
            _releaseEffectsOnResize = false;
            ReleaseGameEffectTextures();
        }
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;

        if (!VrSession.IsInitialized && _vrSubmit[0] != null)
            ReleaseVrTargets();
    }

    /// <summary>Frees the eye and UI panel textures after VR stopped (the render thread no longer uses them).</summary>
    [HideFromIl2Cpp]
    private void ReleaseVrTargets()
    {
        RestoreLeftEyeCamera();
        ReleaseMonitorBlit();
        for (var eye = 0; eye < 2; eye++)
        {
            DestroyTarget(_vrEye[eye]);
            DestroyTarget(_vrSubmit[eye]);
            _vrEye[eye] = _vrSubmit[eye] = null;
        }
        DestroyTarget(_uiSubmit);
        _uiSubmit = null;
        _uiSourceReady = false;
        _needsRecenter = true; // F11 starts with the view centred again
        _loggedFirstVrFrame = false;
        Plugin.Logger.LogInfo("VR: eye textures released");
    }

    /// <summary>
    /// Pre-cull of our frame-start camera: after all game logic (Update/LateUpdate, Cinemachine) and before any other
    /// camera renders. Locates the headset frame and sets MainCamera up to render the left eye itself.
    /// </summary>
    [HideFromIl2Cpp]
    private void OnFrameStart()
    {
        _frameStartFrame = Time.frameCount;
        _menu.AfterGameLogic();
        var leftEyeByGame = false;
        try
        {
            FlushPendingFrame();
            RestoreLeftEyeCamera();
            if (!VrSession.IsRunning) return;

            var cam = Camera.main;
            if (cam == null) return;

            var slot = VrSession.WaitAndLocate();
            if (slot < 0) return;
            _pendingSlot = slot;
            _pendingCamera = cam;

            ref var frame = ref VrSession.GetSlot(slot);
            if (!frame.ShouldRender) return;

            ResourceStats.FrameStart();
            EnsureVrTargets();
            UpdateHeadPose(frame.Left, frame.Right);

            // A debug capture (F6) takes the separate-render path, which saves each eye's helper textures.
            if (VrSkipMonitorRender.Value && !_gameRenderForced && _captureName == null)
            {
                SetUpLeftEyeAsGameRender(cam, frame.Left);
                leftEyeByGame = true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"VR: frame start failed: {e}");
            RestoreLeftEyeCamera();
            leftEyeByGame = false;
        }
        finally
        {
            SetMonitorBlitAttached(leftEyeByGame);
        }
    }

    /// <summary>Points MainCamera's upcoming render at the left eye: eye pose, eye lens, eye texture.</summary>
    [HideFromIl2Cpp]
    private void SetUpLeftEyeAsGameRender(Camera cam, XrView view)
    {
        var t = cam.transform;
        _leftEyeBasePosition = t.position;
        _leftEyeBaseRotation = t.rotation;
        _leftEyeOriginalTarget = cam.targetTexture;
        _leftEyeOriginalLens = LensState.Save(cam);
        _leftEyeFov = view.fov;
        _leftEyeCompanionsRendered = false;
        _leftEyeRendered = false;
        _leftEyeCamera = cam;

        ApplyEyePose(t, view, _leftEyeBasePosition, _leftEyeBaseRotation);
        ApplyEyeLens(cam, view.fov, ProjectionFromFov(view.fov, cam.nearClipPlane, cam.farClipPlane));
        FindCompanionCameras(cam);
        cam.targetTexture = EyeTarget(0);

        if (!_loggedLeftEyeByGame)
        {
            _loggedLeftEyeByGame = true;
            Plugin.Logger.LogInfo($"VR: '{cam.name}' is only rendered for the eyes (no separate monitor render); the monitor shows the left eye");
        }
    }

    /// <summary>Puts MainCamera back exactly as the game left it (no-op unless it's set up for the left eye).</summary>
    [HideFromIl2Cpp]
    private void RestoreLeftEyeCamera()
    {
        var cam = _leftEyeCamera;
        _leftEyeCamera = null;
        if (ReferenceEquals(cam, null)) return;

        if (cam != null)
        {
            cam.targetTexture = _leftEyeOriginalTarget;
            _leftEyeOriginalLens.Restore(cam);
            cam.transform.position = _leftEyeBasePosition;
            cam.transform.rotation = _leftEyeBaseRotation;
        }
        RestoreCompanionCameras();
    }

    /// <summary>
    /// A headset frame that was located but never rendered (our display camera didn't run) is submitted empty,
    /// so every xrWaitFrame stays paired with xrBeginFrame/xrEndFrame.
    /// </summary>
    [HideFromIl2Cpp]
    private void FlushPendingFrame()
    {
        if (_pendingSlot < 0) return;
        var slot = _pendingSlot;
        _pendingSlot = -1;
        _pendingCamera = null;
        VrSession.GetSlot(slot).ShouldRender = false;
        VrSession.Submit(slot);
    }

    [HideFromIl2Cpp]
    private void OnAnyCameraPreCull(Camera culling)
    {
        if (!_manualRender && !ReferenceEquals(_mainCameraThisFrame, null) && culling == _mainCameraThisFrame)
            ResourceStats.GameCameraRenders++;

        if (culling == _frameStart)
        {
            OnFrameStart();
            return;
        }

        // MainCamera's own render is about to produce the left eye. The game's helper cameras (sky, glow) have
        // rendered their mono view by now, so redo them for the left eye, as for every eye render.
        if (!ReferenceEquals(_leftEyeCamera, null) && culling == _leftEyeCamera && !_leftEyeCompanionsRendered)
        {
            _leftEyeCompanionsRendered = true;
            RenderCompanionCameras(_leftEyeFov);
            return;
        }

        if (culling != _display) return;

        if (VrSession.IsRunning || _pendingSlot >= 0)
        {
            int slot;
            Camera cam;
            var frameStarted = _frameStartFrame == Time.frameCount;
            var leftEyeByGame = false;
            if (frameStarted)
            {
                // Normal path: the frame was located at frame start; MainCamera has rendered (and its image effects
                // have run) by now, so it's safe to put it back.
                slot = _pendingSlot;
                cam = _pendingCamera;
                _pendingSlot = -1;
                _pendingCamera = null;
                leftEyeByGame = _leftEyeRendered;
                if (leftEyeByGame)
                {
                    ResourceStats.LeftEyeByGame++;
                }
                else if (!ReferenceEquals(_leftEyeCamera, null))
                {
                    // Set up for the left eye, but its own render didn't produce it: either Unity didn't render
                    // MainCamera this frame, or it did and we didn't see the render finish.
                    var renderedUnseen = _leftEyeCompanionsRendered;
                    if (renderedUnseen) ResourceStats.LeftEyeUndetected++;
                    else ResourceStats.LeftEyeSeparate++;
                    if (!_loggedLeftEyeMissed)
                    {
                        _loggedLeftEyeMissed = true;
                        if (renderedUnseen)
                            Plugin.Logger.LogWarning("VR: the game camera rendered, but the end of its render wasn't seen; rendering the left eye again");
                        else
                            Plugin.Logger.LogInfo("VR: Unity skips the game camera's own render (as expected); the left eye is rendered separately");
                    }
                }
                RestoreLeftEyeCamera();
            }
            else
            {
                // Fallback if our frame-start camera didn't run: locate now and render both eyes ourselves.
                if (!_loggedFrameStartFallback)
                {
                    _loggedFrameStartFallback = true;
                    Plugin.Logger.LogWarning("VR: frame-start camera didn't run this frame; rendering both eyes after the game's own render (old behaviour)");
                }
                SetMonitorBlitAttached(false);
                cam = Camera.main;
                slot = cam != null ? WaitAndLocateLogged() : -1;
            }
            _leftEyeRendered = false;
            if (slot >= 0) RenderVrFrame(cam, slot, leftEyeByGame, frameStarted);
            return;
        }

        if (_previewOn)
        {
            var cam = Camera.main;
            if (cam != null) RenderPreviewFrame(cam);
        }
    }

    /// <summary>
    /// Notes that MainCamera's own render produced the left eye. Its image effects still run after this callback
    /// (OnRenderImage), so MainCamera is only put back at our display camera.
    /// </summary>
    [HideFromIl2Cpp]
    private void OnAnyCameraPostRender(Camera rendered)
    {
        if (ReferenceEquals(_leftEyeCamera, null) || rendered != _leftEyeCamera || !_leftEyeCompanionsRendered || _leftEyeRendered)
            return;
        _leftEyeRendered = true;
        ResourceStats.Mark("left eye (game's own render)");
    }

    [HideFromIl2Cpp]
    private static int WaitAndLocateLogged()
    {
        try
        {
            return VrSession.WaitAndLocate();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"VR: wait/locate failed: {e}");
            return -1;
        }
    }

    [HideFromIl2Cpp]
    private void OnUiTextureCreated(RenderTexture uiTexture)
    {
        DestroyTarget(_uiSubmit);
        _uiSubmit = CreateTarget("NivalisVR_UiSubmit", uiTexture.width, uiTexture.height, 0);
        _uiSourceReady = VrSession.SetUiSource(_uiSubmit.GetNativeTexturePtr(), uiTexture.width, uiTexture.height);
    }

    [HideFromIl2Cpp]
    private void OnAnyCameraPreRender(Camera rendering)
    {
        if (_verifyCamera == null || rendering != _verifyCamera) return;

        _projectionVerified = true;
        _verifyCamera = null;
        var actual = rendering.projectionMatrix;
        var kept = Mathf.Abs(actual.m00 - _expectedProjection.m00) < 1e-4f && Mathf.Abs(actual.m02 - _expectedProjection.m02) < 1e-4f &&
                   Mathf.Abs(actual.m11 - _expectedProjection.m11) < 1e-4f && Mathf.Abs(actual.m12 - _expectedProjection.m12) < 1e-4f;
        Plugin.Logger.LogInfo($"VR: per-eye projection {(kept ? "kept" : "WAS OVERRIDDEN")} at render time " +
                              $"(expected m00={_expectedProjection.m00:F4} m02={_expectedProjection.m02:F4} m11={_expectedProjection.m11:F4} m12={_expectedProjection.m12:F4}; " +
                              $"actual m00={actual.m00:F4} m02={actual.m02:F4} m11={actual.m11:F4} m12={actual.m12:F4})");
    }

    // ---------------------------------------------------------------- headset

    [HideFromIl2Cpp]
    private void RenderVrFrame(Camera cam, int slot, bool leftEyeByGame, bool frameStarted)
    {
        // The located frame must be submitted (even empty) so begin/end stay balanced with xrWaitFrame.
        try
        {
            ref var frame = ref VrSession.GetSlot(slot);
            if (frame.ShouldRender && cam != null)
            {
                if (!frameStarted)
                {
                    ResourceStats.FrameStart();
                    EnsureVrTargets();
                    UpdateHeadPose(frame.Left, frame.Right);
                }

                if (leftEyeByGame)
                {
                    // MainCamera's own render already drew the left eye into its eye texture.
                    if (VrFlipY.Value) Graphics.Blit(_vrEye[0], _vrSubmit[0], new Vector2(1f, -1f), new Vector2(0f, 1f));
                }
                else
                {
                    RenderVrEye(cam, frame.Left, 0);
                    ResourceStats.Mark("left eye");
                }
                RenderVrEye(cam, frame.Right, 1);
                ResourceStats.Mark("right eye");
                _captureName = null;

                if (_ui.Active && _uiSourceReady && _ui.Texture != null)
                {
                    // Flip into the submit texture (Unity D3D11 render textures are stored bottom-up).
                    if (VrFlipY.Value) Graphics.Blit(_ui.Texture, _uiSubmit, new Vector2(1f, -1f), new Vector2(0f, 1f));
                    else Graphics.Blit(_ui.Texture, _uiSubmit);

                    frame.UiVisible = true;
                    frame.UiPose = _uiPose;
                    frame.UiWidth = UiPanelWidth.Value;
                    frame.UiHeight = UiPanelWidth.Value * _ui.Texture.height / _ui.Texture.width;
                }

                // Panel width in headset pixels: eye pixels per unit of tan(angle), times the panel's angular width.
                var fov = frame.Left.fov;
                var pixelsPerTan = VrSession.EyeWidth / (Mathf.Tan(fov.angleRight) - Mathf.Tan(fov.angleLeft));
                VrWindow.EnsureShrunk(pixelsPerTan * UiPanelWidth.Value / UiPanelDistance.Value);

                if (!_loggedFirstVrFrame)
                {
                    _loggedFirstVrFrame = true;
                    Plugin.Logger.LogInfo($"VR: first eye frame rendered from '{cam.name}' at {VrSession.EyeWidth}x{VrSession.EyeHeight}, flipY={VrFlipY.Value}");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"VR: eye rendering failed: {e}");
        }
        finally
        {
            VrSession.Submit(slot);
        }
    }

    [HideFromIl2Cpp]
    private void UpdateHeadPose(XrView left, XrView right)
    {
        var leftPos = ToUnityPosition(left.pose.position);
        var rightPos = ToUnityPosition(right.pose.position);
        _lastHeadPosition = (leftPos + rightPos) * 0.5f;
        _lastHeadRotation = ToUnityRotation(left.pose.orientation);

        if (_needsRecenter)
        {
            _needsRecenter = false;
            // Only yaw is recentered, so the horizon stays where the real world's is.
            _originPosition = _lastHeadPosition;
            _originRotation = Quaternion.Euler(0f, _lastHeadRotation.eulerAngles.y, 0f);
            Plugin.Logger.LogInfo($"VR: recentered at head position {_originPosition}, yaw {_lastHeadRotation.eulerAngles.y:F1}");
            PlaceUiPanel();
        }
    }

    [HideFromIl2Cpp]
    internal static void RequestRecenter()
    {
        if (_instance == null) return;
        _instance._needsRecenter = true;
        Plugin.Logger.LogInfo("VR: recenter requested");
    }

    /// <summary>Moves the UI panel after its distance changed (menu). Its width is read every frame anyway.</summary>
    [HideFromIl2Cpp]
    internal static void ReplaceUiPanel()
    {
        if (_instance != null && VrSession.IsRunning) _instance.PlaceUiPanel();
    }

    /// <summary>Saves the new render scale and, while VR runs, switches the eye resolution right away (menu).</summary>
    [HideFromIl2Cpp]
    internal static void SetRenderScale(float renderScale)
    {
        VrRenderScale.Value = renderScale;
        if (VrSession.IsInitialized) VrSession.SetRenderScale(renderScale);
    }

    /// <summary>
    /// Fixes the UI panel in tracking space straight ahead of the recentered head (yaw only), facing the user.
    /// It stays put when you look around; F10 brings it back in front of you.
    /// </summary>
    [HideFromIl2Cpp]
    private void PlaceUiPanel()
    {
        var centre = _originPosition + _originRotation * new Vector3(0f, UiPanelHeightOffset.Value, UiPanelDistance.Value);
        // Unity -> OpenXR: mirror Z. A quad layer is visible from its +Z side; with the yaw converted the same
        // way as the head pose, its +Z points back at the user.
        _uiPose = new XrPosef
        {
            position = new XrVector3f { x = centre.x, y = centre.y, z = -centre.z },
            orientation = new XrQuaternionf { x = -_originRotation.x, y = -_originRotation.y, z = _originRotation.z, w = _originRotation.w },
        };
        Plugin.Logger.LogInfo($"VR: UI panel placed {UiPanelDistance.Value} m ahead, {UiPanelWidth.Value} m wide");
    }

    [HideFromIl2Cpp]
    private void RenderVrEye(Camera cam, XrView view, int eye)
    {
        var t = cam.transform;
        var basePosition = t.position;
        var baseRotation = t.rotation;
        var originalTarget = cam.targetTexture;

        var target = EyeTarget(eye);
        var originalLens = LensState.Save(cam);
        try
        {
            ApplyEyePose(t, view, basePosition, baseRotation);

            // Something in the game's camera stack (PPv2's PostProcessLayer.OnPreCull, at least) calls
            // Camera.ResetProjectionMatrix() before every render, so an explicit projectionMatrix gets thrown away.
            // Instead, make the camera's own physical lens describe the eye's asymmetric frustum: a reset then
            // recomputes exactly that frustum, and culling uses it too.
            var projection = ProjectionFromFov(view.fov, cam.nearClipPlane, cam.farClipPlane);
            ApplyEyeLens(cam, view.fov, projection);
            FindCompanionCameras(cam);
            if (_captureName != null && eye == 0)
            {
                foreach (var companion in _companions)
                    DebugCapture.Save(companion.targetTexture, $"{_captureName}_game_{companion.name}");
            }
            RenderCompanionCameras(view.fov);

            if (!_projectionVerified)
            {
                _verifyCamera = cam;
                _expectedProjection = projection;
            }

            cam.targetTexture = target;
            _manualRender = true;
            try
            {
                cam.Render();
            }
            finally
            {
                _manualRender = false;
            }

            if (_captureName != null)
            {
                DebugCapture.Save(target, $"{_captureName}_eye{eye}");
                foreach (var companion in _companions)
                    DebugCapture.Save(companion.targetTexture, $"{_captureName}_eye{eye}_{companion.name}");
            }
        }
        finally
        {
            _verifyCamera = null;
            cam.targetTexture = originalTarget;
            originalLens.Restore(cam);
            t.position = basePosition;
            t.rotation = baseRotation;
            RestoreCompanionCameras();
        }

        if (VrFlipY.Value)
            Graphics.Blit(_vrEye[eye], _vrSubmit[eye], new Vector2(1f, -1f), new Vector2(0f, 1f));
    }

    /// <summary>Head pose relative to the recentered origin, applied on top of the game camera's own pose.</summary>
    [HideFromIl2Cpp]
    private void ApplyEyePose(Transform t, XrView view, Vector3 basePosition, Quaternion baseRotation)
    {
        var inverseOrigin = Quaternion.Inverse(_originRotation);
        var localPosition = inverseOrigin * (ToUnityPosition(view.pose.position) - _originPosition);
        var localRotation = inverseOrigin * ToUnityRotation(view.pose.orientation);
        t.position = basePosition + baseRotation * localPosition;
        t.rotation = baseRotation * localRotation;
    }

    /// <summary>The texture MainCamera renders an eye into (flipped into _vrSubmit afterwards when FlipY is on).</summary>
    [HideFromIl2Cpp]
    private RenderTexture EyeTarget(int eye) => VrFlipY.Value ? _vrEye[eye] : _vrSubmit[eye];

    // Child cameras of MainCamera that render into textures the main view samples in screen space
    // (SkyboxCamera -> the global sky texture used by e.g. water/fog; BoidGlowCamera -> flock glow).
    // The game renders them once per frame from its mono view; in VR each eye needs its own, or the sampled
    // image ignores head movement and shows up as a "static copy" (seen in the clouds on the boat).
    private readonly System.Collections.Generic.List<Camera> _companions = new();
    private string _captureName;           // set by F6 ([Debug] CaptureKey): save this frame's eye images and helper textures

    [HideFromIl2Cpp]
    private void FindCompanionCameras(Camera cam)
    {
        _companions.Clear();
        foreach (var companion in cam.GetComponentsInChildren<Camera>())
        {
            if (companion == cam || !companion.enabled || companion.targetTexture == null) continue;
            _companions.Add(companion);
        }
    }

    /// <summary>
    /// Re-renders the companion cameras for the eye MainCamera is currently set up for. They are children of
    /// MainCamera, so they already follow its eye pose; they get exactly the eye's frustum.
    /// The game's sky camera has a narrower field of view (60° vs 75°), so in the flat game the sky texture is a
    /// zoomed copy of the sky. Keeping that zoom per eye was wrong: each eye's frustum is off-centre in opposite
    /// directions, so the zoom shifted the copy sideways in opposite directions too (~10° of disparity), and it
    /// looked like a second cloud layer much closer than the sky. With the eye's own frustum the copy lines up
    /// with the real sky.
    /// </summary>
    [HideFromIl2Cpp]
    private void RenderCompanionCameras(XrFovf fov)
    {
        foreach (var companion in _companions)
        {
            companion.projectionMatrix = ProjectionFromFov(fov, companion.nearClipPlane, companion.farClipPlane);
            companion.Render();
        }
    }

    [HideFromIl2Cpp]
    private void RestoreCompanionCameras()
    {
        foreach (var companion in _companions)
        {
            if (companion != null) companion.ResetProjectionMatrix();
        }
        _companions.Clear();
    }

    /// <summary>Creates the eye textures, or recreates them after a resolution change (menu), and hands them to VrSession.</summary>
    [HideFromIl2Cpp]
    private void EnsureVrTargets()
    {
        int width = VrSession.EyeWidth, height = VrSession.EyeHeight;
        var exist = _vrSubmit[0] != null && _vrSubmit[1] != null && (!VrFlipY.Value || (_vrEye[0] != null && _vrEye[1] != null));
        if (exist && _vrSubmit[0].width == width && _vrSubmit[0].height == height)
        {
            if (VrSession.NeedsSourceTextures)
                VrSession.SetSourceTextures(_vrSubmit[0].GetNativeTexturePtr(), _vrSubmit[1].GetNativeTexturePtr());
            return;
        }

        if (exist)
        {
            Plugin.Logger.LogInfo($"VR: eye textures resized to {width}x{height}");
            ReleaseGameEffectTextures();
        }
        for (var eye = 0; eye < 2; eye++)
        {
            DestroyTarget(_vrEye[eye]);
            DestroyTarget(_vrSubmit[eye]);
            var side = eye == 0 ? "Left" : "Right";
            _vrEye[eye] = CreateTarget($"NivalisVR_VrEye{side}", VrSession.EyeWidth, VrSession.EyeHeight, 24);
            _vrSubmit[eye] = CreateTarget($"NivalisVR_VrSubmit{side}", VrSession.EyeWidth, VrSession.EyeHeight, VrFlipY.Value ? 0 : 24);
        }

        VrSession.SetSourceTextures(_vrSubmit[0].GetNativeTexturePtr(), _vrSubmit[1].GetNativeTexturePtr());
    }

    /// <summary>
    /// The game's Hx light shafts build new textures whenever MainCamera's size changes and never free the old ones
    /// (the cause of the 0.4.0 black screen), so every resolution change from the menu leaked ~5 textures at the
    /// old size. Before the eye size changes, Hx releases its current ones; it builds new ones at the next render.
    /// </summary>
    [HideFromIl2Cpp]
    private static void ReleaseGameEffectTextures()
    {
        try
        {
            var cam = Camera.main;
            if (cam != null)
                Plugin.Logger.LogInfo($"VR: released the light-shaft textures of {ReleaseHxTextures(cam)} camera(s) before the resize");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"VR: couldn't release the light-shaft textures before the resize: {e.Message}");
        }
    }

    [HideFromIl2Cpp]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ReleaseHxTextures(Camera cam)
    {
        var count = 0;
        foreach (var hx in cam.GetComponentsInChildren<HxVolumetricCamera>())
        {
            hx.ReleaseTempTextures();
            count++;
        }
        return count;
    }

    // OpenXR is right-handed (+Y up, -Z forward); Unity is left-handed (+Z forward). Mirror the Z axis.
    [HideFromIl2Cpp]
    private static Vector3 ToUnityPosition(XrVector3f p) => new(p.x, p.y, -p.z);

    [HideFromIl2Cpp]
    private static Quaternion ToUnityRotation(XrQuaternionf q) => new(-q.x, -q.y, q.z, q.w);

    // Sign convention of Camera.lensShift relative to the projection's off-centre terms; verified on the first eye.
    private float _lensShiftSign = 1f;
    private bool _lensCalibrated;

    /// <summary>
    /// Sets up a physical lens whose implicit projection equals the eye's asymmetric frustum:
    /// sensor size = focal length * frustum extent (in tangent space), lens shift = frustum centre / extent,
    /// and gate fit None so the sensor maps exactly onto the eye texture.
    /// </summary>
    [HideFromIl2Cpp]
    private void ApplyEyeLens(Camera cam, XrFovf fov, Matrix4x4 expected)
    {
        const float focalLength = 10f; // arbitrary: only the ratios to the sensor size matter
        var tanLeft = Mathf.Tan(fov.angleLeft);
        var tanRight = Mathf.Tan(fov.angleRight);
        var tanUp = Mathf.Tan(fov.angleUp);
        var tanDown = Mathf.Tan(fov.angleDown);
        var width = tanRight - tanLeft;
        var height = tanUp - tanDown;

        // Frustum centre as a fraction of the frustum extent.
        var shift = new Vector2((tanRight + tanLeft) / (2f * width), (tanUp + tanDown) / (2f * height));

        cam.usePhysicalProperties = true;
        cam.gateFit = Camera.GateFitMode.None;
        cam.focalLength = focalLength;
        cam.sensorSize = new Vector2(focalLength * width, focalLength * height);
        cam.lensShift = shift * _lensShiftSign;

        if (_lensCalibrated) return;
        _lensCalibrated = true;

        var actual = cam.projectionMatrix;
        if (Mathf.Abs(actual.m02 + expected.m02) < Mathf.Abs(actual.m02 - expected.m02))
        {
            _lensShiftSign = -1f;
            cam.lensShift = shift * _lensShiftSign;
            actual = cam.projectionMatrix;
        }
        Plugin.Logger.LogInfo($"VR: physical lens calibration shiftSign={_lensShiftSign} " +
                              $"(expected m00={expected.m00:F4} m02={expected.m02:F4} m11={expected.m11:F4} m12={expected.m12:F4}; " +
                              $"lens m00={actual.m00:F4} m02={actual.m02:F4} m11={actual.m11:F4} m12={actual.m12:F4})");
    }

    /// <summary>The lens-related camera settings we temporarily change for an eye render.</summary>
    private struct LensState
    {
        private bool _physical;
        private float _fieldOfView, _focalLength;
        private Vector2 _sensorSize, _lensShift;
        private Camera.GateFitMode _gateFit;

        public static LensState Save(Camera cam) => new()
        {
            _physical = cam.usePhysicalProperties,
            _fieldOfView = cam.fieldOfView,
            _focalLength = cam.focalLength,
            _sensorSize = cam.sensorSize,
            _lensShift = cam.lensShift,
            _gateFit = cam.gateFit,
        };

        public void Restore(Camera cam)
        {
            cam.sensorSize = _sensorSize;
            cam.lensShift = _lensShift;
            cam.gateFit = _gateFit;
            cam.focalLength = _focalLength;
            cam.usePhysicalProperties = _physical;
            cam.ResetProjectionMatrix();
            cam.fieldOfView = _fieldOfView; // last, in case the physical settings above recomputed it
        }
    }

    /// <summary>Asymmetric off-axis projection (OpenGL convention, which Camera.projectionMatrix expects).</summary>
    [HideFromIl2Cpp]
    private static Matrix4x4 ProjectionFromFov(XrFovf fov, float near, float far)
    {
        var left = near * Mathf.Tan(fov.angleLeft);
        var right = near * Mathf.Tan(fov.angleRight);
        var top = near * Mathf.Tan(fov.angleUp);
        var bottom = near * Mathf.Tan(fov.angleDown);

        var m = new Matrix4x4();
        m.m00 = 2f * near / (right - left);
        m.m02 = (right + left) / (right - left);
        m.m11 = 2f * near / (top - bottom);
        m.m12 = (top + bottom) / (top - bottom);
        m.m22 = -(far + near) / (far - near);
        m.m23 = -2f * far * near / (far - near);
        m.m32 = -1f;
        return m;
    }

    // ---------------------------------------------------------------- monitor view in VR

    /// <summary>
    /// While MainCamera renders the left eye instead of the monitor view, our display camera copies the left eye to
    /// the monitor, cropped around its centre to the window's shape. The UI is drawn on top by UiRedirect's canvas.
    /// </summary>
    [HideFromIl2Cpp]
    private void SetMonitorBlitAttached(bool attached)
    {
        if (attached)
        {
            var source = EyeTarget(0);
            if (source == null)
                attached = false;
            else if (_monitorBlit == null || source != _monitorBlitSource ||
                     Screen.width != _monitorBlitScreenWidth || Screen.height != _monitorBlitScreenHeight)
                BuildMonitorBlit(source);
        }

        if (attached == _monitorBlitAttached || _monitorBlit == null) return;
        if (attached) _display.AddCommandBuffer(CameraEvent.AfterEverything, _monitorBlit);
        else _display.RemoveCommandBuffer(CameraEvent.AfterEverything, _monitorBlit);
        _monitorBlitAttached = attached;
    }

    [HideFromIl2Cpp]
    private void BuildMonitorBlit(RenderTexture source)
    {
        ReleaseMonitorBlit();

        var sourceAspect = (float)source.width / source.height;
        var screenAspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        var scale = Vector2.one;
        if (screenAspect > sourceAspect) scale.y = sourceAspect / screenAspect; // wide window: full width, middle band
        else scale.x = screenAspect / sourceAspect;
        var offset = new Vector2((1f - scale.x) * 0.5f, (1f - scale.y) * 0.5f);

        _monitorBlit = new CommandBuffer();
        _monitorBlit.name = "NivalisVR monitor view (left eye)";
        _monitorBlit.Blit(source, new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget), scale, offset);
        _monitorBlitSource = source;
        _monitorBlitScreenWidth = Screen.width;
        _monitorBlitScreenHeight = Screen.height;
    }

    [HideFromIl2Cpp]
    private void ReleaseMonitorBlit()
    {
        if (_monitorBlit == null) return;
        if (_monitorBlitAttached && _display != null)
            _display.RemoveCommandBuffer(CameraEvent.AfterEverything, _monitorBlit);
        _monitorBlit.Release();
        _monitorBlit = null;
        _monitorBlitAttached = false;
        _monitorBlitSource = null;
    }

    // ---------------------------------------------------------------- flat side-by-side preview

    [HideFromIl2Cpp]
    private void RenderPreviewFrame(Camera cam)
    {
        try
        {
            EnsurePreviewTargets();

            RenderPreviewEye(cam, _previewLeft, -0.5f);
            RenderPreviewEye(cam, _previewRight, +0.5f);

            Graphics.CopyTexture(_previewLeft, 0, 0, 0, 0, _previewLeft.width, _previewLeft.height, _previewSbs, 0, 0, 0, 0);
            Graphics.CopyTexture(_previewRight, 0, 0, 0, 0, _previewRight.width, _previewRight.height, _previewSbs, 0, 0, _previewLeft.width, 0);

            if (!_loggedFirstPreviewFrame)
            {
                _loggedFirstPreviewFrame = true;
                Plugin.Logger.LogInfo($"Side-by-side preview: first frame rendered from '{cam.name}', eye {_previewLeft.width}x{_previewLeft.height}, IPD {PreviewIpd.Value}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Side-by-side preview failed, disabling: {e}");
            _previewOn = false;
        }
    }

    [HideFromIl2Cpp]
    private static void RenderPreviewEye(Camera cam, RenderTexture target, float side)
    {
        var t = cam.transform;
        var originalPos = t.position;
        var originalTarget = cam.targetTexture;

        t.position = originalPos + t.right * (side * PreviewIpd.Value);
        cam.targetTexture = target;
        cam.aspect = (float)target.width / target.height;
        _manualRender = true;
        try
        {
            cam.Render();
        }
        finally
        {
            _manualRender = false;
            cam.targetTexture = originalTarget;
            cam.ResetAspect();
            t.position = originalPos;
        }
    }

    [HideFromIl2Cpp]
    private void EnsurePreviewTargets()
    {
        var eyeWidth = Screen.width / 2;
        var eyeHeight = Screen.height;
        if (_previewLeft != null && _previewLeft.width == eyeWidth && _previewLeft.height == eyeHeight) return;

        ReleasePreviewTargets();

        _previewLeft = CreateTarget("NivalisVR_PreviewLeft", eyeWidth, eyeHeight, 24);
        _previewRight = CreateTarget("NivalisVR_PreviewRight", eyeWidth, eyeHeight, 24);
        _previewSbs = CreateTarget("NivalisVR_PreviewSideBySide", eyeWidth * 2, eyeHeight, 24);

        _previewBlit = new CommandBuffer();
        _previewBlit.name = "NivalisVR side-by-side preview";
        _previewBlit.Blit(_previewSbs, new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
        SetPreviewBlitAttached(true);

        Plugin.Logger.LogInfo($"Side-by-side preview: created eye targets {eyeWidth}x{eyeHeight}");
    }

    [HideFromIl2Cpp]
    private void SetPreviewBlitAttached(bool attached)
    {
        if (attached == _previewBlitAttached || _previewBlit == null) return;
        if (attached) _display.AddCommandBuffer(CameraEvent.AfterEverything, _previewBlit);
        else _display.RemoveCommandBuffer(CameraEvent.AfterEverything, _previewBlit);
        _previewBlitAttached = attached;
    }

    [HideFromIl2Cpp]
    private void ReleasePreviewTargets()
    {
        if (_previewBlit != null)
        {
            if (_display != null && _previewBlitAttached)
                _display.RemoveCommandBuffer(CameraEvent.AfterEverything, _previewBlit);
            _previewBlit.Release();
            _previewBlit = null;
            _previewBlitAttached = false;
        }

        DestroyTarget(_previewLeft);
        DestroyTarget(_previewRight);
        DestroyTarget(_previewSbs);
        _previewLeft = _previewRight = _previewSbs = null;
    }

    // ---------------------------------------------------------------- shared

    [HideFromIl2Cpp]
    private static RenderTexture CreateTarget(string name, int width, int height, int depth)
    {
        var rt = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        rt.name = name;
        // Otherwise Resources.UnloadUnusedAssets (run on level loads) destroys it.
        rt.hideFlags = HideFlags.DontUnloadUnusedAsset;
        rt.Create();
        return rt;
    }

    [HideFromIl2Cpp]
    private static void DestroyTarget(RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        Object.Destroy(rt);
    }
}
