using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// Diagnostics: saves render textures (eye images, the game's helper textures) as PNG files in
/// BepInEx/NivalisVR-captures, so visual problems in the headset can be inspected exactly.
/// </summary>
internal static class DebugCapture
{
    private const int MaxSize = 1400;

    public static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, "NivalisVR-captures");

    /// <summary>Saves the texture (downscaled to at most MaxSize on its longer side) as name.png.</summary>
    public static void Save(Texture source, string name)
    {
        if (source == null)
        {
            Plugin.Logger.LogInfo($"Capture: '{name}' skipped (no texture)");
            return;
        }

        RenderTexture scaled = null;
        Texture2D readback = null;
        var previousActive = RenderTexture.active;
        try
        {
            var scale = Mathf.Min(1f, (float)MaxSize / Mathf.Max(source.width, source.height));
            var width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            scaled = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, scaled);
            RenderTexture.active = scaled;
            readback = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readback.Apply(false);

            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, name + ".png");
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(readback).ToArray());
            Plugin.Logger.LogInfo($"Capture: saved {path} ({source.width}x{source.height} -> {width}x{height})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Capture: '{name}' failed: {e.Message}");
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (scaled != null) RenderTexture.ReleaseTemporary(scaled);
            if (readback != null) Object.Destroy(readback);
        }
    }
}
