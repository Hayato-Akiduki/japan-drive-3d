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
    public const double Lon0 = 119.0, Lat0 = 21.5, Res = 0.005;
    public const int W = 6000, H = 6700;
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
        Fill(bits, JapanOutline.AllLakes.Select(l => l.Points), false);
        // 川は線なので、太さをつけて水にする
        foreach (var (_, rank, line) in JapanOutline.AllRivers)
        {
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
/// 座標はメートル（X が東、Z が北）。地図の1度を <see cref="M"/> メートルに縮めている（実際の約15分の1）。
/// 山は実際の位置と標高（高さは実際の 0.12 倍）で地形になっていて、登ったりぶつかったりできる。
/// 描画はプレイヤー位置を原点にする（遠くまで走っても精度が落ちない）。
/// </summary>
public sealed class FreeWorld
{
    public const double M = 7500;
    public const float VScale = 0.12f;         // 実際の標高 → ゲームの高さ
    public const float SeaY = -1.2f;
    public const float RoadY = 0.18f;          // 道路面の高さ（地面とのZファイティング・草のめり込みを防ぐ）
    private const double G = 50;               // 地面のマス目
    private static readonly double Cos36 = Math.Cos(36 * Math.PI / 180);

    private readonly Scene scene;
    private readonly Random rng = new();
    public double PX, PZ;
    private Vector3 cam;

    /// <summary>描画範囲（m）。高く飛ぶほど広くなる。</summary>
    public double ViewRadius = 1000;
    public float Altitude;

    private sealed class Seg
    {
        public required double Ax, Az, Bx, Bz;
        public required Road Road;
        public required double Len, Dx, Dz;
        public double TrimA = 20, TrimB = 20;
        public int JctA = -1, JctB = -1;
    }

    private sealed class JunctionArm
    {
        public required int SegIndex;
        public required bool IsA;
        public required double Angle;
        public required double OutDx, OutDz;
        public double Trim;
    }

    private sealed class Junction
    {
        public required City City;
        public required double X, Z;
        public required float Radius;
        public required List<JunctionArm> Arms;
    }

    private readonly record struct Peak(string Name, double X, double Z, float H, float R, float Elev);
    public readonly record struct AirportInfo(string Name, double X, double Z, float Heading, double Dx, double Dz);

    private readonly List<Seg> segs = new();
    private readonly List<Junction> junctions = new();
    private readonly List<Peak> peaks = new();
    private readonly List<AirportInfo> airports = new();
    private List<int> nearSegs = new();
    private readonly Dictionary<(long, long, int), float> heightCache = new();

    public List<Place> Places { get; } = new();
    public Dictionary<Spot, (double X, double Z)> SpotPos { get; } = new();
    public HashSet<Spot> TerrainSpots { get; } = new();   // 地形そのものが名所（模型は置かない）
    public HashSet<Spot> SmokeSpots { get; } = new();     // 噴煙だけ出す
    public List<FreeCar> Cars { get; } = new();
    public IReadOnlyList<(string Name, double X, double Z, float Elev)> PeakList =>
        peaks.Select(p => (p.Name, p.X, p.Z, p.Elev)).ToList();
    public IReadOnlyList<AirportInfo> Airports => airports;

    public FreeWorld(Scene scene)
    {
        this.scene = scene;
        foreach (var r in MapData.Roads)
        {
            if (r.Type == RoadType.Ferry) continue;
            var (ax, az) = CityPos(r.A);
            var (bx, bz) = CityPos(r.B);
            double len = Math.Max(1, Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)));
            segs.Add(new Seg
            {
                Ax = ax, Az = az, Bx = bx, Bz = bz, Road = r,
                Len = len, Dx = (bx - ax) / len, Dz = (bz - az) / len,
            });
        }
        BuildJunctions();

        foreach (var (name, lat, lon, elev) in GeoFeatures.Peaks)
        {
            var (x, z) = FromLatLon(lat, lon);
            float rKm = 5 + elev / 300f;
            peaks.Add(new Peak(name, x, z, elev * VScale, (float)(rKm * M / 111.0), elev));
        }
        foreach (var (name, lat, lon, hdg) in GeoFeatures.Airports)
        {
            var (ax, az) = FromLatLon(lat, lon);
            double rad = hdg * Math.PI / 180.0;
            double dx = Math.Sin(rad), dz = Math.Cos(rad);
            // 道路から少し離した平坦な位置に滑走路を配置する
            for (int k = 0; k < 12 && RoadDistance(ax, az) < 85; k++)
            {
                ax += dz * 55;
                az -= dx * 55;
            }
            airports.Add(new AirportInfo(name, ax, az, hdg, dx, dz));
        }
        foreach (var c in MapData.Cities)
        {
            var (x, z) = CityPos(c);
            int tier = c.Name is "東京" or "大阪" or "横浜" or "名古屋" or "ソウル" or "台北" ? 0
                : c.IsGoal || c.Name is "釜山" or "仁川" or "大邱" or "台中" or "高雄" or "台南" or "豊原" ? 1 : 2;
            Places.Add(new Place { Name = c.Name, X = x, Z = z, Tier = tier, Radius = TierRadius(tier) });
        }
        foreach (var (name, lat, lon, tier) in GeoFeatures.Towns)
        {
            if (MapData.Cities.Any(c => c.Name == name)) continue;
            var (x, z) = FromLatLon(lat, lon);
            Places.Add(new Place { Name = name, X = x, Z = z, Tier = tier, Radius = TierRadius(tier) });
        }
        _ = LandRaster.Land(0, 0);   // 地図を先に作っておく
        PlaceSpots();
    }

    private void BuildJunctions()
    {
        foreach (var c in MapData.Cities)
        {
            var arms = new List<JunctionArm>();
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (s.Road.A == c)
                    arms.Add(new JunctionArm { SegIndex = i, IsA = true, OutDx = s.Dx, OutDz = s.Dz, Angle = Math.Atan2(s.Dx, s.Dz) });
                else if (s.Road.B == c)
                    arms.Add(new JunctionArm { SegIndex = i, IsA = false, OutDx = -s.Dx, OutDz = -s.Dz, Angle = Math.Atan2(-s.Dx, -s.Dz) });
            }
            if (arms.Count == 0) continue;
            arms.Sort((a, b) => a.Angle.CompareTo(b.Angle));

            float radius = arms.Count switch { 1 => 15f, 2 => 20f, 3 => 25f, 4 => 29f, _ => 33f };
            var (cx, cz) = CityPos(c);
            int ji = junctions.Count;

            for (int k = 0; k < arms.Count; k++)
            {
                var arm = arms[k];
                double trim = radius + 2;
                if (arms.Count >= 2)
                {
                    var prev = arms[(k - 1 + arms.Count) % arms.Count];
                    var next = arms[(k + 1) % arms.Count];
                    double dPrev = AngleDiff(arm.Angle, prev.Angle);
                    double dNext = AngleDiff(next.Angle, arm.Angle);
                    double minAng = Math.Min(dPrev, dNext);
                    if (minAng < 2.2)
                    {
                        // 鋭角で交わる道路同士の路肩（10.2m）まで完全に離れる距離を計算する
                        double overlap = 10.4 / Math.Tan(Math.Max(0.20, minAng * 0.5));
                        trim = Math.Max(trim, overlap + 1.5);
                    }
                }
                var seg = segs[arm.SegIndex];
                arm.Trim = Math.Min(trim, Math.Min(90.0, seg.Len * 0.35));
                if (arm.IsA) { seg.TrimA = arm.Trim; seg.JctA = ji; }
                else { seg.TrimB = arm.Trim; seg.JctB = ji; }
            }

            junctions.Add(new Junction { City = c, X = cx, Z = cz, Radius = radius, Arms = arms });
        }
    }

    private static double AngleDiff(double a, double b)
    {
        double d = (a - b) % (Math.PI * 2);
        if (d < 0) d += Math.PI * 2;
        return d > Math.PI ? Math.PI * 2 - d : d;
    }

    private static float TierRadius(int tier) => tier switch { 0 => 1150, 1 => 800, 2 => 520, _ => 330 };

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
        double len2 = s.Len * s.Len;
        t = len2 < 1e-6 ? 0 : Math.Clamp(((x - s.Ax) * dx + (z - s.Az) * dz) / len2, 0, 1);
        double px = s.Ax + dx * t - x, pz = s.Az + dz * t - z;
        return Math.Sqrt(px * px + pz * pz);
    }

    private double AirportDistance(double x, double z)
    {
        double best = double.MaxValue;
        foreach (var ap in airports)
        {
            double rx = x - ap.X, rz = z - ap.Z;
            if (Math.Abs(rx) > 650 || Math.Abs(rz) > 650) continue;
            double along = Math.Abs(rx * ap.Dx + rz * ap.Dz);
            double across = Math.Abs(-rx * ap.Dz + rz * ap.Dx);
            double da = Math.Max(0, along - 265);
            double dc = Math.Max(0, across - 26);
            double d = Math.Sqrt(da * da + dc * dc);
            if (d < best) best = d;
        }
        return best;
    }

    private double RoadDistance(double x, double z)
    {
        double best = AirportDistance(x, z);
        foreach (var s in segs) best = Math.Min(best, SegDistance(s, x, z, out _));
        foreach (var j in junctions)
        {
            double dj = Math.Sqrt((x - j.X) * (x - j.X) + (z - j.Z) * (z - j.Z)) - (j.Radius - 8);
            if (dj < best) best = Math.Max(0, dj);
        }
        return best;
    }

    private double NearRoadDistance(double x, double z)
    {
        double best = AirportDistance(x, z);
        foreach (int i in nearSegs)
        {
            var s = segs[i];
            best = Math.Min(best, SegDistance(s, x, z, out _));
            if (s.JctA >= 0)
            {
                var j = junctions[s.JctA];
                double dj = Math.Sqrt((x - j.X) * (x - j.X) + (z - j.Z) * (z - j.Z)) - (j.Radius - 8);
                if (dj < best) best = Math.Max(0, dj);
            }
            if (s.JctB >= 0)
            {
                var j = junctions[s.JctB];
                double dj = Math.Sqrt((x - j.X) * (x - j.X) + (z - j.Z) * (z - j.Z)) - (j.Radius - 8);
                if (dj < best) best = Math.Max(0, dj);
            }
        }
        return best;
    }

    /// <summary>道路（橋・交差点・滑走路）の上にいるか。</summary>
    public bool OnRoad(double x, double z, double width = 9)
    {
        if (AirportDistance(x, z) < width) return true;
        foreach (int i in nearSegs)
        {
            var s = segs[i];
            if (SegDistance(s, x, z, out _) < width) return true;
            if (s.JctA >= 0)
            {
                var j = junctions[s.JctA];
                if ((x - j.X) * (x - j.X) + (z - j.Z) * (z - j.Z) < (j.Radius + width - 8) * (j.Radius + width - 8)) return true;
            }
            if (s.JctB >= 0)
            {
                var j = junctions[s.JctB];
                if ((x - j.X) * (x - j.X) + (z - j.Z) * (z - j.Z) < (j.Radius + width - 8) * (j.Radius + width - 8)) return true;
            }
        }
        return false;
    }

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

        // 地面メッシュ（50m格子）のポリゴンが道路をまたいでも道路面（RoadY）より高くならないよう、90m以内は完全に平坦にする
        float flat = Smooth(90, 380, RoadDistance(x, z));
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
        if (!IsLand(x, z)) v = SeaY;
        else
        {
            v = TerrainHeight(x, z);
            // 上空からの粗いメッシュ（lvl > 0）でも道路の上に地面が被さらないようにする
            if (lvl > 0 && v > 0 && RoadDistance(x, z) < step * 0.9 + 30) v = 0;
        }
        heightCache[key] = v;
        return v;
    }

    /// <summary>その場所の地面の高さ（道路・陸・水）。</summary>
    public float SurfaceY(double x, double z)
    {
        double rd = NearRoadDistance(x, z);
        if (rd <= 8.2) return RoadY;
        float baseY = IsLand(x, z) ? TerrainHeight(x, z) : SeaY;
        if (rd < 10.4 && baseY > SeaY + 0.1f)
        {
            float t = (float)((rd - 8.2) / 2.2);
            return RoadY * (1 - t) + baseY * t;
        }
        return baseY;
    }

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
                double baseD = sp.Type switch
                {
                    LandmarkType.Mountain or LandmarkType.Volcano => 1900,
                    LandmarkType.Lake => 1000,
                    _ => TierRadius(city.IsGoal ? 1 : 2) + 150 + i * 120,
                };
                double baseAng = Scene.Hash(sp.Name.GetHashCode()) * Math.PI * 2 + i * Math.PI;
                (double, double) pos = (cx + Math.Sin(baseAng) * baseD, cz + Math.Cos(baseAng) * baseD);
                bool found = false;
                for (int ring = 0; ring < 3 && !found; ring++)
                {
                    double d = baseD + ring * 90;
                    for (int k = 0; k < 16; k++)
                    {
                        double a = baseAng + k * Math.PI / 8;
                        double x = cx + Math.Sin(a) * d, z = cz + Math.Cos(a) * d;
                        if (IsLand(x, z) && TerrainHeight(x, z) < 15 && RoadDistance(x, z) > 60)
                        {
                            pos = (x, z);
                            found = true;
                            break;
                        }
                    }
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

    private const double CityCell = 44;         // 街区の格子サイズ（2マス＝88mごとに街路を通す）

    private List<Block> Blocks(Place place)
    {
        if (place.Blocks != null) return place.Blocks;
        var list = new List<Block>();
        double radius = place.Radius;
        int n = (int)(radius / CityCell);
        var roadsHere = segs.Where(s => SegDistance(s, place.X, place.Z, out _) < radius + 120).ToList();
        var jctsHere = junctions.Where(j => Math.Abs(j.X - place.X) < radius + 120 && Math.Abs(j.Z - place.Z) < radius + 120).ToList();
        float maxH = place.Tier switch { 0 => 190, 1 => 115, 2 => 55, _ => 28 };

        for (int i = -n; i < n; i++)
        {
            for (int j = -n; j < n; j++)
            {
                long id = (i + 1000) * 7919L + (j + 1000) * 104729L + place.Name.GetHashCode();
                // 街路（2マス＝88mおきの境界線）を避けるため、各セルの中心（+0.5）付近に建物を配置する
                double jitterX = (Scene.Hash(id) - 0.5) * 0.14;
                double jitterZ = (Scene.Hash(id + 1) - 0.5) * 0.14;
                double x = place.X + (i + 0.5 + jitterX) * CityCell;
                double z = place.Z + (j + 0.5 + jitterZ) * CityCell;
                double d = Math.Sqrt((x - place.X) * (x - place.X) + (z - place.Z) * (z - place.Z));
                if (d > radius || Scene.Hash(id + 2) > 0.78 - d / radius * 0.38) continue;

                float w = 13 + Scene.Hash(id + 5) * 13;
                float dep = 13 + Scene.Hash(id + 6) * 13;
                double halfDiag = Math.Sqrt(w * w + dep * dep) * 0.5;

                // 四隅とも陸地であること
                if (!IsLand(x, z) || !IsLand(x - w * 0.5, z - dep * 0.5) || !IsLand(x + w * 0.5, z + dep * 0.5)) continue;
                // 高速道路・路肩・空港滑走路から建物の対角線＋余裕分（15m）以上離す
                double minRoadClearance = halfDiag + 15.0;
                if (AirportDistance(x, z) < minRoadClearance + 25.0) continue;
                if (roadsHere.Any(s => SegDistance(s, x, z, out _) < minRoadClearance)) continue;
                // 交差点広場からも確実に離す
                if (jctsHere.Any(jc => Math.Sqrt((x - jc.X) * (x - jc.X) + (z - jc.Z) * (z - jc.Z)) < jc.Radius + halfDiag + 10.0)) continue;
                // 名所の敷地内にも建てない
                bool onSpot = false;
                foreach (var (sp, spos) in SpotPos)
                {
                    if (TerrainSpots.Contains(sp)) continue;
                    double sd = Math.Sqrt((x - spos.X) * (x - spos.X) + (z - spos.Z) * (z - spos.Z));
                    if (sd < CollectRadius(sp.Type) * 0.45 + halfDiag) { onSpot = true; break; }
                }
                if (onSpot) continue;

                float near = (float)(1 - d / radius);
                float h = 8 + near * near * maxH * Scene.Hash(id + 3) + Scene.Hash(id + 4) * 10;
                list.Add(new Block
                {
                    X = x, Z = z, H = h,
                    W = w, D = dep,
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
                if (c.Y < RoadY) { c.Y = RoadY; c.Vy = Math.Abs(c.Vy) * 0.35f; c.Vx *= 0.6f; c.Vz *= 0.6f; c.Spin *= 0.5f; }
                if (c.FlyTime > 3.2f) Cars.RemoveAt(i);
                continue;
            }
            var s = segs[c.Seg];
            double len = s.Len;
            c.T += c.Dir * c.Speed * dt / len;
            c.Dist += c.Speed * dt;
            if (c.T < 0 || c.T > 1)
            {
                // 交差点に着いたら消えずに次の道路へ乗り継ぐ
                if (!TransferCarAtJunction(c)) { Cars.RemoveAt(i); continue; }
            }
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
            if (t < 0.02 || t > 0.98) continue;
            double roll = rng.NextDouble();
            var kind = roll < 0.22 ? VehicleKind.Truck : roll < 0.42 ? VehicleKind.Kei
                : roll < 0.65 ? VehicleKind.Minivan : roll < 0.7 ? VehicleKind.Sports : VehicleKind.Sedan;
            var car = new FreeCar
            {
                Seg = si, T = t, Dir = rng.NextDouble() < 0.5 ? 1 : -1,
                Lane = rng.NextDouble() < 0.6 ? 5.6f : 2.4f,
                Speed = kind == VehicleKind.Truck ? 22 + (float)rng.NextDouble() * 3 : 24 + (float)rng.NextDouble() * 8,
                Color = Palette[rng.Next(Palette.Length)], Kind = kind, Y = RoadY,
            };
            SetCarPos(car);
            double dx = car.X - PX, dz = car.Z - PZ;
            if (dx * dx + dz * dz < 150 * 150) continue;
            Cars.Add(car);
        }
    }

    private bool TransferCarAtJunction(FreeCar c)
    {
        var cur = segs[c.Seg];
        int ji = c.T > 1 ? cur.JctB : cur.JctA;
        if (ji < 0) return false;
        var jct = junctions[ji];
        if (jct.Arms.Count == 0) return false;

        var candidates = jct.Arms.Where(a => a.SegIndex != c.Seg).ToList();
        var nextArm = candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : jct.Arms[0];
        var nextSeg = segs[nextArm.SegIndex];
        c.Seg = nextArm.SegIndex;
        c.Dir = nextArm.IsA ? 1 : -1;
        double startDist = Math.Min(nextArm.Trim * 0.6, nextSeg.Len * 0.2);
        c.T = nextArm.IsA ? startDist / nextSeg.Len : 1.0 - startDist / nextSeg.Len;
        return true;
    }

    private void SetCarPos(FreeCar c)
    {
        var s = segs[c.Seg];
        double dx = s.Dx * c.Dir, dz = s.Dz * c.Dir;
        // 左側通行：進行方向の左へずらす（左向きの法線は (-dz, dx)）
        c.X = s.Ax + (s.Bx - s.Ax) * c.T - dz * c.Lane;
        c.Z = s.Az + (s.Bz - s.Az) * c.T + dx * c.Lane;
        c.Y = RoadY;
        c.Heading = (float)(Math.Atan2(dx, dz) * 180 / Math.PI);
    }

    public FreeCar? HitTest(double x, double z, float y, float radius = 2.4f)
    {
        foreach (var c in Cars)
        {
            if (c.Flying) continue;
            double dx = c.X - x, dz = c.Z - z;
            float r = radius + (c.Kind == VehicleKind.Truck ? 2.5f : 0.6f);
            if (dx * dx + dz * dz < r * r && y < (c.Kind == VehicleKind.Truck ? 4.3f : 2.5f) && y > -0.5f) return c;
        }
        return null;
    }

    public void Knock(FreeCar c, double dirX, double dirZ, float power)
    {
        c.Flying = true;
        c.FlyTime = 0;
        c.Y = RoadY + 0.2f;
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
        DrawCityStreets();
        DrawAirports(time);
        DrawRoads();
        DrawRoadDetails(time);
        DrawJunctions(time);
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

    private float UrbanBlend(double x, double z)
    {
        float u = 0;
        foreach (var pl in Places)
        {
            double dx = x - pl.X, dz = z - pl.Z;
            double lim = pl.Radius * 1.05;
            if (Math.Abs(dx) > lim || Math.Abs(dz) > lim) continue;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d < lim)
            {
                float v = 1f - Smooth(pl.Radius * 0.45, lim, d);
                if (v > u) u = v;
            }
        }
        return u;
    }

    private void DrawGround()
    {
        int lvl = 0;
        while (ViewRadius / (G * (1 << lvl)) > 42) lvl++;
        double step = G * (1 << lvl);
        long cx = (long)Math.Floor(PX / step), cz = (long)Math.Floor(PZ / step);
        int n = (int)(ViewRadius / step);
        var sea = Rgb(36, 94, 158);
        var shallowSea = Rgb(50, 134, 184);
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
            double wx = ix * step, wz = iz * step;
            float h = HeightAt(ix, iz, lvl, step);
            Color c;
            if (h <= SeaY + 0.01f)
            {
                bool nearShore = HeightAt(ix + 1, iz, lvl, step) > SeaY + 0.01f || HeightAt(ix - 1, iz, lvl, step) > SeaY + 0.01f
                    || HeightAt(ix, iz + 1, lvl, step) > SeaY + 0.01f || HeightAt(ix, iz - 1, lvl, step) > SeaY + 0.01f;
                c = nearShore ? shallowSea : sea;
            }
            else
            {
                float ub = h < 15 ? UrbanBlend(wx, wz) : 0;
                float hx = HeightAt(ix + 1, iz, lvl, step), hz = HeightAt(ix, iz + 1, lvl, step);
                if (hx <= SeaY + 0.01f || hz <= SeaY + 0.01f || HeightAt(ix - 1, iz, lvl, step) <= SeaY + 0.01f || HeightAt(ix, iz - 1, lvl, step) <= SeaY + 0.01f)
                    c = ub > 0.25f ? Rgb(148, 150, 154) : sand;
                else
                {
                    float noise = Scene.Hash(ix * 73856093L ^ iz * 19349663L);
                    float north = Math.Clamp((float)((wz / M) - 4) / 5f, 0, 1);
                    // 高さで色を変える：草原 → 森 → 高山帯 → 岩稜 → 冠雪
                    c = Lerp(Rgb(86 + (int)(noise * 18), 146 + (int)(noise * 14), 70), Rgb(150, 175, 130), north * 0.6f);
                    if (h > 25) c = Lerp(c, Rgb(52, 98, 55), Math.Clamp((h - 25) / 80, 0, 1));
                    if (h > 150) c = Lerp(c, Rgb(118, 110, 96), Math.Clamp((h - 150) / 90, 0, 1));
                    if (h > 300) c = Lerp(c, Rgb(242, 244, 248), Math.Clamp((h - 300) / 60, 0, 1));

                    // 市街地（都市・町）の中はコンクリートの街区色になじませる
                    if (ub > 0.01f)
                    {
                        var urbanCol = Rgb(140 + (int)(noise * 10), 142 + (int)(noise * 10), 146 + (int)(noise * 10));
                        c = Lerp(c, urbanCol, ub * 0.85f);
                    }

                    // 斜面の向きで明るさを変える（立体感）
                    float shade = 1 + Math.Clamp((float)((h - hx) * 0.5 + (h - hz) * 0.8) / (float)step * 2.2f, -0.35f, 0.25f);
                    c = new Color((byte)Math.Clamp(c.R * shade, 0, 255), (byte)Math.Clamp(c.G * shade, 0, 255), (byte)Math.Clamp(c.B * shade, 0, 255), (byte)255);
                }
            }
            V(wx, h, wz, c);
        }
    }

    /// <summary>全国の主要空港（滑走路・誘導路・管制塔・進入灯）を描画する。</summary>
    private void DrawAirports(float time)
    {
        var rwyCol = Rgb(56, 58, 64);
        var shoulderCol = Rgb(118, 120, 124);
        var taxiCol = Rgb(76, 78, 84);
        var white = Rgb(238, 238, 238);
        var yellow = Rgb(240, 200, 50);

        Rlgl.Begin(DrawMode.Quads);
        foreach (var ap in airports)
        {
            double dist2 = (ap.X - PX) * (ap.X - PX) + (ap.Z - PZ) * (ap.Z - PZ);
            double maxR = ViewRadius + 600;
            if (dist2 > maxR * maxR) continue;

            double dx = ap.Dx, dz = ap.Dz;
            double nx = -dz, nz = dx;
            const double halfL = 250;

            void Quad(double s0, double s1, double w0, double w1, float y, Color c)
            {
                double ax = ap.X + dx * s0, az = ap.Z + dz * s0;
                double bx = ap.X + dx * s1, bz = ap.Z + dz * s1;
                V(ax + nx * w0, y, az + nz * w0, c);
                V(ax + nx * w1, y, az + nz * w1, c);
                V(bx + nx * w1, y, bz + nz * w1, c);
                V(bx + nx * w0, y, bz + nz * w0, c);
            }

            // 敷地エプロン・平行誘導路
            Quad(-halfL - 15, halfL + 15, -24, 38, RoadY - 0.02f, shoulderCol);
            Quad(-halfL * 0.75, halfL * 0.75, 18, 30, RoadY, taxiCol);
            Quad(-halfL * 0.75, halfL * 0.75, 23.7, 24.3, RoadY + 0.02f, yellow);

            // 滑走路本体（500m × 26m）
            Quad(-halfL, halfL, -13, 13, RoadY + 0.01f, rwyCol);
            Quad(-halfL, halfL, -12.6, -12.1, RoadY + 0.03f, white);
            Quad(-halfL, halfL, 12.1, 12.6, RoadY + 0.03f, white);

            // センターライン破線とピアノキー標識
            for (double s = -halfL + 28; s < halfL - 28; s += 24)
                Quad(s, s + 12, -0.35, 0.35, RoadY + 0.035f, white);

            for (int k = -4; k <= 4; k++)
            {
                if (k == 0) continue;
                double w0 = k * 2.4 - 0.65, w1 = k * 2.4 + 0.65;
                Quad(-halfL + 6, -halfL + 22, w0, w1, RoadY + 0.035f, white);
                Quad(halfL - 22, halfL - 6, w0, w1, RoadY + 0.035f, white);
            }
        }
        Rlgl.End();

        // 管制塔・ターミナル・滑走路灯
        foreach (var ap in airports)
        {
            double dist2 = (ap.X - PX) * (ap.X - PX) + (ap.Z - PZ) * (ap.Z - PZ);
            if (dist2 > 1400 * 1400) continue;
            double dx = ap.Dx, dz = ap.Dz;
            double nx = -dz, nz = dx;

            // ターミナルビルと管制塔（滑走路横 +46m）
            double tx = ap.X + nx * 46, tz = ap.Z + nz * 46;
            var termPos = R(tx, RoadY + 5f, tz);
            float d = Vector3.Distance(termPos, cam);
            if (d < 1300)
            {
                Raylib.DrawCube(termPos, 28, 10, 28, scene.FogC(Rgb(210, 215, 222), d));
                var towerBase = R(tx + nx * 6, RoadY, tz + nz * 6);
                Raylib.DrawCylinder(towerBase, 2.6f, 3.2f, 28f, 8, scene.FogC(Rgb(230, 232, 236), d));
                Raylib.DrawCylinder(towerBase + new Vector3(0, 28f, 0), 4.8f, 3.4f, 4.5f, 8, scene.FogC(Rgb(90, 165, 215), d, lit: scene.Night));
                Raylib.DrawCylinder(towerBase + new Vector3(0, 32.5f, 0), 4.2f, 5.0f, 1.2f, 8, scene.FogC(Rgb(235, 235, 240), d));
                // 回転ビーコン灯
                bool beaconOn = ((int)(time * 4) & 1) == 0;
                var bCol = beaconOn ? Rgb(80, 255, 130) : Rgb(255, 255, 255);
                Raylib.DrawSphere(towerBase + new Vector3(0, 35.2f, 0), 0.9f, scene.FogC(bCol, d, lit: true));
            }

            // 滑走路端の誘導灯
            if (d < 750)
            {
                for (double s = -240; s <= 240; s += 40)
                {
                    var lp1 = R(ap.X + dx * s - nx * 13.5, RoadY + 0.35f, ap.Z + dz * s - nz * 13.5);
                    var lp2 = R(ap.X + dx * s + nx * 13.5, RoadY + 0.35f, ap.Z + dz * s + nz * 13.5);
                    var lCol = Math.Abs(s) > 200 ? Rgb(255, 70, 60) : Rgb(255, 235, 160);
                    Raylib.DrawSphere(lp1, 0.32f, scene.FogC(lCol, d, lit: true));
                    Raylib.DrawSphere(lp2, 0.32f, scene.FogC(lCol, d, lit: true));
                }
            }
        }
    }

    /// <summary>都市・町の内部に碁盤の目の一般道（街路）を描く。</summary>
    private void DrawCityStreets()
    {
        if (Altitude > 550) return;
        const double gridStep = CityCell * 2; // 88m間隔で街路を通す
        const double sliceLen = 22;
        const float streetY = 0.07f;
        var streetCol = Rgb(84, 86, 92);
        var walkCol = Rgb(168, 168, 164);

        Rlgl.Begin(DrawMode.Quads);
        foreach (var pl in Places)
        {
            double maxRange = pl.Radius + Math.Min(900, ViewRadius);
            if ((pl.X - PX) * (pl.X - PX) + (pl.Z - PZ) * (pl.Z - PZ) > maxRange * maxRange) continue;

            int lines = (int)(pl.Radius * 0.88 / gridStep);
            int slices = (int)(pl.Radius * 0.88 / sliceLen);

            // 南北方向の街路
            for (int k = -lines; k <= lines; k++)
            {
                double sx = pl.X + k * gridStep;
                if (Math.Abs(sx - PX) > 750) continue;
                for (int s = -slices; s < slices; s++)
                {
                    double z0 = pl.Z + s * sliceLen, z1 = z0 + sliceLen;
                    double mz = (z0 + z1) * 0.5;
                    if (Math.Abs(mz - PZ) > 750) continue;
                    if ((sx - pl.X) * (sx - pl.X) + (mz - pl.Z) * (mz - pl.Z) > pl.Radius * pl.Radius * 0.82) continue;
                    if (!IsLand(sx, mz) || TerrainHeight(sx, mz) > 8) continue;
                    if (NearRoadDistance(sx, mz) < 11.5) continue;

                    // 歩道と車道
                    V(sx - 5.2, streetY - 0.01f, z0, walkCol); V(sx + 5.2, streetY - 0.01f, z0, walkCol);
                    V(sx + 5.2, streetY - 0.01f, z1, walkCol); V(sx - 5.2, streetY - 0.01f, z1, walkCol);

                    V(sx - 3.8, streetY, z0, streetCol); V(sx + 3.8, streetY, z0, streetCol);
                    V(sx + 3.8, streetY, z1, streetCol); V(sx - 3.8, streetY, z1, streetCol);
                }
            }

            // 東西方向の街路
            for (int k = -lines; k <= lines; k++)
            {
                double sz = pl.Z + k * gridStep;
                if (Math.Abs(sz - PZ) > 750) continue;
                for (int s = -slices; s < slices; s++)
                {
                    double x0 = pl.X + s * sliceLen, x1 = x0 + sliceLen;
                    double mx = (x0 + x1) * 0.5;
                    if (Math.Abs(mx - PX) > 750) continue;
                    if ((mx - pl.X) * (mx - pl.X) + (sz - pl.Z) * (sz - pl.Z) > pl.Radius * pl.Radius * 0.82) continue;
                    if (!IsLand(mx, sz) || TerrainHeight(mx, sz) > 8) continue;
                    if (NearRoadDistance(mx, sz) < 11.5) continue;

                    V(x0, streetY - 0.01f, sz - 5.2, walkCol); V(x1, streetY - 0.01f, sz - 5.2, walkCol);
                    V(x1, streetY - 0.01f, sz + 5.2, walkCol); V(x0, streetY - 0.01f, sz + 5.2, walkCol);

                    V(x0, streetY + 0.005f, sz - 3.8, streetCol); V(x1, streetY + 0.005f, sz - 3.8, streetCol);
                    V(x1, streetY + 0.005f, sz + 3.8, streetCol); V(x0, streetY + 0.005f, sz + 3.8, streetCol);
                }
            }
        }
        Rlgl.End();
    }

    private void DrawRoads()
    {
        var asphalt = Rgb(68, 70, 76);
        var shoulder = Rgb(126, 128, 132);
        var median = Rgb(150, 150, 146);
        var white = Rgb(235, 235, 235);
        var girder = Rgb(190, 80, 55);
        var rail = Rgb(225, 225, 220);

        Rlgl.Begin(DrawMode.Quads);
        foreach (int si in nearSegs)
        {
            var s = segs[si];
            double d0 = SegDistance(s, PX, PZ, out double t0);
            if (d0 > ViewRadius) continue;
            double len = s.Len;
            double h = Math.Sqrt(ViewRadius * ViewRadius - d0 * d0);
            double sMin = Math.Max(0, t0 * len - h), sMax = Math.Min(len, t0 * len + h);
            double dx = s.Dx, dz = s.Dz;
            double nx = -dz, nz = dx;

            void DrawSlice(double a, double b, bool inJunctionNeck, int jctIndex, int dashIdx)
            {
                if (b <= a + 0.05) return;
                double ax = s.Ax + dx * a, az = s.Az + dz * a, bx = s.Ax + dx * b, bz = s.Az + dz * b;
                double mx = (ax + bx) * 0.5, mz = (az + bz) * 0.5;

                void Strip(double w0, double w1, float y0, float y1, Color c)
                {
                    V(ax + nx * w0, y0, az + nz * w0, c);
                    V(ax + nx * w1, y1, az + nz * w1, c);
                    V(bx + nx * w1, y1, bz + nz * w1, c);
                    V(bx + nx * w0, y0, bz + nz * w0, c);
                }
                void Side(double w, float y0, float y1, Color c)
                {
                    V(ax + nx * w, y0, az + nz * w, c);
                    V(ax + nx * w, y1, az + nz * w, c);
                    V(bx + nx * w, y1, bz + nz * w, c);
                    V(bx + nx * w, y0, bz + nz * w, c);
                }

                // アスファルト本体（常に隙間なく描く）
                Strip(-8.0, 8.0, RoadY, RoadY, asphalt);

                if (!inJunctionNeck)
                {
                    // 通常区間：路肩の法面・中央分離帯・白線・破線・橋桁をすべて描く
                    bool onLand = IsLand(mx, mz);
                    if (onLand)
                    {
                        Strip(-10.2, -8.0, -0.04f, RoadY, shoulder);
                        Strip(8.0, 10.2, RoadY, -0.04f, shoulder);
                    }
                    else
                    {
                        float deep = s.Road.Type == RoadType.MegaBridge ? -12 : SeaY;
                        Side(-8.0, deep, RoadY, girder);
                        Side(8.0, deep, RoadY, girder);
                        Side(-8.0, RoadY, RoadY + 1.15f, rail);
                        Side(8.0, RoadY, RoadY + 1.15f, rail);
                    }

                    Strip(-0.5, 0.5, RoadY + 0.04f, RoadY + 0.04f, median);
                    Strip(-7.8, -7.55, RoadY + 0.03f, RoadY + 0.03f, white);
                    Strip(7.55, 7.8, RoadY + 0.03f, RoadY + 0.03f, white);
                    if (dashIdx % 2 == 0)
                    {
                        Strip(-4.1, -3.9, RoadY + 0.03f, RoadY + 0.03f, white);
                        Strip(3.9, 4.1, RoadY + 0.03f, RoadY + 0.03f, white);
                    }
                }
                else if (jctIndex >= 0)
                {
                    // 交差点アプローチ部：他の道路と近い内側はアスファルトで繋ぎ、外側の縁だけ白線と路肩を描く
                    var jct = junctions[jctIndex];
                    double distJct = Math.Sqrt((mx - jct.X) * (mx - jct.X) + (mz - jct.Z) * (mz - jct.Z));
                    if (distJct >= jct.Radius - 1.5)
                    {
                        bool leftFree = IsEdgeClearOfOtherArms(jct, si, mx - nx * 9.0, mz - nz * 9.0, 10.5);
                        bool rightFree = IsEdgeClearOfOtherArms(jct, si, mx + nx * 9.0, mz + nz * 9.0, 10.5);
                        if (leftFree)
                        {
                            Strip(-10.2, -8.0, -0.04f, RoadY, shoulder);
                            Strip(-7.8, -7.55, RoadY + 0.03f, RoadY + 0.03f, white);
                        }
                        else Strip(-11.0, -7.8, RoadY, RoadY, asphalt);

                        if (rightFree)
                        {
                            Strip(8.0, 10.2, RoadY, -0.04f, shoulder);
                            Strip(7.55, 7.8, RoadY + 0.03f, RoadY + 0.03f, white);
                        }
                        else Strip(7.8, 11.0, RoadY, RoadY, asphalt);
                    }
                }
            }

            const double markOffset = 5.5; // 横断歩道・停止線のスペース分だけ中央分離帯の開始位置を奥にする
            double neckA = Math.Min(s.Len * 0.45, s.TrimA + markOffset);
            double neckB = Math.Min(s.Len * 0.45, s.TrimB + markOffset);

            // 1) A側の交差点ネック区間 [0, neckA]
            if (sMin < neckA)
            {
                double na = Math.Max(0, sMin), nb = Math.Min(neckA, sMax);
                const double neckStep = 4.0;
                int nk0 = (int)Math.Floor(na / neckStep), nk1 = (int)Math.Ceiling(nb / neckStep);
                for (int k = nk0; k < nk1; k++)
                    DrawSlice(Math.Max(na, k * neckStep), Math.Min(nb, (k + 1) * neckStep), true, s.JctA, k);
            }

            // 2) 中央の通常道路区間 [neckA, len - neckB]
            double bodyStart = neckA, bodyEnd = Math.Max(neckA, len - neckB);
            if (sMax > bodyStart && sMin < bodyEnd)
            {
                double ba = Math.Max(bodyStart, sMin), bb = Math.Min(bodyEnd, sMax);
                double step = 25 * Math.Max(1, ViewRadius / 1000);
                int k0 = (int)Math.Floor((ba - bodyStart) / step), k1 = (int)Math.Ceiling((bb - bodyStart) / step);
                for (int k = k0; k < k1; k++)
                {
                    double a = Math.Max(ba, bodyStart + k * step);
                    double b = Math.Min(bb, bodyStart + (k + 1) * step);
                    DrawSlice(a, b, false, -1, k);
                }
            }

            // 3) B側の交差点ネック区間 [len - neckB, len]
            if (sMax > bodyEnd)
            {
                double na = Math.Max(bodyEnd, sMin), nb = Math.Min(len, sMax);
                const double neckStep = 4.0;
                int nk0 = (int)Math.Floor((na - bodyEnd) / neckStep), nk1 = (int)Math.Ceiling((nb - bodyEnd) / neckStep);
                for (int k = nk0; k < nk1; k++)
                    DrawSlice(Math.Max(na, bodyEnd + k * neckStep), Math.Min(nb, bodyEnd + (k + 1) * neckStep), true, s.JctB, k);
            }
        }
        Rlgl.End();
    }

    /// <summary>高速道路の街灯・キロポスト、および海上大橋の橋脚・主塔を描画する。</summary>
    private void DrawRoadDetails(float time)
    {
        if (Altitude > 550) return;
        double maxDist = Math.Min(750, ViewRadius);
        foreach (int si in nearSegs)
        {
            var s = segs[si];
            double d0 = SegDistance(s, PX, PZ, out double t0);
            if (d0 > maxDist) continue;
            double len = s.Len;
            double h = Math.Sqrt(maxDist * maxDist - d0 * d0);
            double bodyStart = Math.Min(len * 0.45, s.TrimA + 16);
            double bodyEnd = Math.Max(bodyStart, len - Math.Min(len * 0.45, s.TrimB + 16));
            double sMin = Math.Max(bodyStart, t0 * len - h);
            double sMax = Math.Min(bodyEnd, t0 * len + h);
            if (sMax <= sMin) continue;

            double dx = s.Dx, dz = s.Dz;
            double nx = -dz, nz = dx;

            const double poleStep = 90.0;
            int k0 = (int)Math.Ceiling(sMin / poleStep);
            int k1 = (int)Math.Floor(sMax / poleStep);
            for (int k = k0; k <= k1; k++)
            {
                double distAlong = k * poleStep;
                double mx = s.Ax + dx * distAlong, mz = s.Az + dz * distAlong;
                var midPos = R(mx, RoadY, mz);
                float d = Vector3.Distance(midPos, cam);
                if (d > maxDist) continue;

                bool onLand = IsLand(mx, mz);
                if (!onLand)
                {
                    // 海上橋のコンクリート橋脚
                    var pierCol = scene.FogC(Rgb(165, 168, 174), d);
                    Raylib.DrawCube(R(mx - nx * 5.8, RoadY - 6f, mz - nz * 5.8), 1.8f, 12f, 1.8f, pierCol);
                    Raylib.DrawCube(R(mx + nx * 5.8, RoadY - 6f, mz + nz * 5.8), 1.8f, 12f, 1.8f, pierCol);

                    // 3スパン（270m）ごとに吊り橋・斜張橋の主塔を建てる
                    if (k % 3 == 0)
                    {
                        var towerCol = scene.FogC(s.Road.Type == RoadType.MegaBridge ? Rgb(230, 85, 55) : Rgb(232, 235, 240), d);
                        var leftT = R(mx - nx * 8.6, RoadY, mz - nz * 8.6);
                        var rightT = R(mx + nx * 8.6, RoadY, mz + nz * 8.6);
                        Raylib.DrawCylinder(leftT, 0.9f, 1.3f, 34f, 6, towerCol);
                        Raylib.DrawCylinder(rightT, 0.9f, 1.3f, 34f, 6, towerCol);
                        Raylib.DrawLine3D(leftT + new Vector3(0, 26f, 0), rightT + new Vector3(0, 26f, 0), towerCol);
                        Raylib.DrawLine3D(leftT + new Vector3(0, 32f, 0), rightT + new Vector3(0, 32f, 0), towerCol);
                        // 航空障害灯
                        Raylib.DrawSphere(leftT + new Vector3(0, 34.6f, 0), 0.45f, scene.FogC(Rgb(255, 60, 50), d, lit: true));
                        Raylib.DrawSphere(rightT + new Vector3(0, 34.6f, 0), 0.45f, scene.FogC(Rgb(255, 60, 50), d, lit: true));
                    }
                }
                else if (d < 480)
                {
                    // 陸上の高速道路：左右交互の照明ポールと緑のキロポスト
                    int side = (k & 1) == 0 ? 1 : -1;
                    double lx = mx + nx * (8.4 * side), lz = mz + nz * (8.4 * side);
                    var poleBase = R(lx, RoadY, lz);
                    var poleCol = scene.FogC(Rgb(170, 174, 180), d);
                    Raylib.DrawCylinder(poleBase, 0.10f, 0.13f, 6.6f, 5, poleCol);
                    var lampPos = R(mx + nx * (6.6 * side), RoadY + 6.6f, mz + nz * (6.6 * side));
                    Raylib.DrawLine3D(poleBase + new Vector3(0, 6.5f, 0), lampPos, poleCol);
                    var bulbCol = scene.Night ? Rgb(255, 220, 140) : Rgb(225, 230, 235);
                    Raylib.DrawSphere(lampPos, 0.24f, scene.FogC(bulbCol, d, lit: scene.Night));
                }
            }
        }
    }

    private bool IsEdgeClearOfOtherArms(Junction jct, int selfSeg, double x, double z, double minDist = 8.4)
    {
        foreach (var arm in jct.Arms)
        {
            if (arm.SegIndex == selfSeg) continue;
            if (SegDistance(segs[arm.SegIndex], x, z, out _) < minDist) return false;
        }
        return true;
    }

    /// <summary>交差点（JCT・ロータリー・横断歩道・停止線・信号機）を描画する。</summary>
    private void DrawJunctions(float time)
    {
        var asphalt = Rgb(68, 70, 76);
        var curb = Rgb(168, 170, 174);
        var shoulder = Rgb(126, 128, 132);
        var white = Rgb(238, 238, 238);
        var islandRing = Rgb(235, 195, 55);
        var islandTop = Rgb(175, 178, 182);
        var islandGrass = Rgb(78, 138, 68);

        Rlgl.Begin(DrawMode.Quads);
        foreach (var j in junctions)
        {
            double dist2 = (j.X - PX) * (j.X - PX) + (j.Z - PZ) * (j.Z - PZ);
            double maxR = ViewRadius + j.Radius + 80;
            if (dist2 > maxR * maxR) continue;

            const int slices = 32;
            float r = j.Radius;
            float rOut = r + 2.0f;

            // 円形の交差点アスファルト広場と外周の縁石・白線
            for (int i = 0; i < slices; i++)
            {
                double a0 = i * Math.PI * 2 / slices;
                double a1 = (i + 1) * Math.PI * 2 / slices;
                double amid = (a0 + a1) * 0.5;
                double s0 = Math.Sin(a0), c0 = Math.Cos(a0);
                double s1 = Math.Sin(a1), c1 = Math.Cos(a1);

                // アスファルト円盤（中心からの扇形を四角形で描画）
                V(j.X, RoadY + 0.005f, j.Z, asphalt);
                V(j.X + s0 * r, RoadY + 0.005f, j.Z + c0 * r, asphalt);
                V(j.X + s1 * r, RoadY + 0.005f, j.Z + c1 * r, asphalt);
                V(j.X, RoadY + 0.005f, j.Z, asphalt);

                // 道路が接続していない外周部分には路肩と外側白線・縁石を描く
                double mx = j.X + Math.Sin(amid) * r, mz = j.Z + Math.Cos(amid) * r;
                if (IsEdgeClearOfOtherArms(j, -1, mx, mz, 9.6))
                {
                    V(j.X + s0 * r, RoadY, j.Z + c0 * r, shoulder);
                    V(j.X + s0 * rOut, -0.04f, j.Z + c0 * rOut, shoulder);
                    V(j.X + s1 * rOut, -0.04f, j.Z + c1 * rOut, shoulder);
                    V(j.X + s1 * r, RoadY, j.Z + c1 * r, shoulder);

                    V(j.X + s0 * (r - 0.55), RoadY + 0.03f, j.Z + c0 * (r - 0.55), white);
                    V(j.X + s0 * (r - 0.25), RoadY + 0.03f, j.Z + c0 * (r - 0.25), white);
                    V(j.X + s1 * (r - 0.25), RoadY + 0.03f, j.Z + c1 * (r - 0.25), white);
                    V(j.X + s1 * (r - 0.55), RoadY + 0.03f, j.Z + c1 * (r - 0.55), white);

                    V(j.X + s0 * r, RoadY + 0.14f, j.Z + c0 * r, curb);
                    V(j.X + s0 * (r + 0.9), RoadY + 0.14f, j.Z + c0 * (r + 0.9), curb);
                    V(j.X + s1 * (r + 0.9), RoadY + 0.14f, j.Z + c1 * (r + 0.9), curb);
                    V(j.X + s1 * r, RoadY + 0.14f, j.Z + c1 * r, curb);
                }
            }

            // 3方向以上の交差点の中央にはロータリー導流島（中央島）を描く
            if (j.Arms.Count >= 3 && dist2 < 900 * 900)
            {
                float ri = j.Arms.Count >= 4 ? 5.2f : 3.8f;
                const int islices = 20;
                for (int i = 0; i < islices; i++)
                {
                    double a0 = i * Math.PI * 2 / islices;
                    double a1 = (i + 1) * Math.PI * 2 / islices;
                    double s0 = Math.Sin(a0), c0 = Math.Cos(a0);
                    double s1 = Math.Sin(a1), c1 = Math.Cos(a1);

                    // 黄色の反射リング
                    V(j.X + s0 * ri, RoadY + 0.035f, j.Z + c0 * ri, islandRing);
                    V(j.X + s0 * (ri + 0.45f), RoadY + 0.035f, j.Z + c0 * (ri + 0.45f), islandRing);
                    V(j.X + s1 * (ri + 0.45f), RoadY + 0.035f, j.Z + c1 * (ri + 0.45f), islandRing);
                    V(j.X + s1 * ri, RoadY + 0.035f, j.Z + c1 * ri, islandRing);

                    // 縁石の上面
                    V(j.X, RoadY + 0.18f, j.Z, islandTop);
                    V(j.X + s0 * ri, RoadY + 0.18f, j.Z + c0 * ri, islandTop);
                    V(j.X + s1 * ri, RoadY + 0.18f, j.Z + c1 * ri, islandTop);
                    V(j.X, RoadY + 0.18f, j.Z, islandTop);

                    // 内側の植栽
                    float rg = ri - 0.7f;
                    V(j.X, RoadY + 0.21f, j.Z, islandGrass);
                    V(j.X + s0 * rg, RoadY + 0.21f, j.Z + c0 * rg, islandGrass);
                    V(j.X + s1 * rg, RoadY + 0.21f, j.Z + c1 * rg, islandGrass);
                    V(j.X, RoadY + 0.21f, j.Z, islandGrass);
                }
            }

            // 各接続道路の入口に横断歩道と停止線を描く
            if (dist2 < 700 * 700)
            {
                foreach (var arm in j.Arms)
                {
                    var seg = segs[arm.SegIndex];
                    if (arm.Trim + 8 > seg.Len * 0.45) continue;
                    double dx = arm.OutDx, dz = arm.OutDz;
                    double nx = -dz, nz = dx;
                    double baseDist = arm.Trim + 0.8;
                    double bx = j.X + dx * baseDist, bz = j.Z + dz * baseDist;

                    // 横断歩道（縞模様）
                    for (int s = -3; s <= 3; s++)
                    {
                        double w0 = s * 1.9 - 0.45, w1 = s * 1.9 + 0.45;
                        double d0 = 0.6, d1 = 3.2;
                        V(bx + dx * d0 + nx * w0, RoadY + 0.035f, bz + dz * d0 + nz * w0, white);
                        V(bx + dx * d0 + nx * w1, RoadY + 0.035f, bz + dz * d0 + nz * w1, white);
                        V(bx + dx * d1 + nx * w1, RoadY + 0.035f, bz + dz * d1 + nz * w1, white);
                        V(bx + dx * d1 + nx * w0, RoadY + 0.035f, bz + dz * d1 + nz * w0, white);
                    }

                    // 停止線（交差点へ進入する左側車線：nx の正方向側 0.8m〜7.5m）
                    double sd0 = 4.4, sd1 = 4.9;
                    V(bx + dx * sd0 + nx * 0.8, RoadY + 0.035f, bz + dz * sd0 + nz * 0.8, white);
                    V(bx + dx * sd0 + nx * 7.5, RoadY + 0.035f, bz + dz * sd0 + nz * 7.5, white);
                    V(bx + dx * sd1 + nx * 7.5, RoadY + 0.035f, bz + dz * sd1 + nz * 7.5, white);
                    V(bx + dx * sd1 + nx * 0.8, RoadY + 0.035f, bz + dz * sd1 + nz * 0.8, white);
                }
            }
        }
        Rlgl.End();

        // 交差点の信号機と照明灯（3Dオブジェクト）
        if (Altitude > 300) return;
        foreach (var j in junctions)
        {
            double dist2 = (j.X - PX) * (j.X - PX) + (j.Z - PZ) * (j.Z - PZ);
            if (dist2 > 420 * 420) continue;

            for (int ai = 0; ai < j.Arms.Count; ai++)
            {
                var arm = j.Arms[ai];
                var seg = segs[arm.SegIndex];
                if (arm.Trim + 8 > seg.Len * 0.45) continue;
                double dx = arm.OutDx, dz = arm.OutDz;
                double nx = -dz, nz = dx;
                double px = j.X + dx * (arm.Trim + 2.0) + nx * 8.8;
                double pz = j.Z + dz * (arm.Trim + 2.0) + nz * 8.8;
                var polePos = R(px, RoadY, pz);
                float d = Vector3.Distance(polePos, cam);
                if (d > 400) continue;

                var poleCol = scene.FogC(Rgb(165, 168, 172), d);
                Raylib.DrawCylinder(polePos, 0.14f, 0.16f, 5.8f, 6, poleCol);

                // 横腕と信号ボックス
                double armX = px - nx * 2.2, armZ = pz - nz * 2.2;
                var topPole = R(px, RoadY + 5.5f, pz);
                var boxPos = R(armX, RoadY + 5.5f, armZ);
                Raylib.DrawLine3D(topPole, boxPos, poleCol);
                Raylib.DrawCube(boxPos, 0.9f, 0.45f, 0.9f, scene.FogC(Rgb(45, 48, 52), d));

                // 信号の色（方向ごとに青・黄・赤を交互に切り替え）
                float cycle = (time + (ai % 2) * 9f) % 18f;
                Color sigCol = cycle < 7.5f ? Rgb(50, 240, 110) : cycle < 9.0f ? Rgb(255, 210, 40) : Rgb(255, 55, 55);
                var lightPos = R(armX + dx * 0.35, RoadY + 5.5f, armZ + dz * 0.35);
                Raylib.DrawSphere(lightPos, 0.24f, scene.FogC(sigCol, d, lit: true));
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
                // 道路・路肩・交差点から25m以内には木を生やさない
                if (!IsLand(x, z) || NearRoadDistance(x, z) < 25) continue;
                // 市街地の中心部や建物の敷地内には野生の木を生やさない
                if (UrbanBlend(x, z) > 0.35f) continue;
                float gy = TerrainHeight(x, z);
                if (gy > 330) continue;   // 雪のある山頂には木がない
                if (HitBlock(x, z, gy + 1, 5f) != null) continue;
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
                // 高いビルには屋上の塔屋・ペントハウスを付けて立体感を出す
                if (b.H > 32 && d < 650)
                {
                    var roofCol = scene.FogC(Rgb(g - 20, g - 22, g - 25), d);
                    Raylib.DrawCube(R(b.X, b.H + 1.8f, b.Z), b.W * 0.45f, 3.6f, b.D * 0.45f, roofCol);
                }
            }
        }
    }
}
