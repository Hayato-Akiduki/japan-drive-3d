namespace JapanDrive.Tests;

/// <summary>
/// 都市・道路・名所・海岸線のデータの自動チェック。
/// 国や道路を追加したら dotnet test JapanDrive.Tests で確かめる。
/// </summary>
public class MapDataTests
{
    /// <summary>高速道路が続けて海の上を走ってよい距離（km）。手描きの海岸線の粗さを許す。</summary>
    private const int MaxSeaRunKm = 20;

    /// <summary>高速道路が続けて湖の上を走ってよい距離（km）。浜名湖・猪苗代湖の近くなど、湖の形が粗い所を許す。</summary>
    private const int MaxLakeRunKm = 15;

    /// <summary>自動生成した海岸線で、地図に描く1本の線の長さの上限（km）。切り抜きの辺が残ると何百kmにもなる。</summary>
    private const double MaxGeneratedCoastEdgeKm = 150;

    private static IEnumerable<Road> Expressways => MapData.Roads.Where(r => r.Type == RoadType.Expressway);

    private static void AssertNone(IEnumerable<string> problems, string what)
    {
        var list = problems.ToList();
        Assert.True(list.Count == 0, $"{what}（{list.Count}件）:\n" + string.Join("\n", list));
    }

    [Fact]
    public void CityNamesAreUnique() =>
        AssertNone(MapData.Cities.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key), "同じ名前の都市があります");

    [Fact]
    public void EveryCityCanBeReachedFromTokyo()
    {
        var seen = new HashSet<City> { MapData.Get("東京") };
        var queue = new Queue<City>(seen);
        while (queue.Count > 0)
        {
            var city = queue.Dequeue();
            foreach (var road in city.Roads)
                if (seen.Add(road.Other(city))) queue.Enqueue(road.Other(city));
        }
        AssertNone(MapData.Cities.Where(c => !seen.Contains(c)).Select(c => c.Name), "東京から道路・航路でたどり着けない都市があります");
    }

    [Fact]
    public void CitiesAreOnLandAndNotInLakes() =>
        AssertNone(MapData.Cities
            .Where(c => !Geo.NearLand(c.Lat, c.Lon, 5) || Geo.Lakes.Contains(c.Lat, c.Lon))
            .Select(c => $"{c.Name} ({c.Lat}, {c.Lon})"), "海の上（陸から5km以上）や湖の中にある都市があります");

    [Fact]
    public void ExpresswayLengthMatchesItsRoute() =>
        AssertNone(Expressways
            .Select(r => (r, ratio: r.Km / Geo.PolylineKm(r.Points)))
            .Where(x => x.ratio < 0.85 || x.ratio > 1.9)
            .Select(x => $"{x.r.Name} {x.r.A}→{x.r.B}: {x.r.Km}km ／ 経由地をつないだ長さの {x.ratio:0.00} 倍"),
            "道路の距離と経由地の位置が合いません（経由地の打ち間違いかもしれません）");

    [Fact]
    public void ExpresswaysDoNotRunOverOpenSea() =>
        AssertNone(Expressways
            .Select(r => (r, run: LongestRun(r, p => !Geo.Land.Contains(p.Lat, p.Lon))))
            .Where(x => x.run.Km > MaxSeaRunKm)
            .Select(x => $"{x.r.Name} {x.r.A}→{x.r.B}: 約{x.run.Km}km 海の上（{x.run.Start} 付近から）"),
            $"高速道路が {MaxSeaRunKm}km 以上続けて海の上を走っています（経由地を陸側に足してください）");

    [Fact]
    public void ExpresswaysDoNotRunThroughLakes() =>
        AssertNone(Expressways
            .Select(r => (r, run: LongestRun(r, p => Geo.Lakes.Contains(p.Lat, p.Lon))))
            .Where(x => x.run.Km > MaxLakeRunKm)
            .Select(x => $"{x.r.Name} {x.r.A}→{x.r.B}: 約{x.run.Km}km 湖の中（{x.run.Start} 付近から）"),
            $"高速道路が {MaxLakeRunKm}km 以上続けて湖の中を走っています（フリーモードでは水没します）");

    [Fact]
    public void SpotsHaveUniqueKeys() =>
        AssertNone(Spots.All.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key), "同じ都市に同じ名前の名所があります");

    [Fact]
    public void RealSpotsMatchSpotNames()
    {
        var names = Spots.All.Select(s => s.Name).ToHashSet();
        AssertNone(GeoFeatures.RealSpots.Keys.Where(k => !names.Contains(k)),
            "実在地点（RealSpots）の名前が名所一覧にありません（名所が本物の場所に置かれません）");
    }

    [Fact]
    public void EveryPointIsInsideTheLandRaster()
    {
        double lonMax = LandRaster.Lon0 + LandRaster.W * LandRaster.Res;
        double latMax = LandRaster.Lat0 + LandRaster.H * LandRaster.Res;
        bool Inside(double lat, double lon) => lat >= LandRaster.Lat0 && lat < latMax && lon >= LandRaster.Lon0 && lon < lonMax;

        var points = MapData.Cities.Select(c => (c.Name, c.Lat, c.Lon))
            .Concat(MapData.Roads.SelectMany(r => r.Points.Select(p => ($"{r.Name}の経由地", p.Lat, p.Lon))))
            .Concat(GeoFeatures.Airports.Select(a => (a.Name, a.Lat, a.Lon)))
            .Concat(GeoFeatures.Peaks.Select(p => (p.Name, p.Lat, p.Lon)))
            .Concat(GeoFeatures.RealSpots.Select(kv => (kv.Key, kv.Value.Lat, kv.Value.Lon)));
        AssertNone(points.Where(p => !Inside(p.Lat, p.Lon)).Select(p => $"{p.Item1} ({p.Lat}, {p.Lon})"),
            "陸地ラスター（LandRaster.Lon0/Lat0/W/H）の外にある地点があります（範囲を広げてください）");
    }

    [Fact]
    public void GeneratedCoastlinesHaveNoLongVisibleEdges()
    {
        var rings = SoutheastAsiaGeo.Land.Concat(RussiaGeo.Land).Select(JapanOutline.Decode);
        var problems = new List<string>();
        foreach (var ring in rings)
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Length];
                double km = Geo.Km(a, b);
                if (km > MaxGeneratedCoastEdgeKm && !JapanOutline.HiddenEdge(a, b))
                    problems.Add($"{a} → {b}: {km:0}km");
            }
        AssertNone(problems, "地図に長い直線の海岸線が描かれます（切り抜きの辺が隠れていないかもしれません）");
    }

    /// <summary>道路を1kmごとに見て、条件に合う区間が最も長く続く距離（km）とその始まりの地点。</summary>
    private static (int Km, (double Lat, double Lon) Start) LongestRun(Road road, Func<(double Lat, double Lon), bool> match)
    {
        int run = 0;
        (int Km, (double, double) Start) best = (0, road.Points[0]);
        (double, double) start = road.Points[0];
        foreach (var p in Geo.Sample(road.Points, 1))
        {
            if (!match(p)) { run = 0; continue; }
            if (run == 0) start = (Math.Round(p.Lat, 2), Math.Round(p.Lon, 2));
            run++;
            if (run > best.Km) best = (run, start);
        }
        return best;
    }
}
