using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// Diagnostics for the "black screen after ~15 minutes in VR" bug: Unity's graphics resource IDs (max 1,048,575 per
/// session) run out because textures keep getting created while VR runs (Player.log: "Resource ID out of range").
/// Every few seconds this logs how many textures were created and which ones; once per report it also splits the
/// new temporary render textures of one VR frame into left eye, right eye and the rest of the frame (which includes
/// the game's own render for the monitor). Shift+F8 toggles it; [Debug] ResourceStats turns it on at startup.
/// </summary>
internal static class ResourceStats
{
    internal static ConfigEntry<bool> Enabled;

    // Counted by StereoRenderer, reported and reset with each report.
    internal static int GameCameraRenders;   // MainCamera renders started by Unity itself (not our eye renders)
    internal static int LeftEyeByGame;       // VR frames whose left eye came from MainCamera's own render
    internal static int LeftEyeSeparate;     // ... set up for that, but Unity didn't render MainCamera; rendered separately
    internal static int LeftEyeUndetected;   // ... MainCamera did render, but its end wasn't seen; rendered again

    private const float IntervalSeconds = 5f;
    private const int TopGroups = 8;

    // Unity names pooled temporary render textures "TempBuffer <serial> <w>x<h>"; the serial counts creations.
    private static readonly Regex TempBufferName = new(@"^TempBuffer (\d+)", RegexOptions.Compiled);

    private static bool _wasEnabled;
    private static float _nextReportAt;
    private static int _lastFrame;
    private static float _lastTime;
    private static int _lastMaxTempSerial = -1;
    private static int _lastProbeId;
    private static HashSet<int> _knownRenderTextures = new();
    private static HashSet<int> _knownTextures2D = new();
    private static bool _failed;

    // Per-frame split, armed after each report and filled in during the next VR frame.
    private static bool _splitArmed;
    private static readonly List<(string Step, int MaxTempSerial, HashSet<int> Ids)> _marks = new();

    internal static void BindConfig(ConfigFile config)
    {
        Enabled = config.Bind("Debug", "ResourceStats", false,
            "Every 5 seconds, log how many textures the game creates and which ones (diagnostic for the black " +
            "screen after ~15 minutes in VR). Shift+F8 toggles it in game.");
    }

    internal static void Toggle()
    {
        Enabled.Value = !Enabled.Value;
        Plugin.Logger.LogInfo($"Resource stats {(Enabled.Value ? "enabled (report every 5 s)" : "disabled")}");
    }

    /// <summary>Called once per frame; writes a report every IntervalSeconds while enabled.</summary>
    internal static void Update(bool vrRunning)
    {
        if (_failed) return;

        var enabled = Enabled.Value;
        if (enabled && !_wasEnabled) Reset();
        _wasEnabled = enabled;
        if (!enabled || Time.realtimeSinceStartup < _nextReportAt) return;

        try
        {
            Report(vrRunning);
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Logger.LogError($"Resource stats failed, disabled for this session: {e}");
        }
        _nextReportAt = Time.realtimeSinceStartup + IntervalSeconds;
    }

    /// <summary>
    /// Start of a VR frame (before the eye renders). The first call starts a split; the next one closes it, so the
    /// last step covers everything after our eye renders, including the game's own render for the monitor.
    /// </summary>
    internal static void FrameStart() => Mark(_marks.Count == 0 ? "start" : "rest of frame (other cameras, UI)");

    /// <summary>Marks a step of the VR frame for the per-frame split (no-op unless a split is armed).</summary>
    internal static void Mark(string step)
    {
        if (!_splitArmed || _failed) return;

        try
        {
            var (maxSerial, ids) = SampleRenderTextures();
            _marks.Add((step, maxSerial, ids));
            if (_marks.Count < 4) return;

            // Steps: frame start, after left eye, after right eye, next frame start.
            var sb = new StringBuilder("Resource stats: one VR frame:");
            for (var i = 1; i < _marks.Count; i++)
            {
                var created = _marks[i].MaxTempSerial - _marks[i - 1].MaxTempSerial;
                var newObjects = _marks[i].Ids.Count(id => !_marks[i - 1].Ids.Contains(id));
                sb.Append($" {_marks[i].Step}: +{created} temp RTs (+{newObjects} new RT objects);");
            }
            Plugin.Logger.LogInfo(sb.ToString());
            _marks.Clear();
            _splitArmed = false;
        }
        catch (Exception e)
        {
            _marks.Clear();
            _splitArmed = false;
            Plugin.Logger.LogWarning($"Resource stats: frame split failed: {e.Message}");
        }
    }

    private static void Reset()
    {
        _nextReportAt = 0f;
        _lastMaxTempSerial = -1;
        _lastProbeId = 0;
        _knownRenderTextures = new HashSet<int>();
        _knownTextures2D = new HashSet<int>();
        _marks.Clear();
        _splitArmed = false;
    }

    /// <summary>Rough video memory of a render texture: colour (+ depth/stencil), MSAA, mips. Good enough to spot growth.</summary>
    private static long EstimateBytes(RenderTexture rt)
    {
        var colour = rt.format switch
        {
            RenderTextureFormat.R8 => 1,
            RenderTextureFormat.RHalf or RenderTextureFormat.RG16 or RenderTextureFormat.R16 or RenderTextureFormat.RGB565
                or RenderTextureFormat.ARGB4444 or RenderTextureFormat.ARGB1555 => 2,
            RenderTextureFormat.ARGBHalf or RenderTextureFormat.DefaultHDR or RenderTextureFormat.ARGB64 or RenderTextureFormat.RGFloat
                or RenderTextureFormat.RGInt or RenderTextureFormat.RGBAUShort => 8,
            RenderTextureFormat.ARGBFloat or RenderTextureFormat.ARGBInt => 16,
            _ => 4,
        };
        var depth = rt.depth > 0 ? 4 : 0;
        var pixels = (long)rt.width * rt.height * Math.Max(1, rt.volumeDepth) * Math.Max(1, rt.antiAliasing);
        var bytes = pixels * (colour + depth);
        return rt.useMipMap ? bytes * 4 / 3 : bytes;
    }

    private static void Report(bool vrRunning)
    {
        var frame = Time.frameCount;
        var now = Time.realtimeSinceStartup;
        var probeId = ProbeObjectId();

        var renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>();
        var maxTempSerial = 0;
        var rtIds = new HashSet<int>();
        var newRtGroups = new Dictionary<string, int>();
        var createdRts = 0;
        var createdBytes = 0L;
        foreach (var rt in renderTextures)
        {
            if (rt == null) continue;
            var id = rt.GetInstanceID();
            rtIds.Add(id);
            // A released render texture keeps its object but no video memory, so count both.
            if (rt.IsCreated())
            {
                createdRts++;
                createdBytes += EstimateBytes(rt);
            }
            var name = rt.name ?? "";
            var match = TempBufferName.Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var serial) && serial > maxTempSerial)
                maxTempSerial = serial;
            if (_lastMaxTempSerial >= 0 && !_knownRenderTextures.Contains(id))
                Count(newRtGroups, $"{TempBufferName.Replace(name, "TempBuffer")} [{rt.width}x{rt.height} {rt.format}]");
        }

        var textures2D = Resources.FindObjectsOfTypeAll<Texture2D>();
        var texIds = new HashSet<int>();
        var newTexGroups = new Dictionary<string, int>();
        foreach (var tex in textures2D)
        {
            if (tex == null) continue;
            var id = tex.GetInstanceID();
            texIds.Add(id);
            if (_lastMaxTempSerial >= 0 && !_knownTextures2D.Contains(id))
                Count(newTexGroups, $"{tex.name} [{tex.width}x{tex.height} {tex.format}]");
        }

        if (_lastMaxTempSerial < 0)
        {
            Plugin.Logger.LogInfo($"Resource stats: baseline ({(vrRunning ? "VR running" : "flat")}): " +
                                  $"{rtIds.Count} render textures (highest TempBuffer serial {maxTempSerial}), {texIds.Count} Texture2D");
        }
        else
        {
            var frames = Math.Max(1, frame - _lastFrame);
            var seconds = Math.Max(0.001f, now - _lastTime);
            var tempCreated = maxTempSerial - _lastMaxTempSerial;
            var objectsCreated = Math.Abs(probeId - _lastProbeId);
            var sb = new StringBuilder();
            sb.Append($"Resource stats ({(vrRunning ? "VR running" : "flat")}, {seconds:F1} s, {frames} frames, {frames / seconds:F0} fps): ");
            sb.Append($"temp RTs created +{tempCreated} ({(float)tempCreated / frames:F1}/frame); ");
            sb.Append($"render textures {rtIds.Count} (+{newRtGroups.Values.Sum()} new; {createdRts} in video memory, ~{createdBytes / (1024 * 1024)} MB); ");
            sb.Append($"Texture2D {texIds.Count} (+{newTexGroups.Values.Sum()} new); ");
            sb.Append($"object IDs used ~{objectsCreated} ({(float)objectsCreated / frames:F1}/frame); ");
            sb.Append($"game camera's own renders {GameCameraRenders} ({(float)GameCameraRenders / frames:F2}/frame); ");
            sb.Append($"left eye by game camera {LeftEyeByGame}, separately {LeftEyeSeparate}, undetected {LeftEyeUndetected}");
            AppendGroups(sb, "new render textures", newRtGroups);
            AppendGroups(sb, "new Texture2D", newTexGroups);
            Plugin.Logger.LogInfo(sb.ToString());
        }

        GameCameraRenders = LeftEyeByGame = LeftEyeSeparate = LeftEyeUndetected = 0;
        _lastFrame = frame;
        _lastTime = now;
        _lastMaxTempSerial = maxTempSerial;
        _lastProbeId = probeId;
        _knownRenderTextures = rtIds;
        _knownTextures2D = texIds;

        if (vrRunning)
        {
            _marks.Clear();
            _splitArmed = true;
        }
    }

    private static (int MaxTempSerial, HashSet<int> Ids) SampleRenderTextures()
    {
        var maxSerial = 0;
        var ids = new HashSet<int>();
        foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
        {
            if (rt == null) continue;
            ids.Add(rt.GetInstanceID());
            var match = TempBufferName.Match(rt.name ?? "");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var serial) && serial > maxSerial)
                maxSerial = serial;
        }
        return (maxSerial, ids);
    }

    // Instance IDs are handed out in sequence, so the gap between two probes shows how many Unity objects were
    // created in between (an approximate count; it creates no texture itself).
    private static int ProbeObjectId()
    {
        var probe = new GameObject("NivalisVR_ResourceProbe");
        var id = probe.GetInstanceID();
        Object.Destroy(probe);
        return id;
    }

    private static void Count(Dictionary<string, int> groups, string key)
    {
        groups.TryGetValue(key, out var n);
        groups[key] = n + 1;
    }

    private static void AppendGroups(StringBuilder sb, string label, Dictionary<string, int> groups)
    {
        if (groups.Count == 0) return;
        sb.AppendLine();
        sb.Append($"    {label}: ");
        sb.Append(string.Join(", ", groups.OrderByDescending(g => g.Value).Take(TopGroups).Select(g => $"{g.Key} x{g.Value}")));
        if (groups.Count > TopGroups) sb.Append($", ... ({groups.Count - TopGroups} more kinds)");
    }
}
