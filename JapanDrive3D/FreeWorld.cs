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

/// <summary>町の建物（ぶつかる・壊せる）。</summary>
public sealed class Block
{
    public double X, Z;
    public float W, D, H;
    public int Tone;
    public bool Destroyed;
}

/// <summary>建物が建つ場所（高速道路の都市と、地図にない町）。</summary>
public sealed class Place
{
    public required string Name;
    public double X, Z;
    public float Radius;
    public int Tier;
    public List<Block>? Blocks;
}

/// <summary>
/// 陸と水（海・湖・川）の地図。Natural Earth の海岸線を 0.005 度（ゲーム内で約25m）のマス目に塗ったもの。
/// </summary>
public static class LandRaster
{
    public const double Lon0 = 122.5, Lat0 = 24.0, Res = 0.005;
    public const int W = 5300, H = 4320;
    private static readonly ulong[] Bits = Build();

    public static bool Land(double lat, double lon)
    {
        int c = (int)((lon - Lon0) / Res), r = (int)((lat - Lat0) / Res);
        if ((uint)c >= W || (uint)r >= H) return false;
        long i = (long)r * W + c;
        return (Bits[i >> 6] & (1UL << (int)(i & 63))) != 0;
    }

    private static void Set(ulong[] bits, int r, int c, bool v)
    {
        if ((uint)c >= W || (uint)r >= H) return;
        long i = (long)r * W + c;
        if (v) bits[i >> 6] |= 1UL << (int)(i & 63);
        else bits[i >> 6] &= ~(1UL << (int)(i & 63));
    }

    /// <summary>多角形の内側を塗る（行ごとに辺との交点を求めて、交点の間を塗る）。</summary>
    private static void Fill(ulong[] bits, IEnumerable<(double Lat, double Lon)[]> rings, bool value)
    {
        var rows = new List<double>?[H];
        foreach (var ring in rings)
        {
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Length];
                double ya = (a.Lat - Lat0) / Res - 0.5, yb = (b.Lat - Lat0) / Res - 0.5;
                double xa = (a.Lon - Lon0) / Res, xb = (b.Lon - Lon0) / Res;
                if (ya == yb) continue;
                int r0 = (int)Math.Ceiling(Math.Min(ya, yb)), r1 = (int)Math.Ceiling(Math.Max(ya, yb)) - 1;
                for (int r = Math.Max(0, r0); r <= Math.Min(H - 1, r1); r++)
                    (rows[r] ??= new()).Add(xa + (r - ya) * (xb - xa) / (yb - ya));
            }
        }
        for (int r = 0; r < H; r++)
        {
            var xs = rows[r];
            if (xs == null) continue;
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
                for (int c = (int)Math.Ceiling(xs[k] - 0.5); c <= (int)Math.Floor(xs[k + 1] - 0.5); c++)
                    Set(bits, r, c, value);
        }
    }

    private static ulong[] Build()
    {
        var bits = new ulong[((long)W * H + 63) / 64];
        Fill(bits, JapanOutline.Detailed, true);
        Fill(bits, JapanGeo.Lakes.Select(l => JapanOutline.Decode(l.Points)), false);
        // 川は線なので、太さをつけて水にする
        foreach (var (_, rank, pts) in JapanGeo.Rivers)
        {
            var line = JapanOutline.Decode(pts);
            double rad = rank <= 4 ? 1.6 : 1.1;
            for (int i = 0; i + 1 < line.Length; i++)
            {
                double ya = (line[i].Lat - Lat0) / Res, xa = (line[i].Lon - Lon0) / Res;
                double yb = (line[i + 1].Lat - Lat0) / Res, xb = (line[i + 1].Lon - Lon0) / Res;
                int steps = (int)(Math.Max(Math.Abs(yb - ya), Math.Abs(xb - xa)) * 3) + 1;
                for (int s = 0; s <= steps; s++)
                {
                    double y = ya + (yb - ya) * s / steps, x = xa + (xb - xa) * s / steps;
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                            if (dr * dr + dc * dc <= rad * rad) Set(bits, (int)y + dr, (int)x + dc, false);
                }
            }
        }
        return bits;
    }
}

/// <summary>
/// フリーモードの世界：日本地図をそのまま3Dにしたオープンワールド。
/// 座標はメートル（X が東、Z が北）。地図の1度を <see cref="M"/> メートルに縮めている（実際の約22分の1）。
/// 山は実際の位置と標高（高さは実際の 0.12 倍）で地形になっていて、登ったりぶつかったりできる。
/// 描画はプレイヤー位置を原点にする（遠くまで走っても精度が落ちない）。
/// </summary>
public sealed class FreeWorld
{
    public const double M = 5000;
    public const float VScale = 0.12f;         // 実際の標高 → ゲームの高さ
    private const double G = 50;               // 地面のマス目
    private static readonly double Cos36 = Math.Cos(36 * Math.PI / 180);

    private readonly Scene scene;
    private readonly Random rng = new();
    public double PX, PZ;
    private Vector3 cam;

    /// <summary>描画範囲（m）。高く飛ぶほど広くなる。</summary>
    public double ViewRadius = 1000;
    public float Altitude;

    private readonly record struct Seg(double Ax, double Az, double Bx, double Bz, Road Road)
    {
        public double Len => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (Bz - Az) * (Bz - Az));
    }

    private readonly record struct Peak(string Name, double X, double Z, float H, float R, float Elev);

    private readonly List<Seg> segs = new();
    private readonly List<Peak> peaks = new();
    private List<int> nearSegs = new();
    private readonly Dictionary<(long, long, int), float> heightCache = new();

    public List<Place> Places { get; } = new();
    public Dictionary<Spot, (double X, double Z)> SpotPos { get; } = new();
    public HashSet<Spot> TerrainSpots { get; } = new();   // 地形そのものが名所（模型は置かない）
    public HashSet<Spot> SmokeSpots { get; } = new();     // 噴煙だけ出す
    public List<FreeCar> Cars { get; } = new();
    public IReadOnlyList<(string Name, double X, double Z, float Elev)> PeakList =>
        peaks.Select(p => (p.Name, p.X, p.Z, p.Elev)).ToList();

    public FreeWorld(Scene scene)
    {
        this.scene = scene;
        foreach (var r in MapData.Roads)
        {
            if (r.Type == RoadType.Ferry) continue;
            var (ax, az) = CityPos(r.A);
            var (bx, bz) = CityPos(r.B);
            segs.Add(new Seg(ax, az, bx, bz, r));
        }
        foreach (var (name, lat, lon, elev) in GeoFeatures.Peaks)
        {
            var (x, z) = FromLatLon(lat, lon);
            float rKm = 5 + elev / 300f;
            peaks.Add(new Peak(name, x, z, elev * VScale, rKm * 1000 / 22.2f, elev));
        }
        foreach (var c in MapData.Cities)
        {
            var (x, z) = CityPos(c);
            int tier = c.Name is "東京" or "大阪" ? 0 : c.IsGoal ? 1 : 2;
            Places.Add(new Place { Name = c.Name, X = x, Z = z, Tier = tier, Radius = TierRadius(tier) });
        }
        foreach (var (name, lat, lon, tier) in GeoFeatures.Towns)
        {
            var (x, z) = FromLatLon(lat, lon);
            Places.Add(new Place { Name = name, X = x, Z = z, Tier = tier, Radius = TierRadius(tier) });
        }
        _ = LandRaster.Land(0, 0);   // 地図を先に作っておく
        PlaceSpots();
    }

    private static float TierRadius(int tier) => tier switch { 0 => 950, 1 => 650, 2 => 420, _ => 270 };

    // ---------------- 座標 ----------------

    public static (double X, double Z) FromMap(PointF p) => (p.X * M, -p.Y * M);
    public static PointF ToMap(double x, double z) => new((float)(x / M), (float)(-z / M));
    public static (double X, double Z) CityPos(City c) => FromMap(Game.ToWorld(c));
    public static (double X, double Z) FromLatLon(double lat, double lon) => FromMap(Game.ToWorld(lat, lon));
    public static (double Lat, double Lon) ToLatLon(double x, double z) => (37 + z / M, 137 + x / M / Cos36);

    /// <summary>ワールド座標 → 描画座標（画面の右が -X なので東を -X にする）。</summary>
    public Vector3 R(double x, float y, double z) => new((float)-(x - PX), y, (float)(z - PZ));

    public bool IsLand(double x, double z)
    {
        var (lat, lon) = ToLatLon(x, z);
        return LandRaster.Land(lat, lon);
    }

    public bool IsLandFast(double x, double z) => IsLand(x, z);

    private static double SegDistance(Seg s, double x, double z, out double t)
    {
        double dx = s.Bx - s.Ax, dz = s.Bz - s.Az;
        double len2 = dx * dx + dz * dz;
        t = len2 < 1e-6 ? 0 : Math.Clamp(((x - s.Ax) * dx + (z - s.Az) * dz) / len2, 0, 1);
        double px = s.Ax + dx * t - x, pz = s.Az + dz * t - z;
        return Math.Sqrt(px * px + pz * pz);
    }

    private double RoadDistance(double x, double z)
    {
        double best = double.MaxValue;
        foreach (var s in segs) best = Math.Min(best, SegDistance(s, x, z, out _));
        return best;
    }

    /// <summary>道路（橋）の上にいるか。</summary>
    public bool OnRoad(double x, double z, double width = 9)
    {
        foreach (int i in nearSegs)
            if (SegDistance(segs[i], x, z, out _) < width) return true;
        return false;
    }

    public const float SeaY = -1.2f;

    // ---------------- 地形の高さ ----------------

    private static float Smooth(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>陸地の高さ（m）。山・丘を足し、道路と町のまわりは平らにする。</summary>
    public float TerrainHeight(double x, double z)
    {
        float h = 0;
        foreach (var p in peaks)
        {
            double dx = x - p.X, dz = z - p.Z;
            if (Math.Abs(dx) > p.R || Math.Abs(dz) > p.R) continue;
            double t = Math.Sqrt(dx * dx + dz * dz) / p.R;
            if (t >= 1) continue;
            float v = p.H * (float)Math.Pow(1 - t, 1.7);
            h = Math.Max(h, v) + Math.Min(h, v) * 0.25f;   // 近くの山は少し足し合わせて山脈らしく
        }
        // 小さな丘（地形の起伏）
        const double cell = 900;
        long cx = (long)Math.Floor(x / cell), cz = (long)Math.Floor(z / cell);
        float hills = 0;
        for (long i = cx - 1; i <= cx + 1; i++)
        {
            for (long j = cz - 1; j <= cz + 1; j++)
            {
                long id = i * 92821 + j * 68917 + 7;
                if (Scene.Hash(id) > 0.45f) continue;
                double hx = (i + Scene.Hash(id + 1)) * cell, hz = (j + Scene.Hash(id + 2)) * cell;
                float hr = 350 + Scene.Hash(id + 3) * 450, hh = 12 + Scene.Hash(id + 4) * 55;
                double t = Math.Sqrt((x - hx) * (x - hx) + (z - hz) * (z - hz)) / hr;
                if (t < 1) hills += hh * (float)(1 - t * t) * (float)(1 - t * t);
            }
        }
        h = Math.Max(h, hills) + Math.Min(h, hills) * 0.3f;
        if (h < 0.05f) return 0;

        // 道路と町のまわりは平らに（高速道路は山を切り開いて通っている）
        float flat = Smooth(25, 260, RoadDistance(x, z));
        foreach (var pl in Places)
        {
            double dx = x - pl.X, dz = z - pl.Z;
            double lim = pl.Radius + 450;
            if (Math.Abs(dx) > lim || Math.Abs(dz) > lim) continue;
            flat = Math.Min(flat, Smooth(pl.Radius, lim, Math.Sqrt(dx * dx + dz * dz)));
        }
        return h * flat;
    }

    private float HeightAt(long ix, long iz, int lvl, double step)
    {
        var key = (ix, iz, lvl);
        if (heightCache.TryGetValue(key, out float v)) return v;
        if (heightCache.Count > 400000) heightCache.Clear();
        double x = ix * step, z = iz * step;
        v = IsLand(x, z) ? TerrainHeight(x, z) : SeaY;
        heightCache[key] = v;
        return v;
    }

    /// <summary>その場所の地面の高さ（道路・陸・水）。</summary>
    public float SurfaceY(double x, double z) =>
        OnRoad(x, z) ? 0.06f : IsLand(x, z) ? TerrainHeight(x, z) : SeaY;

    public float GroundY(double x, double z) => SurfaceY(x, z);

    public (string Name, double Dist) NearestPlace(double x, double z)
    {
        string best = Places[0].Name;
        double bd = double.MaxValue;
        foreach (var p in Places)
        {
            double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
            if (d < bd) { bd = d; best = p.Name; }
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
                if (GeoFeatures.RealSpots.TryGetValue(sp.Name, out var real))
                {
                    // 富士山や支笏湖などは、本物の場所そのものが名所
                    SpotPos[sp] = FromLatLon(real.Lat, real.Lon);
                    TerrainSpots.Add(sp);
                    if (real.Smoke) SmokeSpots.Add(sp);
                    continue;
                }
                double d = sp.Type switch
                {
                    LandmarkType.Mountain or LandmarkType.Volcano => 1900,
                    LandmarkType.Lake => 1000,
                    _ => TierRadius(city.IsGoal ? 1 : 2) + 150 + i * 120,
                };
                double baseAng = Scene.Hash(sp.Name.GetHashCode()) * Math.PI * 2 + i * Math.PI;
                (double, double) pos = (cx + Math.Sin(baseAng) * d, cz + Math.Cos(baseAng) * d);
                for (int k = 0; k < 8; k++)
                {
                    double a = baseAng + k * Math.PI / 4;
                    double x = cx + Math.Sin(a) * d, z = cz + Math.Cos(a) * d;
                    if (IsLand(x, z) && TerrainHeight(x, z) < 15) { pos = (x, z); break; }
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

    // ---------------- 建物（ぶつかる・壊せる） ----------------

    private List<Block> Blocks(Place place)
    {
        if (place.Blocks != null) return place.Blocks;
        var list = new List<Block>();
        double radius = place.Radius;
        const double cell = 38;
        int n = (int)(radius / cell);
        var roadsHere = segs.Where(s => SegDistance(s, place.X, place.Z, out _) < radius + 50).ToList();
        float maxH = place.Tier switch { 0 => 190, 1 => 115, 2 => 55, _ => 28 };
        for (int i = -n; i <= n; i++)
        {
            for (int j = -n; j <= n; j++)
            {
                long id = (i + 1000) * 7919L + (j + 1000) * 104729L + place.Name.GetHashCode();
                double x = place.X + (i + Scene.Hash(id) * 0.4) * cell, z = place.Z + (j + Scene.Hash(id + 1) * 0.4) * cell;
                double d = Math.Sqrt((x - place.X) * (x - place.X) + (z - place.Z) * (z - place.Z));
                if (d > radius || Scene.Hash(id + 2) > 0.75 - d / radius * 0.4) continue;
                if (!IsLand(x, z) || roadsHere.Any(s => SegDistance(s, x, z, out _) < 20)) continue;
                float near = (float)(1 - d / radius);
                float h = 8 + near * near * maxH * Scene.Hash(id + 3) + Scene.Hash(id + 4) * 10;
                list.Add(new Block
                {
                    X = x, Z = z, H = h,
                    W = 12 + Scene.Hash(id + 5) * 16, D = 12 + Scene.Hash(id + 6) * 16,
                    Tone = (int)(Scene.Hash(id + 7) * 80),
                });
            }
        }
        place.Blocks = list;
        return list;
    }

    /// <summary>点（半径 radius の円）が建物にめり込んでいたら、その建物を返す。</summary>
    public Block? HitBlock(double x, double z, float y, float radius)
    {
        foreach (var place in Places)
        {
            double lim = place.Radius + 60;
            if (Math.Abs(x - place.X) > lim || Math.Abs(z - place.Z) > lim) continue;
            foreach (var b in Blocks(place))
            {
                if (b.Destroyed || y > b.H) continue;
                if (Math.Abs(x - b.X) < b.W / 2 + radius && Math.Abs(z - b.Z) < b.D / 2 + radius) return b;
            }
        }
        return null;
    }

    /// <summary>屋根の上に乗れるように、その場所の建物の高さを返す（なければ 0）。</summary>
    public float RoofY(double x, double z)
    {
        foreach (var place in Places)
        {
            double lim = place.Radius + 60;
            if (Math.Abs(x - place.X) > lim || Math.Abs(z - place.Z) > lim) continue;
            foreach (var b in Blocks(place))
                if (!b.Destroyed && Math.Abs(x - b.X) < b.W / 2 && Math.Abs(z - b.Z) < b.D / 2) return b.H;
        }
        return 0;
    }

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
        DrawTrees();
        DrawCities();
        foreach (var (spot, pos) in SpotPos)
        {
            double dx = pos.X - PX, dz = pos.Z - PZ;
            float range = (spot.Type is LandmarkType.Mountain or LandmarkType.Volcano ? 2600 : 1300) * Math.Max(1, (float)ViewRadius / 1500);
            if (dx * dx + dz * dz > range * range) continue;
            if (SmokeSpots.Contains(spot)) DrawSmoke(pos.X, pos.Z, time);
            if (TerrainSpots.Contains(spot)) continue;
            Landmarks.Draw(scene, spot, R(pos.X, IsLand(pos.X, pos.Z) ? TerrainHeight(pos.X, pos.Z) : SeaY + 1, pos.Z), time);
        }
        foreach (var c in Cars)
            scene.DrawVehicleAt(R(c.X, c.Y, c.Z), -c.Heading, c.Color, c.Kind, c.Dist / 0.33f * 57.3f % 360,
                roll: c.Roll, shadow: !c.Flying);
    }

    /// <summary>噴煙（桜島）。</summary>
    private void DrawSmoke(double x, double z, float time)
    {
        float top = TerrainHeight(x, z);
        var steam = new Color((byte)225, (byte)225, (byte)225, (byte)160);
        for (int i = 0; i < 9; i++)
        {
            float rise = (time * 6 + i * 22) % 200;
            var p = R(x + Math.Sin(i * 1.7) * 8 + rise * 0.4, top + 10 + rise, z);
            Raylib.DrawSphereEx(p, 16 + rise * 0.2f, 8, 8, scene.LandmarkFog(steam, p));
        }
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
            float h = HeightAt(ix, iz, lvl, step);
            Color c;
            if (h <= SeaY + 0.01f) c = sea;
            else
            {
                float hx = HeightAt(ix + 1, iz, lvl, step), hz = HeightAt(ix, iz + 1, lvl, step);
                if (hx <= SeaY + 0.01f || hz <= SeaY + 0.01f || HeightAt(ix - 1, iz, lvl, step) <= SeaY + 0.01f || HeightAt(ix, iz - 1, lvl, step) <= SeaY + 0.01f)
                    c = sand;
                else
                {
                    float noise = Scene.Hash(ix * 73856093L ^ iz * 19349663L);
                    float north = Math.Clamp((float)((iz * step / M) - 4) / 5f, 0, 1);
                    // 高さで色を変える：草原 → 森 → 岩 → 雪
                    c = Lerp(Rgb(86 + (int)(noise * 18), 146 + (int)(noise * 14), 70), Rgb(150, 175, 130), north * 0.6f);
                    if (h > 25) c = Lerp(c, Rgb(52, 98, 55), Math.Clamp((h - 25) / 80, 0, 1));
                    if (h > 150) c = Lerp(c, Rgb(118, 110, 96), Math.Clamp((h - 150) / 90, 0, 1));
                    if (h > 300) c = Lerp(c, Rgb(242, 244, 248), Math.Clamp((h - 300) / 60, 0, 1));
                    // 斜面の向きで明るさを変える（立体感）
                    float shade = 1 + Math.Clamp((float)((h - hx) * 0.5 + (h - hz) * 0.8) / (float)step * 2.2f, -0.35f, 0.25f);
                    c = new Color((byte)Math.Clamp(c.R * shade, 0, 255), (byte)Math.Clamp(c.G * shade, 0, 255), (byte)Math.Clamp(c.B * shade, 0, 255), (byte)255);
                }
            }
            V(ix * step, h, iz * step, c);
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
                // 海・湖・川の上は橋：桁と欄干
                if (!IsLand((ax + bx) / 2, (az + bz) / 2))
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
                if (!IsLand(x, z) || OnRoad(x, z, 18)) continue;
                float gy = TerrainHeight(x, z);
                if (gy > 330) continue;   // 雪のある山頂には木がない
                var p = R(x, gy, z);
                float d = Vector3.Distance(p, cam);
                if (d > 520) continue;
                float h = 6 + Scene.Hash(id + 3) * 7;
                bool pine = gy > 60 || Scene.Hash(id + 4) < 0.5f;
                Raylib.DrawCylinder(p, 0.25f, 0.35f, h * 0.35f, 5, scene.FogC(Rgb(90, 65, 45), d));
                var leaf = scene.FogC(pine ? Rgb(40, 95, 50) : Rgb(70, 130, 55), d);
                var top = p + new Vector3(0, h * 0.25f, 0);
                if (pine) Raylib.DrawCylinder(top, 0, h * 0.28f, h * 0.8f, 7, leaf);
                else Raylib.DrawSphere(top + new Vector3(0, h * 0.35f, 0), h * 0.3f, leaf);
            }
        }
    }

    private void DrawCities()
    {
        foreach (var place in Places)
        {
            double range = Math.Max(1800, ViewRadius) + place.Radius;
            if ((place.X - PX) * (place.X - PX) + (place.Z - PZ) * (place.Z - PZ) > range * range) continue;
            foreach (var b in Blocks(place))
            {
                var p = R(b.X, b.Destroyed ? 1 : b.H / 2, b.Z);
                float d = Vector3.Distance(p, cam);
                if (d > Math.Max(900, ViewRadius * 0.85) || (d > 2500 && b.H < 30)) continue;
                if (b.Destroyed)
                {
                    // がれき
                    Raylib.DrawCube(p, b.W * 0.9f, 2, b.D * 0.9f, scene.FogC(Rgb(95, 90, 85), d));
                    continue;
                }
                int g = 150 + b.Tone;
                var c = scene.Night
                    ? scene.FogC(Lerp(Rgb(40, 40, 50), Rgb(230, 200, 120), (b.Tone % 10) / 18f), d, lit: true)
                    : scene.FogC(Rgb(g, g - 5, g - 15), d);
                Raylib.DrawCube(p, b.W, b.H, b.D, c);
            }
        }
    }
}
