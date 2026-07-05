using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Perception.GroundTruth;

/// Controls the ego (first-person) camera used for autonomous driving data capture.
/// This is a SENSOR camera — it always renders so Unity Perception can capture dataset frames.
///
/// EgoCam GameObject - child of the vehicle, positioned at driver seat.
/// 
/// Two modes of operation:
///   1. Hidden (default when chase cam is active): the main capture camera renders to a background RenderTexture.
///   2. On-screen: a dynamically created visual camera clone (EgoCam_ScreenView) is enabled to draw on Display 0.

[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(Camera))]
public class EgoCameraController : MonoBehaviour
{
    [Header("Background Capture Settings")]
    [Tooltip("Resolution of the RenderTexture used for synthetic data capture in the background.")]
    public Vector2Int captureResolution = new Vector2Int(1920, 1080);

    [Header("Runtime Info (Read Only)")]
    [SerializeField]
    private bool _isShowingOnScreen = false;

    private Camera _egoCamera;
    private RenderTexture _egoRenderTexture;
    private Camera _egoViewCamera;

    /// Direct access to the ego camera component.
    public Camera EgoCamera => _egoCamera;

    void Awake()
    {
        _egoCamera = GetComponent<Camera>();

        _egoRenderTexture = new RenderTexture(captureResolution.x, captureResolution.y, 24, RenderTextureFormat.DefaultHDR);
        _egoRenderTexture.name = "EgoCam_CaptureTexture";
        _egoRenderTexture.Create();

        // Assign RenderTexture to the main capture camera permanently
        _egoCamera.targetTexture = _egoRenderTexture;
        _egoCamera.targetDisplay = 1; // Redirect blits/visualizations to Display 2 (index=1) to prevent drawing on Display 1
        _egoCamera.depth = 0;
        _egoCamera.enabled = true;
        gameObject.tag = "Untagged";

        // Turn off visualization overlay on the capture camera on startup to prevent overrides
        var perception = GetComponent<PerceptionCamera>();
        if (perception != null)
        {
            perception.showVisualizations = false;
        }

        // Bypass the hardcoded Perception blit to Display 1/screen backbuffer
        BypassPerceptionBlit();

        // Dynamically create a screen-view camera clone
        GameObject viewObj = new GameObject("EgoCam_ScreenView");
        viewObj.transform.SetParent(this.transform, false);
        viewObj.transform.localPosition = Vector3.zero;
        viewObj.transform.localRotation = Quaternion.identity;

        _egoViewCamera = viewObj.AddComponent<Camera>();
        _egoViewCamera.CopyFrom(_egoCamera);
        
        // Remove RenderTexture and route to the screen (Display 0)
        _egoViewCamera.targetTexture = null;
        _egoViewCamera.targetDisplay = 0;
        _egoViewCamera.depth = 15;
        _egoViewCamera.enabled = false;
        viewObj.tag = "Untagged";

        // Duplicate HDRP camera data
        var srcHdrp = _egoCamera.GetComponent<HDAdditionalCameraData>();
        var dstHdrp = _egoViewCamera.GetComponent<HDAdditionalCameraData>();
        if (srcHdrp != null && dstHdrp != null)
        {
            dstHdrp.volumeLayerMask = srcHdrp.volumeLayerMask;
            dstHdrp.clearColorMode = srcHdrp.clearColorMode;
            dstHdrp.backgroundColorHDR = srcHdrp.backgroundColorHDR;
            dstHdrp.antialiasing = srcHdrp.antialiasing;
        }

        _isShowingOnScreen = false;
        Debug.Log("[EgoCameraController] Background capture camera initialized. Dynamic screen-view camera clone created.");
    }

    private int _logCounter = 0;

    void Update()
    {
        _logCounter++;
        if (_logCounter % 60 == 0)
        {
            var chaseCamObj = GameObject.Find("ChaseCamera");
            var chaseCam = chaseCamObj != null ? chaseCamObj.GetComponent<Camera>() : null;
            
            string egoTex = _egoCamera != null && _egoCamera.targetTexture != null ? _egoCamera.targetTexture.name : "null";
            string cloneTex = _egoViewCamera != null && _egoViewCamera.targetTexture != null ? _egoViewCamera.targetTexture.name : "null";
            string chaseTex = chaseCam != null && chaseCam.targetTexture != null ? chaseCam.targetTexture.name : "null";

            string egoPos = _egoCamera != null ? _egoCamera.transform.position.ToString("F3") : "N/A";
            string clonePos = _egoViewCamera != null ? _egoViewCamera.transform.position.ToString("F3") : "N/A";
            string chasePos = chaseCam != null ? chaseCam.transform.position.ToString("F3") : "N/A";

            Debug.Log($"[EgoCameraMonitor] Frame: {Time.frameCount}\n" +
                      $" - EgoCam: enabled={(_egoCamera != null ? _egoCamera.enabled.ToString() : "N/A")}, display={(_egoCamera != null ? _egoCamera.targetDisplay.ToString() : "N/A")}, depth={(_egoCamera != null ? _egoCamera.depth.ToString() : "N/A")}, texture={egoTex}, pos={egoPos}\n" +
                      $" - EgoClone: enabled={(_egoViewCamera != null ? _egoViewCamera.enabled.ToString() : "N/A")}, display={(_egoViewCamera != null ? _egoViewCamera.targetDisplay.ToString() : "N/A")}, depth={(_egoViewCamera != null ? _egoViewCamera.depth.ToString() : "N/A")}, texture={cloneTex}, pos={clonePos}\n" +
                      $" - ChaseCam: enabled={(chaseCam != null ? chaseCam.enabled.ToString() : "N/A")}, display={(chaseCam != null ? chaseCam.targetDisplay.ToString() : "N/A")}, depth={(chaseCam != null ? chaseCam.depth.ToString() : "N/A")}, texture={chaseTex}, pos={chasePos}");
        }
    }

    void OnEnable()
    {
        RenderPipelineManager.endCameraRendering += LogCameraRender;
    }

    void OnDisable()
    {
        RenderPipelineManager.endCameraRendering -= LogCameraRender;
    }

    private void LogCameraRender(ScriptableRenderContext context, Camera cam)
    {
        if (Time.frameCount % 60 == 0)
        {
            Debug.Log($"[CameraRenderPass] Frame: {Time.frameCount}, Camera: {cam.name}, targetTexture: {(cam.targetTexture != null ? cam.targetTexture.name : "null")}, targetDisplay: {cam.targetDisplay}, depth: {cam.depth}, enabled: {cam.enabled}");
        }
    }

    void OnDestroy()
    {
        RestorePerceptionBlit();

        if (_egoRenderTexture != null)
        {
            _egoRenderTexture.Release();
            Destroy(_egoRenderTexture);
        }
    }

    private static bool s_isBypassed = false;
    private static System.Reflection.FieldInfo s_endFrameRenderingField;
    private static System.Reflection.FieldInfo s_visualizedPerceptionCameraField;
    private static System.Reflection.MethodInfo s_camerasAreTargetingTheGameViewMethod;
    private static System.Reflection.MethodInfo s_atLeastOneCameraInSceneTargetingGameViewMethod;

    private static void BypassPerceptionBlit()
    {
        if (s_isBypassed) return;

        try
        {
            var perceptionAssembly = typeof(PerceptionCamera).Assembly;
            var perceptionUpdaterType = perceptionAssembly.GetType("UnityEngine.Perception.GroundTruth.PerceptionUpdater");
            if (perceptionUpdaterType != null)
            {
                var onEndContextMethod = perceptionUpdaterType.GetMethod(
                    "OnEndContextRendering", 
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                );
                
                if (onEndContextMethod != null)
                {
                    var originalDelegate = (Action<ScriptableRenderContext, List<Camera>>)Delegate.CreateDelegate(
                        typeof(Action<ScriptableRenderContext, List<Camera>>),
                        onEndContextMethod
                    );
                    
                    RenderPipelineManager.endContextRendering -= originalDelegate;
                    RenderPipelineManager.endContextRendering += CustomOnEndContextRendering;
                    s_isBypassed = true;
                    Debug.Log("[EgoCameraController] Successfully bypassed Perception blit by replacing endContextRendering subscription.");
                }
                else
                {
                    Debug.LogError("[EgoCameraController] Could not find method OnEndContextRendering on PerceptionUpdater.");
                }
            }
            else
            {
                Debug.LogError("[EgoCameraController] Could not find type UnityEngine.Perception.GroundTruth.PerceptionUpdater.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[EgoCameraController] Error setting up reflection-based blit bypass: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void RestorePerceptionBlit()
    {
        if (!s_isBypassed) return;

        try
        {
            var perceptionAssembly = typeof(PerceptionCamera).Assembly;
            var perceptionUpdaterType = perceptionAssembly.GetType("UnityEngine.Perception.GroundTruth.PerceptionUpdater");
            if (perceptionUpdaterType != null)
            {
                var onEndContextMethod = perceptionUpdaterType.GetMethod(
                    "OnEndContextRendering", 
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                );
                if (onEndContextMethod != null)
                {
                    var originalDelegate = (Action<ScriptableRenderContext, List<Camera>>)Delegate.CreateDelegate(
                        typeof(Action<ScriptableRenderContext, List<Camera>>),
                        onEndContextMethod
                    );
                    
                    RenderPipelineManager.endContextRendering -= CustomOnEndContextRendering;
                    RenderPipelineManager.endContextRendering += originalDelegate;
                    s_isBypassed = false;
                    Debug.Log("[EgoCameraController] Successfully restored Perception blit subscription.");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[EgoCameraController] Failed to restore original OnEndContextRendering: {ex.Message}");
        }
    }

    private static void CustomOnEndContextRendering(ScriptableRenderContext ctx, List<Camera> cameras)
    {
        try
        {
            if (s_endFrameRenderingField == null)
            {
                var perceptionAssembly = typeof(PerceptionCamera).Assembly;
                var updaterType = perceptionAssembly.GetType("UnityEngine.Perception.GroundTruth.PerceptionUpdater");
                s_endFrameRenderingField = updaterType.GetField("endFrameRendering", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                s_visualizedPerceptionCameraField = typeof(PerceptionCamera).GetField("visualizedPerceptionCamera", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                s_camerasAreTargetingTheGameViewMethod = updaterType.GetMethod("CamerasAreTargetingTheGameView", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                s_atLeastOneCameraInSceneTargetingGameViewMethod = updaterType.GetMethod("AtLeastOneCameraInSceneTargetingGameView", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            }

            // Run original filter checks to see if this end-rendering call targets the active game view
            bool camerasAreTargetingGameView = (bool)s_camerasAreTargetingTheGameViewMethod.Invoke(null, new object[] { cameras });
            bool atLeastOneTargetingGameView = (bool)s_atLeastOneCameraInSceneTargetingGameViewMethod.Invoke(null, null);

            if (!camerasAreTargetingGameView && atLeastOneTargetingGameView)
                return;

            // Invoke the endFrameRendering delegate so that PerceptionCamera still records the labels / readbacks
            var endFrameRenderingDelegate = (Action<ScriptableRenderContext>)s_endFrameRenderingField.GetValue(null);
            if (endFrameRenderingDelegate != null)
            {
                endFrameRenderingDelegate.Invoke(ctx);
            }

            // Wait all requests if visualizedPerceptionCamera is not null (same as original)
            var visualizedCam = s_visualizedPerceptionCameraField.GetValue(null);
            if (visualizedCam != null)
            {
                AsyncGPUReadback.WaitAllRequests();
            }

            // BYPASS: We deliberately omit BlitVisualizedPerceptionCameraToScreen(ctx);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[EgoCameraController] Exception in CustomOnEndContextRendering: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// Called by ChaseCameraController when chase cam is toggled OFF.
    /// Redirects the ego camera's output directly to the screen (Display 0).
    public void ShowOnScreen()
    {
        if (_egoViewCamera == null) return;

        // Simply enable the screen-view camera component
        _egoViewCamera.enabled = true;
        _egoViewCamera.gameObject.tag = "MainCamera";

        _isShowingOnScreen = true;
        Debug.Log("[EgoCameraController] ShowOnScreen: Ego screen-view camera enabled.");
    }

    /// Called by ChaseCameraController when chase cam is toggled ON.
    /// Redirects the ego camera's output back to the RenderTexture.
    public void HideFromScreen()
    {
        if (_egoViewCamera == null) return;

        // Simply disable the screen-view camera component
        _egoViewCamera.enabled = false;
        _egoViewCamera.gameObject.tag = "Untagged";

        _isShowingOnScreen = false;
        Debug.Log("[EgoCameraController] HideFromScreen: Ego screen-view camera disabled.");
    }

    /// Returns whether the ego camera is currently visible in the Game view.
    public bool IsShowingOnScreen => _isShowingOnScreen;
}
