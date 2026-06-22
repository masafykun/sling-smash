using System.Collections.Generic;
using UnityEngine;

// SLING SMASH — 3D slingshot demolition (Angry-Birds-in-3D).
// ONE control: DRAG BACK & RELEASE. Pull the pouch back/down to load power, drag sideways to aim,
// and a dotted trajectory shows exactly where the ball will fly. Release to launch a heavy ball in
// a 3D arc that smashes into a chunky ziggurat of crates & stone. Knock the glowing CRYSTAL targets
// off the platform (or smack them hard) to clear the level. Limited shots — clear before you run out.
// Chain pops for a combo multiplier, earn clear bonuses, chase your BEST. Levels grow taller.
//
// Built entirely from code (CreatePrimitive) so it renders & simulates reliably in WebGL with
// engine-code stripping DISABLED (see AutoBuilder). Uses ONLY Box/Sphere/Capsule colliders that ship
// with primitives — never MeshCollider (that was coin-cruiser's WebGL killer). Real Rigidbody physics
// drives the toppling; the rest (preview dots, debris) is collider-free. Coexists with Juice & AutoShot.
public class SlingSmash : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__SlingSmash");
        go.AddComponent<SlingSmash>();
        DontDestroyOnLoad(go);
    }

    // ---- scene refs ----
    Transform cam; Camera camComp;
    Transform pouch, band0, band1;     // slingshot pouch + two elastic bands
    Vector3 pouchRest;
    TextMesh hudScore, hudBest, hudShots, hudLevel, hudTargets, hudHint, comboText, bannerText, dbg;

    readonly List<Block> blocks = new List<Block>();
    readonly List<Transform> dots = new List<Transform>();
    Projectile activeProj;

    // ---- state ----
    enum State { Idle, Flying, Cleared, GameOver }
    State state = State.Idle;
    int score, best, level = 1, shots, targetsRemaining;
    int combo; float comboTimer, comboFlash, bannerTimer, clearTimer;
    bool aiming; Vector2 dragStart; Vector3 lastVel; float lastSpeed01, lastYaw;
    bool attract = true, started; float attractTimer;
    float aspect = 1.78f, hudScale = 1f, halfH, halfW;

    // ---- tuning ----
    const float TOWER_Z = 6.0f;          // structure centre (forward = +Z)
    static readonly Vector3 LAUNCH = new Vector3(0f, 2.45f, -2.2f);
    const float MAXDRAG = 260f;
    const float MINSPEED = 7f, MAXSPEED = 20f;
    const float MAXYAW = 42f;
    float towerTopY = 5f;                 // top of the current structure (for attract aiming)
    const float KILL_Y = -3.2f;          // fallen off the platform
    const float HUD_Z = 6.5f;
    const int DOT_COUNT = 22;

    // debug
    bool showDbg; int dbgPops, dbgKnocks;

    Material crateMat, crateMat2, stoneMat, targMat, targMat2, platMat, edgeMat, ballMat, ballCore, bandMat, postMat, dotMat, dotMatFar;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("slingsmash_best", 0);

        Physics.gravity = new Vector3(0f, -22f, 0f);
        Physics.defaultSolverIterations = 12;

        BuildMaterials();
        BuildWorld();
        BuildCamera();
        BuildSling();
        BuildHud();
        BuildDots();

        level = 1; score = 0;
        BuildLevel();
        attractTimer = 1.0f;
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metal = 0f, float smooth = 0.25f, bool emi = false, float ei = 0.8f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit"); if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emi && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * ei); }
        return m;
    }

    void BuildMaterials()
    {
        crateMat  = Mat(new Color(0.78f, 0.52f, 0.26f), 0f, 0.18f);
        crateMat2 = Mat(new Color(0.86f, 0.62f, 0.32f), 0f, 0.18f);
        stoneMat  = Mat(new Color(0.45f, 0.48f, 0.55f), 0.15f, 0.30f);
        targMat   = Mat(new Color(0.25f, 0.95f, 0.85f), 0f, 0.7f, true, 1.5f);
        targMat2  = Mat(new Color(1.00f, 0.45f, 0.85f), 0f, 0.7f, true, 1.5f);
        platMat   = Mat(new Color(0.22f, 0.40f, 0.30f), 0f, 0.20f);
        edgeMat   = Mat(new Color(0.40f, 0.95f, 0.70f), 0f, 0.4f, true, 0.7f);
        ballMat   = Mat(new Color(0.95f, 0.97f, 1.00f), 0.5f, 0.85f);
        ballCore  = Mat(new Color(1.00f, 0.75f, 0.25f), 0f, 0.6f, true, 1.4f);
        bandMat   = Mat(new Color(0.55f, 0.20f, 0.16f), 0f, 0.3f);
        postMat   = Mat(new Color(0.40f, 0.28f, 0.18f), 0f, 0.25f);
        dotMat    = Mat(new Color(1.00f, 0.92f, 0.45f), 0f, 0.5f, true, 1.2f);
        dotMatFar = Mat(new Color(1.00f, 0.55f, 0.35f), 0f, 0.5f, true, 1.0f);
    }

    // ===================================================================== world
    static GameObject Prim(PrimitiveType pt, Vector3 pos, Vector3 scale, Material m, bool keepCollider)
    {
        var g = GameObject.CreatePrimitive(pt);
        if (!keepCollider) { var c = g.GetComponent<Collider>(); if (c) Destroy(c); }
        g.transform.position = pos; g.transform.localScale = scale;
        g.GetComponent<Renderer>().sharedMaterial = m;
        return g;
    }

    void BuildWorld()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1.0f, 0.96f, 0.86f);
        sun.intensity = 1.15f;
        sun.transform.rotation = Quaternion.Euler(46f, 34f, 0f);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.6f;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.55f, 0.62f, 0.78f);
        RenderSettings.ambientEquatorColor = new Color(0.40f, 0.42f, 0.46f);
        RenderSettings.ambientGroundColor  = new Color(0.20f, 0.22f, 0.24f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.86f);
        RenderSettings.fogStartDistance = 28f;
        RenderSettings.fogEndDistance = 95f;

        // finite platform (blocks knocked past the edge fall into the void)
        var plat = Prim(PrimitiveType.Cube, new Vector3(0f, -0.5f, TOWER_Z - 0.5f), new Vector3(11f, 1f, 13f), platMat, true);
        plat.name = "Platform";
        // glowing rim around the platform top
        for (int s = -1; s <= 1; s += 2)
            Prim(PrimitiveType.Cube, new Vector3(s * 5.5f, 0.02f, TOWER_Z - 0.5f), new Vector3(0.16f, 0.06f, 13f), edgeMat, false);
        Prim(PrimitiveType.Cube, new Vector3(0f, 0.02f, TOWER_Z + 6f), new Vector3(11f, 0.06f, 0.16f), edgeMat, false);
        Prim(PrimitiveType.Cube, new Vector3(0f, 0.02f, TOWER_Z - 7f), new Vector3(11f, 0.06f, 0.16f), edgeMat, false);

        // a few distant silhouette pylons for depth
        for (int i = 0; i < 6; i++)
        {
            float x = (i - 2.5f) * 9f;
            Prim(PrimitiveType.Cube, new Vector3(x, 4f, TOWER_Z + 38f + (i % 2) * 6f), new Vector3(3f, 8f + (i % 3) * 3f, 3f),
                 Mat(new Color(0.30f, 0.36f, 0.46f), 0f, 0.1f), false);
        }
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera"); cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.66f, 0.78f, 0.92f);
        camComp.fieldOfView = 52f;
        camComp.farClipPlane = 220f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0f, 6.2f, -11.5f);
        cam.rotation = Quaternion.LookRotation(new Vector3(0f, 2.6f, TOWER_Z) - cam.position, Vector3.up);
    }

    void BuildSling()
    {
        // two angled posts behind the launch point
        for (int s = -1; s <= 1; s += 2)
            Prim(PrimitiveType.Cube, new Vector3(s * 0.65f, 1.0f, LAUNCH.z - 0.1f), new Vector3(0.22f, 2.0f, 0.22f), postMat, false);
        // pouch (the ball-holder), starts at rest
        pouchRest = LAUNCH;
        var p = Prim(PrimitiveType.Sphere, pouchRest, Vector3.one * 0.45f, ballMat, false);
        p.name = "Pouch"; pouch = p.transform;
        Prim(PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.5f, ballCore, false).transform.SetParent(pouch, false);

        band0 = MakeBand(); band1 = MakeBand();
        UpdateBands();
    }

    Transform MakeBand()
    {
        var b = Prim(PrimitiveType.Cube, Vector3.zero, new Vector3(0.06f, 0.06f, 1f), bandMat, false);
        return b.transform;
    }

    void UpdateBands()
    {
        if (band0 == null) return;
        PlaceBand(band0, new Vector3(-0.65f, 1.95f, LAUNCH.z - 0.1f), pouch.position);
        PlaceBand(band1, new Vector3( 0.65f, 1.95f, LAUNCH.z - 0.1f), pouch.position);
    }

    void PlaceBand(Transform b, Vector3 a, Vector3 c)
    {
        Vector3 mid = (a + c) * 0.5f; float len = (c - a).magnitude;
        b.position = mid;
        b.rotation = Quaternion.LookRotation((c - a).normalized, Vector3.up);
        b.localScale = new Vector3(0.06f, 0.06f, Mathf.Max(0.01f, len));
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudScore   = MakeText(0.085f, Color.white, TextAnchor.UpperLeft);
        hudBest    = MakeText(0.052f, new Color(1f, 0.92f, 0.7f), TextAnchor.UpperRight);
        hudLevel   = MakeText(0.052f, new Color(0.8f, 1f, 0.85f), TextAnchor.UpperRight);
        hudShots   = MakeText(0.075f, new Color(1f, 0.85f, 0.4f), TextAnchor.LowerLeft);
        hudTargets = MakeText(0.06f, new Color(0.4f, 1f, 0.9f), TextAnchor.LowerRight);
        hudHint    = MakeText(0.052f, new Color(1f, 1f, 0.92f), TextAnchor.MiddleCenter);
        comboText  = MakeText(0.11f, new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleCenter);
        bannerText = MakeText(0.13f, Color.white, TextAnchor.MiddleCenter);
        dbg        = MakeText(0.04f, new Color(0.7f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; bannerText.text = "";
        AdjustHud();
        hudHint.text = "DRAG BACK & RELEASE\nto launch";
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        hudScale = Mathf.Clamp(halfW / 6.0f, 0.16f, 1.3f);
        float ix = halfW * 0.95f, iy = halfH * 0.93f;

        hudScore.transform.localPosition   = new Vector3(-ix, iy, HUD_Z);          hudScore.characterSize   = 0.085f * hudScale;
        hudBest.transform.localPosition     = new Vector3( ix, iy, HUD_Z);          hudBest.characterSize     = 0.052f * hudScale;
        hudLevel.transform.localPosition    = new Vector3( ix, iy - 0.32f * halfH, HUD_Z); hudLevel.characterSize = 0.052f * hudScale;
        hudShots.transform.localPosition    = new Vector3(-ix, -iy, HUD_Z);         hudShots.characterSize    = 0.075f * hudScale;
        hudTargets.transform.localPosition  = new Vector3( ix, -iy, HUD_Z);         hudTargets.characterSize  = 0.06f  * hudScale;
        hudHint.transform.localPosition     = new Vector3(0f, iy * 0.55f, HUD_Z);   hudHint.characterSize     = 0.052f * hudScale;
        dbg.transform.localPosition         = new Vector3(-ix, -iy * 0.45f, HUD_Z); dbg.characterSize         = 0.04f  * hudScale;
        comboText.transform.localPosition   = new Vector3(0f, halfH * 0.5f, HUD_Z);
        if (comboFlash <= 0f) comboText.characterSize = 0.11f * hudScale;
    }

    void RefreshHud()
    {
        if (hudScore)   hudScore.text   = "SCORE  " + score;
        if (hudBest)    hudBest.text    = "BEST  " + best;
        if (hudLevel)   hudLevel.text   = "LEVEL  " + level;
        if (hudShots)   hudShots.text   = "BALLS  " + Mathf.Max(0, shots);
        if (hudTargets) hudTargets.text = "TARGETS  " + targetsRemaining;
    }

    void SetHudVisible(bool v)
    {
        hudScore.gameObject.SetActive(v);
        hudBest.gameObject.SetActive(v);
        hudLevel.gameObject.SetActive(v);
        hudShots.gameObject.SetActive(v);
        hudTargets.gameObject.SetActive(v);
    }

    void BuildDots()
    {
        for (int i = 0; i < DOT_COUNT; i++)
        {
            var d = Prim(PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.16f, dotMat, false);
            d.name = "dot"; d.SetActive(false);
            dots.Add(d.transform);
        }
    }

    // ===================================================================== level
    void BuildLevel()
    {
        ClearBlocks();
        shots = 3 + Mathf.Min(level, 3);              // 4..6 balls
        int H = 4 + Mathf.Min(level, 3);              // tower height 5..7
        int wantTargets = Mathf.Clamp(1 + level, 2, 5);

        const float SZ = 0.98f; float step = 1.0f;
        float[] xs = { -0.51f, 0.51f };
        float[] zs = { TOWER_Z - 0.51f, TOWER_Z + 0.51f };

        // an iconic 2x2 square tower (stable, easy to hit, topples dramatically)
        var cells = new List<Vector3>();
        var layerOf = new List<int>();
        for (int L = 0; L < H; L++)
        {
            float y = SZ * 0.5f + L * step;
            foreach (var x in xs) foreach (var z in zs) { cells.Add(new Vector3(x, y, z)); layerOf.Add(L); }
        }
        towerTopY = SZ * 0.5f + (H - 1) * step + 0.5f;

        // crystals: spread across the upper half; always one on top
        var targetIdx = new HashSet<int>();
        int topStart = (H - 1) * 4;
        targetIdx.Add(topStart + Random.Range(0, 4));   // one on the very top
        int guard = 0;
        while (targetIdx.Count < wantTargets && guard++ < 400)
        {
            int idx = Random.Range(0, cells.Count);
            if (layerOf[idx] < H / 2) continue;          // upper half only
            targetIdx.Add(idx);
        }

        targetsRemaining = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            bool isTarget = targetIdx.Contains(i);
            bool isStone = !isTarget && layerOf[i] == 0;     // heavy stone base
            MakeBlock(cells[i], isTarget, isStone);
        }
        RefreshHud();
        if (!started) hudHint.text = "DRAG BACK & RELEASE\nto launch";
    }

    void MakeBlock(Vector3 pos, bool isTarget, bool isStone)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);   // keeps its BoxCollider (WebGL-safe)
        g.name = isTarget ? "target" : (isStone ? "stone" : "crate");
        float sc = isTarget ? 0.86f : 0.98f;
        g.transform.position = pos;
        g.transform.localScale = Vector3.one * sc;
        Material m = isTarget ? (Random.value < 0.5f ? targMat : targMat2) : (isStone ? stoneMat : (Random.value < 0.5f ? crateMat : crateMat2));
        g.GetComponent<Renderer>().sharedMaterial = m;

        var rb = g.AddComponent<Rigidbody>();
        rb.mass = isTarget ? 0.6f : (isStone ? 3.0f : 1.0f);
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.maxDepenetrationVelocity = 3f;
        rb.sleepThreshold = 0.05f;

        var pm = new PhysicsMaterial { dynamicFriction = 0.6f, staticFriction = 0.7f, bounciness = 0.03f,
            frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Minimum };
        g.GetComponent<Collider>().material = pm;

        var b = g.AddComponent<Block>();
        b.Init(this, isTarget, pos);
        blocks.Add(b);
        rb.Sleep();                                                 // rest until struck

        if (isTarget) targetsRemaining++;
    }

    void ClearBlocks()
    {
        foreach (var b in blocks) if (b) Destroy(b.gameObject);
        blocks.Clear();
    }

    // ===================================================================== input + aiming
    void Update()
    {
        float dt = Time.deltaTime;
        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        bool down = Input.GetMouseButtonDown(0);
        bool held = Input.GetMouseButton(0);
        bool up = Input.GetMouseButtonUp(0);
        Vector2 ptr = Input.mousePosition;
        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i);
            if (t.phase == TouchPhase.Began) { down = true; ptr = t.position; }
            if (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary) { held = true; ptr = t.position; }
            if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) { up = true; ptr = t.position; }
        }

        if (down) { attract = false; started = true; if (hudHint) hudHint.text = ""; }

        switch (state)
        {
            case State.Idle:    HandleAimInput(down, held, up, ptr); break;
            case State.Flying:  break;
            case State.Cleared:
                clearTimer -= dt;
                if (clearTimer <= 0f) { level++; score += 0; BuildLevel(); ReturnToIdle(); bannerText.text = ""; }
                break;
            case State.GameOver:
                if (down) RestartGame();
                break;
        }

        // attract autopilot until first input (drives the screenshot demo)
        if (attract && state == State.Idle && activeProj == null)
        {
            attractTimer -= dt;
            if (attractTimer <= 0f) { AttractShoot(); attractTimer = 2.4f; }
        }

        UpdateProjectile(dt);
        CullBlocks();
        UpdateCamera(dt);
        UpdatePouch(dt);
        TickHud(dt);
        if (combo > 0) { comboTimer -= dt; if (comboTimer <= 0f) EndCombo(); }
        if (showDbg) UpdateDbg();
    }

    void HandleAimInput(bool down, bool held, bool up, Vector2 ptr)
    {
        if (down) { aiming = true; dragStart = ptr; }
        if (aiming && (held || up))
        {
            Vector2 d = ptr - dragStart;
            float back = Mathf.Clamp(-d.y, 0f, MAXDRAG);
            float side = Mathf.Clamp(d.x, -MAXDRAG, MAXDRAG);
            float power01 = back / MAXDRAG;
            float yawDeg = (side / MAXDRAG) * MAXYAW;
            lastSpeed01 = power01; lastYaw = yawDeg;
            lastVel = LaunchVel(power01, yawDeg);

            // pull the pouch back/down toward the drag for slingshot feel
            Vector3 pull = new Vector3(side / MAXDRAG * 0.9f, -power01 * 0.9f, -power01 * 1.1f);
            pouch.position = pouchRest + pull;
            UpdateBands();

            if (power01 > 0.06f) ShowPreview(lastVel); else HidePreview();

            if (up)
            {
                aiming = false;
                if (power01 > 0.08f) Launch(lastVel);
                else { pouch.position = pouchRest; UpdateBands(); HidePreview(); }
            }
        }
    }

    Vector3 LaunchVel(float power01, float yawDeg)
    {
        float speed = Mathf.Lerp(MINSPEED, MAXSPEED, power01);
        float pitch = Mathf.Lerp(34f, 19f, power01);                 // weak = lob, strong = flatter drive
        Vector3 dir = Quaternion.Euler(-pitch, yawDeg, 0f) * Vector3.forward;
        return dir * speed;
    }

    void ShowPreview(Vector3 vel)
    {
        Vector3 p = pouchRest; Vector3 v = vel; float h = 0.05f;
        for (int i = 0; i < dots.Count; i++)
        {
            for (int s = 0; s < 4; s++) { p += v * h; v += Physics.gravity * h; }   // ~0.2s per dot
            var d = dots[i];
            if (p.y < -1f) { d.gameObject.SetActive(false); continue; }
            d.gameObject.SetActive(true);
            d.position = p;
            float f = i / (float)dots.Count;
            d.localScale = Vector3.one * Mathf.Lerp(0.30f, 0.14f, f);
            d.GetComponent<Renderer>().sharedMaterial = f > 0.6f ? dotMatFar : dotMat;
        }
    }

    void HidePreview() { foreach (var d in dots) d.gameObject.SetActive(false); }

    void Launch(Vector3 vel)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);   // keeps its SphereCollider
        g.name = "Ball";
        g.transform.position = pouchRest;
        g.transform.localScale = Vector3.one * 0.7f;
        g.GetComponent<Renderer>().sharedMaterial = ballMat;
        var core = Prim(PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.55f, ballCore, false);
        core.transform.SetParent(g.transform, false);

        var rb = g.AddComponent<Rigidbody>();
        rb.mass = 7f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.linearVelocity = vel;

        activeProj = g.AddComponent<Projectile>();
        activeProj.Init(this);

        if (!attract) shots--;
        state = State.Flying;
        HidePreview();
        pouch.gameObject.SetActive(false);                          // pouch "becomes" the ball
        Juice.Blip(360f + lastSpeed01 * 200f, 0.09f, 0.45f);
        Juice.Shake(0.12f);
        RefreshHud();
    }

    void AttractShoot()
    {
        // search for a power whose simulated arc actually enters the tower (reliable demo hits)
        float power = 0.5f; float yaw = Random.Range(-7f, 7f);
        for (float p = 0.28f; p <= 0.85f; p += 0.04f)
            if (ArcHitsTower(LaunchVel(p, yaw))) { power = p; break; }
        lastSpeed01 = power;
        Launch(LaunchVel(power, yaw));
    }

    bool ArcHitsTower(Vector3 v)
    {
        Vector3 p = pouchRest; float h = 0.02f;
        for (int i = 0; i < 350; i++)
        {
            p += v * h; v += Physics.gravity * h;
            if (p.z > TOWER_Z - 1.2f && p.z < TOWER_Z + 1.2f && p.y > 0.25f && p.y < towerTopY && Mathf.Abs(p.x) < 1.4f)
                return true;
            if (p.y < -1f) break;
        }
        return false;
    }

    // ===================================================================== projectile lifecycle
    void UpdateProjectile(float dt)
    {
        if (activeProj == null) return;
        if (activeProj.ShouldEnd())
        {
            Destroy(activeProj.gameObject);
            activeProj = null;
            OnShotResolved();
        }
    }

    void OnShotResolved()
    {
        if (state != State.Flying) return;       // already cleared/over
        pouch.gameObject.SetActive(true);
        pouch.position = pouchRest; UpdateBands();
        if (targetsRemaining <= 0) { LevelClear(); return; }
        if (shots <= 0) { GameOver(); return; }
        state = State.Idle;
    }

    void ReturnToIdle() { state = State.Idle; pouch.gameObject.SetActive(true); pouch.position = pouchRest; UpdateBands(); }

    // called by Block when a projectile smacks it / it topples / it falls off
    public void OnBlockHit(Block b, float impact, Vector3 pos)
    {
        if (impact > 4f) { Juice.Shake(Mathf.Min(0.5f, impact * 0.02f)); Juice.Blip(180f, 0.05f, 0.25f); }
    }

    public void OnBlockKnocked(Block b)
    {
        dbgKnocks++;
        score += 20; RefreshHud();
        Juice.Pop(b.transform.position, new Color(0.9f, 0.75f, 0.45f), 5);
    }

    public void PopTarget(Block b)
    {
        if (b.dead) return;
        b.dead = true;
        dbgPops++;
        targetsRemaining = Mathf.Max(0, targetsRemaining - 1);
        BumpCombo();
        int gain = 300 * combo;
        score += gain;
        Vector3 wp = b.transform.position;
        Juice.Score(wp);
        Juice.Pop(wp, new Color(0.3f, 1f, 0.85f), 16);
        Juice.Pop(wp, new Color(1f, 0.9f, 0.4f), 10);
        Juice.Shake(0.25f);
        Juice.Blip(700f + Mathf.Min(combo, 12) * 60f, 0.08f, 0.4f);
        FloatText((combo >= 2 ? "×" + combo + "  " : "") + "+" + gain, new Color(0.4f, 1f, 0.85f));
        blocks.Remove(b);
        Destroy(b.gameObject);
        RefreshHud();
        if (targetsRemaining <= 0 && (state == State.Flying || state == State.Idle)) LevelClear();
    }

    void BumpCombo()
    {
        combo++; comboTimer = 2.0f; comboFlash = 1f;
        if (combo >= 2) { comboText.text = "COMBO ×" + combo; FlashCombo(); }
        if (combo > PlayerPrefs.GetInt("slingsmash_bestcombo", 0)) PlayerPrefs.SetInt("slingsmash_bestcombo", combo);
    }
    void EndCombo() { combo = 0; if (comboText) comboText.text = ""; }

    void FlashCombo()
    {
        comboText.color = combo >= 6 ? new Color(1f, 0.4f, 0.8f)
                        : combo >= 3 ? new Color(1f, 0.8f, 0.3f)
                                     : new Color(0.4f, 1f, 0.85f);
    }

    void LevelClear()
    {
        state = State.Cleared;
        int bonus = 500 + Mathf.Max(0, shots) * 200;
        score += bonus;
        if (score > best) { best = score; PlayerPrefs.SetInt("slingsmash_best", best); PlayerPrefs.Save(); }
        Juice.Score(new Vector3(0, 3f, TOWER_Z));
        Juice.Shake(0.4f);
        bannerText.transform.localPosition = new Vector3(0, 0.2f * halfH, HUD_Z);
        bannerText.characterSize = 0.1f * hudScale; bannerText.color = new Color(0.5f, 1f, 0.7f);
        bannerText.text = "LEVEL CLEAR!\n+" + bonus + " BONUS";
        RefreshHud();
        clearTimer = 2.0f;
    }

    void GameOver()
    {
        state = State.GameOver;
        bool nb = score >= best;
        if (score > best) { best = score; PlayerPrefs.SetInt("slingsmash_best", best); PlayerPrefs.Save(); }
        Juice.Lose();
        hudHint.gameObject.SetActive(false);
        comboText.text = "";
        SetHudVisible(false);
        bannerText.transform.localPosition = new Vector3(0, 0, HUD_Z);
        bannerText.characterSize = 0.085f * hudScale; bannerText.color = Color.white;
        bannerText.text = "OUT OF BALLS\n\nSCORE  " + score + (nb ? "\nNEW BEST!" : "\nBEST  " + best)
                        + "\nREACHED LEVEL " + level + "\n\nTAP TO PLAY AGAIN";
    }

    void RestartGame()
    {
        bannerText.text = ""; comboText.text = ""; combo = 0;
        hudHint.gameObject.SetActive(true); hudHint.text = "";
        SetHudVisible(true);
        level = 1; score = 0;
        BuildLevel(); ReturnToIdle();
    }

    void CullBlocks()
    {
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            var b = blocks[i];
            if (b == null) { blocks.RemoveAt(i); continue; }
            if (b.transform.position.y < KILL_Y)
            {
                if (b.isTarget && !b.dead) { PopTarget(b); }    // knocked off the edge = pop
                else { blocks.RemoveAt(i); Destroy(b.gameObject); }
            }
        }
    }

    // ===================================================================== camera / pouch / hud
    void UpdateCamera(float dt)
    {
        if (cam == null) return;
        Vector3 look = new Vector3(0f, 2.6f, TOWER_Z);
        Vector3 wantPos = new Vector3(0f, 6.2f, -11.5f);
        if (state == State.Flying && activeProj != null)
        {
            Vector3 bp = activeProj.transform.position;
            look = Vector3.Lerp(look, new Vector3(bp.x * 0.5f, Mathf.Max(1.5f, bp.y), Mathf.Clamp(bp.z, -2f, TOWER_Z + 4f)), 0.6f);
            wantPos.x = Mathf.Clamp(bp.x * 0.25f, -3f, 3f);
        }
        cam.position = Vector3.Lerp(cam.position, wantPos, 1f - Mathf.Exp(-4f * dt));
        Quaternion q = Quaternion.LookRotation(look - cam.position, Vector3.up);
        cam.rotation = Quaternion.Slerp(cam.rotation, q, 1f - Mathf.Exp(-6f * dt));
        AdjustHud();
    }

    void UpdatePouch(float dt)
    {
        if (pouch == null || !pouch.gameObject.activeSelf) return;
        if (!aiming && state != State.Flying)
        {
            pouch.position = Vector3.Lerp(pouch.position, pouchRest, 1f - Mathf.Exp(-14f * dt));
            UpdateBands();
        }
    }

    void TickHud(float dt)
    {
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.2f;
            if (comboText) comboText.characterSize = 0.11f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.6f);
        }
        if (bannerTimer > 0f) { bannerTimer -= dt; if (bannerTimer <= 0f && state != State.GameOver && state != State.Cleared) bannerText.text = ""; }
        if (!started && hudHint)
        {
            float a = 0.55f + 0.45f * Mathf.Sin(Time.time * 4f);
            hudHint.color = new Color(1f, 1f, 0.92f, a);
        }
    }

    void FloatText(string s, Color c)
    {
        bannerText.transform.localPosition = new Vector3(0, -halfH * 0.35f, HUD_Z);
        bannerText.characterSize = 0.1f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = 0.8f;
    }

    void UpdateDbg()
    {
        dbg.text = string.Format(
            "state {0}  lvl {1}  shots {2}\nscore {3}  best {4}  combo {5}\ntargets {6}  blocks {7}\npops {8} knocks {9}  proj {10}\npwr {11:0.00} yaw {12:0.0}\nfps {13:0}  asp {14:0.00}",
            state, level, shots, score, best, combo, targetsRemaining, blocks.Count, dbgPops, dbgKnocks,
            activeProj != null ? 1 : 0, lastSpeed01, lastYaw,
            1f / Mathf.Max(0.0001f, Time.smoothDeltaTime), aspect);
    }
}

// ---------------------------------------------------------------------------- a physics block
public class Block : MonoBehaviour
{
    SlingSmash game; public bool isTarget; public bool dead;
    Vector3 startPos; bool knocked;

    public void Init(SlingSmash g, bool target, Vector3 sp) { game = g; isTarget = target; startPos = sp; }

    void OnCollisionEnter(Collision c)
    {
        if (dead) return;
        var pj = c.gameObject.GetComponent<Projectile>();
        if (pj != null)
        {
            float impact = c.relativeVelocity.magnitude;
            game.OnBlockHit(this, impact, transform.position);
            if (isTarget && impact > 7f) { game.PopTarget(this); return; }   // direct hard smash
        }
    }

    void Update()
    {
        if (dead) return;
        float moved = (transform.position - startPos).magnitude;
        if (!knocked && moved > 1.4f)
        {
            knocked = true;
            if (!isTarget) game.OnBlockKnocked(this);
        }
        // a crystal knocked clearly off its perch counts as destroyed (no stalls, even if it lands on the platform)
        if (isTarget && moved > 2.3f) game.PopTarget(this);
    }
}

// ---------------------------------------------------------------------------- the launched ball
public class Projectile : MonoBehaviour
{
    SlingSmash game; Rigidbody rb; float age; int contacts; float restTime;

    public void Init(SlingSmash g) { game = g; rb = GetComponent<Rigidbody>(); }

    void Update()
    {
        age += Time.deltaTime;
        if (rb != null && rb.linearVelocity.magnitude < 0.8f && age > 0.4f) restTime += Time.deltaTime;
        else restTime = 0f;
    }

    void OnCollisionEnter(Collision c)
    {
        contacts++;
        if (contacts == 1)
        {
            Juice.Shake(0.18f);
            Juice.Pop(c.GetContact(0).point, new Color(1f, 0.85f, 0.4f), 8);
            Juice.Blip(220f, 0.06f, 0.3f);
        }
    }

    public bool ShouldEnd()
    {
        if (transform.position.y < -3.2f) return true;     // fell off
        if (age > 5.5f) return true;                       // safety timeout
        if (restTime > 0.7f && contacts > 0) return true;  // came to rest after hitting
        if (age > 2.2f && contacts == 0) return true;      // missed everything entirely
        return false;
    }
}
