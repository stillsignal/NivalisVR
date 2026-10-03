# Nivalis VR

A free, unofficial VR mod for Nivalis Nights. It adds stereo 3D and head tracking for PC VR headsets.
You still play with keyboard and mouse. There is no motion controller support.

What it does:

- Renders the game in proper stereo for your headset, with full head tracking (you can lean and look around).
- Shows menus, the HUD and dialogue on a large panel fixed in front of you, with a mouse pointer.
- While VR is running, your monitor shows what your left eye sees, in a smaller window. It goes back to normal
  afterwards.

Not affiliated with or endorsed by ION LANDS. The mod doesn't modify any game files (it's loaded by BepInEx) and
doesn't touch DRM or Steam ownership checks.

## Before you start

- This is an early beta. It has only been tested on one PC (RTX 5090) with SteamVR. Other headsets that use OpenXR
  (Index, Vive, Pimax, Quest through Link, Air Link, Virtual Desktop or Steam Link) should work but haven't been
  tested. Windows Mixed Reality isn't supported.
- You need a strong GPU. The game is drawn twice per frame, once for each eye. If it stutters, lower the resolution
  for this game in SteamVR (or your headset's own software), or turn on motion smoothing.
- To play in VR, start SteamVR (or your headset's software) before the game. If SteamVR isn't running, the game
  starts normally without VR. You can press F11 at any time to switch to VR.
- When you look towards a low sun, like at sunset, the glow around it can look stronger in one eye. That comes from
  the game's volumetric lighting, which doesn't fully support VR.
- Game updates may break the mod.
- Back up your saves before trying any mod. They're in `%USERPROFILE%\AppData\LocalLow\ION LANDS\Nivalis Nights\`.

## Requirements

- Nivalis Nights on Windows (Steam version), running in DirectX 11 (the game's default).
- A PC VR headset with an OpenXR runtime set as active (SteamVR, Meta Quest Link, Virtual Desktop, etc.).

## Install

1. Back up your saves (see above). The mod doesn't touch them, but it's a good habit with any mod.
2. Download `NivalisVR-<version>.zip` from the [Releases](../../releases) page.
3. Open the game folder. In Steam, right-click Nivalis Nights, then Manage, then Browse local files.
   It's the folder with `Nivalis Nights.exe` in it.
4. Extract the zip into that folder (right-click the zip, choose Extract All, and pick the game folder).
   Afterwards the game folder should have these new files next to `Nivalis Nights.exe`:
   ```
   Nivalis Nights.exe      (the game, unchanged)
   BepInEx\                (new: the mod loader and the mod)
   dotnet\                 (new: runtime used by the mod loader)
   winhttp.dll             (new: starts the mod loader)
   doorstop_config.ini     (new)
   .doorstop_version       (new, may be hidden)
   NivalisVR-README.txt    (new: this file)
   ```
   If you got a single `NivalisVR-<version>` folder inside the game folder instead, move everything in it up one level.
5. Start the game once without VR so the mod loader can set itself up. The first launch is slow (a minute or two,
   sometimes with no window at first) because it downloads a small set of Unity libraries from `unity.bepinex.dev`.
   Allow it through your firewall if asked. After that, the game starts at normal speed.

To check that it worked: after that first launch there should be a file `BepInEx\LogOutput.log` in the game folder
with the line `Nivalis VR <version> loaded` in it.

If you already use other BepInEx mods for this game: this zip includes BepInEx 6 (IL2CPP, build be.788). If you
already have BepInEx 6 IL2CPP installed, copying just `BepInEx\plugins\NivalisVR\` from the zip should work, though
only be.788 has been tested. BepInEx 5 doesn't work with this game.

To update to a new version, extract the new zip over the old one and let it replace the files. Your settings in
`BepInEx\config\com.nivalisvr.plugin.cfg` are kept.

## Playing

1. Start SteamVR (or your headset's software) with the headset connected.
2. Start the game from Steam as usual.
3. Put the headset on and press F10 to center the view and the menu panel in front of you.

| Key | What it does |
|-----|--------------|
| F10 | Recenter the view and the menu panel |
| F11 | Start VR (for example if you launched the game without SteamVR running) |
| F9  | Side-by-side 3D preview on the monitor (only when VR isn't running) |
| F8  | Write a diagnostic report to the log (for bug reports) |
| Shift+F8 | Turn texture logging on or off (for bug reports, see `ResourceStats` below) |

## Settings

The settings file is `BepInEx\config\com.nivalisvr.plugin.cfg`. It's created on the first launch. Edit it while the
game is closed.

| Section | Setting | Default | What it does |
|---------|---------|---------|--------------|
| VR | Enabled | true | Start VR automatically when the game launches (with SteamVR, only if it's already running) |
| VR | RenderScale | 1.0 | Multiplier on the resolution SteamVR recommends. Changing the resolution in SteamVR works better. |
| VR | SkipMonitorRender | true | Don't draw the game a third time for the monitor; the monitor shows the left eye instead. Only turn this off for troubleshooting: with it off, the picture goes black after about 15 minutes. |
| UI | PanelDistance | 2.0 | How far away the menu panel is, in metres |
| UI | PanelWidth | 2.6 | Menu panel width in metres |
| UI | PanelHeightOffset | -0.1 | Panel height relative to your eyes, in metres |
| UI | PanelResolutionScale | 1.0 | Menu sharpness compared to the headset's resolution |
| Monitor | ShrinkWindowInVr | true | Use a small game window on the monitor while in VR, the same size as the menu panel in the headset (keeps the menus sharp) |
| Stereo | PreviewEnabled | false | Side-by-side preview on the monitor when VR isn't running |
| Debug | CaptureKey | false | Lets F6 save the current eye images to `BepInEx\NivalisVR-captures` (for bug reports) |
| Debug | ResourceStats | false | Every 5 seconds, write to the log how many textures the game creates (for bug reports). Shift+F8 also turns it on or off. |

## Troubleshooting

- Nothing shows up in the headset: make sure SteamVR (or your headset's software) was running before the game, or
  press F11. Also check that it's set as the active OpenXR runtime (in SteamVR: Settings, OpenXR,
  "Set SteamVR as OpenXR Runtime").
- After about 15 minutes in VR the picture goes black in the headset and on the monitor, but menus still work: that
  was a bug in version 0.4.0, fixed in 0.4.1. Update to the latest version (see Install).
- The mod doesn't seem to load at all (no VR, and `BepInEx\LogOutput.log` is missing or ends with errors): your
  antivirus may have removed a BepInEx file, most often `BepInEx\core\dobby.dll`. That's a false positive on a
  standard BepInEx file. Restore it from your antivirus quarantine, or extract the zip again and allow the files.
- Something else is wrong: open an issue on GitHub, describe what happened, and paste the contents of
  `BepInEx\LogOutput.log` (open it in Notepad and copy the text). The log can include your Windows user name in file
  paths, so feel free to replace it before posting.

## Turning VR off without uninstalling

- To play without VR, start the game without starting SteamVR first. The mod stays idle until you press F11.
- To keep VR off, set `Enabled = false` under `[VR]` in `BepInEx\config\com.nivalisvr.plugin.cfg`.
- To turn off all mods for a while, rename `winhttp.dll` in the game folder (for example to `winhttp.dll.off`).
  Rename it back to turn them on again.

## Uninstall

The mod never changes the game's own files, so deleting the files it added puts the game back exactly as it was.
Close the game and open the game folder (in Steam: right-click Nivalis Nights, Manage, Browse local files). Then:

- To remove everything (the mod and the mod loader), delete `BepInEx`, `dotnet`, `winhttp.dll`,
  `doorstop_config.ini`, `.doorstop_version` and `NivalisVR-README.txt`. `.doorstop_version` is a hidden file; in
  Explorer use View, Show, Hidden items to see it. Only do this if you don't use other BepInEx mods for this game,
  because it removes them too.
- To remove only this mod and keep other BepInEx mods, delete the folder `BepInEx\plugins\NivalisVR` and the file
  `BepInEx\config\com.nivalisvr.plugin.cfg`.

Your saves and the game's own settings aren't affected either way. You don't need to run Steam's "Verify integrity of
game files" afterwards (it wouldn't remove the mod's files anyway).

## Building from source

- You need the .NET 8 SDK and a copy of the game with this BepInEx build installed and launched once (that creates
  `BepInEx/interop`).
- Build with `dotnet build src/NivalisVR -c Release -p:GameDir="<path to game folder>\\"`.
  By default the project looks for the game in `game-copy/` next to the repository root.
- `scripts/package-release.sh` builds the release zip. It needs the BepInEx build, the OpenXR SDK release and
  BepInEx's license text; see the variables at the top of the script.

## Credits

Built with the help of Claude (Opus 5.5) by Anthropic. Uses BepInEx and the Khronos OpenXR loader.

## License

The mod's own code is MIT licensed (see `LICENSE`). BepInEx and the OpenXR loader, which are included in the release
zip, keep their own licenses (see `THIRD-PARTY-NOTICES.md`).
