using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// Milestone 4: gets the game's Screen Space - Overlay UI (menus, HUD, dialogue) into VR.
///
/// While VR is running, every root overlay canvas is switched to Screen Space - Camera on our own UI camera,
/// which renders only UI into a transparent screen-sized texture. Keeping the texture the size of the screen
/// means the game's mouse raycasts still line up with the buttons. That texture is shown:
///  - in the headset as an OpenXR quad layer (see StereoRenderer / VrSession), and
///  - on the monitor through a full-screen overlay RawImage, so the flat view still has its UI.
/// A small cursor canvas draws a dot at the mouse position whenever the game shows its cursor.
/// When VR stops, every canvas is restored to how the game had it.
///
/// Deliberately a plain class (not an injected MonoBehaviour); StereoRenderer drives it from Update.
/// </summary>
internal class UiRedirect
{
    private const string OwnPrefix = "NivalisVR_";
    private const float CanvasPlaneDistance = 100f;
    private const float CursorPlaneDistance = 90f;
    private const float ScanInterval = 1f;
    // The camera is orthographic with one world unit per UI pixel, centred on (width/2, height/2), so the
    // redirected canvases keep the same world x/y as overlay canvases: x/y in world units == screen pixels.
    // Game code relies on that (e.g. the fast-travel map compares icon positions with the mouse position;
    // with the canvases elsewhere nothing could be hovered or clicked).
    // Far along z with a short view distance, so the UI camera can only ever see UI.
    // The 1 unit per pixel scale also keeps text crisp: with a perspective camera a UI pixel was ~0.0014 units,
    // too fine for floats far from the origin, and letters snapped to a ~3 px grid ("bouncing" text).
    private const float CanvasZ = 20000f;

    private struct Redirected
    {
        public Canvas Canvas;
        public RenderMode Mode;
        public Camera Camera;
        public float PlaneDistance;
    }

    private readonly List<Redirected> _redirected = new();
    private Camera _camera;
    private RenderTexture _texture;
    private Canvas _monitorCanvas;
    private RawImage _monitorImage;
    private Canvas _cursorCanvas;
    private RectTransform _cursor;
    private float _nextScan;
    private bool _cursorError;

    public bool Active { get; private set; }
    public RenderTexture Texture => _texture;

    /// <summary>Raised when the UI texture is (re)created, so the VR side can grab its native pointer.</summary>
    public event Action<RenderTexture> TextureCreated;

    /// <summary>Scan for new overlay canvases on the next Update instead of waiting for the next periodic scan.</summary>
    public void RequestScan() => _nextScan = 0f;

    public void Update(bool vrRunning)
    {
        if (vrRunning)
        {
            if (!Active)
            {
                Active = true;
                EnsureObjects();
                _camera.enabled = true;
                _monitorCanvas.gameObject.SetActive(true);
                _cursorCanvas.gameObject.SetActive(true);
                _nextScan = 0f;
                Plugin.Logger.LogInfo("UI redirect: active");
            }

            EnsureTexture();
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanInterval;
                ScanCanvases();
            }
            UpdateCursor();
        }
        else if (Active)
        {
            Active = false;
            RestoreAll();
            _camera.enabled = false;
            _monitorCanvas.gameObject.SetActive(false);
            _cursorCanvas.gameObject.SetActive(false);
            // Freed, so the next VR start creates it again and the headset gets its UI panel back (TextureCreated).
            ReleaseTexture();
            Plugin.Logger.LogInfo("UI redirect: inactive, canvases restored");
        }
    }

    private void EnsureObjects()
    {
        if (_camera != null) return;

        var cameraObject = new GameObject(OwnPrefix + "UiCamera");
        Object.DontDestroyOnLoad(cameraObject);
        PlaceCamera(cameraObject.transform, Screen.width, Screen.height);
        cameraObject.transform.rotation = Quaternion.identity;
        _camera = cameraObject.AddComponent<Camera>();
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        _camera.cullingMask = 1 << LayerMask.NameToLayer("UI");
        _camera.orthographic = true;
        _camera.orthographicSize = Screen.height / 2f; // updated with the texture: 1 world unit per UI pixel
        _camera.nearClipPlane = 1f;
        _camera.farClipPlane = 200f;
        _camera.depth = 50; // after MainCamera (0), before our display camera (100)
        _camera.renderingPath = RenderingPath.Forward;
        _camera.allowHDR = false;
        _camera.allowMSAA = false;
        _camera.useOcclusionCulling = false;

        // Monitor: draw the UI texture back over the flat view.
        var monitorObject = new GameObject(OwnPrefix + "MonitorUi");
        Object.DontDestroyOnLoad(monitorObject);
        _monitorCanvas = monitorObject.AddComponent<Canvas>();
        _monitorCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _monitorCanvas.sortingOrder = 0;
        var imageObject = new GameObject("Image");
        imageObject.transform.SetParent(monitorObject.transform, false);
        _monitorImage = imageObject.AddComponent<RawImage>();
        _monitorImage.raycastTarget = false;
        var imageRect = _monitorImage.rectTransform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        // Cursor: a dot on its own canvas, drawn by the UI camera on top of everything.
        var cursorCanvasObject = new GameObject(OwnPrefix + "CursorCanvas");
        Object.DontDestroyOnLoad(cursorCanvasObject);
        cursorCanvasObject.layer = LayerMask.NameToLayer("UI");
        _cursorCanvas = cursorCanvasObject.AddComponent<Canvas>();
        _cursorCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        _cursorCanvas.worldCamera = _camera;
        _cursorCanvas.planeDistance = CursorPlaneDistance;
        _cursorCanvas.sortingOrder = short.MaxValue;
        var cursorObject = new GameObject("Cursor");
        cursorObject.layer = cursorCanvasObject.layer;
        cursorObject.transform.SetParent(cursorCanvasObject.transform, false);
        var cursorImage = cursorObject.AddComponent<Image>();
        cursorImage.sprite = CreateCursorSprite();
        cursorImage.raycastTarget = false; // must never block the game's clicks
        _cursor = cursorImage.rectTransform;
        _cursor.sizeDelta = new Vector2(28f, 28f);

        _monitorCanvas.gameObject.SetActive(false);
        _cursorCanvas.gameObject.SetActive(false);
        _camera.enabled = false;
    }

    private void EnsureTexture()
    {
        if (_texture != null && _texture.width == Screen.width && _texture.height == Screen.height) return;

        ReleaseTexture();

        // Depth/stencil buffer is needed for UI Masks (stencil-based).
        _texture = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        _texture.name = OwnPrefix + "UiTexture";
        _texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _texture.Create();
        _camera.targetTexture = _texture;
        _camera.orthographicSize = _texture.height / 2f;
        PlaceCamera(_camera.transform, _texture.width, _texture.height);
        _monitorImage.texture = _texture;
        Plugin.Logger.LogInfo($"UI redirect: UI texture {_texture.width}x{_texture.height}");
        TextureCreated?.Invoke(_texture);
    }

    private void ReleaseTexture()
    {
        if (_texture == null) return;
        _camera.targetTexture = null;
        _monitorImage.texture = null;
        _texture.Release();
        Object.Destroy(_texture);
        _texture = null;
    }

    /// <summary>Canvas plane spans world x 0..width, y 0..height at z = CanvasZ, exactly like an overlay canvas.</summary>
    private static void PlaceCamera(Transform cameraTransform, int width, int height)
    {
        cameraTransform.position = new Vector3(width / 2f, height / 2f, CanvasZ - CanvasPlaneDistance);
    }

    private void ScanCanvases()
    {
        // Forget canvases the game destroyed.
        _redirected.RemoveAll(r => r.Canvas == null);

        foreach (var canvas in Object.FindObjectsOfType<Canvas>(true))
        {
            if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            var name = canvas.name;
            // Our own canvases stay where they are, except the mod's menu, which goes on the panel like the game's menus.
            if ((name.StartsWith(OwnPrefix) && name != ModMenu.CanvasName) || name.StartsWith("UniverseLib") || name.StartsWith("UnityExplorer")) continue;

            _redirected.Add(new Redirected
            {
                Canvas = canvas,
                Mode = canvas.renderMode,
                Camera = canvas.worldCamera,
                PlaneDistance = canvas.planeDistance,
            });
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _camera;
            canvas.planeDistance = CanvasPlaneDistance;
            _camera.cullingMask |= 1 << canvas.gameObject.layer;
            Plugin.Logger.LogInfo($"UI redirect: '{canvas.name}' (layer {LayerMask.LayerToName(canvas.gameObject.layer)}, order {canvas.sortingOrder}) -> UI camera");
        }
    }

    private void RestoreAll()
    {
        foreach (var r in _redirected)
        {
            if (r.Canvas == null) continue;
            r.Canvas.renderMode = r.Mode;
            r.Canvas.worldCamera = r.Camera;
            r.Canvas.planeDistance = r.PlaneDistance;
        }
        _redirected.Clear();
        _camera.cullingMask = 1 << LayerMask.NameToLayer("UI");
    }

    private void UpdateCursor()
    {
        var show = Cursor.visible && Cursor.lockState != CursorLockMode.Locked;
        _cursor.gameObject.SetActive(show);
        if (!show || _cursorError) return;

        try
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var position = mouse.position.ReadValue();
            _cursor.position = _camera.ScreenToWorldPoint(new Vector3(position.x, position.y, CursorPlaneDistance));
        }
        catch (Exception e)
        {
            _cursorError = true;
            Plugin.Logger.LogError($"UI redirect: cursor tracking failed, disabling it: {e.Message}");
        }
    }

    /// <summary>White dot with a dark outline, readable on any background.</summary>
    private static Sprite CreateCursorSprite()
    {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = OwnPrefix + "CursorTexture";
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var centre = (size - 1) / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Mathf.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                Color c;
                if (d <= 9f) c = Color.white;
                else if (d <= 12f) c = new Color(0.05f, 0.05f, 0.05f, 1f);
                else if (d <= 13f) c = new Color(0.05f, 0.05f, 0.05f, 13f - d);
                else c = new Color(0f, 0f, 0f, 0f);
                texture.SetPixel(x, y, c);
            }
        }
        texture.Apply();
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }
}
