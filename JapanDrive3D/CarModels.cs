using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public enum VehicleKind { Sedan, Minivan, Kei, Truck, Sports, Tank, Amphibious, Horse, Hikyaku, Helicopter, Airplane }

/// <summary>
/// ローポリの車モデル。側面の輪郭（プロファイル）を押し出したボディ＋窓のキャビン＋細部パーツで組み立てる。
/// ローカル座標：+Z が前、+Y が上、X は左右対称。
/// </summary>
public static class CarModels
{
    private enum Part { Body, Glass, Dark, Chrome, Tail, Head, Plate, Cargo }

    private readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, Part Part, float Shade, float Up);
    private readonly record struct Wheel(float Z, float X, float R, float W, bool Front);

    private sealed class Model
    {
        public readonly List<Tri> Tris = new();
        public readonly List<Wheel> Wheels = new();
        public float HalfWidth, Front, Rear;
        public bool YellowPlate;
    }

    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.35f, 1f, 0.45f));
    private static readonly Dictionary<VehicleKind, Model> Models = new();

    static CarModels()
    {
        Models[VehicleKind.Sedan] = BuildCar(
            new[] { (-2.3f, 0.30f), (2.3f, 0.30f), (2.33f, 0.56f), (2.15f, 0.76f), (1.2f, 0.92f), (-1.3f, 0.95f), (-2.15f, 0.93f), (-2.33f, 0.74f) },
            hw: 0.9f, cab: (-1.3f, 1.2f, 0.94f, 0.84f, -0.75f, 0.35f, 1.44f, 0.7f),
            wheelZ: 1.42f, wheelR: 0.33f, headY: 0.64f, tailY: 0.8f);

        Models[VehicleKind.Minivan] = BuildCar(
            new[] { (-2.35f, 0.32f), (2.35f, 0.32f), (2.38f, 0.62f), (2.1f, 0.9f), (1.45f, 1.02f), (-2.3f, 1.02f), (-2.38f, 0.82f) },
            hw: 0.88f, cab: (-2.3f, 1.45f, 1.01f, 0.84f, -2.22f, 0.55f, 1.86f, 0.76f),
            wheelZ: 1.5f, wheelR: 0.33f, headY: 0.74f, tailY: 1.1f);

        var kei = BuildCar(
            new[] { (-1.7f, 0.30f), (1.7f, 0.30f), (1.73f, 0.6f), (1.55f, 0.84f), (1.15f, 0.95f), (-1.68f, 0.95f), (-1.73f, 0.72f) },
            hw: 0.74f, cab: (-1.66f, 1.15f, 0.94f, 0.7f, -1.6f, 0.5f, 1.72f, 0.66f),
            wheelZ: 1.15f, wheelR: 0.28f, headY: 0.7f, tailY: 1.05f);
        kei.YellowPlate = true;
        Models[VehicleKind.Kei] = kei;

        var sports = BuildCar(
            new[] { (-2.25f, 0.26f), (2.25f, 0.26f), (2.3f, 0.44f), (2.1f, 0.6f), (0.9f, 0.8f), (-1.6f, 0.86f), (-2.2f, 0.84f), (-2.3f, 0.62f) },
            hw: 0.93f, cab: (-1.6f, 0.9f, 0.84f, 0.82f, -0.95f, -0.05f, 1.24f, 0.66f),
            wheelZ: 1.42f, wheelR: 0.34f, headY: 0.55f, tailY: 0.72f);
        AddBox(sports, new Vector3(0, 1.02f, -2.05f), new Vector3(1.7f, 0.05f, 0.32f), Part.Dark);   // リアスポイラー
        AddBox(sports, new Vector3(0.55f, 0.93f, -2.05f), new Vector3(0.06f, 0.16f, 0.12f), Part.Dark);
        AddBox(sports, new Vector3(-0.55f, 0.93f, -2.05f), new Vector3(0.06f, 0.16f, 0.12f), Part.Dark);
        Models[VehicleKind.Sports] = sports;

        Models[VehicleKind.Truck] = BuildTruck();
        Models[VehicleKind.Tank] = BuildTank();
        Models[VehicleKind.Amphibious] = BuildAmphibious();
        Turret = BuildTurret();
    }

    private static readonly Model Turret;

    public static float Length(VehicleKind kind) => kind switch
    {
        VehicleKind.Truck => 10.2f,
        VehicleKind.Tank => 7.6f,
        VehicleKind.Horse or VehicleKind.Hikyaku => Characters.Length(kind),
        VehicleKind.Helicopter or VehicleKind.Airplane => Aircraft.Length(kind),
        VehicleKind.Amphibious => 8.1f,
        VehicleKind.Kei => 3.4f,
        _ => 4.6f,
    };

    // ---------------- 組み立て ----------------

    private static Model BuildCar((float Z, float Y)[] profile, float hw,
        (float zb0, float zb1, float yb, float hwb, float zt0, float zt1, float yt, float hwt) cab,
        float wheelZ, float wheelR, float headY, float tailY)
    {
        var m = new Model { HalfWidth = hw, Front = profile.Max(p => p.Z), Rear = profile.Min(p => p.Z) };
        AddProfilePrism(m, profile, hw, Part.Body);
        AddCabin(m, cab.zb0, cab.zb1, cab.yb, cab.hwb, cab.zt0, cab.zt1, cab.yt, cab.hwt, glassSides: true);

        float f = m.Front, r = m.Rear;
        // フロント：ヘッドライト・グリル・ナンバー・バンパー
        AddBox(m, new Vector3(hw - 0.27f, headY, f - 0.02f), new Vector3(0.42f, 0.12f, 0.08f), Part.Head);
        AddBox(m, new Vector3(-(hw - 0.27f), headY, f - 0.02f), new Vector3(0.42f, 0.12f, 0.08f), Part.Head);
        AddBox(m, new Vector3(0, headY - 0.06f, f - 0.01f), new Vector3(hw * 0.8f, 0.14f, 0.06f), Part.Dark);
        AddBox(m, new Vector3(0, 0.38f, f + 0.02f), new Vector3(0.36f, 0.16f, 0.03f), Part.Plate);
        AddBox(m, new Vector3(0, 0.3f, f), new Vector3(hw * 2 + 0.02f, 0.1f, 0.12f), Part.Dark);
        // リア：テールランプ・ナンバー・バンパー
        AddBox(m, new Vector3(hw - 0.25f, tailY, r + 0.02f), new Vector3(0.42f, 0.14f, 0.08f), Part.Tail);
        AddBox(m, new Vector3(-(hw - 0.25f), tailY, r + 0.02f), new Vector3(0.42f, 0.14f, 0.08f), Part.Tail);
        AddBox(m, new Vector3(0, 0.5f, r - 0.02f), new Vector3(0.36f, 0.16f, 0.03f), Part.Plate);
        AddBox(m, new Vector3(0, 0.3f, r), new Vector3(hw * 2 + 0.02f, 0.1f, 0.12f), Part.Dark);
        // ドアミラー・サイドの黒いモール
        AddBox(m, new Vector3(cab.hwb + 0.1f, cab.yb + 0.1f, cab.zb1 - 0.15f), new Vector3(0.16f, 0.1f, 0.14f), Part.Body);
        AddBox(m, new Vector3(-(cab.hwb + 0.1f), cab.yb + 0.1f, cab.zb1 - 0.15f), new Vector3(0.16f, 0.1f, 0.14f), Part.Body);
        AddBox(m, new Vector3(0, 0.36f, 0), new Vector3(hw * 2 + 0.01f, 0.08f, (f - r) * 0.55f), Part.Dark);

        float wx = hw - 0.12f;
        foreach (float z in new[] { wheelZ, -wheelZ })
        {
            m.Wheels.Add(new Wheel(z, wx, wheelR, 0.24f, z > 0));
            m.Wheels.Add(new Wheel(z, -wx, wheelR, 0.24f, z > 0));
        }
        return m;
    }

    private static Model BuildTruck()
    {
        var m = new Model { HalfWidth = 1.23f, Front = 4.7f, Rear = -5.15f };
        AddBox(m, new Vector3(0, 0.78f, -0.3f), new Vector3(1.9f, 0.32f, 9.6f), Part.Dark);           // フレーム
        AddBox(m, new Vector3(0, 1.3f, 3.65f), new Vector3(2.35f, 1.25f, 1.95f), Part.Body);          // キャブ下部
        AddCabin(m, 2.68f, 4.62f, 1.92f, 1.17f, 2.72f, 4.42f, 3.05f, 1.12f, glassSides: false);       // キャブ上部
        AddBox(m, new Vector3(0, 2.18f, -1.38f), new Vector3(2.45f, 3.0f, 7.5f), Part.Cargo);         // 荷台
        AddBox(m, new Vector3(0, 1.35f, -1.38f), new Vector3(2.47f, 0.32f, 7.52f), Part.Body);        // 荷台のライン
        AddBox(m, new Vector3(0, 0.66f, 4.68f), new Vector3(2.3f, 0.36f, 0.16f), Part.Chrome);        // バンパー
        AddBox(m, new Vector3(0, 1.32f, 4.63f), new Vector3(1.4f, 0.5f, 0.04f), Part.Dark);           // グリル
        AddBox(m, new Vector3(0.85f, 0.95f, 4.64f), new Vector3(0.35f, 0.18f, 0.04f), Part.Head);
        AddBox(m, new Vector3(-0.85f, 0.95f, 4.64f), new Vector3(0.35f, 0.18f, 0.04f), Part.Head);
        AddBox(m, new Vector3(1.32f, 2.3f, 4.35f), new Vector3(0.08f, 0.5f, 0.1f), Part.Dark);       // ミラー
        AddBox(m, new Vector3(-1.32f, 2.3f, 4.35f), new Vector3(0.08f, 0.5f, 0.1f), Part.Dark);
        AddBox(m, new Vector3(0.95f, 0.88f, -5.14f), new Vector3(0.32f, 0.18f, 0.04f), Part.Tail);
        AddBox(m, new Vector3(-0.95f, 0.88f, -5.14f), new Vector3(0.32f, 0.18f, 0.04f), Part.Tail);
        AddBox(m, new Vector3(0, 0.55f, -5.08f), new Vector3(2.2f, 0.16f, 0.1f), Part.Dark);          // 突入防止バー
        AddBox(m, new Vector3(0, 0.88f, -5.15f), new Vector3(0.36f, 0.18f, 0.03f), Part.Plate);
        foreach (float z in new[] { 3.6f, -2.9f, -4.1f })
        {
            m.Wheels.Add(new Wheel(z, 1.0f, 0.5f, z > 0 ? 0.32f : 0.5f, z > 0));
            m.Wheels.Add(new Wheel(z, -1.0f, 0.5f, z > 0 ? 0.32f : 0.5f, z > 0));
        }
        return m;
    }

    /// <summary>戦車の車体（砲塔は別モデル）。</summary>
    private static Model BuildTank()
    {
        var m = new Model { HalfWidth = 1.95f, Front = 3.9f, Rear = -3.6f };
        AddProfilePrism(m, new[] { (-3.5f, 0.55f), (3.3f, 0.55f), (3.9f, 1.05f), (3.5f, 1.6f), (-3.55f, 1.62f) }, 1.2f, Part.Body);
        foreach (float sx in new[] { -1f, 1f })
        {
            AddBox(m, new Vector3(sx * 1.6f, 0.62f, 0), new Vector3(0.7f, 1.0f, 7.2f), Part.Dark);    // 履帯
            AddBox(m, new Vector3(sx * 1.6f, 1.2f, 0.1f), new Vector3(0.82f, 0.08f, 7.5f), Part.Body); // フェンダー
            AddBox(m, new Vector3(sx * 1.6f, 1.45f, -2.6f), new Vector3(0.6f, 0.4f, 1.2f), Part.Body); // 物入れ
            for (int k = 0; k < 6; k++)
                m.Wheels.Add(new Wheel(-2.75f + k * 1.1f, sx * 1.97f, 0.38f, 0.1f, false));
        }
        AddBox(m, new Vector3(0.9f, 1.3f, 3.62f), new Vector3(0.3f, 0.14f, 0.06f), Part.Head);
        AddBox(m, new Vector3(-0.9f, 1.3f, 3.62f), new Vector3(0.3f, 0.14f, 0.06f), Part.Head);
        AddBox(m, new Vector3(0.95f, 1.35f, -3.58f), new Vector3(0.25f, 0.14f, 0.06f), Part.Tail);
        AddBox(m, new Vector3(-0.95f, 1.35f, -3.58f), new Vector3(0.25f, 0.14f, 0.06f), Part.Tail);
        AddBox(m, new Vector3(0, 1.66f, -2.8f), new Vector3(1.8f, 0.1f, 1.2f), Part.Dark);         // 排気グリル
        return m;
    }

    /// <summary>戦車の砲塔（ローカル原点を回転の中心にする）。</summary>
    private static Model BuildTurret()
    {
        var m = new Model { HalfWidth = 1.4f, Front = 2.2f, Rear = -2.4f };
        AddProfilePrism(m, new[] { (-2.0f, 0f), (1.5f, 0f), (2.15f, 0.42f), (1.6f, 0.85f), (-2.1f, 0.82f) }, 1.35f, Part.Body);
        AddBox(m, new Vector3(0, 0.42f, 2.2f), new Vector3(0.7f, 0.55f, 0.3f), Part.Dark);       // 防盾
        AddBox(m, new Vector3(0, 0.45f, -2.3f), new Vector3(2.3f, 0.55f, 0.35f), Part.Dark);     // 後部バスケット
        AddBox(m, new Vector3(-0.55f, 0.92f, -0.5f), new Vector3(0.75f, 0.16f, 0.75f), Part.Body); // ハッチ
        AddBox(m, new Vector3(0.6f, 0.98f, 0.1f), new Vector3(0.45f, 0.3f, 0.5f), Part.Glass);   // 照準器
        AddBox(m, new Vector3(-0.55f, 1.05f, 0.2f), new Vector3(0.08f, 0.08f, 0.9f), Part.Dark);  // 機関銃
        return m;
    }

    /// <summary>水陸両用輸送車（箱型の車体と履帯）。</summary>
    private static Model BuildAmphibious()
    {
        var m = new Model { HalfWidth = 1.88f, Front = 4.05f, Rear = -4.0f };
        AddProfilePrism(m, new[] { (-4.0f, 0.55f), (3.0f, 0.55f), (4.05f, 1.35f), (3.55f, 2.15f), (-4.0f, 2.25f) }, 1.6f, Part.Body);
        foreach (float sx in new[] { -1f, 1f })
        {
            AddBox(m, new Vector3(sx * 1.66f, 0.6f, -0.3f), new Vector3(0.48f, 0.9f, 6.6f), Part.Dark);
            for (int k = 0; k < 6; k++)
                m.Wheels.Add(new Wheel(-3.0f + k * 1.05f, sx * 1.9f, 0.34f, 0.08f, false));
            AddBox(m, new Vector3(sx * 1.62f, 1.25f, -0.3f), new Vector3(0.1f, 0.12f, 7.4f), Part.Dark); // 水切りの線
        }
        AddBox(m, new Vector3(0.75f, 2.55f, 1.0f), new Vector3(0.95f, 0.6f, 0.95f), Part.Body);   // 銃塔
        AddBox(m, new Vector3(0.75f, 2.62f, 1.9f), new Vector3(0.12f, 0.12f, 1.2f), Part.Dark);
        AddBox(m, new Vector3(-0.6f, 2.3f, -2.4f), new Vector3(1.8f, 0.1f, 2.2f), Part.Dark);     // 上部ハッチ
        AddBox(m, new Vector3(0, 1.75f, 3.84f), new Vector3(2.9f, 0.7f, 0.08f), Part.Body);       // 波切り板
        AddBox(m, new Vector3(1.2f, 1.95f, 3.62f), new Vector3(0.3f, 0.14f, 0.06f), Part.Head);
        AddBox(m, new Vector3(-1.2f, 1.95f, 3.62f), new Vector3(0.3f, 0.14f, 0.06f), Part.Head);
        AddBox(m, new Vector3(1.3f, 1.9f, -4.02f), new Vector3(0.3f, 0.14f, 0.06f), Part.Tail);
        AddBox(m, new Vector3(-1.3f, 1.9f, -4.02f), new Vector3(0.3f, 0.14f, 0.06f), Part.Tail);
        AddBox(m, new Vector3(0, 1.4f, -4.02f), new Vector3(1.6f, 1.4f, 0.06f), Part.Dark);       // 後部ランプドア
        return m;
    }

    private static void AddTri(Model m, Vector3 a, Vector3 b, Vector3 c, Part part, Vector3 inside)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-10f) return;
        n = Vector3.Normalize(n);
        if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) n = -n;
        float shade = 0.5f + 0.5f * Math.Max(0, Vector3.Dot(n, LightDir));
        m.Tris.Add(new Tri(a, b, c, part, shade, n.Y));
    }

    private static void AddQuad(Model m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Part part, Vector3 inside)
    {
        AddTri(m, a, b, c, part, inside);
        AddTri(m, a, c, d, part, inside);
    }

    /// <summary>側面の輪郭を左右に押し出したボディ。</summary>
    private static void AddProfilePrism(Model m, (float Z, float Y)[] profile, float hw, Part part)
    {
        float cz = profile.Average(p => p.Z), cy = profile.Average(p => p.Y);
        var inside = new Vector3(0, cy, cz);
        for (int i = 0; i < profile.Length; i++)
        {
            var a = profile[i];
            var b = profile[(i + 1) % profile.Length];
            // 側面（中心からの扇形）
            AddTri(m, new Vector3(hw, cy, cz), new Vector3(hw, a.Y, a.Z), new Vector3(hw, b.Y, b.Z), part, inside);
            AddTri(m, new Vector3(-hw, cy, cz), new Vector3(-hw, a.Y, a.Z), new Vector3(-hw, b.Y, b.Z), part, inside);
            // 上面・前後面（角を少し内側にして丸みを出す）
            float inset = a.Y > cy && b.Y > cy ? 0.06f : 0;
            AddQuad(m,
                new Vector3(hw - inset, a.Y, a.Z), new Vector3(-(hw - inset), a.Y, a.Z),
                new Vector3(-(hw - inset), b.Y, b.Z), new Vector3(hw - inset, b.Y, b.Z), part, inside);
            if (inset > 0)
            {
                // 肩の面取り
                AddQuad(m, new Vector3(hw, a.Y - 0.05f, a.Z), new Vector3(hw - inset, a.Y, a.Z),
                    new Vector3(hw - inset, b.Y, b.Z), new Vector3(hw, b.Y - 0.05f, b.Z), part, inside);
                AddQuad(m, new Vector3(-hw, a.Y - 0.05f, a.Z), new Vector3(-(hw - inset), a.Y, a.Z),
                    new Vector3(-(hw - inset), b.Y, b.Z), new Vector3(-hw, b.Y - 0.05f, b.Z), part, inside);
            }
        }
    }

    /// <summary>窓まわり（下の四角形と屋根の四角形をつないだ台形の箱）。</summary>
    private static void AddCabin(Model m, float zb0, float zb1, float yb, float hwb,
        float zt0, float zt1, float yt, float hwt, bool glassSides)
    {
        var b0 = new Vector3(hwb, yb, zb0); var b1 = new Vector3(-hwb, yb, zb0);
        var b2 = new Vector3(-hwb, yb, zb1); var b3 = new Vector3(hwb, yb, zb1);
        var t0 = new Vector3(hwt, yt, zt0); var t1 = new Vector3(-hwt, yt, zt0);
        var t2 = new Vector3(-hwt, yt, zt1); var t3 = new Vector3(hwt, yt, zt1);
        var inside = new Vector3(0, (yb + yt) / 2, (zb0 + zb1 + zt0 + zt1) / 4);

        AddQuad(m, t0, t1, t2, t3, Part.Body, inside);                         // 屋根
        AddQuad(m, b3, b2, t2, t3, Part.Glass, inside);                        // フロントガラス
        AddQuad(m, b0, b1, t1, t0, Part.Glass, inside);                        // リアガラス
        var side = glassSides ? Part.Glass : Part.Body;
        AddQuad(m, b0, b3, t3, t0, side, inside);
        AddQuad(m, b1, b2, t2, t1, side, inside);
        if (glassSides)
        {
            // 窓の柱（Bピラー）と屋根の縁取り
            float zc = (zt0 + zt1) / 2;
            float zbc = (zb0 + zb1) / 2;
            foreach (float s in new[] { 1f, -1f })
            {
                var p0 = new Vector3(s * (hwb + 0.005f), yb, zbc - 0.07f);
                var p1 = new Vector3(s * (hwb + 0.005f), yb, zbc + 0.07f);
                var p2 = new Vector3(s * (hwt + 0.005f), yt, zc + 0.07f);
                var p3 = new Vector3(s * (hwt + 0.005f), yt, zc - 0.07f);
                AddQuad(m, p0, p1, p2, p3, Part.Body, inside);
            }
        }
        else
        {
            AddBox(m, new Vector3(0, (yb + yt) / 2 + 0.15f, zb1 - 0.02f), new Vector3(hwb * 1.7f, (yt - yb) * 0.55f, 0.04f), Part.Glass);
        }
    }

    private static void AddBox(Model m, Vector3 c, Vector3 size, Part part)
    {
        var h = size / 2;
        Vector3 V(float x, float y, float z) => c + new Vector3(x * h.X, y * h.Y, z * h.Z);
        AddQuad(m, V(-1, 1, -1), V(1, 1, -1), V(1, 1, 1), V(-1, 1, 1), part, c);
        AddQuad(m, V(-1, -1, -1), V(1, -1, -1), V(1, -1, 1), V(-1, -1, 1), part, c);
        AddQuad(m, V(-1, -1, 1), V(1, -1, 1), V(1, 1, 1), V(-1, 1, 1), part, c);
        AddQuad(m, V(-1, -1, -1), V(1, -1, -1), V(1, 1, -1), V(-1, 1, -1), part, c);
        AddQuad(m, V(1, -1, -1), V(1, -1, 1), V(1, 1, 1), V(1, 1, -1), part, c);
        AddQuad(m, V(-1, -1, -1), V(-1, -1, 1), V(-1, 1, 1), V(-1, 1, -1), part, c);
    }

    // ---------------- 描画 ----------------

    private static Color Mul(Color c, float k) =>
        new((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k), c.A);

    private static readonly Color GlassBase = Rgb(28, 38, 52), GlassSky = Rgb(150, 175, 200);

    private static void DrawTris(Scene scene, Model m, Color body, float dist, bool braking, bool near)
    {
        bool night = scene.Night;
        Rlgl.Begin(DrawMode.Triangles);
        foreach (var t in m.Tris)
        {
            if (!near && t.Part is Part.Plate or Part.Chrome) continue;
            Color c;
            bool lit = false;
            switch (t.Part)
            {
                case Part.Body: c = Mul(body, t.Shade); break;
                case Part.Cargo: c = Mul(Rgb(232, 232, 235), t.Shade); break;
                case Part.Glass: c = Lerp(GlassBase, GlassSky, Math.Max(0, t.Up) * 0.55f + 0.1f); break;
                case Part.Dark: c = Mul(Rgb(32, 32, 36), t.Shade); break;
                case Part.Chrome: c = Mul(Rgb(200, 200, 205), t.Shade); break;
                case Part.Plate: c = m.YellowPlate ? Rgb(235, 200, 40) : Rgb(235, 235, 228); break;
                case Part.Tail:
                    lit = night || braking;
                    c = braking ? Rgb(255, 30, 30) : night ? Rgb(230, 30, 30) : Rgb(170, 25, 30);
                    break;
                default: // Head
                    lit = night;
                    c = night ? Rgb(255, 252, 230) : Rgb(225, 228, 230);
                    break;
            }
            c = scene.FogC(c, dist, lit);
            Rlgl.Color4ub(c.R, c.G, c.B, 255);
            Rlgl.Vertex3f(t.A.X, t.A.Y, t.A.Z);
            Rlgl.Vertex3f(t.B.X, t.B.Y, t.B.Z);
            Rlgl.Vertex3f(t.C.X, t.C.Y, t.C.Z);
        }
        Rlgl.End();
    }

    /// <summary>車を描く（Rlglの現在の行列で、位置・向きはすでに設定済みであること）。</summary>
    public static void Draw(Scene scene, VehicleKind kind, Color body, float dist, bool braking, float wheelSteer, float wheelSpin,
        float turretYaw = 0, float recoil = 0, bool shadow = true)
    {
        if (kind is VehicleKind.Helicopter or VehicleKind.Airplane)
        {
            Aircraft.Draw(scene, kind, body, dist, wheelSpin, true);
            return;
        }
        if (kind is VehicleKind.Horse or VehicleKind.Hikyaku)
        {
            Characters.Draw(scene, kind, body, dist, wheelSpin * MathF.PI / 180, shadow);
            return;
        }
        var m = Models[kind];
        bool night = scene.Night;
        bool near = dist < 140;

        // 接地影
        if (shadow)
        {
            Rlgl.Begin(DrawMode.Quads);
            Rlgl.Color4ub(0, 0, 0, (byte)(night ? 50 : 95));
            float sw = m.HalfWidth + 0.15f;
            Rlgl.Vertex3f(sw, 0.035f, m.Rear - 0.2f);
            Rlgl.Vertex3f(-sw, 0.035f, m.Rear - 0.2f);
            Rlgl.Vertex3f(-sw, 0.035f, m.Front + 0.2f);
            Rlgl.Vertex3f(sw, 0.035f, m.Front + 0.2f);
            Rlgl.End();
        }

        DrawTris(scene, m, body, dist, braking, near);

        if (kind == VehicleKind.Tank)
        {
            // 砲塔と主砲（砲塔だけ回転する）
            Rlgl.PushMatrix();
            Rlgl.Translatef(0, 1.6f, 0.2f);
            Rlgl.Rotatef(turretYaw, 0, 1, 0);
            DrawTris(scene, Turret, Mul(body, 1.05f), dist, false, near);
            float back = recoil * 0.6f;
            var gun = scene.FogC(Mul(body, 0.85f), dist);
            Raylib.DrawCylinderEx(new Vector3(0, 0.45f, 2.3f - back), new Vector3(0, 0.45f, 7.3f - back), 0.13f, 0.11f, 10, gun);
            Raylib.DrawCube(new Vector3(0, 0.45f, 7.35f - back), 0.3f, 0.3f, 0.5f, scene.FogC(Rgb(40, 40, 40), dist));
            Raylib.DrawLine3D(new Vector3(-0.9f, 0.9f, -1.6f), new Vector3(-0.9f, 3.4f, -1.9f), scene.FogC(Rgb(30, 30, 30), dist));
            Rlgl.PopMatrix();
        }

        // タイヤとホイール
        var tire = scene.FogC(Rgb(22, 22, 24), dist);
        var rim = scene.FogC(Rgb(185, 188, 195), dist);
        int sides = near ? 14 : 8;
        foreach (var w in m.Wheels)
        {
            Rlgl.PushMatrix();
            Rlgl.Translatef(w.X, w.R, w.Z);
            if (w.Front && wheelSteer != 0) Rlgl.Rotatef(wheelSteer, 0, 1, 0);
            float outer = Math.Sign(w.X) * w.W / 2;
            Raylib.DrawCylinderEx(new Vector3(-outer, 0, 0), new Vector3(outer, 0, 0), w.R, w.R, sides, tire);
            if (near)
            {
                var o = new Vector3(outer * 1.04f, 0, 0);
                Raylib.DrawCylinderEx(o, o * 1.02f + new Vector3(Math.Sign(w.X) * 0.005f, 0, 0), w.R * 0.62f, w.R * 0.62f, sides, rim);
                // 回転が見えるようにスポークを描く
                Rlgl.Rotatef(-wheelSpin, 1, 0, 0);
                var spoke = scene.FogC(Rgb(90, 92, 98), dist);
                for (int i = 0; i < 5; i++)
                {
                    float a = i * MathF.PI * 2 / 5;
                    var dir = new Vector3(0, MathF.Sin(a), MathF.Cos(a)) * w.R * 0.55f;
                    Raylib.DrawLine3D(o * 1.04f, o * 1.04f + dir, spoke);
                }
            }
            Rlgl.PopMatrix();
        }
    }
}
