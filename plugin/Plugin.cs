using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace ShorelineHayField;

// A hay field in the open meadow by the smugglers' base on Shoreline (north-east corner, inside the fence).
// Everything is built at raid start: the far part standing dry grass (EFT's own grass6_d_02_autumn_late texture,
// loaded from the player's install), the near part mown stubble with round bales, some standing and some rolled on their side.
// No game files are shipped; the straw textures are generated here. Bales have movement and bullet colliders.
// Grass, stubble and bales skip anything that isn't open terrain (buildings, the plane wreck, pylons, rocks).
[BepInPlugin("amos.shorelinehayfield", "ShorelineHayField", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private ConfigEntry<bool> _enabled;
    private ConfigEntry<float> _x, _z, _yaw, _width, _length, _mown;
    private ConfigEntry<int> _roundBales, _seed;
    private ConfigEntry<float> _density;
    private ConfigEntry<KeyboardShortcut> _moveHere;

    private GameObject _root;
    private GameWorld _builtFor;
    private bool _dirty, _building;

    private void Awake()
    {
        Log = Logger;
        const string s = "Hay field";
        _enabled = Config.Bind(s, "Enabled", true, "Build the hay field on Shoreline.");
        _x = Config.Bind(s, "Center X", -575f, "World X of the field centre (smugglers' base meadow is about -575).");
        _z = Config.Bind(s, "Center Z", -115f, "World Z of the field centre.");
        _yaw = Config.Bind(s, "Rotation", 0f, new ConfigDescription("Field rotation in degrees.", new AcceptableValueRange<float>(-180f, 180f)));
        _width = Config.Bind(s, "Width", 120f, new ConfigDescription("Field size across, metres.", new AcceptableValueRange<float>(10f, 250f)));
        _length = Config.Bind(s, "Length", 90f, new ConfigDescription("Field size along, metres.", new AcceptableValueRange<float>(10f, 250f)));
        _mown = Config.Bind(s, "Mown share", 0f, new ConfigDescription("Share of the field that is mown stubble. 0 = standing hay everywhere with the bales in it.", new AcceptableValueRange<float>(0f, 1f)));
        _density = Config.Bind(s, "Grass density", 1f, new ConfigDescription("Standing hay clumps multiplier.", new AcceptableValueRange<float>(0.2f, 2f)));
        _roundBales = Config.Bind(s, "Round bales", 44, new ConfigDescription("Round bales in the field (on the mown part when Mown share is above 0).", new AcceptableValueRange<int>(0, 80)));
        _seed = Config.Bind(s, "Seed", 1337, "Layout seed. Keep it the same on every Fika client so the bales match.");
        _moveHere = Config.Bind(s, "Move field to me", new KeyboardShortcut(KeyCode.H, KeyCode.LeftControl, KeyCode.LeftShift),
            "Rebuilds the field centred ahead of where you stand, facing your view, and saves it.");
        Config.SettingChanged += (_, __) => _dirty = true;
    }

    private void Update()
    {
        var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
        if (gw == null || gw.MainPlayer == null || !string.Equals(gw.LocationId, "Shoreline", StringComparison.OrdinalIgnoreCase))
        {
            if (_root != null) { Destroy(_root); _root = null; }
            _builtFor = null;
            return;
        }
        if (_building) return;

        if (_moveHere.Value.IsDown())
        {
            var p = gw.MainPlayer.Transform.position;
            var f = gw.MainPlayer.Transform.forward; f.y = 0;
            var c = p + f.normalized * (_length.Value * 0.5f + 3f);   // ahead of the player so they see it rebuild
            _x.Value = Mathf.Round(c.x); _z.Value = Mathf.Round(c.z);
            float yaw = Mathf.Round(Quaternion.LookRotation(f.normalized).eulerAngles.y);
            _yaw.Value = yaw > 180f ? yaw - 360f : yaw;
            Config.Save();
            Log.LogInfo($"Field moved to ({_x.Value}, {_z.Value}) yaw {_yaw.Value}");
            _dirty = true;
        }

        if (!_enabled.Value)
        {
            if (_root != null) { Destroy(_root); _root = null; }
            _builtFor = gw;
            return;
        }
        if (_builtFor != gw || _dirty || _root == null)
        {
            _dirty = false;
            _builtFor = gw;
            StartCoroutine(BuildLater());
        }
    }

    private IEnumerator BuildLater()
    {
        _building = true;
        yield return new WaitForSeconds(1f);   // let the scene settle (terrain colliders, grass streaming)
        try
        {
            // immediate, so the old bales' colliders don't count as obstacles for the new layout
            if (_root != null) { DestroyImmediate(_root); _root = null; }
            var t = Time.realtimeSinceStartup;
            _root = new Builder(new Vector3(_x.Value, 0, _z.Value), _yaw.Value, _width.Value, _length.Value, _mown.Value,
                _density.Value, _roundBales.Value, _seed.Value).Build();
            Log.LogInfo($"Built in {(Time.realtimeSinceStartup - t) * 1000f:0} ms");
        }
        catch (Exception e) { Log.LogError("Build failed: " + e); }
        _building = false;
    }
}

internal sealed class Builder
{
    private const float Chunk = 24f;   // grass / stubble chunk size, for culling

    private readonly Vector3 _center; private readonly Quaternion _rot;
    private readonly float _w, _l, _mown, _density; private readonly int _bales, _seed;
    private readonly System.Random _rng;
    private readonly int _terrainLayer, _lowPoly, _highPoly, _probeMask, _worldMask;
    private Transform _root;

    public Builder(Vector3 center, float yaw, float w, float l, float mown, float density, int bales, int seed)
    {
        _center = center; _rot = Quaternion.Euler(0, yaw, 0); _w = w; _l = l; _mown = mown; _density = density;
        _bales = bales; _seed = seed;
        _rng = new System.Random(seed);
        _terrainLayer = LayerMask.NameToLayer("Terrain");
        _lowPoly = LayerMask.NameToLayer("LowPolyCollider");
        _highPoly = LayerMask.NameToLayer("HighPolyCollider");
        _worldMask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider");
        _probeMask = _worldMask | LayerMask.GetMask("Terrain");
    }

    private float R(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    // local (u across, v along) -> world on the ground
    private Vector3 World(float u, float v) => _center + _rot * new Vector3(u, 0, v);

    // ---- where things may go --------------------------------------------------------------------------------
    private float Noise(float u, float v, float f, float o) => Mathf.PerlinNoise(u * f + o + _seed * 0.013f, v * f + o * 1.7f);

    // > 0 inside the field; the outline wobbles a few metres so it doesn't read as a rectangle
    private float Inside(float u, float v)
    {
        float d = Mathf.Min(Mathf.Min(u + _w / 2, _w / 2 - u), Mathf.Min(v + _l / 2, _l / 2 - v));
        d -= Noise(u, v, 0.045f, 3.1f) * 9f + Noise(u, v, 0.18f, 9.7f) * 2.5f;
        return Mathf.Min(d, FenceRoom(u, v));
    }

    // ---- the meadow's fence: stay on the centre's side of it ---------------------------------------------------
    private const float FenceMargin = 2f;
    private readonly List<(Vector2 a, Vector2 b)> _fences = new List<(Vector2, Vector2)>();
    private float[,] _fenceRoom;   // per 1 m cell: metres to the nearest fence, or -1 if a fence lies between it and the centre

    private void CollectFences()
    {
        var c2 = new Vector2(_center.x, _center.z);
        float reach = Mathf.Sqrt(_w * _w + _l * _l) / 2 + 30f;
        foreach (var mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
        {
            if (mf == null || mf.sharedMesh == null || !mf.gameObject.scene.IsValid() || mf.transform.IsChildOf(_root)) continue;
            if (!IsFence(mf.transform)) continue;
            var lb = mf.sharedMesh.bounds;
            var t = mf.transform;
            var wc = t.TransformPoint(lb.center);
            if ((new Vector2(wc.x, wc.z) - c2).magnitude > reach) continue;
            // the piece's long horizontal axis, in world space
            var ax = t.TransformVector(new Vector3(lb.extents.x, 0, 0)); var az = t.TransformVector(new Vector3(0, 0, lb.extents.z));
            var half = new Vector2(ax.x, ax.z).sqrMagnitude >= new Vector2(az.x, az.z).sqrMagnitude ? ax : az;
            var h2 = new Vector2(half.x, half.z);
            float len = h2.magnitude * 2;
            if (len < 0.5f || len > 40f) continue;
            // skip overhead lines (power cables, wires strung high)
            var bottom = t.TransformPoint(lb.center - new Vector3(0, lb.extents.y, 0)).y;
            if (Physics.Raycast(new Vector3(wc.x, wc.y + 50f, wc.z), Vector3.down, out var hit, 150f, LayerMask.GetMask("Terrain"), QueryTriggerInteraction.Ignore)
                && bottom - hit.point.y > 3f) continue;
            // stretch a little so gaps between fence pieces don't leak
            var dir = h2.normalized * 1.5f;
            _fences.Add((new Vector2(wc.x, wc.z) - h2 - dir, new Vector2(wc.x, wc.z) + h2 + dir));
        }

        int nu = Mathf.CeilToInt(_w) + 1, nv = Mathf.CeilToInt(_l) + 1;
        _fenceRoom = new float[nu, nv];
        int outside = 0;
        for (int i = 0; i < nu; i++)
        for (int j = 0; j < nv; j++)
        {
            var w = World(-_w / 2 + i, -_l / 2 + j); var p = new Vector2(w.x, w.z);
            float room = 999f; bool blocked = false;
            foreach (var (a, b) in _fences)
            {
                if (Cross(c2, p, a, b)) { blocked = true; break; }
                room = Mathf.Min(room, DistToSeg(p, a, b));
            }
            _fenceRoom[i, j] = blocked ? -1f : room - FenceMargin;
            if (blocked) outside++;
        }
        Plugin.Log.LogInfo($"Fence: {_fences.Count} pieces around the field; {outside * 100 / (nu * nv)}% of the area is outside it");
    }

    private static bool IsFence(Transform t)
    {
        for (int k = 0; k < 3 && t != null; k++, t = t.parent)
        {
            var n = t.name.ToLowerInvariant();
            if (n.Contains("fence") || n.Contains("barbed")) return true;
        }
        return false;
    }

    private float FenceRoom(float u, float v)
    {
        if (_fenceRoom == null) return 999f;
        int i = Mathf.Clamp(Mathf.RoundToInt(u + _w / 2), 0, _fenceRoom.GetLength(0) - 1);
        int j = Mathf.Clamp(Mathf.RoundToInt(v + _l / 2), 0, _fenceRoom.GetLength(1) - 1);
        return _fenceRoom[i, j];
    }

    private static float Side(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

    private static bool Cross(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        => Side(p1, p2, q1) * Side(p1, p2, q2) < 0 && Side(q1, q2, p1) * Side(q1, q2, p2) < 0;

    private static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
        return (a + ab * t - p).magnitude;
    }

    // mown below this line (v), standing hay above; wavy
    private float Split(float u) => _mown < 0.05f ? -_l : -_l / 2 + _l * _mown + Mathf.Sin(u * 0.08f + _seed) * 4f + Mathf.Sin(u * 0.31f + 1f) * 1.2f;

    private struct Ground { public bool Ok; public float Y; public Vector3 N; }

    // terrain height, and whether open terrain is the first thing hit from above (no building, rock, wreck, pylon)
    private Ground Probe(Vector3 p)
    {
        var from = new Vector3(p.x, 600f, p.z);
        if (!Physics.Raycast(from, Vector3.down, out var hit, 1200f, _probeMask, QueryTriggerInteraction.Ignore))
            return new Ground { Ok = false };
        bool terrain = hit.collider is TerrainCollider || hit.collider.gameObject.layer == _terrainLayer;
        if (!terrain) return new Ground { Ok = false, Y = hit.point.y };
        if (hit.normal.y < 0.88f) return new Ground { Ok = false, Y = hit.point.y };   // steeper than ~28 deg
        return new Ground { Ok = true, Y = hit.point.y, N = hit.normal };
    }

    // room for a solid object of this radius on open terrain
    private bool Clear(Vector3 p, float radius, float height)
    {
        var c = p + Vector3.up * (height * 0.5f + 0.15f);
        return !Physics.CheckSphere(c, radius, _worldMask, QueryTriggerInteraction.Ignore)
            && !Physics.CheckSphere(c + Vector3.up * 0.4f, radius, _worldMask, QueryTriggerInteraction.Ignore);
    }

    public GameObject Build()
    {
        var go = new GameObject("ShorelineHayField");
        _root = go.transform;
        _root.position = new Vector3(_center.x, 0, _center.z);
        CollectFences();

        var mats = new Materials();
        if (!mats.Find(_center)) { Plugin.Log.LogWarning("No usable scene shaders found; field not built."); return go; }

        _tpl = FindHayTemplate(out _tplPivot, out _tplRadius);
        int bales = BuildRoundBales(mats.BaleSide, mats.BaleEnd);
        BuildStubble(mats.Stubble);
        int clumps = BuildStandingHay(mats.Hay);
        Plugin.Log.LogInfo($"Hay field at {_center.x:0},{_center.z:0} ({_w}x{_l} m): {clumps} hay clumps, {bales} round bales");
        return go;
    }

    // ---- mown stubble: a ground skin following the terrain, cut to the outline and around obstacles ---------
    private void BuildStubble(Material mat)
    {
        const float step = 1f;
        int nu = Mathf.CeilToInt(_w / step) + 1, nv = Mathf.CeilToInt(_l / step) + 1;
        var g = new Ground[nu, nv];
        for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) g[i, j] = Probe(World(-_w / 2 + i * step, -_l / 2 + j * step));

        var chunks = new Dictionary<(int, int), MeshData>();
        for (int i = 0; i < nu - 1; i++)
        for (int j = 0; j < nv - 1; j++)
        {
            float u = -_w / 2 + (i + 0.5f) * step, v = -_l / 2 + (j + 0.5f) * step;
            if (Inside(u, v) < 0 || v > Split(u) + 1.5f + Noise(u, v, 0.4f, 5f) * 2f) continue;
            if (!g[i, j].Ok || !g[i + 1, j].Ok || !g[i, j + 1].Ok || !g[i + 1, j + 1].Ok) continue;
            var md = Get(chunks, u, v);
            int b = md.V.Count;
            foreach (var (di, dj) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                float uu = -_w / 2 + (i + di) * step, vv = -_l / 2 + (j + dj) * step;
                var p = World(uu, vv); p.y = g[i + di, j + dj].Y + 0.03f;
                md.V.Add(p - _root.position); md.N.Add(g[i + di, j + dj].N);
                // world-space UVs so neighbouring quads and chunks line up
                md.UV.Add(new Vector2(p.x / 3f, p.z / 3f));
            }
            md.T.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        foreach (var kv in chunks) AddChunk("Stubble", kv.Key, kv.Value, mat, false);
    }

    // ---- standing hay: crossed quads with EFT's dry grass texture, merged per chunk -------------------------
    private int BuildStandingHay(Material mat)
    {
        float spacing = 0.5f / Mathf.Sqrt(_density);
        var chunks = new Dictionary<(int, int), MeshData>();
        int count = 0;
        for (float u = -_w / 2; u < _w / 2; u += spacing)
        for (float v = -_l / 2; v < _l / 2; v += spacing)
        {
            float ju = u + R(-0.45f, 0.45f) * spacing, jv = v + R(-0.45f, 0.45f) * spacing;
            float split = Split(ju) + R(-0.8f, 0.8f);
            if (jv < split) continue;
            float inside = Inside(ju, jv);
            if (inside < 0) continue;
            // thinner and shorter towards the outline and the mown edge, so it blends into the meadow
            float edge = Mathf.Min(inside, (jv - split) * 1.5f);
            if (edge < 2.5f && R(0, 1) > edge / 2.5f) continue;
            // a few natural gaps
            if (Noise(ju, jv, 0.12f, 21f) < 0.22f && R(0, 1) < 0.7f) continue;
            var p = World(ju, jv);
            // clear under each bale and thin a ring around it, so the bales sit in the hay without being buried
            float room = BaleRoom(p);
            if (room <= 0f || R(0, 1) > 0.25f + 0.75f * room) continue;
            var gr = Probe(p); if (!gr.Ok) continue;
            p.y = gr.Y - 0.05f;
            float h = R(0.8f, 1.3f) * (0.75f + 0.25f * Mathf.Clamp01(edge / 2.5f)) * (0.85f + Noise(ju, jv, 0.07f, 40f) * 0.3f);
            float wdt = h * R(0.85f, 1.05f), yaw0 = R(0, 180f);
            var md = Get(chunks, ju, jv);
            var b = p - _root.position;
            for (int k = 0; k < 3; k++)
            {
                var dir = Quaternion.Euler(0, yaw0 + k * 60f, 0) * Vector3.right * (wdt / 2);
                var lean = new Vector3(R(-0.12f, 0.12f), 0, R(-0.12f, 0.12f)) * h;
                int s = md.V.Count;
                md.V.Add(b - dir); md.V.Add(b + dir); md.V.Add(b + dir + Vector3.up * h + lean); md.V.Add(b - dir + Vector3.up * h + lean);
                md.UV.Add(new Vector2(0, 0)); md.UV.Add(new Vector2(1, 0)); md.UV.Add(new Vector2(1, 1)); md.UV.Add(new Vector2(0, 1));
                for (int q = 0; q < 4; q++) md.N.Add(Vector3.up);
                md.T.AddRange(new[] { s, s + 3, s + 1, s + 1, s + 3, s + 2 });
            }
            count++;
        }
        foreach (var kv in chunks) AddChunk("Hay", kv.Key, kv.Value, mat, true);
        return count;
    }

    // ---- round bales lying on the stubble --------------------------------------------------------------------
    // Shoreline already has a hay prop ("Hay_new", three of them by the smugglers' base). Clone it when we can.
    private GameObject FindHayTemplate(out float pivotAboveGround, out float radius)
    {
        pivotAboveGround = 0; radius = 1.1f;
        // include inactive ones: EFT's culling may have switched the camp's props off while we're out in the field
        var t = Resources.FindObjectsOfTypeAll<LODGroup>()
            .Where(l => l != null && l.gameObject.scene.IsValid() && l.gameObject.name.StartsWith("Hay_new", StringComparison.Ordinal) && !l.transform.IsChildOf(_root))
            .Select(l => l.gameObject).FirstOrDefault();
        if (t == null) { Plugin.Log.LogInfo("Hay_new prop not found; using generated bales."); return null; }
        var rends = t.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0 || rends.Any(r => r.isPartOfStaticBatch))
        {
            Plugin.Log.LogInfo($"Hay_new is {(rends.Length == 0 ? "empty" : "statically batched")}; using generated bales.");
            return null;
        }
        var b = rends[0].bounds; foreach (var r in rends) b.Encapsulate(r.bounds);
        radius = Mathf.Max(b.extents.x, b.extents.z);
        var tp = t.transform.position;
        pivotAboveGround = Physics.Raycast(new Vector3(tp.x, tp.y + 50f, tp.z), Vector3.down, out var th, 200f, LayerMask.GetMask("Terrain"), QueryTriggerInteraction.Ignore)
            ? tp.y - th.point.y : tp.y - b.min.y;
        var cols = t.GetComponentsInChildren<Collider>(true);
        Plugin.Log.LogInfo($"Cloning {t.name}: size {b.size}, pivot {pivotAboveGround:0.00} m above ground, scale {t.transform.lossyScale.x:0.00}, " +
            $"{rends.Length} renderers, colliders on layers {string.Join(",", cols.Select(c => LayerMask.LayerToName(c.gameObject.layer)).Distinct())}");
        return t;
    }

    private GameObject _tpl; private float _tplPivot, _tplRadius;
    private Vector3 _tplAxis = Vector3.right;   // the bale's roll axis, in the template root's local space
    private readonly List<Vector2> _balePts = new List<Vector2>();   // world XZ of every bale, so the hay can make room

    // 0 = right at a bale (no hay), 1 = clear of every bale
    private float BaleRoom(Vector3 w)
    {
        float r = (_tpl != null ? _tplRadius : 0.8f) + 0.3f;
        var p = new Vector2(w.x, w.z); float best = 1f;
        foreach (var b in _balePts)
        {
            float d = (b - p).magnitude;
            if (d < r + 1.6f) best = Mathf.Min(best, Mathf.Clamp01((d - r) / 1.6f));
        }
        return best;
    }

    // The axis of a cylinder mesh is the bounds dimension that differs from the other two (those are both the diameter).
    private static Vector3 RollAxis(GameObject tpl)
    {
        var mf = tpl.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => f.sharedMesh != null && !f.name.ToLowerInvariant().Contains("decal"))
            .OrderByDescending(f => f.name.Contains("LOD0") ? 1 : 0).FirstOrDefault();
        if (mf == null) return Vector3.right;
        var e = mf.sharedMesh.bounds.extents;
        float dx = Mathf.Abs(e.x - (e.y + e.z) / 2), dy = Mathf.Abs(e.y - (e.x + e.z) / 2), dz = Mathf.Abs(e.z - (e.x + e.y) / 2);
        var local = dx >= dy && dx >= dz ? Vector3.right : dy >= dz ? Vector3.up : Vector3.forward;
        var world = mf.transform.rotation * local;
        return Quaternion.Inverse(tpl.transform.rotation) * world;
    }

    // Best-candidate sampling: each bale tries many spots and takes the one farthest from the bales already placed,
    // so they spread evenly over the mown part instead of clumping.
    private List<(Vector3 p, Ground g)> SpreadSpots(int count, float minSpacing, float clearR)
    {
        var res = new List<(Vector3, Ground)>(); var pts = new List<Vector2>();
        for (int n = 0; n < count; n++)
        {
            Vector2 best = default; Vector3 bestP = default; Ground bestG = default; float bestD = -1f;
            for (int k = 0; k < 40; k++)
            {
                // with no mown strip the bales go anywhere in the field, among the standing hay
                float u = R(-_w / 2, _w / 2), v = R(-_l / 2, _mown < 0.05f ? _l / 2 : Split(u) - 2f);
                if (Inside(u, v) < 3f) continue;
                var uv = new Vector2(u, v);
                float d = pts.Count == 0 ? 1000f : pts.Min(q => (q - uv).magnitude);
                if (d < minSpacing || d <= bestD) continue;
                var p = World(u, v); var gr = Probe(p); if (!gr.Ok) continue;
                p.y = gr.Y;
                if (!Clear(p, clearR, 1.4f)) continue;
                best = uv; bestP = p; bestG = gr; bestD = d;
            }
            if (bestD < 0) continue;
            pts.Add(best); res.Add((bestP, bestG));
            _balePts.Add(new Vector2(bestP.x, bestP.z));
        }
        return res;
    }

    private int BuildRoundBales(Material side, Material end)
    {
        var template = _tpl;
        if (template != null) _tplAxis = RollAxis(template);
        bool tplUpright = template != null && Mathf.Abs((template.transform.rotation * _tplAxis).y) > 0.7f;
        var meshes = new[] { RoundBaleMesh(0.75f, 1.2f, 1), RoundBaleMesh(0.72f, 1.2f, 2), RoundBaleMesh(0.78f, 1.25f, 3) };
        float spacing = template != null ? Mathf.Max(5f, _tplRadius * 3f) : 5f;
        float clearR = template != null ? _tplRadius * 0.9f : 1.1f;
        int n = 0, lyingCount = 0;
        foreach (var (p, gr) in SpreadSpots(_bales, spacing, clearR))
        {
            bool lying = R(0, 1) < 0.4f;
            if (template != null)
            {
                PlaceClone(template, p, gr, lying, tplUpright, n++);
                if (lying) lyingCount++;
                continue;
            }
            var mesh = meshes[n % meshes.Length];
            var rot = Quaternion.FromToRotation(Vector3.up, gr.N) * Quaternion.Euler(0, R(0, 360f), 0);
            var go = Child("RoundBale_" + n++, p + gr.N * 0.66f, rot);   // generated mesh lies on its side, flattened at -0.68
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { side, end };
            AddColliders(go, mesh, null);
        }
        if (template != null) Plugin.Log.LogInfo($"Bales: template is {(tplUpright ? "standing" : "lying")}, {lyingCount} of {n} placed on their side");
        return n;
    }

    private void PlaceClone(GameObject template, Vector3 p, Ground gr, bool lying, bool tplUpright, int n)
    {
        var baseRot = template.transform.rotation;
        if (lying == tplUpright)
        {
            // roll it 90 degrees about a horizontal axis across its own axis: standing <-> on its side
            var axis = baseRot * _tplAxis;
            var roll = Vector3.Cross(axis, Vector3.up);
            if (roll.sqrMagnitude < 0.01f) roll = Vector3.right;
            baseRot = Quaternion.AngleAxis(90f, roll.normalized) * baseRot;
        }
        var yaw = Quaternion.Euler(0, R(0, 360f), 0);
        var tilt = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, gr.N), 0.7f);
        var clone = UnityEngine.Object.Instantiate(template, p + Vector3.up * 3f, tilt * yaw * baseRot, _root);
        clone.name = (lying ? "HayBaleLying_" : "HayBale_") + n;
        clone.transform.localScale = template.transform.lossyScale * R(0.92f, 1.08f);
        if (clone.GetComponentInChildren<BallisticCollider>(true) == null)
        {
            // the original's collider has no EFT material; give bullets one
            foreach (var c in clone.GetComponentsInChildren<Collider>(true))
            {
                var bc = c.gameObject.AddComponent<BallisticCollider>();
                bc.TypeOfMaterial = MaterialType.GrassHigh; bc.PenetrationLevel = 12f; bc.PenetrationChance = 0.6f;
            }
        }
        foreach (var tr in clone.GetComponentsInChildren<Transform>(true)) tr.gameObject.SetActive(true);
        foreach (var lg in clone.GetComponentsInChildren<LODGroup>(true)) lg.enabled = true;
        var body = new List<Renderer>();
        foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
        {
            bool decal = r.name.ToLowerInvariant().Contains("decal");
            r.enabled = !(decal && lying);   // the ground decal would stand upright under a rolled bale
            if (!decal) body.Add(r);
        }
        // drop it onto the ground by its real bounds, whichever way it now faces
        var lod0 = body.Where(r => r.name.Contains("LOD0")).ToList();
        var use = lod0.Count > 0 ? lod0 : body;
        if (use.Count > 0)
        {
            var b = use[0].bounds; foreach (var r in use) b.Encapsulate(r.bounds);
            clone.transform.position += Vector3.up * (gr.Y - (lying ? 0.06f : 0.04f) - b.min.y);
        }
        else clone.transform.position = p + Vector3.up * (_tplPivot - 0.03f);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------
    private sealed class MeshData
    {
        public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
        public readonly List<Vector2> UV = new List<Vector2>(); public readonly List<int> T = new List<int>();
    }

    private static MeshData Get(Dictionary<(int, int), MeshData> d, float u, float v)
    {
        var k = (Mathf.FloorToInt(u / Chunk), Mathf.FloorToInt(v / Chunk));
        if (!d.TryGetValue(k, out var m)) d[k] = m = new MeshData();
        return m;
    }

    private GameObject Child(string name, Vector3 pos, Quaternion rot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        go.transform.SetPositionAndRotation(pos, rot);
        return go;
    }

    // one renderer per chunk so off-screen chunks are culled; a LODGroup hides distant grass chunks
    private void AddChunk(string name, (int, int) key, MeshData md, Material mat, bool grass)
    {
        if (md.T.Count == 0) return;
        var t = new List<int>(md.T);
        for (int i = 0; i < md.T.Count; i += 3) t.AddRange(new[] { md.T[i], md.T[i + 2], md.T[i + 1] });   // two-sided
        var m = new Mesh { name = $"{name}_{key.Item1}_{key.Item2}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(md.V); m.SetNormals(md.N); m.SetUVs(0, md.UV); m.SetTriangles(t, 0);
        m.RecalculateBounds(); m.RecalculateTangents();
        var go = Child(m.name, _root.position, Quaternion.identity);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (grass)
        {
            var lg = go.AddComponent<LODGroup>();
            lg.SetLODs(new[] { new LOD(0.07f, new Renderer[] { r }) });   // ~250 m for a 24 m chunk
            lg.RecalculateBounds();
        }
    }

    // movement collider on LowPolyCollider, bullet collider (with EFT material) on HighPolyCollider - like EFT's props
    private void AddColliders(GameObject go, Mesh convex, Vector3? box)
    {
        void Make(string name, int layer, bool ballistic)
        {
            var c = new GameObject(name);
            c.transform.SetParent(go.transform, false);
            if (layer >= 0) c.layer = layer;
            if (box.HasValue) c.AddComponent<BoxCollider>().size = box.Value;
            else { var mc = c.AddComponent<MeshCollider>(); mc.sharedMesh = convex; mc.convex = true; }
            if (ballistic)
            {
                var bc = c.AddComponent<BallisticCollider>();
                bc.TypeOfMaterial = MaterialType.GrassHigh;
                bc.PenetrationLevel = 12f; bc.PenetrationChance = 0.6f;
            }
        }
        Make("COLLIDER", _lowPoly, false);
        Make("BALLISTIC_hay", _highPoly, true);
    }

    // Round bale: a lathe around X with rounded shoulders, slightly lumpy, settled flat at the bottom.
    // Submesh 0 = wrapped side (and shoulders), submesh 1 = the two rolled ends.
    private static Mesh RoundBaleMesh(float r, float width, int variant)
    {
        const int seg = 40;
        float hw = width / 2, e = 0.13f;
        // profile: (x, radius, isEnd)
        var prof = new List<(float x, float rad, bool end)>();
        for (int i = 0; i <= 4; i++) prof.Add((-hw, (r - e) * i / 4f, true));
        for (int i = 1; i <= 5; i++) { float a = Mathf.PI - i / 5f * Mathf.PI / 2f; prof.Add((-hw + e + Mathf.Cos(a) * e, r - e + Mathf.Sin(a) * e, false)); }
        for (int i = 1; i <= 5; i++) prof.Add((-hw + e + (width - 2 * e) * i / 6f, r * (1f + 0.03f * Mathf.Sin(i / 6f * Mathf.PI)), false));
        for (int i = 0; i <= 5; i++) { float a = Mathf.PI / 2f - i / 5f * Mathf.PI / 2f; prof.Add((hw - e + Mathf.Cos(a) * e, r - e + Mathf.Sin(a) * e, false)); }
        for (int i = 3; i >= 0; i--) prof.Add((hw, (r - e) * i / 4f, true));

        var rnd = new System.Random(variant * 7919);
        float ph1 = (float)rnd.NextDouble() * 10f, ph2 = (float)rnd.NextDouble() * 10f;
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var isEnd = new List<bool>();
        int P = prof.Count;
        for (int s = 0; s <= seg; s++)
        {
            float a = s / (float)seg * Mathf.PI * 2f;
            for (int k = 0; k < P; k++)
            {
                var (x, rad, end) = prof[k];
                // lumps: low-frequency around the bale, a little along it
                float lump = 1f + 0.035f * Mathf.Sin(a * 3f + ph1 + x * 2f) + 0.02f * Mathf.Sin(a * 7f + ph2) + 0.015f * Mathf.PerlinNoise(a * 2f + ph1, x * 4f + ph2);
                float rr = rad * (rad > 0.01f ? lump : 1f);
                var p = new Vector3(x, Mathf.Sin(a) * rr, Mathf.Cos(a) * rr);
                if (p.y < -r * 0.9f) p.y = -r * 0.9f + (p.y + r * 0.9f) * 0.25f;   // settled under its own weight
                v.Add(p);
                isEnd.Add(end);
                uv.Add(end ? new Vector2(0.5f + Mathf.Cos(a) * rad / r * 0.5f, 0.5f + Mathf.Sin(a) * rad / r * 0.5f)
                           : new Vector2(s / (float)seg * 5f, (x + hw) / width));
            }
        }
        var sideT = new List<int>(); var endT = new List<int>();
        for (int s = 0; s < seg; s++)
        for (int k = 0; k < P - 1; k++)
        {
            int a0 = s * P + k, a1 = a0 + 1, b0 = a0 + P, b1 = b0 + 1;
            var list = isEnd[a0] && isEnd[a1] ? endT : sideT;
            list.AddRange(new[] { a0, b0, a1, a1, b0, b1 });
        }
        var m = new Mesh { name = "RoundBale_" + variant };
        m.SetVertices(v); m.SetUVs(0, uv);
        m.subMeshCount = 2; m.SetTriangles(sideT, 0); m.SetTriangles(endT, 1);
        OrientOutward(m, sideT, endT);
        return m;
    }

    // Point every triangle away from the mesh centre (both meshes are convex-ish), then compute normals and add
    // back faces so the bale reads right whatever winding the renderer culls.
    private static void OrientOutward(Mesh m, params List<int>[] subs)
    {
        var v = m.vertices; var c = m.bounds.center;
        foreach (var t in subs)
            for (int i = 0; i < t.Count; i += 3)
            {
                var a = v[t[i]]; var n = Vector3.Cross(v[t[i + 1]] - a, v[t[i + 2]] - a);
                if (Vector3.Dot(n, (a + v[t[i + 1]] + v[t[i + 2]]) / 3f - c) < 0) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            }
        for (int s = 0; s < subs.Length; s++) m.SetTriangles(subs[s], s);
        m.RecalculateNormals();   // from the outward faces only
        for (int s = 0; s < subs.Length; s++)
        {
            var t = new List<int>(subs[s]);
            for (int i = 0; i < subs[s].Count; i += 3) t.AddRange(new[] { subs[s][i], subs[s][i + 2], subs[s][i + 1] });
            m.SetTriangles(t, s);
        }
        m.RecalculateBounds(); m.RecalculateTangents();
    }
}

// Materials: shaders are borrowed from the loaded Shoreline scene so lighting matches EFT's pipeline.
internal sealed class Materials
{
    public Material Stubble, Hay, BaleSide, BaleEnd;

    private static readonly string[] OpaqueNames = { "p0/Reflective/Bumped Specular SMap", "p0/Reflective/Bumped Specular", "p0/Bumped Diffuse", "Legacy Shaders/Bumped Diffuse", "Legacy Shaders/Diffuse", "Standard" };
    private static readonly string[] CutoutNames = { "p0/Cutout/Bumped Diffuse", "p0/Cutout/Diffuse", "Legacy Shaders/Transparent/Cutout/Bumped Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse", "Unlit/Transparent Cutout" };

    public bool Find(Vector3 near)
    {
        Shader opaque = OpaqueNames.Select(Shader.Find).FirstOrDefault(s => s != null && s.isSupported);
        Shader cutout = CutoutNames.Select(Shader.Find).FirstOrDefault(s => s != null && s.isSupported);
        Material opaqueSrc = null, cutoutSrc = null;
        if (opaque == null || cutout == null)
        {
            var seen = new HashSet<string>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if ((r.bounds.center - near).sqrMagnitude > 400f * 400f) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || !m.HasProperty("_MainTex")) continue;
                    var sn = m.shader.name; seen.Add(sn);
                    var tag = m.GetTag("RenderType", false);
                    if (cutout == null && cutoutSrc == null && m.HasProperty("_Cutoff") && (tag == "TransparentCutout" || sn.Contains("Cutout")) && !sn.Contains("Tree") && !sn.Contains("Leaves"))
                        cutoutSrc = m;
                    if (opaque == null && opaqueSrc == null && (tag == "Opaque" || tag == "") && sn.Contains("Bumped") && !sn.Contains("Decal") && !sn.Contains("Transparent") && !sn.Contains("Cutout"))
                        opaqueSrc = m;
                }
            }
            Plugin.Log.LogInfo("Scene shaders near field: " + string.Join(", ", seen.Take(60)));
        }
        Plugin.Log.LogInfo($"Opaque shader: {(opaque != null ? opaque.name : opaqueSrc?.shader.name ?? "none")}; cutout: {(cutout != null ? cutout.name : cutoutSrc?.shader.name ?? "none")}");
        if (opaque == null && opaqueSrc == null) return false;
        if (cutout == null && cutoutSrc == null) { cutout = opaque ?? opaqueSrc.shader; }

        Material MakeOpaque(Texture2D tex)
        {
            var m = opaqueSrc != null ? new Material(opaqueSrc) : new Material(opaque);
            Prepare(m, tex, Color.white); return m;
        }
        Stubble = MakeOpaque(Tex.Stubble());
        BaleSide = MakeOpaque(Tex.StrawSide());
        BaleEnd = MakeOpaque(Tex.StrawEnd());

        Hay = cutoutSrc != null ? new Material(cutoutSrc) : new Material(cutout);
        Prepare(Hay, Tex.EftGrass() ?? Tex.FallbackGrass(), new Color(1.05f, 1f, 0.92f));
        if (Hay.HasProperty("_Cutoff")) Hay.SetFloat("_Cutoff", 0.4f);
        Hay.renderQueue = 2450;
        return true;
    }

    private static void Prepare(Material m, Texture2D tex, Color tint)
    {
        m.mainTexture = tex; m.mainTextureScale = Vector2.one; m.mainTextureOffset = Vector2.zero;
        if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
        foreach (var p in new[] { "_BumpMap", "_NormalMap" }) if (m.HasProperty(p)) m.SetTexture(p, Tex.FlatNormal());
        foreach (var p in new[] { "_SpecMap", "_SpecTex", "_MetallicGlossMap", "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask", "_EmissionMap", "_ParallaxMap", "_OcclusionMap" })
            if (m.HasProperty(p)) m.SetTexture(p, p == "_OcclusionMap" ? Texture2D.whiteTexture : Texture2D.blackTexture);
        if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.03f, 0.03f, 0.025f));
        foreach (var p in new[] { "_Glossness", "_Glossiness", "_Shininess", "_Metallic", "_EmissionStrength" })
            if (m.HasProperty(p)) { try { m.SetFloat(p, p == "_Shininess" ? 0.03f : 0f); } catch { } }
        if (m.HasProperty("_ReflectColor")) m.SetColor("_ReflectColor", Color.black);
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
        m.DisableKeyword("_EMISSION");
    }
}

// Generated straw textures (nothing shipped from the game) + EFT's own dry grass loaded from the player's install.
internal static class Tex
{
    private static Texture2D _flat;
    public static Texture2D FlatNormal()
    {
        if (_flat != null) return _flat;
        _flat = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "hay_flat_n" };
        var c = new Color(0.5f, 0.5f, 1f, 0.5f);   // flat for both RGB and DXT5nm (AG) decoding
        _flat.SetPixels(Enumerable.Repeat(c, 16).ToArray()); _flat.Apply(false, true);
        return _flat;
    }

    private static Texture2D Make(string name, int size, Func<float, float, Color> f)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
        var px = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) px[y * size + x] = f(x / (float)size, y / (float)size);
        t.SetPixels(px); t.Apply(true, true);
        return t;
    }

    // tileable Perlin (blend of four offset samples)
    private static float N(float x, float y, float fx, float fy, float seed)
    {
        float a = Mathf.PerlinNoise(x * fx + seed, y * fy + seed * 0.37f);
        float b = Mathf.PerlinNoise((x - 1f) * fx + seed, y * fy + seed * 0.37f);
        float c = Mathf.PerlinNoise(x * fx + seed, (y - 1f) * fy + seed * 0.37f);
        float d = Mathf.PerlinNoise((x - 1f) * fx + seed, (y - 1f) * fy + seed * 0.37f);
        return Mathf.Lerp(Mathf.Lerp(a, b, x), Mathf.Lerp(c, d, x), y);
    }

    // weathered hay: muted gold with grey-brown, not bright yellow
    private static readonly Color Hi = new Color(0.66f, 0.57f, 0.36f), Mid = new Color(0.52f, 0.43f, 0.26f),
        Lo = new Color(0.33f, 0.27f, 0.17f), Grey = new Color(0.46f, 0.43f, 0.36f);

    private static Color Straw(float fibre, float blotch, float weather)
    {
        var c = Color.Lerp(Mid, Hi, Mathf.Clamp01(fibre * 1.3f - 0.15f));
        c = Color.Lerp(c, Lo, Mathf.Clamp01((0.38f - fibre) * 2.5f));
        c = Color.Lerp(c, Grey, Mathf.Clamp01(weather) * 0.45f);
        c *= 0.9f + blotch * 0.2f; c.a = 1f; return c;
    }

    // stray strands: thin bright/dark streaks along one axis
    private static float Strands(float along, float across, float seed)
    {
        float s = Mathf.PerlinNoise(across * 300f + seed, along * 6f + seed);
        return Mathf.Clamp01((s - 0.62f) * 4f) - Mathf.Clamp01((0.3f - s) * 3f);
    }

    public static Texture2D StrawSide() => Make("hay_side", 512, (x, y) =>
    {
        // fibres run around the bale (along U)
        float fibre = N(x, y, 4f, 70f, 11f) * 0.45f + N(x, y, 16f, 180f, 23f) * 0.35f + N(x, y, 40f, 300f, 7f) * 0.2f;
        fibre += Strands(x, y, 3f) * 0.25f;
        float weather = N(x, y, 3f, 2f, 51f) * 1.2f - 0.4f;
        var c = Straw(fibre, N(x, y, 5f, 5f, 5f), weather);
        // net wrap: faint pale diagonal mesh
        float net = Mathf.Min(Mathf.Abs(Mathf.Repeat((x + y * 0.3f) * 60f, 1f) - 0.5f), Mathf.Abs(Mathf.Repeat((x - y * 0.3f) * 60f, 1f) - 0.5f));
        if (net < 0.04f) c = Color.Lerp(c, new Color(0.78f, 0.78f, 0.74f), 0.18f);
        return c;
    });

    public static Texture2D StrawEnd() => Make("hay_end", 512, (x, y) =>
    {
        float dx = x - 0.5f, dy = y - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy) * 2f, a = Mathf.Atan2(dy, dx);
        // tight, irregular roll: rings wobble and break up
        float wob = N(x, y, 6f, 6f, 13f) * 0.08f;
        float ring = Mathf.Sin((r + wob + a / (Mathf.PI * 2f) * 0.035f) * 180f) * 0.5f + 0.5f;
        float fibre = Mathf.Lerp(N(x, y, 50f, 50f, 3f), ring, 0.35f) + Strands(a, r, 9f) * 0.2f;
        var c = Straw(fibre * 0.9f, N(x, y, 5f, 5f, 9f), N(x, y, 3f, 3f, 77f) - 0.2f);
        return c * (0.92f - Mathf.Clamp01(0.6f - r) * 0.12f);   // denser, darker core
    });

    public static Texture2D Stubble() => Make("hay_stubble", 512, (x, y) =>
    {
        float soil = N(x, y, 5f, 5f, 2f);
        float stalk = N(x, y, 90f, 90f, 41f);
        float rows = N(x, y, 2f, 40f, 61f);   // soft mower lines, no hard stripes
        var straw = Straw(N(x, y, 25f, 70f, 13f), soil, 0.3f);
        var ground = Color.Lerp(new Color(0.24f, 0.22f, 0.14f), new Color(0.34f, 0.33f, 0.19f), soil);
        float cover = Mathf.Clamp01(0.35f + rows * 0.3f + (stalk - 0.5f) * 1.1f);
        var c = Color.Lerp(ground, straw * 0.9f, cover); c.a = 1f; return c;
    });

    public static Texture2D EftGrass()
    {
        const string rel = "Windows/assets/content/location_objects/02_custom/fbx/_outdoor/_nature/grass/grass6_d_02_autumn_late";
        try
        {
            var path = Path.Combine(Application.streamingAssetsPath, rel);
            var bundle = AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(b => b != null && b.name.EndsWith("grass6_d_02_autumn_late", StringComparison.OrdinalIgnoreCase));
            bool ours = false;
            if (bundle == null && File.Exists(path)) { bundle = AssetBundle.LoadFromFile(path); ours = bundle != null; }
            if (bundle == null) { Plugin.Log.LogWarning("EFT grass bundle not found: " + path); return null; }
            var tex = bundle.LoadAllAssets<Texture2D>().FirstOrDefault();
            if (ours) bundle.Unload(false);
            if (tex != null) { tex.wrapMode = TextureWrapMode.Clamp; Plugin.Log.LogInfo("Using EFT texture " + tex.name); }
            return tex;
        }
        catch (Exception e) { Plugin.Log.LogWarning("EFT grass load failed: " + e.Message); return null; }
    }

    public static Texture2D FallbackGrass()
    {
        var t = new Texture2D(256, 256, TextureFormat.RGBA32, true) { name = "hay_grass_fallback", wrapMode = TextureWrapMode.Clamp };
        var px = new Color[256 * 256];
        var rng = new System.Random(7);
        for (int s = 0; s < 70; s++)
        {
            float x0 = (float)rng.NextDouble() * 220 + 18, lean = ((float)rng.NextDouble() - 0.5f) * 60, h = 140 + (float)rng.NextDouble() * 110;
            var col = Straw((float)rng.NextDouble(), 0.6f, 0.2f);
            for (int y = 0; y < h && y < 256; y++)
            {
                float k = y / h; int x = Mathf.RoundToInt(x0 + lean * k * k);
                for (int w = -1; w <= 0; w++) if (x + w >= 0 && x + w < 256) px[y * 256 + x + w] = col;
            }
        }
        t.SetPixels(px); t.Apply(true, false);
        return t;
    }
}
