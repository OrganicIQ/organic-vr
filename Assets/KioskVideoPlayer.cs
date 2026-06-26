using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;
using UnityEngine.XR;

// ===== DEBUG SCREEN COLOR LEGEND (what a solid-color screen means on the headset) =====
//   BLACK   = trial expired (see expiryDateString) — app intentionally disabled.
//   YELLOW  = video folder UNREACHABLE. Almost always missing All-Files-Access permission on Quest,
//             or the folder path is wrong. Grant All Files Access, then verify the path.
//   MAGENTA = folder reachable, but NO playable videos (.mp4/.mp/.mov/.mkv/.webm) found inside it.
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
    private GameObject sphereOU360;
    private GameObject sphereSBS360;
    private GameObject sphereEAC;        // YouTube Equi-Angular Cubemap (3x2 atlas) — genuine _eac files
    private GameObject sphereUpperDome;  // full-360 panorama packed into the top half (the misnamed _ytcubemap file)
    private GameObject flatScreen;
    private GameObject activeSphere;
    private bool isIntroPlaying = false;
    private bool wasRestartPressed = false;
    private bool isExpired = false;

    [Header("Operator Restart (controller)")]
    [Tooltip("Seconds the operator must HOLD any controller button to restart the loop. A continuous hold avoids accidental bumps restarting mid-session.")]
    public float restartHoldSeconds = 5f;
    private float restartHeldTime = 0f;

    [Header("Headset Removal")]
    [Tooltip("When true, the app lets Quest use its default sleep behavior as soon as the headset is removed.")]
    public bool allowSystemSleepWhenHeadsetRemoved = true;
    private bool wasHeadsetWorn = true;
    private bool restartFromBeginningOnNextResume = false;

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

        // NOTE: we no longer allocate a fixed-size RenderTexture here. A hardcoded 3840x2160 (16:9)
        // target squashed true 2:1 equirect videos (e.g. 4096x2048) — the top/bottom (pole) rows fell
        // outside the 16:9 frame and sampled black, which is what produced the cap-sized BLACK HOLE on
        // those files (while a 16:9 mp4 matched the target and looked fine). Instead we now size the
        // RenderTexture to each video's ACTUAL width/height after Prepare() reports them, in
        // OnVideoPrepared(). This preserves the native equirect aspect for any resolution.
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.isLooping = false; // We handle looping ourselves for playlist support
        videoPlayer.prepareCompleted += OnVideoPrepared;

        // Create the two different screens for mixed video formats
        CreateVideoSpheres();

        // Hide all surfaces initially for the Intro
        HideAllVideoSurfaces();

        // Set up video events
        videoPlayer.errorReceived += OnVideoError;
        videoPlayer.loopPointReached += OnVideoFinished;

        // Keep screen awake while the headset is being worn. When removed, CheckHeadsetPresence()
        // restores the system sleep behavior so Quest can turn off normally.
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

        // 3. True stereo over-under 360 sphere. The shader samples the correct half per eye.
        sphereOU360 = new GameObject("VideoSphere_OU360");
        sphereOU360.transform.position = Vector3.zero;
        sphereOU360.transform.localScale = new Vector3(100f, 100f, 100f);
        BuildEquirectSphere(sphereOU360, topHalfOnly: false);
        AssignMaterial(sphereOU360, "Custom/VideoSphereStereo");
        SetMaterialFloat(sphereOU360, "_Layout", 1f);
        sphereOU360.SetActive(false);

        // 4. True stereo side-by-side 360 sphere. The shader samples the correct half per eye.
        sphereSBS360 = new GameObject("VideoSphere_SBS360");
        sphereSBS360.transform.position = Vector3.zero;
        sphereSBS360.transform.localScale = new Vector3(100f, 100f, 100f);
        BuildEquirectSphere(sphereSBS360, topHalfOnly: false);
        AssignMaterial(sphereSBS360, "Custom/VideoSphereStereo");
        SetMaterialFloat(sphereSBS360, "_Layout", 2f);
        sphereSBS360.SetActive(false);

        // 5. YouTube EAC (Equi-Angular Cubemap) sphere. Same inward mesh, but the EAC shader samples by
        //    fragment DIRECTION (it ignores the baked equirect UVs) and unwraps YouTube's 3x2 cube atlas.
        sphereEAC = new GameObject("VideoSphere_EAC");
        sphereEAC.transform.position = Vector3.zero;
        sphereEAC.transform.localScale = new Vector3(100f, 100f, 100f);
        BuildEquirectSphere(sphereEAC, topHalfOnly: false);
        AssignMaterial(sphereEAC, "Custom/VideoSphereEAC");
        sphereEAC.SetActive(false);

        // 6. Upper-dome sphere for the misnamed "_ytcubemap" file: a full-360 panorama crammed into the
        //    TOP HALF of the frame (bottom half is dark void). Mapped horizon->zenith with NO vertical
        //    stretch (the cause of the "zoomed in" look on the over-under sphere); floor stays dark.
        sphereUpperDome = new GameObject("VideoSphere_UpperDome");
        sphereUpperDome.transform.position = Vector3.zero;
        sphereUpperDome.transform.localScale = new Vector3(100f, 100f, 100f);
        BuildEquirectSphere(sphereUpperDome, SphereMapping.UpperDomeTopHalf);
        AssignMaterial(sphereUpperDome);
        sphereUpperDome.SetActive(false);

        // 7. Regular flat video screen for non-360 clips.
        flatScreen = new GameObject("VideoScreen_Flat");
        flatScreen.transform.position = Vector3.zero;
        BuildFlatScreen(flatScreen);
        AssignMaterial(flatScreen);
        flatScreen.SetActive(false);
    }

    // Builds an inward-facing equirectangular UV sphere on the given GameObject.
    // Longitude maps to U (0..1 across 360 deg), latitude maps to V (0..1 bottom..top).
    // topHalfOnly remaps V into 0.5..1.0 for over/under (3D) videos so only the top half is sampled.
    // How the equirect texture maps onto the sphere's latitude (vertical) axis.
    enum SphereMapping
    {
        Full,             // whole texture across the whole sphere (standard mono 360)
        OverUnderTopHalf, // top 50% of texture = one eye, mapped across the whole sphere (over-under 3D)
        UpperDomeTopHalf  // top 50% of texture is a FULL-360 panorama covering only the UPPER hemisphere;
                          // map it horizon->zenith un-stretched, clamp the lower hemisphere to the dark edge.
    }

    // Back-compat overload (bool). false = Full, true = OverUnderTopHalf.
    void BuildEquirectSphere(GameObject go, bool topHalfOnly)
    {
        BuildEquirectSphere(go, topHalfOnly ? SphereMapping.OverUnderTopHalf : SphereMapping.Full);
    }

    void BuildEquirectSphere(GameObject go, SphereMapping mapping)
    {
        bool topHalfOnly = (mapping == SphereMapping.OverUnderTopHalf);
        const int longitudeSegments = 64; // around (horizontal)
        const int latitudeSegments = 32;  // top-to-bottom (vertical)

        // We build the sphere as: a grid of the INTERIOR latitude rings (lat = 1..latitudeSegments-1),
        // plus ONE dedicated apex vertex at each pole connected to the nearest ring by a triangle FAN.
        // The old code emitted degenerate (zero-area) triangles at the two pole rings because every
        // vertex of those rings sat at the same world point — those degenerate triangles rendered as
        // nothing, which is what produced the BLACK HOLE when looking straight up or down. A proper
        // pole apex + fan makes the poles watertight and fully textured.

        int interiorRings = latitudeSegments - 1;                 // rings at lat = 1 .. latitudeSegments-1
        int ringStride = longitudeSegments + 1;                   // verts per ring (last duplicates first for UV seam)
        int gridVertCount = interiorRings * ringStride;
        int topApexIndex = gridVertCount;                         // apex vertices appended after the grid
        int bottomApexIndex = gridVertCount + 1;
        int vertCount = gridVertCount + 2;

        Vector3[] vertices = new Vector3[vertCount];
        Vector2[] uv = new Vector2[vertCount];
        int v = 0;

        // --- Interior grid rings (skip the degenerate pole rings lat=0 and lat=latitudeSegments) ---
        for (int lat = 1; lat <= latitudeSegments - 1; lat++)
        {
            // theta: 0 at top pole -> PI at bottom pole
            float theta = Mathf.PI * lat / latitudeSegments;
            float sinTheta = Mathf.Sin(theta);
            float cosTheta = Mathf.Cos(theta);

            for (int lon = 0; lon <= longitudeSegments; lon++)
            {
                // SEAM-SAFE UV: u runs cleanly 0 -> 1 across the ring with NO wrap-around. The previous
                // code added a +0.5 phase to u and then did `if (u > 1) u -= 1`, which made the seam
                // triangle interpolate u from ~0.999 back to 0.0 — squashing the ENTIRE texture into one
                // sliver and producing the broad smeared "cylindrical line" at the back. The extra
                // (longitudeSegments+1)th vertex is a 3D duplicate of the first, and its u MUST stay at
                // 1.0 (not snap to 0.0) so the wrap edge samples texel 1.0 meeting texel 0.0 seamlessly.
                float u = (float)lon / longitudeSegments; // 0..1, no wrap

                // The "front faces +Z" framing that the +0.5 u-phase used to provide is now done in the
                // GEOMETRY instead: shift phi by PI so texture u=0.5 (the video's native front) lands on
                // +Z. This keeps recenterYawOffset semantics identical while leaving the UVs seam-clean.
                float phi = 2f * Mathf.PI * lon / longitudeSegments + Mathf.PI;
                float sinPhi = Mathf.Sin(phi);
                float cosPhi = Mathf.Cos(phi);

                // Unit position on the sphere.
                Vector3 pos = new Vector3(sinTheta * sinPhi, cosTheta, sinTheta * cosPhi);
                vertices[v] = pos;

                float vv = 1f - (float)lat / latitudeSegments; // top of texture at top of sphere
                if (topHalfOnly) vv = 0.5f + vv * 0.5f;          // sample only the top half (left eye)
                if (mapping == SphereMapping.UpperDomeTopHalf)
                {
                    // The real picture lives in the top 50% of the frame and is a full-360 panorama that
                    // only covers the UPPER hemisphere. Map zenith (theta=0) -> texture top (V=1.0) and
                    // horizon (theta=PI/2) -> the panorama's bottom edge (V=0.5). Below the horizon there
                    // is no data, so clamp to V=0.5 (the dark edge) instead of stretching the picture down.
                    // domeT: 1 at zenith -> 0 at horizon, then 0 across the whole lower hemisphere.
                    float domeT = Mathf.Clamp01(cosTheta); // cosTheta = 1 up, 0 at horizon, negative below
                    vv = 0.5f + 0.5f * domeT;
                }
                uv[v] = new Vector2(u, vv);

                v++;
            }
        }

        // --- Pole apex vertices. UV pinned to the texture's vertical center column (u=0.5) so the
        //     pinch point sits at the middle of the top/bottom edge of the equirect frame. ---
        float topVV = 1f;    // top edge of texture
        float bottomVV = 0f; // bottom edge of texture
        if (topHalfOnly) { topVV = 0.5f + topVV * 0.5f; bottomVV = 0.5f + bottomVV * 0.5f; }
        if (mapping == SphereMapping.UpperDomeTopHalf)
        {
            topVV = 1f;     // zenith = top of the panorama
            bottomVV = 0.5f; // nadir clamps to the panorama's dark bottom edge
        }
        vertices[topApexIndex] = new Vector3(0f, 1f, 0f);
        uv[topApexIndex] = new Vector2(0.5f, topVV);
        vertices[bottomApexIndex] = new Vector3(0f, -1f, 0f);
        uv[bottomApexIndex] = new Vector2(0.5f, bottomVV);

        // Triangles wound so the faces point INWARD (we sit inside the sphere).
        // Counts: grid quad bands = (interiorRings - 1) bands * longitudeSegments * 6,
        //         plus a top fan and a bottom fan of longitudeSegments * 3 each.
        int gridBands = interiorRings - 1;
        int[] triangles = new int[gridBands * longitudeSegments * 6 + longitudeSegments * 3 * 2];
        int t = 0;

        // Grid quad bands between adjacent interior rings.
        for (int ring = 0; ring < gridBands; ring++)
        {
            for (int lon = 0; lon < longitudeSegments; lon++)
            {
                int current = ring * ringStride + lon;
                int next = current + ringStride;

                // Inward winding (reverse of the usual outward order).
                triangles[t++] = current;
                triangles[t++] = current + 1;
                triangles[t++] = next;

                triangles[t++] = current + 1;
                triangles[t++] = next + 1;
                triangles[t++] = next;
            }
        }

        // Top fan: connect the apex to the FIRST interior ring (ring index 0, nearest the top pole).
        // Winding matches the inward-facing grid above.
        for (int lon = 0; lon < longitudeSegments; lon++)
        {
            int ringVert = lon;            // first ring starts at vertex 0
            triangles[t++] = topApexIndex;
            triangles[t++] = ringVert;
            triangles[t++] = ringVert + 1;
        }

        // Bottom fan: connect the apex to the LAST interior ring (nearest the bottom pole).
        int lastRingStart = (interiorRings - 1) * ringStride;
        for (int lon = 0; lon < longitudeSegments; lon++)
        {
            int ringVert = lastRingStart + lon;
            triangles[t++] = bottomApexIndex;
            triangles[t++] = ringVert + 1;
            triangles[t++] = ringVert;
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

    void BuildFlatScreen(GameObject go)
    {
        // A 16:9 screen six meters in front of the rig origin. The parent object follows the camera
        // position, while yaw rotation still uses the same _yaw token path as 360 content.
        float distance = 6f;
        float halfWidth = 3.2f;
        float halfHeight = 1.8f;

        Vector3[] vertices =
        {
            new Vector3(-halfWidth, -halfHeight, distance),
            new Vector3( halfWidth, -halfHeight, distance),
            new Vector3(-halfWidth,  halfHeight, distance),
            new Vector3( halfWidth,  halfHeight, distance)
        };

        Vector2[] uv =
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f)
        };

        int[] triangles = { 0, 2, 1, 2, 3, 1 };

        Mesh mesh = new Mesh();
        mesh.name = "FlatVideoScreen";
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>();
    }

    void SetMaterialFloat(GameObject obj, string propertyName, float value)
    {
        var renderer = obj != null ? obj.GetComponent<Renderer>() : null;
        if (renderer != null && renderer.material != null)
        {
            renderer.material.SetFloat(propertyName, value);
        }
    }

    void HideAllVideoSurfaces()
    {
        if (sphere3DV != null) sphere3DV.SetActive(false);
        if (sphereMono360 != null) sphereMono360.SetActive(false);
        if (sphereOU360 != null) sphereOU360.SetActive(false);
        if (sphereSBS360 != null) sphereSBS360.SetActive(false);
        if (sphereEAC != null) sphereEAC.SetActive(false);
        if (sphereUpperDome != null) sphereUpperDome.SetActive(false);
        if (flatScreen != null) flatScreen.SetActive(false);
    }

    void BindTexture(GameObject obj, Texture texture)
    {
        var renderer = obj != null ? obj.GetComponent<Renderer>() : null;
        if (renderer != null && renderer.material != null)
        {
            renderer.material.mainTexture = texture;
        }
    }

    void BindTextureToAllSurfaces(Texture texture)
    {
        BindTexture(sphere3DV, texture);
        BindTexture(sphereMono360, texture);
        BindTexture(sphereOU360, texture);
        BindTexture(sphereSBS360, texture);
        BindTexture(sphereEAC, texture);
        BindTexture(sphereUpperDome, texture);
        BindTexture(flatScreen, texture);
    }

    void AssignMaterial(GameObject obj, string shaderName = "Custom/VideoSphereUnlit")
    {
        // Use the requested custom URP-compatible shader (equirect unlit by default; EAC for cubemaps).
        Shader customShader = Shader.Find(shaderName);
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

    bool FileNameContains(string path, params string[] tokens)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (string token in tokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
                    file.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
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
        HideAllVideoSurfaces();

        // Determine which screen to use based on the filename tag.
        //
        // Recommended tags:
        //   __mono360  full-frame equirectangular 360 (default)
        //   __ou360    stereo over-under / top-bottom 360
        //   __sbs360   stereo side-by-side 360
        //   __cubemap  YouTube EAC cubemap atlas
        //   __flat     normal 2D video
        //
        // Older tags remain supported: _OU, _3dv, _eac, _ytcubemap, _ytcube.
        if (FileNameContains(videoPath, "__flat", "_flat"))
        {
            activeSphere = flatScreen;
        }
        else if (FileNameContains(videoPath, "__cubemap", "_cubemap", "__eac", "_eac"))
        {
            // Genuine YouTube Equi-Angular Cubemap (3x2 atlas) — dedicated EAC sphere/shader.
            activeSphere = sphereEAC;
        }
        else if (FileNameContains(videoPath, "__ytcubemap", "_ytcubemap", "__ytcube", "_ytcube"))
        {
            // MISNAMED file: real 360 panorama is in the TOP half of the 2:1 frame; the bottom half is
            // unwanted. The top-half sphere samples only the upper 50% and wraps it across the full
            // sphere, hiding the bottom band entirely — the layout the user asked to restore.
            activeSphere = sphere3DV;
        }
        else if (FileNameContains(videoPath, "__sbs360", "_sbs360", "__sbs", "_sbs", "_LR"))
        {
            activeSphere = sphereSBS360;
        }
        else if (FileNameContains(videoPath, "__ou360", "_ou360", "__tb360", "_tb360", "_3dv", "_OU"))
        {
            activeSphere = sphereOU360;
        }
        else // Fallback for _mono360 and everything else
        {
            activeSphere = sphereMono360;
        }

        Debug.Log("[KIOSK] Selected video surface = " + (activeSphere != null ? activeSphere.name : "none") +
                  " for " + Path.GetFileName(videoPath));

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

        // Prepare (not Play) first: once prepared, OnVideoPrepared sizes the RenderTexture to the
        // video's real dimensions, binds it to the active sphere, then starts playback. This is what
        // keeps a 2:1 equirect from being squashed into a 16:9 target (the black-hole-at-poles bug).
        videoPlayer.url = videoUri;
        videoPlayer.Prepare();
    }

    // Called once the VideoPlayer knows the real frame size. (Re)allocates videoRT to match the video's
    // native resolution so equirect poles aren't clipped, rebinds it to the sphere materials, then plays.
    void OnVideoPrepared(VideoPlayer vp)
    {
        int w = (int)vp.width;
        int h = (int)vp.height;
        if (w <= 0 || h <= 0) { w = 4096; h = 2048; } // safety fallback for a 2:1 equirect

        // Recreate the RenderTexture only when the size actually changes (avoids per-loop churn).
        if (videoRT == null || videoRT.width != w || videoRT.height != h)
        {
            if (videoRT != null) { videoRT.Release(); }
            videoRT = new RenderTexture(w, h, 0);
            // Repeat (not the default Clamp) so the equirect seam at u=1.0 blends into u=0.0 instead of
            // clamping to the edge texel — removes the faint hairline at the back of the 360 sphere.
            videoRT.wrapMode = TextureWrapMode.Repeat;
            videoRT.Create();

            videoPlayer.targetTexture = videoRT;

            BindTextureToAllSurfaces(videoRT);
        }

        vp.Play();
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
        HideAllVideoSurfaces();
    }



    void OnDestroy()
    {
        Application.onBeforeRender -= SmoothHeadTracking;
        if (videoPlayer != null) videoPlayer.prepareCompleted -= OnVideoPrepared;
    }

    void Update()
    {
        if (isExpired) return; // Do nothing if the trial has expired

        SmoothHeadTracking();
        CheckHeadsetPresence();
        CheckRestartInput();
    }

    void CheckHeadsetPresence()
    {
        bool isWorn = IsHeadsetWorn();

        if (!isWorn)
        {
            if (allowSystemSleepWhenHeadsetRemoved)
            {
                Screen.sleepTimeout = SleepTimeout.SystemSetting;
            }
        }
        else if (!wasHeadsetWorn)
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            if (videoPlayer != null && playlist.Count > 0 && !isIntroPlaying)
            {
                StartCoroutine(ResumeOrRestartAfterHeadsetReturn());
            }
        }

        wasHeadsetWorn = isWorn;
    }

    bool IsHeadsetWorn()
    {
        var hmd = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        if (hmd.isValid &&
            hmd.TryGetFeatureValue(UnityEngine.XR.CommonUsages.userPresence, out bool userPresent))
        {
            return userPresent;
        }

        return true;
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

        HideAllVideoSurfaces();

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
            if (sphereOU360 != null) sphereOU360.transform.position = Camera.main.transform.position;
            if (sphereSBS360 != null) sphereSBS360.transform.position = Camera.main.transform.position;
            if (sphereEAC != null) sphereEAC.transform.position = Camera.main.transform.position;
            if (sphereUpperDome != null) sphereUpperDome.transform.position = Camera.main.transform.position;
            if (flatScreen != null) flatScreen.transform.position = Camera.main.transform.position;
        }
    }// Auto-resume when headset is put back on (no controller needed)
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
            return;
        }

        if (videoPlayer != null && playlist.Count > 0 && !isIntroPlaying)
        {
            StartCoroutine(ResumeOrRestartAfterHeadsetReturn());
        }
    }

    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            MarkAppInactiveForRestart();
            return;
        }

        if (videoPlayer != null && playlist.Count > 0 && !isIntroPlaying)
        {
            // Headset was put back on — auto resume immediately
            StartCoroutine(ResumeOrRestartAfterHeadsetReturn());
        }
    }

    void MarkAppInactiveForRestart()
    {
        restartFromBeginningOnNextResume = true;
        Screen.sleepTimeout = SleepTimeout.SystemSetting;
        Debug.Log("[KIOSK] App paused/lost focus; next resume will restart the kiosk.");
    }

    IEnumerator ResumeOrRestartAfterHeadsetReturn()
    {
        yield return new WaitForSeconds(0.5f);

        if (videoPlayer == null || playlist.Count == 0 || isIntroPlaying || isExpired) yield break;

        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        if (restartFromBeginningOnNextResume)
        {
            restartFromBeginningOnNextResume = false;
            Debug.Log("[KIOSK] Restarting kiosk after app resumed from inactive state.");
            RestartKiosk();
            yield break;
        }

        if (!videoPlayer.isPlaying)
        {
            RecenterVideoSphere();
            videoPlayer.Play();
        }
    }
}
