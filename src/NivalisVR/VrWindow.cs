using System;
using BepInEx.Configuration;
using UnityEngine;

namespace NivalisVR;

/// <summary>
/// While VR is running, shrinks the game window to the UI panel's pixel size in the headset.
///  - The flat (monitor) render of MainCamera becomes much cheaper.
///  - The game's UI texture is the window size, so the UI is drawn at the headset's pixel density
///    (even, crisp text instead of minified glyphs), and mouse clicks still line up without remapping.
/// The original window size/mode is restored when VR stops and (best effort) on quit.
/// Uses Screen.SetResolution only; the game's own settings.ini is never touched.
/// </summary>
internal static class VrWindow
{
    internal static ConfigEntry<bool> ShrinkInVr;
    internal static ConfigEntry<float> PanelResolutionScale;

    // Screen.SetResolution takes effect a few frames later; only judge the result after this many frames.
    private const int SettleFrames = 30;
    // The game re-applies its saved resolution at startup and on every level load, so re-shrink whenever it
    // changes the size. Only give up if it happens in rapid succession (something fighting us every frame).
    private const int BurstLimit = 3;
    private const float BurstWindowSeconds = 10f;
    private static readonly System.Collections.Generic.Queue<float> RecentReapplies = new();
    private static bool _gaveUp;

    private static bool _applied;
    private static int _originalWidth, _originalHeight;
    private static FullScreenMode _originalMode;
    private static int _targetWidth, _targetHeight;
    private static int _appliedFrame;
    private static bool _settled;
    private static int _settledWidth, _settledHeight;
    private static bool _quitHooked;

    internal static void BindConfig(ConfigFile config)
    {
        ShrinkInVr = config.Bind("Monitor", "ShrinkWindowInVr", true,
            "While VR is running, switch the game to a small window sized to the UI panel's resolution in the headset " +
            "(saves GPU time on the flat render and makes menu text crisp). Restored when VR stops.");
        PanelResolutionScale = config.Bind("UI", "PanelResolutionScale", 1.0f,
            "UI resolution relative to the headset's pixel density on the panel (1.0 = one UI pixel per headset pixel). " +
            "Only used when ShrinkWindowInVr is on.");
    }

    /// <summary>
    /// Called each VR frame with the panel's horizontal size in headset pixels; applies the window size once.
    /// </summary>
    public static void EnsureShrunk(float panelPixelWidth)
    {
        if (!ShrinkInVr.Value || panelPixelWidth <= 0f) return;

        if (!_applied)
        {
            Shrink(panelPixelWidth);
            return;
        }

        if (Time.frameCount < _appliedFrame + SettleFrames) return;

        if (!_settled)
        {
            if (Screen.width == _targetWidth && Screen.height == _targetHeight)
            {
                _settled = true;
                _settledWidth = Screen.width;
                _settledHeight = Screen.height;
                return;
            }
            // Not the size we asked for: the game probably overrode it while ours was still being applied.
        }
        else if (Screen.width == _settledWidth && Screen.height == _settledHeight)
        {
            return;
        }

        // Something else (the game applying its saved settings) changed the resolution.
        if (_gaveUp) return;
        var now = Time.unscaledTime;
        while (RecentReapplies.Count > 0 && now - RecentReapplies.Peek() > BurstWindowSeconds)
            RecentReapplies.Dequeue();
        if (RecentReapplies.Count >= BurstLimit)
        {
            _gaveUp = true;
            Plugin.Logger.LogWarning($"VR window: resolution changed {BurstLimit + 1} times within {BurstWindowSeconds} s ({Screen.width}x{Screen.height}); leaving it alone for this session");
            return;
        }
        RecentReapplies.Enqueue(now);
        Plugin.Logger.LogInfo($"VR window: the game changed the resolution to {Screen.width}x{Screen.height} {Screen.fullScreenMode}; shrinking again (that size will be restored later)");
        _applied = false;
        Shrink(panelPixelWidth);
    }

    private static void Shrink(float panelPixelWidth)
    {
        _originalWidth = Screen.width;
        _originalHeight = Screen.height;
        _originalMode = Screen.fullScreenMode;

        // Keep the current aspect ratio so the game's UI layout is unchanged.
        _targetWidth = Mathf.Clamp(Mathf.RoundToInt(panelPixelWidth * PanelResolutionScale.Value), 640, 4096);
        _targetHeight = Mathf.Max(1, Mathf.RoundToInt(_targetWidth * (float)_originalHeight / _originalWidth));
        Screen.SetResolution(_targetWidth, _targetHeight, FullScreenMode.Windowed);
        _applied = true;
        _appliedFrame = Time.frameCount;
        _settled = false;
        HookQuit();
        Plugin.Logger.LogInfo($"VR window: {_originalWidth}x{_originalHeight} {_originalMode} -> {_targetWidth}x{_targetHeight} Windowed");
    }

    public static void Restore()
    {
        if (!_applied) return;
        _applied = false;
        Screen.SetResolution(_originalWidth, _originalHeight, _originalMode);
        Plugin.Logger.LogInfo($"VR window: restored {_originalWidth}x{_originalHeight} {_originalMode}");
    }

    private static void HookQuit()
    {
        if (_quitHooked) return;
        _quitHooked = true;
        try
        {
            Application.add_quitting((Il2CppSystem.Action)(Action)Restore);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"VR window: could not hook Application.quitting ({e.Message}); the window size may persist after quitting in VR");
        }
    }
}
