using System;
using System.Runtime.CompilerServices;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// F5 hides or shows the player HUD, for walking around with nothing on screen. Goes through the game's own
/// UIManager.HideHud/ShowHud: dialogue, menus and NPC names keep working (tested on Update #3). The game's
/// ToggleUiShown was tried too but hides dialogue choices and menus as well, so it isn't used.
/// The game doesn't report whether the HUD is hidden, so we track it ourselves.
/// Only while VR runs: in flat the game should look exactly like the unmodded game (F5 is a common quicksave key, a
/// vanishing HUD would look like the game broke), so a HUD hidden in VR comes back when VR stops.
/// Everything that touches game types is in Apply: if a game update renames them, the key only logs an error
/// instead of breaking the rest of the mod.
/// </summary>
internal static class HudToggle
{
    private static bool _hudHidden;

    internal static bool IsHidden => _hudHidden;

    internal static void Toggle() => Set(!_hudHidden, "F5");

    /// <summary>Called every frame while VR isn't running; shows the HUD again if we hid it.</summary>
    internal static void RestoreForFlat()
    {
        if (_hudHidden) Set(false, "VR stopped");
    }

    private static void Set(bool hidden, string reason)
    {
        try
        {
            Apply(hidden, reason);
        }
        catch (Exception e)
        {
            _hudHidden = false; // don't retry every frame after a game update broke it
            Plugin.Logger.LogError($"HUD toggle failed: {e.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Apply(bool hidden, string reason)
    {
        var ui = Object.FindObjectOfType<Nivalis.UIManager>();
        if (ui == null)
        {
            _hudHidden = false;
            Plugin.Logger.LogWarning("HUD toggle: the game's UIManager wasn't found");
            return;
        }

        _hudHidden = hidden;
        if (hidden) ui.HideHud();
        else ui.ShowHud();
        Plugin.Logger.LogInfo($"HUD {(hidden ? "hidden" : "shown")} ({reason})");
    }
}
