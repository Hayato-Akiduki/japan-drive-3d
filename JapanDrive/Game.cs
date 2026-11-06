using System.Drawing;

namespace JapanDrive;

public enum GameMode { Tour, Free }

/// <summary>ゲームの状態とルール（描画とは独立）。</summary>
public sealed class Game
{
    public const double TankSize = 50;       // L
    public const double FuelPrice = 175;     // 円/L
    public const double MaxSpeed = 160;      // km/h
    public const double LegalSpeed = 100;    // km/h
    private const double GameMinutesPerSecond = 3.0; // 現実1秒 = ゲーム内3分

    private readonly Random rng = new();

    public Game(GameMode mode = GameMode.Tour)
    {
        Mode = mode;
        StartCity = MapData.Get("東京");
        AtCity = StartCity;
        From = To = StartCity;
        StartMinutes = GameMinutes = 8 * 60;
        GoalCount = MapData.Cities.Count(c => c.IsGoal && c != StartCity);
        if (mode == GameMode.Tour)
        {
            AddLog("東京を出発！ ★の都市をすべて巡って東京へ戻ろう。");
            AddLog("数字キーか地図クリックで道路を選び、↑で走り出します。");
        }
        else
        {
            AddLog("フリードライブ：目的地なしで気ままに走ろう。");
        }
    }

    public GameMode Mode { get; }

    /// <summary>分岐で止まらず、選んでおいた道路（未選択なら一番まっすぐな道路）へ自動で進む。</summary>
    public bool FreeDrive { get; set; }
    /// <summary>次の分岐で進む道路（走行中に予約しておく）。</summary>
    public Road? PreferredNext { get; set; }
    /// <summary>次の都市で一度停車する（寄り道など）。</summary>
    public bool StopAtNextCity { get; set; }
    /// <summary>最後に走った道路（分岐で「まっすぐ」を判定するため）。</summary>
    public Road? LastRoad { get; private set; }
    /// <summary>次の分岐の都市（停車中ならその都市）。</summary>
    public City JunctionCity => AtCity ?? To;

    public City StartCity { get; }
    public int GoalCount { get; }

    /// <summary>都市に停車中ならその都市。走行中は null。</summary>
    public City? AtCity { get; private set; }
    public Road? Road { get; private set; }
    public City From { get; private set; }
    public City To { get; private set; }
    public double Traveled { get; private set; }

    public double Speed { get; private set; }
    public double Fuel { get; private set; } = TankSize;
    public double GameMinutes { get; private set; }
    public double StartMinutes { get; }
    public double Odometer { get; private set; }

    public int TollPaid { get; private set; }
    public int FuelPaid { get; private set; }
    public int FinePaid { get; private set; }
    public int RescuePaid { get; private set; }
    public int RepairPaid { get; private set; }
    public int Crashes { get; private set; }
    public int TotalPaid => TollPaid + FuelPaid + FinePaid + RescuePaid + RepairPaid;
    public int Violations { get; private set; }

    public HashSet<City> Visited { get; } = new();
    public City? NavTarget { get; private set; }
    public bool AutoDrive { get; set; } = true;
    public bool FastForward { get; set; }
    public bool Accel { get; set; }
    public bool Brake { get; set; }
    public bool Cleared { get; private set; }
    public List<string> Log { get; } = new();

    /// <summary>フェリーに乗っている（水陸両用車は自力で海を渡るので false）。</summary>
    public bool OnFerry => Road?.Type == RoadType.Ferry && !Amphibious;
    /// <summary>海上の航路を進んでいる。</summary>
    public bool OnSea => Road?.Type == RoadType.Ferry;
    public double TimeScale => OnSea ? 4 : FastForward ? 3 : 1;

    /// <summary>車両ごとの最高速度（戦車は遅いなど）。</summary>
    public double SpeedCap { get; set; } = MaxSpeed;
    /// <summary>水陸両用車に乗っている（フェリー航路を自力で渡る）。</summary>
    public bool Amphibious { get; set; }
    /// <summary>燃費の悪さ（1 = 普通の車）。</summary>
    public double FuelFactor { get; set; } = 1;
    public double Remaining => Road == null ? 0 : Road.Km - Traveled;

    // ---------------- 更新 ----------------

    public void Update(double dt)
    {
        if (Cleared || Road == null)
        {
            Speed = 0;
            return; // 都市に停車中は時間が止まる（考える時間）
        }

        double gameDt = dt * GameMinutesPerSecond * TimeScale; // ゲーム内の分

        if (OnFerry)
        {
            Speed = 30;
        }
        else
        {
            if (Accel) Speed += 30 * dt;
            else if (Brake) Speed -= 80 * dt;
            else Speed -= 3 * dt;
            Speed = Math.Clamp(Speed, 0, Math.Min(MaxSpeed, SpeedCap));
        }

        GameMinutes += gameDt;
        double dist = Speed * gameDt / 60.0;

        if (!OnFerry)
        {
            Fuel -= dist / KmPerLiter(Speed) * FuelFactor;
            if (Fuel <= 0) OutOfFuel();
            CheckSpeeding(gameDt);
        }

        Traveled += dist;
        Odometer += dist;
        if (Traveled >= Road.Km) Arrive(To);
    }

    public static double KmPerLiter(double speed) => Math.Max(8, 18 - Math.Max(0, speed - 80) * 0.1);

    private void CheckSpeeding(double gameDt)
    {
        if (Speed <= 120) return;
        double perMinute = Speed >= 140 ? 0.02 : 0.008;
        if (rng.NextDouble() >= perMinute * gameDt) return;

        int fine = Speed >= 140 ? 35000 : Speed >= 130 ? 25000 : 18000;
        FinePaid += fine;
        Violations++;
        AddLog($"【検挙】{Speed:0}km/hでスピード違反！ 反則金 {fine:N0}円、30分のロス。");
        Speed = 0;
        GameMinutes += 30;
    }

    private void OutOfFuel()
    {
        Fuel = 10;
        Speed = 0;
        GameMinutes += 90;
        RescuePaid += 15000;
        AddLog("【ガス欠】ロードサービスで10L給油（15,000円・1時間30分）。");
    }

    private void Arrive(City city)
    {
        LastRoad = Road;
        Road = null;
        AtCity = city;
        From = To = city;
        Traveled = 0;

        if (city.IsGoal && city != StartCity && Visited.Add(city))
            AddLog($"★ {city.Name} に到達！（{Visited.Count}/{GoalCount}）");

        if (Mode == GameMode.Tour && city == StartCity && Visited.Count == GoalCount)
        {
            Cleared = true;
            Speed = 0;
            NavTarget = null;
            AddLog("日本一周 達成！おめでとう！");
            return;
        }

        if (NavTarget == city)
        {
            NavTarget = null;
            Speed = 0;
            AddLog($"{city.Name} に到着しました（ナビ終了）。");
            return;
        }

        if (StopAtNextCity)
        {
            StopAtNextCity = false;
            Speed = 0;
            AddLog($"{city.Name} で高速道路を降ります。");
            return;
        }

        if (FreeDrive || (NavTarget != null && AutoDrive))
        {
            if (Fuel < TankSize * 0.25)
            {
                Speed = 0;
                AddLog($"{city.Name}：燃料が少ないので停車しました。[F]で給油。");
                return;
            }
            var next = FreeDrive ? PlannedRoad() : NextNavRoad();
            if (next != null)
            {
                Depart(next, keepSpeed: true);
                return;
            }
        }

        Speed = 0;
        AddLog($"{city.Name} に到着。次の道路を選んでください。");
    }

    // ---------------- 操作 ----------------

    public void Depart(Road road, bool keepSpeed = false)
    {
        if (AtCity == null || Cleared || !AtCity.Roads.Contains(road)) return;
        var from = AtCity;
        From = from;
        To = road.Other(from);
        Road = road;
        Traveled = 0;
        AtCity = null;
        PreferredNext = null;
        if (!keepSpeed) Speed = 0;

        if (road.Type == RoadType.Ferry && Amphibious)
        {
            AddLog($"{road.Name}の航路を自力で航行します（{To.Name}まで{road.Km:0}km・料金なし）。");
            return;
        }
        TollPaid += road.Toll;

        if (road.Type == RoadType.Ferry)
        {
            GameMinutes += 30;
            AddLog($"{road.Name}に乗船（{road.Toll:N0}円）。{To.Name}まで約{road.Km / 30:0.0}時間。");
        }
        else
        {
            AddLog($"{from.Name}→{To.Name}：{road.Name}（{road.Km:0}km・{road.Toll:N0}円）");
        }
    }

    /// <summary>3D版：他の車に接触した。</summary>
    public void Crash()
    {
        if (Road == null) return;
        Crashes++;
        RepairPaid += 30000;
        GameMinutes += 20;
        Speed *= 0.3;
        AddLog("【接触事故】修理代 30,000円、20分のロス。車間距離に注意！");
    }

    /// <summary>3D版：ガードレールにこすった時などの減速。</summary>
    public void Slow(double factor)
    {
        if (!OnFerry) Speed *= factor;
    }

    /// <summary>指定した都市に停車した状態にする（確認・デバッグ用）。</summary>
    public void PlaceAt(City city)
    {
        Road = null;
        AtCity = city;
        From = To = city;
        Traveled = 0;
    }

    /// <summary>時刻を設定する（確認・デバッグ用）。</summary>
    public void SetClock(double minutes) => GameMinutes = minutes;

    public void Refuel()
    {
        if (AtCity == null)
        {
            AddLog("給油は都市（SA）に停車中のみできます。");
            return;
        }
        double liters = TankSize - Fuel;
        if (liters < 0.5)
        {
            AddLog("燃料は満タンです。");
            return;
        }
        int cost = (int)Math.Round(liters * FuelPrice);
        FuelPaid += cost;
        Fuel = TankSize;
        GameMinutes += 10;
        AddLog($"{liters:0.0}L 給油しました（{cost:N0}円・10分）。");
    }

    public void UTurn()
    {
        if (Road == null || OnFerry) return;
        if (Speed > 40)
        {
            AddLog("Uターンするには 40km/h 以下に減速してください。");
            return;
        }
        (From, To) = (To, From);
        Traveled = Road.Km - Traveled;
        Speed = 0;
        GameMinutes += 15;
        AddLog($"次のICで折り返し、{To.Name}方面へ向かいます（15分）。");
    }

    public void SetNav(City? target)
    {
        if (target == null)
        {
            if (NavTarget != null) AddLog("ナビを解除しました。");
            NavTarget = null;
            return;
        }
        if (AtCity == target) return;
        NavTarget = target;
        var path = ShortestPath(NavOrigin, target, out double km);
        if (path == null) return;
        km += Remaining;
        AddLog($"ナビ設定：{target.Name}（約{km:0}km）");
    }

    // ---------------- ナビ ----------------

    public City NavOrigin => AtCity ?? To;

    public List<City>? NavPath() =>
        NavTarget == null ? null : ShortestPath(NavOrigin, NavTarget, out _);

    public Road? NextNavRoad()
    {
        if (AtCity == null) return null;
        var path = NavPath();
        if (path == null || path.Count < 2) return null;
        return AtCity.Roads.Where(r => r.Other(AtCity) == path[1]).MinBy(r => r.Km);
    }

    /// <summary>次の分岐で実際に進む道路（ナビ ＞ 予約 ＞ まっすぐ）。</summary>
    public Road? PlannedRoad()
    {
        var city = JunctionCity;
        if (NavTarget != null && AutoDrive && NavTarget != city)
        {
            var path = ShortestPath(city, NavTarget, out _);
            if (path != null && path.Count >= 2)
            {
                var r = city.Roads.Where(x => x.Other(city) == path[1]).MinBy(x => x.Km);
                if (r != null) return r;
            }
        }
        if (PreferredNext != null && city.Roads.Contains(PreferredNext)) return PreferredNext;
        return StraightestRoad(city, Road ?? LastRoad);
    }

    /// <summary>来た道路から見た曲がる角度（度、正が右・負が左）。</summary>
    public static double TurnAngle(City city, Road? incoming, Road outgoing)
    {
        if (incoming == null) return 0;
        var prev = ToWorld(incoming.Other(city));
        var here = ToWorld(city);
        var next = ToWorld(outgoing.Other(city));
        double a1 = Math.Atan2(here.Y - prev.Y, here.X - prev.X);
        double a2 = Math.Atan2(next.Y - here.Y, next.X - here.X);
        double d = (a2 - a1) * 180 / Math.PI;
        while (d > 180) d -= 360;
        while (d <= -180) d += 360;
        return d;
    }

    public static string TurnLabel(double angle) =>
        Math.Abs(angle) < 35 ? "直進" : Math.Abs(angle) > 150 ? "戻る" : angle > 0 ? "右" : "左";

    public static Road? StraightestRoad(City city, Road? incoming)
    {
        if (city.Roads.Count == 0) return null;
        if (incoming == null || !city.Roads.Contains(incoming)) return city.Roads[0];
        var others = city.Roads.Where(r => r != incoming).ToList();
        if (others.Count == 0) return incoming; // 行き止まりは引き返す
        return others.MinBy(r => Math.Abs(TurnAngle(city, incoming, r)));
    }

    public static List<City>? ShortestPath(City from, City to, out double total)
    {
        var dist = MapData.Cities.ToDictionary(c => c, _ => double.PositiveInfinity);
        var prev = new Dictionary<City, City>();
        var open = new HashSet<City>(MapData.Cities);
        dist[from] = 0;

        while (open.Count > 0)
        {
            var u = open.MinBy(c => dist[c])!;
            if (double.IsPositiveInfinity(dist[u])) break;
            open.Remove(u);
            if (u == to) break;
            foreach (var r in u.Roads)
            {
                var v = r.Other(u);
                double nd = dist[u] + r.Km;
                if (nd < dist[v])
                {
                    dist[v] = nd;
                    prev[v] = u;
                }
            }
        }

        total = dist[to];
        if (double.IsPositiveInfinity(total)) return null;
        var path = new List<City> { to };
        while (path[^1] != from) path.Add(prev[path[^1]]);
        path.Reverse();
        return path;
    }

    // ---------------- 表示用 ----------------

    public static PointF ToWorld(double lat, double lon) =>
        new((float)((lon - 137.0) * Math.Cos(36 * Math.PI / 180)), (float)(37.0 - lat));

    public static PointF ToWorld(City c) => ToWorld(c.Lat, c.Lon);

    public PointF CarWorld
    {
        get
        {
            if (Road == null) return ToWorld(From);
            var a = ToWorld(From);
            var b = ToWorld(To);
            float t = (float)Math.Clamp(Traveled / Road.Km, 0, 1);
            return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
    }

    public static string FormatClock(double minutes)
    {
        int total = (int)minutes;
        return $"{total / 1440 + 1}日目 {total % 1440 / 60:00}:{total % 60:00}";
    }

    public static string FormatDuration(double minutes)
    {
        int total = (int)minutes;
        int d = total / 1440, h = total % 1440 / 60, m = total % 60;
        return d > 0 ? $"{d}日{h}時間{m}分" : $"{h}時間{m}分";
    }

    public void AddLog(string text)
    {
        int t = (int)GameMinutes;
        Log.Add($"{t % 1440 / 60:00}:{t % 60:00} {text}");
        if (Log.Count > 100) Log.RemoveAt(0);
    }
}
