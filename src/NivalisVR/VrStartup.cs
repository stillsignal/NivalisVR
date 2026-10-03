using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace NivalisVR;

/// <summary>
/// Decides whether VR starts automatically when the game launches.
/// Creating an OpenXR instance with SteamVR as the active runtime launches SteamVR, so starting VR unconditionally
/// made the game impossible to play flat. If SteamVR is the active runtime but isn't running, the game starts flat;
/// F11 starts VR later (and launches SteamVR then). Other runtimes (Meta Quest Link, Virtual Desktop, ...) normally
/// run in the background, so VR is started as before.
/// </summary>
internal static class VrStartup
{
    private const string ActiveRuntimeKey = @"SOFTWARE\Khronos\OpenXR\1";

    public static bool ShouldStartAutomatically()
    {
        try
        {
            var runtime = ActiveRuntimeManifest();
            if (runtime == null)
            {
                Plugin.Logger.LogInfo("VR: no active OpenXR runtime found in the registry; trying to start VR anyway");
                return true;
            }

            if (runtime.IndexOf("steamxr", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Plugin.Logger.LogInfo($"VR: active OpenXR runtime is {runtime}; starting VR");
                return true;
            }

            if (Process.GetProcessesByName("vrserver").Length > 0)
            {
                Plugin.Logger.LogInfo("VR: SteamVR is running; starting VR");
                return true;
            }

            Plugin.Logger.LogInfo("VR: SteamVR isn't running, so the game starts without VR. " +
                                  "To switch to VR, start SteamVR and press F11.");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"VR: could not check the VR runtime ({e.Message}); starting VR");
            return true;
        }
    }

    /// <summary>Path of the active runtime's manifest (XR_RUNTIME_JSON overrides the registry, as in the loader).</summary>
    private static string ActiveRuntimeManifest()
    {
        var overridePath = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.LocalMachine.OpenSubKey(ActiveRuntimeKey);
        return key?.GetValue("ActiveRuntime") as string;
    }
}
