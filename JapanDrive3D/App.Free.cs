using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;
using PointF = System.Drawing.PointF;

namespace JapanDrive;

/// <summary>
/// フリーモード：道路に関係なく日本中を自由に走れるオープンワールド。速度制限なし。
/// </summary>
public sealed partial class App
{
    private bool freeRoam;
    private FreeWorld? free;
    private float heading;          // 北=0、東=90（度）
    private float freeKmh;
    private float freeY;
    private bool freeOnWater;
    private double animDist;
    private (double X, double Z)? waypoint;

    // 空を飛ぶ乗り物
    private float altitude;         // 地面・海面からの高さ（m）
    private float pitch, bank;      // 機首の上下・傾き（度）
    private float rotor;
    private float groundLevel;
    private bool stallWarned;

    private static bool IsFlyer(VehicleKind k) => k is VehicleKind.Helicopter or VehicleKind.Airplane;

    // デモ用
    private float demoClimb;
    private float demoAccel = 3.5f;
    private float demoSteer;

    private void StartFree()
    {
        free = new FreeWorld(scene);
        var city = MapData.Get(demoAt ?? "東京");
        (free.PX, free.PZ) = FreeWorld.CityPos(city);
        bool resume = Persist && demoAt == null && save.FreeX is double && save.FreeZ is double;
        if (resume)
        {
            free.PX = save.FreeX!.Value;
            free.PZ = save.FreeZ!.Value;
            heading = save.FreeHeading;
        }
        free.UpdateNear();
        freeKmh = 0;
        freeY = 0;
        altitude = pitch = bank = groundLevel = 0;
        freeOnWater = false;
        animDist = 0;
        waypoint = null;
        game.Log.Clear();
        game.AddLog(resume
            ? $"フリーモード：前回の続き（{free.NearestCity(free.PX, free.PZ).City.Name}付近）から再開しました。"
            : $"フリーモード：{city.Name}から出発。道路も海も、日本中どこでも自由に走れます（速度制限なし）。");
        game.AddLog("各地の名所に近づくとスタンプがもらえます。M の地図でクリックした場所へワープもできます。");
    }

    private static float Accel(VehicleKind k) => k switch
    {
        VehicleKind.Sports => 60,
        VehicleKind.Tank => 24,
        VehicleKind.Amphibious => 28,
        VehicleKind.Horse => 32,
        VehicleKind.Hikyaku => 20,
        _ => 40,
    };

    private static double StrideOf(VehicleKind k) => k == VehicleKind.Horse ? 3.2 : 2.4;

    private float FreeSpin => playerKind is VehicleKind.Horse or VehicleKind.Hikyaku
        ? (float)(animDist / StrideOf(playerKind) * 360 % 360)
        : (float)(animDist / 0.33 * 57.2958 % 360);

    /// <summary>砲身の向き（ワールドの方位、度）。</summary>
    private float BarrelHeading => heading - turretYaw - steerYaw;

    private void UpdateFree(float dt)
    {
        var f = free!;
        bool up = Raylib.IsKeyDown(KeyboardKey.Up) || Raylib.IsKeyDown(KeyboardKey.W);
        bool down = Raylib.IsKeyDown(KeyboardKey.Down) || Raylib.IsKeyDown(KeyboardKey.S);
        float steer = SteerInput();
        if (shotFile != null)
        {
            up = demoTime < demoAccel;
            steer = demoSteer;
        }
        game.Brake = down && freeKmh > 0;
        bool flyer = IsFlyer(playerKind);
        bool climb = Raylib.IsKeyDown(KeyboardKey.Space) || Raylib.IsKeyDown(KeyboardKey.PageUp);
        bool descend = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift) || Raylib.IsKeyDown(KeyboardKey.PageDown);
        if (shotFile != null)
        {
            climb = demoClimb > 0 && demoTime > 1.5f && demoTime < 1.5f + demoClimb;
            descend = false;
        }
        bool airborne = altitude > 0.5f;

        // 速度制限なし：踏み続けるほど速くなる
        if (up) freeKmh += Accel(playerKind) * (1 + Math.Max(0, freeKmh) / 500f) * dt;
        else if (down && airborne) freeKmh = Math.Max(0, freeKmh - 60 * dt);   // 空中ではバックしない
        else if (down) freeKmh = freeKmh > 0 ? Math.Max(0, freeKmh - 140 * dt) : Math.Max(-30, freeKmh - 20 * dt);
        else
        {
            freeKmh -= freeKmh * 0.12f * dt;
            if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
        }

        bool road = f.OnRoad(f.PX, f.PZ);
        bool land = road || f.IsLandFast(f.PX, f.PZ);
        bool water = !land;
        if (water && !airborne && playerKind != VehicleKind.Amphibious && !flyer)
            freeKmh = Math.Clamp(freeKmh, -15, 30); // 水の中はゆっくり

        if (flyer && airborne)
            heading += steer * (playerKind == VehicleKind.Helicopter ? 60 : 45) * dt;
        else
        {
            float turn = 75f / (1 + Math.Abs(freeKmh) / 250f) * Math.Min(1, Math.Abs(freeKmh) / 8f);
            heading += steer * turn * dt * (freeKmh < 0 ? -1 : 1);
        }
        heading = (heading % 360 + 360) % 360;
        steerYaw += (-steer * Math.Min(1f, Math.Abs(freeKmh) / 36f) * 6f - steerYaw) * Math.Min(1, dt * 6);

        float ms = freeKmh / 3.6f;
        double hr = heading * Math.PI / 180;

        // ---- 飛行 ----
        float prevAlt = altitude, vs = 0;
        if (playerKind == VehicleKind.Helicopter)
        {
            vs = (climb ? 14 : descend ? -14 : 0) * (1 + altitude / 500f);
            altitude = Math.Max(0, altitude + vs * dt);
            float target = altitude > 0.5f ? -Math.Clamp(freeKmh / 25f, -8, 14) : 0;   // 前進中は機首を下げる
            pitch += Math.Clamp(target - pitch, -30 * dt, 30 * dt);
        }
        else if (playerKind == VehicleKind.Airplane)
        {
            const float stallKmh = 140;
            float target = climb ? 20 : descend ? -20 : 0;
            if (!airborne && (freeKmh < stallKmh || target < 0)) target = 0;   // 遅いと離陸できない
            pitch += Math.Clamp(target - pitch, -40 * dt, 40 * dt);
            vs = ms * MathF.Sin(pitch * MathF.PI / 180);
            if (airborne && freeKmh < stallKmh)
            {
                vs -= 12;   // 失速
                if (!stallWarned) game.AddLog("失速！ ↑で速度を上げてください。");
                stallWarned = true;
            }
            else if (freeKmh > stallKmh + 20) stallWarned = false;
            altitude = Math.Max(0, altitude + vs * dt);
            if (altitude <= 0 && pitch < 0) pitch = 0;
            ms *= MathF.Cos(pitch * MathF.PI / 180);
        }
        else altitude = 0;
        if (altitude > 30000)
        {
            altitude = 30000;
            if (prevAlt < 30000) game.AddLog("高度3万m：これ以上は宇宙です！");
        }
        if (prevAlt > 0.5f && altitude <= 0)
        {
            AddFxW(FxKind.Dust, f.PX, groundLevel + 0.3f, f.PZ, vs < -12 ? 2.5f : 1.2f);
            shake = Math.Max(shake, vs < -12 ? 0.7f : 0.2f);
            game.AddLog(vs < -12 ? "ドスン！ 強めに着地しました。" : "着陸しました。");
        }
        else if (prevAlt <= 0.5f && altitude > 0.5f) game.AddLog("離陸！");
        airborne = altitude > 0.5f;
        float bankTarget = flyer && airborne ? steer * (playerKind == VehicleKind.Helicopter ? 12 : 35) : 0;
        bank += (bankTarget - bank) * Math.Min(1, dt * 3);
        rotor += dt * (playerKind == VehicleKind.Helicopter ? 1400 : playerKind == VehicleKind.Airplane ? 400 + Math.Abs(freeKmh) * 6 : 0);
        rotor %= 360000;

        // 高く飛ぶほど遠くまで見えるようにする
        scene.FogScale = 1 + altitude / 120f;
        f.ViewRadius = Math.Min(14000, 1000 * scene.FogScale);
        f.Altitude = altitude;

        f.PX += Math.Sin(hr) * ms * dt;
        f.PZ += Math.Cos(hr) * ms * dt;
        animDist += Math.Abs(ms) * dt;

        float targetY = road ? 0.06f : land ? 0
            : airborne || flyer ? FreeWorld.SeaY
            : playerKind == VehicleKind.Amphibious ? FreeWorld.SeaY - 0.9f + MathF.Sin(time * 1.6f) * 0.15f
            : FreeWorld.SeaY - 0.8f;
        if (airborne) water = false;
        if (water != freeOnWater)
        {
            freeOnWater = water;
            AddFxW(FxKind.Splash, f.PX, FreeWorld.SeaY, f.PZ, 1.6f);
            if (water) game.AddLog(playerKind == VehicleKind.Amphibious ? "海へ入りました！ 水上を航行します。" : "海に入りました（水の中はゆっくりしか進めません）。");
        }
        groundLevel += (targetY - groundLevel) * Math.Min(1, dt * 8);
        freeY = groundLevel + altitude;
        wakeTimer -= dt;
        if (water && Math.Abs(ms) > 2 && wakeTimer <= 0)
        {
            wakeTimer = 0.07f;
            AddFxW(FxKind.Wake, f.PX - Math.Sin(hr) * 4.5, FreeWorld.SeaY + 0.05f, f.PZ - Math.Cos(hr) * 4.5, 0.25f + Math.Abs(ms) * 0.012f);
        }

        game.SetClock(game.GameMinutes + dt);
        f.UpdateNear();
        f.UpdateTraffic(dt);

        // ほかの車にぶつかったらはね飛ばす
        crashCooldown -= dt;
        if (crashCooldown <= 0 && Math.Abs(ms) > 2 && !water && altitude < 2)
        {
            var hit = f.HitTest(f.PX, f.PZ, 1, PlayerHalfWidth + PlayerLength * 0.3f);
            if (hit != null)
            {
                f.Knock(hit, Math.Sin(hr), Math.Cos(hr), playerKind == VehicleKind.Tank ? 0.8f : 0.5f);
                AddFxW(FxKind.Dust, hit.X, 0.5f, hit.Z, 1.2f);
                destroyed++;
                if (playerKind != VehicleKind.Tank) freeKmh *= 0.6f;
                shake = Math.Max(shake, 0.4f);
                crashCooldown = 0.2f;
            }
        }
        shake = Math.Max(0, shake - dt);

        // 名所に近づいたらスタンプ
        foreach (var (spot, pos) in f.SpotPos)
        {
            if (stamps.ContainsKey(spot.Key)) continue;
            double dx = pos.X - f.PX, dz = pos.Z - f.PZ;
            float r = FreeWorld.CollectRadius(spot.Type);
            if (dx * dx + dz * dz < r * r) AwardStamp(spot);
        }
        if (waypoint is (double wx, double wz) && (wx - f.PX) * (wx - f.PX) + (wz - f.PZ) * (wz - f.PZ) < 80 * 80)
        {
            waypoint = null;
            game.AddLog("目的地に到着しました！");
        }

        resetConfirm -= dt;
        UpdateCombat(dt, ms);
        UpdateCamera(dt);
    }

    private void HandleFreeKeys()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.V)) SwitchVehicle();
        if (Raylib.IsKeyPressed(KeyboardKey.C)) cockpit = !cockpit;
        if (Raylib.IsKeyPressed(KeyboardKey.B)) stampBook = true;
        if (Raylib.IsKeyPressed(KeyboardKey.M)) bigMap = !bigMap;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            if (bigMap) bigMap = false;
            else if (waypoint != null) { waypoint = null; game.AddLog("目的地を解除しました。"); }
        }
        if (Raylib.IsKeyPressed(KeyboardKey.N))
        {
            if (resetConfirm > 0) { SaveNow(); title = true; titleTime = 0; freeRoam = false; }
            else
            {
                resetConfirm = 2.5f;
                game.AddLog("もう一度 N を押すとタイトルに戻ります。");
            }
        }
    }

    // ---------------- 砲撃（フリーモード） ----------------

    private void FireFree(float ms)
    {
        var f = free!;
        double br = BarrelHeading * Math.PI / 180, hr = heading * Math.PI / 180;
        double dx = Math.Sin(br), dz = Math.Cos(br);
        const float speed = 230;
        shells.Add(new Shell
        {
            Free = true,
            WX = f.PX + dx * 7.6, WZ = f.PZ + dz * 7.6, Y = freeY + 2.05f,
            Vx = (float)(dx * speed + Math.Sin(hr) * ms), Vz = (float)(dz * speed + Math.Cos(hr) * ms), Vy = 3,
            Prev = f.R(f.PX + dx * 7.6, freeY + 2.05f, f.PZ + dz * 7.6),
        });
        AddFxW(FxKind.Muzzle, f.PX + dx * 7.6, freeY + 2.05f, f.PZ + dz * 7.6, 1);
        recoil = 1;
        reload = 1.4f;
        shake = Math.Max(shake, 0.35f);
        Audio.Play(Audio.Cannon, 0.9f);
    }

    /// <summary>フリーモードの砲弾を進める。終わったら true。</summary>
    private bool UpdateFreeShell(Shell sh, float dt)
    {
        var f = free!;
        sh.Prev = f.R(sh.WX, sh.Y, sh.WZ);
        for (int step = 0; step < 2; step++)
        {
            float h = dt / 2;
            sh.WX += sh.Vx * h;
            sh.WZ += sh.Vz * h;
            sh.Vy -= 9.8f * h;
            sh.Y += sh.Vy * h;
            sh.Life += h;
            var hit = f.HitTest(sh.WX, sh.WZ, sh.Y, 1.5f);
            if (hit != null)
            {
                AddFxW(FxKind.Fire, hit.X, 1.2f, hit.Z, hit.Kind == VehicleKind.Truck ? 1.6f : 1.2f);
                float v = MathF.Sqrt(sh.Vx * sh.Vx + sh.Vz * sh.Vz);
                f.Knock(hit, sh.Vx / v, sh.Vz / v, 1);
                destroyed++;
                return true;
            }
            float g = f.GroundY(sh.WX, sh.WZ);
            if (sh.Y <= g)
            {
                AddFxW(g < 0 ? FxKind.Splash : FxKind.Fire, sh.WX, g, sh.WZ, g < 0 ? 1.6f : 0.8f);
                return true;
            }
            if (sh.Life > 3.5f) return true;
        }
        return false;
    }

    private void AddFxW(FxKind kind, double x, float y, double z, float size)
    {
        effects.Add(new Fx { Kind = kind, Free = true, WX = x, WZ = z, Y = y, Size = size, Seed = rng.Next(1000) });
        if (free != null) FxSound(kind, Math.Sqrt((x - free.PX) * (x - free.PX) + (z - free.PZ) * (z - free.PZ)), size);
    }

    // ---------------- 描画 ----------------

    private void DrawFreeWorld3D()
    {
        var f = free!;
        f.Draw(camera.Position, time);
        if (IsFlyer(playerKind))
        {
            DrawPlayerAircraft(f);
            DrawCombat3D();
            return;
        }
        if (!cockpit)
            scene.DrawVehicleAt(f.R(f.PX, freeY, f.PZ), -heading + steerYaw, PlayerColor, playerKind, FreeSpin,
                braking: game.Brake, wheelSteer: playerKind == VehicleKind.Sports ? steerYaw * 2.5f : 0,
                turretYaw: turretYaw, recoil: recoil, shadow: !freeOnWater);
        DrawCombat3D();
    }

    private void DrawPlayerAircraft(FreeWorld f)
    {
        // 地面に落ちる影（高いほど薄く）
        if (altitude < 400)
        {
            var g = f.R(f.PX, groundLevel + 0.08f, f.PZ);
            float hw = playerKind == VehicleKind.Airplane ? 5f : 1.6f, hl = playerKind == VehicleKind.Airplane ? 4f : 4.5f;
            float hr = heading * MathF.PI / 180;
            var fw = new Vector3(-MathF.Sin(hr), 0, MathF.Cos(hr));
            var sd = new Vector3(fw.Z, 0, -fw.X);
            byte a = (byte)(90 * (1 - altitude / 400));
            Rlgl.Begin(DrawMode.Quads);
            foreach (var v in new[] { g + fw * hl + sd * hw, g + fw * hl - sd * hw, g - fw * hl - sd * hw, g - fw * hl + sd * hw })
            {
                Rlgl.Color4ub(0, 0, 0, a);
                Rlgl.Vertex3f(v.X, v.Y, v.Z);
            }
            Rlgl.End();
        }
        if (cockpit) return;
        var pos = f.R(f.PX, freeY, f.PZ);
        Rlgl.PushMatrix();
        Rlgl.Translatef(pos.X, pos.Y, pos.Z);
        Rlgl.Rotatef(-heading, 0, 1, 0);
        Rlgl.Rotatef(-pitch, 1, 0, 0);
        Rlgl.Rotatef(bank, 0, 0, 1);
        Aircraft.Draw(scene, playerKind, PlayerColor, 10, rotor, altitude < 8);
        Rlgl.PopMatrix();
    }

    private PointF CarMapPos => freeRoam && free != null ? FreeWorld.ToMap(free.PX, free.PZ) : game.CarWorld;

    private float CarMapRot
    {
        get
        {
            if (freeRoam)
            {
                double hr = heading * Math.PI / 180;
                return (float)(Math.Atan2(-Math.Cos(hr), Math.Sin(hr)) * 180 / Math.PI);
            }
            var a = Game.ToWorld(game.From);
            var b = Game.ToWorld(game.To);
            return game.Road != null ? MathF.Atan2(b.Y - a.Y, b.X - a.X) * 180 / MathF.PI : -90;
        }
    }

    private static string Compass(double deg)
    {
        string[] names = { "北", "北東", "東", "南東", "南", "南西", "西", "北西" };
        return names[(int)Math.Round(((deg % 360) + 360) % 360 / 45) % 8];
    }

    private void DrawFreeHud()
    {
        var f = free!;
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Ui;

        // 近くの都市名と名所名を3Dの上に表示
        var look = Vector3.Normalize(camera.Target - camera.Position);
        foreach (var c in MapData.Cities)
        {
            var (cx, cz) = FreeWorld.CityPos(c);
            var p = f.R(cx, 70, cz);
            float d = p.Length();
            if (d > 1600 || Vector3.Dot(p - camera.Position, look) < 5) continue;
            var sp = Raylib.GetWorldToScreen(p, camera);
            float size = Math.Clamp(26 - d / 80, 13, 26);
            var m = Measure(c.Name, size);
            Panel(sp.X - m.X / 2 - 8 * u, sp.Y - 4 * u, m.X + 16 * u, m.Y + 8 * u, new Color(10, 40, 80, 170));
            Text(c.Name, sp.X - m.X / 2, sp.Y, size, Color.White);
        }
        foreach (var (spot, pos) in f.SpotPos)
        {
            var p = f.R(pos.X, Landmarks.Height(spot.Type) * 1.6f + 10, pos.Z);
            float d = p.Length();
            if (d > 1300 || Vector3.Dot(p - camera.Position, look) < 5) continue;
            var sp = Raylib.GetWorldToScreen(p, camera);
            bool got = stamps.ContainsKey(spot.Key);
            string label = (got ? "● " : "★ ") + spot.Name;
            var m = Measure(label, 15);
            Panel(sp.X - m.X / 2 - 8 * u, sp.Y - 4 * u, m.X + 16 * u, m.Y + 8 * u, got ? new Color(60, 60, 60, 160) : new Color(120, 30, 30, 190));
            Text(label, sp.X - m.X / 2, sp.Y, 15, got ? Rgb(200, 200, 200) : Rgb(255, 225, 130));
        }

        // 左上：状況
        var (near, nd) = f.NearestCity(f.PX, f.PZ);
        Panel(16 * u, 16 * u, 430 * u, 150 * u);
        float x = 32 * u, y = 28 * u;
        Text(Game.FormatClock(game.GameMinutes), x, y, 22, Orange);
        y += 32 * u;
        Text("フリーモード（道路も速度も自由）", x, y, 18, Color.White);
        y += 28 * u;
        Text($"{near.Name}付近（中心まで {nd / 1000:0.0} km）　向き：{Compass(heading)}", x, y, 15, Rgb(220, 225, 235));
        y += 26 * u;
        Text($"名所スタンプ {stamps.Count}/{Spots.All.Count}（B：スタンプ帳）{(destroyed > 0 ? $"　撃破 {destroyed}" : "")}", x, y, 15, Rgb(255, 200, 200));

        DrawMiniMap(sw - 316 * u, 16 * u, 300 * u);
        DrawFreeGuide(sw, u);
        DrawSpeedometer(sw - 170 * u, sh - 175 * u, u);
        DrawLog(16 * u, sh - 150 * u, u);
        DrawCrosshair(u);

        string help = IsFlyer(playerKind)
            ? "↑↓ 速度　←→ 旋回　Space 上昇　Shift 下降" + (playerKind == VehicleKind.Airplane ? "（140km/h 以上で離陸）" : "") + "　V 乗り物　M 地図・ワープ　B スタンプ帳　C 視点"
            : playerKind == VehicleKind.Tank
            ? "↑↓ 前進/後退（速度無制限）　←→ ハンドル　マウス・Z/X 砲塔　左クリック・Ctrl 発射　V 乗り物　M 地図・ワープ　B スタンプ帳　C 視点"
            : "↑↓ 前進/後退（速度無制限）　←→ ハンドル　V 乗り物切替　M 地図（クリックでワープ）　B スタンプ帳　C 視点　N タイトル";
        var hm = Measure(help, 13);
        Text(help, (sw - hm.X) / 2, sh - 24 * u, 13, new Color(255, 255, 255, 170));

        DrawStampPopup(sw, sh, u);
        if (bigMap) DrawBigMap(sw, sh, u);
        if (stampBook) DrawStampBook(sw, sh, u);
    }

    /// <summary>目的地（なければ一番近い未取得の名所）の方向を矢印で案内する。</summary>
    private void DrawFreeGuide(int sw, float u)
    {
        var f = free!;
        string name;
        double tx, tz;
        if (waypoint is (double wx, double wz))
        {
            name = "目的地";
            (tx, tz) = (wx, wz);
        }
        else
        {
            var target = f.SpotPos.Where(kv => !stamps.ContainsKey(kv.Key.Key))
                .OrderBy(kv => (kv.Value.X - f.PX) * (kv.Value.X - f.PX) + (kv.Value.Z - f.PZ) * (kv.Value.Z - f.PZ))
                .FirstOrDefault();
            if (target.Key == null) return;
            name = $"最寄りの名所：{target.Key.Name}（{target.Key.City.Name}）";
            (tx, tz) = target.Value;
        }
        double dx = tx - f.PX, dz = tz - f.PZ;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        double bearing = Math.Atan2(dx, dz) * 180 / Math.PI;
        string text = $"{name}　{Compass(bearing)}へ {dist / 1000:0.0} km";
        var m = Measure(text, 17);
        float w = m.X + 80 * u, x = (sw - w) / 2, y = 16 * u;
        Panel(x, y, w, 46 * u, new Color(60, 25, 20, 210));
        Text(text, x + 64 * u, y + 12 * u, 17, Rgb(255, 225, 140));
        // 自分の向きに対する方向（上が前）
        float rel = (float)((bearing - heading) * Math.PI / 180);
        var c = new Vector2(x + 32 * u, y + 23 * u);
        var fwd = new Vector2(MathF.Sin(rel), -MathF.Cos(rel));
        var side = new Vector2(-fwd.Y, fwd.X);
        Raylib.DrawTriangle(c + fwd * 15 * u, c - fwd * 10 * u - side * 9 * u, c - fwd * 10 * u + side * 9 * u, Rgb(255, 180, 60));
        Raylib.DrawTriangle(c + fwd * 15 * u, c - fwd * 10 * u + side * 9 * u, c - fwd * 10 * u - side * 9 * u, Rgb(255, 180, 60));
    }

    private void HandleFreeMapMouse()
    {
        bool left = Raylib.IsMouseButtonPressed(MouseButton.Left);
        bool right = Raylib.IsMouseButtonPressed(MouseButton.Right);
        if (!left && !right) return;
        var (center, origin, scale) = BigMapView();
        var mouse = Raylib.GetMousePosition();
        var mp = new PointF(center.X + (mouse.X - origin.X) / scale, center.Y + (mouse.Y - origin.Y) / scale);
        var (wx, wz) = FreeWorld.FromMap(mp);
        var f = free!;
        var (city, d) = f.NearestCity(wx, wz);
        string where = d < 3000 ? $"{city.Name}付近" : "その地点";
        if (left)
        {
            BeginFade(() =>
            {
                f.PX = wx;
                f.PZ = wz;
                freeKmh = 0;
                f.Cars.Clear();
                f.UpdateNear();
                bigMap = false;
                Audio.Play(Audio.Whoosh);
                game.AddLog($"{where}へワープしました。");
            });
        }
        else
        {
            waypoint = (wx, wz);
            game.AddLog($"{where}を目的地に設定しました（Esc で解除）。");
        }
    }
}
