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
        S("稚内", "宗谷岬", LandmarkType.Monument);
        S("網走", "知床五胡", LandmarkType.Lake);
        S("旭川", "旭岳", LandmarkType.Mountain);
        S("根室", "納沙布岬灯台", LandmarkType.Lighthouse);
        S("札幌", "札幌市時計台", LandmarkType.ClockTower);
        S("釧路", "釧路湿原", LandmarkType.Lake, 1);
        S("帯広", "幸福駅", LandmarkType.Monument, 2);
        S("千歳", "支笏湖", LandmarkType.Lake);
        S("室蘭", "地球岬灯台", LandmarkType.Lighthouse);
        S("函館", "五稜郭タワー", LandmarkType.Tower, 1);
        // 東北
        S("青森", "八甲田山", LandmarkType.Mountain);
        S("弘前", "弘前城", LandmarkType.Castle);
        S("八戸", "蕪島神社", LandmarkType.Torii);
        S("秋田", "入道埼灯台", LandmarkType.Lighthouse);
        S("盛岡", "岩手山", LandmarkType.Mountain);
        S("宮古", "浄土ヶ浜・魹ヶ崎灯台", LandmarkType.Lighthouse);
        S("酒田", "鳥海山", LandmarkType.Mountain);
        S("仙台", "伊達政宗騎馬像", LandmarkType.Monument);
        S("山形", "山寺（立石寺）", LandmarkType.Temple);
        S("会津若松", "鶴ヶ城", LandmarkType.Castle);
        S("郡山", "猪苗代湖", LandmarkType.Lake);
        S("いわき", "塩屋埼灯台", LandmarkType.Lighthouse);
        // 関東・甲信越
        S("新潟", "萬代橋", LandmarkType.Bridge);
        S("上越", "高田城", LandmarkType.Castle);
        S("日光", "華厳の滝・中禅寺湖", LandmarkType.Lake);
        S("長野", "善光寺", LandmarkType.Temple);
        S("宇都宮", "日光東照宮", LandmarkType.Pagoda);
        S("水戸", "偕楽園", LandmarkType.Garden);
        S("高崎", "高崎白衣大観音", LandmarkType.Buddha);
        S("松本", "松本城", LandmarkType.Castle, 1);
        S("岡谷", "諏訪大社", LandmarkType.Torii);
        S("東京", "東京タワー", LandmarkType.Tower);
        S("東京", "浅草寺", LandmarkType.Temple);
        S("甲府", "武田神社", LandmarkType.Torii);
        S("千葉", "成田山新勝寺", LandmarkType.Pagoda);
        S("横浜", "横浜マリンタワー", LandmarkType.Tower, 1);
        S("木更津", "海ほたるPA", LandmarkType.Bridge, 2);
        S("館山", "野島埼灯台", LandmarkType.Lighthouse);
        S("下田", "爪木崎灯台", LandmarkType.Lighthouse);
        S("八丈島", "八丈富士・南原千畳敷", LandmarkType.Volcano, 1);
        S("小笠原", "父島・南島ラピエ", LandmarkType.Lake);
        // 北陸・東海
        S("輪島", "白米千枚田", LandmarkType.Garden);
        S("富山", "立山連峰", LandmarkType.Mountain);
        S("金沢", "兼六園", LandmarkType.Garden);
        S("高山", "白川郷 合掌造り", LandmarkType.Monument, 1);
        S("福井", "丸岡城", LandmarkType.Castle);
        S("敦賀", "氣比神宮", LandmarkType.Torii);
        S("名古屋", "名古屋城", LandmarkType.Castle);
        S("静岡", "富士山", LandmarkType.Mountain, 1);
        S("浜松", "浜名湖", LandmarkType.Lake);
        S("伊勢", "伊勢神宮", LandmarkType.Torii);
        // 近畿
        S("舞鶴", "天橋立", LandmarkType.Bridge, 1);
        S("米原", "彦根城", LandmarkType.Castle);
        S("京都", "伏見稲荷大社", LandmarkType.Torii, 1);
        S("京都", "東寺 五重塔", LandmarkType.Pagoda);
        S("姫路", "姫路城", LandmarkType.Castle);
        S("大阪", "大阪城", LandmarkType.Castle);
        S("大阪", "通天閣", LandmarkType.Tower, 2);
        S("神戸", "神戸ポートタワー", LandmarkType.Tower, 3);
        S("奈良", "東大寺 大仏殿", LandmarkType.Buddha);
        S("和歌山", "和歌山城", LandmarkType.Castle);
        S("新宮", "熊野那智大社", LandmarkType.Pagoda);
        S("白浜", "円月島・千畳敷", LandmarkType.Lake);
        // 中国
        S("鳥取", "鳥取砂丘", LandmarkType.Garden);
        S("松江", "松江城", LandmarkType.Castle, 1);
        S("米子", "大山", LandmarkType.Mountain);
        S("出雲", "出雲大社", LandmarkType.Torii);
        S("浜田", "石見銀山", LandmarkType.Temple);
        S("岡山", "後楽園", LandmarkType.Garden);
        S("福山", "鞆の浦 常夜燈", LandmarkType.Lighthouse, 1);
        S("広島", "厳島神社", LandmarkType.Torii, 2);
        S("山口", "錦帯橋", LandmarkType.Bridge, 1);
        S("下関", "赤間神宮", LandmarkType.Torii);
        // 四国
        S("高松", "栗林公園", LandmarkType.Garden);
        S("徳島", "鳴門の渦潮", LandmarkType.Lake, 2);
        S("今治", "来島海峡大橋", LandmarkType.Bridge, 2);
        S("川之江", "金刀比羅宮", LandmarkType.Temple);
        S("松山", "道後温泉本館", LandmarkType.Temple, 1);
        S("高知", "高知城", LandmarkType.Castle);
        S("室戸", "室戸岬灯台", LandmarkType.Lighthouse);
        S("宇和島", "宇和島城", LandmarkType.Castle);
        S("四万十", "四万十川 沈下橋", LandmarkType.Bridge);
        // 九州
        S("北九州", "小倉城", LandmarkType.Castle);
        S("福岡", "福岡タワー", LandmarkType.Tower, 1);
        S("福岡", "太宰府天満宮", LandmarkType.Temple);
        S("鳥栖", "吉野ヶ里遺跡", LandmarkType.Monument, 1);
        S("大分", "別府温泉", LandmarkType.Volcano, 1);
        S("佐世保", "九十九島", LandmarkType.Lake);
        S("阿蘇", "阿蘇山", LandmarkType.Volcano);
        S("熊本", "熊本城", LandmarkType.Castle, 1);
        S("長崎", "大浦天主堂", LandmarkType.ClockTower, 1);
        S("延岡", "高千穂峡", LandmarkType.Bridge, 1);
        S("宮崎", "青島神社", LandmarkType.Torii);
        S("鹿児島", "桜島", LandmarkType.Volcano);
        S("指宿", "開聞岳", LandmarkType.Mountain, 1);
        // 南西諸島・沖縄
        S("屋久島", "宮之浦岳・縄文杉", LandmarkType.Mountain);
        S("奄美", "奄美大島の海", LandmarkType.Lake);
        S("名護", "美ら海・万座毛", LandmarkType.Lake);
        S("那覇", "首里城", LandmarkType.Castle, 2);
        S("宮古島", "東平安名崎灯台", LandmarkType.Lighthouse);
        S("石垣", "川平湾", LandmarkType.Lake);
        S("与那国", "西崎（日本最西端の碑）", LandmarkType.Lighthouse, 1);
        // 樺太（サハリン）
        S("大泊", "中知床岬（アニワ灯台）", LandmarkType.Lighthouse, 1);
        S("豊原", "旧樺太庁博物館", LandmarkType.ClockTower, 1);
        S("真岡", "真岡港・九人の乙女碑", LandmarkType.Monument);
        S("知取", "突阻山・東海岸灯台", LandmarkType.Mountain);
        S("恵須取", "恵須取炭鉱・間宮海峡展望台", LandmarkType.Monument, 1);
        S("敷香", "多来加湾・ツンドラ湿原", LandmarkType.Lake, 1);
        S("オハ", "北樺太油田記念塔", LandmarkType.Tower, 2);
        // 千島列島・カムチャツカ・極東ロシア
        S("国後", "爺爺岳", LandmarkType.Volcano);
        S("択捉", "単冠湾・散布山", LandmarkType.Volcano, 1);
        S("幌筵", "阿頼度山（親子場山）", LandmarkType.Volcano);
        S("カムチャツカ", "アヴァチャ山・ペトロパブロフスク港", LandmarkType.Volcano);
        S("ウラジオストク", "ルースキー島大橋・金角湾", LandmarkType.Bridge, 2);
        S("ハバロフスク", "アムール川鉄橋・ウスペンスキー聖堂", LandmarkType.Bridge, 1);
        // 韓国・北朝鮮（対馬・朝鮮半島・済州島）
        S("対馬", "万関橋・和多都美神社", LandmarkType.Bridge, 1);
        S("釜山", "広安大橋・海雲台", LandmarkType.Bridge, 2);
        S("済州", "漢拏山", LandmarkType.Volcano);
        S("大邱", "海印寺 八万大蔵経", LandmarkType.Temple);
        S("光州", "無等山", LandmarkType.Mountain);
        S("大田", "エキスポ科学公園・万博塔", LandmarkType.Tower, 1);
        S("江陵", "鏡浦台・正東津", LandmarkType.Garden);
        S("ソウル", "Nソウルタワー", LandmarkType.Tower, 3);
        S("ソウル", "景福宮 勤政殿", LandmarkType.Castle, 2);
        S("仁川", "仁川大橋", LandmarkType.Bridge, 2);
        S("平壌", "主体思想塔・凱旋門", LandmarkType.Tower, 2);
        // 中国大陸
        S("瀋陽", "瀋陽故宮", LandmarkType.Castle, 2);
        S("大連", "星海広場・旅順港", LandmarkType.Bridge, 2);
        S("北京", "紫禁城（故宮）・天壇祈年殿", LandmarkType.Castle, 2);
        S("北京", "万里の長城（八達嶺）", LandmarkType.Monument, 1);
        S("青島", "桟橋（回瀾閣）・嶗山", LandmarkType.Pagoda, 1);
        S("上海", "東方明珠テレビ塔・外灘", LandmarkType.Tower, 3);
        S("福州", "三坊七巷・鼓山湧泉寺", LandmarkType.Temple, 1);
        S("アモイ", "コロンス島（鼓浪嶼）・日光岩", LandmarkType.ClockTower, 1);
        S("香港", "ヴィクトリア・ピーク＆天壇大仏", LandmarkType.Buddha);
        // 台湾・フィリピン
        S("台北", "台北101", LandmarkType.Tower, 1);
        S("台北", "九份・龍山寺", LandmarkType.Temple, 1);
        S("台中", "日月潭", LandmarkType.Lake);
        S("花蓮", "太魯閣渓谷", LandmarkType.Bridge, 1);
        S("台南", "赤崁楼・安平古堡", LandmarkType.Castle, 2);
        S("高雄", "蓮池潭 龍虎塔", LandmarkType.Pagoda, 1);
        S("台東", "三仙台 八拱橋", LandmarkType.Bridge, 1);
        S("マニラ", "イントラムロス・リサール記念碑", LandmarkType.Monument, 2);
    }

    public static IEnumerable<Spot> At(City city) => All.Where(s => s.City == city);
}
