using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NivalisVR;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.nivalisvr.plugin";
    public const string Name = "Nivalis VR";
    public const string Version = "0.4.3";

    internal static ManualLogSource Logger;
    private static ConfigEntry<bool> _autoDump;

    public override void Load()
    {
        Logger = Log;
        Log.LogInfo($"Nivalis VR {Version} loaded");
        Log.LogInfo($"Unity version: {Application.unityVersion}");

        _autoDump = Config.Bind("Debug", "AutoDumpOnLevelLoad", false,
            "Write a full camera/post-processing/UI report to the log a few seconds after each level loads " +
            "(very long). F8 writes one on demand regardless of this setting.");

        SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;

        AddComponent<CameraDumper>();

        StereoRenderer.BindConfig(Config);
        ClassInjector.RegisterTypeInIl2Cpp<StereoRenderer>();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        var main = Camera.main;
        Log.LogInfo($"Scene loaded: '{scene.name}' (index {scene.buildIndex}, {mode})");
        Log.LogInfo($"  Camera.main: {(main != null ? $"'{main.name}'" : "<null>")}");

        // Objects created during plugin Load (before any scene exists) are destroyed when the first scene
        // loads, even with DontDestroyOnLoad, so create the renderer once a scene is up.
        StereoRenderer.EnsureCreated();

        // Levels load additively on top of _Global; give them a moment to finish setting up cameras.
        if (_autoDump.Value && scene.name != "_Global")
            CameraDumper.ScheduleDump($"scene '{scene.name}' loaded", 3f);
    }
}
