using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public enum Env { Land, Mountain, Bridge, Ferry, Local, MegaBridge }

/// <summary>
/// 3Dの道路と景色。道路は「距離 s（m）」と「横位置 u（m、負が左＝走行側）」の座標で考え、
/// 描画時にプレイヤー位置を原点とするワールド座標へ変換する（遠くへ走っても精度が落ちない）。
/// </summary>
public sealed class Scene
{
    public const float ViewAhead = 760;
    public const float ViewBehind = 60;
    private const double Step = 6;

    private static readonly HashSet<string> MountainRoads = new()
    {
        "中央道", "長野道", "上信越道", "関越道", "磐越道", "米子道", "高知道", "徳島道", "松山道",
        "山形道", "秋田道", "道東道", "大分道", "宮崎道", "東九州道", "北陸道", "長崎道",
    };

    private readonly List<(double S, Env Env)> segments = new();
    private double cxp;
    private Vector3 cam;
    private float light = 1;
    private Color fog;

    public double PlayerS { get; set; }
    /// <summary>霧の距離の倍率（空を飛ぶと遠くまで見える）。</summary>
    public float FogScale { get; set; } = 1;
    /// <summary>一般道（寄り道）の終点。ここから先は名所前の広場。</summary>
    public double LocalEndS { get; set; } = double.MaxValue;
    public float Urban { get; set; }
    public Color SkyTop { get; private set; }
    public Color Horizon { get; private set; }
    public bool Night => light < 0.6f;
    /// <summary>今フレームに描いた案内標識の中心（HUDで文字を重ねる）。</summary>
    public List<Vector3> Signs { get; } = new();

    public void Reset() => segments.Clear();

    public void BeginRoad(double s, Road road) => BeginEnv(s, EnvOf(road));

    public void BeginEnv(double s, Env env)
    {
        segments.Add((s, env));
        if (segments.Count > 4) segments.RemoveAt(0);
    }

    /// <summary>海面の高さ（道路面が0）。</summary>
    public static float SeaLevel(Env env) => env switch
    {
        Env.Ferry => -6,
        Env.Bridge => -35,
        Env.MegaBridge => -70,
        _ => 0,
    };

    public static bool IsSea(Env env) => env is Env.Ferry or Env.Bridge or Env.MegaBridge;

    public static Env EnvOf(Road road) =>
        road.Type == RoadType.MegaBridge ? Env.MegaBridge
        : road.Type == RoadType.Ferry ? Env.Ferry
        : road.Type == RoadType.Bridge ? Env.Bridge
        : MountainRoads.Contains(road.Name) ? Env.Mountain : Env.Land;

    // ---------------- 座標 ----------------

    public static double Cx(double s) => 60 * Math.Sin(s / 700 + 1.3) + 25 * Math.Sin(s / 260 + 2.6);
    public static double Slope(double s) => 60 / 700.0 * Math.Cos(s / 700 + 1.3) + 25 / 260.0 * Math.Cos(s / 260 + 2.6);

    /// <summary>道路座標 → ワールド座標（画面の右が -X なので x = Cx - u）。</summary>
    public Vector3 P(double s, float u, float y) => new((float)(Cx(s) - cxp) - u, y, (float)(s - PlayerS));

    public Env EnvAt(double s)
    {
        Env? env = null;
        foreach (var seg in segments)
            if (seg.S <= s || env == null) env = seg.Env;
        return env ?? Env.Land;
    }

    public static float Hash(long n)
    {
        ulong x = (ulong)n * 0x9E3779B97F4A7C15UL;
        x ^= x >> 31;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        return (x >> 40) / (float)(1UL << 24);
    }

    private const double TunnelChunk = 1200;

    private bool TunnelInChunk(long k, out double start, out double len)
    {
        start = k * TunnelChunk + 150 + Hash(k * 7 + 5) * 200;
        len = 250 + Hash(k * 7 + 9) * 550;
        return Hash(k * 7 + 3) < 0.45f && EnvAt(start) == Env.Mountain && EnvAt(start + len) == Env.Mountain;
    }

    public bool IsTunnel(double s)
    {
        long k = (long)Math.Floor(s / TunnelChunk);
        return TunnelInChunk(k, out double start, out double len) && s >= start && s < start + len;
    }

    // ---------------- 時間帯と色 ----------------

    public void SetTime(double gameMinutes)
    {
        double h = gameMinutes / 60 % 24;
        float day = h < 5 ? 0 : h < 7 ? (float)((h - 5) / 2) : h < 17 ? 1 : h < 19 ? (float)(1 - (h - 17) / 2) : 0;
        float dusk = Math.Clamp(Math.Max(1 - (float)Math.Abs(h - 6) / 1.3f, 1 - (float)Math.Abs(h - 18) / 1.3f), 0, 1);
        light = 0.25f + 0.75f * day;

        SkyTop = Lerp(Rgb(6, 10, 30), Rgb(55, 115, 205), day);
        SkyTop = Lerp(SkyTop, Rgb(90, 80, 140), dusk * 0.5f);
        Horizon = Lerp(Rgb(22, 26, 50), Rgb(196, 216, 236), day);
        Horizon = Lerp(Horizon, Rgb(238, 150, 100), dusk * 0.7f);
        fog = Horizon;
    }

    private Color Shade(Color c) => new((byte)(c.R * light), (byte)(c.G * light), (byte)(c.B * light), c.A);

    public Color FogC(Color c, float dist, bool lit = false)
    {
        float k = FogScale;
        float f = Math.Clamp((dist - 80 * k) / ((ViewAhead - 80) * k), 0, 1);
        f = f * f * (3 - 2 * f);
        return Lerp(lit ? c : Shade(c), fog, f);
    }

    private Color Haze(Color c, float dist)
    {
        float f = 0.35f + 0.5f * Math.Clamp(dist / 1000f, 0, 1);
        return Lerp(Shade(c), fog, f);
    }

    /// <summary>名所用：遠くても霞みすぎないフォグ。</summary>
    public Color LandmarkFog(Color c, Vector3 p)
    {
        float f = Math.Clamp((Vector3.Distance(p, cam) - 150 * FogScale) / (1200f * FogScale), 0, 0.7f);
        return Lerp(Shade(c), fog, f);
    }

    public Color FogAt(Color c, Vector3 p, bool lit = false) => FogC(c, Vector3.Distance(p, cam), lit);

    // ---------------- 頂点ヘルパー ----------------

    private void V(Vector3 p, Color c, bool lit)
    {
        var col = FogC(c, Vector3.Distance(p, cam), lit);
        Rlgl.Color4ub(col.R, col.G, col.B, 255);
        Rlgl.Vertex3f(p.X, p.Y, p.Z);
    }

    /// <summary>水平な帯（路面・地面）。</summary>
    private void Band(double s0, double s1, float u0, float u1, float y, Color c, bool lit = false)
    {
        V(P(s0, u0, y), c, lit);
        V(P(s0, u1, y), c, lit);
        V(P(s1, u1, y), c, lit);
        V(P(s1, u0, y), c, lit);
    }

    /// <summary>道路に沿った垂直の帯（ガードレール・壁）。</summary>
    private void Wall(double s0, double s1, float u, float y0, float y1, Color c)
    {
        V(P(s0, u, y0), c, false);
        V(P(s0, u, y1), c, false);
        V(P(s1, u, y1), c, false);
        V(P(s1, u, y0), c, false);
    }

    /// <summary>道路を横切る垂直面（トンネル坑口）。</summary>
    private void Face(double s, float u0, float u1, float y0, float y1, Color c)
    {
        V(P(s, u0, y0), c, false);
        V(P(s, u1, y0), c, false);
        V(P(s, u1, y1), c, false);
        V(P(s, u0, y1), c, false);
    }

    // ---------------- 描画 ----------------

    private static readonly Color Asphalt = Rgb(68, 70, 76);
    private static readonly Color ShoulderC = Rgb(92, 92, 98);
    private static readonly Color MedianC = Rgb(150, 150, 146);
    private static readonly Color White = Rgb(235, 235, 235);
    private static readonly Color Rail = Rgb(222, 222, 215);
    private static readonly Color SeaC = Rgb(40, 95, 150);

    public void Draw(Vector3 cameraPos)
    {
        cxp = Cx(PlayerS);
        cam = cameraPos;
        Signs.Clear();

        Rlgl.Begin(DrawMode.Quads);
        double start = Math.Floor((PlayerS - ViewBehind) / Step) * Step;
        for (double s0 = start; s0 < PlayerS + ViewAhead; s0 += Step)
            DrawSegment(s0, s0 + Step);
        Rlgl.End();

        DrawMountains();
        DrawTrees();
        DrawBuildings();
        DrawTunnelHills();
        DrawBridgeTowers();
        DrawMegaBridge();
        DrawGantries();
        DrawTown();
    }

    private void DrawSegment(double s0, double s1)
    {
        var env = EnvAt(s0);
        if (env == Env.Local)
        {
            DrawLocalSegment(s0, s1);
            return;
        }
        if (env == Env.Ferry)
        {
            Band(s0, s1, -500, 500, -6, SeaC);
            return;
        }

        bool mega = env == Env.MegaBridge;
        bool bridge = env == Env.Bridge || mega;
        bool tunnel = IsTunnel(s0);
        float groundY = mega ? -70 : bridge ? -35 : -0.05f;
        Color ground = bridge ? SeaC : env == Env.Mountain ? Rgb(66, 116, 58) : Rgb(96, 150, 74);

        Band(s0, s1, -500, -10.5f, groundY, ground);
        Band(s0, s1, 10.5f, 500, groundY, ground);
        if (!bridge)
        {
            Band(s0, s1, -10.5f, -9.6f, 0, Rgb(110, 140, 80));
            Band(s0, s1, 9.6f, 10.5f, 0, Rgb(110, 140, 80));
        }

        // 路面（左側通行：u<0 が自分の車線、u>0 が対向車線）
        Band(s0, s1, -9.6f, -8.0f, 0, ShoulderC);
        Band(s0, s1, -8.0f, -0.6f, 0, Asphalt);
        Band(s0, s1, 0.6f, 8.0f, 0, Asphalt);
        Band(s0, s1, 8.0f, 9.6f, 0, ShoulderC);
        Band(s0, s1, -0.6f, 0.6f, 0.01f, MedianC);

        // 白線
        Band(s0, s1, -8.0f, -7.85f, 0.02f, White);
        Band(s0, s1, -0.95f, -0.8f, 0.02f, White);
        Band(s0, s1, 7.85f, 8.0f, 0.02f, White);
        Band(s0, s1, 0.8f, 0.95f, 0.02f, White);
        if ((long)Math.Floor(s0 / Step) % 3 == 0)
        {
            Band(s0, s1, -4.48f, -4.32f, 0.02f, White);
            Band(s0, s1, 4.32f, 4.48f, 0.02f, White);
        }

        // ガードレール
        Wall(s0, s1, -0.3f, 0.45f, 0.8f, Rail);
        Wall(s0, s1, 0.3f, 0.45f, 0.8f, Rail);
        if (mega)
        {
            // 超巨大橋：深い鋼製トラスの橋桁
            var steel = Rgb(205, 95, 55);
            Wall(s0, s1, -10.6f, -16f, 0f, steel);
            Wall(s0, s1, 10.6f, -16f, 0f, steel);
            Band(s0, s1, -10.6f, 10.6f, -16f, Rgb(110, 100, 95));
            Band(s0, s1, -10.6f, 10.6f, -3f, Rgb(130, 120, 115));
            Wall(s0, s1, -9.8f, 0f, 1.3f, Rail);
            Wall(s0, s1, 9.8f, 0f, 1.3f, Rail);
        }
        else if (bridge)
        {
            Wall(s0, s1, -10.4f, -3f, 0f, Rgb(170, 60, 50));
            Wall(s0, s1, 10.4f, -3f, 0f, Rgb(170, 60, 50));
            Band(s0, s1, -10.4f, 10.4f, -3f, Rgb(120, 110, 105));
            Wall(s0, s1, -9.8f, 0f, 1.1f, Rail);
            Wall(s0, s1, 9.8f, 0f, 1.1f, Rail);
        }
        else
        {
            Wall(s0, s1, -9.8f, 0.45f, 0.8f, Rail);
            Wall(s0, s1, 9.8f, 0.45f, 0.8f, Rail);
        }

        if (tunnel)
        {
            var wall = Rgb(150, 146, 136);
            Wall(s0, s1, -10.5f, 0, 7.2f, wall);
            Wall(s0, s1, 10.5f, 0, 7.2f, wall);
            Band(s0, s1, -10.5f, 10.5f, 7.2f, Rgb(58, 58, 60));
            // オレンジ色のトンネル照明
            if ((long)Math.Floor(s0 / Step) % 2 == 0)
            {
                Band(s0, s1 - 2, -6.0f, -5.4f, 7.15f, Rgb(255, 180, 80), lit: true);
                Band(s0, s1 - 2, 5.4f, 6.0f, 7.15f, Rgb(255, 180, 80), lit: true);
            }
            // 坑口（トンネルの入口・出口の山肌）
            var portal = Rgb(70, 100, 60);
            if (!IsTunnel(s0 - Step) || !IsTunnel(s1))
            {
                double s = !IsTunnel(s0 - Step) ? s0 : s1;
                Face(s, -80, -10.5f, 0, 35, portal);
                Face(s, 10.5f, 80, 0, 35, portal);
                Face(s, -10.5f, 10.5f, 7.2f, 35, Rgb(150, 146, 136));
            }
        }
    }

    /// <summary>一般道：片側1車線・歩道つき。終点の先は名所前の広場。</summary>
    private void DrawLocalSegment(double s0, double s1)
    {
        var grass = Rgb(100, 150, 78);
        if (s0 > LocalEndS + 8)
        {
            Band(s0, s1, -500, -45, -0.05f, grass);
            Band(s0, s1, 45, 500, -0.05f, grass);
            Band(s0, s1, -45, 45, 0, Rgb(185, 180, 168));
            return;
        }
        Band(s0, s1, -500, -6, -0.05f, grass);
        Band(s0, s1, 6, 500, -0.05f, grass);
        Band(s0, s1, -6, -3.6f, 0.08f, Rgb(178, 176, 170));
        Band(s0, s1, 3.6f, 6, 0.08f, Rgb(178, 176, 170));
        Band(s0, s1, -3.6f, 3.6f, 0, Asphalt);
        Band(s0, s1, -3.45f, -3.3f, 0.02f, White);
        Band(s0, s1, 3.3f, 3.45f, 0.02f, White);
        if ((long)Math.Floor(s0 / Step) % 2 == 0) Band(s0, s1, -0.08f, 0.08f, 0.02f, White);
        Wall(s0, s1, -3.6f, 0, 0.08f, Rgb(140, 140, 135));
        Wall(s0, s1, 3.6f, 0, 0.08f, Rgb(140, 140, 135));
    }

    private void DrawTown()
    {
        // 家並み
        const double slot = 16;
        long k0 = (long)Math.Floor((PlayerS - 60) / slot);
        long k1 = (long)Math.Floor((PlayerS + 600) / slot);
        for (long k = k0; k <= k1; k++)
        {
            double s = k * slot;
            if (EnvAt(s) != Env.Local || s > LocalEndS - 30) continue;
            for (int side = -1; side <= 1; side += 2)
            {
                long id = k * 2 + (side + 1) / 2 + 5000;
                if (Hash(id * 17) > 0.8f) continue;
                float u = side * (11 + Hash(id * 17 + 1) * 6);
                float w = 7 + Hash(id * 17 + 2) * 3, h = 5 + Hash(id * 17 + 3) * 2.5f;
                var p = P(s + Hash(id * 17 + 4) * 4, u, 0);
                float d = Vector3.Distance(p, cam);
                int tone = 200 + (int)(Hash(id * 17 + 5) * 50);
                var wallC = Night ? FogC(Rgb(230, 200, 130), d, lit: Hash(id * 17 + 6) < 0.6f) : FogC(Rgb(tone, tone - 8, tone - 25), d);
                Raylib.DrawCube(p + new Vector3(0, h / 2, 0), w, h, 8, wallC);
                var roofC = FogC(Hash(id * 17 + 7) < 0.5f ? Rgb(70, 75, 90) : Rgb(120, 70, 50), d);
                Rlgl.PushMatrix();
                Rlgl.Translatef(p.X, h, p.Z);
                Rlgl.Scalef(w / 2 + 0.7f, 1, 4.7f);
                Rlgl.Rotatef(45, 0, 1, 0);
                Raylib.DrawCylinder(Vector3.Zero, 0, 1.4142f, 2.8f, 4, roofC);
                Rlgl.PopMatrix();
            }
        }
        // 電柱と電線
        const double poleSlot = 30;
        long p0 = (long)Math.Floor((PlayerS - 60) / poleSlot);
        long p1 = (long)Math.Floor((PlayerS + 450) / poleSlot);
        Vector3? prevTop = null;
        for (long k = p0; k <= p1; k++)
        {
            double s = k * poleSlot;
            if (EnvAt(s) != Env.Local || s > LocalEndS) { prevTop = null; continue; }
            var p = P(s, -6.6f, 0);
            float d = Vector3.Distance(p, cam);
            var c = FogC(Rgb(150, 150, 145), d);
            Raylib.DrawCylinder(p, 0.14f, 0.18f, 9, 6, c);
            var top = p + new Vector3(0, 8.6f, 0);
            Raylib.DrawCube(top, 1.6f, 0.15f, 0.15f, c);
            if (prevTop is Vector3 pt)
            {
                var wire = FogC(Rgb(40, 40, 40), d);
                Raylib.DrawLine3D(pt + new Vector3(0.7f, 0, 0), top + new Vector3(0.7f, 0, 0), wire);
                Raylib.DrawLine3D(pt - new Vector3(0.7f, 0, 0), top - new Vector3(0.7f, 0, 0), wire);
            }
            prevTop = top;
        }
    }

    private void DrawMountains()
    {
        const double slot = 220;
        long k0 = (long)Math.Floor((PlayerS - 200) / slot);
        long k1 = (long)Math.Floor((PlayerS + 960) / slot);
        for (long k = k0; k <= k1; k++)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                long id = k * 4 + side + 10;
                double s = k * slot + Hash(id * 3) * slot;
                var env = EnvAt(s);
                float chance = env == Env.Mountain ? 0.9f : env is Env.Ferry or Env.MegaBridge ? 0.15f : 0.5f;
                if (Hash(id * 3 + 1) > chance) continue;
                float u = side * (230 + Hash(id * 3 + 2) * 380);
                float scale = env == Env.Mountain ? 1.6f : 1f;
                float r = (140 + Hash(id * 5) * 220) * scale;
                float h = (60 + Hash(id * 5 + 1) * 150) * scale;
                r = Math.Min(r, Math.Abs(u) - 60);   // 山すそが道路にかからないように
                float baseY = IsSea(env) ? SeaLevel(env) : -0.2f;
                var p = P(s, u, baseY);
                if (p.Z > 990) continue;
                var c = Haze(Rgb(60, 105, 80), Vector3.Distance(p, cam));
                Raylib.DrawCylinder(p, 0, r, h, 9, c);
            }
        }
    }

    private void DrawTrees()
    {
        const double slot = 14;
        long k0 = (long)Math.Floor((PlayerS - 60) / slot);
        long k1 = (long)Math.Floor((PlayerS + 460) / slot);
        for (long k = k0; k <= k1; k++)
        {
            double sBase = k * slot;
            var env = EnvAt(sBase);
            if (env is Env.Bridge or Env.Ferry or Env.MegaBridge) continue;
            float density = env == Env.Mountain ? 0.8f : env == Env.Local ? 0.25f : 0.45f;
            if (env == Env.Local && sBase > LocalEndS - 40 && sBase < LocalEndS + 260) continue;
            for (int side = -1; side <= 1; side += 2)
            {
                long id = k * 2 + (side + 1) / 2;
                if (Hash(id * 11) > density) continue;
                double s = sBase + Hash(id * 11 + 1) * slot;
                if (IsTunnel(s)) continue;
                float u = side * (13 + Hash(id * 11 + 2) * 60);
                float h = 6 + Hash(id * 11 + 3) * 7;
                var p = P(s, u, 0);
                float d = Vector3.Distance(p, cam);
                bool pine = env == Env.Mountain || Hash(id * 11 + 4) < 0.5f;
                Raylib.DrawCylinder(p, 0.25f, 0.35f, h * 0.35f, 5, FogC(Rgb(90, 65, 45), d));
                var leaf = FogC(pine ? Rgb(40, 95, 50) : Rgb(70, 130, 55), d);
                var top = p + new Vector3(0, h * 0.25f, 0);
                if (pine) Raylib.DrawCylinder(top, 0, h * 0.28f, h * 0.8f, 7, leaf);
                else Raylib.DrawSphere(top + new Vector3(0, h * 0.35f, 0), h * 0.3f, leaf);
            }
        }
    }

    private void DrawBuildings()
    {
        if (Urban < 0.02f) return;
        const double slot = 32;
        long k0 = (long)Math.Floor((PlayerS - 60) / slot);
        long k1 = (long)Math.Floor((PlayerS + 700) / slot);
        for (long k = k0; k <= k1; k++)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                long id = k * 2 + (side + 1) / 2 + 777;
                if (Hash(id * 13) > 0.75f * Urban) continue;
                double s = k * slot + Hash(id * 13 + 1) * 10;
                if (EnvAt(s) != Env.Land || IsTunnel(s)) continue;
                float u = side * (26 + Hash(id * 13 + 2) * 110);
                float w = 10 + Hash(id * 13 + 3) * 16;
                float dep = 10 + Hash(id * 13 + 4) * 16;
                float h = 8 + Hash(id * 13 + 5) * Hash(id * 13 + 6) * 70;
                var p = P(s, u, h / 2);
                float d = Vector3.Distance(p, cam);
                int g = 150 + (int)(Hash(id * 13 + 7) * 80);
                var c = FogC(Rgb(g, g - 5, g - 15), d);
                if (Night) c = FogC(Lerp(Rgb(40, 40, 50), Rgb(230, 200, 120), Hash(id * 13 + 8) * 0.5f), d, lit: true);
                Raylib.DrawCube(p, w, h, dep, c);
            }
        }
    }

    private void DrawTunnelHills()
    {
        long k0 = (long)Math.Floor((PlayerS - 900) / TunnelChunk);
        long k1 = (long)Math.Floor((PlayerS + ViewAhead) / TunnelChunk);
        for (long k = k0; k <= k1; k++)
        {
            if (!TunnelInChunk(k, out double start, out double len)) continue;
            double mid = start + len / 2;
            var p = P(mid, 0, -0.5f);
            if (p.Z - (float)len / 2 > ViewAhead || p.Z + (float)len / 2 < -ViewBehind) continue;
            var c = FogC(Rgb(62, 100, 55), Math.Max(10, Vector3.Distance(p, cam) - (float)len / 2));
            Raylib.DrawCylinder(p, 0, (float)len / 2, 75 + (float)len * 0.05f, 16, c);
        }
    }

    private void DrawBridgeTowers()
    {
        const double slot = 700;
        long k0 = (long)Math.Floor((PlayerS - 300) / slot);
        long k1 = (long)Math.Floor((PlayerS + ViewAhead) / slot);
        for (long k = k0; k <= k1; k++)
        {
            double s = k * slot + 350;
            if (EnvAt(s) != Env.Bridge) continue;
            var center = P(s, 0, 0);
            float d = Vector3.Distance(center, cam);
            var tower = FogC(Rgb(225, 225, 230), d);
            foreach (float u in new[] { -13f, 13f })
            {
                Raylib.DrawCube(P(s, u, 5), 2.5f, 80, 2.5f, tower);
                for (int i = 1; i <= 7; i++)
                {
                    double ds = i * 45;
                    var top = P(s, u, 43);
                    Raylib.DrawLine3D(top, P(s + ds, u * 0.8f, 1.2f), tower);
                    Raylib.DrawLine3D(top, P(s - ds, u * 0.8f, 1.2f), tower);
                }
            }
            Raylib.DrawCube(P(s, 0, 42), 28, 2.5f, 2.5f, tower);
            Raylib.DrawCube(P(s, 0, 20), 28, 2f, 2f, tower);
        }
    }

    /// <summary>沖縄海上大橋：高さ約300mの主塔・ケーブル・トラス・照明。</summary>
    private void DrawMegaBridge()
    {
        // 主塔とケーブル
        const double slot = 600;
        long k0 = (long)Math.Floor((PlayerS - 300) / slot);
        long k1 = (long)Math.Floor((PlayerS + 990) / slot);
        for (long k = k0; k <= k1; k++)
        {
            double s = k * slot + 300;
            if (EnvAt(s) != Env.MegaBridge) continue;
            var c = P(s, 0, 0);
            float d = Vector3.Distance(c, cam);
            var tower = LandmarkFog(Rgb(225, 225, 232), c);
            foreach (float u in new[] { -18f, 18f })
            {
                Raylib.DrawCube(P(s, u, 80), 6, 300, 6, tower);
                Raylib.DrawCube(P(s, u, -66), 12, 8, 14, LandmarkFog(Rgb(150, 150, 150), c)); // 基礎
                var top = P(s, u * 0.9f, 226);
                for (int i = 1; i <= 12; i++)
                {
                    double ds = i * 24;
                    Raylib.DrawLine3D(top, P(s + ds, u * 0.6f, 1.3f), tower);
                    Raylib.DrawLine3D(top, P(s - ds, u * 0.6f, 1.3f), tower);
                }
                // 航空障害灯
                bool blink = (Raylib.GetTime() + k * 0.3) % 1.5 < 0.75;
                Raylib.DrawSphere(P(s, u, 231), 1.6f, blink ? Rgb(255, 40, 30) : FogC(Rgb(120, 30, 30), d));
            }
            Raylib.DrawCube(P(s, 0, 225), 42, 6, 6, tower);
            Raylib.DrawCube(P(s, 0, 150), 42, 5, 5, tower);
            Raylib.DrawCube(P(s, 0, -8), 42, 6, 8, tower);
        }

        // トラスの斜材と照明
        const double tslot = 12;
        long t0 = (long)Math.Floor((PlayerS - 60) / tslot);
        long t1 = (long)Math.Floor((PlayerS + 500) / tslot);
        for (long k = t0; k <= t1; k++)
        {
            double s = k * tslot;
            if (EnvAt(s) != Env.MegaBridge) continue;
            float d = Vector3.Distance(P(s, 0, 0), cam);
            var steel = FogC(Rgb(170, 75, 45), d);
            foreach (float u in new[] { -10.65f, 10.65f })
                Raylib.DrawLine3D(P(s, u, -15.8f), P(s + tslot, u, -0.2f), steel);
            if (k % 5 == 0)
            {
                var pole = FogC(Rgb(180, 180, 185), d);
                foreach (float u in new[] { -10.2f, 10.2f })
                {
                    Raylib.DrawCube(P(s, u, 4.5f), 0.25f, 9, 0.25f, pole);
                    Raylib.DrawCube(P(s, u * 0.88f, 9), 1.6f, 0.25f, 0.4f, Night ? Rgb(255, 210, 140) : pole);
                }
            }
        }
    }

    private void DrawGantries()
    {
        const double slot = 1600;
        long k0 = (long)Math.Floor((PlayerS - 50) / slot);
        long k1 = (long)Math.Floor((PlayerS + 500) / slot);
        for (long k = k0; k <= k1; k++)
        {
            double s = k * slot + 800;
            var env = EnvAt(s);
            if (env is Env.Ferry or Env.Local || IsTunnel(s)) continue;
            var p = P(s, -5, 6);
            float d = Vector3.Distance(p, cam);
            var steel = FogC(Rgb(170, 170, 175), d);
            Raylib.DrawCube(P(s, -10.1f, 3.5f), 0.4f, 7, 0.4f, steel);
            Raylib.DrawCube(P(s, -0.1f, 3.5f), 0.4f, 7, 0.4f, steel);
            Raylib.DrawCube(P(s, -5.1f, 7.1f), 10.4f, 0.35f, 0.35f, steel);
            Raylib.DrawCube(p, 8.2f, 2.6f, 0.2f, FogC(Rgb(0, 110, 72), d));
            Signs.Add(p + new Vector3(0, 0, -0.15f));
        }
    }

    // ---------------- 車 ----------------

    public void DrawVehicle(double s, float u, float yawExtra, Color body, VehicleKind kind, bool oncoming,
        bool braking = false, float wheelSteer = 0, float y = 0, float roll = 0, float turretYaw = 0, float recoil = 0)
    {
        float yaw = MathF.Atan2((float)Slope(s), 1) * 180 / MathF.PI + yawExtra + (oncoming ? 180 : 0);
        float spin = kind == VehicleKind.Horse ? (float)(s / 3.2 * 360 % 360)
            : kind == VehicleKind.Hikyaku ? (float)(s / 2.4 * 360 % 360)
            : (float)(s / 0.33 * 180 / Math.PI % 360) * (oncoming ? -1 : 1);
        DrawVehicleAt(P(s, u, y), yaw, body, kind, spin, braking, wheelSteer, roll, turretYaw, recoil, Math.Abs(y) < 0.5f);
    }

    /// <summary>ワールド座標と向き（度）を直接指定して車を描く。</summary>
    public void DrawVehicleAt(Vector3 pos, float yaw, Color body, VehicleKind kind, float spin,
        bool braking = false, float wheelSteer = 0, float roll = 0, float turretYaw = 0, float recoil = 0, bool shadow = true)
    {
        float d = Vector3.Distance(pos, cam);
        if (d > ViewAhead * FogScale) return;
        Rlgl.PushMatrix();
        Rlgl.Translatef(pos.X, pos.Y, pos.Z);
        Rlgl.Rotatef(yaw, 0, 1, 0);
        if (roll != 0) Rlgl.Rotatef(roll, 1, 0, 0.4f);
        CarModels.Draw(this, kind, body, d, braking, wheelSteer, spin, turretYaw, recoil, shadow: shadow && roll == 0);
        Rlgl.PopMatrix();
    }

    /// <summary>フリーモード用：フォグ計算に使うカメラ位置だけを設定する。</summary>
    public void SetCamera(Vector3 cameraPos) => cam = cameraPos;

    /// <summary>夜間、自車のヘッドライトが路面を照らす。</summary>
    public void DrawHeadlightPool(double s, float u)
    {
        if (!Night) return;
        Rlgl.Begin(DrawMode.Quads);
        for (int i = 0; i < 8; i++)
        {
            double s0 = s + 2.5 + i * 4, s1 = s0 + 4;
            float w0 = 1.2f + i * 0.45f, w1 = w0 + 0.45f;
            byte a0 = (byte)(70 - i * 8), a1 = (byte)(70 - (i + 1) * 8);
            var p0 = P(s0, u - w0, 0.04f); var p1 = P(s0, u + w0, 0.04f);
            var p2 = P(s1, u + w1, 0.04f); var p3 = P(s1, u - w1, 0.04f);
            Rlgl.Color4ub(255, 240, 190, a0); Rlgl.Vertex3f(p0.X, p0.Y, p0.Z);
            Rlgl.Color4ub(255, 240, 190, a0); Rlgl.Vertex3f(p1.X, p1.Y, p1.Z);
            Rlgl.Color4ub(255, 240, 190, a1); Rlgl.Vertex3f(p2.X, p2.Y, p2.Z);
            Rlgl.Color4ub(255, 240, 190, a1); Rlgl.Vertex3f(p3.X, p3.Y, p3.Z);
        }
        Rlgl.End();
    }

    /// <summary>フェリーの船体（プレイヤーの車が甲板に乗っている）。</summary>
    public void DrawFerry(float playerU)
    {
        var hullPos = P(PlayerS + 12, playerU, -3);
        float d = Vector3.Distance(hullPos, cam);
        Raylib.DrawCube(hullPos, 22, 6, 110, FogC(Rgb(235, 238, 242), d));
        Raylib.DrawCube(P(PlayerS + 12, playerU, -5.5f), 22.4f, 1.2f, 110.5f, FogC(Rgb(30, 70, 140), d));
        Raylib.DrawCube(P(PlayerS + 12, playerU, 0.02f), 20, 0.05f, 105, FogC(Rgb(120, 125, 130), d));
        Raylib.DrawCube(P(PlayerS + 52, playerU, 6), 20, 12, 18, FogC(Rgb(245, 245, 248), d));
        Raylib.DrawCube(P(PlayerS + 52, playerU, 9), 20.2f, 1.6f, 18.2f, FogC(Rgb(40, 50, 70), d));
        Raylib.DrawCube(P(PlayerS + 48, playerU, 15), 4, 6, 4, FogC(Rgb(200, 50, 40), d));
    }
}
