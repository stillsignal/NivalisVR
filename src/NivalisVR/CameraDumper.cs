using System;
using System.Text;
using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NivalisVR;

/// <summary>
/// Diagnostic for milestone 2: logs every camera's components, settings and command buffers,
/// plus the post-processing volumes and skybox. Runs after each level load and on F8.
/// </summary>
public class CameraDumper : MonoBehaviour
{
    private static float _dumpAt = -1f;
    private static string _dumpReason;
    private static bool _inputBroken;

    public CameraDumper(IntPtr ptr) : base(ptr) { }

    public static void ScheduleDump(string reason, float delaySeconds)
    {
        _dumpReason = reason;
        _dumpAt = Time.realtimeSinceStartup + delaySeconds;
    }

    private void Update()
    {
        if (!_inputBroken)
        {
            try
            {
                // The game has legacy UnityEngine.Input disabled; only the Input System works.
                var keyboard = Keyboard.current;
                if (keyboard != null && keyboard.f8Key.wasPressedThisFrame)
                    ScheduleDump("F8 pressed", 0f);
            }
            catch (Exception e)
            {
                _inputBroken = true;
                Plugin.Logger.LogWarning($"Input System unavailable, F8 dump disabled: {e.Message}");
            }
        }

        if (_dumpAt >= 0f && Time.realtimeSinceStartup >= _dumpAt)
        {
            _dumpAt = -1f;
            try
            {
                Dump(_dumpReason);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Camera dump failed: {e}");
            }
        }
    }

    private static void Dump(string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== CAMERA DUMP ({reason}) =====");
        sb.AppendLine($"Screen {Screen.width}x{Screen.height}, QualityLevel={QualitySettings.GetQualityLevel()} ({QualitySettings.names[QualitySettings.GetQualityLevel()]}), vSync={QualitySettings.vSyncCount}, targetFrameRate={Application.targetFrameRate}");

        var skybox = RenderSettings.skybox;
        sb.AppendLine($"RenderSettings.skybox: {(skybox != null ? $"'{skybox.name}' shader='{skybox.shader.name}'" : "<none>")}");

        // Camera.allCameras only returns enabled cameras; FindObjectsOfType also catches disabled ones.
        foreach (var cam in Object.FindObjectsOfType<Camera>(true))
            DumpCamera(sb, cam);

        DumpVolumes(sb);
        DumpUi(sb);

        sb.Append("===== END CAMERA DUMP =====");
        Plugin.Logger.LogInfo(sb.ToString());
    }

    private static void DumpCamera(StringBuilder sb, Camera cam)
    {
        sb.AppendLine();
        sb.AppendLine($"--- Camera '{cam.name}' path='{PathOf(cam.transform)}' scene='{cam.gameObject.scene.name}'");
        sb.AppendLine($"    enabled={cam.enabled} activeInHierarchy={cam.gameObject.activeInHierarchy} isMain={cam == Camera.main} tag={cam.tag} layer={LayerMask.LayerToName(cam.gameObject.layer)}");
        sb.AppendLine($"    depth={cam.depth} clearFlags={cam.clearFlags} bg={cam.backgroundColor} cullingMask={MaskToNames(cam.cullingMask)}");
        sb.AppendLine($"    fov={cam.fieldOfView:F2} near={cam.nearClipPlane} far={cam.farClipPlane} ortho={cam.orthographic} aspect={cam.aspect:F3} rect={cam.rect}");
        sb.AppendLine($"    renderingPath={cam.renderingPath} actual={cam.actualRenderingPath} hdr={cam.allowHDR} msaa={cam.allowMSAA} dynRes={cam.allowDynamicResolution} depthTexMode={cam.depthTextureMode} occlusion={cam.useOcclusionCulling}");
        sb.AppendLine($"    stereoTargetEye={cam.stereoTargetEye} usePhysical={cam.usePhysicalProperties}");

        var rt = cam.targetTexture;
        sb.AppendLine(rt == null
            ? "    target=screen"
            : $"    target=RT '{rt.name}' {rt.width}x{rt.height} fmt={rt.format} depth={rt.depth} aa={rt.antiAliasing}");

        var pos = cam.transform.position;
        var rot = cam.transform.eulerAngles;
        sb.AppendLine($"    pos={pos} rot={rot} lossyScale={cam.transform.lossyScale}");

        sb.AppendLine($"    components:");
        foreach (var c in cam.GetComponents<Component>())
        {
            var behaviour = c.TryCast<Behaviour>();
            var state = behaviour != null ? (behaviour.enabled ? "on " : "off") : "   ";
            sb.AppendLine($"      [{state}] {c.GetIl2CppType().FullName}");
        }

        var pp = cam.GetComponent<PostProcessLayer>();
        if (pp != null)
        {
            sb.AppendLine($"    PostProcessLayer: aa={pp.antialiasingMode} volumeLayer={MaskToNames(pp.volumeLayer.value)} trigger={(pp.volumeTrigger != null ? pp.volumeTrigger.name : "<null>")} finalBlitToCameraTarget={pp.finalBlitToCameraTarget} stopNaN={pp.stopNaNPropagation}");
        }

        var brain = cam.GetComponent<CinemachineBrain>();
        if (brain != null)
        {
            string active;
            try { active = brain.ActiveVirtualCamera?.Name ?? "<none>"; }
            catch (Exception e) { active = $"<error: {e.Message}>"; }
            sb.AppendLine($"    CinemachineBrain: update={brain.m_UpdateMethod} blendUpdate={brain.m_BlendUpdateMethod} activeVcam={active}");
        }

        sb.AppendLine($"    commandBuffers total={cam.commandBufferCount}");
        if (cam.commandBufferCount > 0)
        {
            foreach (CameraEvent evt in Enum.GetValues(typeof(CameraEvent)))
            {
                foreach (var cb in cam.GetCommandBuffers(evt))
                    sb.AppendLine($"      {evt}: '{cb.name}' ({cb.sizeInBytes} bytes)");
            }
        }
    }

    private static void DumpVolumes(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("--- PostProcessVolumes");
        foreach (var vol in Object.FindObjectsOfType<PostProcessVolume>(true))
        {
            var profile = vol.sharedProfile;
            sb.AppendLine($"    '{vol.name}' path='{PathOf(vol.transform)}' scene='{vol.gameObject.scene.name}' enabled={vol.enabled} global={vol.isGlobal} priority={vol.priority} weight={vol.weight} layer={LayerMask.LayerToName(vol.gameObject.layer)} profile={(profile != null ? $"'{profile.name}'" : "<null>")}");
            if (profile == null) continue;

            var settings = profile.settings;
            for (var i = 0; i < settings.Count; i++)
            {
                var s = settings[i];
                if (s == null) continue;
                sb.AppendLine($"        {s.GetIl2CppType().Name}: active={s.active} enabled={s.enabled.value}");
            }
        }
    }

    private static void DumpUi(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine($"--- UI: Cursor visible={Cursor.visible} lockState={Cursor.lockState}");

        var eventSystem = EventSystem.current;
        var module = eventSystem != null ? eventSystem.currentInputModule : null;
        sb.AppendLine($"    EventSystem: {(eventSystem != null ? $"'{PathOf(eventSystem.transform)}' module={(module != null ? module.GetIl2CppType().FullName : "<none>")}" : "<none>")}");

        var nested = 0;
        foreach (var canvas in Object.FindObjectsOfType<Canvas>(true))
        {
            if (!canvas.isRootCanvas)
            {
                nested++;
                continue;
            }

            try
            {
                var rect = canvas.GetComponent<RectTransform>().rect;
                var camera = canvas.worldCamera;
                var graphics = canvas.GetComponentsInChildren<Graphic>(false).Length;
                sb.AppendLine($"    Canvas '{PathOf(canvas.transform)}' scene='{canvas.gameObject.scene.name}' layer={LayerMask.LayerToName(canvas.gameObject.layer)}");
                sb.AppendLine($"        enabled={canvas.enabled} active={canvas.gameObject.activeInHierarchy} mode={canvas.renderMode} sortingLayer={canvas.sortingLayerName} order={canvas.sortingOrder} " +
                              $"camera={(camera != null ? camera.name : "<none>")} planeDistance={canvas.planeDistance} scaleFactor={canvas.scaleFactor:F3} pixelPerfect={canvas.pixelPerfect} " +
                              $"size={rect.width:F0}x{rect.height:F0} activeGraphics={graphics}");

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null)
                    sb.AppendLine($"        CanvasScaler: mode={scaler.uiScaleMode} reference={scaler.referenceResolution} match={scaler.matchWidthOrHeight} screenMatch={scaler.screenMatchMode} scaleFactor={scaler.scaleFactor}");

                var group = canvas.GetComponent<CanvasGroup>();
                if (group != null)
                    sb.AppendLine($"        CanvasGroup: alpha={group.alpha} interactable={group.interactable} blocksRaycasts={group.blocksRaycasts}");

                var raycaster = canvas.GetComponent<GraphicRaycaster>();
                sb.AppendLine($"        GraphicRaycaster: {(raycaster != null ? (raycaster.enabled ? "on" : "off") : "none")}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"    Canvas '{canvas.name}': error reading properties: {e.Message}");
            }
        }

        sb.AppendLine($"    ({nested} nested canvases not listed)");
    }

    private static string PathOf(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    private static string MaskToNames(int mask)
    {
        if (mask == -1) return "Everything";
        if (mask == 0) return "Nothing";
        var sb = new StringBuilder();
        for (var i = 0; i < 32; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            var name = LayerMask.LayerToName(i);
            if (sb.Length > 0) sb.Append('|');
            sb.Append(string.IsNullOrEmpty(name) ? i.ToString() : name);
        }
        return sb.ToString();
    }
}
