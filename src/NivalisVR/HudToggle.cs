using System;
using System.Runtime.CompilerServices;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// F5 hides or shows the player HUD, for walking around with nothing on screen. Goes through the game's own
/// UIManager.HideHud/ShowHud: dialogue, menus and NPC names keep working (tested on Update #3). The game's
/// ToggleUiShown was tried too but hides dialogue choices and menus as well, so it isn't used.
/// The game doesn't report whether the HUD is hidden, so we track it ourselves. Works in VR and flat.
/// Everything that touches game types is in Apply: if a game update renames them, the key only logs an error
/// instead of breaking the rest of the mod.
/// </summary>
internal static class HudToggle
{
    private static bool _hudHidden;

    internal static bool IsHidden => _hudHidden;

    internal static void Toggle()
    {
        try
        {
            Apply();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"HUD toggle failed: {e.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Apply()
    {
        var ui = Object.FindObjectOfType<Nivalis.UIManager>();
        if (ui == null)
        {
            Plugin.Logger.LogWarning("HUD toggle: the game's UIManager wasn't found");
            return;
        }

        _hudHidden = !_hudHidden;
        if (_hudHidden) ui.HideHud();
        else ui.ShowHud();
        Plugin.Logger.LogInfo($"HUD {(_hudHidden ? "hidden" : "shown")} (F5)");
    }
}
