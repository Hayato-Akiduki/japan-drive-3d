using System.Numerics;
using Raylib_cs;

namespace JapanDrive;

public sealed partial class App
{
    private Game game = new(GameMode.Free) { FreeDrive = true };
    private readonly Scene scene = new();
    private readonly Traffic traffic = new();
    private readonly Random rng = new();

    private bool title = true;
    private float titleTime;

    private double playerS;
    private float playerU = Traffic.DrivingLane;
    private float steerYaw;
    private bool cockpit;
    private bool bigMap;
    private bool stampBook;
    private bool upWasDown;
    private float crashCooldown;
    private float shake;
    private float resetConfirm;
    private Road? lastRoad;
    private Camera3D camera;
    private float time;

    // ---- 名所スタンプラリー ----
    private sealed class Detour
    {
        public required Spot Spot;
        public double EndS;        // 一般道の終点（停車位置）
        public double LandmarkS;   // 名所の中心
        public double Kmh;
        public bool Arrived;
        public float ArrivedTime;
    }

    private Detour? detour;
    private Spot? pendingDetour;              // 次の都市で降りて寄る名所
    private readonly Dictionary<string, double> stamps = new();   // 名所キー → 押した時刻
    private Spot? popupSpot;
    private bool popupNew;
    private float popupTime = 99;

    // ---- 画面切り替えのフェード ----
    private float fade;
    private int fadeDir;
    private Action? fadeAction;

    // 確認用のデモ実行：--shot ファイル名 [--seconds 秒] [--showcase] [--hour 時刻] [--title] [--detour 名所番号] [--tour]
    private string? shotFile;
    private float shotSeconds = 8;
    private bool showcase;
    private float demoTime;
    private int demoDetour = -1;
    private float? demoHour;
    private bool demoTour;
    private bool demoBook;
    private VehicleKind? demoVehicle;
    private string? demoAt, demoTake;
    private bool demoFire, demoDive;

    public static void Main(string[] args)
    {
        var app = new App();
        bool stayOnTitle = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shot" when i + 1 < args.Length: app.shotFile = args[++i]; break;
                case "--seconds" when i + 1 < args.Length: app.shotSeconds = float.Parse(args[++i]); break;
                case "--showcase": app.showcase = true; break;
                case "--hour" when i + 1 < args.Length: app.demoHour = float.Parse(args[++i]); break;
                case "--title": stayOnTitle = true; break;
                case "--detour" when i + 1 < args.Length: app.demoDetour = int.Parse(args[++i]); break;
                case "--tour": app.demoTour = true; break;
                case "--book": app.demoBook = true; break;
                case "--persist": app.demoPersist = true; break;
                case "--update-test": app.demoUpdate = true; break;
                case "--update-restart": app.demoUpdate = app.demoRestart = true; break;
                case "--vehicle" when i + 1 < args.Length:
                    app.demoVehicle = args[++i] switch
                    {
                        "tank" => VehicleKind.Tank, "amph" => VehicleKind.Amphibious,
                        "horse" => VehicleKind.Horse, "hikyaku" => VehicleKind.Hikyaku,
                        "heli" => VehicleKind.Helicopter, "plane" => VehicleKind.Airplane, _ => VehicleKind.Sports,
                    };
                    break;
                case "--at" when i + 1 < args.Length: app.demoAt = args[++i]; break;
                case "--take" when i + 1 < args.Length: app.demoTake = args[++i]; break;
                case "--fire": app.demoFire = true; break;
                case "--dive": app.demoDive = true; break;
                case "--gunner": app.cockpit = true; break;
                case "--aim" when i + 1 < args.Length: app.turretTarget = float.Parse(args[++i]); break;
                case "--accel" when i + 1 < args.Length: app.demoAccel = float.Parse(args[++i]); break;
                case "--climb" when i + 1 < args.Length: app.demoClimb = float.Parse(args[++i]); break;
                case "--latlon" when i + 1 < args.Length:
                    var ll = args[++i].Split(',');
                    app.demoLatLon = (double.Parse(ll[0]), double.Parse(ll[1]));
                    break;
                case "--bigmap": app.demoBigMap = true; break;
                case "--steer" when i + 1 < args.Length: app.demoSteer = float.Parse(args[++i]); break;
                case "--heading" when i + 1 < args.Length: app.heading = float.Parse(args[++i]); break;
            }
        }
        if (app.shotFile != null && !stayOnTitle) app.pendingStart = app.demoTour ? GameMode.Tour : GameMode.Free;
        app.Run();
    }

    private GameMode? pendingStart;

    private void Run()
    {
        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1280, 720, "日本一周ドライブ 3D");
        int mon = Raylib.GetCurrentMonitor();
        int mw = Raylib.GetMonitorWidth(mon), mh = Raylib.GetMonitorHeight(mon);
        if (mw > 0 && mh > 0)
        {
            int w = (int)(mw * 0.8), h = (int)(mh * 0.8);
            Raylib.SetWindowSize(w, h);
            Raylib.SetWindowPosition((mw - w) / 2, (mh - h) / 2);
        }
        Raylib.SetExitKey(KeyboardKey.Null);
        SetWindowIcon();
        Raylib.SetTargetFPS(60);

        // 両面描画にする（rlglは描画をまとめて後で実行するので、途中で切り替えると反映がずれるため）
        Rlgl.DisableBackfaceCulling();
        Draw.LoadFont();
        Audio.Init();
        LoadSave();
        Updater.CleanupOld();
        if (shotFile == null || demoUpdate) Updater.StartCheck();
        traffic.Reset(playerS);
        camera = new Camera3D
        {
            Up = Vector3.UnitY,
            FovY = 60,
            Projection = CameraProjection.Perspective,
        };
        if (pendingStart is GameMode m) StartGame(m);

        while (!Raylib.WindowShouldClose())
        {
            float dt = Math.Min(Raylib.GetFrameTime(), 0.05f);
            time += dt;
            Update(dt);
            UpdateSound(dt);
            Render();
            if (shotFile != null && (demoTime += dt) > shotSeconds)
            {
                Raylib.TakeScreenshot(shotFile);
                break;
            }
            if (restartRequested)
            {
                // 新しい版で起動し直す
                Updater.Restart();
                break;
            }
        }

        SaveNow();
        Audio.Shutdown();
        Raylib.UnloadFont(Draw.Font);
        Raylib.CloseWindow();
    }

    /// <summary>ウィンドウ（タイトルバー・タスクバー）のアイコン。</summary>
    private static void SetWindowIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("icon.png");
        if (stream == null) return;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var image = Raylib.LoadImageFromMemory(".png", ms.ToArray());
        Raylib.SetWindowIcon(image);
        Raylib.UnloadImage(image);
    }

    private void StartGame(GameMode mode)
    {
        game = new Game(mode) { FreeDrive = true };
        if (demoHour is float h) game.SetClock(h * 60);
        playerS = 0;
        playerU = Traffic.DrivingLane;
        lastRoad = null;
        scene.Reset();
        scene.LocalEndS = double.MaxValue;
        scene.PlayerS = 0;
        traffic.Reset(0);
        bigMap = stampBook = false;
        detour = null;
        pendingDetour = null;
        title = false;
        pstate = PlayerState.Road;
        playerY = 0;
        shells.Clear();
        effects.Clear();
        turretYaw = 0;
        if (shotFile == null) turretTarget = 0;
        if (demoVehicle is VehicleKind dv) playerKind = dv;
        if (mode != GameMode.Free && IsFlyer(playerKind)) playerKind = VehicleKind.Sports;
        clearRecorded = false;
        lastViolations = lastCrashes = lastToll = 0;
        Audio.Play(Audio.Select);
        ApplyVehicle();
        freeRoam = mode == GameMode.Free;
        if (freeRoam) StartFree();
        else if (demoAt != null) game.PlaceAt(MapData.Get(demoAt));
        if (!freeRoam) game.AddLog("Q/E か数字キーで次の分岐を選べます（選ばなければ直進）。P で名所に寄り道。");
        if (shotFile != null && demoDetour >= 0) StartDetour(Spots.All[Math.Clamp(demoDetour, 0, Spots.All.Count - 1)]);
        if (demoBook)
        {
            // 確認用：いくつかスタンプを押した状態でスタンプ帳を開く
            for (int i = 0; i < Spots.All.Count; i += 3) stamps[Spots.All[i].Key] = 8 * 60 + i * 37;
            stampBook = true;
        }
    }

    private float CurrentKmh => freeRoam ? freeKmh : detour != null ? (float)detour.Kmh : (float)game.Speed;

    // ================= 更新 =================

    private void Update(float dt)
    {
        Draw.Ui = Raylib.GetScreenHeight() / 900f;
        HandleAudioKeys();
        if (title)
        {
            UpdateTitle(dt);
            return;
        }

        HandleKeys();
        NotifyUpdateInGame();
        UpdateFade(dt);
        popupTime += dt;
        if (bigMap || stampBook)
        {
            if (bigMap)
            {
                if (freeRoam) HandleFreeMapMouse();
                else HandleBigMapMouse();
            }
            UpdateCamera(dt);
            return; // 地図・スタンプ帳を見ている間は一時停止
        }

        if (freeRoam)
        {
            UpdateFree(dt);
            return;
        }

        if (detour != null)
        {
            UpdateDetour(dt);
            UpdateCombat(dt, 0);
            UpdateCamera(dt);
            return;
        }

        bool up = Raylib.IsKeyDown(KeyboardKey.Up) || Raylib.IsKeyDown(KeyboardKey.W);
        if (shotFile != null)
        {
            if (game.AtCity != null && demoTime < 0.5f && game.Road == null)
                game.Depart(game.AtCity.Roads.FirstOrDefault(r => r.Name == demoTake) ?? game.AtCity.Roads[Math.Min(3, game.AtCity.Roads.Count - 1)]);
            up = demoTime < 3.5f || demoTake != null;
        }
        if (up && !upWasDown && game.AtCity != null && fadeDir == 0)
        {
            var road = game.PlannedRoad();
            if (road != null) game.Depart(road);
            else game.AddLog("先に数字キー（または M の地図）で道路を選んでください。");
        }
        upWasDown = up;
        game.Accel = up;
        game.Brake = Raylib.IsKeyDown(KeyboardKey.Down) || Raylib.IsKeyDown(KeyboardKey.S);

        game.Update(dt);
        if (game.Road != null && game.Road != lastRoad)
        {
            scene.BeginRoad(playerS, game.Road);
            lastRoad = game.Road;
        }

        // 予約した寄り道：都市に着いたら一般道へ
        if (pendingDetour != null && game.AtCity == pendingDetour.City && fadeDir == 0)
        {
            var spot = pendingDetour;
            pendingDetour = null;
            BeginFade(() => StartDetour(spot));
        }
        else if (pendingDetour != null && game.AtCity != null && game.AtCity != pendingDetour.City)
        {
            pendingDetour = null; // 別の都市で止まった
        }

        // 見た目の移動（ゲーム内の距離は時間圧縮されているが、画面上は実際の速さで走る）
        float ms = game.OnFerry ? 9f : (float)(game.Speed / 3.6);
        playerS += ms * dt;

        float steer = SteerInput();
        if (shotFile != null && demoDive && demoTime > 2.0f) steer = -1;
        SteerPlayer(dt, steer, ms);
        UpdateVehicleState(dt, ms);
        steerYaw += (-steer * Math.Min(1f, ms / 10f) * 6f - steerYaw) * Math.Min(1, dt * 6);

        scene.PlayerS = playerS;
        if (!game.OnSea) traffic.Update(dt, playerS, playerU, ms, PlayerLength, pstate == PlayerState.Road);

        crashCooldown -= dt;
        if (!game.OnSea && pstate == PlayerState.Road && crashCooldown <= 0 && game.Road != null)
        {
            var hit = traffic.CheckCollision(playerS, playerU, PlayerLength, PlayerHalfWidth);
            if (hit != null && playerKind == VehicleKind.Tank)
            {
                // 戦車は車をはね飛ばす
                traffic.Knock(hit, hit.U >= playerU ? 1 : -1, 0.6f);
                AddFx(FxKind.Dust, hit.S, hit.U, 0.5f, 1.2f);
                destroyed++;
                shake = Math.Max(shake, 0.3f);
                crashCooldown = 0.2f;
            }
            else if (hit != null)
            {
                game.Crash();
                crashCooldown = 2.5f;
                shake = 0.8f;
                float half = (hit.Length + Traffic.PlayerLength) / 2 + 1.5f;
                if (hit.S > playerS) { hit.S = playerS + half; hit.Speed = Math.Max(hit.Speed, ms + 4); }
                else { hit.S = playerS - half; hit.Speed = 0; }
            }
        }
        shake = Math.Max(0, shake - dt);
        UpdateCombat(dt, ms);

        // 都市の近くはビルが増える
        float urbanTarget = 0;
        if (game.AtCity != null) urbanTarget = game.AtCity.IsGoal ? 1 : 0.5f;
        else if (game.Road != null && !game.OnFerry)
        {
            bool nearFrom = game.Traveled < game.Remaining;
            var city = nearFrom ? game.From : game.To;
            double km = nearFrom ? game.Traveled : game.Remaining;
            urbanTarget = (city.IsGoal ? 1f : 0.5f) * (float)Math.Clamp(1 - (km - 5) / 25, 0, 1);
        }
        scene.Urban += (urbanTarget - scene.Urban) * Math.Min(1, dt * 0.6f);

        resetConfirm -= dt;
        UpdateCamera(dt);
    }

    private static float SteerInput() =>
        (Raylib.IsKeyDown(KeyboardKey.Left) || Raylib.IsKeyDown(KeyboardKey.A) ? -1 : 0)
        + (Raylib.IsKeyDown(KeyboardKey.Right) || Raylib.IsKeyDown(KeyboardKey.D) ? 1 : 0);

    private void UpdateTitle(float dt)
    {
        HandleUpdateKeys();
        titleTime += dt;
        playerS += 22 * dt;
        scene.PlayerS = playerS;
        traffic.Update(dt, playerS, playerU, 22);
        if (Raylib.IsKeyPressed(KeyboardKey.One) || Raylib.IsKeyPressed(KeyboardKey.Kp1)) StartGame(GameMode.Tour);
        else if (Raylib.IsKeyPressed(KeyboardKey.Two) || Raylib.IsKeyPressed(KeyboardKey.Kp2)) StartGame(GameMode.Free);
        UpdateCamera(dt);
    }

    // ---------------- 寄り道（一般道） ----------------

    private void StartDetour(Spot spot)
    {
        double length = shotFile != null ? 150 : 430;
        detour = new Detour { Spot = spot, EndS = playerS + length };
        detour.LandmarkS = detour.EndS + 8 + Landmarks.Offset(spot.Type);
        scene.BeginEnv(playerS - 200, Env.Local);
        scene.LocalEndS = detour.EndS;
        playerU = -1.8f;
        game.SetClock(game.GameMinutes + 15);
        game.AddLog($"{spot.City.Name}で高速を降りて、{spot.Name}へ向かいます。");
    }

    private void EndDetour()
    {
        detour = null;
        scene.LocalEndS = double.MaxValue;
        playerS += 60;
        scene.BeginEnv(playerS - 200, game.LastRoad != null ? Scene.EnvOf(game.LastRoad) : Env.Land);
        traffic.Reset(playerS);
        playerU = Traffic.DrivingLane;
        game.SetClock(game.GameMinutes + 15);
        game.AddLog("高速道路に戻りました。↑ で次の道路へ出発します。");
    }

    private void UpdateDetour(float dt)
    {
        var d = detour!;
        bool up = Raylib.IsKeyDown(KeyboardKey.Up) || Raylib.IsKeyDown(KeyboardKey.W);
        bool brake = Raylib.IsKeyDown(KeyboardKey.Down) || Raylib.IsKeyDown(KeyboardKey.S);
        if (shotFile != null) up = true;
        game.Brake = brake;

        if (!d.Arrived)
        {
            if (up) d.Kmh += 28 * dt;
            else if (brake) d.Kmh -= 70 * dt;
            else d.Kmh -= 4 * dt;
            // 名所の前で自動的に止まる
            double remain = d.EndS - playerS;
            double maxKmh = Math.Sqrt(Math.Max(0, remain - 1) * 2 * 4.5) * 3.6;
            d.Kmh = Math.Clamp(d.Kmh, 0, Math.Min(Math.Min(80, VehicleSpeedCap), maxKmh));
            playerS += d.Kmh / 3.6 * dt;
            game.SetClock(game.GameMinutes + dt * 0.5);
            if (remain < 3 && d.Kmh < 4)
            {
                d.Kmh = 0;
                d.Arrived = true;
                AwardStamp(d.Spot);
            }
        }
        else d.ArrivedTime += dt;

        float ms = (float)d.Kmh / 3.6f;
        float steer = SteerInput();
        playerU = Math.Clamp(playerU + steer * 4f * Math.Min(1f, ms / 6f) * dt, -2.9f, 2.9f);
        steerYaw += (-steer * Math.Min(1f, ms / 8f) * 8f - steerYaw) * Math.Min(1, dt * 6);
        scene.PlayerS = playerS;
        scene.Urban = 0;
    }

    private void AwardStamp(Spot spot)
    {
        popupSpot = spot;
        popupTime = 0;
        popupNew = !stamps.ContainsKey(spot.Key);
        if (popupNew) Audio.Play(Audio.Stamp);
        if (popupNew)
        {
            stamps[spot.Key] = game.GameMinutes;
            game.AddLog($"【スタンプGET】{spot.Name}（{stamps.Count}/{Spots.All.Count}）");
            if (stamps.Count == Spots.All.Count)
            {
                game.AddLog("全国の名所スタンプをすべて集めました！おめでとう！");
                Audio.Play(Audio.Fanfare);
            }
            SaveNow();
        }
        else game.AddLog($"{spot.Name}に到着（スタンプは獲得済み）");
    }

    private Spot? SpotFor(City city) =>
        Spots.At(city).FirstOrDefault(s => !stamps.ContainsKey(s.Key)) ?? Spots.At(city).FirstOrDefault();

    // ---------------- フェード ----------------

    private void BeginFade(Action action)
    {
        if (fadeDir != 0) return;
        fadeDir = 1;
        fadeAction = action;
    }

    private void UpdateFade(float dt)
    {
        if (fadeDir == 0) return;
        fade += fadeDir * dt * 2.5f;
        if (fadeDir > 0 && fade >= 1)
        {
            fade = 1;
            fadeAction?.Invoke();
            fadeAction = null;
            fadeDir = -1;
        }
        else if (fadeDir < 0 && fade <= 0)
        {
            fade = 0;
            fadeDir = 0;
        }
    }

    // ---------------- カメラ ----------------

    private void UpdateCamera(float dt)
    {
        scene.PlayerS = playerS;
        Vector3 fwd, car;
        if (freeRoam && !title && free != null)
        {
            float hr = heading * MathF.PI / 180;
            fwd = new Vector3(-MathF.Sin(hr), 0, MathF.Cos(hr));
            car = free.R(free.PX, freeY, free.PZ);
        }
        else
        {
            float slope = (float)Scene.Slope(playerS);
            fwd = Vector3.Normalize(new Vector3(slope, 0, 1));
            car = scene.P(playerS, playerU, playerY);
        }
        bool big = playerKind is VehicleKind.Tank or VehicleKind.Amphibious;
        bool small = playerKind is VehicleKind.Horse or VehicleKind.Hikyaku;

        Vector3 pos, target;
        if (title)
        {
            float a = titleTime * 0.18f;
            pos = car + new Vector3(MathF.Sin(a) * 11, 3.2f, MathF.Cos(a) * 11);
            target = car + new Vector3(0, 0.8f, 0);
            camera.FovY = 50;
        }
        else if (showcase)
        {
            var left = new Vector3(fwd.Z, 0, -fwd.X);
            pos = car + fwd * 5.5f + left * 3.8f + new Vector3(0, 1.6f, 0);
            target = car + new Vector3(0, 0.6f, 0) - fwd * 0.5f;
            camera.FovY = 50;
        }
        else if (freeRoam && IsFlyer(playerKind))
        {
            // 空を飛ぶ乗り物：機首の上下に合わせてカメラも動かす
            float p = pitch * MathF.PI / 180;
            var dir = Vector3.Normalize(fwd * MathF.Cos(p) + new Vector3(0, MathF.Sin(p), 0));
            if (cockpit)
            {
                pos = car + new Vector3(0, 2.1f, 0) + fwd * 1.8f;
                target = pos + dir * 40;
                camera.FovY = 70;
            }
            else
            {
                float back = playerKind == VehicleKind.Airplane ? 20 : 17;
                pos = car - dir * back + new Vector3(0, 5.5f, 0);
                // 高く飛ぶほど下の景色を見下ろす
                float down = Math.Min(22, altitude / 150f);
                target = car + dir * 25 + new Vector3(0, 1.5f - down, 0);
                camera.FovY = 62;
            }
        }
        else if (cockpit && detour?.Arrived != true)
        {
            if (playerKind == VehicleKind.Tank)
            {
                // 砲手視点：砲塔の向きに合わせて見る
                float a = (turretYaw + steerYaw) * MathF.PI / 180;
                var dir = new Vector3(fwd.X * MathF.Cos(a) + fwd.Z * MathF.Sin(a), 0, -fwd.X * MathF.Sin(a) + fwd.Z * MathF.Cos(a));
                pos = car + new Vector3(0, 3.1f, 0) + dir * 0.6f;
                target = pos + dir * 40 + new Vector3(0, -1.2f, 0);
            }
            else
            {
                float eye = big ? 2.9f : playerKind == VehicleKind.Horse ? 2.95f : playerKind == VehicleKind.Hikyaku ? 1.65f : 1.25f;
                pos = car + new Vector3(0, eye, 0) + fwd * (big ? 1.5f : small ? 0.4f : 0.3f);
                target = pos + fwd * 30 + new Vector3(0, -0.6f, 0);
            }
            camera.FovY = 70;
        }
        else
        {
            pos = car - fwd * (big ? 14f : small ? 6.5f : 9.5f) + new Vector3(0, big ? 5.5f : small ? 2.8f : 3.6f, 0);
            target = car + fwd * 14 + new Vector3(0, big ? 1.8f : small ? 1.3f : 1.0f, 0);
            camera.FovY = 60;
            if (detour != null)
            {
                // 名所に着いたら、名所を見上げるカメラへ
                float t = detour.Arrived ? Math.Min(1, detour.ArrivedTime / 2.5f) : 0;
                t = t * t * (3 - 2 * t);
                float h = Landmarks.Height(detour.Spot.Type);
                var lm = scene.P(detour.LandmarkS, 0, h * 0.55f);
                target = Vector3.Lerp(target, lm, t);
                pos = Vector3.Lerp(pos, car - fwd * 14 + new Vector3(0, 2.0f + Math.Min(h, 60) * 0.05f, 0), t);
                camera.FovY = 60 + 6 * t;
            }
        }
        if (shake > 0)
        {
            var j = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, 0) * shake * 0.6f;
            pos += j;
            target += j;
        }
        camera.Position = pos;
        camera.Target = target;
    }

    // ---------------- キー操作 ----------------

    private void HandleKeys()
    {
        if (stampBook)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.B) || Raylib.IsKeyPressed(KeyboardKey.Escape)) stampBook = false;
            return;
        }
        if (freeRoam)
        {
            HandleFreeKeys();
            return;
        }

        if (detour != null)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter) ||
                Raylib.IsKeyPressed(KeyboardKey.Escape) || (detour.Arrived && Raylib.IsKeyPressed(KeyboardKey.Up)))
                BeginFade(EndDetour);
            if (Raylib.IsKeyPressed(KeyboardKey.B)) stampBook = true;
            if (Raylib.IsKeyPressed(KeyboardKey.C)) cockpit = !cockpit;
            return;
        }

        // 数字キー：停車中は出発、走行中は次の分岐の予約
        var junction = game.JunctionCity;
        for (int i = 0; i < 9 && i < junction.Roads.Count; i++)
        {
            if (Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One + i)) ||
                Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.Kp1 + i)))
            {
                if (game.AtCity != null)
                {
                    game.Depart(game.AtCity.Roads[i]);
                    bigMap = false;
                }
                else ReserveRoad(junction.Roads[i]);
                break;
            }
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Q)) CycleReserve(-1);
        if (Raylib.IsKeyPressed(KeyboardKey.E)) CycleReserve(1);

        if (Raylib.IsKeyPressed(KeyboardKey.T))
        {
            game.FreeDrive = !game.FreeDrive;
            game.AddLog(game.FreeDrive ? "分岐おまかせ：ON（止まらずに進みます）" : "分岐おまかせ：OFF（分岐ごとに停車します）");
        }
        if (Raylib.IsKeyPressed(KeyboardKey.P)) RequestDetour();
        if (Raylib.IsKeyPressed(KeyboardKey.V)) SwitchVehicle();
        if (Raylib.IsKeyPressed(KeyboardKey.B)) stampBook = true;
        if (Raylib.IsKeyPressed(KeyboardKey.M)) bigMap = !bigMap;
        if (Raylib.IsKeyPressed(KeyboardKey.C)) cockpit = !cockpit;
        if (Raylib.IsKeyPressed(KeyboardKey.F)) game.Refuel();
        if (Raylib.IsKeyPressed(KeyboardKey.U)) game.UTurn();
        if (Raylib.IsKeyPressed(KeyboardKey.Space)) game.FastForward = !game.FastForward;
        if (Raylib.IsKeyPressed(KeyboardKey.G))
        {
            game.AutoDrive = !game.AutoDrive;
            game.AddLog(game.AutoDrive ? "ナビ自動走行：ON" : "ナビ自動走行：OFF");
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            if (bigMap) bigMap = false;
            else game.SetNav(null);
        }
        if (Raylib.IsKeyPressed(KeyboardKey.N))
        {
            if (game.Cleared || resetConfirm > 0)
            {
                SaveNow();
                title = true;
                titleTime = 0;
            }
            else
            {
                resetConfirm = 2.5f;
                game.AddLog("もう一度 N を押すとタイトルに戻ります。");
            }
        }
    }

    private void ReserveRoad(Road road)
    {
        game.PreferredNext = road;
        var city = game.JunctionCity;
        game.AddLog($"次の分岐（{city.Name}）：{road.Name} で {road.Other(city).Name} 方面へ");
    }

    private void CycleReserve(int dir)
    {
        var city = game.JunctionCity;
        if (city.Roads.Count == 0) return;
        var current = game.PlannedRoad();
        int i = current == null ? 0 : city.Roads.IndexOf(current);
        i = ((i + dir) % city.Roads.Count + city.Roads.Count) % city.Roads.Count;
        if (game.AtCity != null) game.PreferredNext = city.Roads[i];
        else ReserveRoad(city.Roads[i]);
    }

    private void RequestDetour()
    {
        if (pendingDetour != null)
        {
            game.AddLog($"{pendingDetour.Name}への寄り道を取り消しました。");
            pendingDetour = null;
            game.StopAtNextCity = false;
            return;
        }
        if (game.OnFerry) return;
        var city = game.JunctionCity;
        var spot = SpotFor(city);
        if (spot == null)
        {
            game.AddLog($"{city.Name}には寄り道できる名所がありません。");
            return;
        }
        if (game.AtCity != null)
        {
            BeginFade(() => StartDetour(spot));
            return;
        }
        pendingDetour = spot;
        game.StopAtNextCity = true;
        game.AddLog($"{city.Name}で高速を降りて「{spot.Name}」に寄り道します（P で取り消し）。");
    }

    // ================= 描画 =================

    private void Render()
    {
        Raylib.BeginDrawing();
        scene.SetTime(title ? 9 * 60 : game.GameMinutes);
        Raylib.ClearBackground(scene.Horizon);
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Raylib.DrawRectangleGradientV(0, 0, w, (int)(h * 0.55f), scene.SkyTop, scene.Horizon);

        if (freeRoam && !title && free != null)
            Rlgl.SetClipPlanes(Math.Min(2.0, 0.3 + altitude * 0.01), Math.Max(2500, free.ViewRadius * 1.6 + 3000 + altitude * 1.5));
        else
        {
            scene.FogScale = 1;
            Rlgl.SetClipPlanes(0.05, 1000);
        }
        Raylib.BeginMode3D(camera);
        if (freeRoam && !title)
        {
            DrawFreeWorld3D();
            Raylib.EndMode3D();
            DrawFreeHud();
            if (fade > 0) Raylib.DrawRectangle(0, 0, w, h, new Color((byte)0, (byte)0, (byte)0, (byte)(255 * Math.Clamp(fade, 0, 1))));
            Raylib.EndDrawing();
            return;
        }
        scene.Draw(camera.Position);
        if (detour != null)
        {
            Landmarks.Draw(scene, detour.Spot, scene.P(detour.LandmarkS, 0, 0), time);
        }
        else if (game.OnFerry && !title)
        {
            scene.DrawFerry(playerU);
        }
        else if (!game.OnSea || title)
        {
            foreach (var v in traffic.Cars)
                scene.DrawVehicle(v.S, v.U, 0, v.Color, v.Kind, v.Oncoming, v.Braking, y: v.Y, roll: v.Roll);
        }
        if ((pstate == PlayerState.Road && !game.OnSea) || title) scene.DrawHeadlightPool(playerS, playerU);
        if (!cockpit || title || detour?.Arrived == true)
            scene.DrawVehicle(playerS, playerU, steerYaw, PlayerColor, playerKind, false,
                braking: game.Brake && CurrentKmh > 0.5f, wheelSteer: playerKind == VehicleKind.Sports ? steerYaw * 2.5f : 0,
                y: playerY, turretYaw: turretYaw, recoil: recoil);
        DrawCombat3D();
        Raylib.EndMode3D();

        if (title) DrawTitle();
        else DrawHud();
        if (fade > 0) Raylib.DrawRectangle(0, 0, w, h, new Color((byte)0, (byte)0, (byte)0, (byte)(255 * Math.Clamp(fade, 0, 1))));
        Raylib.EndDrawing();
    }
}
