using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

[RequireComponent(typeof(VideoPlayer))]
public class KioskVideoPlayer : MonoBehaviour
{
    public string folderName = "Kiosk";

    private VideoPlayer videoPlayer;
    private List<string> playlist = new List<string>();
    private int currentVideoIndex = 0;
    private string targetDirectory;
    private bool initialized = false;

    private RenderTexture videoRT;
    private GameObject videoSphere;

    void Start()
    {
        videoPlayer = GetComponent<VideoPlayer>();

        // Remove any broken TrackedPoseDriver that might conflict and cause the camera to freeze
        if (Camera.main != null)
        {
            var driver = Camera.main.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            if (driver != null) Destroy(driver);
        }

        // Register our ultra-smooth head tracking update
        Application.onBeforeRender += SmoothHeadTracking;

        // Disable the skybox — we use our own sphere instead
        if (Camera.main != null)
        {
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
            Camera.main.backgroundColor = Color.black;
        }
        RenderSettings.skybox = null;

        // Create a RenderTexture for the video
        videoRT = new RenderTexture(3840, 2160, 0);
        videoRT.Create();

        // Configure VideoPlayer to render into our RenderTexture
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.targetTexture = videoRT;
        videoPlayer.isLooping = false; // We handle looping ourselves for playlist support

        // Create the inverted sphere that surrounds the camera
        CreateVideoSphere();

        // Set up video events
        videoPlayer.errorReceived += OnVideoError;
        videoPlayer.loopPointReached += OnVideoFinished;

        // Use the app's own data folder (bypasses Android Scoped Storage)
        targetDirectory = Path.Combine(Application.persistentDataPath, folderName);

        // Keep screen always on (prevents Quest from sleeping during kiosk playback)
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        InitializePlaylist();
    }

    void CreateVideoSphere()
    {
        // Create a sphere
        videoSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        videoSphere.name = "VideoSphere360";
        videoSphere.transform.position = Vector3.zero;
        videoSphere.transform.localScale = new Vector3(100f, 100f, 100f);

        // Remove the collider (we don't need physics on this)
        var collider = videoSphere.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        // Flip the sphere inside-out so the video renders on the INSIDE
        Mesh mesh = videoSphere.GetComponent<MeshFilter>().mesh;
        FlipMeshInside(mesh);

        // Crop the UVs to only show the TOP HALF of the video (the left-eye view)
        CropUVsToTopHalf(mesh);

        // Use our custom URP-compatible shader
        Shader customShader = Shader.Find("Custom/VideoSphereUnlit");
        if (customShader != null)
        {
            Material videoMat = new Material(customShader);
            videoMat.mainTexture = videoRT;
            videoSphere.GetComponent<Renderer>().material = videoMat;
        }
        else
        {
            // Ultimate fallback: try Sprites/Default which is ALWAYS included in every Unity build
            Shader fallback = Shader.Find("Sprites/Default");
            if (fallback != null)
            {
                Material videoMat = new Material(fallback);
                videoMat.mainTexture = videoRT;
                videoMat.SetInt("_Cull", 0); // Cull Off
                videoSphere.GetComponent<Renderer>().material = videoMat;
            }
        }
    }

    void FlipMeshInside(Mesh mesh)
    {
        // Reverse all triangle winding order so normals face inward
        int[] triangles = mesh.triangles;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int temp = triangles[i];
            triangles[i] = triangles[i + 2];
            triangles[i + 2] = temp;
        }
        mesh.triangles = triangles;

        // Flip normals inward
        Vector3[] normals = mesh.normals;
        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = -normals[i];
        }
        mesh.normals = normals;
    }

    void CropUVsToTopHalf(Mesh mesh)
    {
        // The Over/Under video has left-eye in top half (Y: 0.5 to 1.0)
        // Remap all UVs so Y ranges from 0.5 to 1.0 only (top half)
        Vector2[] uvs = mesh.uv;
        for (int i = 0; i < uvs.Length; i++)
        {
            uvs[i] = new Vector2(uvs[i].x, 0.5f + uvs[i].y * 0.5f);
        }
        mesh.uv = uvs;
    }

    void InitializePlaylist()
    {
        if (initialized) return;
        initialized = true;

        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
            ShowDebugColor(Color.yellow);
            return;
        }

        LoadPlaylist();
        PlayCurrentVideo();
    }

    void LoadPlaylist()
    {
        playlist.Clear();
        try
        {
            string[] allFiles = Directory.GetFiles(targetDirectory);
            foreach (string file in allFiles)
            {
                if (file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".mp", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    playlist.Add(file);
                }
            }
            playlist.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            ShowDebugColor(Color.cyan);
        }
    }

    void PlayCurrentVideo()
    {
        if (playlist.Count == 0)
        {
            ShowDebugColor(Color.yellow);
            return;
        }

        if (currentVideoIndex >= playlist.Count) currentVideoIndex = 0;

        string videoPath = playlist[currentVideoIndex];
        string videoUri = videoPath.StartsWith("file://") ? videoPath : "file://" + videoPath;

        videoPlayer.url = videoUri;
        videoPlayer.Play();

        if (videoSphere != null) videoSphere.SetActive(true);
    }

    void OnVideoFinished(VideoPlayer vp)
    {
        currentVideoIndex = (currentVideoIndex + 1) % playlist.Count;
        PlayCurrentVideo();
    }

    void OnVideoError(VideoPlayer vp, string message)
    {
        ShowDebugColor(Color.blue);
    }

    void ShowDebugColor(Color color)
    {
        if (Camera.main != null)
        {
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
            Camera.main.backgroundColor = color;
        }
        if (videoSphere != null) videoSphere.SetActive(false);
    }



    void OnDestroy()
    {
        Application.onBeforeRender -= SmoothHeadTracking;
    }

    void Update()
    {
        SmoothHeadTracking();
    }

    void SmoothHeadTracking()
    {
        // Manual head tracking that updates right before rendering to eliminate all jitter/shaking
        if (Camera.main != null && UnityEngine.XR.XRSettings.enabled)
        {
            Vector3 pos = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.CenterEye);
            Quaternion rot = UnityEngine.XR.InputTracking.GetLocalRotation(UnityEngine.XR.XRNode.CenterEye);

            Camera.main.transform.localPosition = pos;
            Camera.main.transform.localRotation = rot;

            // Make the sphere perfectly follow the camera's position (but NOT rotation).
            // This prevents positional warping/shaking if the user moves their body.
            if (videoSphere != null)
            {
                videoSphere.transform.position = Camera.main.transform.position;
            }
        }
    }// Auto-resume when headset is put back on (no controller needed)
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && videoPlayer != null && playlist.Count > 0)
        {
            if (!videoPlayer.isPlaying)
                videoPlayer.Play();
        }
    }

    void OnApplicationPause(bool pauseStatus)
    {
        if (!pauseStatus && videoPlayer != null && playlist.Count > 0)
        {
            // Headset was put back on — auto resume immediately
            StartCoroutine(AutoResumeAfterPause());
        }
    }

    IEnumerator AutoResumeAfterPause()
    {
        // Wait a tiny moment for the system to settle, then force resume
        yield return new WaitForSeconds(0.5f);
        if (videoPlayer != null && !videoPlayer.isPlaying && playlist.Count > 0)
        {
            videoPlayer.Play();
        }
    }
}
