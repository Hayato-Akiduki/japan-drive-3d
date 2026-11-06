namespace JapanDrive;

public enum LandmarkType
{
    Tower, Castle, Torii, Pagoda, Temple, Mountain, Volcano,
    ClockTower, Lighthouse, Bridge, Buddha, Garden, Lake, Monument,
}

/// <summary>スタンプラリーの名所。Variant は見た目の違い（塔の色、天守の壁の色など）。</summary>
public sealed record Spot(string Name, City City, LandmarkType Type, int Variant = 0)
{
    public string Key => City.Name + "/" + Name;
}

public static class Spots
{
    public static List<Spot> All { get; } = new();

    static Spots()
    {
        void S(string city, string name, LandmarkType type, int variant = 0) =>
            All.Add(new Spot(name, MapData.Get(city), type, variant));

        // 北海道
        S("札幌", "札幌市時計台", LandmarkType.ClockTower);
        S("旭川", "旭岳", LandmarkType.Mountain);
        S("千歳", "支笏湖", LandmarkType.Lake);
        S("帯広", "幸福駅", LandmarkType.Monument, 2);
        S("釧路", "釧路湿原", LandmarkType.Lake, 1);
        S("函館", "五稜郭タワー", LandmarkType.Tower, 1);
        // 東北
        S("青森", "八甲田山", LandmarkType.Mountain);
        S("盛岡", "岩手山", LandmarkType.Mountain);
        S("秋田", "入道埼灯台", LandmarkType.Lighthouse);
        S("仙台", "伊達政宗騎馬像", LandmarkType.Monument);
        S("山形", "山寺（立石寺）", LandmarkType.Temple);
        S("郡山", "猪苗代湖", LandmarkType.Lake);
        S("いわき", "塩屋埼灯台", LandmarkType.Lighthouse);
        // 関東・甲信越
        S("新潟", "萬代橋", LandmarkType.Bridge);
        S("宇都宮", "日光東照宮", LandmarkType.Pagoda);
        S("水戸", "偕楽園", LandmarkType.Garden);
        S("高崎", "高崎白衣大観音", LandmarkType.Buddha);
        S("東京", "東京タワー", LandmarkType.Tower);
        S("東京", "浅草寺", LandmarkType.Temple);
        S("千葉", "成田山新勝寺", LandmarkType.Pagoda);
        S("甲府", "武田神社", LandmarkType.Torii);
        S("岡谷", "諏訪大社", LandmarkType.Torii);
        S("長野", "善光寺", LandmarkType.Temple);
        S("上越", "高田城", LandmarkType.Castle);
        // 北陸・東海
        S("富山", "立山連峰", LandmarkType.Mountain);
        S("金沢", "兼六園", LandmarkType.Garden);
        S("福井", "丸岡城", LandmarkType.Castle);
        S("静岡", "富士山", LandmarkType.Mountain, 1);
        S("浜松", "浜名湖", LandmarkType.Lake);
        S("名古屋", "名古屋城", LandmarkType.Castle);
        // 近畿
        S("米原", "彦根城", LandmarkType.Castle);
        S("京都", "伏見稲荷大社", LandmarkType.Torii, 1);
        S("京都", "東寺 五重塔", LandmarkType.Pagoda);
        S("大阪", "大阪城", LandmarkType.Castle);
        S("大阪", "通天閣", LandmarkType.Tower, 2);
        S("神戸", "神戸ポートタワー", LandmarkType.Tower, 3);
        S("和歌山", "和歌山城", LandmarkType.Castle);
        // 中国
        S("岡山", "後楽園", LandmarkType.Garden);
        S("米子", "大山", LandmarkType.Mountain);
        S("松江", "松江城", LandmarkType.Castle, 1);
        S("福山", "鞆の浦 常夜燈", LandmarkType.Lighthouse, 1);
        S("広島", "厳島神社", LandmarkType.Torii, 2);
        S("山口", "錦帯橋", LandmarkType.Bridge, 1);
        S("下関", "赤間神宮", LandmarkType.Torii);
        // 四国
        S("高松", "栗林公園", LandmarkType.Garden);
        S("徳島", "鳴門の渦潮", LandmarkType.Lake, 2);
        S("川之江", "金刀比羅宮", LandmarkType.Temple);
        S("松山", "道後温泉本館", LandmarkType.Temple, 1);
        S("今治", "来島海峡大橋", LandmarkType.Bridge, 2);
        S("高知", "高知城", LandmarkType.Castle);
        // 九州
        S("北九州", "小倉城", LandmarkType.Castle);
        S("福岡", "福岡タワー", LandmarkType.Tower, 1);
        S("福岡", "太宰府天満宮", LandmarkType.Temple);
        S("鳥栖", "吉野ヶ里遺跡", LandmarkType.Monument, 1);
        S("長崎", "大浦天主堂", LandmarkType.ClockTower, 1);
        S("熊本", "熊本城", LandmarkType.Castle, 1);
        S("大分", "別府温泉", LandmarkType.Volcano, 1);
        S("宮崎", "青島神社", LandmarkType.Torii);
        S("鹿児島", "桜島", LandmarkType.Volcano);
        // 奄美・沖縄
        S("奄美", "奄美大島の海", LandmarkType.Lake);
        S("那覇", "首里城", LandmarkType.Castle, 2);
    }

    public static IEnumerable<Spot> At(City city) => All.Where(s => s.City == city);
}
