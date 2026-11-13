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

/// <summary>市街地の建物の種類。</summary>
public enum BuildingKind : byte { House, ShopLow, Apartment, Commercial, Skyscraper, Warehouse }

/// <summary>町の建物（ぶつかる・壊せる）。</summary>
public sealed class Block
{
    public double X, Z;
    public float W, D, H, BaseY;
    public float TopY => BaseY + H;
    public int Tone, Seed;
    public BuildingKind Kind;
    public byte Style, FrontDir; // FrontDir: 0:+X, 1:-X, 2:+Z, 3:-Z
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
    public const double Lon0 = 110.0, Lat0 = 12.0, Res = 0.005;
    public const int W = 10200, H = 8800;
    private static readonly List<(string Name, int Rank, (double Lat, double Lon)[] LatLon)> SmoothRivers = new();
    private static readonly ulong[] Bits = Build();

    /// <summary>Catmull-Rom 補間で滑らかにし、上流→下流に揃えた日本の主要河川ライン一覧。</summary>
    public static IReadOnlyList<(string Name, int Rank, (double Lat, double Lon)[] LatLon)> Rivers => SmoothRivers;

    /// <summary>河川ランクと上流→下流の進行度 u∈[0..1] に応じた水面半幅（m）を返す。</summary>
    public static double RiverHalfWidth(int rank, double u)
    {
        double t = Math.Clamp(u, 0.0, 1.0);
        return rank <= 4 ? 34.0 + 20.0 * t : 24.0 + 14.0 * t;
    }

    public static bool Land(double lat, double lon)
    {
        int c = (int)((lon - Lon0) / Res), r = (int)((lat - Lat0) / Res);
        if ((uint)c >= W || (uint)r >= H) return false;
        long i = (long)r * W + c;
        return (Bits[i >> 6] & (1UL << (int)(i & 63))) != 0;
    }

    private static bool GetBit(ulong[] bits, double lat, double lon)
    {
        int c = (int)((lon - Lon0) / Res), r = (int)((lat - Lat0) / Res);
        if ((uint)c >= W || (uint)r >= H) return false;
        long i = (long)r * W + c;
        return (bits[i >> 6] & (1UL << (int)(i & 63))) != 0;
    }

    /// <summary>河口側が海・湖に接している向きを調べ、常に上流→下流順に並べ替える。</summary>
    private static (double Lat, double Lon)[] OrderRiverPoints((double Lat, double Lon)[] raw, Func<double, double, bool> landAt)
    {
        if (raw.Length < 2) return raw;
        int OceanRing(double lat, double lon)
        {
            int hits = 0;
            double[] radii = { 0.045, 0.095 };
            for (int ri = 0; ri < radii.Length; ri++)
            {
                double rLat = radii[ri], rLon = rLat * 1.24;
                for (int k = 0; k < 8; k++)
                {
                    double a = k * (Math.PI * 0.25);
                    if (!landAt(lat + Math.Sin(a) * rLat, lon + Math.Cos(a) * rLon)) hits++;
                }
            }
            return hits;
        }
        int oceanA = OceanRing(raw[0].Lat, raw[0].Lon);
        int oceanB = OceanRing(raw[^1].Lat, raw[^1].Lon);
        bool reverse = false;
        if (oceanA != oceanB)
        {
            reverse = oceanA > oceanB;
        }
        else
        {
            double PeakScore(double lat, double lon)
            {
                double s = 0;
                foreach (var (_, pLat, pLon, elev) in GeoFeatures.Peaks)
                {
                    double dLat = pLat - lat, dLon = (pLon - lon) * 0.81;
                    double d2 = dLat * dLat + dLon * dLon;
                    s += elev / (0.04 + d2);
                }
                return s;
            }
            if (PeakScore(raw[^1].Lat, raw[^1].Lon) > PeakScore(raw[0].Lat, raw[0].Lon))
                reverse = true;
        }
        if (!reverse) return raw;
        var flipped = new (double Lat, double Lon)[raw.Length];
        for (int i = 0; i < raw.Length; i++) flipped[i] = raw[raw.Length - 1 - i];
        return flipped;
    }

    /// <summary>求心性 Catmull-Rom スプラインにより河川折れ線をループ・オーバーシュート無しで滑らかに細分化する。</summary>
    private static (double Lat, double Lon)[] SmoothRiverLatLon((double Lat, double Lon)[] raw, double targetStepDeg = 0.0042)
    {
        if (raw.Length < 2) return raw;
        var clean = new List<(double Lat, double Lon)>(raw.Length);
        foreach (var pt in raw)
        {
            if (clean.Count == 0
                || Math.Abs(pt.Lat - clean[^1].Lat) > 1e-5
                || Math.Abs(pt.Lon - clean[^1].Lon) > 1e-5)
                clean.Add(pt);
        }
        if (clean.Count < 2) return clean.ToArray();

        static (double Lat, double Lon) EvalCatmull(
            (double Lat, double Lon) p0, (double Lat, double Lon) p1,
            (double Lat, double Lon) p2, (double Lat, double Lon) p3, double u)
        {
            static double Knot(double t, (double Lat, double Lon) a, (double Lat, double Lon) b)
            {
                double dLat = b.Lat - a.Lat, dLon = (b.Lon - a.Lon) * 0.81;
                double d = Math.Sqrt(dLat * dLat + dLon * dLon);
                return t + Math.Max(1e-4, Math.Sqrt(d));
            }
            static (double Lat, double Lon) LerpPt((double Lat, double Lon) a, (double Lat, double Lon) b, double ta, double tb, double tt)
            {
                double w = (tt - ta) / Math.Max(1e-9, tb - ta);
                return (a.Lat + (b.Lat - a.Lat) * w, a.Lon + (b.Lon - a.Lon) * w);
            }
            double t0 = 0, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);
            double t = t1 + (t2 - t1) * u;
            var a1 = LerpPt(p0, p1, t0, t1, t);
            var a2 = LerpPt(p1, p2, t1, t2, t);
            var a3 = LerpPt(p2, p3, t2, t3, t);
            var b1 = LerpPt(a1, a2, t0, t2, t);
            var b2 = LerpPt(a2, a3, t1, t3, t);
            return LerpPt(b1, b2, t1, t2, t);
        }

        int n = clean.Count;
        var outPts = new List<(double Lat, double Lon)>(n * 8);
        for (int i = 0; i < n - 1; i++)
        {
            var p1 = clean[i];
            var p2 = clean[i + 1];
            var p0 = i > 0 ? clean[i - 1] : (2 * p1.Lat - p2.Lat, 2 * p1.Lon - p2.Lon);
            var p3 = i + 2 < n ? clean[i + 2] : (2 * p2.Lat - p1.Lat, 2 * p2.Lon - p1.Lon);
            double dLat = p2.Lat - p1.Lat, dLon = (p2.Lon - p1.Lon) * 0.81;
            double segDeg = Math.Sqrt(dLat * dLat + dLon * dLon);
            int steps = Math.Clamp((int)Math.Ceiling(segDeg / targetStepDeg), 2, 96);
            for (int k = 0; k < steps; k++)
                outPts.Add(k == 0 ? p1 : EvalCatmull(p0, p1, p2, p3, (double)k / steps));
        }
        outPts.Add(clean[^1]);
        return outPts.ToArray();
    }

    private static void Set(ulong[] bits, int r, int c, bool v)
    {
        if ((uint)c >= W || (uint)r >= H) return;
        long i = (long)r * W + c;
        if (v) bits[i >> 6] |= 1UL << (int)(i & 63);
        else bits[i >> 6] &= ~(1UL << (int)(i & 63));
    }

    /// <summary>多角形の内側を塗る（ポリゴン同士の重なりで穴が空かないようリングごとに塗る）。</summary>
    private static void Fill(ulong[] bits, IEnumerable<(double Lat, double Lon)[]> rings, bool value)
    {
        var xs = new List<double>(16);
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            double minLat = double.MaxValue, maxLat = double.MinValue;
            for (int i = 0; i < ring.Length; i++)
            {
                if (ring[i].Lat < minLat) minLat = ring[i].Lat;
                if (ring[i].Lat > maxLat) maxLat = ring[i].Lat;
            }
            int rStart = Math.Max(0, (int)Math.Ceiling((minLat - Lat0) / Res - 0.5));
            int rEnd = Math.Min(H - 1, (int)Math.Ceiling((maxLat - Lat0) / Res - 0.5) - 1);
            for (int r = rStart; r <= rEnd; r++)
            {
                xs.Clear();
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Length];
                    double ya = (a.Lat - Lat0) / Res - 0.5, yb = (b.Lat - Lat0) / Res - 0.5;
                    if ((ya <= r && yb > r) || (yb <= r && ya > r))
                    {
                        double xa = (a.Lon - Lon0) / Res, xb = (b.Lon - Lon0) / Res;
                        xs.Add(xa + (r - ya) * (xb - xa) / (yb - ya));
                    }
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                    for (int c = (int)Math.Ceiling(xs[k] - 0.5); c <= (int)Math.Floor(xs[k + 1] - 0.5); c++)
                        Set(bits, r, c, value);
            }
        }
    }

    private static void StampLine(ulong[] bits, double latA, double lonA, double latB, double lonB, double radCells, bool value)
    {
        double ya = (latA - Lat0) / Res, xa = (lonA - Lon0) / Res;
        double yb = (latB - Lat0) / Res, xb = (lonB - Lon0) / Res;
        int steps = (int)(Math.Max(Math.Abs(yb - ya), Math.Abs(xb - xa)) * 2.5) + 1;
        int ir = (int)Math.Ceiling(radCells);
        double r2 = radCells * radCells;
        for (int s = 0; s <= steps; s++)
        {
            double y = ya + (yb - ya) * s / steps, x = xa + (xb - xa) * s / steps;
            int cy = (int)y, cx = (int)x;
            for (int dr = -ir; dr <= ir; dr++)
                for (int dc = -ir; dc <= ir; dc++)
                    if (dr * dr + dc * dc <= r2) Set(bits, cy + dr, cx + dc, value);
        }
    }

    private static ulong[] Build()
    {
        var bits = new ulong[((long)W * H + 63) / 64];
        Fill(bits, JapanOutline.Detailed, true);
        // 先に都市・空港・陸上高速道路の沿岸誤差を陸地補強してから湖・川を彫ることで、海岸落ちを防ぎつつ川の連続性を保つ
        foreach (var c in MapData.Cities)
            StampLine(bits, c.Lat, c.Lon, c.Lat, c.Lon, 4.2, true);
        foreach (var (_, lat, lon, _) in GeoFeatures.Airports)
            StampLine(bits, lat, lon, lat, lon, 7.5, true);
        foreach (var r in MapData.Roads)
        {
            if (r.Type != RoadType.Expressway) continue;
            var pts = r.Points;
            for (int i = 0; i + 1 < pts.Length; i++)
                StampLine(bits, pts[i].Lat, pts[i].Lon, pts[i + 1].Lat, pts[i + 1].Lon, 2.4, true);
        }
        Fill(bits, JapanOutline.AllLakes.Select(l => l.Points), false);
        // 川は曲線スプラインで滑らかにし、上流から下流へ向かって自然に川幅を広げて彫る
        SmoothRivers.Clear();
        foreach (var (name, rank, rawLine) in JapanOutline.AllRivers)
        {
            var ordered = OrderRiverPoints(rawLine, (la, lo) => GetBit(bits, la, lo));
            var smooth = SmoothRiverLatLon(ordered, 0.0042);
            SmoothRivers.Add((name, rank, smooth));
            int segCount = Math.Max(1, smooth.Length - 1);
            for (int i = 0; i < segCount; i++)
            {
                double u = (i + 0.5) / segCount;
                double halfW = RiverHalfWidth(rank, u);
                double rad = halfW / 32.0 + 0.35;
                StampLine(bits, smooth[i].Lat, smooth[i].Lon, smooth[i + 1].Lat, smooth[i + 1].Lon, rad, false);
            }
        }
        // 交差点広場（最小半径）と空港滑走路だけは川で水没しないよう陸に戻す
        foreach (var c in MapData.Cities)
            StampLine(bits, c.Lat, c.Lon, c.Lat, c.Lon, 0.95, true);
        foreach (var (_, lat, lon, _) in GeoFeatures.Airports)
            StampLine(bits, lat, lon, lat, lon, 4.2, true);
        return bits;
    }

    private static readonly (double DistDeg, float Factor)[] ShoreRings =
    {
        (0.006, 0.0f),
        (0.012, 0.22f),
        (0.020, 0.52f),
        (0.030, 0.82f),
    };

    /// <summary>水辺（海・川・湖）の近くで山が急に切り立たないよう、水際距離に応じた山の抑え係数 [0..1] を返す。</summary>
    public static float ShoreFactor(double lat, double lon)
    {
        if (!Land(lat, lon)) return 0f;
        foreach (var (d, factor) in ShoreRings)
        {
            double dLon = d * 1.2;
            if (!Land(lat + d, lon) || !Land(lat - d, lon) || !Land(lat, lon + dLon) || !Land(lat, lon - dLon)
                || !Land(lat + d * 0.7, lon + dLon * 0.7) || !Land(lat + d * 0.7, lon - dLon * 0.7)
                || !Land(lat - d * 0.7, lon + dLon * 0.7) || !Land(lat - d * 0.7, lon - dLon * 0.7))
                return factor;
        }
        return 1f;
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
    public const float SeaY = -1.2f;           // 海底・川底の高さ
    public const float WaterY = -0.45f;        // 水面（海・川・湖）の高さ
    public const float RoadY = 0.18f;          // 道路面の高さ（地面とのZファイティング・草のめり込みを防ぐ）
    private const double G = 25;               // 地面のマス目
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
        public double TrimA = 0, TrimB = 0;
        public int JctA = -1, JctB = -1;
        public int NextSegA = -1, NextSegB = -1;
    }

    private sealed class CurveJoint
    {
        public required double X, Z;
        public required int SegA, SegB;
        public required Road Road;
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
    private readonly record struct RoofTri(Vector3 P0, Vector3 P1, Vector3 P2, Color C0, Color C1, Color C2);
    private readonly record struct RoofBeacon(double X, float Y, double Z, float Dist, bool Phase);
    private readonly record struct RoofSpire(double X, float Y0, float Y1, double Z, float Dist);

    private readonly struct RiverSample
    {
        public readonly double X, Z;
        public readonly double Tx, Tz;
        public readonly double Nx, Nz;
        public readonly float S;
        public readonly float HalfW;
        public readonly float LeftBank;
        public readonly float RightBank;
        public readonly float FlowFade;
        public readonly float Urban;
        public readonly float LeftRoad;
        public readonly float RightRoad;
        public readonly float LeftGround;
        public readonly float RightGround;
        public readonly bool LeftCityGrid;
        public readonly bool RightCityGrid;

        public RiverSample(
            double x, double z, double tx, double tz, double nx, double nz, float s, float halfW,
            float leftBank, float rightBank, float flowFade, float urban,
            float leftRoad, float rightRoad, float leftGround, float rightGround,
            bool leftCityGrid, bool rightCityGrid)
        {
            X = x; Z = z; Tx = tx; Tz = tz; Nx = nx; Nz = nz; S = s; HalfW = halfW;
            LeftBank = leftBank; RightBank = rightBank; FlowFade = flowFade; Urban = urban;
            LeftRoad = leftRoad; RightRoad = rightRoad; LeftGround = leftGround; RightGround = rightGround;
            LeftCityGrid = leftCityGrid; RightCityGrid = rightCityGrid;
        }
    }

    private sealed class RiverRibbon
    {
        public required string Name;
        public required int Rank;
        public required uint Seed;
        public required RiverSample[] Samples;
        public required double MinX, MaxX, MinZ, MaxZ;
        public required float TotalLength;
        public required float MaxHalfW;
    }

    private readonly List<Seg> segs = new();
    private readonly List<CurveJoint> curves = new();
    private readonly List<Junction> junctions = new();
    private readonly List<Peak> peaks = new();
    private readonly List<AirportInfo> airports = new();
    private readonly List<RoofTri> roofTris = new(512);
    private readonly List<RoofBeacon> roofBeacons = new(128);
    private readonly List<RoofSpire> roofSpires = new(128);
    private readonly List<RiverRibbon> rivers = new();
    private List<int> nearSegs = new();
    private readonly Dictionary<(long, long, int), float> heightCache = new();
    private float drawTime;

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
        var citySegArms = MapData.Cities.ToDictionary(c => c, _ => new List<(int SegIdx, bool IsA)>());
        foreach (var r in MapData.Roads)
        {
            if (r.Type == RoadType.Ferry) continue;
            var pts = r.Points;
            int firstSeg = segs.Count;
            int count = pts.Length - 1;
            for (int k = 0; k < count; k++)
            {
                var (ax, az) = FromLatLon(pts[k].Lat, pts[k].Lon);
                var (bx, bz) = FromLatLon(pts[k + 1].Lat, pts[k + 1].Lon);
                double len = Math.Max(1, Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)));
                int si = segs.Count;
                segs.Add(new Seg
                {
                    Ax = ax, Az = az, Bx = bx, Bz = bz, Road = r,
                    Len = len, Dx = (bx - ax) / len, Dz = (bz - az) / len,
                    NextSegA = k > 0 ? si - 1 : -1,
                    NextSegB = k + 1 < count ? si + 1 : -1,
                });
                if (k == 0) citySegArms[r.A].Add((si, true));
                if (k == count - 1) citySegArms[r.B].Add((si, false));
                if (k > 0)
                    curves.Add(new CurveJoint { X = ax, Z = az, SegA = si - 1, SegB = si, Road = r });
            }
        }
        BuildJunctions(citySegArms);

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
            int tier = c.Name is "東京" or "大阪" or "横浜" or "名古屋" or "ソウル" or "台北" or "北京" or "上海" or "香港" or "マニラ" ? 0
                : c.IsGoal || c.Name is "釜山" or "仁川" or "大邱" or "平壌" or "瀋陽" or "大連" or "青島" or "福州" or "アモイ" or "ウラジオストク" or "ハバロフスク" or "台中" or "高雄" or "台南" or "豊原" ? 1 : 2;
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
        BuildRivers();
    }

    private void BuildJunctions(Dictionary<City, List<(int SegIdx, bool IsA)>> citySegArms)
    {
        foreach (var c in MapData.Cities)
        {
            var rawArms = citySegArms[c];
            if (rawArms.Count == 0) continue;
            var arms = new List<JunctionArm>(rawArms.Count);
            foreach (var (si, isA) in rawArms)
            {
                var s = segs[si];
                double odx = isA ? s.Dx : -s.Dx;
                double odz = isA ? s.Dz : -s.Dz;
                arms.Add(new JunctionArm { SegIndex = si, IsA = isA, OutDx = odx, OutDz = odz, Angle = Math.Atan2(odx, odz) });
            }
            arms.Sort((a, b) => a.Angle.CompareTo(b.Angle));

            float radius = arms.Count switch { 1 => 15f, 2 => 18f, 3 => 21f, 4 => 23f, _ => 26f };
            var (cx, cz) = CityPos(c);
            int ji = junctions.Count;

            for (int k = 0; k < arms.Count; k++)
            {
                var arm = arms[k];
                double trim = radius + 1.5;
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
                        double overlap = 10.4 / Math.Tan(Math.Max(0.22, minAng * 0.5));
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
            if (Math.Abs(rx) > 1800 || Math.Abs(rz) > 1800) continue;
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

    private double DistanceToRoadSet(double x, double z, List<int> segIndices)
    {
        double best = AirportDistance(x, z);
        foreach (int i in segIndices)
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

    private double NearRoadDistance(double x, double z) => DistanceToRoadSet(x, z, nearSegs);

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

        // 地面メッシュ（25m格子）のポリゴンが道路・水際をまたいでも急崖にならないよう滑らかに平坦化する
        float flat = Smooth(90, 380, RoadDistance(x, z));
        var (lat, lon) = ToLatLon(x, z);
        flat = Math.Min(flat, LandRaster.ShoreFactor(lat, lon));
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
            // 上空からの粗いメッシュ（lvl > 0）では、道路・カーブ・街路をまたぐマスの4隅すべてが0m以下になるよう対角長（√2・step）ぶんまで平坦化する
            if (lvl > 0 && v > 0 && IsVertexNearRoadOrCity(x, z, step)) v = 0;
        }
        heightCache[key] = v;
        return v;
    }

    private bool IsVertexNearRoadOrCity(double x, double z, double step)
    {
        double pad = step * 1.45;
        if (RoadDistance(x, z) < pad + 34) return true;
        double streetPad = pad + 14;
        foreach (var pl in Places)
        {
            double lim = pl.Radius * 0.95 + streetPad;
            double dx = x - pl.X, dz = z - pl.Z;
            if (Math.Abs(dx) < lim && Math.Abs(dz) < lim && dx * dx + dz * dz < lim * lim)
                return true;
        }
        return false;
    }

    /// <summary>描画メッシュ（25m格子）と完全に一致する連続な地形高さ（m）。水陸の境界に段差を作らない。</summary>
    public float BaseGroundY(double x, double z)
    {
        double gx = x / G, gz = z / G;
        long ix = (long)Math.Floor(gx), iz = (long)Math.Floor(gz);
        float fx = (float)(gx - ix), fz = (float)(gz - iz);
        float h00 = HeightAt(ix, iz, 0, G), h10 = HeightAt(ix + 1, iz, 0, G);
        float h01 = HeightAt(ix, iz + 1, 0, G), h11 = HeightAt(ix + 1, iz + 1, 0, G);
        float top = h00 + (h10 - h00) * fx;
        float bot = h01 + (h11 - h01) * fx;
        return top + (bot - top) * fz;
    }

    public bool IsInWater(double x, double z)
        => TryGetRiverAt(x, z, out _, out bool inRiverWater) ? inRiverWater : BaseGroundY(x, z) < WaterY;

    /// <summary>その場所の地面の高さ（道路・路肩スロープ・河川堤防・陸・水底）。</summary>
    public float SurfaceY(double x, double z)
    {
        float baseY = BaseGroundY(x, z);
        if (TryGetRiverAt(x, z, out float riverY, out bool inRiverWater))
            baseY = inRiverWater ? riverY : Math.Max(baseY, riverY);
        double rd = NearRoadDistance(x, z);
        if (rd <= 8.2) return RoadY;
        if (rd < 13.5)
        {
            float t = Smooth(8.2, 13.5, rd);
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

    private static uint StableHash(string text)
    {
        uint h = 2166136261u;
        foreach (char c in text)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }

    /// <summary>名所モデル本体・背景の山コーンを含む占有円（中心からの相対X/Z、半径 m）を返す。</summary>
    private static (double Ox, double Oz, double Radius)[] SpotFootprints(Spot sp)
    {
        return sp.Type switch
        {
            LandmarkType.Mountain when sp.Variant == 1 => [(0, 0, 440)],
            LandmarkType.Mountain => [(0, 0, 270), (-190, 60, 200), (210, 90, 180)],
            LandmarkType.Volcano when sp.Variant == 1 => [(0, 0, 60), (0, 140, 160)],
            LandmarkType.Volcano => [(0, 0, 420)],
            LandmarkType.Torii when sp.Variant == 2 => [(0, 35, 85), (0, 160, 130)],
            LandmarkType.Torii => [(0, 0, 40)],
            LandmarkType.Bridge when sp.Variant == 0 => [(0, 0, 110)],
            LandmarkType.Bridge when sp.Variant == 1 => [(0, 0, 90), (0, 220, 160)],
            LandmarkType.Bridge => [(0, 150, 310), (-170, 180, 60), (180, 210, 80)],
            LandmarkType.Lake when sp.Variant == 1 => [(0, 0, 175), (-200, 250, 160)],
            LandmarkType.Lake when sp.Variant == 2 => [(0, 60, 210)],
            LandmarkType.Lake => [(0, 0, 145), (-160, 200, 170), (40, 260, 190), (220, 170, 150)],
            LandmarkType.Castle or LandmarkType.Temple or LandmarkType.Garden or LandmarkType.Monument => [(0, 0, 50)],
            _ => [(0, 0, 35)],
        };
    }

    private double SpotClearanceMargin(Spot sp, double x, double z)
    {
        double minMargin = double.MaxValue;
        foreach (var (ox, oz, r) in SpotFootprints(sp))
        {
            double fx = x + ox, fz = z + oz;
            double roadMargin = RoadDistance(fx, fz) - (r + 34);
            if (roadMargin < minMargin) minMargin = roadMargin;

            foreach (var pl in Places)
            {
                double minDist = pl.Radius + r + 20;
                double dx = fx - pl.X, dz = fz - pl.Z;
                if (Math.Abs(dx) >= minDist || Math.Abs(dz) >= minDist) continue;
                double cityMargin = Math.Sqrt(dx * dx + dz * dz) - minDist;
                if (cityMargin < minMargin) minMargin = cityMargin;
            }
        }
        return minMargin;
    }

    private void PlaceSpots()
    {
        var cityRadiusByName = Places.ToDictionary(p => p.Name, p => p.Radius);
        foreach (var city in MapData.Cities)
        {
            var spots = Spots.At(city).ToList();
            var (cx, cz) = CityPos(city);
            double cityR = cityRadiusByName.GetValueOrDefault(city.Name, TierRadius(city.IsGoal ? 1 : 2));
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
                double maxExtent = 0;
                foreach (var (ox, oz, r) in SpotFootprints(sp))
                    maxExtent = Math.Max(maxExtent, Math.Sqrt(ox * ox + oz * oz) + r);

                double baseD = sp.Type switch
                {
                    LandmarkType.Mountain or LandmarkType.Volcano => Math.Max(1900, cityR + maxExtent + 60),
                    LandmarkType.Lake => Math.Max(1100, cityR + maxExtent + 60),
                    _ => Math.Max(TierRadius(city.IsGoal ? 1 : 2) + 150, cityR + maxExtent + 40) + i * 140,
                };
                double baseAng = Scene.Hash(StableHash(sp.Key)) * Math.PI * 2 + i * Math.PI;
                (double X, double Z) bestPos = (cx + Math.Sin(baseAng) * baseD, cz + Math.Cos(baseAng) * baseD);
                double bestScore = double.NegativeInfinity;
                bool found = false;

                for (int ring = 0; ring < 36 && !found; ring++)
                {
                    double d = baseD + ring * 130;
                    for (int k = 0; k < 24; k++)
                    {
                        double a = baseAng + k * Math.PI / 12;
                        double x = cx + Math.Sin(a) * d, z = cz + Math.Cos(a) * d;
                        if (!IsLand(x, z)) continue;
                        float th = TerrainHeight(x, z);
                        double margin = SpotClearanceMargin(sp, x, z);
                        if (th < 15 && margin >= 0)
                        {
                            bestPos = (x, z);
                            found = true;
                            break;
                        }
                        double score = margin - Math.Max(0f, th - 15f) * 2.0 - ring * 0.5;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPos = (x, z);
                        }
                    }
                }
                SpotPos[sp] = bestPos;
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

                // 四隅と中心とも陸地・平坦地（水際の斜面には建てない）であること
                if (!IsLand(x, z) || !IsLand(x - w * 0.5, z - dep * 0.5) || !IsLand(x + w * 0.5, z + dep * 0.5)
                    || !IsLand(x - w * 0.5, z + dep * 0.5) || !IsLand(x + w * 0.5, z - dep * 0.5)) continue;
                if (BaseGroundY(x, z) < -0.02f || TerrainHeight(x, z) > 3.5f) continue;
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
                float hRand = Scene.Hash(id + 3);
                float h = 8 + near * near * maxH * hRand + Scene.Hash(id + 4) * 10;
                // 郊外や中小都市の周囲では住宅街らしい低層（6.5m〜12m）の比率を上げる
                bool outerZone = place.Tier >= 2 ? near < 0.52f : near < 0.28f;
                if (outerZone && Scene.Hash(id + 8) < 0.68f)
                    h = 6.5f + Scene.Hash(id + 4) * 5.2f;

                float styleRoll = Scene.Hash(id + 9);
                BuildingKind kind =
                    h >= 54f ? BuildingKind.Skyscraper
                    : h >= 22f ? (styleRoll < (0.35f + near * 0.45f) ? BuildingKind.Commercial : BuildingKind.Apartment)
                    : h >= 12.5f ? (styleRoll < 0.56f ? BuildingKind.Apartment : BuildingKind.Commercial)
                    : (outerZone && w > 18f && dep > 18f && styleRoll < 0.28f) ? BuildingKind.Warehouse
                    : (near > 0.44f || styleRoll < 0.24f) ? BuildingKind.ShopLow
                    : BuildingKind.House;

                if (kind == BuildingKind.House)
                {
                    // 一軒家は区画より一回りコンパクトにして住宅らしい縦横比にする
                    w = Math.Min(w, 13.8f);
                    dep = Math.Min(dep, 13.8f);
                }
                else if (kind == BuildingKind.ShopLow)
                {
                    h = Math.Clamp(h, 6.0f, 10.8f);
                    w = Math.Min(w, 22.0f);
                    dep = Math.Min(dep, 22.0f);
                }
                else if (kind == BuildingKind.Warehouse)
                {
                    h = Math.Clamp(h, 7.2f, 12.2f);
                }

                // 四隅・中心の地盤高から建物の基準高さを決め、斜め地でも埋まり・浮きを防ぐ
                float y00 = BaseGroundY(x - w * 0.45, z - dep * 0.45);
                float y10 = BaseGroundY(x + w * 0.45, z - dep * 0.45);
                float y01 = BaseGroundY(x - w * 0.45, z + dep * 0.45);
                float y11 = BaseGroundY(x + w * 0.45, z + dep * 0.45);
                float baseY = Math.Max(0f, Math.Max(BaseGroundY(x, z), Math.Min(Math.Min(y00, y10), Math.Min(y01, y11)) + 0.15f));

                // 最寄り街路（88m格子）へ向く正面方向（0:+X, 1:-X, 2:+Z, 3:-Z）
                byte xFace = (i & 1) == 0 ? (byte)1 : (byte)0;
                byte zFace = (j & 1) == 0 ? (byte)3 : (byte)2;
                byte frontDir = Scene.Hash(id + 10) < 0.5f ? xFace : zFace;

                list.Add(new Block
                {
                    X = x, Z = z, H = h, BaseY = baseY,
                    W = w, D = dep,
                    Tone = (int)(Scene.Hash(id + 7) * 80),
                    Seed = (int)(id & 0x7FFFFFFF),
                    Kind = kind,
                    Style = (byte)(Scene.Hash(id + 11) * 12),
                    FrontDir = frontDir,
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
                if (b.Destroyed || y > b.TopY) continue;
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
                if (!b.Destroyed && Math.Abs(x - b.X) < b.W / 2 && Math.Abs(z - b.Z) < b.D / 2) return b.TopY;
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
        if (c.T > 1 && cur.NextSegB >= 0)
        {
            c.Seg = cur.NextSegB;
            c.Dir = 1;
            c.T = 0.005;
            return true;
        }
        if (c.T < 0 && cur.NextSegA >= 0)
        {
            c.Seg = cur.NextSegA;
            c.Dir = -1;
            c.T = 0.995;
            return true;
        }
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
        drawTime = time;
        scene.SetCamera(cameraPos);
        DrawGround();
        DrawRivers(time);
        DrawCityStreets(time);
        DrawAirports(time);
        DrawRoads();
        DrawRoadDetails(time);
        DrawJunctions(time);
        DrawTrees();
        DrawCities(time);
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

    private (int lvl, double step, long cx, long cz, int n) GroundGrid()
    {
        int lvl = 0;
        while (ViewRadius / (G * (1 << lvl)) > 48) lvl++;
        double step = G * (1 << lvl);
        return (lvl, step, (long)Math.Floor(PX / step), (long)Math.Floor(PZ / step), (int)(ViewRadius / step));
    }

    private void DrawGround()
    {
        var (lvl, step, cx, cz, n) = GroundGrid();
        var deepFloor = Rgb(22, 72, 128);
        var sand = Rgb(212, 200, 150);
        var wetSand = Rgb(164, 152, 112);
        var wetConcrete = Rgb(116, 120, 126);
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
            float ub = h < 15 ? UrbanBlend(wx, wz) : 0;
            Color wetCol = ub > 0.25f ? wetConcrete : wetSand;
            Color shoreCol = ub > 0.25f ? Rgb(152, 154, 158) : sand;
            Color c;
            if (h <= SeaY + 0.05f)
            {
                bool nearShore = HeightAt(ix + 1, iz, lvl, step) > SeaY + 0.05f || HeightAt(ix - 1, iz, lvl, step) > SeaY + 0.05f
                    || HeightAt(ix, iz + 1, lvl, step) > SeaY + 0.05f || HeightAt(ix, iz - 1, lvl, step) > SeaY + 0.05f;
                c = nearShore ? wetCol : deepFloor;
            }
            else if (h < WaterY + 0.1f)
            {
                float t = Math.Clamp((h - SeaY) / (WaterY - SeaY), 0, 1);
                c = Lerp(wetCol, shoreCol, t);
            }
            else
            {
                float hx = HeightAt(ix + 1, iz, lvl, step), hz = HeightAt(ix, iz + 1, lvl, step);
                bool nearWater = hx <= SeaY + 0.05f || hz <= SeaY + 0.05f
                    || HeightAt(ix - 1, iz, lvl, step) <= SeaY + 0.05f || HeightAt(ix, iz - 1, lvl, step) <= SeaY + 0.05f;
                if (nearWater)
                    c = shoreCol;
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

    /// <summary>海・川・湖の水平な半透明水面を描画し、水際の境界を鮮明に表現する。</summary>
    public void DrawWater()
    {
        var (lvl, step, cx, cz, n) = GroundGrid();
        var deepWater = Rgb(24, 96, 166);
        var shallowWater = Rgb(42, 148, 204);

        Rlgl.DrawRenderBatchActive();
        Raylib.BeginBlendMode(BlendMode.Alpha);
        Rlgl.DisableDepthMask();
        Rlgl.Begin(DrawMode.Quads);

        void WVert(long ix, long iz, float h)
        {
            double wx = ix * step, wz = iz * step;
            var p = R(wx, WaterY, wz);
            float dist = Vector3.Distance(p, cam);
            float depth = Math.Clamp((WaterY - h) / (WaterY - SeaY), 0f, 1f);
            var baseCol = Lerp(shallowWater, deepWater, depth);
            var fogged = scene.FogC(baseCol, dist);
            float baseA = h >= WaterY ? 125f : 165f + depth * 55f;
            float fogMix = Math.Clamp(dist / (float)Math.Max(400, ViewRadius * 0.92), 0f, 1f);
            byte a = (byte)Math.Clamp(baseA + (255f - baseA) * fogMix, 0, 255);
            Rlgl.Color4ub(fogged.R, fogged.G, fogged.B, a);
            Rlgl.Vertex3f(p.X, p.Y, p.Z);
        }

        for (int i = -n; i < n; i++)
        {
            for (int j = -n; j < n; j++)
            {
                if (i * i + j * j > n * n) continue;
                long ix = cx + i, iz = cz + j;
                float h00 = HeightAt(ix, iz, lvl, step);
                float h10 = HeightAt(ix + 1, iz, lvl, step);
                float h11 = HeightAt(ix + 1, iz + 1, lvl, step);
                float h01 = HeightAt(ix, iz + 1, lvl, step);
                if (Math.Min(Math.Min(h00, h10), Math.Min(h11, h01)) >= WaterY + 0.05f) continue;

                WVert(ix, iz, h00);
                WVert(ix + 1, iz, h10);
                WVert(ix + 1, iz + 1, h11);
                WVert(ix, iz + 1, h01);
            }
        }
        DrawRiverWater(drawTime);
        Rlgl.End();
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthMask();
        Raylib.EndBlendMode();
    }

    // ---------------- 河川（滑らかな堤防・護岸・アニメーション水流） ----------------

    private readonly struct BankProfile
    {
        public readonly float D0, D1, D2, D3, D4, D5, D6;
        public readonly float Y0, Y1, Y2, Y3, Y4, Y5, Y6;
        public readonly float IsUrban, RoadClear, PlainAllowed, Noise, BankWeight;

        public BankProfile(
            float d0, float d1, float d2, float d3, float d4, float d5, float d6,
            float y0, float y1, float y2, float y3, float y4, float y5, float y6,
            float isUrban, float roadClear, float plainAllowed, float noise, float bankWeight)
        {
            D0 = d0; D1 = d1; D2 = d2; D3 = d3; D4 = d4; D5 = d5; D6 = d6;
            Y0 = y0; Y1 = y1; Y2 = y2; Y3 = y3; Y4 = y4; Y5 = y5; Y6 = y6;
            IsUrban = isUrban; RoadClear = roadClear; PlainAllowed = plainAllowed;
            Noise = noise; BankWeight = bankWeight;
        }

        public float EvalY(double dist)
        {
            float d = (float)dist;
            if (d <= D1) return LerpF(Y0, Y1, (d - D0) / Math.Max(0.1f, D1 - D0));
            if (d <= D2) return LerpF(Y1, Y2, (d - D1) / Math.Max(0.1f, D2 - D1));
            if (d <= D3) return LerpF(Y2, Y3, (d - D2) / Math.Max(0.1f, D3 - D2));
            if (d <= D4) return LerpF(Y3, Y4, (d - D3) / Math.Max(0.1f, D4 - D3));
            if (d <= D5) return LerpF(Y4, Y5, (d - D4) / Math.Max(0.1f, D5 - D4));
            if (d <= D6) return LerpF(Y5, Y6, (d - D5) / Math.Max(0.1f, D6 - D5));
            return Y6;
        }
    }

    private static float LerpF(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private bool IsInsideCityGrid(double x, double z)
    {
        foreach (var pl in Places)
        {
            double r = pl.Radius * 0.96;
            double dx = x - pl.X, dz = z - pl.Z;
            if (Math.Abs(dx) < r && Math.Abs(dz) < r && dx * dx + dz * dz < r * r)
                return true;
        }
        return false;
    }

    /// <summary>全28河川について、橋梁・都市街区・海岸河口を整合させた3D河川メッシュ用サンプルを構築する。</summary>
    private void BuildRivers()
    {
        rivers.Clear();
        var worldPts = new List<(double X, double Z)>(512);
        var localSegs = new List<int>(64);

        foreach (var (name, rank, latLonPts) in LandRaster.Rivers)
        {
            worldPts.Clear();
            foreach (var (lat, lon) in latLonPts)
            {
                var (wx, wz) = FromLatLon(lat, lon);
                if (worldPts.Count == 0
                    || (wx - worldPts[^1].X) * (wx - worldPts[^1].X) + (wz - worldPts[^1].Z) * (wz - worldPts[^1].Z) >= 100.0)
                    worldPts.Add((wx, wz));
            }
            if (latLonPts.Length >= 2)
            {
                var last = FromLatLon(latLonPts[^1].Lat, latLonPts[^1].Lon);
                if (worldPts.Count == 0) worldPts.Add(last);
                else worldPts[^1] = last;
            }
            int n = worldPts.Count;
            if (n < 2) continue;

            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var (wx, wz) in worldPts)
            {
                if (wx < minX) minX = wx;
                if (wx > maxX) maxX = wx;
                if (wz < minZ) minZ = wz;
                if (wz > maxZ) maxZ = wz;
            }
            const double pad = 120.0;
            localSegs.Clear();
            for (int si = 0; si < segs.Count; si++)
            {
                var s = segs[si];
                double sMinX = Math.Min(s.Ax, s.Bx), sMaxX = Math.Max(s.Ax, s.Bx);
                double sMinZ = Math.Min(s.Az, s.Bz), sMaxZ = Math.Max(s.Az, s.Bz);
                if (sMaxX >= minX - pad && sMinX <= maxX + pad && sMaxZ >= minZ - pad && sMinZ <= maxZ + pad)
                    localSegs.Add(si);
            }

            // 接線・平滑法線・累積距離を算出する
            var rawTx = new double[n];
            var rawTz = new double[n];
            var cumS = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    double dx = worldPts[i].X - worldPts[i - 1].X, dz = worldPts[i].Z - worldPts[i - 1].Z;
                    cumS[i] = cumS[i - 1] + (float)Math.Sqrt(dx * dx + dz * dz);
                }
                int ia = Math.Max(0, i - 1), ib = Math.Min(n - 1, i + 1);
                double tx = worldPts[ib].X - worldPts[ia].X, tz = worldPts[ib].Z - worldPts[ia].Z;
                double tl = Math.Max(1e-6, Math.Sqrt(tx * tx + tz * tz));
                rawTx[i] = tx / tl;
                rawTz[i] = tz / tl;
            }
            float totalLen = Math.Max(1f, cumS[^1]);

            var rawLeftB = new float[n];
            var rawRightB = new float[n];
            var rawFlow = new float[n];
            var halfWidths = new float[n];
            var nxs = new double[n];
            var nzs = new double[n];
            var txs = new double[n];
            var tzs = new double[n];
            var leftRoad = new float[n];
            var rightRoad = new float[n];
            var leftGround = new float[n];
            var rightGround = new float[n];
            var urbanArr = new float[n];
            var leftCity = new bool[n];
            var rightCity = new bool[n];

            for (int i = 0; i < n; i++)
            {
                double sx = 0, sz = 0;
                for (int k = -1; k <= 1; k++)
                {
                    int j = Math.Clamp(i + k, 0, n - 1);
                    sx += rawTx[j];
                    sz += rawTz[j];
                }
                double sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz));
                double tx = sx / sl, tz = sz / sl;
                double nx = -tz, nz = tx;
                txs[i] = tx; tzs[i] = tz;
                nxs[i] = nx; nzs[i] = nz;

                double u = cumS[i] / totalLen;
                float hw = (float)LandRaster.RiverHalfWidth(rank, u);
                halfWidths[i] = hw;

                double cx = worldPts[i].X, cz = worldPts[i].Z;
                bool lNear = IsLand(cx + nx * (hw + 30), cz + nz * (hw + 30));
                bool lMid = IsLand(cx + nx * (hw + 85), cz + nz * (hw + 85));
                bool lFar = IsLand(cx + nx * (hw + 145), cz + nz * (hw + 145));
                bool rNear = IsLand(cx - nx * (hw + 30), cz - nz * (hw + 30));
                bool rMid = IsLand(cx - nx * (hw + 85), cz - nz * (hw + 85));
                bool rFar = IsLand(cx - nx * (hw + 145), cz - nz * (hw + 145));

                rawLeftB[i] = (lNear ? 0.55f : 0f) + (lMid ? 0.45f : 0f);
                rawRightB[i] = (rNear ? 0.55f : 0f) + (rMid ? 0.45f : 0f);
                rawFlow[i] = (lFar || rFar || IsLand(cx, cz)) ? 1f : 0f;

                double rdCenter = DistanceToRoadSet(cx, cz, localSegs);
                double rdL = DistanceToRoadSet(cx + nx * (hw + 9), cz + nz * (hw + 9), localSegs);
                double rdR = DistanceToRoadSet(cx - nx * (hw + 9), cz - nz * (hw + 9), localSegs);
                float bridgeSuppress = Smooth(11.0, 23.0, rdCenter);
                leftRoad[i] = Math.Min(bridgeSuppress, Smooth(14.5, 30.0, rdL));
                rightRoad[i] = Math.Min(bridgeSuppress, Smooth(14.5, 30.0, rdR));

                leftGround[i] = Math.Max(0f, TerrainHeight(cx + nx * (hw + 26), cz + nz * (hw + 26)));
                rightGround[i] = Math.Max(0f, TerrainHeight(cx - nx * (hw + 26), cz - nz * (hw + 26)));
                urbanArr[i] = UrbanBlend(cx, cz);
                leftCity[i] = IsInsideCityGrid(cx + nx * (hw + 12), cz + nz * (hw + 12));
                rightCity[i] = IsInsideCityGrid(cx - nx * (hw + 12), cz - nz * (hw + 12));
            }

            // 河口や合流点で護岸・流水がぶつ切りにならないよう前後サンプルを滑らかに平均化する
            float SmoothWindow(float[] arr, int idx, int radius)
            {
                float sum = 0;
                int cnt = 0;
                for (int k = idx - radius; k <= idx + radius; k++)
                {
                    int j = Math.Clamp(k, 0, n - 1);
                    sum += arr[j];
                    cnt++;
                }
                return sum / cnt;
            }

            var samples = new RiverSample[n];
            for (int i = 0; i < n; i++)
            {
                float endTaper = 1f;
                if (n > 8)
                {
                    float tailDist = totalLen - cumS[i];
                    if (tailDist < 120f && rawFlow[^1] < 0.5f)
                        endTaper = Math.Clamp(tailDist / 120f, 0f, 1f);
                }
                float lb = SmoothWindow(rawLeftB, i, 3);
                float rb = SmoothWindow(rawRightB, i, 3);
                float ff = SmoothWindow(rawFlow, i, 4) * endTaper;
                samples[i] = new RiverSample(
                    worldPts[i].X, worldPts[i].Z, txs[i], tzs[i], nxs[i], nzs[i],
                    cumS[i], halfWidths[i], lb, rb, ff, urbanArr[i],
                    leftRoad[i], rightRoad[i], leftGround[i], rightGround[i],
                    leftCity[i], rightCity[i]);
            }

            rivers.Add(new RiverRibbon
            {
                Name = name,
                Rank = rank,
                Seed = StableHash(name),
                Samples = samples,
                MinX = minX,
                MaxX = maxX,
                MinZ = minZ,
                MaxZ = maxZ,
                TotalLength = totalLen,
                MaxHalfW = (float)LandRaster.RiverHalfWidth(rank, 1.0),
            });
        }
    }

    private BankProfile ComputeBankProfile(
        float halfW, float bankW, float flowFade, float roadClear, float urban,
        float outerGround, bool cityGrid, float noise, float aerialT)
    {
        float inlandMix = bankW * flowFade;
        float waterSurfY = WaterY + 0.03f + aerialT * inlandMix * 0.45f;
        float isUrban = Math.Clamp(urban + (cityGrid ? 0.65f : 0f), 0f, 1f);
        float plainAllowed = Smooth(2.2, 0.4, outerGround);

        float d0 = 0f;
        float d1 = halfW * 0.65f;
        float d2 = halfW + 1.0f;
        float d3 = halfW + LerpF(7.0f + noise * 2.5f, 3.2f, isUrban);
        float d4 = d3 + LerpF(6.2f, 5.5f, isUrban);
        float d5 = d4 + LerpF(2.6f, 2.2f, isUrban);
        float d6 = halfW + 33.0f;

        float y0 = Math.Max(SeaY + 0.16f, waterSurfY - 0.60f);
        float y1 = Math.Max(SeaY + 0.30f, waterSurfY - 0.40f);
        float y2 = waterSurfY + 0.05f;
        if (roadClear < 0.25f) y2 = Math.Min(y2, -0.10f);

        float bridgeDropY = -0.11f;
        float ruralBarY = LerpF(-0.10f, 0.07f + noise * 0.04f, roadClear);
        float urbanWallY = cityGrid ? LerpF(-0.12f, -0.02f, roadClear) : LerpF(-0.12f, 0.06f, roadClear);
        float y3 = LerpF(ruralBarY, urbanWallY, isUrban);
        if (bankW > 0.3f && roadClear > 0.25f) y3 = Math.Max(y3, waterSurfY + 0.02f);

        float hillClimb = Math.Max(0f, outerGround * 0.58f);
        float ruralCrestY = LerpF(bridgeDropY, 0.56f + noise * 0.08f, roadClear * plainAllowed) + hillClimb;
        float urbanPromY = (cityGrid ? LerpF(-0.12f, -0.02f, roadClear) : LerpF(-0.12f, 0.05f, roadClear)) + hillClimb * 0.5f;
        float y4 = LerpF(ruralCrestY, urbanPromY, isUrban);
        float y5 = y4 - LerpF(0.02f, 0.01f, isUrban);
        float outerCap = (cityGrid || roadClear < 0.35f) ? Math.Min(outerGround - 0.08f, -0.09f) : outerGround - 0.08f;
        float y6 = outerCap;

        if (bankW < 0.999f)
        {
            float seaFloor = SeaY - 0.05f;
            y2 = LerpF(seaFloor, y2, bankW);
            y3 = LerpF(seaFloor, y3, bankW);
            y4 = LerpF(seaFloor, y4, bankW);
            y5 = LerpF(seaFloor, y5, bankW);
            y6 = LerpF(seaFloor, y6, bankW);
        }

        return new BankProfile(
            d0, d1, d2, d3, d4, d5, d6,
            y0, y1, y2, y3, y4, y5, y6,
            isUrban, roadClear, plainAllowed, noise, bankW);
    }

    /// <summary>座標 (x, z) が河川チャネル・堤防範囲にあるとき、正確な河床・河川敷・堤防高さと水没判定を返す。</summary>
    private bool TryGetRiverAt(double x, double z, out float groundY, out bool inWater)
    {
        groundY = 0;
        inWater = false;
        double bestDist = double.MaxValue;
        RiverRibbon? bestRiver = null;
        int bestIdx = -1;
        double bestT = 0;
        double bestSigned = 0;

        foreach (var r in rivers)
        {
            double reach = r.MaxHalfW + 36.0;
            if (x < r.MinX - reach || x > r.MaxX + reach || z < r.MinZ - reach || z > r.MaxZ + reach)
                continue;
            var pts = r.Samples;
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                double abx = b.X - a.X, abz = b.Z - a.Z;
                double len2 = abx * abx + abz * abz;
                double t = len2 > 1e-6 ? Math.Clamp(((x - a.X) * abx + (z - a.Z) * abz) / len2, 0.0, 1.0) : 0.0;
                double px = a.X + abx * t, pz = a.Z + abz * t;
                double dx = x - px, dz = z - pz;
                double d2 = dx * dx + dz * dz;
                if (d2 < bestDist)
                {
                    double nx = a.Nx + (b.Nx - a.Nx) * t;
                    double nz = a.Nz + (b.Nz - a.Nz) * t;
                    bestDist = d2;
                    bestRiver = r;
                    bestIdx = i;
                    bestT = t;
                    bestSigned = dx * nx + dz * nz;
                }
            }
        }

        if (bestRiver == null || bestDist > (bestRiver.MaxHalfW + 34.0) * (bestRiver.MaxHalfW + 34.0))
            return false;

        var sa = bestRiver.Samples[bestIdx];
        var sb = bestRiver.Samples[bestIdx + 1];
        float ft = (float)bestT;
        float halfW = LerpF(sa.HalfW, sb.HalfW, ft);
        double absD = Math.Sqrt(bestDist);
        if (absD > halfW + 33.0) return false;

        bool left = bestSigned >= 0;
        float bankW = left ? LerpF(sa.LeftBank, sb.LeftBank, ft) : LerpF(sa.RightBank, sb.RightBank, ft);
        float flowFade = LerpF(sa.FlowFade, sb.FlowFade, ft);
        if (bankW < 0.08f && flowFade < 0.12f) return false;

        float roadClear = left ? LerpF(sa.LeftRoad, sb.LeftRoad, ft) : LerpF(sa.RightRoad, sb.RightRoad, ft);
        float outerG = left ? LerpF(sa.LeftGround, sb.LeftGround, ft) : LerpF(sa.RightGround, sb.RightGround, ft);
        bool cityGrid = left ? (ft < 0.5f ? sa.LeftCityGrid : sb.LeftCityGrid) : (ft < 0.5f ? sa.RightCityGrid : sb.RightCityGrid);
        float urban = LerpF(sa.Urban, sb.Urban, ft);
        float noise = Scene.Hash((long)bestRiver.Seed + bestIdx * 131L);
        var prof = ComputeBankProfile(halfW, bankW, flowFade, roadClear, urban, outerG, cityGrid, noise, 0f);

        inWater = absD <= halfW && flowFade >= 0.1f;
        groundY = prof.EvalY(absD);
        return true;
    }

    private static Color GrassGroundColor(double wz, float urban, float noise)
    {
        float north = Math.Clamp((float)((wz / M) - 4) / 5f, 0, 1);
        var grass = Lerp(Rgb(86 + (int)(noise * 18), 146 + (int)(noise * 14), 70), Rgb(150, 175, 130), north * 0.6f);
        if (urban > 0.01f)
        {
            var urbanCol = Rgb(140 + (int)(noise * 10), 142 + (int)(noise * 10), 146 + (int)(noise * 10));
            grass = Lerp(grass, urbanCol, urban * 0.85f);
        }
        return grass;
    }

    private (int Start, int End) VisibleRiverRange(RiverRibbon r, double extraRadius)
    {
        double lim = ViewRadius + extraRadius;
        if (PX < r.MinX - lim || PX > r.MaxX + lim || PZ < r.MinZ - lim || PZ > r.MaxZ + lim)
            return (1, 0);
        var pts = r.Samples;
        double lim2 = lim * lim;
        int start = int.MaxValue, end = -1;
        for (int i = 0; i < pts.Length; i++)
        {
            double dx = pts[i].X - PX, dz = pts[i].Z - PZ;
            if (dx * dx + dz * dz <= lim2)
            {
                if (i < start) start = i;
                if (i > end) end = i;
            }
        }
        if (end < 0) return (1, 0);
        return (Math.Max(0, start - 2), Math.Min(pts.Length - 1, end + 2));
    }

    private float CurrentAerialLift()
    {
        float byHeight = Smooth(110.0, 260.0, cam.Y);
        float byView = Smooth(1600.0, 3200.0, ViewRadius) * 0.65f;
        return Math.Clamp(Math.Max(byHeight, byView), 0f, 1f);
    }

    /// <summary>河川の河床・砂利の河原・草地堤防・都市部の護岸擁壁・川辺の小石やヨシを描画する。</summary>
    private void DrawRivers(float time)
    {
        float aerialT = CurrentAerialLift();
        foreach (var r in rivers)
        {
            var (i0, i1) = VisibleRiverRange(r, r.MaxHalfW + 80.0);
            if (i1 <= i0) continue;

            Rlgl.Begin(DrawMode.Quads);
            DrawRiverRibbonSide(r, i0, i1, true, aerialT);
            DrawRiverRibbonSide(r, i0, i1, false, aerialT);
            Rlgl.End();

            DrawRiverProps(r, i0, i1, time);
        }
    }

    private void DrawRiverRibbonSide(RiverRibbon r, int i0, int i1, bool left, float aerialT)
    {
        var pts = r.Samples;
        double sign = left ? 1.0 : -1.0;

        BankProfile ProfileAt(int idx)
        {
            var s = pts[idx];
            float bankW = left ? s.LeftBank : s.RightBank;
            float roadClear = left ? s.LeftRoad : s.RightRoad;
            float outerG = left ? s.LeftGround : s.RightGround;
            bool cityGrid = left ? s.LeftCityGrid : s.RightCityGrid;
            float noise = Scene.Hash((long)r.Seed + idx * 131L);
            return ComputeBankProfile(s.HalfW, bankW, s.FlowFade, roadClear, s.Urban, outerG, cityGrid, noise, aerialT);
        }

        void EmitBand(
            in RiverSample a, in RiverSample b,
            float da0, float da1, float ya0, float ya1, Color ca0, Color ca1,
            float db0, float db1, float yb0, float yb1, Color cb0, Color cb1)
        {
            V(a.X + a.Nx * sign * da0, ya0, a.Z + a.Nz * sign * da0, ca0);
            V(a.X + a.Nx * sign * da1, ya1, a.Z + a.Nz * sign * da1, ca1);
            V(b.X + b.Nx * sign * db1, yb1, b.Z + b.Nz * sign * db1, cb1);
            V(b.X + b.Nx * sign * db0, yb0, b.Z + b.Nz * sign * db0, cb0);
        }

        void StationColors(
            in RiverSample s, in BankProfile p, int idx,
            out Color c0, out Color c1, out Color c2, out Color c3Wall, out Color c3Top, out Color c4, out Color c5, out Color c6)
        {
            float n = p.Noise;
            float blockShade = (idx % 2 == 0) ? 1.0f : 0.94f;
            var silt = Lerp(Rgb(38, 60, 56), Rgb(34, 48, 52), p.IsUrban);
            var shallowPebble = Lerp(Rgb(78, 88, 80), Rgb(68, 76, 82), p.IsUrban);
            var wetShore = Lerp(Rgb(118 - (int)(n * 10), 112 - (int)(n * 8), 96), Rgb(92, 98, 104), p.IsUrban);

            var gravelBar = Rgb(176 + (int)(n * 20), 168 + (int)(n * 16), 148 + (int)(n * 14));
            var revetWall = Rgb((int)(122 * blockShade), (int)(126 * blockShade), (int)(132 * blockShade));
            c3Wall = Lerp(gravelBar, revetWall, p.IsUrban);

            var urbanProm = Rgb(186 + (int)(n * 6), 188 + (int)(n * 6), 192 + (int)(n * 6));
            c3Top = Lerp(gravelBar, urbanProm, p.IsUrban);

            var grassCol = GrassGroundColor(s.Z, s.Urban, n);
            var leveeTurf = Lerp(Rgb(80 + (int)(n * 16), 132 + (int)(n * 16), 64), grassCol, 0.45f);
            float showPath = p.RoadClear * p.PlainAllowed;
            var crestPath = Lerp(leveeTurf, Rgb(176, 172, 162), showPath * 0.92f);
            var urbanWalk = Rgb(154 + (int)(n * 8), 156 + (int)(n * 8), 160 + (int)(n * 8));

            c0 = silt;
            c1 = shallowPebble;
            c2 = wetShore;
            c4 = Lerp(leveeTurf, urbanWalk, p.IsUrban);
            c5 = Lerp(crestPath, urbanWalk, p.IsUrban);
            c6 = grassCol;
        }

        var prevS = pts[i0];
        var prevP = ProfileAt(i0);
        StationColors(prevS, prevP, i0, out var pa0, out var pa1, out var pa2, out var pa3w, out var pa3t, out var pa4, out var pa5, out var pa6);

        int emittedQuads = 0;
        for (int i = i0; i < i1; i++)
        {
            var curS = pts[i + 1];
            var curP = ProfileAt(i + 1);
            StationColors(curS, curP, i + 1, out var pb0, out var pb1, out var pb2, out var pb3w, out var pb3t, out var pb4, out var pb5, out var pb6);

            bool hasWater = prevS.FlowFade > 0.02f || curS.FlowFade > 0.02f;
            bool hasBank = prevP.BankWeight > 0.02f || curP.BankWeight > 0.02f;

            if (hasWater || hasBank)
            {
                EmitBand(prevS, curS, prevP.D0, prevP.D1, prevP.Y0, prevP.Y1, pa0, pa1, curP.D0, curP.D1, curP.Y0, curP.Y1, pb0, pb1);
                EmitBand(prevS, curS, prevP.D1, prevP.D2, prevP.Y1, prevP.Y2, pa1, pa2, curP.D1, curP.D2, curP.Y1, curP.Y2, pb1, pb2);
                emittedQuads += 2;
            }
            if (hasBank)
            {
                EmitBand(prevS, curS, prevP.D2, prevP.D3, prevP.Y2, prevP.Y3, pa2, pa3w, curP.D2, curP.D3, curP.Y2, curP.Y3, pb2, pb3w);
                EmitBand(prevS, curS, prevP.D3, prevP.D4, prevP.Y3, prevP.Y4, pa3t, pa4, curP.D3, curP.D4, curP.Y3, curP.Y4, pb3t, pb4);
                EmitBand(prevS, curS, prevP.D4, prevP.D5, prevP.Y4, prevP.Y5, pa4, pa5, curP.D4, curP.D5, curP.Y4, curP.Y5, pb4, pb5);
                EmitBand(prevS, curS, prevP.D5, prevP.D6, prevP.Y5, prevP.Y6, pa5, pa6, curP.D5, curP.D6, curP.Y5, curP.Y6, pb5, pb6);
                emittedQuads += 4;
            }

            if (emittedQuads >= 360)
            {
                Rlgl.End();
                Rlgl.Begin(DrawMode.Quads);
                emittedQuads = 0;
            }

            prevS = curS;
            prevP = curP;
            pa0 = pb0; pa1 = pb1; pa2 = pb2; pa3w = pb3w; pa3t = pb3t; pa4 = pb4; pa5 = pb5; pa6 = pb6;
        }
    }

    /// <summary>農村・中上流の河原に川石や風になびくヨシ群生を描画して水辺のディテールを高める。</summary>
    private void DrawRiverProps(RiverRibbon r, int i0, int i1, float time)
    {
        if (Altitude > 160) return;
        const double propMaxDist2 = 520.0 * 520.0;
        var pts = r.Samples;

        Rlgl.Begin(DrawMode.Quads);
        int quads = 0;
        for (int i = i0; i <= i1; i++)
        {
            var s = pts[i];
            if (s.Urban > 0.45f) continue;
            double dx = s.X - PX, dz = s.Z - PZ;
            if (dx * dx + dz * dz > propMaxDist2) continue;

            for (int side = -1; side <= 1; side += 2)
            {
                bool left = side > 0;
                float bankW = left ? s.LeftBank : s.RightBank;
                float roadClear = left ? s.LeftRoad : s.RightRoad;
                bool cityGrid = left ? s.LeftCityGrid : s.RightCityGrid;
                if (bankW < 0.7f || roadClear < 0.65f || cityGrid) continue;

                long sid = (long)r.Seed + i * 7919L + (left ? 17L : 53L);
                float h0 = Scene.Hash(sid);
                float noise = Scene.Hash((long)r.Seed + i * 131L);
                var prof = ComputeBankProfile(
                    s.HalfW, bankW, s.FlowFade, roadClear, s.Urban,
                    left ? s.LeftGround : s.RightGround, cityGrid, noise, 0f);

                // 川石（河原と水際の礫）
                if (h0 < 0.46f)
                {
                    float lat = prof.D2 + 0.8f + Scene.Hash(sid + 1) * Math.Max(1.0f, prof.D3 - prof.D2 - 1.5f);
                    float along = (Scene.Hash(sid + 2) - 0.5f) * 16f;
                    double rx = s.X + s.Nx * side * lat + s.Tx * along;
                    double rz = s.Z + s.Nz * side * lat + s.Tz * along;
                    float ry = prof.EvalY(lat) - 0.06f;
                    float rad = 0.65f + Scene.Hash(sid + 3) * 0.85f;
                    float hRock = rad * (0.45f + Scene.Hash(sid + 4) * 0.25f);
                    var rockTop = Rgb(126, 130, 128);
                    var rockSide = Rgb(92, 96, 94);

                    V(rx - rad * 0.7f, ry + hRock, rz - rad * 0.6f, rockTop);
                    V(rx + rad * 0.7f, ry + hRock, rz - rad * 0.5f, rockTop);
                    V(rx + rad * 0.6f, ry + hRock, rz + rad * 0.7f, rockTop);
                    V(rx - rad * 0.6f, ry + hRock, rz + rad * 0.6f, rockTop);

                    V(rx - rad, ry, rz + rad, rockSide);
                    V(rx + rad, ry, rz + rad, rockSide);
                    V(rx + rad * 0.6f, ry + hRock, rz + rad * 0.7f, rockSide);
                    V(rx - rad * 0.6f, ry + hRock, rz + rad * 0.6f, rockSide);

                    V(rx + rad, ry, rz + rad, rockSide);
                    V(rx + rad, ry, rz - rad, rockSide);
                    V(rx + rad * 0.7f, ry + hRock, rz - rad * 0.5f, rockSide);
                    V(rx + rad * 0.6f, ry + hRock, rz + rad * 0.7f, rockSide);
                    quads += 3;
                }

                // 揺れるヨシ・ススキ群生（水際の湿地）
                if (Scene.Hash(sid + 9) < 0.42f)
                {
                    float lat = prof.D2 + 1.2f + Scene.Hash(sid + 10) * 3.0f;
                    float along = (Scene.Hash(sid + 11) - 0.5f) * 14f;
                    double rx = s.X + s.Nx * side * lat + s.Tx * along;
                    double rz = s.Z + s.Nz * side * lat + s.Tz * along;
                    float ry = prof.EvalY(lat) - 0.04f;
                    float sway = MathF.Sin(time * 2.2f + Scene.Hash(sid + 12) * 6.28f) * 0.22f;
                    float tall = 1.15f + Scene.Hash(sid + 13) * 0.55f;
                    var reedBase = Rgb(76, 114, 58);
                    var reedTip = Rgb(146, 152, 92);

                    V(rx - 0.28f, ry, rz - 0.1f, reedBase);
                    V(rx + 0.28f, ry, rz + 0.1f, reedBase);
                    V(rx + 0.16f + sway, ry + tall, rz + 0.08f + sway * 0.5f, reedTip);
                    V(rx - 0.16f + sway, ry + tall, rz - 0.08f + sway * 0.5f, reedTip);

                    V(rx - 0.1f, ry, rz - 0.28f, reedBase);
                    V(rx + 0.1f, ry, rz + 0.28f, reedBase);
                    V(rx + 0.08f + sway, ry + tall * 0.92f, rz + 0.16f + sway * 0.5f, reedTip);
                    V(rx - 0.08f + sway, ry + tall * 0.92f, rz - 0.16f + sway * 0.5f, reedTip);
                    quads += 2;
                }

                if (quads >= 320)
                {
                    Rlgl.End();
                    Rlgl.Begin(DrawMode.Quads);
                    quads = 0;
                }
            }
        }
        Rlgl.End();
    }

    /// <summary>DrawWater の半透明パス上で滑らかな川のグラデーション水面・岸辺の白波・下流への流水紋様を描画する。</summary>
    private void DrawRiverWater(float time)
    {
        float aerialT = CurrentAerialLift();
        foreach (var r in rivers)
        {
            var (i0, i1) = VisibleRiverRange(r, r.MaxHalfW + 80.0);
            if (i1 <= i0) continue;
            DrawRiverWaterSurface(r, i0, i1, time, aerialT);
            DrawRiverFlowStreaks(r, i0, i1, time, aerialT);
        }
    }

    private void WaterVert(double wx, float wy, double wz, Color baseCol, float baseAlpha, float flowFade)
    {
        var p = R(wx, wy, wz);
        float dist = Vector3.Distance(p, cam);
        var fogged = scene.FogC(baseCol, dist);
        float fogMix = Math.Clamp(dist / (float)Math.Max(400, ViewRadius * 0.92), 0f, 1f);
        float alpha = (baseAlpha + (255f - baseAlpha) * fogMix) * flowFade;
        Rlgl.Color4ub(fogged.R, fogged.G, fogged.B, (byte)Math.Clamp(alpha, 0, 255));
        Rlgl.Vertex3f(p.X, p.Y, p.Z);
    }

    private void OverlayWaterVert(double wx, float wy, double wz, Color baseCol, float alpha)
    {
        var p = R(wx, wy, wz);
        float dist = Vector3.Distance(p, cam);
        var fogged = scene.FogC(baseCol, dist);
        float fogMix = Math.Clamp(dist / (float)Math.Max(350, ViewRadius * 0.85), 0f, 1f);
        float a = alpha * (1f - fogMix);
        Rlgl.Color4ub(fogged.R, fogged.G, fogged.B, (byte)Math.Clamp(a, 0, 255));
        Rlgl.Vertex3f(p.X, p.Y, p.Z);
    }

    private void DrawRiverWaterSurface(RiverRibbon r, int i0, int i1, float time, float aerialT)
    {
        var pts = r.Samples;
        ReadOnlySpan<float> lanes = stackalloc float[] { -1.0f, -0.58f, 0.0f, 0.58f, 1.0f };
        float speed = r.Rank <= 4 ? 14f : 18f;

        float SurfaceYAt(in RiverSample s)
        {
            float inlandMix = Math.Min(s.LeftBank, s.RightBank) * s.FlowFade;
            return WaterY + 0.03f + aerialT * inlandMix * 0.45f;
        }

        Color LaneColor(in RiverSample s, int li, out float alpha)
        {
            float wave = MathF.Sin(s.S * 0.055f - time * speed * 0.25f + li * 1.15f) * 0.5f + 0.5f;
            float lift = wave * 16f;
            Color rural, city;
            if (li == 2)
            {
                rural = Rgb(22 + (int)lift, 114 + (int)lift, 152 + (int)(lift * 0.7f));
                city = Rgb(26 + (int)lift, 90 + (int)lift, 124 + (int)(lift * 0.7f));
                alpha = 212f;
            }
            else if (li == 1 || li == 3)
            {
                rural = Rgb(36 + (int)lift, 148 + (int)lift, 178 + (int)(lift * 0.7f));
                city = Rgb(42 + (int)lift, 120 + (int)lift, 154 + (int)(lift * 0.7f));
                alpha = 198f;
            }
            else
            {
                rural = Rgb(90 + (int)(lift * 0.8f), 186 + (int)(lift * 0.8f), 196 + (int)(lift * 0.6f));
                city = Rgb(78 + (int)(lift * 0.8f), 146 + (int)(lift * 0.8f), 168 + (int)(lift * 0.6f));
                alpha = 152f;
            }
            return Lerp(rural, city, s.Urban);
        }

        int quads = 0;
        for (int i = i0; i < i1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (a.FlowFade <= 0.02f && b.FlowFade <= 0.02f) continue;

            float ya = SurfaceYAt(a), yb = SurfaceYAt(b);
            for (int li = 0; li < lanes.Length - 1; li++)
            {
                float u0 = lanes[li], u1 = lanes[li + 1];
                var ca0 = LaneColor(a, li, out float aa0);
                var ca1 = LaneColor(a, li + 1, out float aa1);
                var cb0 = LaneColor(b, li, out float ab0);
                var cb1 = LaneColor(b, li + 1, out float ab1);

                WaterVert(a.X + a.Nx * a.HalfW * u0, ya, a.Z + a.Nz * a.HalfW * u0, ca0, aa0, a.FlowFade);
                WaterVert(a.X + a.Nx * a.HalfW * u1, ya, a.Z + a.Nz * a.HalfW * u1, ca1, aa1, a.FlowFade);
                WaterVert(b.X + b.Nx * b.HalfW * u1, yb, b.Z + b.Nz * b.HalfW * u1, cb1, ab1, b.FlowFade);
                WaterVert(b.X + b.Nx * b.HalfW * u0, yb, b.Z + b.Nz * b.HalfW * u0, cb0, ab0, b.FlowFade);
                quads++;
            }

            if (quads >= 320)
            {
                Rlgl.End();
                Rlgl.Begin(DrawMode.Quads);
                quads = 0;
            }
        }
    }

    private void DrawRiverFlowStreaks(RiverRibbon r, int i0, int i1, float time, float aerialT)
    {
        var pts = r.Samples;
        float speed = r.Rank <= 4 ? 19f : 23f;
        var foamCol = Rgb(228, 248, 252);
        var glintCol = Rgb(208, 242, 250);

        // 1) 岸辺の白波・さざなみリボン
        int quads = 0;
        for (int i = i0; i < i1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (a.FlowFade <= 0.05f && b.FlowFade <= 0.05f) continue;
            double dx = a.X - PX, dz = a.Z - PZ;
            if (dx * dx + dz * dz > 1400.0 * 1400.0) continue;

            float ya = WaterY + 0.042f + aerialT * Math.Min(a.LeftBank, a.RightBank) * a.FlowFade * 0.45f;
            float yb = WaterY + 0.042f + aerialT * Math.Min(b.LeftBank, b.RightBank) * b.FlowFade * 0.45f;

            for (int side = -1; side <= 1; side += 2)
            {
                float bankA = side > 0 ? a.LeftBank : a.RightBank;
                float bankB = side > 0 ? b.LeftBank : b.RightBank;
                if (bankA <= 0.1f && bankB <= 0.1f) continue;

                float pulseA = MathF.Sin(a.S * 0.11f - time * 3.6f + side * 1.9f) * 0.5f + 0.5f;
                float pulseB = MathF.Sin(b.S * 0.11f - time * 3.6f + side * 1.9f) * 0.5f + 0.5f;
                float outerU = 0.995f;
                float innerUA = 0.92f - pulseA * 0.035f;
                float innerUB = 0.92f - pulseB * 0.035f;
                float aa = (55f + pulseA * 80f) * a.FlowFade * bankA * (1f - a.Urban * 0.45f);
                float ab = (55f + pulseB * 80f) * b.FlowFade * bankB * (1f - b.Urban * 0.45f);

                OverlayWaterVert(a.X + a.Nx * side * a.HalfW * innerUA, ya, a.Z + a.Nz * side * a.HalfW * innerUA, foamCol, aa * 0.25f);
                OverlayWaterVert(a.X + a.Nx * side * a.HalfW * outerU, ya, a.Z + a.Nz * side * a.HalfW * outerU, foamCol, aa);
                OverlayWaterVert(b.X + b.Nx * side * b.HalfW * outerU, yb, b.Z + b.Nz * side * b.HalfW * outerU, foamCol, ab);
                OverlayWaterVert(b.X + b.Nx * side * b.HalfW * innerUB, yb, b.Z + b.Nz * side * b.HalfW * innerUB, foamCol, ab * 0.25f);
                quads++;
            }

            if (quads >= 320)
            {
                Rlgl.End();
                Rlgl.Begin(DrawMode.Quads);
                quads = 0;
            }
        }

        // 2) 下流へ向かって連続的に流れる滑らかな流心・細波リボン
        float sMin = pts[i0].S;
        float sMax = pts[i1].S;
        const float period = 52f;
        const float streakLen = 34f;
        ReadOnlySpan<float> baseLanes = stackalloc float[] { -0.54f, -0.18f, 0.20f, 0.56f };

        for (int li = 0; li < baseLanes.Length; li++)
        {
            float travel = (time * speed + li * 13.7f) % period;
            float firstS = MathF.Ceiling((sMin - travel) / period) * period + travel;
            int scan = i0;

            bool SampleAtS(float targetS, out double sx, out double sz, out double snx, out double snz, out float shw, out float sf, out float sy)
            {
                sx = sz = snx = snz = 0;
                shw = sf = sy = 0;
                while (scan + 1 < i1 && pts[scan + 1].S < targetS) scan++;
                if (scan >= i1 || targetS < pts[scan].S || targetS > pts[scan + 1].S) return false;
                var a = pts[scan];
                var b = pts[scan + 1];
                float t = (targetS - a.S) / Math.Max(0.1f, b.S - a.S);
                sx = a.X + (b.X - a.X) * t;
                sz = a.Z + (b.Z - a.Z) * t;
                snx = a.Nx + (b.Nx - a.Nx) * t;
                snz = a.Nz + (b.Nz - a.Nz) * t;
                shw = LerpF(a.HalfW, b.HalfW, t);
                sf = LerpF(a.FlowFade, b.FlowFade, t);
                float inland = LerpF(Math.Min(a.LeftBank, a.RightBank), Math.Min(b.LeftBank, b.RightBank), t) * sf;
                sy = WaterY + 0.044f + aerialT * inland * 0.45f;
                return true;
            }

            for (float sHead = firstS; sHead <= sMax; sHead += period)
            {
                float sTail = sHead - streakLen;
                float sMid = sHead - streakLen * 0.48f;
                if (sTail < sMin) continue;
                if (!SampleAtS(sTail, out double tx0, out double tz0, out double tnx, out double tnz, out float thw, out float tf, out float ty)) continue;
                if (!SampleAtS(sMid, out double mx0, out double mz0, out double mnx, out double mnz, out float mhw, out float mf, out float my)) continue;
                if (mf <= 0.08f) continue;
                double mdx = mx0 - PX, mdz = mz0 - PZ;
                if (mdx * mdx + mdz * mdz > 1500.0 * 1500.0) continue;
                if (!SampleAtS(sHead, out double hx0, out double hz0, out double hnx, out double hnz, out float hhw, out float hf, out float hy)) continue;

                float uTail = baseLanes[li] + MathF.Sin(sTail * 0.025f + li * 2.1f) * 0.12f;
                float uMid = baseLanes[li] + MathF.Sin(sMid * 0.025f + li * 2.1f) * 0.12f;
                float uHead = baseLanes[li] + MathF.Sin(sHead * 0.025f + li * 2.1f) * 0.12f;
                float wMid = 0.78f + (r.Rank <= 4 ? 0.28f : 0f);
                float alphaMid = 82f * mf;

                // 尾→中間（フェードイン）
                OverlayWaterVert(tx0 + tnx * thw * uTail, ty, tz0 + tnz * thw * uTail, glintCol, 0);
                OverlayWaterVert(tx0 + tnx * thw * uTail, ty, tz0 + tnz * thw * uTail, glintCol, 0);
                OverlayWaterVert(mx0 + mnx * (mhw * uMid + wMid * 0.5f), my, mz0 + mnz * (mhw * uMid + wMid * 0.5f), glintCol, alphaMid);
                OverlayWaterVert(mx0 + mnx * (mhw * uMid - wMid * 0.5f), my, mz0 + mnz * (mhw * uMid - wMid * 0.5f), glintCol, alphaMid);

                // 中間→先端（フェードアウト）
                OverlayWaterVert(mx0 + mnx * (mhw * uMid - wMid * 0.5f), my, mz0 + mnz * (mhw * uMid - wMid * 0.5f), glintCol, alphaMid);
                OverlayWaterVert(mx0 + mnx * (mhw * uMid + wMid * 0.5f), my, mz0 + mnz * (mhw * uMid + wMid * 0.5f), glintCol, alphaMid);
                OverlayWaterVert(hx0 + hnx * hhw * uHead, hy, hz0 + hnz * hhw * uHead, glintCol, 0);
                OverlayWaterVert(hx0 + hnx * hhw * uHead, hy, hz0 + hnz * hhw * uHead, glintCol, 0);
                quads += 2;

                if (quads >= 320)
                {
                    Rlgl.End();
                    Rlgl.Begin(DrawMode.Quads);
                    quads = 0;
                }
            }
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

            // ターミナルビルと管制塔（滑走路に平行配置・滑走路横 +46m）
            double tx = ap.X + nx * 46, tz = ap.Z + nz * 46;
            var termPos = R(tx, RoadY, tz);
            float d = Vector3.Distance(termPos, cam);
            if (d < 1300)
            {
                float yaw = -(float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                Rlgl.PushMatrix();
                Rlgl.Translatef(termPos.X, termPos.Y, termPos.Z);
                Rlgl.Rotatef(yaw, 0, 1, 0);

                var wallC = scene.FogC(Rgb(218, 224, 232), d);
                var trimC = scene.FogC(Rgb(150, 158, 170), d);
                var glassC = scene.Night
                    ? scene.FogC(Rgb(220, 235, 255), d, lit: true)
                    : scene.FogC(Rgb(66, 128, 168), d);
                var roofC = scene.FogC(Rgb(118, 126, 138), d);

                // コンコース基礎・1-2階本体・ガラスファサード・大屋根庇
                Raylib.DrawCube(new Vector3(0, 5f, 0), 18, 10, 44, wallC);
                Raylib.DrawCube(new Vector3(-9.05f, 5.2f, 0), 0.2f, 6.2f, 38, glassC); // 滑走路側大窓
                Raylib.DrawCube(new Vector3(9.05f, 4.4f, 0), 0.2f, 4.2f, 32, glassC);  // 街側大窓
                Raylib.DrawCube(new Vector3(-1f, 10.3f, 0), 21.5f, 0.9f, 47, roofC);  // 張り出し大屋根
                // 左右の搭乗ウィングとボーディングブリッジ風通路
                Raylib.DrawCube(new Vector3(-10.5f, 4.2f, -15), 5.5f, 2.8f, 3.2f, trimC);
                Raylib.DrawCube(new Vector3(-10.5f, 4.2f, 15), 5.5f, 2.8f, 3.2f, trimC);
                Raylib.DrawCube(new Vector3(-1.5f, 11.8f, 0), 8, 2.4f, 16, trimC);
                Rlgl.PopMatrix();

                var towerBase = R(tx + nx * 8 + dx * 28, RoadY, tz + nz * 8 + dz * 28);
                Raylib.DrawCylinder(towerBase, 2.6f, 3.4f, 28f, 8, scene.FogC(Rgb(228, 232, 238), d));
                Raylib.DrawCylinder(towerBase + new Vector3(0, 28f, 0), 5.0f, 3.4f, 4.6f, 8, scene.FogC(Rgb(80, 160, 210), d, lit: scene.Night));
                Raylib.DrawCylinder(towerBase + new Vector3(0, 32.6f, 0), 4.4f, 5.2f, 1.2f, 8, scene.FogC(Rgb(235, 235, 240), d));
                // 回転ビーコン灯
                bool beaconOn = ((int)(time * 4) & 1) == 0;
                var bCol = beaconOn ? Rgb(80, 255, 130) : Rgb(255, 255, 255);
                Raylib.DrawSphere(towerBase + new Vector3(0, 35.3f, 0), 0.9f, scene.FogC(bCol, d, lit: true));
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

    private float StreetY(double x, double z) => Math.Max(0.08f, BaseGroundY(x, z) + 0.05f);

    private bool IsStreetClear(Place pl, double x, double z)
    {
        if ((x - pl.X) * (x - pl.X) + (z - pl.Z) * (z - pl.Z) > pl.Radius * pl.Radius * 0.84) return false;
        if (!IsLand(x, z)) return false;
        float bh = BaseGroundY(x, z);
        if (bh < -0.05f || bh > 8f) return false;
        return NearRoadDistance(x, z) >= 16.0;
    }

    private void DrawTrafficSignal3D(double poleX, double poleZ, float baseY, double faceDx, double faceDz,
        float armLen, float overhangDir, float poleH, float scale, int activeIdx, bool pedGreen,
        bool drawNamePlaque, float dist)
    {
        var poleBase = R(poleX, baseY, poleZ);
        var poleCol = scene.FogC(Rgb(168, 172, 178), dist);
        var boxCol = scene.FogC(Rgb(36, 40, 46), dist);
        float yaw = -(float)(Math.Atan2(faceDx, faceDz) * 180.0 / Math.PI);

        Rlgl.PushMatrix();
        Rlgl.Translatef(poleBase.X, poleBase.Y, poleBase.Z);
        Rlgl.Rotatef(yaw, 0, 1, 0);

        // 支柱と最上部の街灯
        Raylib.DrawCylinder(Vector3.Zero, 0.13f * scale, 0.16f * scale, poleH, 6, poleCol);
        float armY = poleH - 0.55f * scale;
        float hx = overhangDir * armLen;
        Raylib.DrawCube(new Vector3(hx * 0.5f, armY, 0), armLen + 0.16f * scale, 0.14f * scale, 0.14f * scale, poleCol);

        if (scale >= 0.9f)
        {
            var lampCol = scene.FogC(scene.Night ? Rgb(255, 224, 145) : Rgb(230, 234, 238), dist, lit: scene.Night);
            Raylib.DrawCube(new Vector3(overhangDir * 0.65f, poleH + 0.12f, 0), 1.3f, 0.12f, 0.14f, poleCol);
            Raylib.DrawSphere(new Vector3(overhangDir * 1.25f, poleH + 0.04f, 0), 0.22f, lampCol);
        }

        // 横型3灯式信号機（運転者から見て左から 青・黄・赤。背面が白飛びしないよう本体は濃灰色）
        float bw = 2.38f * scale, bh = 0.74f * scale, bd = 0.44f * scale;
        var headPos = new Vector3(hx, armY, 0);
        Raylib.DrawCube(headPos, bw, bh, bd, boxCol);
        Raylib.DrawCube(new Vector3(hx, armY, bd * 0.44f), bw + 0.08f * scale, bh + 0.08f * scale, 0.05f * scale, scene.FogC(Rgb(200, 206, 214), dist));
        Raylib.DrawCube(new Vector3(hx, armY, bd * 0.48f), bw - 0.06f * scale, bh - 0.06f * scale, 0.04f * scale, boxCol);

        float[] lx = { -0.74f * scale, 0f, 0.74f * scale };
        Color[] onCols = { Rgb(35, 240, 160), Rgb(255, 205, 30), Rgb(255, 45, 45) };
        Color[] offCols = { Rgb(22, 46, 40), Rgb(52, 42, 18), Rgb(52, 22, 22) };
        for (int li = 0; li < 3; li++)
        {
            Color c = li == activeIdx ? scene.FogC(onCols[li], dist, lit: true) : scene.FogC(offCols[li], dist);
            Raylib.DrawSphere(new Vector3(hx + lx[li], armY, bd * 0.48f), 0.24f * scale, c);
            // 庇（ひさし）
            Raylib.DrawCube(new Vector3(hx + lx[li], armY + 0.25f * scale, bd * 0.68f), 0.48f * scale, 0.08f * scale, 0.24f * scale, boxCol);
        }

        // 交差点名板（背面はアルミグレー、前面のみ白地に濃紺ライン）
        if (drawNamePlaque)
        {
            float px = hx + overhangDir * 0.25f, py = armY - 0.64f * scale;
            Raylib.DrawCube(new Vector3(px, py, 0.01f), 1.48f * scale, 0.36f * scale, 0.06f * scale, scene.FogC(Rgb(132, 138, 146), dist));
            Raylib.DrawCube(new Vector3(px, py, 0.05f), 1.44f * scale, 0.32f * scale, 0.04f * scale, scene.FogC(Rgb(242, 244, 248), dist));
            Raylib.DrawCube(new Vector3(px, py, 0.08f), 1.15f * scale, 0.11f * scale, 0.03f * scale, scene.FogC(Rgb(25, 55, 115), dist));
        }

        // 歩行者用信号機（柱の中腹）
        if (scale >= 0.75f)
        {
            float pedY = 2.95f;
            float pDir = overhangDir > 0 ? 0.26f : -0.26f;
            Raylib.DrawCube(new Vector3(pDir, pedY, 0), 0.36f, 0.72f, 0.28f, boxCol);
            var pedRed = !pedGreen ? scene.FogC(Rgb(255, 55, 55), dist, lit: true) : scene.FogC(Rgb(55, 22, 22), dist);
            var pedGrn = pedGreen ? scene.FogC(Rgb(45, 240, 155), dist, lit: true) : scene.FogC(Rgb(20, 48, 40), dist);
            Raylib.DrawSphere(new Vector3(pDir, pedY + 0.17f, 0.14f), 0.11f, pedRed);
            Raylib.DrawSphere(new Vector3(pDir, pedY - 0.17f, 0.14f), 0.11f, pedGrn);
        }

        Rlgl.PopMatrix();
    }

    /// <summary>都市・町の内部に碁盤の目の一般道（街路と信号・横断歩道付き交差点）を描く。</summary>
    private void DrawCityStreets(float time)
    {
        if (Altitude > 550) return;
        const double gridStep = CityCell * 2; // 88m間隔で街路を通す
        const double sliceLen = 22;
        const double roadHW = 3.8;
        const double walkHW = 5.2;
        const double jHalf = 5.2;
        const double chamfer = 0.85;

        var streetCol = Rgb(80, 82, 88);
        var walkCol = Rgb(172, 172, 168);
        var curbCol = Rgb(126, 128, 134);
        var white = Rgb(236, 236, 236);
        var amber = Rgb(245, 170, 28);

        var streetProps = new List<(double X, double Z, int Kx, int Kz, bool N, bool S, bool E, bool W)>();

        void StripOnFrame(double ox, double oz, double dx, double dz, double nx, double nz,
            double d0, double d1, double w0, double w1, float dy, Color c)
        {
            double ax = ox + dx * d0, az = oz + dz * d0;
            double bx = ox + dx * d1, bz = oz + dz * d1;
            double p0x = ax + nx * w0, p0z = az + nz * w0;
            double p1x = ax + nx * w1, p1z = az + nz * w1;
            double p2x = bx + nx * w1, p2z = bz + nz * w1;
            double p3x = bx + nx * w0, p3z = bz + nz * w0;
            V(p0x, StreetY(p0x, p0z) + dy, p0z, c);
            V(p1x, StreetY(p1x, p1z) + dy, p1z, c);
            V(p2x, StreetY(p2x, p2z) + dy, p2z, c);
            V(p3x, StreetY(p3x, p3z) + dy, p3z, c);
        }

        void CurbWall(double p0x, double p0z, double p1x, double p1z, float hTop, Color c)
        {
            float y0 = StreetY(p0x, p0z), y1 = StreetY(p1x, p1z);
            V(p0x, y0, p0z, c);
            V(p0x, y0 + hTop, p0z, c);
            V(p1x, y1 + hTop, p1z, c);
            V(p1x, y1, p1z, c);
        }

        void DrawGridSlice(Place pl, double ox, double oz, double dx, double dz, double len, bool isFirstSlice, bool isLastSlice, bool hasStartJct, bool hasEndJct)
        {
            double a = isFirstSlice && hasStartJct ? jHalf : 0;
            double b = isLastSlice && hasEndJct ? len - jHalf : len;
            if (b <= a + 0.4) return;
            double mx = ox + dx * ((a + b) * 0.5), mz = oz + dz * ((a + b) * 0.5);
            double ax = ox + dx * a, az = oz + dz * a;
            double bx = ox + dx * b, bz = oz + dz * b;
            if (!IsStreetClear(pl, mx, mz) || !IsStreetClear(pl, ax, az) || !IsStreetClear(pl, bx, bz)) return;
            if (NearRoadDistance(ox + dx * ((a + b) * 0.25 + a * 0.5), oz + dz * ((a + b) * 0.25 + a * 0.5)) < 15.8) return;

            double nx = -dz, nz = dx;
            // 車道本体と左右の白線
            StripOnFrame(ox, oz, dx, dz, nx, nz, a, b, -roadHW, roadHW, 0f, streetCol);
            StripOnFrame(ox, oz, dx, dz, nx, nz, a, b, -roadHW + 0.12, -roadHW + 0.28, 0.022f, white);
            StripOnFrame(ox, oz, dx, dz, nx, nz, a, b, roadHW - 0.28, roadHW - 0.12, 0.022f, white);

            // 左右の立ち上げ歩道と縁石（交差点の十字路側へははみ出させない）
            StripOnFrame(ox, oz, dx, dz, nx, nz, a, b, -walkHW, -roadHW, 0.11f, walkCol);
            StripOnFrame(ox, oz, dx, dz, nx, nz, a, b, roadHW, walkHW, 0.11f, walkCol);
            CurbWall(ax - nx * roadHW, az - nz * roadHW, bx - nx * roadHW, bz - nz * roadHW, 0.11f, curbCol);
            CurbWall(ax + nx * roadHW, az + nz * roadHW, bx + nx * roadHW, bz + nz * roadHW, 0.11f, curbCol);

            // 中央線：交差点手前はオレンジの実線、ブロック中央は破線
            if (isFirstSlice && hasStartJct)
            {
                if (b > 8.2)
                    StripOnFrame(ox, oz, dx, dz, nx, nz, Math.Max(a, 7.7), Math.Min(b, 15.5), -0.09, 0.09, 0.025f, amber);
                if (b > 17.5)
                    StripOnFrame(ox, oz, dx, dz, nx, nz, 17.5, Math.Min(b, 22.0), -0.08, 0.08, 0.024f, white);
            }
            else if (isLastSlice && hasEndJct)
            {
                if (b > 5.0)
                    StripOnFrame(ox, oz, dx, dz, nx, nz, Math.Max(a, 0.0), Math.Min(b, 4.5), -0.08, 0.08, 0.024f, white);
                if (b > 7.5)
                    StripOnFrame(ox, oz, dx, dz, nx, nz, Math.Max(a, 6.5), Math.Min(b, len - 7.7), -0.09, 0.09, 0.025f, amber);
            }
            else
            {
                StripOnFrame(ox, oz, dx, dz, nx, nz, a + 1.0, Math.Min(b, a + 6.5), -0.08, 0.08, 0.024f, white);
                StripOnFrame(ox, oz, dx, dz, nx, nz, a + 12.0, Math.Min(b, a + 17.5), -0.08, 0.08, 0.024f, white);
            }
        }

        Rlgl.Begin(DrawMode.Quads);
        foreach (var pl in Places)
        {
            double maxRange = pl.Radius + Math.Min(900, ViewRadius);
            if ((pl.X - PX) * (pl.X - PX) + (pl.Z - PZ) * (pl.Z - PZ) > maxRange * maxRange) continue;

            int lines = (int)(pl.Radius * 0.88 / gridStep);
            int slices = (int)(pl.Radius * 0.88 / sliceLen);

            // 1) 南北・東西の中間街路（交差点の間を歩道付きで繋ぐ）
            for (int k = -lines; k <= lines; k++)
            {
                double sx = pl.X + k * gridStep;
                if (Math.Abs(sx - PX) <= 750)
                {
                    for (int s = -slices; s < slices; s++)
                    {
                        double z0 = pl.Z + s * sliceLen;
                        if (Math.Abs(z0 + sliceLen * 0.5 - PZ) > 750) continue;
                        int mod = ((s % 4) + 4) % 4;
                        bool first = mod == 0, last = mod == 3;
                        bool hasS = first && IsStreetClear(pl, sx, z0);
                        bool hasE = last && IsStreetClear(pl, sx, z0 + sliceLen);
                        DrawGridSlice(pl, sx, z0, 0, 1, sliceLen, first, last, hasS, hasE);
                    }
                }

                double sz = pl.Z + k * gridStep;
                if (Math.Abs(sz - PZ) <= 750)
                {
                    for (int s = -slices; s < slices; s++)
                    {
                        double x0 = pl.X + s * sliceLen;
                        if (Math.Abs(x0 + sliceLen * 0.5 - PX) > 750) continue;
                        int mod = ((s % 4) + 4) % 4;
                        bool first = mod == 0, last = mod == 3;
                        bool hasS = first && IsStreetClear(pl, x0, sz);
                        bool hasE = last && IsStreetClear(pl, x0 + sliceLen, sz);
                        DrawGridSlice(pl, x0, sz, 1, 0, sliceLen, first, last, hasS, hasE);
                    }
                }
            }

            // 2) 碁盤目の交差点（十字路・T字路：角切り歩道・横断歩道・左側停止線・中央ダイヤ）
            int jLines = (int)(pl.Radius * 0.85 / gridStep);
            for (int kx = -jLines; kx <= jLines; kx++)
            {
                double ix = pl.X + kx * gridStep;
                if (Math.Abs(ix - PX) > 750) continue;
                for (int kz = -jLines; kz <= jLines; kz++)
                {
                    double iz = pl.Z + kz * gridStep;
                    if (Math.Abs(iz - PZ) > 750) continue;
                    if (!IsStreetClear(pl, ix, iz)) continue;

                    bool hasN = IsStreetClear(pl, ix, iz + 11.0);
                    bool hasS = IsStreetClear(pl, ix, iz - 11.0);
                    bool hasE = IsStreetClear(pl, ix + 11.0, iz);
                    bool hasW = IsStreetClear(pl, ix - 11.0, iz);
                    int arms = (hasN ? 1 : 0) + (hasS ? 1 : 0) + (hasE ? 1 : 0) + (hasW ? 1 : 0);
                    if (arms == 0) continue;

                    // 中央アスファルト広場
                    StripOnFrame(ix, iz, 0, 1, -1, 0, -roadHW, roadHW, -roadHW, roadHW, 0f, streetCol);

                    // 4隅の角切り歩道クワッド
                    for (int sxSign = -1; sxSign <= 1; sxSign += 2)
                    {
                        for (int szSign = -1; szSign <= 1; szSign += 2)
                        {
                            double xIn = ix + sxSign * roadHW, xCut = ix + sxSign * (roadHW + chamfer), xOut = ix + sxSign * walkHW;
                            double zIn = iz + szSign * roadHW, zCut = iz + szSign * (roadHW + chamfer), zOut = iz + szSign * walkHW;

                            // 角切り下地のアスファルト三角形（隙間防止）
                            V(xIn, StreetY(xIn, zIn), zIn, streetCol);
                            V(xCut, StreetY(xCut, zIn), zIn, streetCol);
                            V(xIn, StreetY(xIn, zCut), zCut, streetCol);
                            V(xIn, StreetY(xIn, zIn), zIn, streetCol);

                            // 角切りされた立ち上げ歩道上面（+0.11m）
                            V(xCut, StreetY(xCut, zIn) + 0.11f, zIn, walkCol);
                            V(xOut, StreetY(xOut, zIn) + 0.11f, zIn, walkCol);
                            V(xOut, StreetY(xOut, zOut) + 0.11f, zOut, walkCol);
                            V(xCut, StreetY(xCut, zCut) + 0.11f, zCut, walkCol);

                            V(xIn, StreetY(xIn, zCut) + 0.11f, zCut, walkCol);
                            V(xCut, StreetY(xCut, zCut) + 0.11f, zCut, walkCol);
                            V(xOut, StreetY(xOut, zOut) + 0.11f, zOut, walkCol);
                            V(xIn, StreetY(xIn, zOut) + 0.11f, zOut, walkCol);

                            // 斜めの縁石立面
                            CurbWall(xCut, zIn, xIn, zCut, 0.11f, curbCol);
                        }
                    }

                    // 4方向アームの接続部・横断歩道・左側停止線
                    void DrawCardinalLeg(double dx, double dz, bool open)
                    {
                        double nx = -dz, nz = dx;
                        if (open)
                        {
                            StripOnFrame(ix, iz, dx, dz, nx, nz, roadHW, jHalf, -roadHW, roadHW, 0f, streetCol);
                            if (arms >= 2)
                            {
                                // 横断歩道（5本の白帯）
                                for (int st = -2; st <= 2; st++)
                                    StripOnFrame(ix, iz, dx, dz, nx, nz, 4.0, 6.4, st * 1.45 - 0.36, st * 1.45 + 0.36, 0.025f, white);
                                // 停止線（進行方向左側 = -nx 側）
                                StripOnFrame(ix, iz, dx, dz, nx, nz, 7.05, 7.50, -3.55, -0.22, 0.025f, white);
                            }
                        }
                        else
                        {
                            // T字路などの行き止まり側は縁石と歩道で綺麗に閉じる
                            StripOnFrame(ix, iz, dx, dz, nx, nz, roadHW, jHalf, -roadHW, roadHW, 0.11f, walkCol);
                            StripOnFrame(ix, iz, dx, dz, nx, nz, roadHW - 0.32, roadHW - 0.12, -roadHW, roadHW, 0.022f, white);
                            CurbWall(ix + dx * roadHW - nx * roadHW, iz + dz * roadHW - nz * roadHW,
                                     ix + dx * roadHW + nx * roadHW, iz + dz * roadHW + nz * roadHW, 0.11f, curbCol);
                        }
                    }

                    DrawCardinalLeg(0, 1, hasN);
                    DrawCardinalLeg(0, -1, hasS);
                    DrawCardinalLeg(1, 0, hasE);
                    DrawCardinalLeg(-1, 0, hasW);

                    // 交差点中心のひし形マーク（3方向以上）
                    if (arms >= 3)
                    {
                        double rOut = 1.45, rIn = 1.15;
                        for (int q = 0; q < 4; q++)
                        {
                            double a0 = q * Math.PI * 0.5, a1 = (q + 1) * Math.PI * 0.5;
                            double ax0 = ix + Math.Sin(a0) * rIn, az0 = iz + Math.Cos(a0) * rIn;
                            double bx0 = ix + Math.Sin(a0) * rOut, bz0 = iz + Math.Cos(a0) * rOut;
                            double bx1 = ix + Math.Sin(a1) * rOut, bz1 = iz + Math.Cos(a1) * rOut;
                            double ax1 = ix + Math.Sin(a1) * rIn, az1 = iz + Math.Cos(a1) * rIn;
                            V(ax0, StreetY(ax0, az0) + 0.025f, az0, white);
                            V(bx0, StreetY(bx0, bz0) + 0.025f, bz0, white);
                            V(bx1, StreetY(bx1, bz1) + 0.025f, bz1, white);
                            V(ax1, StreetY(ax1, az1) + 0.025f, az1, white);
                        }

                        double d2 = (ix - PX) * (ix - PX) + (iz - PZ) * (iz - PZ);
                        if (d2 < 260 * 260 && Altitude < 240 && NearRoadDistance(ix, iz) >= 22.0)
                            streetProps.Add((ix, iz, kx, kz, hasN, hasS, hasE, hasW));
                    }
                }
            }
        }
        Rlgl.End();

        // 3) 近くの都市交差点の信号機・カーブミラー・一時停止標識（3Dオブジェクト）
        if (streetProps.Count == 0 || Altitude >= 240) return;
        foreach (var sp in streetProps)
        {
            double d2 = (sp.X - PX) * (sp.X - PX) + (sp.Z - PZ) * (sp.Z - PZ);
            float dist = (float)Math.Sqrt(d2);
            bool majorCross = (sp.Kx & 1) == 0 && (sp.Kz & 1) == 0;

            if (majorCross)
            {
                float nsCycle = (time + (((sp.Kx + sp.Kz) & 1) * 4.5f)) % 18f;
                int nsState = nsCycle < 7.0f ? 0 : nsCycle < 8.4f ? 1 : 2;
                int ewState = nsCycle < 9.0f ? 2 : nsCycle < 16.0f ? 0 : nsCycle < 17.4f ? 1 : 2;

                void SignalCorner(bool active, double dx, double dz, int sigState, bool pedGrn)
                {
                    if (!active) return;
                    double nx = -dz, nz = dx;
                    // 左側車線（-nx側）脇の角切り歩道上にポールを立て、車道側へ腕を伸ばす
                    double px = sp.X + dx * 4.45 - nx * 4.45;
                    double pz = sp.Z + dz * 4.45 - nz * 4.45;
                    DrawTrafficSignal3D(px, pz, StreetY(px, pz) + 0.11f, dx, dz, 2.3f, 1f, 5.4f, 0.78f, sigState, pedGrn, false, dist);
                }

                SignalCorner(sp.N, 0, 1, nsState, nsState == 0);
                SignalCorner(sp.S, 0, -1, nsState, nsState == 0);
                SignalCorner(sp.E, 1, 0, ewState, ewState == 0);
                SignalCorner(sp.W, -1, 0, ewState, ewState == 0);
            }
            else if (d2 < 140 * 140 && Altitude < 140)
            {
                // 細街路交差点：オレンジ色のカーブミラーと逆三角形「止まれ」標識
                double mx = sp.X + 4.45, mz = sp.Z + 4.45;
                float my = StreetY(mx, mz) + 0.11f;
                var mPole = R(mx, my, mz);
                Raylib.DrawCylinder(mPole, 0.06f, 0.07f, 2.8f, 6, scene.FogC(Rgb(235, 165, 35), dist));
                var mirrorCenter = R(mx, my + 2.85f, mz);
                Rlgl.PushMatrix();
                Rlgl.Translatef(mirrorCenter.X, mirrorCenter.Y, mirrorCenter.Z);
                Rlgl.Rotatef(135, 0, 1, 0);
                Rlgl.Rotatef(90, 1, 0, 0);
                Raylib.DrawCylinder(Vector3.Zero, 0.44f, 0.44f, 0.10f, 12, scene.FogC(Rgb(245, 115, 20), dist));
                Raylib.DrawCylinder(new Vector3(0, 0.04f, 0), 0.34f, 0.34f, 0.06f, 12, scene.FogC(Rgb(215, 228, 238), dist));
                Rlgl.PopMatrix();

                double sx = sp.X - 4.45, sz = sp.Z - 4.45;
                float sy = StreetY(sx, sz) + 0.11f;
                var sPole = R(sx, sy, sz);
                Raylib.DrawCylinder(sPole, 0.05f, 0.05f, 2.25f, 5, scene.FogC(Rgb(165, 170, 175), dist));
                // 赤い逆三角形「止まれ」標識（白い外枠＋両面から見える赤地）
                Rlgl.PushMatrix();
                var signPos = R(sx, sy + 2.38f, sz);
                Rlgl.Translatef(signPos.X, signPos.Y, signPos.Z);
                Rlgl.Rotatef(90, 1, 0, 0);
                Raylib.DrawCylinder(new Vector3(0, -0.02f, 0), 0.46f, 0.46f, 0.04f, 3, scene.FogC(Rgb(242, 242, 242), dist));
                Raylib.DrawCylinder(new Vector3(0, -0.03f, 0), 0.37f, 0.37f, 0.06f, 3, scene.FogC(Rgb(215, 35, 35), dist));
                Rlgl.PopMatrix();
            }
        }
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

            void DrawSlice(double a, double b, bool inJunctionNeck, int jctIndex, int dashIdx, double medStart, double medEnd)
            {
                if (b <= a + 0.05) return;
                double ax = s.Ax + dx * a, az = s.Az + dz * a, bx = s.Ax + dx * b, bz = s.Az + dz * b;
                double mx = (ax + bx) * 0.5, mz = (az + bz) * 0.5;

                void StripRange(double sa, double sb, double w0, double w1, float y0, float y1, Color c)
                {
                    if (sb <= sa + 0.05) return;
                    double sax = s.Ax + dx * sa, saz = s.Az + dz * sa;
                    double sbx = s.Ax + dx * sb, sbz = s.Az + dz * sb;
                    V(sax + nx * w0, y0, saz + nz * w0, c);
                    V(sax + nx * w1, y1, saz + nz * w1, c);
                    V(sbx + nx * w1, y1, sbz + nz * w1, c);
                    V(sbx + nx * w0, y0, sbz + nz * w0, c);
                }
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
                    // 通常区間：陸上は法面、河川上・海上は橋桁とガードレールを描画する
                    bool onLand = IsLand(mx, mz);
                    if (onLand)
                    {
                        Strip(-10.2, -8.0, -0.04f, RoadY, shoulder);
                        Strip(8.0, 10.2, RoadY, -0.04f, shoulder);
                    }
                    else
                    {
                        float deep = s.Road.Type == RoadType.MegaBridge ? -12 : SeaY;
                        var gCol = s.Road.Type == RoadType.Expressway ? Rgb(148, 152, 158) : girder;
                        Side(-8.0, deep, RoadY, gCol);
                        Side(8.0, deep, RoadY, gCol);
                        Side(-8.0, RoadY, RoadY + 1.15f, rail);
                        Side(8.0, RoadY, RoadY + 1.15f, rail);
                    }

                    StripRange(Math.Max(a, medStart), Math.Min(b, medEnd), -0.5, 0.5, RoadY + 0.04f, RoadY + 0.04f, median);
                    Strip(-7.8, -7.55, RoadY + 0.03f, RoadY + 0.03f, white);
                    Strip(7.55, 7.8, RoadY + 0.03f, RoadY + 0.03f, white);
                    if ((dashIdx & 1) == 0)
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

            const double markOffset = 5.6; // 横断歩道・停止線のスペース分だけ中央分離帯の開始位置を奥にする
            double neckA = s.JctA >= 0 ? Math.Min(s.Len * 0.45, s.TrimA + markOffset) : 0;
            double neckB = s.JctB >= 0 ? Math.Min(s.Len * 0.45, s.TrimB + markOffset) : 0;
            double bodyStart = neckA, bodyEnd = Math.Max(neckA, len - neckB);
            double medStart = (s.JctA >= 0 && junctions[s.JctA].Arms.Count >= 2) ? Math.Min(bodyEnd, neckA + 8.5) : bodyStart;
            double medEnd = (s.JctB >= 0 && junctions[s.JctB].Arms.Count >= 2) ? Math.Max(medStart, bodyEnd - 8.5) : bodyEnd;

            // 1) A側の交差点ネック区間 [0, neckA]
            if (neckA > 0 && sMin < neckA)
            {
                double na = Math.Max(0, sMin), nb = Math.Min(neckA, sMax);
                const double neckStep = 4.0;
                int nk0 = (int)Math.Floor(na / neckStep), nk1 = (int)Math.Ceiling(nb / neckStep);
                for (int k = nk0; k < nk1; k++)
                    DrawSlice(Math.Max(na, k * neckStep), Math.Min(nb, (k + 1) * neckStep), true, s.JctA, k, medStart, medEnd);
            }

            // 2) 中央の通常道路区間 [neckA, len - neckB]
            if (sMax > bodyStart && sMin < bodyEnd)
            {
                double ba = Math.Max(bodyStart, sMin), bb = Math.Min(bodyEnd, sMax);
                // 破線や橋の境界がカメラ高度でスライドしないよう固定ワールド刻み（12mの2べき乗倍）で描画
                double step = ViewRadius > 3200 ? 48.0 : ViewRadius > 1600 ? 24.0 : 12.0;
                int k0 = (int)Math.Floor(ba / step), k1 = (int)Math.Ceiling(bb / step);
                for (int k = k0; k < k1; k++)
                {
                    double a = Math.Max(ba, k * step);
                    double b = Math.Min(bb, (k + 1) * step);
                    int dashIdx = (int)Math.Floor((a + 0.01) / 12.0);
                    DrawSlice(a, b, false, -1, dashIdx, medStart, medEnd);
                }
            }

            // 3) B側の交差点ネック区間 [len - neckB, len]
            if (neckB > 0 && sMax > bodyEnd)
            {
                double na = Math.Max(bodyEnd, sMin), nb = Math.Min(len, sMax);
                const double neckStep = 4.0;
                int nk0 = (int)Math.Floor((na - bodyEnd) / neckStep), nk1 = (int)Math.Ceiling((nb - bodyEnd) / neckStep);
                for (int k = nk0; k < nk1; k++)
                    DrawSlice(Math.Max(na, bodyEnd + k * neckStep), Math.Min(nb, bodyEnd + (k + 1) * neckStep), true, s.JctB, k, medStart, medEnd);
            }
        }

        // 折れ線の中間ウェイポイント（カーブ継手）を滑らかな円盤・路肩で繋ぐ
        foreach (var cv in curves)
        {
            double dist2 = (cv.X - PX) * (cv.X - PX) + (cv.Z - PZ) * (cv.Z - PZ);
            double maxR = ViewRadius + 25;
            if (dist2 > maxR * maxR) continue;
            bool onLand = IsLand(cv.X, cv.Z);
            float deep = cv.Road.Type == RoadType.MegaBridge ? -12 : SeaY;
            var cvGirder = cv.Road.Type == RoadType.Expressway ? Rgb(148, 152, 158) : girder;
            const int cslices = 16;
            for (int i = 0; i < cslices; i++)
            {
                double a0 = i * Math.PI * 2 / cslices, a1 = (i + 1) * Math.PI * 2 / cslices;
                double amid = (a0 + a1) * 0.5;
                double s0 = Math.Sin(a0), c0 = Math.Cos(a0), s1 = Math.Sin(a1), c1 = Math.Cos(a1);
                double mx = cv.X + Math.Sin(amid) * 8.0, mz = cv.Z + Math.Cos(amid) * 8.0;
                bool outerEdge = SegDistance(segs[cv.SegA], mx, mz, out _) >= 7.3
                    && SegDistance(segs[cv.SegB], mx, mz, out _) >= 7.3;

                if (onLand)
                {
                    V(cv.X + s0 * 8.0, RoadY, cv.Z + c0 * 8.0, shoulder);
                    V(cv.X + s0 * 10.2, -0.04f, cv.Z + c0 * 10.2, shoulder);
                    V(cv.X + s1 * 10.2, -0.04f, cv.Z + c1 * 10.2, shoulder);
                    V(cv.X + s1 * 8.0, RoadY, cv.Z + c1 * 8.0, shoulder);
                }
                else if (outerEdge)
                {
                    V(cv.X + s0 * 8.0, deep, cv.Z + c0 * 8.0, cvGirder);
                    V(cv.X + s0 * 8.0, RoadY, cv.Z + c0 * 8.0, cvGirder);
                    V(cv.X + s1 * 8.0, RoadY, cv.Z + c1 * 8.0, cvGirder);
                    V(cv.X + s1 * 8.0, deep, cv.Z + c1 * 8.0, cvGirder);

                    V(cv.X + s0 * 8.0, RoadY, cv.Z + c0 * 8.0, rail);
                    V(cv.X + s0 * 8.0, RoadY + 1.15f, cv.Z + c0 * 8.0, rail);
                    V(cv.X + s1 * 8.0, RoadY + 1.15f, cv.Z + c1 * 8.0, rail);
                    V(cv.X + s1 * 8.0, RoadY, cv.Z + c1 * 8.0, rail);
                }
                if (outerEdge)
                {
                    V(cv.X + s0 * 7.55, RoadY + 0.032f, cv.Z + c0 * 7.55, white);
                    V(cv.X + s0 * 7.80, RoadY + 0.032f, cv.Z + c0 * 7.80, white);
                    V(cv.X + s1 * 7.80, RoadY + 0.032f, cv.Z + c1 * 7.80, white);
                    V(cv.X + s1 * 7.55, RoadY + 0.032f, cv.Z + c1 * 7.55, white);
                }
                V(cv.X, RoadY + 0.003f, cv.Z, asphalt);
                V(cv.X + s0 * 8.0, RoadY + 0.003f, cv.Z + c0 * 8.0, asphalt);
                V(cv.X + s1 * 8.0, RoadY + 0.003f, cv.Z + c1 * 8.0, asphalt);
                V(cv.X, RoadY + 0.003f, cv.Z, asphalt);

                V(cv.X, RoadY + 0.042f, cv.Z, median);
                V(cv.X + s0 * 0.55, RoadY + 0.042f, cv.Z + c0 * 0.55, median);
                V(cv.X + s1 * 0.55, RoadY + 0.042f, cv.Z + c1 * 0.55, median);
                V(cv.X, RoadY + 0.042f, cv.Z, median);
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

                    // 3スパン（270m）ごとに吊り橋・斜張橋の主塔を建てる（海上橋のみ）
                    if (k % 3 == 0 && s.Road.Type != RoadType.Expressway)
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

    /// <summary>交差点（角切り歩道・誘導標示・横断歩道・停止線・矢印・信号機・青看）を描画する。</summary>
    private void DrawJunctions(float time)
    {
        var asphalt = Rgb(68, 70, 76);
        var curb = Rgb(134, 136, 142);
        var walkCol = Rgb(172, 174, 172);
        var shoulder = Rgb(126, 128, 132);
        var white = Rgb(238, 238, 238);
        var amber = Rgb(245, 170, 28);
        var islandRing = Rgb(235, 195, 55);
        var islandTop = Rgb(175, 178, 182);
        var islandGrass = Rgb(78, 138, 68);

        void QuadBar(double x0, double z0, double x1, double z1, double halfW, float y, Color c)
        {
            double ldx = x1 - x0, ldz = z1 - z0;
            double l = Math.Sqrt(ldx * ldx + ldz * ldz);
            if (l < 1e-4) return;
            double px = -ldz / l * halfW, pz = ldx / l * halfW;
            V(x0 + px, y, z0 + pz, c);
            V(x0 - px, y, z0 - pz, c);
            V(x1 - px, y, z1 - pz, c);
            V(x1 + px, y, z1 + pz, c);
        }

        void DrawPavementArrow(double cx, double cz, double dx, double dz, double nx, double nz, int turnDir)
        {
            // dx,dz は交差点から外向き。交差点へ向かう向きは (-dx, -dz)
            double ix = -dx, iz = -dz;
            float ay = RoadY + 0.036f;
            QuadBar(cx - ix * 1.9, cz - iz * 1.9, cx + ix * 1.5, cz + iz * 1.5, 0.21, ay, white);
            double tx = cx + ix * 2.05, tz = cz + iz * 2.05;
            QuadBar(tx, tz, cx + ix * 0.95 + nx * 0.65, cz + iz * 0.95 + nz * 0.65, 0.16, ay, white);
            QuadBar(tx, tz, cx + ix * 0.95 - nx * 0.65, cz + iz * 0.95 - nz * 0.65, 0.16, ay, white);

            double bx = cx - ix * 0.35, bz = cz - iz * 0.35;
            double ex = cx + ix * 0.75 + nx * (turnDir * 1.25), ez = cz + iz * 0.75 + nz * (turnDir * 1.25);
            QuadBar(bx, bz, ex, ez, 0.18, ay, white);
            QuadBar(ex + ix * 0.2 + nx * (turnDir * 0.2), ez + iz * 0.2 + nz * (turnDir * 0.2),
                    cx + ix * 0.15 + nx * (turnDir * 0.95), cz + iz * 0.15 + nz * (turnDir * 0.95), 0.14, ay, white);
            QuadBar(ex + ix * 0.2 + nx * (turnDir * 0.2), ez + iz * 0.2 + nz * (turnDir * 0.2),
                    cx + ix * 0.95 + nx * (turnDir * 0.45), cz + iz * 0.95 + nz * (turnDir * 0.45), 0.14, ay, white);
        }

        void DrawDiamondMark(double cx, double cz, double axX, double axZ, double rxX, double rxZ, double halfL, double halfW, double barW, float y)
        {
            double px0 = cx - axX * halfL, pz0 = cz - axZ * halfL;
            double px1 = cx + rxX * halfW, pz1 = cz + rxZ * halfW;
            double px2 = cx + axX * halfL, pz2 = cz + axZ * halfL;
            double px3 = cx - rxX * halfW, pz3 = cz - rxZ * halfW;
            QuadBar(px0, pz0, px1, pz1, barW, y, white);
            QuadBar(px1, pz1, px2, pz2, barW, y, white);
            QuadBar(px2, pz2, px3, pz3, barW, y, white);
            QuadBar(px3, pz3, px0, pz0, barW, y, white);
        }

        Rlgl.Begin(DrawMode.Quads);
        foreach (var j in junctions)
        {
            double dist2 = (j.X - PX) * (j.X - PX) + (j.Z - PZ) * (j.Z - PZ);
            double maxR = ViewRadius + j.Radius + 80;
            if (dist2 > maxR * maxR) continue;

            const int slices = 32;
            float r = j.Radius;
            float rWalk = r + 2.8f;
            float rOut = r + 4.4f;

            // 円形の交差点アスファルト広場と外周の角歩道・縁石・白線
            for (int i = 0; i < slices; i++)
            {
                double a0 = i * Math.PI * 2 / slices;
                double a1 = (i + 1) * Math.PI * 2 / slices;
                double amid = (a0 + a1) * 0.5;
                double s0 = Math.Sin(a0), c0 = Math.Cos(a0);
                double s1 = Math.Sin(a1), c1 = Math.Cos(a1);

                // アスファルト円盤
                V(j.X, RoadY + 0.005f, j.Z, asphalt);
                V(j.X + s0 * r, RoadY + 0.005f, j.Z + c0 * r, asphalt);
                V(j.X + s1 * r, RoadY + 0.005f, j.Z + c1 * r, asphalt);
                V(j.X, RoadY + 0.005f, j.Z, asphalt);

                // 道路が接続していない外周部分には白線・立ち上げ縁石・歩道・法面を描く
                double mx = j.X + Math.Sin(amid) * r, mz = j.Z + Math.Cos(amid) * r;
                if (IsEdgeClearOfOtherArms(j, -1, mx, mz, 9.5))
                {
                    V(j.X + s0 * (r - 0.55), RoadY + 0.032f, j.Z + c0 * (r - 0.55), white);
                    V(j.X + s0 * (r - 0.25), RoadY + 0.032f, j.Z + c0 * (r - 0.25), white);
                    V(j.X + s1 * (r - 0.25), RoadY + 0.032f, j.Z + c1 * (r - 0.25), white);
                    V(j.X + s1 * (r - 0.55), RoadY + 0.032f, j.Z + c1 * (r - 0.55), white);

                    // 縁石の立面（道路面→歩道高さ）
                    V(j.X + s0 * r, RoadY, j.Z + c0 * r, curb);
                    V(j.X + s0 * r, RoadY + 0.14f, j.Z + c0 * r, curb);
                    V(j.X + s1 * r, RoadY + 0.14f, j.Z + c1 * r, curb);
                    V(j.X + s1 * r, RoadY, j.Z + c1 * r, curb);

                    // 歩道エプロン上面
                    V(j.X + s0 * r, RoadY + 0.14f, j.Z + c0 * r, walkCol);
                    V(j.X + s0 * rWalk, RoadY + 0.14f, j.Z + c0 * rWalk, walkCol);
                    V(j.X + s1 * rWalk, RoadY + 0.14f, j.Z + c1 * rWalk, walkCol);
                    V(j.X + s1 * r, RoadY + 0.14f, j.Z + c1 * r, walkCol);

                    // 外側法面
                    float outerY = IsLand(mx, mz) ? -0.04f : SeaY;
                    V(j.X + s0 * rWalk, RoadY + 0.14f, j.Z + c0 * rWalk, shoulder);
                    V(j.X + s0 * rOut, outerY, j.Z + c0 * rOut, shoulder);
                    V(j.X + s1 * rOut, outerY, j.Z + c1 * rOut, shoulder);
                    V(j.X + s1 * rWalk, RoadY + 0.14f, j.Z + c1 * rWalk, shoulder);
                }
            }

            // 鋭角〜中角で合流する隣接アーム間のV字ゴア領域をアスファルトで隙間なく埋める
            if (j.Arms.Count >= 2)
            {
                for (int k = 0; k < j.Arms.Count; k++)
                {
                    var a0 = j.Arms[k];
                    var a1 = j.Arms[(k + 1) % j.Arms.Count];
                    double diff = AngleDiff(a1.Angle, a0.Angle);
                    if (diff > 0.05 && diff < 1.35)
                    {
                        double goreDist = Math.Min(52.0, Math.Max(a0.Trim, a1.Trim));
                        if (goreDist > r - 1.0)
                        {
                            double p0x = j.X + a0.OutDx * goreDist, p0z = j.Z + a0.OutDz * goreDist;
                            double p1x = j.X + a1.OutDx * goreDist, p1z = j.Z + a1.OutDz * goreDist;
                            V(j.X, RoadY + 0.002f, j.Z, asphalt);
                            V(p0x, RoadY + 0.002f, p0z, asphalt);
                            V(p1x, RoadY + 0.002f, p1z, asphalt);
                            V(j.X, RoadY + 0.002f, j.Z, asphalt);
                        }
                    }
                }
            }

            if (j.Arms.Count == 1 && dist2 < 900 * 900)
            {
                // 終点（1方向のみ）は転回用の植栽ロータリーにする
                float ri = 3.4f;
                const int islices = 16;
                for (int i = 0; i < islices; i++)
                {
                    double a0 = i * Math.PI * 2 / islices, a1 = (i + 1) * Math.PI * 2 / islices;
                    double s0 = Math.Sin(a0), c0 = Math.Cos(a0), s1 = Math.Sin(a1), c1 = Math.Cos(a1);
                    V(j.X + s0 * ri, RoadY + 0.035f, j.Z + c0 * ri, islandRing);
                    V(j.X + s0 * (ri + 0.4), RoadY + 0.035f, j.Z + c0 * (ri + 0.4), islandRing);
                    V(j.X + s1 * (ri + 0.4), RoadY + 0.035f, j.Z + c1 * (ri + 0.4), islandRing);
                    V(j.X + s1 * ri, RoadY + 0.035f, j.Z + c1 * ri, islandRing);

                    V(j.X, RoadY + 0.16f, j.Z, islandTop);
                    V(j.X + s0 * ri, RoadY + 0.16f, j.Z + c0 * ri, islandTop);
                    V(j.X + s1 * ri, RoadY + 0.16f, j.Z + c1 * ri, islandTop);
                    V(j.X, RoadY + 0.16f, j.Z, islandTop);

                    V(j.X, RoadY + 0.19f, j.Z, islandGrass);
                    V(j.X + s0 * (ri - 0.6f), RoadY + 0.19f, j.Z + c0 * (ri - 0.6f), islandGrass);
                    V(j.X + s1 * (ri - 0.6f), RoadY + 0.19f, j.Z + c1 * (ri - 0.6f), islandGrass);
                    V(j.X, RoadY + 0.19f, j.Z, islandGrass);
                }
            }
            else if (j.Arms.Count >= 2 && dist2 < 900 * 900)
            {
                // 2方向以上の交差点は平坦な路面にして、中央ひし形マーク＋誘導破線を描く
                DrawDiamondMark(j.X, j.Z, 0, 1, 1, 0, 2.5, 2.5, 0.18, RoadY + 0.036f);

                // ほぼ直進で対向する道路同士をセンター破線で誘導する
                for (int a = 0; a < j.Arms.Count; a++)
                {
                    for (int b = a + 1; b < j.Arms.Count; b++)
                    {
                        if (AngleDiff(j.Arms[a].Angle, j.Arms[b].Angle) < 2.55) continue;
                        foreach (var arm in new[] { j.Arms[a], j.Arms[b] })
                        {
                            for (int dStep = 0; dStep < 2; dStep++)
                            {
                                double g0 = 5.0 + dStep * 6.5, g1 = g0 + 3.4;
                                if (g1 > arm.Trim - 0.5) continue;
                                QuadBar(j.X + arm.OutDx * g0, j.Z + arm.OutDz * g0,
                                        j.X + arm.OutDx * g1, j.Z + arm.OutDz * g1, 0.11, RoadY + 0.036f, white);
                            }
                        }
                    }
                }

                // 隣接する角同士を結ぶ左折ガイド破線
                if (j.Arms.Count >= 3)
                {
                    for (int k = 0; k < j.Arms.Count; k++)
                    {
                        var a0 = j.Arms[k];
                        var a1 = j.Arms[(k + 1) % j.Arms.Count];
                        double diff = AngleDiff(a1.Angle, a0.Angle);
                        if (diff < 0.9 || diff > 2.2) continue;
                        double turnR = r * 0.62;
                        for (int d = 0; d < 3; d++)
                        {
                            double t0 = 0.20 + d * 0.25, t1 = t0 + 0.12;
                            double ang0 = a0.Angle + diff * t0, ang1 = a0.Angle + diff * t1;
                            QuadBar(j.X + Math.Sin(ang0) * turnR, j.Z + Math.Cos(ang0) * turnR,
                                    j.X + Math.Sin(ang1) * turnR, j.Z + Math.Cos(ang1) * turnR, 0.12, RoadY + 0.036f, white);
                        }
                    }
                }
            }

            // 各接続道路の入口：横断歩道・左側停止線・オレンジ中央線・進路変更禁止白線・進行矢印・◇マーク
            if (dist2 < 700 * 700 && j.Arms.Count >= 2)
            {
                foreach (var arm in j.Arms)
                {
                    var seg = segs[arm.SegIndex];
                    if (arm.Trim + 7 > seg.Len * 0.45) continue;
                    double dx = arm.OutDx, dz = arm.OutDz;
                    double nx = -dz, nz = dx;
                    double baseDist = arm.Trim + 0.4;
                    double bx = j.X + dx * baseDist, bz = j.Z + dz * baseDist;

                    void ArmStrip(double d0, double d1, double w0, double w1, float y, Color c)
                    {
                        V(bx + dx * d0 + nx * w0, y, bz + dz * d0 + nz * w0, c);
                        V(bx + dx * d0 + nx * w1, y, bz + dz * d0 + nz * w1, c);
                        V(bx + dx * d1 + nx * w1, y, bz + dz * d1 + nz * w1, c);
                        V(bx + dx * d1 + nx * w0, y, bz + dz * d1 + nz * w0, c);
                    }

                    // 横断歩道（縞模様）
                    for (int s = -3; s <= 3; s++)
                        ArmStrip(0.5, 3.8, s * 1.9 - 0.43, s * 1.9 + 0.43, RoadY + 0.036f, white);

                    // 停止線（交差点へ進入する左側車線：-nx 側 -7.55m〜-0.35m）
                    ArmStrip(4.6, 5.2, -7.55, -0.35, RoadY + 0.036f, white);

                    double availOut = seg.Len * 0.44 - baseDist;
                    // 中央線のオレンジ二重実線（停止線直前まで伸ばし、分離帯と接続）
                    if (availOut > 7.0)
                    {
                        double centerEnd = Math.Min(availOut, 15.0);
                        ArmStrip(5.2, centerEnd, -0.34, -0.10, RoadY + 0.038f, amber);
                        ArmStrip(5.2, centerEnd, 0.10, 0.34, RoadY + 0.038f, amber);
                    }

                    // 進入側の進路変更禁止（白実線 -4.1〜-3.9）
                    if (availOut > 9.0)
                    {
                        double solidEnd = Math.Min(availOut, 26.0);
                        ArmStrip(5.2, solidEnd, -4.10, -3.90, RoadY + 0.038f, white);
                    }

                    // 進行方向別通行区分矢印（直進+右折 / 直進+左折）
                    if (availOut > 15.5)
                    {
                        double arrowDist = baseDist + 11.2;
                        DrawPavementArrow(j.X + dx * arrowDist - nx * 2.2, j.Z + dz * arrowDist - nz * 2.2, dx, dz, nx, nz, +1);
                        DrawPavementArrow(j.X + dx * arrowDist - nx * 5.8, j.Z + dz * arrowDist - nz * 5.8, dx, dz, nx, nz, -1);
                    }

                    // 横断歩道予告の白ひし形（◇）マーク
                    if (availOut > 25.0)
                    {
                        double diaDist = baseDist + 21.5;
                        DrawDiamondMark(j.X + dx * diaDist - nx * 2.2, j.Z + dz * diaDist - nz * 2.2, dx, dz, nx, nz, 1.85, 0.72, 0.14, RoadY + 0.036f);
                        DrawDiamondMark(j.X + dx * diaDist - nx * 5.8, j.Z + dz * diaDist - nz * 5.8, dx, dz, nx, nz, 1.85, 0.72, 0.14, RoadY + 0.036f);
                    }
                }
            }
        }
        Rlgl.End();

        // 交差点の3Dオブジェクト（信号機・交差点名板・歩行者信号・青看・ガードパイプ）
        if (Altitude > 300) return;
        foreach (var j in junctions)
        {
            double dist2 = (j.X - PX) * (j.X - PX) + (j.Z - PZ) * (j.Z - PZ);
            if (dist2 > 420 * 420 || j.Arms.Count < 2) continue;

            // 外周歩道上の白い歩行者用ガードパイプ
            if (dist2 < 240 * 240 && Altitude < 200)
            {
                const int gSlices = 32;
                double rGuard = j.Radius + 0.6;
                for (int i = 0; i < gSlices; i++)
                {
                    double a0 = i * Math.PI * 2 / gSlices, a1 = (i + 1) * Math.PI * 2 / gSlices;
                    double amid = (a0 + a1) * 0.5;
                    double mx = j.X + Math.Sin(amid) * rGuard, mz = j.Z + Math.Cos(amid) * rGuard;
                    if (!IsEdgeClearOfOtherArms(j, -1, mx, mz, 10.8)) continue;
                    double x0 = j.X + Math.Sin(a0) * rGuard, z0 = j.Z + Math.Cos(a0) * rGuard;
                    double x1 = j.X + Math.Sin(a1) * rGuard, z1 = j.Z + Math.Cos(a1) * rGuard;
                    float gd = Vector3.Distance(R(mx, RoadY, mz), cam);
                    var railCol = scene.FogC(Rgb(235, 238, 240), gd);

                    Raylib.DrawCylinder(R(x0, RoadY + 0.14f, z0), 0.05f, 0.05f, 0.85f, 5, railCol);
                    double segLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                    float segYaw = -(float)(Math.Atan2(x1 - x0, z1 - z0) * 180.0 / Math.PI);
                    var barMid = R((x0 + x1) * 0.5, RoadY + 0.90f, (z0 + z1) * 0.5);
                    Rlgl.PushMatrix();
                    Rlgl.Translatef(barMid.X, barMid.Y, barMid.Z);
                    Rlgl.Rotatef(segYaw, 0, 1, 0);
                    Raylib.DrawCube(Vector3.Zero, 0.07f, 0.08f, (float)segLen + 0.05f, railCol);
                    Raylib.DrawCube(new Vector3(0, -0.35f, 0), 0.06f, 0.06f, (float)segLen + 0.05f, railCol);
                    Rlgl.PopMatrix();
                }
            }

            double axis0 = j.Arms[0].Angle;
            for (int ai = 0; ai < j.Arms.Count; ai++)
            {
                var arm = j.Arms[ai];
                var seg = segs[arm.SegIndex];
                if (arm.Trim + 7 > seg.Len * 0.45) continue;
                double dx = arm.OutDx, dz = arm.OutDz;
                double nx = -dz, nz = dx;

                // 左側進入車線（-nx側）の歩道上に信号機を立てる（干渉時は右側へ退避）
                double side = -1;
                double px = j.X + dx * (arm.Trim + 0.9) - nx * 8.8;
                double pz = j.Z + dz * (arm.Trim + 0.9) - nz * 8.8;
                if (!IsEdgeClearOfOtherArms(j, arm.SegIndex, px, pz, 9.5))
                {
                    side = 1;
                    px = j.X + dx * (arm.Trim + 0.9) + nx * 8.8;
                    pz = j.Z + dz * (arm.Trim + 0.9) + nz * 8.8;
                    if (!IsEdgeClearOfOtherArms(j, arm.SegIndex, px, pz, 9.5)) continue;
                }
                var polePos = R(px, RoadY, pz);
                float d = Vector3.Distance(polePos, cam);
                if (d > 400) continue;

                // 向き別フェーズ（全赤 0.5s を含む 18s サイクル）
                int phase = Math.Abs(Math.Cos(arm.Angle - axis0)) >= 0.58 ? 0 : 1;
                float cycle = (time + phase * 9f) % 18f;
                int sigIdx = cycle < 7.0f ? 0 : cycle < 8.5f ? 1 : 2; // 0:青 1:黄 2:赤
                bool pedGreen = phase == 0 ? cycle < 6.0f : cycle >= 9.0f && cycle < 15.0f;

                DrawTrafficSignal3D(px, pz, RoadY, dx, dz, 4.6f, side < 0 ? 1f : -1f, 6.2f, 1.0f, sigIdx, pedGreen, true, d);

                // 手前アプローチの青い方面案内標識（青看）
                if (j.Arms.Count >= 3 && d < 320 && arm.Trim + 20.5 < seg.Len * 0.44)
                {
                    double bx = j.X + dx * (arm.Trim + 17.5) - nx * 8.9;
                    double bz = j.Z + dz * (arm.Trim + 17.5) - nz * 8.9;
                    if (IsEdgeClearOfOtherArms(j, arm.SegIndex, bx, bz, 10.5))
                    {
                        var signBase = R(bx, RoadY, bz);
                        float bd = Vector3.Distance(signBase, cam);
                        float yaw = -(float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                        var gPoleCol = scene.FogC(Rgb(165, 170, 176), bd);
                        var blueCol = scene.FogC(Rgb(12, 85, 175), bd);
                        var wCol = scene.FogC(Rgb(242, 244, 248), bd);

                        Rlgl.PushMatrix();
                        Rlgl.Translatef(signBase.X, signBase.Y, signBase.Z);
                        Rlgl.Rotatef(yaw, 0, 1, 0);
                        Raylib.DrawCylinder(Vector3.Zero, 0.13f, 0.16f, 6.2f, 6, gPoleCol);
                        Raylib.DrawCube(new Vector3(2.2f, 5.45f, 0), 4.5f, 0.16f, 0.16f, gPoleCol);
                        // 看板本体（背面はアルミグレー、前面は白い縁取り＋青い板面＋白い十字/直進矢印意匠）
                        Raylib.DrawCube(new Vector3(3.45f, 5.20f, -0.02f), 3.08f, 1.95f, 0.06f, scene.FogC(Rgb(132, 138, 146), bd));
                        Raylib.DrawCube(new Vector3(3.45f, 5.20f, 0.03f), 3.02f, 1.88f, 0.04f, wCol);
                        Raylib.DrawCube(new Vector3(3.45f, 5.20f, 0.06f), 2.84f, 1.72f, 0.04f, blueCol);
                        Raylib.DrawCube(new Vector3(3.45f, 5.20f, 0.09f), 0.22f, 1.15f, 0.03f, wCol);
                        Raylib.DrawCube(new Vector3(3.45f, 5.10f, 0.09f), 1.65f, 0.20f, 0.03f, wCol);
                        Raylib.DrawCube(new Vector3(3.45f, 5.66f, 0.09f), 0.72f, 0.16f, 0.03f, wCol);
                        Raylib.DrawCube(new Vector3(3.45f, 5.81f, 0.09f), 0.36f, 0.16f, 0.03f, wCol);
                        Rlgl.PopMatrix();
                    }
                }
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
                // 道路・路肩・交差点から25m以内、および水辺のスロープには木を生やさない
                if (!IsLand(x, z) || BaseGroundY(x, z) < -0.02f || NearRoadDistance(x, z) < 25) continue;
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

    // ---------------- 建物デザインパレット・共通幾何ヘルパー ----------------

    private static readonly Color[] HouseWalls =
    {
        Rgb(238, 230, 214), Rgb(242, 240, 234), Rgb(216, 220, 224),
        Rgb(206, 184, 154), Rgb(202, 214, 222), Rgb(108, 96, 86),
    };
    private static readonly Color[] HouseRoofs =
    {
        Rgb(44, 54, 74), Rgb(50, 52, 58), Rgb(106, 62, 46), Rgb(50, 76, 60), Rgb(128, 60, 46),
    };
    private static readonly Color[] ShopBands =
    {
        Rgb(28, 142, 86), Rgb(34, 108, 196), Rgb(218, 96, 36),
        Rgb(178, 46, 56), Rgb(120, 76, 46), Rgb(28, 128, 138),
    };
    private static readonly Color[] AptWalls =
    {
        Rgb(232, 226, 214), Rgb(206, 212, 218), Rgb(224, 204, 194), Rgb(184, 134, 114), Rgb(218, 222, 228),
    };
    private static readonly Color[] CommWalls =
    {
        Rgb(178, 184, 192), Rgb(164, 146, 134), Rgb(124, 130, 140), Rgb(214, 218, 224), Rgb(172, 130, 116),
    };
    private static readonly Color[] SkyWalls =
    {
        Rgb(166, 180, 194), Rgb(112, 132, 152), Rgb(84, 94, 110), Rgb(186, 192, 200), Rgb(176, 166, 150),
    };
    private static readonly Color[] SkyGlass =
    {
        Rgb(54, 96, 136), Rgb(46, 102, 108), Rgb(40, 54, 76), Rgb(68, 104, 134), Rgb(62, 82, 102),
    };
    private static readonly Color[] WareWalls =
    {
        Rgb(156, 164, 174), Rgb(178, 174, 166), Rgb(134, 144, 154),
    };

    private static float SideShade(int side) => side switch { 0 => 0.92f, 1 => 0.83f, 2 => 0.74f, _ => 1.00f };

    private static void SideBasis(int side, float hx, float hz,
        out float nx, out float nz, out float tx, out float tz, out float depth, out float span)
    {
        switch (side)
        {
            case 0: nx = 1; nz = 0; tx = 0; tz = 1; depth = hx; span = hz; break;   // 東 +X
            case 1: nx = -1; nz = 0; tx = 0; tz = -1; depth = hx; span = hz; break; // 西 -X
            case 2: nx = 0; nz = 1; tx = -1; tz = 0; depth = hz; span = hx; break;  // 北 +Z
            default: nx = 0; nz = -1; tx = 1; tz = 0; depth = hz; span = hx; break; // 南 -Z
        }
    }

    private static Color ShadeColor(Color c, float k) => new(
        (byte)Math.Clamp((int)(c.R * k), 0, 255),
        (byte)Math.Clamp((int)(c.G * k), 0, 255),
        (byte)Math.Clamp((int)(c.B * k), 0, 255),
        c.A);

    private static float CellHash(int seed, int a, int b, int c)
        => Scene.Hash(seed * 73856093L + a * 19349663L + b * 83492791L + c * 2654435761L);

    private void VQ(double x, float y, double z, Color c, bool lit = false)
    {
        var p = R(x, y, z);
        var col = scene.FogC(c, Vector3.Distance(p, cam), lit: lit);
        Rlgl.Color4ub(col.R, col.G, col.B, col.A);
        Rlgl.Vertex3f(p.X, p.Y, p.Z);
    }

    private void PushTri(double x0, float y0, double z0, double x1, float y1, double z1, double x2, float y2, double z2,
        float outX, float outY, float outZ, Color c, bool lit = false)
    {
        var p0 = R(x0, y0, z0);
        var p1 = R(x1, y1, z1);
        var p2 = R(x2, y2, z2);
        var col = scene.FogC(c, Vector3.Distance((p0 + p1 + p2) / 3f, cam), lit: lit);
        // 描画空間（-X反転）で外向き法線に対して必ず反時計回りになるよう補正
        var nDraw = Vector3.Cross(p1 - p0, p2 - p0);
        var outDraw = new Vector3(-outX, outY, outZ);
        if (Vector3.Dot(nDraw, outDraw) < 0) (p1, p2) = (p2, p1);
        roofTris.Add(new RoofTri(p0, p1, p2, col, col, col));
    }

    private void PushSlopedQuad(double x0, float y0, double z0, double x1, float y1, double z1,
        double x2, float y2, double z2, double x3, float y3, double z3,
        float outX, float outY, float outZ, Color c)
    {
        PushTri(x0, y0, z0, x1, y1, z1, x2, y2, z2, outX, outY, outZ, c);
        PushTri(x0, y0, z0, x2, y2, z2, x3, y3, z3, outX, outY, outZ, c);
    }

    /// <summary>面別の疑似陰影（北南東西＋屋根）付き直方体を Quads バッチに積む。</summary>
    private void AddBuildingBox(double cx, double cz, float y0, float y1, float hx, float hz,
        Color wallCol, Color roofCol, ref int quads, bool drawTop = true, bool lit = false)
    {
        if (y1 <= y0 || hx <= 0.01f || hz <= 0.01f) return;
        double x0 = cx - hx, x1 = cx + hx, z0 = cz - hz, z1 = cz + hz;
        if (drawTop)
        {
            var tc = lit ? roofCol : ShadeColor(roofCol, 1.07f);
            VQ(x0, y1, z0, tc, lit); VQ(x1, y1, z0, tc, lit); VQ(x1, y1, z1, tc, lit); VQ(x0, y1, z1, tc, lit);
            quads++;
        }
        var cs = lit ? wallCol : ShadeColor(wallCol, SideShade(3));
        VQ(x0, y0, z0, cs, lit); VQ(x1, y0, z0, cs, lit); VQ(x1, y1, z0, cs, lit); VQ(x0, y1, z0, cs, lit);
        var cn = lit ? wallCol : ShadeColor(wallCol, SideShade(2));
        VQ(x1, y0, z1, cn, lit); VQ(x0, y0, z1, cn, lit); VQ(x0, y1, z1, cn, lit); VQ(x1, y1, z1, cn, lit);
        var ce = lit ? wallCol : ShadeColor(wallCol, SideShade(0));
        VQ(x1, y0, z0, ce, lit); VQ(x1, y0, z1, ce, lit); VQ(x1, y1, z1, ce, lit); VQ(x1, y1, z0, ce, lit);
        var cw = lit ? wallCol : ShadeColor(wallCol, SideShade(1));
        VQ(x0, y0, z1, cw, lit); VQ(x0, y0, z0, cw, lit); VQ(x0, y1, z0, cw, lit); VQ(x0, y1, z1, cw, lit);
        quads += 4;
    }

    /// <summary>建物の指定面（side:0..3）上の座標 (u0..u1, y0..y1) に外向きクワッドを配置する。</summary>
    private void AddFaceQuad(double cx, double cz, float hx, float hz, int side,
        float u0, float u1, float y0, float y1, float outOffset, Color c, ref int quads, bool lit = false)
    {
        if (u1 <= u0 || y1 <= y0) return;
        SideBasis(side, hx, hz, out float nx, out float nz, out float tx, out float tz, out float depth, out _);
        float dist = depth + outOffset;
        double ax = cx + nx * dist + tx * u0, az = cz + nz * dist + tz * u0;
        double bx = cx + nx * dist + tx * u1, bz = cz + nz * dist + tz * u1;
        var col = lit ? c : ShadeColor(c, SideShade(side));
        VQ(ax, y0, az, col, lit);
        VQ(bx, y0, bz, col, lit);
        VQ(bx, y1, bz, col, lit);
        VQ(ax, y1, az, col, lit);
        quads++;
    }

    /// <summary>壁面から外へ突き出た立体パーツ（ベランダ・庇・袖看板）を積む。</summary>
    private void AddFaceProtrusion(double cx, double cz, float hx, float hz, int side,
        float u0, float u1, float y0, float y1, float out0, float out1, Color wallCol, Color roofCol, ref int quads, bool lit = false)
    {
        SideBasis(side, hx, hz, out float nx, out float nz, out float tx, out float tz, out float depth, out _);
        double ax = cx + nx * (depth + out0) + tx * u0, az = cz + nz * (depth + out0) + tz * u0;
        double bx = cx + nx * (depth + out1) + tx * u1, bz = cz + nz * (depth + out1) + tz * u1;
        double xMin = Math.Min(ax, bx), xMax = Math.Max(ax, bx);
        double zMin = Math.Min(az, bz), zMax = Math.Max(az, bz);
        AddBuildingBox((xMin + xMax) * 0.5, (zMin + zMax) * 0.5, y0, y1,
            (float)((xMax - xMin) * 0.5), (float)((zMax - zMin) * 0.5), wallCol, roofCol, ref quads, drawTop: true, lit: lit);
    }

    private void DrawCities(float time)
    {
        roofTris.Clear();
        roofBeacons.Clear();
        roofSpires.Clear();

        bool night = scene.Night;
        int quads = 0;

        Rlgl.Begin(DrawMode.Quads);
        foreach (var place in Places)
        {
            double range = Math.Max(1800, ViewRadius) + place.Radius;
            if ((place.X - PX) * (place.X - PX) + (place.Z - PZ) * (place.Z - PZ) > range * range) continue;
            foreach (var b in Blocks(place))
            {
                var p = R(b.X, b.Destroyed ? b.BaseY + 1f : b.BaseY + b.H * 0.5f, b.Z);
                float d = Vector3.Distance(p, cam);
                if (d > Math.Max(900, ViewRadius * 0.85) || (d > 2500 && b.H < 30)) continue;

                if (b.Destroyed)
                    DrawRubble(b, d, ref quads);
                else
                    DrawBuilding(b, d, night, ref quads);

                if (quads >= 950)
                {
                    Rlgl.End();
                    Rlgl.Begin(DrawMode.Quads);
                    quads = 0;
                }
            }
        }
        Rlgl.End();

        // 切妻・寄棟屋根の三角形バッチ
        if (roofTris.Count > 0)
        {
            Rlgl.Begin(DrawMode.Triangles);
            for (int i = 0; i < roofTris.Count; i++)
            {
                var t = roofTris[i];
                Rlgl.Color4ub(t.C0.R, t.C0.G, t.C0.B, t.C0.A);
                Rlgl.Vertex3f(t.P0.X, t.P0.Y, t.P0.Z);
                Rlgl.Color4ub(t.C1.R, t.C1.G, t.C1.B, t.C1.A);
                Rlgl.Vertex3f(t.P1.X, t.P1.Y, t.P1.Z);
                Rlgl.Color4ub(t.C2.R, t.C2.G, t.C2.B, t.C2.A);
                Rlgl.Vertex3f(t.P2.X, t.P2.Y, t.P2.Z);
                if ((i & 511) == 511)
                {
                    Rlgl.End();
                    Rlgl.Begin(DrawMode.Triangles);
                }
            }
            Rlgl.End();
        }

        // 高層ビル屋上の避雷針アンテナ・点滅航空障害灯
        foreach (var sp in roofSpires)
        {
            var col = scene.FogC(Rgb(198, 204, 212), sp.Dist);
            Raylib.DrawLine3D(R(sp.X, sp.Y0, sp.Z), R(sp.X, sp.Y1, sp.Z), col);
        }
        foreach (var bc in roofBeacons)
        {
            bool on = ((int)(time * 1.4f) & 1) == (bc.Phase ? 1 : 0);
            var col = on ? scene.FogC(Rgb(255, 42, 42), bc.Dist, lit: true) : scene.FogC(Rgb(85, 28, 28), bc.Dist);
            Raylib.DrawSphere(R(bc.X, bc.Y, bc.Z), on ? 0.42f : 0.28f, col);
        }
    }

    private void DrawRubble(Block b, float d, ref int quads)
    {
        float hx = b.W * 0.48f, hz = b.D * 0.48f;
        float by = b.BaseY;
        var scorch = Rgb(56, 54, 52);
        var conc1 = Rgb(108, 102, 95);
        var conc2 = Rgb(84, 80, 76);

        // 焦げた敷地基礎
        AddBuildingBox(b.X, b.Z, by - 0.3f, by + 0.14f, hx, hz, scorch, scorch, ref quads);
        if (d > 650) return;

        // 崩落したコンクリート塊と壁片
        for (int k = 0; k < 4; k++)
        {
            float ox = (CellHash(b.Seed, k, 1, 0) - 0.5f) * hx * 1.1f;
            float oz = (CellHash(b.Seed, k, 2, 0) - 0.5f) * hz * 1.1f;
            float rw = hx * (0.28f + CellHash(b.Seed, k, 3, 0) * 0.30f);
            float rd = hz * (0.26f + CellHash(b.Seed, k, 4, 0) * 0.30f);
            float rh = 0.8f + CellHash(b.Seed, k, 5, 0) * (b.H > 35 ? 2.4f : 1.3f);
            AddBuildingBox(b.X + ox, b.Z + oz, by + 0.12f, by + 0.12f + rh, rw, rd,
                k % 2 == 0 ? conc1 : conc2, conc1, ref quads);
        }
    }

    private void DrawBuilding(Block b, float d, bool night, ref int quads)
    {
        float hx = b.W * 0.5f, hz = b.D * 0.5f;
        float yBase = b.BaseY;
        float yGround = yBase - 0.5f;

        // 歩道側へはみ出さないコンクリート敷地土間（街区と建物をつなぐ）
        if (d < 340)
        {
            float lotM = b.Kind == BuildingKind.House ? 1.35f : Math.Min(0.55f, Math.Max(0.22f, 13.55f - Math.Max(hx, hz)));
            var lotCol = b.Kind == BuildingKind.House ? Rgb(158, 156, 150) : Rgb(144, 146, 150);
            AddBuildingBox(b.X, b.Z, yGround, yBase + 0.05f, hx + lotM, hz + lotM, Rgb(120, 122, 126), lotCol, ref quads);
        }

        switch (b.Kind)
        {
            case BuildingKind.House:
                DrawHouse(b, d, night, yGround, hx, hz, ref quads);
                break;
            case BuildingKind.ShopLow:
                DrawShopLow(b, d, night, yGround, hx, hz, ref quads);
                break;
            case BuildingKind.Warehouse:
                DrawWarehouse(b, d, night, yGround, hx, hz, ref quads);
                break;
            case BuildingKind.Apartment:
                DrawApartment(b, d, night, yGround, hx, hz, ref quads);
                break;
            case BuildingKind.Commercial:
                DrawCommercial(b, d, night, yGround, hx, hz, ref quads);
                break;
            default:
                DrawSkyscraper(b, d, night, yGround, hx, hz, ref quads);
                break;
        }
    }

    private void DrawHouse(Block b, float d, bool night, float yGround, float hx, float hz, ref int quads)
    {
        var wallCol = HouseWalls[b.Style % HouseWalls.Length];
        var roofCol = HouseRoofs[(b.Style + 2) % HouseRoofs.Length];
        float roofH = Math.Clamp(Math.Min(b.W, b.D) * 0.23f, 1.7f, 3.0f);
        float yEave = b.TopY - roofH;

        AddBuildingBox(b.X, b.Z, yGround, yEave - 0.14f, hx, hz, wallCol, roofCol, ref quads, drawTop: false);

        // 切妻／寄棟屋根（680m以内は斜め屋根・妻壁をしっかり描画）
        if (d < 680)
        {
            var eaveCol = Rgb(232, 232, 228);
            AddBuildingBox(b.X, b.Z, yEave - 0.14f, yEave, hx + 0.52f, hz + 0.52f, eaveCol, Rgb(70, 72, 76), ref quads, drawTop: true);
            bool hip = (b.Style & 1) == 1;
            if (hip) PushHipRoof(b.X, b.Z, hx, hz, yEave, b.TopY, 0.55f, roofCol, ref quads);
            else PushGableRoof(b.X, b.Z, hx, hz, yEave, b.TopY, 0.55f, wallCol, roofCol, ref quads);
        }
        else
        {
            AddBuildingBox(b.X, b.Z, yEave - 0.14f, b.TopY, hx, hz, wallCol, roofCol, ref quads);
        }

        if (d > 240) return;

        // 1階・2階のサッシ窓・玄関ドア・ポーチ灯
        float eaveRel = yEave - b.BaseY;
        bool twoStory = eaveRel > 5.0f;
        var sashCol = Rgb(228, 232, 236);
        var dayGlass = Rgb(48, 70, 92);
        var offGlass = Rgb(18, 24, 34);
        var doorCol = b.Style % 2 == 0 ? Rgb(105, 62, 38) : Rgb(44, 62, 88);

        // 低い外構ブロック塀・生垣（正面以外）
        if (d < 150)
        {
            var hedgeCol = b.Style % 3 == 0 ? Rgb(62, 108, 56) : Rgb(168, 164, 158);
            for (int side = 0; side < 4; side++)
            {
                if (side == b.FrontDir) continue;
                float span = (side <= 1 ? hz : hx) + 1.15f;
                AddFaceProtrusion(b.X, b.Z, hx, hz, side, -span, span, b.BaseY, b.BaseY + 0.85f, 1.05f, 1.32f, hedgeCol, hedgeCol, ref quads);
            }
        }

        for (int side = 0; side < 4; side++)
        {
            float span = side <= 1 ? hz : hx;
            bool isFront = side == b.FrontDir;
            int cols = isFront ? 2 : (span > 4.6f ? 2 : 1);

            for (int floor = 0; floor < (twoStory ? 2 : 1); floor++)
            {
                float wy0 = b.BaseY + (floor == 0 ? 1.05f : 3.55f);
                float wy1 = Math.Min(yEave - 0.32f, wy0 + 1.25f);
                if (wy1 <= wy0 + 0.3f) continue;

                if (floor == 0 && isFront)
                {
                    // 正面玄関ドア＋玄関庇＋ポーチ灯
                    float dxCenter = -span * 0.34f;
                    AddFaceQuad(b.X, b.Z, hx, hz, side, dxCenter - 0.62f, dxCenter + 0.62f, b.BaseY + 0.05f, b.BaseY + 2.20f, 0.045f, doorCol, ref quads);
                    AddFaceQuad(b.X, b.Z, hx, hz, side, dxCenter - 0.16f, dxCenter + 0.16f, b.BaseY + 0.90f, b.BaseY + 1.85f, 0.06f, night ? Rgb(255, 215, 130) : Rgb(70, 96, 118), ref quads, lit: night);
                    if (d < 130)
                    {
                        AddFaceProtrusion(b.X, b.Z, hx, hz, side, dxCenter - 0.95f, dxCenter + 0.95f, b.BaseY + 2.25f, b.BaseY + 2.48f, 0f, 0.72f, Rgb(86, 92, 102), Rgb(86, 92, 102), ref quads);
                        AddFaceQuad(b.X, b.Z, hx, hz, side, dxCenter + 0.72f, dxCenter + 0.95f, b.BaseY + 1.78f, b.BaseY + 2.02f, 0.07f, night ? Rgb(255, 228, 150) : Rgb(215, 218, 222), ref quads, lit: night);
                    }
                    // 玄関横のリビング窓
                    float wxCenter = span * 0.30f;
                    DrawFramedWindow(b.X, b.Z, hx, hz, side, wxCenter - 0.95f, wxCenter + 0.95f, wy0, wy1,
                        sashCol, dayGlass, offGlass, night, CellHash(b.Seed, floor, side, 1) < 0.68f, d < 120, ref quads);
                }
                else
                {
                    for (int c = 0; c < cols; c++)
                    {
                        float wc = cols == 1 ? 0f : (c == 0 ? -span * 0.42f : span * 0.42f);
                        float wh = Math.Min(1.05f, span * 0.25f);
                        bool winLit = night && CellHash(b.Seed, floor, side, c) < 0.56f;
                        DrawFramedWindow(b.X, b.Z, hx, hz, side, wc - wh, wc + wh, wy0, wy1,
                            sashCol, dayGlass, offGlass, night, winLit, d < 120, ref quads);
                    }
                }
            }
        }
    }

    private void DrawShopLow(Block b, float d, bool night, float yGround, float hx, float hz, ref int quads)
    {
        var wallCol = Rgb(232, 232, 230);
        var roofCol = Rgb(138, 142, 148);
        var bandCol = ShopBands[b.Style % ShopBands.Length];
        float top = b.TopY;

        AddBuildingBox(b.X, b.Z, yGround, top - 0.1f, hx, hz, wallCol, roofCol, ref quads);
        // 店舗上部パラペット看板帯
        float signH = Math.Min(1.35f, b.H * 0.20f);
        AddBuildingBox(b.X, b.Z, top - signH, top, hx + 0.14f, hz + 0.14f, bandCol, Rgb(92, 96, 102), ref quads, lit: night && d < 320);
        if (d > 260) return;

        // 看板帯の中央の白いライン装飾
        for (int side = 0; side < 4; side++)
        {
            float span = side <= 1 ? hz : hx;
            AddFaceQuad(b.X, b.Z, hx + 0.14f, hz + 0.14f, side, -span * 0.85f, span * 0.85f,
                top - signH * 0.62f, top - signH * 0.38f, 0.03f, Rgb(246, 246, 244), ref quads, lit: night);
        }

        // 道路側の1階全面ガラスショーウィンドウ・オーニング庇・袖看板
        int side2 = (b.FrontDir & 2) == 0 ? ((b.Seed & 1) == 0 ? 2 : 3) : ((b.Seed & 1) == 0 ? 0 : 1);
        for (int pass = 0; pass < 2; pass++)
        {
            int side = pass == 0 ? b.FrontDir : side2;
            float span = side <= 1 ? hz : hx;
            var glassCol = night ? Rgb(248, 242, 210) : Rgb(64, 118, 146);
            AddFaceQuad(b.X, b.Z, hx, hz, side, -span * 0.82f, span * 0.82f, b.BaseY + 0.12f, b.BaseY + 3.05f, 0.045f, glassCol, ref quads, lit: night);
            // ガラスサッシ中央扉柱
            AddFaceQuad(b.X, b.Z, hx, hz, side, -0.85f, 0.85f, b.BaseY + 0.12f, b.BaseY + 2.75f, 0.06f, night ? Rgb(255, 250, 225) : Rgb(90, 148, 178), ref quads, lit: night);
            // 店舗オーニング庇
            if (d < 150)
                AddFaceProtrusion(b.X, b.Z, hx, hz, side, -span * 0.86f, span * 0.86f, b.BaseY + 3.05f, b.BaseY + 3.45f, 0f, 0.78f, bandCol, bandCol, ref quads);
            // 突き出し袖看板（角部）
            if (pass == 0 && d < 190)
                AddFaceProtrusion(b.X, b.Z, hx, hz, side, span - 0.45f, span - 0.12f, b.BaseY + 2.8f, top - 0.3f, 0.18f, 0.92f, bandCol, bandCol, ref quads, lit: night);
        }

        // 2階部分があれば上階窓
        if (b.H > 7.4f)
            DrawWindowBands(b, b.X, b.Z, hx, hz, b.BaseY + 4.3f, top - signH - 0.5f, 3.2f, 1.35f, night, 0.45f, Rgb(46, 68, 92), d < 130, ref quads);
    }

    private void DrawWarehouse(Block b, float d, bool night, float yGround, float hx, float hz, ref int quads)
    {
        var wallCol = WareWalls[b.Style % WareWalls.Length];
        var roofCol = Rgb(78, 86, 98);
        float roofH = Math.Clamp(Math.Min(b.W, b.D) * 0.14f, 1.4f, 2.4f);
        float yEave = b.TopY - roofH;

        AddBuildingBox(b.X, b.Z, yGround, yEave - 0.1f, hx, hz, wallCol, roofCol, ref quads, drawTop: d >= 480);
        if (d < 480)
            PushGableRoof(b.X, b.Z, hx, hz, yEave, b.TopY, 0.35f, wallCol, roofCol, ref quads);
        else
            AddBuildingBox(b.X, b.Z, yEave - 0.1f, b.TopY, hx, hz, wallCol, roofCol, ref quads);

        if (d > 260) return;

        // 正面の大型トラックシャッター×2〜3 + 高窓連続帯
        int side = b.FrontDir;
        float span = side <= 1 ? hz : hx;
        int bays = span >= 9.0f ? 3 : 2;
        float bayW = Math.Min(3.4f, (span * 1.6f) / bays - 0.7f);
        var shutCol = Rgb(118, 124, 134);
        var frameCol = Rgb(62, 66, 74);
        for (int k = 0; k < bays; k++)
        {
            float uc = (-0.5f * (bays - 1) + k) * (span * 1.45f / bays);
            AddFaceQuad(b.X, b.Z, hx, hz, side, uc - bayW * 0.5f - 0.12f, uc + bayW * 0.5f + 0.12f, b.BaseY + 0.05f, b.BaseY + 4.35f, 0.035f, frameCol, ref quads);
            AddFaceQuad(b.X, b.Z, hx, hz, side, uc - bayW * 0.5f, uc + bayW * 0.5f, b.BaseY + 0.08f, b.BaseY + 4.15f, 0.055f, shutCol, ref quads);
        }
        // 長い庇と高窓スリット
        if (d < 150)
            AddFaceProtrusion(b.X, b.Z, hx, hz, side, -span * 0.85f, span * 0.85f, b.BaseY + 4.40f, b.BaseY + 4.68f, 0f, 0.85f, Rgb(92, 98, 108), roofCol, ref quads);
        for (int s = 0; s < 4; s++)
        {
            float sp = s <= 1 ? hz : hx;
            AddFaceQuad(b.X, b.Z, hx, hz, s, -sp * 0.82f, sp * 0.82f, yEave - 1.55f, yEave - 0.65f, 0.045f,
                night ? Rgb(220, 232, 245) : Rgb(56, 78, 96), ref quads, lit: night);
        }
    }

    private void DrawApartment(Block b, float d, bool night, float yGround, float hx, float hz, ref int quads)
    {
        var wallCol = AptWalls[b.Style % AptWalls.Length];
        var roofDeck = Rgb(106, 110, 118);
        var railCol = Lerp(wallCol, Rgb(242, 244, 246), 0.45f);
        var top = b.TopY;

        AddBuildingBox(b.X, b.Z, yGround, top - 0.02f, hx, hz, wallCol, roofDeck, ref quads);
        DrawRooftopParapetAndPenthouse(b, hx, hz, top, wallCol, roofDeck, d, hasHelipad: false, ref quads);

        // 1階エントランス庇と暗いピロティ/ガラス入口
        if (d < 240)
        {
            float fSpan = b.FrontDir <= 1 ? hz : hx;
            AddFaceQuad(b.X, b.Z, hx, hz, b.FrontDir, -2.4f, 2.4f, b.BaseY + 0.05f, b.BaseY + 2.85f, 0.04f,
                night ? Rgb(250, 224, 155) : Rgb(48, 72, 92), ref quads, lit: night);
            if (d < 150)
                AddFaceProtrusion(b.X, b.Z, hx, hz, b.FrontDir, -2.8f, 2.8f, b.BaseY + 2.85f, b.BaseY + 3.15f, 0f, 0.82f, Rgb(98, 104, 114), Rgb(98, 104, 114), ref quads);
            AddFaceQuad(b.X, b.Z, hx, hz, b.FrontDir, -fSpan * 0.85f, -3.1f, b.BaseY + 1.0f, b.BaseY + 2.25f, 0.04f, Rgb(44, 64, 86), ref quads);
            AddFaceQuad(b.X, b.Z, hx, hz, b.FrontDir, 3.1f, fSpan * 0.85f, b.BaseY + 1.0f, b.BaseY + 2.25f, 0.04f, Rgb(44, 64, 86), ref quads);
        }

        // 各階ベランダと掃き出し窓（正面＋反対面）
        int frontA = b.FrontDir;
        int frontB = b.FrontDir ^ 1;
        float floorH = 3.1f;
        float yStart = b.BaseY + 3.3f;
        float yLimit = top - 1.1f;
        float stepY = d < 280 ? floorH : d < 480 ? floorH * 2f : floorH * 3f;

        if (d < 520 || night)
        {
            for (int f = 0; yStart + f * stepY < yLimit; f++)
            {
                float fy = yStart + f * stepY;
                if (fy + 2.1f > top) break;

                for (int pass = 0; pass < 2; pass++)
                {
                    int side = pass == 0 ? frontA : frontB;
                    float span = side <= 1 ? hz : hx;
                    float uLim = span * 0.86f;

                    // バルコニー背景ガラス帯（夜は部屋単位点灯）
                    DrawFloorStrip(b, b.X, b.Z, hx, hz, side, -uLim, uLim, fy + 0.1f, fy + 2.0f,
                        night, 0.58f, Rgb(56, 82, 104), f, d < 220, true, ref quads);

                    // 立体バルコニー（近距離は3D手すり板、中距離は帯）
                    if (d < 200)
                    {
                        AddFaceProtrusion(b.X, b.Z, hx, hz, side, -uLim - 0.08f, uLim + 0.08f, fy - 0.06f, fy + 0.12f, 0f, 0.55f, railCol, railCol, ref quads);
                        AddFaceProtrusion(b.X, b.Z, hx, hz, side, -uLim - 0.08f, uLim + 0.08f, fy + 0.12f, fy + 0.94f, 0.42f, 0.55f, railCol, railCol, ref quads);
                    }
                    else if (d < 460)
                    {
                        AddFaceQuad(b.X, b.Z, hx, hz, side, -uLim, uLim, fy, fy + 0.90f, 0.16f, railCol, ref quads);
                    }
                }
            }

            // 住戸を区切る縦ピラスター柱（横縞を防ぎ住戸ごとの区画を見せる）
            if (d < 480)
                DrawVerticalMullions(b.X, b.Z, hx, hz, b.BaseY + 3.2f, top - 0.3f, ShadeColor(wallCol, 0.90f), ref quads, maxOut: 0.22f, bothFrontOnly: true, frontA: frontA, frontB: frontB);
        }

        // 側面（階段室・共用廊下窓帯＋各階の妻側採光窓）
        if (d < 360 || night)
        {
            var sideDay = Rgb(58, 84, 108);
            var sideOff = Rgb(18, 24, 36);
            var sideSash = Rgb(224, 228, 232);
            for (int side = 0; side < 4; side++)
            {
                if (side == frontA || side == frontB) continue;
                float span = side <= 1 ? hz : hx;
                if (span < 4.5f) continue;

                // 階段室縦スリット
                if (d < 360)
                    AddFaceQuad(b.X, b.Z, hx, hz, side, -0.55f, 0.55f, b.BaseY + 3.4f, top - 1.2f, 0.045f,
                        night ? Rgb(200, 222, 240) : Rgb(62, 86, 108), ref quads, lit: night);

                // 各階の妻側小窓（1階〜最上階）
                int sideCols = span >= 7.5f ? 2 : 0;
                if (sideCols > 0)
                {
                    float wStepY = d < 240 ? floorH : floorH * 2f;
                    for (int f = 0; b.BaseY + 1.2f + f * wStepY < yLimit; f++)
                    {
                        float wy0 = b.BaseY + 1.2f + f * wStepY;
                        float wy1 = Math.Min(top - 1.0f, wy0 + 1.45f);
                        if (wy1 <= wy0 + 0.4f) continue;
                        for (int sc = 0; sc < sideCols; sc++)
                        {
                            float uc = sc == 0 ? -span * 0.54f : span * 0.54f;
                            bool litWin = night && CellHash(b.Seed, f, side, sc) < 0.52f;
                            DrawFramedWindow(b.X, b.Z, hx, hz, side, uc - 0.85f, uc + 0.85f, wy0, wy1,
                                sideSash, sideDay, sideOff, night, litWin, d < 140, ref quads);
                        }
                    }
                }
            }
        }
    }

    private void DrawCommercial(Block b, float d, bool night, float yGround, float hx, float hz, ref int quads)
    {
        var wallCol = CommWalls[b.Style % CommWalls.Length];
        var roofDeck = Rgb(98, 104, 112);
        var trimCol = ShadeColor(wallCol, 0.84f);
        float top = b.TopY;

        AddBuildingBox(b.X, b.Z, yGround, top - 0.02f, hx, hz, wallCol, roofDeck, ref quads);
        DrawRooftopParapetAndPenthouse(b, hx, hz, top, wallCol, roofDeck, d, hasHelipad: false, ref quads);

        // 1階商業ショーウィンドウ＋庇＋街路側袖看板
        if (d < 320)
        {
            var neonCol = ShopBands[(b.Style + 1) % ShopBands.Length];
            int cornerAdj = (b.FrontDir & 2) == 0 ? (b.Seed % 2 == 0 ? 2 : 3) : (b.Seed % 2 == 0 ? 0 : 1);
            for (int pass = 0; pass < 2; pass++)
            {
                int side = pass == 0 ? b.FrontDir : cornerAdj;
                float span = side <= 1 ? hz : hx;
                AddFaceQuad(b.X, b.Z, hx, hz, side, -span * 0.84f, span * 0.84f, b.BaseY + 0.15f, b.BaseY + 3.65f, 0.045f,
                    night ? Rgb(250, 244, 212) : Rgb(56, 104, 134), ref quads, lit: night);
                if (d < 160)
                {
                    AddFaceProtrusion(b.X, b.Z, hx, hz, side, -span * 0.86f, span * 0.86f, b.BaseY + 3.70f, b.BaseY + 4.08f, 0f, 0.72f, trimCol, trimCol, ref quads);
                    if (b.H > 18f)
                    {
                        float signTop = Math.Min(top - 1.5f, b.BaseY + 13.5f);
                        float signBot = Math.Min(signTop - 2.2f, b.BaseY + 5.2f);
                        if (signTop > signBot + 1.5f)
                            AddFaceProtrusion(b.X, b.Z, hx, hz, side, span - 0.55f, span - 0.22f, signBot, signTop, 0.14f, 0.86f, neonCol, neonCol, ref quads, lit: night);
                    }
                }
            }
        }

        // 上階オフィス窓グリッド＋縦マリオン柱
        float yWin0 = b.BaseY + 4.8f;
        float yWin1 = top - 1.4f;
        DrawWindowBands(b, b.X, b.Z, hx, hz, yWin0, yWin1, 3.6f, 1.95f, night, 0.44f, Rgb(54, 84, 114), d < 250, ref quads);
        if (d < 520) DrawVerticalMullions(b.X, b.Z, hx, hz, yWin0, yWin1, wallCol, ref quads);
    }

    private void DrawSkyscraper(Block b, float d, bool night, float yGround, float hx, float hz, ref int quads)
    {
        int pIdx = b.Style % SkyWalls.Length;
        var wallCol = SkyWalls[pIdx];
        var glassCol = SkyGlass[pIdx];
        var roofDeck = Rgb(92, 98, 108);
        int shapeStyle = b.Style % 3;

        // 形状パターンによる段差セットバック（ヒープ割り当てなし）
        int tierCount = shapeStyle switch { 0 => 2, 1 => 3, _ => 1 };
        float topSx = 1f, topSz = 1f;

        for (int t = 0; t < tierCount; t++)
        {
            float frac0 = shapeStyle switch
            {
                0 => t == 0 ? 0f : 0.64f,
                1 => t == 0 ? 0f : t == 1 ? 0.46f : 0.78f,
                _ => 0f,
            };
            float frac1 = shapeStyle switch
            {
                0 => t == 0 ? 0.64f : 1f,
                1 => t == 0 ? 0.46f : t == 1 ? 0.78f : 1f,
                _ => 1f,
            };
            float scale = shapeStyle switch
            {
                0 => t == 0 ? 1.0f : 0.78f,
                1 => t == 0 ? 1.0f : t == 1 ? 0.82f : 0.64f,
                _ => 1.0f,
            };
            float ty0 = t == 0 ? yGround : b.BaseY + b.H * frac0 - 0.2f;
            float ty1 = b.BaseY + b.H * frac1;
            float thx = hx * scale, thz = hz * scale;
            topSx = scale;
            topSz = scale;
            float wallStartY = t == 0 ? b.BaseY + 4.4f : ty0 + 0.3f;
            var bodyCol = t == 0 ? wallCol : Lerp(wallCol, Rgb(208, 214, 222), 0.14f);

            AddBuildingBox(b.X, b.Z, ty0, ty1 - 0.02f, thx, thz, bodyCol, roofDeck, ref quads);

            // 1階石貼り風基壇＋入口ガラス
            if (t == 0 && d < 320)
            {
                var baseStone = Rgb(78, 84, 94);
                AddBuildingBox(b.X, b.Z, yGround, b.BaseY + 4.2f, thx + 0.12f, thz + 0.12f, baseStone, baseStone, ref quads, drawTop: false);
                AddFaceQuad(b.X, b.Z, thx + 0.12f, thz + 0.12f, b.FrontDir, -Math.Min(5.2f, (b.FrontDir <= 1 ? thz : thx) * 0.65f),
                    Math.Min(5.2f, (b.FrontDir <= 1 ? thz : thx) * 0.65f), b.BaseY + 0.15f, b.BaseY + 3.85f, 0.05f,
                    night ? Rgb(245, 240, 210) : Rgb(76, 128, 162), ref quads, lit: night);
            }

            // カーテンウォール・窓帯・縦マリオン
            float bandTop = ty1 - 1.4f;
            if (bandTop > wallStartY + 2f)
            {
                DrawWindowBands(b, b.X, b.Z, thx, thz, wallStartY, bandTop, 3.8f, 2.35f, night, 0.42f, glassCol, d < 260, ref quads);
                if (d < 560)
                    DrawVerticalMullions(b.X, b.Z, thx, thz, wallStartY, bandTop, Lerp(bodyCol, Rgb(220, 226, 234), 0.35f), ref quads);
            }
        }

        // 最上階屋上：パラペット＋ヘリポート「H」＋隅の航空障害灯＋避雷針
        float topHx = hx * topSx, topHz = hz * topSz;
        DrawRooftopParapetAndPenthouse(b, topHx, topHz, b.TopY, wallCol, roofDeck, d, hasHelipad: true, ref quads);

        // 航空障害灯（屋上四隅の2隅またはアンテナ先端）
        if (d < 1200)
        {
            bool phase = (b.Seed & 1) == 0;
            roofBeacons.Add(new RoofBeacon(b.X - topHx + 0.5, b.TopY + 1.4f, b.Z - topHz + 0.5, d, phase));
            roofBeacons.Add(new RoofBeacon(b.X + topHx - 0.5, b.TopY + 1.4f, b.Z + topHz - 0.5, d, phase));
            if (d < 600 && (b.Style % 2 == 0 || b.H > 100f))
            {
                double antX = b.X + topHx * 0.58, antZ = b.Z + topHz * 0.58;
                float antH = 8f + b.H * 0.08f;
                roofSpires.Add(new RoofSpire(antX, b.TopY, b.TopY + antH, antZ, d));
                roofBeacons.Add(new RoofBeacon(antX, b.TopY + antH + 0.25f, antZ, d, !phase));
            }
        }
    }

    private void DrawFramedWindow(double cx, double cz, float hx, float hz, int side,
        float u0, float u1, float y0, float y1, Color sashCol, Color dayGlass, Color offGlass,
        bool night, bool litWin, bool detailedFrame, ref int quads)
    {
        if (detailedFrame)
        {
            AddFaceQuad(cx, cz, hx, hz, side, u0 - 0.08f, u1 + 0.08f, y0 - 0.07f, y1 + 0.07f, 0.032f, sashCol, ref quads);
            var glass = night ? (litWin ? Rgb(255, 214, 132) : offGlass) : dayGlass;
            AddFaceQuad(cx, cz, hx, hz, side, u0, u1, y0, y1, 0.048f, glass, ref quads, lit: night && litWin);
            // 窓中央の縦桟
            float um = (u0 + u1) * 0.5f;
            AddFaceQuad(cx, cz, hx, hz, side, um - 0.04f, um + 0.04f, y0, y1, 0.062f, sashCol, ref quads);
        }
        else
        {
            var glass = night ? (litWin ? Rgb(255, 214, 132) : offGlass) : dayGlass;
            AddFaceQuad(cx, cz, hx, hz, side, u0, u1, y0, y1, 0.045f, glass, ref quads, lit: night && litWin);
        }
    }

    private void DrawFloorStrip(Block b, double cx, double cz, float hx, float hz, int side,
        float u0, float u1, float y0, float y1, bool night, float litChance, Color dayGlass,
        int floorIdx, bool perRoomNight, bool isResidential, ref int quads)
    {
        var baseGlass = night ? Rgb(18, 24, 38) : dayGlass;
        AddFaceQuad(cx, cz, hx, hz, side, u0, u1, y0, y1, 0.042f, baseGlass, ref quads);

        if (!night) return;

        float width = u1 - u0;
        int cols = perRoomNight ? Math.Clamp((int)(width / 3.4f), 2, 7) : 2;
        float step = width / cols;
        for (int c = 0; c < cols; c++)
        {
            if (CellHash(b.Seed, floorIdx, side, c) > litChance) continue;
            float lu0 = u0 + c * step + 0.18f;
            float lu1 = u0 + (c + 1) * step - 0.18f;
            bool warm = isResidential || CellHash(b.Seed, floorIdx, c, side + 9) < 0.35f;
            var wCol = warm ? Rgb(255, 212, 130) : Rgb(214, 236, 255);
            AddFaceQuad(cx, cz, hx, hz, side, lu0, lu1, y0 + 0.08f, y1 - 0.08f, 0.056f, wCol, ref quads, lit: true);
        }
    }

    private void DrawWindowBands(Block b, double cx, double cz, float hx, float hz,
        float y0, float y1, float floorH, float winH, bool night, float litChance, Color dayGlass,
        bool nearDetail, ref int quads)
    {
        if (y1 <= y0 + 1.0f) return;
        float spanH = y1 - y0;
        // 距離に応じてフロアを適切に束ねて、どの距離でも高フレームレートを保つ
        float targetBands = nearDetail ? spanH / floorH : Math.Min(12f, spanH / (floorH * 1.6f));
        int bands = Math.Max(1, (int)targetBands);
        float step = spanH / bands;
        float bandH = Math.Min(winH, step * 0.60f);

        for (int f = 0; f < bands; f++)
        {
            float by0 = y0 + f * step + (step - bandH) * 0.45f;
            float by1 = by0 + bandH;
            for (int side = 0; side < 4; side++)
            {
                float span = (side <= 1 ? hz : hx) * 0.88f;
                if (span < 1.2f) continue;
                DrawFloorStrip(b, cx, cz, hx, hz, side, -span, span, by0, by1,
                    night, litChance, dayGlass, f, nearDetail, false, ref quads);
            }
        }
    }

    private void DrawVerticalMullions(double cx, double cz, float hx, float hz, float y0, float y1, Color col, ref int quads,
        float maxOut = 0.068f, bool bothFrontOnly = false, int frontA = 0, int frontB = 1)
    {
        for (int side = 0; side < 4; side++)
        {
            if (bothFrontOnly && side != frontA && side != frontB) continue;
            float span = side <= 1 ? hz : hx;
            int mullions = Math.Clamp((int)(span / 3.4f) + 1, 3, 6);
            for (int m = 0; m < mullions; m++)
            {
                float u = -span * 0.88f + (span * 1.76f) * m / Math.Max(1, mullions - 1);
                AddFaceQuad(cx, cz, hx, hz, side, u - 0.24f, u + 0.24f, y0 - 0.2f, y1 + 0.2f, maxOut, col, ref quads);
            }
        }
    }

    private void DrawRooftopParapetAndPenthouse(Block b, float hx, float hz, float top,
        Color wallCol, Color roofDeck, float d, bool hasHelipad, ref int quads)
    {
        if (d > 620) return;

        // 屋上の立ち上がり縁（パラペット）
        float ph = b.H > 50 ? 0.95f : 0.55f;
        float rimT = 0.26f;
        var rimCol = ShadeColor(wallCol, 0.90f);
        AddBuildingBox(b.X, b.Z - hz + rimT, top - 0.02f, top + ph, hx, rimT, rimCol, rimCol, ref quads);
        AddBuildingBox(b.X, b.Z + hz - rimT, top - 0.02f, top + ph, hx, rimT, rimCol, rimCol, ref quads);
        AddBuildingBox(b.X - hx + rimT, b.Z, top - 0.02f, top + ph, rimT, hz - rimT * 2, rimCol, rimCol, ref quads);
        AddBuildingBox(b.X + hx - rimT, b.Z, top - 0.02f, top + ph, rimT, hz - rimT * 2, rimCol, rimCol, ref quads);

        // ヘリポートマーク（中央を広く使って着陸目印にする）
        if (hasHelipad && d < 520 && Math.Min(hx, hz) > 5.2f)
        {
            float padR = Math.Min(hx, hz) * 0.54f;
            float py = top + 0.04f;
            var padCol = Rgb(54, 60, 68);
            VQ(b.X - padR, py, b.Z - padR, padCol); VQ(b.X + padR, py, b.Z - padR, padCol);
            VQ(b.X + padR, py, b.Z + padR, padCol); VQ(b.X - padR, py, b.Z + padR, padCol);
            quads++;

            // 黄色い「H」マーク
            float hr = padR * 0.56f, wt = Math.Max(0.32f, hr * 0.24f);
            float hy = top + 0.07f;
            var yCol = Rgb(245, 200, 38);
            void HBar(double x0, double x1, double z0, double z1)
            {
                VQ(b.X + x0, hy, b.Z + z0, yCol); VQ(b.X + x1, hy, b.Z + z0, yCol);
                VQ(b.X + x1, hy, b.Z + z1, yCol); VQ(b.X + x0, hy, b.Z + z1, yCol);
            }
            HBar(-hr, -hr + wt, -hr, hr);
            HBar(hr - wt, hr, -hr, hr);
            HBar(-hr + wt, hr - wt, -wt * 0.5, wt * 0.5);
            quads += 3;
        }
        else if (d < 460 && Math.Min(hx, hz) > 4.2f)
        {
            // 一般マンション・商業ビルの塔屋・空調設備
            var phCol = ShadeColor(wallCol, 0.84f);
            float pw = hx * 0.36f, pd = hz * 0.36f;
            AddBuildingBox(b.X - hx * 0.25f, b.Z - hz * 0.22f, top - 0.05f, top + 2.8f, pw, pd, phCol, roofDeck, ref quads);
            AddBuildingBox(b.X + hx * 0.35f, b.Z + hz * 0.30f, top - 0.05f, top + 1.45f, 1.4f, 1.0f, Rgb(192, 198, 206), Rgb(170, 176, 184), ref quads);
        }
    }

    private void PushGableRoof(double cx, double cz, float hx, float hz, float yEave, float yPeak, float ov,
        Color wallCol, Color roofCol, ref int quads)
    {
        bool ridgeX = hx >= hz;
        var sA = ShadeColor(roofCol, 1.05f);
        var sB = ShadeColor(roofCol, 0.84f);
        var ridgeCol = ShadeColor(roofCol, 0.72f);

        if (ridgeX)
        {
            float rx = hx + ov, rz = hz + ov;
            PushSlopedQuad(cx - rx, yEave, cz - rz, cx + rx, yEave, cz - rz, cx + rx, yPeak, cz, cx - rx, yPeak, cz, 0, 1, -1, sA);
            PushSlopedQuad(cx + rx, yEave, cz + rz, cx - rx, yEave, cz + rz, cx - rx, yPeak, cz, cx + rx, yPeak, cz, 0, 1, 1, sB);
            PushTri(cx + hx, yEave, cz - hz, cx + hx, yEave, cz + hz, cx + hx, yPeak, cz, 1, 0, 0, ShadeColor(wallCol, SideShade(0)));
            PushTri(cx - hx, yEave, cz + hz, cx - hx, yEave, cz - hz, cx - hx, yPeak, cz, -1, 0, 0, ShadeColor(wallCol, SideShade(1)));
            AddBuildingBox(cx, cz, yPeak - 0.06f, yPeak + 0.16f, rx, 0.14f, ridgeCol, ridgeCol, ref quads);
        }
        else
        {
            float rx = hx + ov, rz = hz + ov;
            PushSlopedQuad(cx + rx, yEave, cz - rz, cx + rx, yEave, cz + rz, cx, yPeak, cz + rz, cx, yPeak, cz - rz, 1, 1, 0, sA);
            PushSlopedQuad(cx - rx, yEave, cz + rz, cx - rx, yEave, cz - rz, cx, yPeak, cz - rz, cx, yPeak, cz + rz, -1, 1, 0, sB);
            PushTri(cx + hx, yEave, cz + hz, cx - hx, yEave, cz + hz, cx, yPeak, cz + hz, 0, 0, 1, ShadeColor(wallCol, SideShade(2)));
            PushTri(cx - hx, yEave, cz - hz, cx + hx, yEave, cz - hz, cx, yPeak, cz - hz, 0, 0, -1, ShadeColor(wallCol, SideShade(3)));
            AddBuildingBox(cx, cz, yPeak - 0.06f, yPeak + 0.16f, 0.14f, rz, ridgeCol, ridgeCol, ref quads);
        }
    }

    private void PushHipRoof(double cx, double cz, float hx, float hz, float yEave, float yPeak, float ov,
        Color roofCol, ref int quads)
    {
        float rx = hx + ov, rz = hz + ov;
        var sS = ShadeColor(roofCol, 1.06f);
        var sN = ShadeColor(roofCol, 0.80f);
        var sE = ShadeColor(roofCol, 0.96f);
        var sW = ShadeColor(roofCol, 0.88f);
        var ridgeCol = ShadeColor(roofCol, 0.72f);

        if (hx >= hz)
        {
            float tx = Math.Max(0.25f, hx - hz * 0.52f);
            PushSlopedQuad(cx - rx, yEave, cz - rz, cx + rx, yEave, cz - rz, cx + tx, yPeak, cz, cx - tx, yPeak, cz, 0, 1, -1, sS);
            PushSlopedQuad(cx + rx, yEave, cz + rz, cx - rx, yEave, cz + rz, cx - tx, yPeak, cz, cx + tx, yPeak, cz, 0, 1, 1, sN);
            PushTri(cx + rx, yEave, cz - rz, cx + rx, yEave, cz + rz, cx + tx, yPeak, cz, 1, 1, 0, sE);
            PushTri(cx - rx, yEave, cz + rz, cx - rx, yEave, cz - rz, cx - tx, yPeak, cz, -1, 1, 0, sW);
            AddBuildingBox(cx, cz, yPeak - 0.06f, yPeak + 0.14f, tx, 0.14f, ridgeCol, ridgeCol, ref quads);
        }
        else
        {
            float tz = Math.Max(0.25f, hz - hx * 0.52f);
            PushSlopedQuad(cx + rx, yEave, cz - rz, cx + rx, yEave, cz + rz, cx, yPeak, cz + tz, cx, yPeak, cz - tz, 1, 1, 0, sE);
            PushSlopedQuad(cx - rx, yEave, cz + rz, cx - rx, yEave, cz - rz, cx, yPeak, cz - tz, cx, yPeak, cz + tz, -1, 1, 0, sW);
            PushTri(cx - rx, yEave, cz - rz, cx + rx, yEave, cz - rz, cx, yPeak, cz - tz, 0, 1, -1, sS);
            PushTri(cx + rx, yEave, cz + rz, cx - rx, yEave, cz + rz, cx, yPeak, cz + tz, 0, 1, 1, sN);
            AddBuildingBox(cx, cz, yPeak - 0.06f, yPeak + 0.14f, 0.14f, tz, ridgeCol, ridgeCol, ref quads);
        }
    }
}
