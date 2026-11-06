namespace JapanDrive;

public enum RoadType { Expressway, Bridge, Ferry, MegaBridge }

public sealed class City
{
    public City(string name, double lat, double lon, bool isGoal)
    {
        Name = name;
        Lat = lat;
        Lon = lon;
        IsGoal = isGoal;
    }

    public string Name { get; }
    public double Lat { get; }
    public double Lon { get; }
    /// <summary>★目的地（日本一周で必ず訪れる都市）</summary>
    public bool IsGoal { get; }
    public List<Road> Roads { get; } = new();

    public override string ToString() => Name;
}

public sealed class Road
{
    public Road(string name, City a, City b, double km, RoadType type)
    {
        Name = name;
        A = a;
        B = b;
        Km = km;
        Type = type;
    }

    public string Name { get; }
    public City A { get; }
    public City B { get; }
    public double Km { get; }
    public RoadType Type { get; }

    public City Other(City c) => c == A ? B : A;

    /// <summary>通行料金（簡易計算）</summary>
    public int Toll => Type switch
    {
        RoadType.Ferry => 18000,
        RoadType.Bridge => RoundTen(Km * 60 + 150),
        RoadType.MegaBridge => RoundTen(Km * 20 + 500),
        _ => RoundTen(Km * 25 + 150),
    };

    private static int RoundTen(double v) => (int)(Math.Round(v / 10) * 10);
}

/// <summary>
/// 主要都市と高速道路網のデータ。
/// 距離はおおよその実距離（ゲーム用に簡略化）。
/// </summary>
public static class MapData
{
    public static List<City> Cities { get; } = new();
    public static List<Road> Roads { get; } = new();
    private static readonly Dictionary<string, City> ByName = new();

    static MapData()
    {
        Build();
        // 分岐の番号が直感的になるよう、北から時計回りに並べる
        foreach (var c in Cities)
            c.Roads.Sort((x, y) => Bearing(c, x.Other(c)).CompareTo(Bearing(c, y.Other(c))));
    }

    public static City Get(string name) => ByName[name];

    private static double Bearing(City from, City to)
    {
        double dx = (to.Lon - from.Lon) * Math.Cos(from.Lat * Math.PI / 180);
        double dy = to.Lat - from.Lat;
        double deg = Math.Atan2(dx, dy) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    private static void C(string name, double lat, double lon, bool goal = false)
    {
        var c = new City(name, lat, lon, goal);
        Cities.Add(c);
        ByName[name] = c;
    }

    private static void R(string road, string a, string b, double km, RoadType type = RoadType.Expressway)
    {
        var r = new Road(road, Get(a), Get(b), km, type);
        Roads.Add(r);
        r.A.Roads.Add(r);
        r.B.Roads.Add(r);
    }

    private static void Build()
    {
        // ---- 北海道 ----
        C("札幌", 43.06, 141.35, true);
        C("旭川", 43.77, 142.36);
        C("千歳", 42.82, 141.65);
        C("帯広", 42.92, 143.20);
        C("釧路", 42.98, 144.38);
        C("函館", 41.77, 140.73);
        // ---- 東北 ----
        C("青森", 40.82, 140.74);
        C("盛岡", 39.70, 141.15);
        C("秋田", 39.72, 140.10);
        C("仙台", 38.27, 140.87, true);
        C("山形", 38.24, 140.36);
        C("郡山", 37.40, 140.38);
        C("いわき", 37.05, 140.89);
        // ---- 関東・甲信越 ----
        C("新潟", 37.90, 139.02, true);
        C("宇都宮", 36.56, 139.88);
        C("水戸", 36.37, 140.47);
        C("高崎", 36.32, 139.00);
        C("東京", 35.68, 139.77, true);
        C("千葉", 35.61, 140.12);
        C("甲府", 35.66, 138.57);
        C("岡谷", 36.06, 138.05);
        C("長野", 36.65, 138.18);
        C("上越", 37.15, 138.24);
        // ---- 北陸・東海 ----
        C("富山", 36.70, 137.21);
        C("金沢", 36.56, 136.66, true);
        C("福井", 36.06, 136.22);
        C("静岡", 34.98, 138.38);
        C("浜松", 34.71, 137.73);
        C("名古屋", 35.18, 136.91, true);
        // ---- 近畿 ----
        C("米原", 35.31, 136.29);
        C("京都", 35.01, 135.77);
        C("大阪", 34.69, 135.50, true);
        C("神戸", 34.69, 135.20);
        C("和歌山", 34.23, 135.17);
        // ---- 中国 ----
        C("岡山", 34.66, 133.92);
        C("米子", 35.43, 133.33);
        C("松江", 35.47, 133.05);
        C("福山", 34.49, 133.36);
        C("広島", 34.39, 132.46, true);
        C("山口", 34.18, 131.47);
        C("下関", 33.96, 130.94);
        // ---- 四国 ----
        C("高松", 34.34, 134.05);
        C("徳島", 34.07, 134.55);
        C("川之江", 33.98, 133.57);
        C("松山", 33.84, 132.77);
        C("今治", 34.07, 133.00);
        C("高知", 33.56, 133.53, true);
        // ---- 九州 ----
        C("北九州", 33.88, 130.88);
        C("福岡", 33.59, 130.40, true);
        C("鳥栖", 33.38, 130.51);
        C("長崎", 32.75, 129.87);
        C("熊本", 32.80, 130.71);
        C("大分", 33.24, 131.61);
        C("宮崎", 31.91, 131.42);
        C("鹿児島", 31.60, 130.56, true);
        // ---- 奄美・沖縄（超巨大海上橋で結ぶ） ----
        C("奄美", 28.38, 129.49);
        C("那覇", 26.21, 127.68);

        // ---- 北海道 ----
        R("道央道", "旭川", "札幌", 137);
        R("道央道", "札幌", "千歳", 40);
        R("道央道", "千歳", "函館", 250);
        R("道東道", "千歳", "帯広", 165);
        R("道東道", "帯広", "釧路", 120);
        R("青函フェリー", "函館", "青森", 113, RoadType.Ferry);
        // ---- 東北 ----
        R("東北道", "青森", "盛岡", 175);
        R("秋田道", "盛岡", "秋田", 120);
        R("東北道", "盛岡", "仙台", 180);
        R("山形道", "仙台", "山形", 60);
        R("東北道", "仙台", "郡山", 160);
        R("常磐道", "仙台", "いわき", 150);
        R("磐越道", "郡山", "いわき", 80);
        R("磐越道", "郡山", "新潟", 210);
        R("東北道", "郡山", "宇都宮", 120);
        R("東北道", "宇都宮", "東京", 105);
        R("常磐道", "東京", "水戸", 100);
        R("常磐道", "水戸", "いわき", 95);
        // ---- 関東・甲信越・北陸 ----
        R("京葉道路", "東京", "千葉", 35);
        R("関越道", "東京", "高崎", 105);
        R("関越道", "高崎", "新潟", 200);
        R("上信越道", "高崎", "長野", 105);
        R("上信越道", "長野", "上越", 75);
        R("北陸道", "上越", "新潟", 110);
        R("北陸道", "上越", "富山", 115);
        R("北陸道", "富山", "金沢", 60);
        R("北陸道", "金沢", "福井", 75);
        R("北陸道", "福井", "米原", 95);
        R("中央道", "東京", "甲府", 120);
        R("中央道", "甲府", "岡谷", 75);
        R("中央道", "岡谷", "名古屋", 165);
        R("長野道", "岡谷", "長野", 75);
        // ---- 東海・近畿 ----
        R("東名高速", "東京", "静岡", 170);
        R("東名高速", "静岡", "浜松", 75);
        R("東名高速", "浜松", "名古屋", 100);
        R("名神高速", "名古屋", "米原", 65);
        R("名神高速", "米原", "京都", 60);
        R("名神高速", "京都", "大阪", 40);
        R("阪神高速", "大阪", "神戸", 30);
        R("阪和道", "大阪", "和歌山", 65);
        // ---- 中国 ----
        R("山陽道", "神戸", "岡山", 130);
        R("山陽道", "岡山", "福山", 55);
        R("山陽道", "福山", "広島", 90);
        R("山陽道", "広島", "山口", 125);
        R("中国道", "山口", "下関", 70);
        R("米子道", "岡山", "米子", 125);
        R("山陰道", "米子", "松江", 30);
        // ---- 本四連絡橋・四国 ----
        R("神戸淡路鳴門道", "神戸", "徳島", 100, RoadType.Bridge);
        R("瀬戸中央道", "岡山", "高松", 50, RoadType.Bridge);
        R("しまなみ海道", "福山", "今治", 60, RoadType.Bridge);
        R("高松道", "高松", "徳島", 70);
        R("高松道", "高松", "川之江", 70);
        R("徳島道", "徳島", "川之江", 95);
        R("松山道", "川之江", "松山", 80);
        R("高知道", "川之江", "高知", 60);
        R("今治小松道", "今治", "松山", 45);
        // ---- 九州 ----
        R("関門橋", "下関", "北九州", 15, RoadType.Bridge);
        R("九州道", "北九州", "福岡", 60);
        R("九州道", "福岡", "鳥栖", 30);
        R("長崎道", "鳥栖", "長崎", 100);
        R("九州道", "鳥栖", "熊本", 85);
        R("九州道", "熊本", "鹿児島", 170);
        R("大分道", "鳥栖", "大分", 115);
        R("東九州道", "北九州", "大分", 130);
        R("東九州道", "大分", "宮崎", 200);
        R("宮崎道", "宮崎", "鹿児島", 125);
        // ---- 沖縄海上大橋（ゲームオリジナル） ----
        R("沖縄海上大橋", "鹿児島", "奄美", 380, RoadType.MegaBridge);
        R("沖縄海上大橋", "奄美", "那覇", 300, RoadType.MegaBridge);
    }
}
