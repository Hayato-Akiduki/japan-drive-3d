using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// 名所の3Dモデル（基本図形の組み合わせ）。ローカル座標：+Z が奥（車から見て向こう側）、+Y が上。
/// </summary>
public static class Landmarks
{
    private static Scene scene = null!;
    private static Vector3 basePos;

    /// <summary>停車位置から名所の中心までの距離（m）。</summary>
    public static float Offset(LandmarkType t) => t switch
    {
        LandmarkType.Mountain => 650,
        LandmarkType.Volcano => 700,
        LandmarkType.Lake => 150,
        LandmarkType.Tower => 120,
        LandmarkType.Bridge => 90,
        LandmarkType.Castle => 85,
        LandmarkType.Temple => 70,
        LandmarkType.Pagoda => 60,
        LandmarkType.Buddha => 65,
        LandmarkType.Lighthouse => 65,
        LandmarkType.Garden => 55,
        _ => 45,
    };

    /// <summary>カメラを向けるおおよその高さ（m）。</summary>
    public static float Height(LandmarkType t) => t switch
    {
        LandmarkType.Mountain => 160,
        LandmarkType.Volcano => 150,
        LandmarkType.Tower => 55,
        LandmarkType.Castle => 30,
        LandmarkType.Bridge => 15,
        LandmarkType.Lake => 5,
        LandmarkType.Garden => 4,
        LandmarkType.Buddha => 22,
        LandmarkType.Pagoda => 20,
        LandmarkType.Lighthouse => 18,
        LandmarkType.Temple => 12,
        _ => 8,
    };

    // ---------------- 部品 ----------------

    private static Color C(Color c) => scene.LandmarkFog(c, basePos);

    private static void Box(float x, float y, float z, float w, float h, float d, Color c) =>
        Raylib.DrawCube(new Vector3(x, y, z), w, h, d, C(c));

    private static void Cyl(float x, float y, float z, float rTop, float rBottom, float h, int slices, Color c) =>
        Raylib.DrawCylinder(new Vector3(x, y, z), rTop, rBottom, h, slices, C(c));

    private static void Ball(float x, float y, float z, float r, Color c) =>
        Raylib.DrawSphere(new Vector3(x, y, z), r, C(c));

    /// <summary>四角い屋根（寄棟）や石垣：底辺 w×d、高さ h、上面は底辺の top 倍。</summary>
    private static void Roof(float x, float y, float z, float w, float d, float h, float top, Color c)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x, y, z);
        Rlgl.Scalef(w / 2, 1, d / 2);
        Rlgl.Rotatef(45, 0, 1, 0);
        Raylib.DrawCylinder(Vector3.Zero, top * 1.4142f, 1.4142f, h, 4, C(c));
        Rlgl.PopMatrix();
    }

    /// <summary>XY平面上の2点を結ぶ板（アーチやケーブル用）。</summary>
    private static void Segment(float x1, float y1, float x2, float y2, float z, float thick, float depth, Color c)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        Rlgl.PushMatrix();
        Rlgl.Translatef((x1 + x2) / 2, (y1 + y2) / 2, z);
        Rlgl.Rotatef(MathF.Atan2(dy, dx) * 180 / MathF.PI, 0, 0, 1);
        Raylib.DrawCube(Vector3.Zero, len + thick * 0.5f, thick, depth, C(c));
        Rlgl.PopMatrix();
    }

    private static void Water(float x, float z, float r, Color? c = null) =>
        Cyl(x, 0.06f, z, r, r, 0.15f, 32, c ?? Rgb(55, 120, 170));

    private static void Pine(float x, float z, float h)
    {
        Cyl(x, 0, z, 0.3f, 0.4f, h * 0.4f, 5, Rgb(90, 65, 45));
        Cyl(x, h * 0.3f, z, 0, h * 0.35f, h * 0.75f, 7, Rgb(35, 90, 50));
    }

    private static readonly Color Red = Rgb(215, 55, 40);
    private static readonly Color White = Rgb(238, 238, 234);
    private static readonly Color Stone = Rgb(150, 145, 135);
    private static readonly Color RoofDark = Rgb(62, 66, 72);
    private static readonly Color Wood = Rgb(150, 105, 65);
    private static readonly Color Gold = Rgb(220, 180, 60);

    // ---------------- 描画 ----------------

    public static void Draw(Scene sc, Spot spot, Vector3 pos, float time)
    {
        scene = sc;
        basePos = pos;
        Rlgl.PushMatrix();
        Rlgl.Translatef(pos.X, pos.Y, pos.Z);
        switch (spot.Type)
        {
            case LandmarkType.Tower: Tower(spot.Variant); break;
            case LandmarkType.Castle: Castle(spot.Variant); break;
            case LandmarkType.Torii: Torii(spot.Variant); break;
            case LandmarkType.Pagoda: Pagoda(); break;
            case LandmarkType.Temple: Temple(spot.Variant); break;
            case LandmarkType.Mountain: Mountain(spot.Variant); break;
            case LandmarkType.Volcano: Volcano(spot.Variant, time); break;
            case LandmarkType.ClockTower: ClockTower(spot.Variant); break;
            case LandmarkType.Lighthouse: Lighthouse(spot.Variant); break;
            case LandmarkType.Bridge: Bridge(spot.Variant); break;
            case LandmarkType.Buddha: Buddha(); break;
            case LandmarkType.Garden: Garden(); break;
            case LandmarkType.Lake: Lake(spot.Variant, time); break;
            case LandmarkType.Monument: Monument(spot.Variant); break;
        }
        Rlgl.PopMatrix();
    }

    private static void Tower(int v)
    {
        switch (v)
        {
            case 0: // 東京タワー：赤白の鉄塔
            {
                const float H = 110;
                for (int i = 0; i < 10; i++)
                {
                    float y0 = i * H / 10, h = H / 10;
                    float w = 34 * MathF.Pow(1 - i / 10f, 1.6f) + 2.5f;
                    var col = i % 2 == 0 ? Rgb(225, 85, 40) : White;
                    if (i < 3)
                    {
                        float cw = w * 0.22f;
                        foreach (float sx in new[] { -1f, 1f })
                            foreach (float sz in new[] { -1f, 1f })
                                Box(sx * (w / 2 - cw / 2), y0 + h / 2, sz * (w / 2 - cw / 2), cw, h, cw, col);
                        Box(0, y0 + h - 0.6f, 0, w, 1.2f, w, col);
                    }
                    else Box(0, y0 + h / 2, 0, w, h, w, col);
                }
                Box(0, H * 0.42f, 0, 20, 5, 20, Rgb(200, 205, 215));
                Box(0, H * 0.72f, 0, 9, 3, 9, Rgb(200, 205, 215));
                Cyl(0, H, 0, 0.2f, 0.8f, 18, 6, White);
                break;
            }
            case 1: // 細身のタワー（五稜郭タワー・福岡タワー）
                Cyl(0, 0, 0, 5, 8, 75, 6, Rgb(195, 205, 215));
                Cyl(0, 48, 0, 10, 9, 8, 10, Rgb(150, 175, 200));
                Cyl(0, 75, 0, 0.3f, 1.5f, 15, 6, White);
                Box(0, 2, 0, 24, 4, 20, Rgb(210, 210, 205));
                break;
            case 2: // 通天閣
                foreach (float sx in new[] { -1f, 1f })
                    foreach (float sz in new[] { -1f, 1f })
                        Box(sx * 7, 7, sz * 7, 2.5f, 14, 2.5f, Rgb(170, 175, 180));
                Box(0, 14.5f, 0, 18, 2, 18, Rgb(170, 175, 180));
                Box(0, 36, 0, 10, 42, 10, Rgb(185, 190, 195));
                Box(0, 40, -5.1f, 8, 6, 0.3f, Rgb(250, 210, 40));
                Box(0, 52, 0, 14, 7, 14, Rgb(200, 205, 210));
                Cyl(0, 55.5f, 0, 0.3f, 1.2f, 14, 6, White);
                break;
            default: // 神戸ポートタワー：赤い鼓型
                Cyl(0, 0, 0, 3.5f, 9, 45, 16, Red);
                Cyl(0, 45, 0, 10, 3.5f, 40, 16, Red);
                Cyl(0, 85, 0, 10, 10, 6, 16, White);
                Cyl(0, 91, 0, 0.3f, 1.5f, 12, 6, White);
                break;
        }
    }

    private static void Castle(int v)
    {
        if (v == 2)
        {
            // 首里城：曲線の石垣と赤い正殿
            Box(0, 2.5f, 0, 70, 5, 50, Rgb(205, 190, 160));
            Box(0, 5.4f, 0, 66, 0.8f, 46, Rgb(185, 175, 150));
            Box(0, 10, 8, 34, 8, 16, Rgb(200, 40, 35));
            Roof(0, 14, 8, 38, 20, 3.2f, 0.6f, Rgb(200, 70, 50));
            Box(0, 18.5f, 8, 26, 3.5f, 10, Rgb(200, 40, 35));
            Roof(0, 20.2f, 8, 30, 14, 4, 0.15f, Rgb(200, 70, 50));
            Box(0, 9, -1.2f, 10, 6, 2, Rgb(200, 40, 35));        // 唐破風の正面
            Roof(0, 12, -1.2f, 13, 4, 2.5f, 0, Rgb(200, 70, 50));
            Box(0, 6.5f, -26, 12, 3, 4, Rgb(200, 40, 35));        // 守礼門
            Roof(0, 8, -26, 15, 6, 2.5f, 0.2f, Rgb(200, 70, 50));
            foreach (float x in new[] { -42f, 42f }) Pine(x, 0, 12);
            return;
        }
        var wall = v == 1 ? Rgb(45, 42, 45) : White;
        Roof(0, 0, 0, 44, 38, 13, 0.82f, Stone);                         // 石垣
        for (int t = 0; t < 4; t++)
        {
            float w = 27 - t * 5.2f, d = 23 - t * 4.6f;
            float y = 13 + t * 8.6f;
            Box(0, y + 3, 0, w, 6, d, wall);
            if (v == 1) Box(0, y + 5.4f, 0, w + 0.1f, 1.2f, d + 0.1f, White);
            Roof(0, y + 6, 0, w + 5.5f, d + 5.5f, 3.4f, 0.55f, Rgb(70, 82, 88));
            // 千鳥破風（三角の飾り屋根）
            if (t is 1 or 2) Roof(0, y + 6.6f, -(d / 2 + 1.5f), w * 0.45f, 3, 3, 0, Rgb(70, 82, 88));
        }
        float top = 13 + 4 * 8.6f;
        Box(-3.5f, top + 1, 0, 0.8f, 2, 1.6f, Gold);                     // 金のしゃちほこ
        Box(3.5f, top + 1, 0, 0.8f, 2, 1.6f, Gold);
        foreach (float x in new[] { -35f, 35f }) Pine(x, -10, 12);
    }

    private static void ToriiGate(float z, float half, float h, Color col)
    {
        Cyl(-half, 0, z, h * 0.05f, h * 0.06f, h, 10, col);
        Cyl(half, 0, z, h * 0.05f, h * 0.06f, h, 10, col);
        Box(0, h * 0.8f, z, half * 2 + h * 0.15f, h * 0.05f, h * 0.07f, col);            // 貫
        Box(0, h * 1.0f, z, half * 2 + h * 0.4f, h * 0.07f, h * 0.12f, col);             // 島木
        Box(0, h * 1.07f, z, half * 2 + h * 0.55f, h * 0.07f, h * 0.14f, Rgb(30, 30, 30)); // 笠木
    }

    private static void Shrine(float z, float scale)
    {
        Box(0, 1, z, 22 * scale, 2, 16 * scale, Stone);
        Box(0, 2 + 3.5f * scale, z, 16 * scale, 7 * scale, 10 * scale, Red);
        Box(0, 2 + 3.5f * scale, z - 5 * scale - 0.05f, 13 * scale, 5 * scale, 0.1f, White);
        Roof(0, 2 + 7 * scale, z, 22 * scale, 16 * scale, 5 * scale, 0.2f, Rgb(70, 95, 80));
    }

    private static void Torii(int v)
    {
        switch (v)
        {
            case 1: // 伏見稲荷：千本鳥居
                for (int i = 0; i < 14; i++) ToriiGate(-12 + i * 2.6f, 2.6f, 6.5f, Rgb(235, 70, 30));
                Shrine(40, 1);
                break;
            case 2: // 厳島神社：海に浮かぶ大鳥居
                Water(0, 50, 85, Rgb(60, 130, 160));
                ToriiGate(-5, 8, 16, Rgb(225, 60, 40));
                Box(0, 3, 45, 70, 6, 10, Red);
                Roof(0, 6, 45, 74, 13, 4, 0.3f, Rgb(70, 75, 70));
                Box(-30, 3, 60, 10, 6, 30, Red);
                Box(30, 3, 60, 10, 6, 30, Red);
                Cyl(0, 0, 160, 0, 130, 90, 10, Rgb(70, 110, 75));      // 弥山
                break;
            default:
                ToriiGate(-6, 5, 12, Red);
                Shrine(22, 1);
                foreach (float x in new[] { -22f, 22f, -28f, 28f }) Pine(x, 10 + Math.Abs(x) * 0.3f, 14);
                break;
        }
    }

    private static void Pagoda()
    {
        Box(0, 0.6f, 0, 15, 1.2f, 15, Stone);
        for (int i = 0; i < 5; i++)
        {
            float size = 9 - i * 1.1f;
            float y = 1.2f + i * 6;
            Box(0, y + 2.2f, 0, size, 4.4f, size, Rgb(165, 60, 40));
            Roof(0, y + 4.4f, 0, size + 5.5f, size + 5.5f, 1.7f, 0.7f, RoofDark);
        }
        Cyl(0, 31.2f, 0, 0.25f, 0.4f, 10, 6, Rgb(170, 140, 70));
        for (int i = 0; i < 5; i++) Cyl(0, 33 + i * 1.4f, 0, 0.9f, 0.9f, 0.3f, 8, Rgb(170, 140, 70));
        Pine(-16, 4, 12);
        Pine(15, -2, 10);
    }

    private static void Temple(int v)
    {
        if (v == 1)
        {
            // 道後温泉本館：木造の重なった屋根と小さな塔
            Box(0, 4, 0, 30, 8, 22, Wood);
            Roof(0, 8, 0, 33, 25, 3, 0.75f, RoofDark);
            Box(0, 12, 0, 22, 5, 15, Wood);
            Roof(0, 14.5f, 0, 25, 18, 3, 0.6f, RoofDark);
            Box(0, 18.5f, 0, 6, 4, 6, Wood);
            Roof(0, 20.5f, 0, 8, 8, 3, 0, RoofDark);
            Box(0, 3, -11.2f, 8, 5, 0.3f, Rgb(80, 50, 30));
            return;
        }
        // 本堂と山門（浅草寺・善光寺など）
        Box(0, 1, 0, 42, 2, 30, Stone);
        Box(0, 7, 0, 32, 10, 20, Rgb(190, 55, 40));
        Box(0, 6, -10.1f, 26, 7, 0.2f, Rgb(120, 40, 30));
        Roof(0, 12, 0, 46, 34, 10, 0.28f, RoofDark);
        Box(0, 22.3f, 0, 14, 1, 1.4f, RoofDark);
        Box(0, 1.2f, -17, 12, 1.4f, 5, Stone);
        // 山門と大提灯
        Box(0, 5, -32, 16, 10, 7, Rgb(190, 55, 40));
        Roof(0, 10, -32, 22, 12, 5, 0.3f, RoofDark);
        Cyl(0, 2.5f, -35.8f, 1.6f, 1.6f, 3.2f, 12, Rgb(200, 40, 35));
        Pine(-26, -20, 11);
        Pine(26, -20, 11);
    }

    private static void Mountain(int v)
    {
        if (v == 1)
        {
            // 富士山：左右対称の裾野と大きな雪
            Cyl(0, 0, 0, 32, 440, 340, 28, Rgb(78, 98, 140));
            Cyl(0, 210, 0, 32, 150, 130, 28, Rgb(245, 248, 252));
            return;
        }
        Cyl(0, 0, 0, 0, 270, 210, 11, Rgb(80, 108, 88));
        Cyl(0, 160, 0, 0, 66, 50, 11, Rgb(240, 242, 245));
        Cyl(-190, 0, 60, 0, 200, 140, 9, Rgb(75, 100, 85));
        Cyl(210, 0, 90, 0, 180, 120, 9, Rgb(75, 100, 85));
    }

    private static void Volcano(int v, float time)
    {
        var steam = new Color((byte)235, (byte)235, (byte)235, (byte)170);
        if (v == 1)
        {
            // 別府：湯けむりと青い地獄の池
            Water(0, 0, 22, Rgb(40, 160, 200));
            Cyl(0, 0, 140, 0, 160, 80, 10, Rgb(75, 110, 80));
            for (int k = 0; k < 6; k++)
            {
                float x = -40 + k * 16, z = 30 + (k % 2) * 20;
                Box(x, 4, z, 10, 8, 8, Rgb(200, 190, 175));
                for (int i = 0; i < 4; i++)
                {
                    float rise = (time * 3 + i * 6 + k * 2) % 24;
                    Raylib.DrawSphere(new Vector3(x + MathF.Sin(time + i) * 1.5f, 8 + rise, z), 2 + rise * 0.25f, C(steam));
                }
            }
            return;
        }
        // 桜島：海に浮かぶ火山と噴煙
        Water(0, 0, 420, Rgb(50, 110, 160));
        Cyl(0, 0, 0, 28, 330, 200, 14, Rgb(110, 95, 85));
        Cyl(0, 200, 0, 22, 28, 4, 14, Rgb(80, 70, 65));
        for (int i = 0; i < 8; i++)
        {
            float rise = (time * 5 + i * 18) % 144;
            Raylib.DrawSphere(new Vector3(MathF.Sin(i * 1.7f) * 6 + rise * 0.3f, 208 + rise, 0), 14 + rise * 0.18f, C(steam));
        }
    }

    private static void ClockTower(int v)
    {
        if (v == 1)
        {
            // 大浦天主堂：白い教会と尖塔
            Box(0, 6, 8, 14, 12, 26, White);
            Roof(0, 12, 8, 15, 27, 5, 0.05f, Rgb(80, 90, 95));
            Box(0, 10, -6, 6, 20, 6, White);
            Cyl(0, 20, -6, 0, 3.6f, 11, 8, Rgb(60, 95, 80));
            Box(0, 32.5f, -6, 0.3f, 3, 0.3f, Gold);
            Box(0, 33, -6, 1.6f, 0.3f, 0.3f, Gold);
            return;
        }
        // 札幌市時計台
        Box(0, 5, 4, 22, 10, 14, White);
        Roof(0, 10, 4, 24, 16, 5, 0.1f, Rgb(170, 55, 50));
        Box(0, 15, -1, 5.5f, 10, 5.5f, White);
        Roof(0, 20, -1, 6.5f, 6.5f, 4, 0, Rgb(170, 55, 50));
        var face = new Vector3(0, 16, -3.8f);
        Raylib.DrawCylinderEx(face, face + new Vector3(0, 0, -0.15f), 1.9f, 1.9f, 20, C(White));
        Raylib.DrawLine3D(face + new Vector3(0, 0, -0.2f), face + new Vector3(0, 1.4f, -0.2f), C(Rgb(20, 20, 20)));
        Raylib.DrawLine3D(face + new Vector3(0, 0, -0.2f), face + new Vector3(1.0f, 0, -0.2f), C(Rgb(20, 20, 20)));
        Box(0, 2, -3.2f, 3, 4, 0.2f, Wood);
    }

    private static void Lighthouse(int v)
    {
        if (v == 1)
        {
            // 鞆の浦の常夜燈：港の石灯籠
            Water(0, 40, 70);
            Box(0, 1.5f, 0, 16, 3, 10, Stone);
            Box(0, 4.5f, 0, 6, 3, 6, Stone);
            Box(0, 9, 0, 3, 6, 3, Stone);
            Box(0, 13.5f, 0, 4, 3, 4, Wood);
            Roof(0, 15, 0, 7, 7, 2.8f, 0, RoofDark);
            Box(-20, 0.8f, 30, 3, 1.2f, 8, Wood);   // 小舟
            Box(18, 0.8f, 40, 3, 1.2f, 7, Wood);
            return;
        }
        Water(0, 75, 100);
        Cyl(0, 0, 0, 16, 26, 6, 10, Rgb(110, 115, 95));
        Cyl(0, 6, 0, 2.4f, 3.8f, 24, 16, White);
        Cyl(0, 30, 0, 4, 4, 0.7f, 16, Rgb(40, 40, 45));
        Cyl(0, 30.7f, 0, 2.2f, 2.2f, 3, 12, Rgb(210, 225, 235));
        Ball(0, 34.2f, 0, 2.3f, Rgb(50, 50, 55));
    }

    private static void Bridge(int v)
    {
        var river = Rgb(60, 120, 160);
        Box(0, 0.05f, 15, 360, 0.1f, 70, river);
        switch (v)
        {
            case 0: // 萬代橋：石造りの6連アーチ
            {
                const float span = 22;
                Box(0, 7.5f, 0, span * 6 + 6, 1.4f, 12, Stone);
                for (int j = 0; j < 6; j++)
                {
                    float xc = -span * 2.5f + j * span, r = span * 0.42f;
                    for (int k = 0; k < 8; k++)
                    {
                        float a1 = MathF.PI * k / 8, a2 = MathF.PI * (k + 1) / 8;
                        Segment(xc - r * MathF.Cos(a1), r * MathF.Sin(a1) * 0.65f, xc - r * MathF.Cos(a2), r * MathF.Sin(a2) * 0.65f, 0, 1.2f, 11.5f, Stone);
                    }
                    Box(xc + span / 2, 3.5f, 0, span * 0.16f, 7, 11.5f, Stone);
                }
                for (int i = -3; i <= 3; i++) Cyl(i * 22, 8.2f, -5.5f, 0.3f, 0.3f, 4, 6, Rgb(60, 60, 60)); // 街灯
                break;
            }
            case 1: // 錦帯橋：木造の5連アーチ
            {
                const float span = 32;
                for (int j = 0; j < 5; j++)
                {
                    float x0 = -span * 2.5f + j * span;
                    float rise = j is 0 or 4 ? 5 : 8;
                    for (int k = 0; k < 10; k++)
                    {
                        float t1 = k / 10f, t2 = (k + 1) / 10f;
                        Segment(x0 + span * t1, 4 + rise * MathF.Sin(MathF.PI * t1), x0 + span * t2, 4 + rise * MathF.Sin(MathF.PI * t2), 0, 0.9f, 5, Wood);
                    }
                    Box(x0, 2, 0, 4, 4, 7, Stone);
                }
                Box(span * 2.5f, 2, 0, 4, 4, 7, Stone);
                Cyl(0, 0, 220, 0, 160, 90, 10, Rgb(70, 110, 75));
                break;
            }
            default: // 来島海峡大橋：吊り橋（大きいので奥に置く）
            {
                Rlgl.PushMatrix();
                Rlgl.Translatef(0, 0, 150);
                Box(0, 0.05f, 0, 600, 0.1f, 360, Rgb(55, 115, 165));
                Box(0, 24, 0, 300, 2.5f, 10, White);
                foreach (float tx in new[] { -60f, 60f })
                {
                    Box(tx, 36, -4.5f, 3, 72, 3, White);
                    Box(tx, 36, 4.5f, 3, 72, 3, White);
                    Box(tx, 70, 0, 3, 3, 12, White);
                }
                foreach (float z in new[] { -4.5f, 4.5f })
                {
                    float prevX = -60, prevY = 71;
                    for (int k = 1; k <= 12; k++)
                    {
                        float x = -60 + 120 * k / 12f;
                        float y = 71 - 44 * (1 - MathF.Pow((x / 60), 2));
                        Segment(prevX, prevY, x, y, z, 0.6f, 0.6f, White);
                        prevX = x; prevY = y;
                    }
                    Segment(-60, 71, -150, 26, z, 0.6f, 0.6f, White);
                    Segment(60, 71, 150, 26, z, 0.6f, 0.6f, White);
                }
                Cyl(-170, 0, 30, 0, 60, 40, 9, Rgb(70, 110, 75));
                Cyl(180, 0, 60, 0, 80, 50, 9, Rgb(70, 110, 75));
                Rlgl.PopMatrix();
                break;
            }
        }
    }

    private static void Buddha()
    {
        Box(0, 3, 0, 12, 6, 12, Stone);
        Cyl(0, 6, 0, 3.4f, 6.2f, 26, 18, White);
        Ball(0, 31.5f, 0, 4.4f, White);
        Ball(0, 37.5f, 0, 3.1f, White);
        Cyl(0, 39.5f, 0, 1.4f, 2.4f, 2.4f, 10, White);
        Box(0, 22, -3.6f, 3.5f, 1.4f, 1.6f, White);   // 合わせた手
        for (int i = 0; i < 6; i++) Pine(-30 + i * 12, 20 + (i % 2) * 8, 10 + i % 3 * 2);
    }

    private static void Garden()
    {
        Water(0, 0, 24, Rgb(70, 130, 150));
        Cyl(6, 0.1f, 4, 4, 5.5f, 1, 12, Rgb(80, 120, 70));
        Pine(6, 4, 5);
        for (int i = 0; i < 10; i++)
        {
            float a = i * MathF.PI * 2 / 10 + 0.3f;
            float x = MathF.Cos(a) * 33, z = MathF.Sin(a) * 30;
            if (i % 3 == 0) Ball(x, 3.5f, z, 3, Rgb(240, 170, 190));   // 梅・桜
            else Pine(x, z, 8);
        }
        // 雪見灯籠
        Cyl(-12, 0, -16, 0.3f, 0.3f, 1.8f, 6, Stone);
        Roof(-12, 1.8f, -16, 2.6f, 2.6f, 0.8f, 0.2f, Stone);
        // 茶室
        Box(0, 2, 32, 10, 4, 8, Wood);
        Roof(0, 4, 32, 13, 11, 3, 0.15f, Rgb(140, 120, 80));
        // 赤い太鼓橋
        for (int k = 0; k < 6; k++)
            Segment(-6 + 2 * k, 0.4f + 1.8f * MathF.Sin(MathF.PI * k / 6), -4 + 2 * k, 0.4f + 1.8f * MathF.Sin(MathF.PI * (k + 1) / 6), -20, 0.3f, 2.5f, Red);
    }

    private static void Lake(int v, float time)
    {
        switch (v)
        {
            case 1: // 湿原：草原と蛇行する川
                Cyl(0, 0.05f, 0, 170, 170, 0.1f, 32, Rgb(150, 160, 90));
                for (int k = 0; k < 14; k++)
                {
                    float z1 = -120 + k * 18, z2 = z1 + 18;
                    float x1 = MathF.Sin(k * 0.8f) * 25, x2 = MathF.Sin((k + 1) * 0.8f) * 25;
                    Rlgl.PushMatrix();
                    Rlgl.Translatef((x1 + x2) / 2, 0.15f, (z1 + z2) / 2);
                    Rlgl.Rotatef(MathF.Atan2(x2 - x1, z2 - z1) * 180 / MathF.PI, 0, 1, 0);
                    Raylib.DrawCube(Vector3.Zero, 7, 0.1f, 20, C(Rgb(60, 110, 150)));
                    Rlgl.PopMatrix();
                }
                Box(0, 3, -135, 10, 0.8f, 8, Wood);   // 展望台
                Cyl(-200, 0, 250, 0, 160, 90, 9, Rgb(80, 105, 90));
                break;
            case 2: // 鳴門の渦潮と大鳴門橋
                Water(0, 60, 190, Rgb(40, 110, 140));
                for (int i = 0; i < 4; i++)
                {
                    float r = 8 + i * 9 + (time * 4 % 9);
                    Raylib.DrawCircle3D(new Vector3(0, 0.3f, 0), r, Vector3.UnitX, 90, C(Rgb(230, 240, 245)));
                    Raylib.DrawCircle3D(new Vector3(40, 0.3f, 50), r * 0.7f, Vector3.UnitX, 90, C(Rgb(230, 240, 245)));
                }
                Box(0, 40, 90, 400, 3, 12, White);
                Box(-70, 40, 90, 4, 80, 4, White);
                Box(70, 40, 90, 4, 80, 4, White);
                break;
            default: // 湖と周りの山
                Water(0, 0, 140);
                Box(0, 0.5f, -130, 4, 0.4f, 24, Wood);   // 桟橋
                Box(2, 0.9f, -110, 3, 1, 6, White);     // ボート
                Cyl(-160, 0, 200, 0, 170, 110, 10, Rgb(70, 105, 80));
                Cyl(40, 0, 260, 0, 190, 130, 10, Rgb(75, 100, 85));
                Cyl(220, 0, 170, 0, 150, 90, 10, Rgb(70, 105, 80));
                break;
        }
    }

    private static void Monument(int v)
    {
        switch (v)
        {
            case 1: // 吉野ヶ里：物見やぐらと竪穴住居
                foreach (float sx in new[] { -1f, 1f })
                    foreach (float sz in new[] { -1f, 1f })
                        Cyl(sx * 3, 0, sz * 3, 0.3f, 0.35f, 12, 6, Wood);
                Box(0, 10, 0, 8, 0.6f, 8, Wood);
                Roof(0, 12, 0, 9, 9, 5, 0, Rgb(170, 150, 100));
                foreach (float x in new[] { -16f, 15f, -8f })
                    Cyl(x, 0, 14 + Math.Abs(x) * 0.3f, 0, 6, 5.5f, 10, Rgb(170, 150, 100));
                for (int i = 0; i < 30; i++)
                {
                    float a = i * MathF.PI * 2 / 30;
                    Box(MathF.Cos(a) * 28, 1.5f, MathF.Sin(a) * 28 + 6, 0.4f, 3, 0.4f, Wood);
                }
                break;
            case 2: // 幸福駅：小さな木造駅舎とホーム
                Box(0, 0.5f, -2, 30, 1, 4, Stone);
                Box(0, 2.5f, 4, 7, 5, 5, Wood);
                Roof(0, 5, 4, 8.5f, 6.5f, 2.5f, 0.1f, Rgb(170, 60, 50));
                foreach (float z in new[] { -6.2f, -7.8f }) Box(0, 0.15f, z, 60, 0.15f, 0.15f, Rgb(120, 110, 100));
                Box(-6, 2.5f, -1, 3, 1.2f, 0.2f, Rgb(250, 200, 200));  // 駅名看板
                Box(-6, 1.2f, -1, 0.2f, 2.4f, 0.2f, Wood);
                break;
            default: // 騎馬像
                Box(0, 4, 0, 6, 8, 7, Stone);
                var bronze = Rgb(70, 82, 72);
                Box(0, 10.4f, 0, 1.6f, 2, 4.4f, bronze);
                foreach (float sx in new[] { -0.5f, 0.5f })
                    foreach (float sz in new[] { -1.6f, 1.6f })
                        Box(sx, 8.7f, sz, 0.35f, 1.6f, 0.35f, bronze);
                Segment(0, 11, 0, 12.3f, 2.4f, 0.8f, 0.8f, bronze);
                Box(0, 12.9f, 2.6f, 0.8f, 0.8f, 1.6f, bronze);       // 馬の頭
                Cyl(0, 11.2f, 0, 0.5f, 0.75f, 2.4f, 8, bronze);      // 武将
                Ball(0, 14.1f, 0, 0.55f, bronze);
                Box(0, 14.9f, 0, 1.8f, 0.15f, 0.2f, Gold);          // 三日月の兜
                foreach (float x in new[] { -16f, 16f }) Pine(x, 6, 10);
                break;
        }
    }
}
