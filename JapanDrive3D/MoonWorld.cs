using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// 月面ワールド。大小のクレーターと起伏のあるレゴリス地形、
/// 満天の星空、空に浮かぶ青い地球、着陸基地（静かの海ベース）を描画する。
/// 座標系は FreeWorld と同じく、プレイヤー位置を原点とする相対座標。
/// </summary>
public sealed class MoonWorld
{
    public double PX, PZ;
    public float Altitude;

    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.55f, 0.72f, -0.42f));

    private readonly struct StarPoint
    {
        public readonly Vector3 Dir;
        public readonly float Size;
        public readonly Color Col;

        public StarPoint(Vector3 dir, float size, Color col)
        {
            Dir = dir;
            Size = size;
            Col = col;
        }
    }

    private readonly StarPoint[] stars;

    public MoonWorld()
    {
        stars = BuildStars(420);
    }

    /// <summary>月面座標 → 描画座標（プレイヤー位置基準）。</summary>
    public Vector3 R(double x, float y, double z) => new((float)(PX - x), y, (float)(z - PZ));

    private static ulong Hash2(long x, long z, ulong salt = 0)
    {
        ulong h = (ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)z * 0xBF58476D1CE4E5B9UL ^ salt;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        return h ^ (h >> 31);
    }

    private static double Hash01(long x, long z, ulong salt = 0) => (Hash2(x, z, salt) & 0xFFFFFFUL) / (double)0x1000000UL;

    private static StarPoint[] BuildStars(int count)
    {
        var arr = new StarPoint[count];
        for (int i = 0; i < count; i++)
        {
            double u = Hash01(i, 17, 101) * 2.0 - 1.0;
            double theta = Hash01(i, 31, 202) * Math.PI * 2.0;
            // 地平線より少し上を中心に全天に配置
            double y = -0.15 + Hash01(i, 47, 303) * 1.15;
            double rr = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
            var dir = Vector3.Normalize(new Vector3((float)(rr * Math.Cos(theta)), (float)y, (float)(rr * Math.Sin(theta))));
            float size = 1.6f + (float)Hash01(i, 73, 404) * 2.8f;
            double tint = Hash01(i, 89, 505);
            int bri = 185 + (int)(Hash01(i, 107, 606) * 70);
            Color col = tint < 0.25 ? new Color((byte)Math.Min(255, bri), (byte)Math.Min(255, bri), (byte)255, (byte)255)
                : tint < 0.45 ? new Color((byte)255, (byte)Math.Min(255, bri), (byte)Math.Max(150, bri - 30), (byte)255)
                : new Color((byte)bri, (byte)bri, (byte)bri, (byte)255);
            arr[i] = new StarPoint(dir, size, col);
        }
        return arr;
    }

    /// <summary>指定座標の月面高さ（m）。着陸基地周辺 (0, 0) は平坦で、外側に大小のクレーターと丘が広がる。</summary>
    public float SurfaceY(double x, double z)
    {
        double baseDist = Math.Sqrt(x * x + z * z);
        double flatBlend = Math.Clamp((baseDist - 52.0) / 48.0, 0.0, 1.0);
        flatBlend = flatBlend * flatBlend * (3.0 - 2.0 * flatBlend);
        if (flatBlend <= 0.0001) return 0f;

        // 緩やかな大きなうねり（丘と谷）
        double h = Math.Sin(x * 0.0065) * Math.Cos(z * 0.0058) * 9.5
                 + Math.Sin((x + z) * 0.014) * 3.2
                 + Math.Cos((x - z * 0.7) * 0.028) * 1.4;

        // 大クレーター（260m セル）＋ 中小クレーター（95m セル）
        h += CraterLayer(x, z, 260.0, 42.0, 88.0, 12.0, 0x1111UL);
        h += CraterLayer(x, z, 95.0, 14.0, 32.0, 5.2, 0x2222UL);

        return (float)(h * flatBlend);
    }

    private static double CraterLayer(double x, double z, double cell, double minR, double maxR, double maxDepth, ulong salt)
    {
        long cx = (long)Math.Floor(x / cell);
        long cz = (long)Math.Floor(z / cell);
        double total = 0.0;
        for (long ix = cx - 1; ix <= cx + 1; ix++)
        {
            for (long iz = cz - 1; iz <= cz + 1; iz++)
            {
                if (Hash01(ix, iz, salt) > 0.62) continue;
                double ox = (ix + 0.2 + Hash01(ix, iz, salt + 1) * 0.6) * cell;
                double oz = (iz + 0.2 + Hash01(ix, iz, salt + 2) * 0.6) * cell;
                double r = minR + Hash01(ix, iz, salt + 3) * (maxR - minR);
                double dx = x - ox, dz = z - oz;
                double d2 = dx * dx + dz * dz;
                double outer = r * 1.32;
                if (d2 >= outer * outer) continue;

                double d = Math.Sqrt(d2) / r;
                double depth = maxDepth * (0.55 + 0.45 * (r - minR) / Math.Max(1.0, maxR - minR));
                if (d < 0.82)
                {
                    // ボウル状のクレーター内部
                    double t = d / 0.82;
                    total += -depth * (1.0 - t * t) + depth * 0.22 * t * t;
                }
                else
                {
                    // 盛り上がったクレーターの縁（リム：月面ジャンプ台になる）
                    double t = (d - 0.82) / 0.50;
                    double rim = Math.Cos(t * Math.PI * 0.5);
                    total += depth * 0.22 * rim * rim;
                }
            }
        }
        return total;
    }

    /// <summary>月面基地の居住ドーム等との当たり判定。</summary>
    public bool HitObstacle(double x, double z, float y, float radius)
    {
        // 居住ドーム（メイン: (-28, 18), サブ: (-42, 6)）
        if (y < 9.5f && Dist2(x, z, -28, 18) < (8.5 + radius) * (8.5 + radius)) return true;
        if (y < 7.0f && Dist2(x, z, -42, 6) < (5.5 + radius) * (5.5 + radius)) return true;
        return false;
    }

    private static double Dist2(double x0, double z0, double x1, double z1)
    {
        double dx = x0 - x1, dz = z0 - z1;
        return dx * dx + dz * dz;
    }

    private Color RegolithColor(double x, double z, float h)
    {
        float eps = 3.0f;
        float hx = SurfaceY(x + eps, z) - SurfaceY(x - eps, z);
        float hz = SurfaceY(x, z + eps) - SurfaceY(x, z - eps);
        var n = Vector3.Normalize(new Vector3(hx, 2f * eps, -hz));
        float ndl = Math.Clamp(Vector3.Dot(n, SunDir), 0f, 1f);
        // 大気散乱がないため陰影コントラストを強めつつ、地形が読める最低輝度を確保
        float shade = 0.28f + 0.72f * ndl;
        // 細かなレゴリスの濃淡（海と高地の色味差）
        float grain = (float)(Math.Sin(x * 0.045) * Math.Cos(z * 0.042) * 10.0 + Math.Sin((x - z) * 0.13) * 5.0);
        float baseTone = 146f + Math.Clamp(h * 1.4f, -22f, 26f) + grain;
        byte r = (byte)Math.Clamp(baseTone * shade, 26f, 222f);
        byte g = (byte)Math.Clamp((baseTone - 2f) * shade, 26f, 220f);
        byte b = (byte)Math.Clamp((baseTone + 3f) * shade, 28f, 226f);
        return new Color(r, g, b, (byte)255);
    }

    public void Draw(Vector3 cameraPos, float time)
    {
        DrawSpaceBackdrop(cameraPos, time);
        DrawTerrain();
        DrawLunarBase(time);
        DrawBoulders();
    }

    /// <summary>漆黒の宇宙空間に満天の星・青い地球・太陽を描く。</summary>
    private void DrawSpaceBackdrop(Vector3 cam, float time)
    {
        // 満天の星（カメラに追従する天球上）
        float starR = 1650f;
        Rlgl.Begin(DrawMode.Quads);
        foreach (var st in stars)
        {
            var c = cam + st.Dir * starR;
            var up = Math.Abs(st.Dir.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var right = Vector3.Normalize(Vector3.Cross(up, st.Dir)) * st.Size;
            var vUp = Vector3.Normalize(Vector3.Cross(st.Dir, right)) * st.Size;
            Rlgl.Color4ub(st.Col.R, st.Col.G, st.Col.B, st.Col.A);
            var p0 = c - right - vUp;
            var p1 = c + right - vUp;
            var p2 = c + right + vUp;
            var p3 = c - right + vUp;
            Rlgl.Vertex3f(p0.X, p0.Y, p0.Z);
            Rlgl.Vertex3f(p1.X, p1.Y, p1.Z);
            Rlgl.Vertex3f(p2.X, p2.Y, p2.Z);
            Rlgl.Vertex3f(p3.X, p3.Y, p3.Z);
        }
        Rlgl.End();

        // 空に浮かぶ青い地球（北の空・仰角約15度、着陸・走行中に正面やや右上に見える位置）
        var earthDir = Vector3.Normalize(new Vector3(-0.22f, 0.25f, 0.94f));
        var earthPos = cam + earthDir * 1250f;
        float earthR = 115f;
        // 淡い大気ハロー
        Raylib.DrawSphereEx(earthPos, earthR * 1.07f, 16, 20, new Color((byte)85, (byte)165, (byte)255, (byte)55));
        // 青い海
        Raylib.DrawSphereEx(earthPos, earthR, 18, 24, Rgb(26, 98, 208));
        // 大陸と雲のパッチ（ゆっくり自転）
        Rlgl.PushMatrix();
        Rlgl.Translatef(earthPos.X, earthPos.Y, earthPos.Z);
        Rlgl.Rotatef(time * 0.6f, 0.2f, 1f, 0f);
        Raylib.DrawSphereEx(new Vector3(earthR * 0.24f, earthR * 0.14f, -earthR * 0.22f), earthR * 0.78f, 12, 14, Rgb(56, 152, 82));
        Raylib.DrawSphereEx(new Vector3(-earthR * 0.26f, -earthR * 0.10f, -earthR * 0.18f), earthR * 0.74f, 12, 14, Rgb(170, 150, 102));
        Raylib.DrawSphereEx(new Vector3(0f, earthR * 0.42f, -earthR * 0.08f), earthR * 0.64f, 12, 14, new Color((byte)245, (byte)248, (byte)255, (byte)165));
        Raylib.DrawSphereEx(new Vector3(earthR * 0.14f, -earthR * 0.34f, -earthR * 0.16f), earthR * 0.68f, 12, 14, new Color((byte)240, (byte)246, (byte)255, (byte)140));
        Rlgl.PopMatrix();

        // 太陽（反対側の空）
        var sunPos = cam + SunDir * 1450f;
        Raylib.DrawSphereEx(sunPos, 32f, 12, 14, Rgb(255, 252, 232));
        Raylib.DrawSphereEx(sunPos, 54f, 12, 14, new Color((byte)255, (byte)245, (byte)200, (byte)70));
    }

    private void DrawTerrain()
    {
        // 近距離（高精細 12m グリッド、±288m）
        DrawTerrainRing(12.0, 24, 0);
        // 遠距離（広域 48m グリッド、±1440m、内側288mをスキップ）
        DrawTerrainRing(48.0, 30, 6);
    }

    private void DrawTerrainRing(double step, int halfCells, int skipInner)
    {
        long cx = (long)Math.Floor(PX / step);
        long cz = (long)Math.Floor(PZ / step);
        int n = halfCells * 2 + 1;
        var ys = new float[n, n];
        var cols = new Color[n, n];
        for (int ix = 0; ix < n; ix++)
        {
            double wx = (cx - halfCells + ix) * step;
            for (int iz = 0; iz < n; iz++)
            {
                double wz = (cz - halfCells + iz) * step;
                float h = SurfaceY(wx, wz);
                ys[ix, iz] = h;
                cols[ix, iz] = RegolithColor(wx, wz, h);
            }
        }

        Rlgl.Begin(DrawMode.Triangles);
        int quads = 0;
        for (int ix = 0; ix < n - 1; ix++)
        {
            int relX = ix - halfCells;
            double x0 = (cx - halfCells + ix) * step;
            double x1 = x0 + step;
            for (int iz = 0; iz < n - 1; iz++)
            {
                int relZ = iz - halfCells;
                if (skipInner > 0 && Math.Abs(relX) < skipInner && Math.Abs(relZ) < skipInner) continue;

                double z0 = (cz - halfCells + iz) * step;
                double z1 = z0 + step;
                var p00 = R(x0, ys[ix, iz], z0);
                var p10 = R(x1, ys[ix + 1, iz], z0);
                var p11 = R(x1, ys[ix + 1, iz + 1], z1);
                var p01 = R(x0, ys[ix, iz + 1], z1);

                var c00 = cols[ix, iz];
                var c10 = cols[ix + 1, iz];
                var c11 = cols[ix + 1, iz + 1];
                var c01 = cols[ix, iz + 1];

                EmitVert(p00, c00); EmitVert(p01, c01); EmitVert(p11, c11);
                EmitVert(p00, c00); EmitVert(p11, c11); EmitVert(p10, c10);

                if (++quads >= 1800)
                {
                    Rlgl.End();
                    Rlgl.Begin(DrawMode.Triangles);
                    quads = 0;
                }
            }
        }
        Rlgl.End();
    }

    private static void EmitVert(Vector3 p, Color c)
    {
        Rlgl.Color4ub(c.R, c.G, c.B, c.A);
        Rlgl.Vertex3f(p.X, p.Y, p.Z);
    }

    /// <summary>静かの海 月面基地（着陸パッド・居住ドーム・ソーラーパネル・通信アンテナ・日本の旗）。</summary>
    private void DrawLunarBase(float time)
    {
        if (PX * PX + PZ * PZ > 1800.0 * 1800.0) return;

        // 1. 着陸パッド (0, 0)
        var pad = R(0, 0.06f, 0);
        Raylib.DrawCylinder(pad, 20f, 21f, 0.16f, 16, Rgb(78, 82, 88));
        Raylib.DrawCylinder(pad + new Vector3(0, 0.17f, 0), 15.5f, 15.5f, 0.03f, 16, Rgb(230, 185, 45));
        Raylib.DrawCylinder(pad + new Vector3(0, 0.19f, 0), 14.3f, 14.3f, 0.03f, 16, Rgb(62, 66, 72));
        // 中央の「H」マーキング
        Raylib.DrawCube(pad + new Vector3(-3.2f, 0.22f, 0), 0.9f, 0.04f, 8.5f, Rgb(238, 240, 245));
        Raylib.DrawCube(pad + new Vector3(3.2f, 0.22f, 0), 0.9f, 0.04f, 8.5f, Rgb(238, 240, 245));
        Raylib.DrawCube(pad + new Vector3(0f, 0.22f, 0), 6.4f, 0.04f, 0.9f, Rgb(238, 240, 245));

        // 誘導ビーコン灯（8方向、点滅）
        bool blink = ((int)(time * 2.4f) & 1) == 0;
        var lightCol = blink ? Rgb(255, 195, 50) : Rgb(140, 95, 25);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI * 0.25;
            var bp = R(Math.Sin(a) * 18.5, 0.1f, Math.Cos(a) * 18.5);
            Raylib.DrawCylinder(bp, 0.24f, 0.30f, 0.75f, 8, Rgb(140, 145, 155));
            Raylib.DrawSphereEx(bp + new Vector3(0, 0.85f, 0), 0.30f, 8, 8, lightCol);
        }

        // 2. 居住ドーム（メイン＆サブ＋連絡トンネル）
        var dome1 = R(-28, 0, 18);
        Raylib.DrawSphereEx(dome1 + new Vector3(0, 0.5f, 0), 8.5f, 14, 18, Rgb(226, 232, 240));
        Raylib.DrawCylinder(dome1, 8.7f, 8.9f, 1.4f, 18, Rgb(130, 138, 150));
        // 光る展望窓
        Raylib.DrawCube(dome1 + new Vector3(6.2f, 3.2f, -3.5f), 2.8f, 1.6f, 2.8f, Rgb(120, 215, 255));

        var dome2 = R(-42, 0, 6);
        Raylib.DrawSphereEx(dome2 + new Vector3(0, 0.3f, 0), 5.5f, 12, 16, Rgb(215, 222, 232));
        Raylib.DrawCylinder(dome2, 5.7f, 5.9f, 1.1f, 16, Rgb(120, 128, 140));
        // 連絡チューブ
        Raylib.DrawCylinderEx(dome1 + new Vector3(0, 1.8f, 0), dome2 + new Vector3(0, 1.8f, 0), 1.4f, 1.4f, 10, Rgb(190, 198, 208));

        // 3. ソーラーパネルアレイ
        for (int i = 0; i < 4; i++)
        {
            var sp = R(24 + i * 7.5, 0, 16);
            Raylib.DrawCylinder(sp, 0.16f, 0.20f, 2.4f, 8, Rgb(165, 170, 178));
            Rlgl.PushMatrix();
            Rlgl.Translatef(sp.X, sp.Y + 2.6f, sp.Z);
            Rlgl.Rotatef(-25f, 1, 0, 0);
            Raylib.DrawCube(Vector3.Zero, 5.2f, 0.14f, 3.2f, Rgb(24, 62, 148));
            Raylib.DrawCube(new Vector3(0, 0.08f, 0), 5.3f, 0.04f, 0.12f, Rgb(190, 200, 215));
            Rlgl.PopMatrix();
        }

        // 4. 地球向けパラボラアンテナ
        var ant = R(-24, 0, -16);
        Raylib.DrawCylinder(ant, 0.35f, 0.65f, 5.5f, 10, Rgb(175, 180, 188));
        Rlgl.PushMatrix();
        Rlgl.Translatef(ant.X, ant.Y + 5.8f, ant.Z);
        Rlgl.Rotatef(-35f, 1, 0, 0);
        Raylib.DrawCylinder(Vector3.Zero, 3.6f, 0.6f, 1.2f, 14, Rgb(232, 236, 242));
        Raylib.DrawCylinder(new Vector3(0, 0.8f, 0), 0.12f, 0.12f, 2.6f, 8, Rgb(110, 115, 125));
        Rlgl.PopMatrix();

        // 5. 月面着陸記念の日の丸旗（無風のため横棒でピンと張っている）
        var flag = R(18, 0, -10);
        Raylib.DrawCylinder(flag, 0.08f, 0.10f, 5.2f, 8, Rgb(225, 228, 235));
        Raylib.DrawCube(flag + new Vector3(-1.3f, 5.15f, 0), 2.6f, 0.07f, 0.07f, Rgb(225, 228, 235));
        Raylib.DrawCube(flag + new Vector3(-1.3f, 4.35f, 0), 2.5f, 1.6f, 0.05f, Rgb(246, 248, 252));
        Raylib.DrawCylinderEx(flag + new Vector3(-1.3f, 4.35f, -0.04f), flag + new Vector3(-1.3f, 4.35f, 0.04f), 0.48f, 0.48f, 14, Rgb(208, 32, 42));
    }

    /// <summary>月面に点在する大小の岩（ボルダー）。</summary>
    private void DrawBoulders()
    {
        if (Altitude > 350f) return;
        const double slot = 38.0;
        long cx = (long)Math.Floor(PX / slot);
        long cz = (long)Math.Floor(PZ / slot);
        int range = 7;
        for (long ix = cx - range; ix <= cx + range; ix++)
        {
            for (long iz = cz - range; iz <= cz + range; iz++)
            {
                if (Hash01(ix, iz, 0x9999UL) > 0.34) continue;
                double wx = (ix + 0.15 + Hash01(ix, iz, 0x999AUL) * 0.7) * slot;
                double wz = (iz + 0.15 + Hash01(ix, iz, 0x999BUL) * 0.7) * slot;
                if (wx * wx + wz * wz < 58.0 * 58.0) continue; // 基地周辺はクリアに保つ
                float h = SurfaceY(wx, wz);
                float sz = 0.6f + (float)Hash01(ix, iz, 0x999CUL) * 2.2f;
                byte tone = (byte)(92 + (int)(Hash01(ix, iz, 0x999DUL) * 48));
                var pos = R(wx, h + sz * 0.35f, wz);
                Rlgl.PushMatrix();
                Rlgl.Translatef(pos.X, pos.Y, pos.Z);
                Rlgl.Rotatef((float)(Hash01(ix, iz, 0x999EUL) * 360.0), 0, 1, 0);
                Rlgl.Scalef(sz * 1.15f, sz * 0.72f, sz * 0.92f);
                Raylib.DrawSphereEx(Vector3.Zero, 1f, 6, 7, new Color(tone, tone, (byte)(tone + 4), (byte)255));
                Rlgl.PopMatrix();
            }
        }
    }
}
