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
/// side-by-side preview on the flat screen (milestone 2, used when VR isn't running).
///
/// The game's own render of MainCamera is left untouched so gameplay code (Camera.main, ScreenPointToRay, etc.)
/// sees exactly what it expects. This component lives on our own display camera (depth 100). When that camera is
/// about to cull (after MainCamera's normal render, and after Cinemachine has positioned it), we temporarily move
/// MainCamera to each eye's pose, point it at an eye RenderTexture, call Render(), then restore it.
/// </summary>
public class StereoRenderer : MonoBehaviour
{
    internal static ConfigEntry<bool> PreviewEnabled;
    internal static ConfigEntry<float> Ipd;
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

    // Headset rendering: the camera renders into _vrEye, which is flipped into _vrSubmit (copied to the swapchain).
    private readonly RenderTexture[] _vrEye = new RenderTexture[2];
    private readonly RenderTexture[] _vrSubmit = new RenderTexture[2];
    private bool _vrInitAttempted;
    private bool _loggedFirstVrFrame;

    // UI panel: the game's overlay UI is redirected into a texture (UiRedirect), flipped into _uiSubmit,
    // and shown in the headset as a quad layer fixed in tracking space in front of the recentered view.
    internal static ConfigEntry<float> UiPanelDistance;
    internal static ConfigEntry<float> UiPanelWidth;
    internal static ConfigEntry<float> UiPanelHeightOffset;
    private UiRedirect _ui;
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
        PreviewEnabled = config.Bind("Stereo", "PreviewEnabled", false,
            "When VR isn't running, show left/right eye views side by side on the flat screen. Toggle in game with F9.");
        Ipd = config.Bind("Stereo", "Ipd", 0.064f,
            "Eye distance in metres for the flat side-by-side preview (the headset uses its own IPD).");
        VrEnabled = config.Bind("VR", "Enabled", true,
            "Start VR automatically when the game launches. With SteamVR this only happens if SteamVR is already running; " +
            "otherwise the game starts without VR. F11 starts VR at any time.");
        VrRenderScale = config.Bind("VR", "RenderScale", 1.0f,
            "Multiplier on the runtime's recommended per-eye resolution (1.0 = exactly what SteamVR recommends for this app; set the resolution in SteamVR or a SteamVR add-on instead).");
        VrFlipY = config.Bind("VR", "FlipY", true,
            "Flip eye images vertically before submitting. Unity stores D3D11 render textures bottom-up; turn off if the headset image is upside down.");
        UiPanelDistance = config.Bind("UI", "PanelDistance", 2.0f,
            "Distance in metres from your head (at recenter) to the menu/HUD panel.");
        UiPanelWidth = config.Bind("UI", "PanelWidth", 2.6f,
            "Width of the menu/HUD panel in metres (height follows the screen's aspect ratio).");
        UiPanelHeightOffset = config.Bind("UI", "PanelHeightOffset", -0.1f,
            "Vertical offset of the panel centre from eye height, in metres (negative = lower).");
        CaptureKeyEnabled = config.Bind("Debug", "CaptureKey", false,
            "F6 saves the current eye images (and the game's sky/glow helper textures) as PNG files in " +
            "BepInEx/NivalisVR-captures, for bug reports.");
        VrWindow.BindConfig(config);
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

        _ui = new UiRedirect();
        _ui.TextureCreated += OnUiTextureCreated;
    }

    private void OnDestroy()
    {
        Plugin.Logger.LogInfo("Stereo renderer: shut down");
        if (_preCullCallback != null && Camera.onPreCull != null)
            Camera.onPreCull = Camera.onPreCull - _preCullCallback;
        if (_preRenderCallback != null && Camera.onPreRender != null)
            Camera.onPreRender = Camera.onPreRender - _preRenderCallback;
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
        if (!_vrInitAttempted && VrEnabled.Value)
        {
            _vrInitAttempted = true;
            if (VrStartup.ShouldStartAutomatically())
                VrSession.Initialize(VrRenderScale.Value);
        }

        VrSession.PollEvents();
        HandleHotkeys();

        try
        {
            _ui.Update(VrSession.IsRunning);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"UI redirect failed: {e}");
        }

        if (!VrSession.IsRunning)
            VrWindow.Restore();

        // The flat preview is only drawn when the headset isn't being driven.
        SetPreviewBlitAttached(PreviewEnabled.Value && !VrSession.IsRunning && _previewBlit != null);
    }

    [HideFromIl2Cpp]
    private void HandleHotkeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.f9Key.wasPressedThisFrame)
        {
            PreviewEnabled.Value = !PreviewEnabled.Value;
            Plugin.Logger.LogInfo($"Side-by-side preview {(PreviewEnabled.Value ? "enabled" : "disabled")}");
        }

        if (keyboard.f10Key.wasPressedThisFrame)
        {
            _needsRecenter = true;
            Plugin.Logger.LogInfo("VR: recenter requested");
        }

        if (CaptureKeyEnabled.Value && keyboard.f6Key.wasPressedThisFrame && VrSession.IsRunning)
        {
            _captureName = $"{DateTime.Now:yyyyMMdd-HHmmss}";
            Plugin.Logger.LogInfo($"Capture requested ({_captureName}), saving to {DebugCapture.Folder}");
        }

        if (keyboard.f11Key.wasPressedThisFrame && !VrSession.IsInitialized)
        {
            Plugin.Logger.LogInfo("VR: starting VR (F11)");
            VrSession.Initialize(VrRenderScale.Value);
        }
    }

    [HideFromIl2Cpp]
    private void OnAnyCameraPreCull(Camera culling)
    {
        if (culling != _display) return;

        var cam = Camera.main;
        if (cam == null) return;

        if (VrSession.IsRunning)
            RenderVrFrame(cam);
        else if (PreviewEnabled.Value)
            RenderPreviewFrame(cam);
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
    private void RenderVrFrame(Camera cam)
    {
        int slot;
        try
        {
            slot = VrSession.WaitAndLocate();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"VR: wait/locate failed: {e}");
            return;
        }
        if (slot < 0) return;

        // From here on the frame must be submitted (even empty) so begin/end stay balanced with xrWaitFrame.
        try
        {
            ref var frame = ref VrSession.GetSlot(slot);
            if (frame.ShouldRender)
            {
                EnsureVrTargets();
                UpdateHeadPose(frame.Left, frame.Right);

                RenderVrEye(cam, frame.Left, 0);
                RenderVrEye(cam, frame.Right, 1);
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

        // Head pose relative to the recentered origin, applied on top of the game camera.
        var inverseOrigin = Quaternion.Inverse(_originRotation);
        var localPosition = inverseOrigin * (ToUnityPosition(view.pose.position) - _originPosition);
        var localRotation = inverseOrigin * ToUnityRotation(view.pose.orientation);

        var target = VrFlipY.Value ? _vrEye[eye] : _vrSubmit[eye];
        var originalLens = LensState.Save(cam);
        try
        {
            t.position = basePosition + baseRotation * localPosition;
            t.rotation = baseRotation * localRotation;

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
            cam.Render();

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

    [HideFromIl2Cpp]
    private void EnsureVrTargets()
    {
        if (_vrSubmit[0] != null && _vrSubmit[1] != null && (!VrFlipY.Value || (_vrEye[0] != null && _vrEye[1] != null)))
            return;

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
                Plugin.Logger.LogInfo($"Side-by-side preview: first frame rendered from '{cam.name}', eye {_previewLeft.width}x{_previewLeft.height}, IPD {Ipd.Value}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Side-by-side preview failed, disabling: {e}");
            PreviewEnabled.Value = false;
        }
    }

    [HideFromIl2Cpp]
    private static void RenderPreviewEye(Camera cam, RenderTexture target, float side)
    {
        var t = cam.transform;
        var originalPos = t.position;
        var originalTarget = cam.targetTexture;

        t.position = originalPos + t.right * (side * Ipd.Value);
        cam.targetTexture = target;
        cam.aspect = (float)target.width / target.height;
        try
        {
            cam.Render();
        }
        finally
        {
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
