using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;
using PointF = System.Drawing.PointF;

namespace JapanDrive;

/// <summary>フリーモードの自由走行で走る他の車（高速道路の上を走る）。</summary>
public sealed class FreeCar
{
    public int Seg;
    public double T;
    public int Dir;
    public float Lane, Speed, Dist;
    public Color Color;
    public VehicleKind Kind;
    public double X, Z;
    public float Heading;
    public bool Flying;
    public float Y, Vy, Vx, Vz, Roll, Spin, FlyTime;
}

/// <summary>
/// フリーモードの世界：日本地図をそのまま3Dにしたオープンワールド。
/// 座標はメートル（X が東、Z が北）。地図の1度を <see cref="M"/> メートルに縮めている。
/// 描画はプレイヤー位置を原点にする（遠くまで走っても精度が落ちない）。
/// </summary>
public sealed class FreeWorld
{
    public const double M = 5000;
    private const double G = 50;          // 地面のマス目
    /// <summary>描画範囲（m）。高く飛ぶほど広くなる。</summary>
    public double ViewRadius = 1000;
    public float Altitude;

    private readonly Scene scene;
    private readonly Random rng = new();
    public double PX, PZ;
    private Vector3 cam;

    private readonly record struct Seg(double Ax, double Az, double Bx, double Bz, Road Road)
    {
        public double Len => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (Bz - Az) * (Bz - Az));
    }

    private readonly List<Seg> segs = new();
    private readonly List<(float[] Xs, float[] Ys)> polys = new();
    private readonly Dictionary<long, bool> landCache = new();
    private readonly Dictionary<City, List<(double X, double Z, float W, float D, float H, int Tone)>> cityBlocks = new();
    private List<int> nearSegs = new();

    public Dictionary<Spot, (double X, double Z)> SpotPos { get; } = new();
    public List<FreeCar> Cars { get; } = new();

    public FreeWorld(Scene scene)
    {
        this.scene = scene;
        foreach (var island in JapanOutline.Islands)
        {
            var pts = island.Select(p => Game.ToWorld(p.Lat, p.Lon)).ToArray();
            polys.Add((pts.Select(p => p.X).ToArray(), pts.Select(p => p.Y).ToArray()));
        }
        foreach (var r in MapData.Roads)
        {
            if (r.Type == RoadType.Ferry) continue;
            var (ax, az) = CityPos(r.A);
            var (bx, bz) = CityPos(r.B);
            segs.Add(new Seg(ax, az, bx, bz, r));
        }
        PlaceSpots();
    }

    // ---------------- 座標 ----------------

    public static (double X, double Z) FromMap(PointF p) => (p.X * M, -p.Y * M);
    public static PointF ToMap(double x, double z) => new((float)(x / M), (float)(-z / M));
    public static (double X, double Z) CityPos(City c) => FromMap(Game.ToWorld(c));

    /// <summary>ワールド座標 → 描画座標（画面の右が -X なので東を -X にする）。</summary>
    public Vector3 R(double x, float y, double z) => new((float)-(x - PX), y, (float)(z - PZ));

    public bool IsLand(double x, double z)
    {
        float mx = (float)(x / M), my = (float)(-z / M);
        foreach (var (xs, ys) in polys)
        {
            bool inside = false;
            for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
            {
                if ((ys[i] > my) != (ys[j] > my) &&
                    mx < (xs[j] - xs[i]) * (my - ys[i]) / (ys[j] - ys[i]) + xs[i])
                    inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }

    private bool LandAt(long ix, long iz)
    {
        long key = (ix << 32) ^ (iz & 0xffffffffL);
        if (landCache.TryGetValue(key, out bool v)) return v;
        if (landCache.Count > 300000) landCache.Clear();
        v = IsLand(ix * G, iz * G);
        landCache[key] = v;
        return v;
    }

    /// <summary>おおまかな陸判定（グリッドのキャッシュを使う）。</summary>
    public bool IsLandFast(double x, double z) => LandAt((long)Math.Round(x / G), (long)Math.Round(z / G));

    private static double SegDistance(Seg s, double x, double z, out double t)
    {
        double dx = s.Bx - s.Ax, dz = s.Bz - s.Az;
        double len2 = dx * dx + dz * dz;
        t = len2 < 1e-6 ? 0 : Math.Clamp(((x - s.Ax) * dx + (z - s.Az) * dz) / len2, 0, 1);
        double px = s.Ax + dx * t - x, pz = s.Az + dz * t - z;
        return Math.Sqrt(px * px + pz * pz);
    }

    /// <summary>道路（橋）の上にいるか。</summary>
    private bool OnAnyRoad(double x, double z, double width)
    {
        foreach (var s in segs)
            if (SegDistance(s, x, z, out _) < width) return true;
        return false;
    }

    public bool OnRoad(double x, double z, double width = 9)
    {
        foreach (int i in nearSegs)
            if (SegDistance(segs[i], x, z, out _) < width) return true;
        return false;
    }

    public float GroundY(double x, double z) => OnRoad(x, z) ? 0.06f : IsLandFast(x, z) ? 0 : SeaY;
    public const float SeaY = -1.2f;

    public (City City, double Dist) NearestCity(double x, double z)
    {
        City best = MapData.Cities[0];
        double bd = double.MaxValue;
        foreach (var c in MapData.Cities)
        {
            var (cx, cz) = CityPos(c);
            double d = Math.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z));
            if (d < bd) { bd = d; best = c; }
        }
        return (best, bd);
    }

    private void PlaceSpots()
    {
        foreach (var city in MapData.Cities)
        {
            var spots = Spots.At(city).ToList();
            var (cx, cz) = CityPos(city);
            for (int i = 0; i < spots.Count; i++)
            {
                var sp = spots[i];
                double d = sp.Type switch
                {
                    LandmarkType.Mountain or LandmarkType.Volcano => 1900,
                    LandmarkType.Lake => 1000,
                    _ => 520 + i * 120,
                };
                double baseAng = Scene.Hash(sp.Name.GetHashCode()) * Math.PI * 2 + i * Math.PI;
                (double, double) pos = (cx + Math.Sin(baseAng) * d, cz + Math.Cos(baseAng) * d);
                for (int k = 0; k < 8; k++)
                {
                    double a = baseAng + k * Math.PI / 4;
                    double x = cx + Math.Sin(a) * d, z = cz + Math.Cos(a) * d;
                    if (IsLand(x, z)) { pos = (x, z); break; }
                }
                SpotPos[sp] = pos;
            }
        }
    }

    public static float CollectRadius(LandmarkType t) => t switch
    {
        LandmarkType.Mountain or LandmarkType.Volcano => 600,
        LandmarkType.Lake => 260,
        LandmarkType.Bridge => 200,
        LandmarkType.Tower or LandmarkType.Castle => 130,
        _ => 90,
    };

    // ---------------- 更新 ----------------

    public void UpdateNear()
    {
        var list = new List<int>();
        for (int i = 0; i < segs.Count; i++)
            if (SegDistance(segs[i], PX, PZ, out _) < Math.Max(1800, ViewRadius + 500)) list.Add(i);
        nearSegs = list;
    }

    private static readonly Color[] Palette =
    {
        Rgb(240, 240, 240), Rgb(30, 30, 35), Rgb(150, 155, 165), Rgb(40, 70, 150),
        Rgb(160, 30, 40), Rgb(210, 190, 60), Rgb(60, 120, 80), Rgb(230, 230, 220),
    };

    public void UpdateTraffic(float dt)
    {
        for (int i = Cars.Count - 1; i >= 0; i--)
        {
            var c = Cars[i];
            if (c.Flying)
            {
                c.FlyTime += dt;
                c.Vy -= 20 * dt;
                c.Y += c.Vy * dt;
                c.X += c.Vx * dt;
                c.Z += c.Vz * dt;
                c.Roll += c.Spin * dt;
                if (c.Y < 0) { c.Y = 0; c.Vy = Math.Abs(c.Vy) * 0.35f; c.Vx *= 0.6f; c.Vz *= 0.6f; c.Spin *= 0.5f; }
                if (c.FlyTime > 3.2f) Cars.RemoveAt(i);
                continue;
            }
            var s = segs[c.Seg];
            double len = s.Len;
            c.T += c.Dir * c.Speed * dt / len;
            c.Dist += c.Speed * dt;
            if (c.T < 0 || c.T > 1) { Cars.RemoveAt(i); continue; }
            SetCarPos(c);
            double dx = c.X - PX, dz = c.Z - PZ;
            if (dx * dx + dz * dz > 1600 * 1600) Cars.RemoveAt(i);
        }

        // 近くの道路に車を出す
        int tries = 0;
        while (Cars.Count < 16 && nearSegs.Count > 0 && tries++ < 6)
        {
            int si = nearSegs[rng.Next(nearSegs.Count)];
            var s = segs[si];
            SegDistance(s, PX, PZ, out double t0);
            double off = (200 + rng.NextDouble() * 900) * (rng.NextDouble() < 0.5 ? -1 : 1);
            double t = t0 + off / s.Len;
            if (t < 0.01 || t > 0.99) continue;
            double roll = rng.NextDouble();
            var kind = roll < 0.22 ? VehicleKind.Truck : roll < 0.42 ? VehicleKind.Kei
                : roll < 0.65 ? VehicleKind.Minivan : roll < 0.7 ? VehicleKind.Sports : VehicleKind.Sedan;
            var car = new FreeCar
            {
                Seg = si, T = t, Dir = rng.NextDouble() < 0.5 ? 1 : -1,
                Lane = rng.NextDouble() < 0.6 ? 5.6f : 2.4f,
                Speed = kind == VehicleKind.Truck ? 22 + (float)rng.NextDouble() * 3 : 24 + (float)rng.NextDouble() * 8,
                Color = Palette[rng.Next(Palette.Length)], Kind = kind,
            };
            SetCarPos(car);
            double dx = car.X - PX, dz = car.Z - PZ;
            if (dx * dx + dz * dz < 150 * 150) continue;
            Cars.Add(car);
        }
    }

    private void SetCarPos(FreeCar c)
    {
        var s = segs[c.Seg];
        double dx = (s.Bx - s.Ax) * c.Dir, dz = (s.Bz - s.Az) * c.Dir;
        double len = Math.Sqrt(dx * dx + dz * dz);
        dx /= len; dz /= len;
        // 左側通行：進行方向の左へずらす（左向きの法線は (-dz, dx)）
        c.X = s.Ax + (s.Bx - s.Ax) * c.T - dz * c.Lane;
        c.Z = s.Az + (s.Bz - s.Az) * c.T + dx * c.Lane;
        c.Heading = (float)(Math.Atan2(dx, dz) * 180 / Math.PI);
    }

    public FreeCar? HitTest(double x, double z, float y, float radius = 2.4f)
    {
        foreach (var c in Cars)
        {
            if (c.Flying) continue;
            double dx = c.X - x, dz = c.Z - z;
            float r = radius + (c.Kind == VehicleKind.Truck ? 2.5f : 0.6f);
            if (dx * dx + dz * dz < r * r && y < (c.Kind == VehicleKind.Truck ? 4.1f : 2.3f) && y > -0.5f) return c;
        }
        return null;
    }

    public void Knock(FreeCar c, double dirX, double dirZ, float power)
    {
        c.Flying = true;
        c.FlyTime = 0;
        c.Y = 0.2f;
        c.Vy = (c.Kind == VehicleKind.Truck ? 9 : 14) * power;
        c.Vx = (float)dirX * 9 * power + (float)(rng.NextDouble() - 0.5) * 4;
        c.Vz = (float)dirZ * 9 * power + (float)(rng.NextDouble() - 0.5) * 4;
        c.Spin = (rng.NextDouble() < 0.5 ? -1 : 1) * (220 + (float)rng.NextDouble() * 260) * power;
    }

    // ---------------- 描画 ----------------

    private void V(double x, float y, double z, Color c)
    {
        var p = R(x, y, z);
        var col = scene.FogC(c, Vector3.Distance(p, cam));
        Rlgl.Color4ub(col.R, col.G, col.B, 255);
        Rlgl.Vertex3f(p.X, p.Y, p.Z);
    }

    public void Draw(Vector3 cameraPos, float time)
    {
        cam = cameraPos;
        scene.SetCamera(cameraPos);
        DrawGround();
        DrawRoads();
        DrawMountains();
        DrawTrees();
        DrawCities();
        foreach (var (spot, pos) in SpotPos)
        {
            double dx = pos.X - PX, dz = pos.Z - PZ;
            float range = (spot.Type is LandmarkType.Mountain or LandmarkType.Volcano ? 2600 : 1300) * Math.Max(1, (float)ViewRadius / 1500);
            if (dx * dx + dz * dz > range * range) continue;
            Landmarks.Draw(scene, spot, R(pos.X, IsLandFast(pos.X, pos.Z) ? 0 : SeaY + 1, pos.Z), time);
        }
        foreach (var c in Cars)
            scene.DrawVehicleAt(R(c.X, c.Y, c.Z), -c.Heading, c.Color, c.Kind, c.Dist / 0.33f * 57.3f % 360,
                roll: c.Roll, shadow: !c.Flying);
    }

    private readonly Dictionary<(long, long, int), bool> lodCache = new();

    /// <summary>粗いマス目（lvl が大きいほど粗い）での陸判定。</summary>
    private bool LandLod(long ix, long iz, int lvl)
    {
        if (lvl == 0) return LandAt(ix, iz);
        var key = (ix, iz, lvl);
        if (lodCache.TryGetValue(key, out bool v)) return v;
        if (lodCache.Count > 300000) lodCache.Clear();
        double st = G * (1 << lvl);
        v = IsLand(ix * st, iz * st);
        lodCache[key] = v;
        return v;
    }

    private void DrawGround()
    {
        int lvl = 0;
        while (ViewRadius / (G * (1 << lvl)) > 42) lvl++;
        double step = G * (1 << lvl);
        long cx = (long)Math.Floor(PX / step), cz = (long)Math.Floor(PZ / step);
        int n = (int)(ViewRadius / step);
        var sea = Rgb(40, 100, 165);
        var sand = Rgb(212, 200, 150);
        Rlgl.Begin(DrawMode.Quads);
        for (int i = -n; i < n; i++)
        {
            for (int j = -n; j < n; j++)
            {
                if (i * i + j * j > n * n) continue;
                long ix = cx + i, iz = cz + j;
                Vert(ix, iz); Vert(ix + 1, iz); Vert(ix + 1, iz + 1); Vert(ix, iz + 1);
            }
        }
        Rlgl.End();

        void Vert(long ix, long iz)
        {
            bool land = LandLod(ix, iz, lvl);
            Color c;
            if (!land) c = sea;
            else if (!LandLod(ix + 1, iz, lvl) || !LandLod(ix - 1, iz, lvl) || !LandLod(ix, iz + 1, lvl) || !LandLod(ix, iz - 1, lvl)) c = sand;
            else
            {
                float h = Scene.Hash(ix * 73856093L ^ iz * 19349663L);
                // 北の方は少し白っぽい草原
                float north = Math.Clamp((float)((iz * step / M) - 4) / 5f, 0, 1);
                c = Lerp(Rgb(86 + (int)(h * 18), 146 + (int)(h * 14), 70), Rgb(150, 175, 130), north * 0.6f);
            }
            V(ix * step, land ? 0 : SeaY, iz * step, c);
        }
    }

    private void DrawRoads()
    {
        var asphalt = Rgb(68, 70, 76);
        var white = Rgb(235, 235, 235);
        var girder = Rgb(190, 80, 55);
        Rlgl.Begin(DrawMode.Quads);
        foreach (int si in nearSegs)
        {
            var s = segs[si];
            double d0 = SegDistance(s, PX, PZ, out double t0);
            if (d0 > ViewRadius) continue;
            double len = s.Len;
            double h = Math.Sqrt(ViewRadius * ViewRadius - d0 * d0);
            double ta = Math.Max(0, t0 - h / len), tb = Math.Min(1, t0 + h / len);
            double dx = (s.Bx - s.Ax) / len, dz = (s.Bz - s.Az) / len;
            double nx = -dz, nz = dx;
            double step = 25 * Math.Max(1, ViewRadius / 1000);
            int k0 = (int)Math.Floor(ta * len / step), k1 = (int)Math.Ceiling(tb * len / step);
            for (int k = k0; k < k1; k++)
            {
                double a = Math.Max(k * step, 0), b = Math.Min((k + 1) * step, len);
                double ax = s.Ax + dx * a, az = s.Az + dz * a, bx = s.Ax + dx * b, bz = s.Az + dz * b;
                void Strip(double w0, double w1, float y, Color c)
                {
                    V(ax + nx * w0, y, az + nz * w0, c);
                    V(ax + nx * w1, y, az + nz * w1, c);
                    V(bx + nx * w1, y, bz + nz * w1, c);
                    V(bx + nx * w0, y, bz + nz * w0, c);
                }
                void Side(double w, float y0, float y1, Color c)
                {
                    V(ax + nx * w, y0, az + nz * w, c);
                    V(ax + nx * w, y1, az + nz * w, c);
                    V(bx + nx * w, y1, bz + nz * w, c);
                    V(bx + nx * w, y0, bz + nz * w, c);
                }
                Strip(-8, 8, 0.06f, asphalt);
                Strip(-0.5, 0.5, 0.08f, Rgb(150, 150, 146));
                Strip(-7.8, -7.6, 0.09f, white);
                Strip(7.6, 7.8, 0.09f, white);
                if (k % 2 == 0)
                {
                    Strip(-4.1, -3.9, 0.09f, white);
                    Strip(3.9, 4.1, 0.09f, white);
                }
                // 海の上は橋：桁と欄干
                if (!IsLandFast((ax + bx) / 2, (az + bz) / 2))
                {
                    float deep = s.Road.Type == RoadType.MegaBridge ? -12 : SeaY;
                    Side(-8, deep, 0.06f, girder);
                    Side(8, deep, 0.06f, girder);
                    Side(-8, 0.06f, 1.2f, Rgb(225, 225, 220));
                    Side(8, 0.06f, 1.2f, Rgb(225, 225, 220));
                }
            }
        }
        Rlgl.End();
    }

    private readonly Dictionary<long, (double X, double Z, float R, float H)?> mountainCache = new();

    private void DrawMountains()
    {
        const double slot = 500;
        double range = Math.Max(1450, ViewRadius * 1.1);
        int n = (int)(range / slot) + 1;
        long cx = (long)Math.Floor(PX / slot), cz = (long)Math.Floor(PZ / slot);
        for (long i = cx - n; i <= cx + n; i++)
        {
            for (long j = cz - n; j <= cz + n; j++)
            {
                long id = i * 92821 + j * 68917;
                if (!mountainCache.TryGetValue(id, out var mt))
                {
                    mt = null;
                    if (Scene.Hash(id) <= 0.3f)
                    {
                        double mx = (i + Scene.Hash(id + 1)) * slot, mz = (j + Scene.Hash(id + 2)) * slot;
                        if (IsLand(mx, mz) && !OnAnyRoad(mx, mz, 260) && NearestCity(mx, mz).Dist >= 1300)
                            mt = (mx, mz, 110 + Scene.Hash(id + 3) * 150, 60 + Scene.Hash(id + 4) * 170);
                    }
                    mountainCache[id] = mt;
                }
                if (mt is not (double x, double z, float r, float hgt)) continue;
                var p = R(x, -0.3f, z);
                if (p.Length() > range) continue;
                Raylib.DrawCylinder(p, 0, r, hgt, 10, scene.LandmarkFog(Rgb(70, 108, 80), p));
                if (hgt > 180) Raylib.DrawCylinder(p + new Vector3(0, hgt * 0.78f, 0), 0, r * 0.22f, hgt * 0.22f, 10, scene.LandmarkFog(Rgb(240, 242, 245), p));
            }
        }
    }

    private void DrawTrees()
    {
        if (Altitude > 250) return; // 高いところからは見えないので省く
        const double slot = 28;
        long cx = (long)Math.Floor(PX / slot), cz = (long)Math.Floor(PZ / slot);
        int n = (int)(480 / slot);
        for (long i = cx - n; i <= cx + n; i++)
        {
            for (long j = cz - n; j <= cz + n; j++)
            {
                long id = i * 73856093L ^ j * 19349663L;
                if (Scene.Hash(id) > 0.22f) continue;
                double x = (i + Scene.Hash(id + 1)) * slot, z = (j + Scene.Hash(id + 2)) * slot;
                if (!IsLandFast(x, z) || OnRoad(x, z, 18)) continue;
                var p = R(x, 0, z);
                float d = Vector3.Distance(p, cam);
                if (d > 520) continue;
                float h = 6 + Scene.Hash(id + 3) * 7;
                bool pine = Scene.Hash(id + 4) < 0.5f;
                Raylib.DrawCylinder(p, 0.25f, 0.35f, h * 0.35f, 5, scene.FogC(Rgb(90, 65, 45), d));
                var leaf = scene.FogC(pine ? Rgb(40, 95, 50) : Rgb(70, 130, 55), d);
                var top = p + new Vector3(0, h * 0.25f, 0);
                if (pine) Raylib.DrawCylinder(top, 0, h * 0.28f, h * 0.8f, 7, leaf);
                else Raylib.DrawSphere(top + new Vector3(0, h * 0.35f, 0), h * 0.3f, leaf);
            }
        }
    }

    private List<(double X, double Z, float W, float D, float H, int Tone)> Blocks(City city)
    {
        if (cityBlocks.TryGetValue(city, out var list)) return list;
        list = new();
        var (cx, cz) = CityPos(city);
        double radius = city.IsGoal ? 650 : 380;
        const double cell = 38;
        int n = (int)(radius / cell);
        var roadsHere = segs.Where(s => SegDistance(s, cx, cz, out _) < radius + 50).ToList();
        for (int i = -n; i <= n; i++)
        {
            for (int j = -n; j <= n; j++)
            {
                long id = (i + 1000) * 7919L + (j + 1000) * 104729L + city.Name.GetHashCode();
                double x = cx + (i + Scene.Hash(id) * 0.4) * cell, z = cz + (j + Scene.Hash(id + 1) * 0.4) * cell;
                double d = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                if (d > radius || Scene.Hash(id + 2) > 0.75 - d / radius * 0.4) continue;
                if (!IsLand(x, z) || roadsHere.Any(s => SegDistance(s, x, z, out _) < 20)) continue;
                float near = (float)(1 - d / radius);
                float h = 8 + near * near * (city.IsGoal ? 110 : 45) * Scene.Hash(id + 3) + Scene.Hash(id + 4) * 10;
                list.Add((x, z, 12 + Scene.Hash(id + 5) * 16, 12 + Scene.Hash(id + 6) * 16, h, (int)(Scene.Hash(id + 7) * 80)));
            }
        }
        cityBlocks[city] = list;
        return list;
    }

    private void DrawCities()
    {
        foreach (var city in MapData.Cities)
        {
            var (cx, cz) = CityPos(city);
            double range = Math.Max(1800, ViewRadius);
            if ((cx - PX) * (cx - PX) + (cz - PZ) * (cz - PZ) > range * range) continue;
            foreach (var b in Blocks(city))
            {
                var p = R(b.X, b.H / 2, b.Z);
                float d = Vector3.Distance(p, cam);
                if (d > Math.Max(900, ViewRadius * 0.85) || (d > 2500 && b.H < 30)) continue;
                int g = 150 + b.Tone;
                var c = scene.Night
                    ? scene.FogC(Lerp(Rgb(40, 40, 50), Rgb(230, 200, 120), (b.Tone % 10) / 18f), d, lit: true)
                    : scene.FogC(Rgb(g, g - 5, g - 15), d);
                Raylib.DrawCube(p, b.W, b.H, b.D, c);
            }
        }
    }
}
