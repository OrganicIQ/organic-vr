using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;
using UnityEngine.XR;
using System.Collections.Generic;

// ===== DEBUG SCREEN COLOR LEGEND (what a solid-color screen means on the headset) =====
//   BLACK   = trial expired (see expiryDateString) — app intentionally disabled.
//   YELLOW  = video folder UNREACHABLE. Almost always missing All-Files-Access permission on Quest,
//             or the folder path is wrong. Grant All Files Access, then verify the path.
//   MAGENTA = folder reachable, but NO playable videos (.mp4/.mp/.mov/.mkv) found inside it.
//             Check that your videos are actually in /storage/emulated/0/GeniMindsXR.
//   CYAN    = an exception was thrown while reading the folder (e.g. access denied mid-read).
//   BLUE    = the VideoPlayer reported a playback error on a specific file (codec/corrupt file).
// =======================================================================================
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
    private GameObject sphere3DV;
    private GameObject sphereMono360;
    private GameObject activeSphere;
    private bool isIntroPlaying = false;
    private bool wasRestartPressed = false;
    private bool isExpired = false;

    [Header("Operator Restart (controller)")]
    [Tooltip("Seconds the operator must HOLD any controller button to restart the loop. A continuous hold avoids accidental bumps restarting mid-session.")]
    public float restartHoldSeconds = 5f;
    private float restartHeldTime = 0f;

    [Header("Recenter Settings")]
    [Tooltip("DEFAULT world yaw (degrees) the video's native front is locked to, VLC-style — independent of head direction. Used when a video filename has no _yaw token. 0 = front faces world-forward. Per-video override: add _yaw<number> to the filename, e.g. moon_yaw30.mp4 (rotate 30° right-to-front) or forest_yaw-25.mp4.")]
    public float recenterYawOffset = 0f;

    // Per-video yaw, parsed from the current filename's _yaw<number> token. Falls back to
    // recenterYawOffset when the token is absent. Lets each equirect file whose source front is
    // off-center be pointed forward individually, without a rebuild.
    float currentVideoYaw = 0f;

    [Header("Trial Version Settings")]
    [Tooltip("If true, the app will stop working after the expiry date.")]
    public bool enableExpiryDate = true;
    [Tooltip("The date the app stops working (YYYY-MM-DD)")]
    public string expiryDateString = "2026-06-30"; // Default: June 30, 2026

    // ---- Anti-rollback hard lock ----------------------------------------------------------
    // The plain expiry check above can be defeated by setting the headset's clock back to before
    // the expiry date. To harden it, we persist the latest date we have ever seen the app run on,
    // in an obfuscated app-private file (survives app restarts; only wiped on uninstall). On every
    // launch we compare the current clock against that high-water mark:
    //   * If the clock reads EARLIER than the last run, the clock was rolled back -> lock (black).
    //   * If the clock is past the expiry date -> lock (black).
    //   * Otherwise advance the high-water mark to today.
    // This makes the obvious "set date to 2020" bypass fail. (A user with ADB file access could
    // still delete the file; this is a soft-but-much-harder lock, appropriate for an offline kiosk.)

    // Allow a tiny clock jitter (timezone / NTP correction) before treating it as a rollback.
    const double rollbackGraceDays = 2.0;

    string LicenseStampPath()
    {
        // App-private, not visible in the public videos folder. Obscure filename.
        return System.IO.Path.Combine(Application.persistentDataPath, ".sysmeta.dat");
    }

    // Obfuscate the stored date so it isn't a plainly-editable text date. Reversible XOR + Base64.
    string ObfuscateTicks(long ticks)
    {
        byte[] raw = System.BitConverter.GetBytes(ticks);
        for (int i = 0; i < raw.Length; i++) raw[i] ^= (byte)(0x5A + i * 7);
        return System.Convert.ToBase64String(raw);
    }

    bool TryDeobfuscateTicks(string text, out long ticks)
    {
        ticks = 0;
        try
        {
            byte[] raw = System.Convert.FromBase64String(text.Trim());
            if (raw.Length != 8) return false;
            for (int i = 0; i < raw.Length; i++) raw[i] ^= (byte)(0x5A + i * 7);
            ticks = System.BitConverter.ToInt64(raw, 0);
            return true;
        }
        catch { return false; }
    }

    void WriteLicenseStamp(System.DateTime when)
    {
        try { System.IO.File.WriteAllText(LicenseStampPath(), ObfuscateTicks(when.Ticks)); }
        catch (Exception e) { Debug.Log("[KIOSK] License stamp write failed: " + e.Message); }
    }

    bool IsLicenseExpired()
    {
        System.DateTime now = System.DateTime.Now;

        // (a) Hard expiry date.
        System.DateTime expiryDate;
        if (System.DateTime.TryParse(expiryDateString, out expiryDate) && now > expiryDate)
        {
            Debug.Log("[KIOSK] License expired: past expiry date " + expiryDateString + ".");
            return true;
        }

        // (b) Anti-rollback against the stored high-water mark.
        string path = LicenseStampPath();
        try
        {
            if (System.IO.File.Exists(path))
            {
                if (TryDeobfuscateTicks(System.IO.File.ReadAllText(path), out long savedTicks))
                {
                    System.DateTime lastSeen = new System.DateTime(savedTicks);
                    // Clock moved meaningfully BACKWARD vs the last run -> tampering.
                    if (now < lastSeen.AddDays(-rollbackGraceDays))
                    {
                        Debug.Log("[KIOSK] License locked: clock rolled back (now " + now +
                                  " < last seen " + lastSeen + ").");
                        return true;
                    }
                }
                else
                {
                    // File exists but is unreadable/corrupted -> treat as tampering.
                    Debug.Log("[KIOSK] License locked: stamp file unreadable/tampered.");
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Debug.Log("[KIOSK] License stamp read failed (allowing run): " + e.Message);
        }

        // Not expired: advance the high-water mark to the latest date we've seen.
        WriteLicenseStamp(now);
        return false;
    }
    // ---------------------------------------------------------------------------------------

    void Start()
    {
        // 1. Check Expiry Date First (with anti-rollback hard lock)
        if (enableExpiryDate)
        {
            if (IsLicenseExpired())
            {
                isExpired = true;
                // Make the screen go completely pitch black
                ShowDebugColor(Color.black);
                return; // Stop the app from initializing entirely
            }
        }

        // Force the OS to treat this as a stationary 3DOF experience to heavily reduce Guardian interruptions
        List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (var subsystem in subsystems)
        {
            subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
        }

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

        videoPlayer = GetComponent<VideoPlayer>();

        // Create a RenderTexture for the video
        videoRT = new RenderTexture(3840, 2160, 0);
        videoRT.Create();

        // Configure VideoPlayer to render into our RenderTexture
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.targetTexture = videoRT;
        videoPlayer.isLooping = false; // We handle looping ourselves for playlist support

        // Create the two different screens for mixed video formats
        CreateVideoSpheres();

        // Hide both spheres initially for the Intro
        if (sphere3DV != null) sphere3DV.SetActive(false);
        if (sphereMono360 != null) sphereMono360.SetActive(false);

        // Set up video events
        videoPlayer.errorReceived += OnVideoError;
        videoPlayer.loopPointReached += OnVideoFinished;

        // Keep screen always on (prevents Quest from sleeping during kiosk playback)
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        // Start the permission and initialization sequence
        StartCoroutine(WaitForPermissionAndStart());
    }

    System.Collections.IEnumerator WaitForPermissionAndStart()
    {
        yield return null; // Ensures the method compiles as a valid IEnumerator even on PC

#if UNITY_ANDROID && !UNITY_EDITOR
        // Legacy read permission (needed on older Android; harmless on newer).
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageRead))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageRead);

            // Wait here until the user physically clicks "Allow" in the headset
            float waited = 0f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageRead) && waited < 10f)
            {
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }
        }

        // Quest runs Android 10+ with scoped storage. To read a top-level folder like
        // /storage/emulated/0/GeniMindsXR we need MANAGE_EXTERNAL_STORAGE (All Files Access).
        // Request it once; this opens the system All-Files-Access screen for the operator to enable.
        RequestAllFilesAccessIfNeeded();
#endif

        InitializePlaylist();

        if (playlist.Count > 0)
        {
            StartCoroutine(PlayIntroSequence());
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    void RequestAllFilesAccessIfNeeded()
    {
        try
        {
            using (var env = new AndroidJavaClass("android.os.Environment"))
            {
                // Environment.isExternalStorageManager() — true once the operator grants All Files Access.
                bool isManager = env.CallStatic<bool>("isExternalStorageManager");
                if (isManager) return;
            }

            // Launch the system "All files access" settings page for this app so the operator can flip it on.
            using (var settings = new AndroidJavaClass("android.provider.Settings"))
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var uriClass = new AndroidJavaClass("android.net.Uri"))
            {
                string action = settings.GetStatic<string>("ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION");
                string pkg = activity.Call<string>("getPackageName");
                var uri = uriClass.CallStatic<AndroidJavaObject>("parse", "package:" + pkg);
                using (var intent = new AndroidJavaObject("android.content.Intent", action, uri))
                {
                    activity.Call("startActivity", intent);
                }
            }
        }
        catch (Exception)
        {
            // Older Android or API mismatch — fall through; legacy read permission may suffice.
        }
    }
#endif

    void CreateVideoSpheres()
    {
        // We build PROPER equirectangular spheres procedurally instead of using Unity's primitive sphere.
        // Unity's CreatePrimitive(Sphere) does NOT have an equirect UV layout — its seam/front sit at an
        // arbitrary spot and it distorts at the poles, which is why the front used to land at a different
        // angle per video and a stitch line was visible. A real equirect sphere maps longitude 0 of every
        // video to a fixed world direction (VLC-style), consistently for all files.

        // 1. Over/Under 3DV sphere — uses only the TOP half of the texture (left eye).
        sphere3DV = new GameObject("VideoSphere_3DV");
        sphere3DV.transform.position = Vector3.zero;
        sphere3DV.transform.localScale = new Vector3(100f, 100f, 100f);
        BuildEquirectSphere(sphere3DV, topHalfOnly: true);
        AssignMaterial(sphere3DV);
        sphere3DV.SetActive(false);

        // 2. Standard mono 360 sphere — uses the full texture.
        sphereMono360 = new GameObject("VideoSphere_Mono360");
        sphereMono360.transform.position = Vector3.zero;
        sphereMono360.transform.localScale = new Vector3(100f, 100f, 100f);
        BuildEquirectSphere(sphereMono360, topHalfOnly: false);
        AssignMaterial(sphereMono360);
        sphereMono360.SetActive(false);
    }

    // Builds an inward-facing equirectangular UV sphere on the given GameObject.
    // Longitude maps to U (0..1 across 360 deg), latitude maps to V (0..1 bottom..top).
    // topHalfOnly remaps V into 0.5..1.0 for over/under (3D) videos so only the top half is sampled.
    void BuildEquirectSphere(GameObject go, bool topHalfOnly)
    {
        const int longitudeSegments = 64; // around (horizontal)
        const int latitudeSegments = 32;  // top-to-bottom (vertical)

        int vertCount = (longitudeSegments + 1) * (latitudeSegments + 1);
        Vector3[] vertices = new Vector3[vertCount];
        Vector2[] uv = new Vector2[vertCount];
        int v = 0;

        for (int lat = 0; lat <= latitudeSegments; lat++)
        {
            // theta: 0 at top pole -> PI at bottom pole
            float theta = Mathf.PI * lat / latitudeSegments;
            float sinTheta = Mathf.Sin(theta);
            float cosTheta = Mathf.Cos(theta);

            for (int lon = 0; lon <= longitudeSegments; lon++)
            {
                // phi: 0..2PI around. Front of the video (longitude 0) faces +Z.
                float phi = 2f * Mathf.PI * lon / longitudeSegments;
                float sinPhi = Mathf.Sin(phi);
                float cosPhi = Mathf.Cos(phi);

                // Unit position on the sphere.
                Vector3 pos = new Vector3(sinTheta * sinPhi, cosTheta, sinTheta * cosPhi);
                vertices[v] = pos;

                // Equirect UVs. U INCREASES with lon (matching the surface sweeping to the viewer's
                // right), so text is NOT mirrored when viewed from inside the sphere. The +0.5 phase
                // shift makes the CENTER of the video (texture u=0.5 = the scene's native front) land on
                // the forward vertex (+Z), so a recenterYawOffset of 0 points the front straight ahead.
                float u = 0.5f + (float)lon / longitudeSegments;
                if (u > 1f) u -= 1f; // wrap into [0,1]
                float vv = 1f - (float)lat / latitudeSegments; // top of texture at top of sphere
                if (topHalfOnly) vv = 0.5f + vv * 0.5f;          // sample only the top half (left eye)
                uv[v] = new Vector2(u, vv);

                v++;
            }
        }

        // Triangles wound so the faces point INWARD (we sit inside the sphere).
        int[] triangles = new int[longitudeSegments * latitudeSegments * 6];
        int t = 0;
        int stride = longitudeSegments + 1;
        for (int lat = 0; lat < latitudeSegments; lat++)
        {
            for (int lon = 0; lon < longitudeSegments; lon++)
            {
                int current = lat * stride + lon;
                int next = current + stride;

                // Inward winding (reverse of the usual outward order).
                triangles[t++] = current;
                triangles[t++] = current + 1;
                triangles[t++] = next;

                triangles[t++] = current + 1;
                triangles[t++] = next + 1;
                triangles[t++] = next;
            }
        }

        Mesh mesh = new Mesh();
        mesh.name = topHalfOnly ? "EquirectSphere_TopHalf" : "EquirectSphere_Full";
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>();
    }

    void AssignMaterial(GameObject obj)
    {
        // Use our custom URP-compatible shader
        Shader customShader = Shader.Find("Custom/VideoSphereUnlit");
        if (customShader != null)
        {
            Material videoMat = new Material(customShader);
            videoMat.mainTexture = videoRT;
            obj.GetComponent<Renderer>().material = videoMat;
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
                obj.GetComponent<Renderer>().material = videoMat;
            }
        }
    }


    void InitializePlaylist()
    {
        if (initialized) return;
        initialized = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        // Target the absolute root folder of the headset so it's instantly visible on PC
        targetDirectory = "/storage/emulated/0/GeniMindsXR";
#else
        // For testing in the Unity Editor on PC
        targetDirectory = Path.Combine(Application.dataPath, "GeniMindsXR");
#endif

        Debug.Log("[KIOSK] Target directory = " + targetDirectory);
        bool exists = Directory.Exists(targetDirectory);
        Debug.Log("[KIOSK] Directory.Exists = " + exists);

        if (!exists)
        {
            try { Directory.CreateDirectory(targetDirectory); } catch (Exception e) { Debug.Log("[KIOSK] CreateDirectory failed: " + e.Message); }
            // Folder unreachable (usually missing All-Files-Access on Quest) — show yellow as a signal.
            Debug.Log("[KIOSK] Folder UNREACHABLE -> yellow");
            ShowDebugColor(Color.yellow);
            return;
        }

        LoadPlaylist();
        Debug.Log("[KIOSK] Playlist count after load = " + playlist.Count);

        // Folder is reachable, but no playable videos were found inside it.
        // Distinct color (magenta) so on-site we can tell this apart from "folder unreachable" (yellow).
        if (playlist.Count == 0)
        {
            ShowDebugColor(Color.magenta);
            return;
        }
        // NOTE: the intro is started by the caller (WaitForPermissionAndStart) so it only ever runs once.
    }

    IEnumerator PlayIntroSequence()
    {
        isIntroPlaying = true;

        // Ensure background is black
        if (Camera.main != null)
        {
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
            Camera.main.backgroundColor = Color.black;
        }

        // Create World Space Canvas, place it 5 meters in front of wherever the camera starts,
        // but DO NOT parent it to the camera. This makes it a true 3D object in the world.
        GameObject canvasObj = new GameObject("IntroCanvas");
        Transform camT = Camera.main.transform;
        canvasObj.transform.position = camT.position + camT.forward * 5f;
        canvasObj.transform.rotation = Quaternion.LookRotation(camT.forward);
        canvasObj.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f); // Scale down for VR

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        
        // Add a Canvas Scaler
        canvasObj.AddComponent<CanvasScaler>();

        CanvasGroup canvasGroup = canvasObj.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f; // Start invisible

        // Create Text Element
        GameObject textObj = new GameObject("OrganicIQText");
        textObj.transform.SetParent(canvasObj.transform, false);

        // Ensure the text rect is perfectly centered and large enough
        RectTransform rt = textObj.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(2000f, 500f);

        Text textComp = textObj.AddComponent<Text>();
        textComp.text = "Organic IQ";
        textComp.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        textComp.fontSize = 200;
        textComp.alignment = TextAnchor.MiddleCenter;
        textComp.horizontalOverflow = HorizontalWrapMode.Overflow;
        textComp.verticalOverflow = VerticalWrapMode.Overflow;
        
        // Beautiful Organic Colors (Green to Blue)
        Color color1 = new Color(0.2f, 0.8f, 0.2f); // Vibrant Green
        Color color2 = new Color(0.1f, 0.6f, 0.9f); // Ocean Blue

        // Intro Animation Loop (5 seconds total)
        float elapsed = 0f;
        float duration = 5f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            // Fade in over 1 second, fade out over the last 1 second
            if (elapsed < 1f) canvasGroup.alpha = elapsed;
            else if (elapsed > duration - 1f) canvasGroup.alpha = duration - elapsed;
            else canvasGroup.alpha = 1f;

            // Animate Text Color smoothly back and forth
            float colorT = Mathf.PingPong(elapsed * 0.5f, 1f);
            textComp.color = Color.Lerp(color1, color2, colorT);

            // Animate Scale (subtle breathing effect)
            float scaleBase = 0.01f;
            float scalePulsing = scaleBase + Mathf.Sin(elapsed * 2f) * 0.0005f;
            canvasObj.transform.localScale = new Vector3(scalePulsing, scalePulsing, scalePulsing);

            // Wait for next frame
            yield return null;
        }

        // Clean up Intro
        Destroy(canvasObj);
        isIntroPlaying = false;

        // Finally start the video
        PlayCurrentVideo();
    }

    void RecenterVideoSphere()
    {
        if (activeSphere == null) return;

        // VLC-style behaviour: the video's native front is locked to a FIXED world direction,
        // independent of where the user's head is pointing. The user can freely look around and the
        // front always sits at the same world spot — exactly like opening a 360 video in VLC.
        //
        // We deliberately do NOT rotate by head yaw. Doing that made the front land wherever the head
        // happened to be at the instant the video started, so every video ended up off by a different
        // amount (e.g. one 15 deg, another 55 deg). A fixed world orientation makes ALL videos identical.
        //
        // currentVideoYaw is the per-video front offset (from the filename _yaw token), or the global
        // recenterYawOffset default when the filename has no token. This is what lets two videos whose
        // source fronts sit at different horizontal positions both be pointed forward.
        activeSphere.transform.rotation = Quaternion.Euler(0, currentVideoYaw, 0);
    }

    // Reads a "_yaw<number>" token from a filename and returns the degrees (supports negatives and
    // decimals), e.g. "city_yaw-22.5.mp4" -> -22.5. Returns recenterYawOffset when no token is found.
    float ParseYawFromFilename(string path)
    {
        try
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            var match = System.Text.RegularExpressions.Regex.Match(
                name, @"_yaw(-?\d+(\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                float.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float yaw))
            {
                Debug.Log("[KIOSK] Per-video yaw from filename = " + yaw + " (" + name + ")");
                return yaw;
            }
        }
        catch (Exception e)
        {
            Debug.Log("[KIOSK] ParseYawFromFilename error: " + e.Message);
        }
        return recenterYawOffset; // no token -> global default
    }

    void LoadPlaylist()
    {
        playlist.Clear();
        try
        {
            string[] allFiles = Directory.GetFiles(targetDirectory);
            Debug.Log("[KIOSK] GetFiles returned " + allFiles.Length + " entries:");
            foreach (string f in allFiles) Debug.Log("[KIOSK]   file: " + f);
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
            Debug.Log("[KIOSK] Playable videos found = " + playlist.Count);
        }
        catch (Exception e)
        {
            Debug.Log("[KIOSK] LoadPlaylist EXCEPTION: " + e.GetType().Name + " - " + e.Message);
            ShowDebugColor(Color.cyan);
        }
    }

    void PlayCurrentVideo()
    {
        if (playlist.Count == 0)
        {
            // No videos to play (folder was reachable but empty) — magenta, matching the legend.
            ShowDebugColor(Color.magenta);
            return;
        }

        if (currentVideoIndex >= playlist.Count) currentVideoIndex = 0;

        string videoPath = playlist[currentVideoIndex];
        string videoUri = videoPath.StartsWith("file://") ? videoPath : "file://" + videoPath;

        // Hide all screens
        if (sphere3DV != null) sphere3DV.SetActive(false);
        if (sphereMono360 != null) sphereMono360.SetActive(false);

        // Determine which screen to use based on the filename tag
        if (videoPath.Contains("_3dv", StringComparison.OrdinalIgnoreCase) || 
            videoPath.Contains("_OU", StringComparison.OrdinalIgnoreCase) ||
            videoPath.Contains("_ytcubemap", StringComparison.OrdinalIgnoreCase))
        {
            activeSphere = sphere3DV;
        }
        else // Fallback for _mono360 and everything else
        {
            activeSphere = sphereMono360;
        }

        // Show the correct screen
        if (activeSphere != null) activeSphere.SetActive(true);

        // Parse a per-video front offset from the filename, e.g. "moon_yaw30.mp4" -> 30 degrees.
        // No token -> use the global recenterYawOffset default.
        currentVideoYaw = ParseYawFromFilename(videoPath);

        // Recenter the world to the user's physical orientation every time a new video starts.
        // Do it immediately (so there's no visible swing) AND again next frame, after head tracking
        // has updated this frame's pose — this is what makes the front land consistently every time.
        RecenterVideoSphere();
        StartCoroutine(RecenterNextFrame());

        videoPlayer.url = videoUri;
        videoPlayer.Play();
    }

    IEnumerator RecenterNextFrame()
    {
        yield return null; // let SmoothHeadTracking write the fresh head pose for this frame
        RecenterVideoSphere();
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
        if (sphere3DV != null) sphere3DV.SetActive(false);
        if (sphereMono360 != null) sphereMono360.SetActive(false);
    }



    void OnDestroy()
    {
        Application.onBeforeRender -= SmoothHeadTracking;
    }

    void Update()
    {
        if (isExpired) return; // Do nothing if the trial has expired

        SmoothHeadTracking();
        CheckRestartInput();
    }

    void CheckRestartInput()
    {
        // OPERATOR RESTART (controller button HELD for restartHoldSeconds). The on-screen laser pointer
        // disappears inside the immersive app, but the controller's BUTTON values still come through
        // InputDevices — so a held button works even though there's no visible pointer. We accept ANY
        // of the common buttons on EITHER controller. A continuous hold avoids accidental bumps
        // restarting mid-session.
        bool restartPressed = false;
        var leftHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
        var rightHand = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);

        restartPressed |= AnyControllerButton(rightHand);
        restartPressed |= AnyControllerButton(leftHand);

        if (restartPressed)
        {
            restartHeldTime += Time.deltaTime;
            if (restartHeldTime >= restartHoldSeconds && !wasRestartPressed)
            {
                wasRestartPressed = true; // latch so it fires once per hold
                Debug.Log("[KIOSK] Controller button restart triggered (held " + restartHoldSeconds + "s).");
                RestartKiosk();
            }
        }
        else
        {
            restartHeldTime = 0f;     // released — reset the hold timer
            wasRestartPressed = false;
        }
    }

    // True if any of the common face buttons / triggers / grips on a controller are pressed.
    bool AnyControllerButton(UnityEngine.XR.InputDevice device)
    {
        if (!device.isValid) return false;
        if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton,   out bool a) && a) return true;  // A / X
        if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool b) && b) return true;  // B / Y
        if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton,   out bool t) && t) return true;  // index trigger
        if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton,      out bool g) && g) return true;  // grip
        return false;
    }

    void RestartKiosk()
    {
        if (isIntroPlaying) return; // Prevent restarting if the intro is already playing

        if (videoPlayer != null) videoPlayer.Stop();
        
        if (sphere3DV != null) sphere3DV.SetActive(false);
        if (sphereMono360 != null) sphereMono360.SetActive(false);

        // Reset to the very first video
        currentVideoIndex = 0;

        // Start the beautiful intro sequence all over again
        StartCoroutine(PlayIntroSequence());
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

            // Make the spheres perfectly follow the camera's position (but NOT rotation).
            // This prevents positional warping/shaking if the user moves their body.
            if (sphere3DV != null) sphere3DV.transform.position = Camera.main.transform.position;
            if (sphereMono360 != null) sphereMono360.transform.position = Camera.main.transform.position;
        }
    }// Auto-resume when headset is put back on (no controller needed)
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && videoPlayer != null && playlist.Count > 0 && !isIntroPlaying)
        {
            if (!videoPlayer.isPlaying)
                videoPlayer.Play();
        }
    }

    void OnApplicationPause(bool pauseStatus)
    {
        if (!pauseStatus && videoPlayer != null && playlist.Count > 0 && !isIntroPlaying)
        {
            // Headset was put back on — auto resume immediately
            StartCoroutine(AutoResumeAfterPause());
        }
    }

    IEnumerator AutoResumeAfterPause()
    {
        // Wait a tiny moment for the system to settle, then force resume
        yield return new WaitForSeconds(0.5f);
        if (videoPlayer != null && !videoPlayer.isPlaying && playlist.Count > 0 && !isIntroPlaying)
        {
            RecenterVideoSphere();
            videoPlayer.Play();
        }
    }
}
