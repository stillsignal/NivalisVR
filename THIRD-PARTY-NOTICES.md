# Third-party software in the Nivalis VR release

The Nivalis VR plugin itself (`NivalisVR.dll`, source in `src/`) is MIT-licensed; see `LICENSE`.
The release zip also contains the following unmodified third-party software.

## BepInEx 6.0.0-be.788 (IL2CPP, win-x64)

- Files: `BepInEx/core/`, `dotnet/`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`
- License: GNU Lesser General Public License v2.1 — full text in `BepInEx/plugins/NivalisVR/licenses/BepInEx-LICENSE.txt`
- Source: https://github.com/BepInEx/BepInEx/tree/5b766a3b7f6c164d4798924a93f3acf4db769d06
  (build `6.0.0-be.788+5b766a3` from https://builds.bepinex.dev/projects/bepinex_be)
- BepInEx's distribution itself bundles further open-source components (for example Il2CppInterop, HarmonyX,
  MonoMod, Cpp2IL, Unity Doorstop and the .NET runtime). Their licenses are listed in the BepInEx repository.

## OpenXR loader 1.1.63 (`openxr_loader.dll`)

- File: `BepInEx/plugins/NivalisVR/openxr_loader.dll`
- License: Apache License 2.0 — full text in `BepInEx/plugins/NivalisVR/licenses/OpenXR-SDK-LICENSE.txt`
- Source: https://github.com/KhronosGroup/OpenXR-SDK-Source/releases/tag/release-1.1.63
- Copyright The Khronos Group Inc. and contributors.
