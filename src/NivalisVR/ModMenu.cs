using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using NivalisVR.OpenXR;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// The mod's own menu (Insert by default, like UEVR): hide the HUD, recenter, eye resolution, and the UI panel's
/// distance and size (the panel carries the HUD, the game's menus and dialogue, and this menu). Changes apply right
/// away and are saved to the config file; each setting has a Default button.
///
/// It's an ordinary overlay canvas, so while VR runs UiRedirect puts it on the headset panel like the game's own
/// menus. The menu reads the mouse itself (position and left button) and hit-tests its buttons: during play the game
/// switches its own UI input off, so the EventSystem never delivered clicks to Unity Buttons here (first test).
/// While it's open the cursor is free, and every game control except the ones the UI input module uses (pointer,
/// clicks, scroll, navigate) is paused, so moving the mouse doesn't turn the camera and Esc doesn't open the game's
/// pause menu.
///
/// Only while VR runs (like UEVR, which only exists while it's injected): in flat, Insert does nothing, and the menu
/// closes if VR stops, so the game never looks altered outside VR.
///
/// Plain class (not an injected MonoBehaviour); StereoRenderer drives it.
/// </summary>
internal class ModMenu
{
    internal const string CanvasName = "NivalisVR_Menu";
    internal static ConfigEntry<Key> MenuKey;

    private const float ScaleStep = 0.05f, MinScale = 0.5f, MaxScale = 1.5f;
    // A resolution change rebuilds the eye textures (a short hitch, and the game's effects rebuild theirs), so it is
    // applied once the clicking stops rather than on every click.
    private const float ScaleApplyDelay = 0.6f;
    private const float PanelStep = 0.1f, MinPanel = 1.0f, MaxPanel = 5.0f;

    // Layout in reference pixels (1920x1080, scaled with the screen height).
    private const float Width = 880f, Height = 600f;
    private const float LabelX = -260f, ControlX = 175f, ControlWidth = 470f, RowHeight = 52f;

    private static readonly Color PanelColor = new(0.07f, 0.08f, 0.11f, 0.94f);
    private static readonly Color ButtonColor = new(0.20f, 0.25f, 0.33f, 1f);
    private static readonly Color ButtonHoverColor = new(0.30f, 0.40f, 0.55f, 1f);
    private static readonly Color ButtonPressedColor = new(0.14f, 0.17f, 0.23f, 1f);
    private static readonly Color TextColor = new(0.92f, 0.94f, 0.97f, 1f);
    private static readonly Color DimTextColor = new(0.62f, 0.66f, 0.72f, 1f);

    private readonly Action _onCanvasCreated;
    private GameObject _root;
    private Canvas _canvas;
    private Font _font;
    private Text _hudButton, _resolution, _resolutionDetail, _distance, _width, _fps;
    private bool _open;
    private bool _failed;

    // Game controls paused while the menu is open.
    private readonly HashSet<IntPtr> _uiActions = new();
    private readonly List<InputAction> _pausedActions = new();
    private readonly HashSet<IntPtr> _pausedPointers = new();
    private bool _savedCursorVisible;
    private CursorLockMode _savedCursorLock;

    private int _fpsFrames;
    private float _fpsSince;

    private bool _scalePending;
    private float _pendingScale, _applyScaleAt;

    // Buttons, hit-tested by HandlePointer.
    private sealed class MenuButton
    {
        public RectTransform Rect;
        public Image Image;
        public Action OnClick;
        public int State = -1; // 0 normal, 1 hover, 2 pressed (only set the colour when it changes)
    }

    private readonly List<MenuButton> _buttons = new();
    private MenuButton _pressed;

    // Diagnostics: frames in which the game had locked the cursor again since we freed it.
    private int _openFrames, _relockedFrames;

    public ModMenu(Action onCanvasCreated)
    {
        _onCanvasCreated = onCanvasCreated;
    }

    internal static void BindConfig(ConfigFile config)
    {
        MenuKey = config.Bind("UI", "MenuKey", Key.Insert,
            "Key that opens the mod's menu while VR is running (hide HUD, recenter, resolution, menu panel distance and size). " +
            "Any key name from Unity's Input System, e.g. Insert, Home, F7.");
    }

    public void Update(bool vrRunning)
    {
        if (_failed) return;
        try
        {
            if (!vrRunning)
            {
                if (_open)
                {
                    Plugin.Logger.LogInfo("Menu: VR stopped, closing the menu");
                    SetOpen(false);
                }
                return;
            }

            ApplyPendingScale(false);

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard[MenuKey.Value].wasPressedThisFrame) SetOpen(!_open);
                else if (_open && keyboard.escapeKey.wasPressedThisFrame) SetOpen(false);
            }

            if (!_open) return;
            _openFrames++;
            if (Cursor.lockState != CursorLockMode.None) _relockedFrames++;
            PauseGameInput();
            FreeCursor();
            HandlePointer();
            if (_open) Refresh();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>Called after all game logic each frame, so the cursor stays free even if the game locks it every frame.</summary>
    public void AfterGameLogic()
    {
        if (!_open || _failed) return;
        try
        {
            FreeCursor();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public void Close()
    {
        if (_open) SetOpen(false);
    }

    private void SetOpen(bool open)
    {
        if (open)
        {
            EnsureBuilt();
            _savedCursorVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            _open = true;
            _root.SetActive(true);
            CollectUiActions();
            PauseGameInput();
            FreeCursor();
            _fpsFrames = 0;
            _fpsSince = Time.unscaledTime;
            _openFrames = _relockedFrames = 0;
            _pressed = null;
            Refresh();
            Plugin.Logger.LogInfo($"Menu opened ({_pausedActions.Count} game controls paused; cursor was {_savedCursorLock}, visible={_savedCursorVisible}; " +
                                  $"canvas {_canvas.renderMode}, camera {(_canvas.worldCamera != null ? _canvas.worldCamera.name : "none")})");
        }
        else
        {
            _open = false;
            ApplyPendingScale(true);
            if (_root != null) _root.SetActive(false);
            ResumeGameInput();
            RestoreCursor();
            Plugin.Logger.LogInfo($"Menu closed (open {_openFrames} frames; the game had locked the cursor again in {_relockedFrames} of them)");
        }
    }

    private void Fail(Exception e)
    {
        _failed = true;
        Plugin.Logger.LogError($"Menu failed, turning it off for this session: {e}");
        try
        {
            _open = false;
            if (_root != null) _root.SetActive(false);
            ResumeGameInput();
            RestoreCursor();
        }
        catch (Exception cleanup)
        {
            Plugin.Logger.LogError($"Menu: cleanup failed: {cleanup.Message}");
        }
    }

    // ---------------------------------------------------------------- actions

    private void ToggleHud()
    {
        HudToggle.Toggle();
        Refresh();
    }

    private static void Recenter() => StereoRenderer.RequestRecenter();

    private void SetScale(float value)
    {
        _pendingScale = value;
        _scalePending = true;
        _applyScaleAt = Time.unscaledTime + ScaleApplyDelay;
        Refresh();
    }

    private float CurrentScale => _scalePending ? _pendingScale : StereoRenderer.VrRenderScale.Value;

    private void ApplyPendingScale(bool now)
    {
        if (!_scalePending || (!now && Time.unscaledTime < _applyScaleAt)) return;
        _scalePending = false;
        StereoRenderer.SetRenderScale(_pendingScale);
    }

    private void SetDistance(float value)
    {
        StereoRenderer.UiPanelDistance.Value = value;
        StereoRenderer.ReplaceUiPanel();
        Refresh();
    }

    private void SetWidth(float value)
    {
        // The panel's width is read every frame; its position stays.
        StereoRenderer.UiPanelWidth.Value = value;
        Refresh();
    }

    /// <summary>Adds delta and snaps to the step grid, so repeated clicks don't drift (0.95 + 0.05 is exactly 1.00).</summary>
    private static float Step(float value, float delta, float step, float min, float max) =>
        Mathf.Clamp(Mathf.Round((value + delta) / step) * step, min, max);

    private void Refresh()
    {
        SetText(_hudButton, HudToggle.IsHidden ? "Show HUD" : "Hide HUD");
        SetText(_resolution, $"{Mathf.RoundToInt(CurrentScale * 100f)}%");
        SetText(_resolutionDetail, !VrSession.IsInitialized
            ? "Applies when VR starts (100% is what SteamVR recommends for this game)"
            : _scalePending
                ? "Applying..."
                : $"{VrSession.EyeWidth} x {VrSession.EyeHeight} per eye (100% is what SteamVR recommends for this game)");
        SetText(_distance, $"{StereoRenderer.UiPanelDistance.Value:F1} m");
        SetText(_width, $"{StereoRenderer.UiPanelWidth.Value:F1} m");

        _fpsFrames++;
        var elapsed = Time.unscaledTime - _fpsSince;
        if (elapsed >= 0.5f)
        {
            SetText(_fps, $"{Mathf.RoundToInt(_fpsFrames / elapsed)} fps");
            _fpsFrames = 0;
            _fpsSince = Time.unscaledTime;
        }
    }

    // Text.text crosses into IL2CPP; only set it when it actually changes.
    private readonly Dictionary<IntPtr, string> _shownText = new();

    private void SetText(Text text, string value)
    {
        if (_shownText.TryGetValue(text.Pointer, out var shown) && shown == value) return;
        _shownText[text.Pointer] = value;
        text.text = value;
    }

    // ---------------------------------------------------------------- mouse

    /// <summary>Hover colours and clicks (press and release on the same button), from the raw mouse.</summary>
    private void HandlePointer()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        var position = mouse.position.ReadValue();
        // Redirected to the UI camera in VR (its texture is screen-sized, so mouse pixels map 1:1), overlay otherwise.
        var camera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
        MenuButton hovered = null;
        foreach (var button in _buttons)
        {
            if (!RectTransformUtility.RectangleContainsScreenPoint(button.Rect, position, camera)) continue;
            hovered = button;
            break;
        }

        MenuButton clicked = null;
        if (mouse.leftButton.wasPressedThisFrame) _pressed = hovered;
        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (_pressed != null && _pressed == hovered) clicked = _pressed;
            _pressed = null;
        }

        foreach (var button in _buttons)
        {
            var state = button == hovered ? (button == _pressed ? 2 : 1) : 0;
            if (state == button.State) continue;
            button.State = state;
            button.Image.color = state == 2 ? ButtonPressedColor : state == 1 ? ButtonHoverColor : ButtonColor;
        }

        clicked?.OnClick(); // last: it may close the menu
    }

    // ---------------------------------------------------------------- game input and cursor

    /// <summary>The actions the game's UI input module reads; everything else is paused while the menu is open.</summary>
    private void CollectUiActions()
    {
        _uiActions.Clear();
        var eventSystem = EventSystem.current;
        var module = eventSystem != null ? eventSystem.currentInputModule : null;
        var uiModule = module != null ? module.TryCast<InputSystemUIInputModule>() : null;
        if (uiModule == null)
        {
            Plugin.Logger.LogWarning("Menu: the game's UI input module wasn't found; game controls stay active while the menu is open");
            return;
        }

        KeepAction(uiModule.point);
        KeepAction(uiModule.leftClick);
        KeepAction(uiModule.rightClick);
        KeepAction(uiModule.middleClick);
        KeepAction(uiModule.scrollWheel);
        KeepAction(uiModule.move);
        KeepAction(uiModule.submit);
        KeepAction(uiModule.cancel);
    }

    private void KeepAction(InputActionReference reference)
    {
        var action = reference != null ? reference.action : null;
        if (action != null) _uiActions.Add(action.Pointer);
    }

    /// <summary>Disables every enabled game action except the UI ones. Runs every frame, in case the game re-enables some.</summary>
    private void PauseGameInput()
    {
        if (_uiActions.Count == 0) return; // without the UI module's actions we can't tell what to keep
        var enabled = InputSystem.ListEnabledActions();
        for (var i = 0; i < enabled.Count; i++)
        {
            var action = enabled[i];
            if (action == null || _uiActions.Contains(action.Pointer)) continue;
            action.Disable();
            if (_pausedPointers.Add(action.Pointer)) _pausedActions.Add(action);
        }
    }

    private void ResumeGameInput()
    {
        foreach (var action in _pausedActions)
        {
            try
            {
                if (!action.enabled) action.Enable();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Menu: couldn't re-enable a game control: {e.Message}");
            }
        }
        _pausedActions.Clear();
        _pausedPointers.Clear();
    }

    private static void FreeCursor()
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }

    private void RestoreCursor()
    {
        Cursor.lockState = _savedCursorLock;
        Cursor.visible = _savedCursorVisible;
        try
        {
            LetGameSetCursor();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Menu: the game's cursor update failed ({e.Message}); restored the cursor as it was");
        }
    }

    /// <summary>The game decides itself whether the cursor should be locked (it knows which of its panels are open).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LetGameSetCursor()
    {
        var ui = Object.FindObjectOfType<Nivalis.UIManager>();
        if (ui != null) ui.UpdateCursorState();
    }

    // ---------------------------------------------------------------- building the canvas

    private void EnsureBuilt()
    {
        if (_root != null) return;

        _font = FindFont();
        _root = new GameObject(CanvasName);
        Object.DontDestroyOnLoad(_root);
        _root.layer = LayerMask.NameToLayer("UI");
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 30000; // above the game's UI, below our cursor
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;
        // Not for our buttons (see HandlePointer): it makes the panel block the game's UI behind it.
        _root.AddComponent<GraphicRaycaster>();

        // The background takes clicks too, so nothing behind the menu gets them.
        var panel = AddRect(_root.transform, "Panel", Vector2.zero, new Vector2(Width, Height));
        panel.gameObject.AddComponent<Image>().color = PanelColor;
        var p = panel.transform;

        AddText(p, "Title", $"Nivalis VR {Plugin.Version}", new Vector2(0f, 255f), new Vector2(Width, 50f), 34, TextAnchor.MiddleCenter, TextColor);
        AddText(p, "Hint", $"{MenuKey.Value} or Esc closes this menu", new Vector2(0f, 215f), new Vector2(Width, 30f), 20, TextAnchor.MiddleCenter, DimTextColor);

        AddLabel(p, "HUD (F5)", 150f);
        _hudButton = AddButton(p, "HudButton", "Hide HUD", new Vector2(ControlX, 150f), new Vector2(ControlWidth, RowHeight), ToggleHud);

        AddLabel(p, "View (F10)", 80f);
        AddButton(p, "RecenterButton", "Recenter", new Vector2(ControlX, 80f), new Vector2(ControlWidth, RowHeight), Recenter);

        AddLabel(p, "Resolution", 10f);
        _resolution = AddStepper(p, "Resolution", 10f, () => CurrentScale, StereoRenderer.VrRenderScale, ScaleStep, MinScale, MaxScale, SetScale);
        _resolutionDetail = AddText(p, "ResolutionDetail", "", new Vector2(0f, -30f), new Vector2(Width - 40f, 30f), 19, TextAnchor.MiddleCenter, DimTextColor);

        // The panel carries everything: HUD, the game's menus and dialogue, and this menu.
        AddLabel(p, "Panel distance", -85f);
        _distance = AddStepper(p, "Distance", -85f, () => StereoRenderer.UiPanelDistance.Value, StereoRenderer.UiPanelDistance, PanelStep, MinPanel, MaxPanel, SetDistance);

        AddLabel(p, "Panel size", -155f);
        _width = AddStepper(p, "Size", -155f, () => StereoRenderer.UiPanelWidth.Value, StereoRenderer.UiPanelWidth, PanelStep, MinPanel, MaxPanel, SetWidth);

        _fps = AddText(p, "Fps", "", new Vector2(LabelX, -235f), new Vector2(300f, RowHeight), 24, TextAnchor.MiddleLeft, DimTextColor);
        AddButton(p, "CloseButton", "Close", new Vector2(ControlX, -235f), new Vector2(ControlWidth, RowHeight), () => SetOpen(false));

        _root.SetActive(false);
        Plugin.Logger.LogInfo("Menu: created");
        _onCanvasCreated?.Invoke();
    }

    private static Font FindFont()
    {
        try
        {
            var builtin = Resources.GetBuiltinResource(Il2CppType.Of<Font>(), "Arial.ttf");
            var font = builtin != null ? builtin.TryCast<Font>() : null;
            if (font != null) return font;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Menu: built-in font not available ({e.Message})");
        }

        var fonts = Resources.FindObjectsOfTypeAll<Font>();
        if (fonts.Length == 0) throw new Exception("no font found for the menu");
        Plugin.Logger.LogInfo($"Menu: using the game's font '{fonts[0].name}'");
        return fonts[0];
    }

    private static RectTransform AddRect(Transform parent, string name, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    private Text AddText(Transform parent, string name, string value, Vector2 position, Vector2 size, int fontSize, TextAnchor alignment, Color color)
    {
        var rect = AddRect(parent, name, position, size);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = _font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    private void AddLabel(Transform parent, string label, float y) =>
        AddText(parent, label, label, new Vector2(LabelX, y), new Vector2(300f, RowHeight), 26, TextAnchor.MiddleLeft, TextColor);

    /// <summary>A button (hit-tested by HandlePointer); returns its label so the caller can change it.</summary>
    private Text AddButton(Transform parent, string name, string label, Vector2 position, Vector2 size, Action onClick)
    {
        var rect = AddRect(parent, name, position, size);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = ButtonColor;
        _buttons.Add(new MenuButton { Rect = rect, Image = image, OnClick = onClick });
        return AddText(rect.transform, "Label", label, Vector2.zero, size, 26, TextAnchor.MiddleCenter, TextColor);
    }

    /// <summary>[-] value [+] [Default] for a float setting (current = the value shown); returns the value text.</summary>
    private Text AddStepper(Transform parent, string name, float y, Func<float> current, ConfigEntry<float> setting, float step, float min, float max, Action<float> apply)
    {
        var left = ControlX - ControlWidth / 2f;
        AddButton(parent, name + "Down", "-", new Vector2(left + 30f, y), new Vector2(60f, RowHeight), () => apply(Step(current(), -step, step, min, max)));
        var value = AddText(parent, name + "Value", "", new Vector2(left + 125f, y), new Vector2(110f, RowHeight), 26, TextAnchor.MiddleCenter, TextColor);
        AddButton(parent, name + "Up", "+", new Vector2(left + 220f, y), new Vector2(60f, RowHeight), () => apply(Step(current(), step, step, min, max)));
        AddButton(parent, name + "Default", "Default", new Vector2(left + 380f, y), new Vector2(180f, RowHeight), () => apply((float)setting.DefaultValue));
        return value;
    }
}
