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
    private (double Lat, double Lon)? demoLatLon;
    private bool demoBigMap;
    private float demoAccel = 3.5f;
    private float demoSteer;

    private void StartFree()
    {
        free = new FreeWorld(scene);
        var city = MapData.Get(demoAt ?? "東京");
        (free.PX, free.PZ) = FreeWorld.CityPos(city);
        if (demoLatLon is (double dLat, double dLon)) (free.PX, free.PZ) = FreeWorld.FromLatLon(dLat, dLon);
        if (demoBigMap) bigMap = true;
        bool resume = Persist && demoAt == null && demoLatLon == null && save.FreeX is double && save.FreeZ is double;
        if (resume)
        {
            free.PX = save.FreeX!.Value;
            free.PZ = save.FreeZ!.Value;
            heading = save.FreeHeading;
        }
        free.UpdateNear();
        freeKmh = 0;
        freeY = groundLevel = free.SurfaceY(free.PX, free.PZ);
        altitude = pitch = bank = terrainPitch = terrainBank = 0;
        carAir = prevAirborne = false;
        carVy = climbRate = 0;
        freeOnWater = false;
        animDist = 0;
        waypoint = null;
        game.Log.Clear();
        game.AddLog(resume
            ? $"フリーモード：前回の続き（{free.NearestPlace(free.PX, free.PZ).Name}付近）から再開しました。"
            : $"フリーモード：{free.NearestPlace(free.PX, free.PZ).Name}から出発。道路も海も、日本中どこでも自由に走れます（速度制限なし）。");
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

        float surface = f.SurfaceY(f.PX, f.PZ);
        float ground = flyer ? Math.Max(surface, f.RoofY(f.PX, f.PZ)) : surface;
        bool airborne = flyer ? freeY > ground + 0.5f : carAir;

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
        bool water = !road && !f.IsLand(f.PX, f.PZ);
        if (water && !airborne && playerKind != VehicleKind.Amphibious && !flyer)
            freeKmh = Math.Clamp(freeKmh, -15, 30); // 水の中はゆっくり

        if (flyer && airborne)
            heading += steer * (playerKind == VehicleKind.Helicopter ? 60 : 45) * dt;
        else if (!carAir)
        {
            float turn = 75f / (1 + Math.Abs(freeKmh) / 250f) * Math.Min(1, Math.Abs(freeKmh) / 8f);
            heading += steer * turn * dt * (freeKmh < 0 ? -1 : 1);
        }
        heading = (heading % 360 + 360) % 360;
        steerYaw += (-steer * Math.Min(1f, Math.Abs(freeKmh) / 36f) * 6f - steerYaw) * Math.Min(1, dt * 6);

        float ms = freeKmh / 3.6f;
        double hr = heading * Math.PI / 180;

        // ---- 飛行（高さは海面からの高さ） ----
        float prevAlt = altitude, vs = 0;
        if (playerKind == VehicleKind.Helicopter)
        {
            vs = (climb ? 14 : descend ? -14 : 0) * (1 + Math.Max(0, altitude) / 500f);
            freeY += vs * dt;
            float target = airborne ? -Math.Clamp(freeKmh / 25f, -8, 14) : 0;   // 前進中は機首を下げる
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
            freeY += vs * dt;
            ms *= MathF.Cos(pitch * MathF.PI / 180);
        }
        if (freeY > 30000)
        {
            freeY = 30000;
            if (prevAlt < 29000) game.AddLog("高度3万m：これ以上は宇宙です！");
        }

        // ---- 前へ進む（建物・急な山にはぶつかる） ----
        double nx = f.PX + Math.Sin(hr) * ms * dt, nz = f.PZ + Math.Cos(hr) * ms * dt;
        bool move = true;
        crashCooldown -= dt;
        var block = f.HitBlock(nx, nz, freeY + 0.8f, PlayerHalfWidth + 0.6f);
        if (block != null)
        {
            if (playerKind == VehicleKind.Tank && Math.Abs(freeKmh) > 8)
            {
                DestroyBlock(block);   // 戦車はビルを突き破る
                freeKmh *= 0.6f;
            }
            else if (flyer && airborne && Math.Abs(freeKmh) > 60)
            {
                AircraftCrash("ビルに激突！");
                move = false;
            }
            else
            {
                if (Math.Abs(freeKmh) > 15 && crashCooldown <= 0)
                {
                    AddFxW(FxKind.Dust, f.PX + Math.Sin(hr) * 2, freeY + 0.8f, f.PZ + Math.Cos(hr) * 2, 1.3f);
                    Audio.Play(Audio.Crash, Math.Min(1, Math.Abs(freeKmh) / 120));
                    shake = Math.Max(shake, Math.Min(0.9f, Math.Abs(freeKmh) / 150));
                    crashCooldown = 0.5f;
                }
                freeKmh = -freeKmh * 0.25f;   // はね返る
                move = false;
            }
        }
        if (move && !flyer && !carAir && Math.Abs(ms * dt) > 0.001f)
        {
            float nh = f.SurfaceY(nx, nz);
            float slope = (nh - surface) / (float)Math.Abs(ms * dt);
            if (slope > 1.1f && nh > surface + 0.3f)
            {
                // 急すぎる斜面は壁と同じ
                if (Math.Abs(freeKmh) > 20 && crashCooldown <= 0)
                {
                    Audio.Play(Audio.Thud, 0.6f);
                    shake = Math.Max(shake, 0.3f);
                    crashCooldown = 0.5f;
                }
                freeKmh = -freeKmh * 0.2f;
                move = false;
            }
            else freeKmh -= Math.Clamp(slope, -2, 2) * 35 * dt;   // 上り坂で遅く、下り坂で速く
        }
        if (move)
        {
            f.PX = nx;
            f.PZ = nz;
            animDist += Math.Abs(ms) * dt;
        }

        // ---- 高さ ----
        road = f.OnRoad(f.PX, f.PZ);
        bool land = road || f.IsLand(f.PX, f.PZ);
        surface = f.SurfaceY(f.PX, f.PZ);
        if (flyer)
        {
            ground = Math.Max(surface, f.RoofY(f.PX, f.PZ));
            if (freeY < ground)
            {
                if (prevAlt > 2 && (vs < -15 || Math.Abs(freeKmh) > 220)) AircraftCrash("墜落！");
                freeY = ground;
            }
            altitude = freeY - ground;
            groundLevel = ground;
            water = !land && altitude < 0.5f;
        }
        else
        {
            water = !land;
            float targetY = land ? surface
                : playerKind == VehicleKind.Amphibious ? FreeWorld.SeaY - 0.9f + MathF.Sin(time * 1.6f) * 0.15f
                : FreeWorld.SeaY - 0.8f;
            if (carAir)
            {
                carVy -= 22 * dt;
                freeY += carVy * dt;
                if (freeY <= targetY)
                {
                    if (carVy < -9)
                    {
                        AddFxW(FxKind.Dust, f.PX, targetY + 0.3f, f.PZ, Math.Min(2.5f, -carVy / 8));
                        shake = Math.Max(shake, Math.Min(0.8f, -carVy / 30));
                    }
                    carAir = false;
                    carVy = 0;
                    freeY = targetY;
                }
            }
            else
            {
                float prevY = freeY;
                // 丘のてっぺんを速く越えると、地面が離れていってジャンプする
                if (land && targetY < prevY - 0.6f && Math.Abs(ms) > 14 && climbRate > 1.5f)
                {
                    carAir = true;
                    carVy = climbRate;
                    freeY += carVy * dt;
                    game.AddLog("ジャンプ！");
                }
                else freeY = targetY < prevY ? Math.Max(targetY, prevY - 40 * dt) : targetY;
                climbRate = (freeY - prevY) / Math.Max(dt, 0.001f);
            }
            groundLevel = targetY;
            altitude = 0;
        }

        // 坂に合わせて車体を傾ける
        if (!flyer)
        {
            double fx = Math.Sin(hr), fz = Math.Cos(hr);
            float hf = f.SurfaceY(f.PX + fx * 2, f.PZ + fz * 2), hb = f.SurfaceY(f.PX - fx * 2, f.PZ - fz * 2);
            float hl = f.SurfaceY(f.PX - fz * 1.3, f.PZ + fx * 1.3), hrr = f.SurfaceY(f.PX + fz * 1.3, f.PZ - fx * 1.3);
            float tp = carAir ? terrainPitch : MathF.Atan2(hf - hb, 4) * 180 / MathF.PI;
            float tb = carAir ? terrainBank : MathF.Atan2(hl - hrr, 2.6f) * 180 / MathF.PI;
            terrainPitch += (Math.Clamp(tp, -40, 40) - terrainPitch) * Math.Min(1, dt * 10);
            terrainBank += (Math.Clamp(tb, -40, 40) - terrainBank) * Math.Min(1, dt * 10);
        }

        if (!prevAirborne && altitude > 0.5f && flyer) game.AddLog("離陸！");
        else if (prevAirborne && altitude <= 0.5f && flyer && !crashedThisFrame)
        {
            AddFxW(FxKind.Dust, f.PX, groundLevel + 0.3f, f.PZ, vs < -12 ? 2.5f : 1.2f);
            shake = Math.Max(shake, vs < -12 ? 0.7f : 0.2f);
            game.AddLog(vs < -12 ? "ドスン！ 強めに着地しました。" : "着陸しました。");
        }
        prevAirborne = flyer && altitude > 0.5f;
        crashedThisFrame = false;
        float bankTarget = flyer && altitude > 0.5f ? steer * (playerKind == VehicleKind.Helicopter ? 12 : 35) : 0;
        bank += (bankTarget - bank) * Math.Min(1, dt * 3);
        rotor += dt * (playerKind == VehicleKind.Helicopter ? 1400 : playerKind == VehicleKind.Airplane ? 400 + Math.Abs(freeKmh) * 6 : 0);
        rotor %= 360000;

        // 高いところ（山の上・空）ほど遠くまで見えるようにする
        scene.FogScale = 1 + Math.Max(0, freeY) / 120f;
        f.ViewRadius = Math.Min(14000, 1000 * scene.FogScale);
        f.Altitude = altitude;

        if (water != freeOnWater)
        {
            freeOnWater = water;
            AddFxW(FxKind.Splash, f.PX, FreeWorld.SeaY, f.PZ, 1.6f);
            if (water) game.AddLog(playerKind == VehicleKind.Amphibious ? "水に入りました！ 水上を航行します。" : "水に入りました（水の中はゆっくりしか進めません）。");
        }
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
        if (crashCooldown <= 0 && Math.Abs(ms) > 2 && !water && freeY - surface < 2)
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

    // 地形まわりの状態
    private bool carAir;            // 車がジャンプ中
    private float carVy, climbRate;
    private float terrainPitch, terrainBank;
    private bool prevAirborne, crashedThisFrame;

    /// <summary>飛行機・ヘリが山やビルにぶつかった（ゲームなので機体は無事で、その場に止まる）。</summary>
    private void AircraftCrash(string what)
    {
        var f = free!;
        AddFxW(FxKind.Fire, f.PX, freeY + 1, f.PZ, 2.2f);
        shake = 1;
        freeKmh = 0;
        pitch = 0;
        crashedThisFrame = true;
        prevAirborne = false;
        game.AddLog($"{what} 機体は無事でした。↑と Space でもう一度飛べます。");
    }

    /// <summary>建物を壊す（戦車の体当たり・砲撃）。</summary>
    private void DestroyBlock(Block b)
    {
        b.Destroyed = true;
        for (int i = 0; i < 3; i++) AddFxW(FxKind.Fire, b.X, b.H * (0.2f + i * 0.3f), b.Z, Math.Min(3.5f, 1.2f + b.H / 40));
        AddFxW(FxKind.Dust, b.X, 1, b.Z, 3);
        shake = Math.Max(shake, 0.6f);
        destroyed++;
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
            var bld = f.HitBlock(sh.WX, sh.WZ, sh.Y, 0.3f);
            if (bld != null)
            {
                DestroyBlock(bld);
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
                turretYaw: turretYaw, recoil: recoil, shadow: !freeOnWater && !carAir, pitch: terrainPitch, bank: terrainBank);
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
        float labelRange = (float)Math.Max(1600, f.ViewRadius * 0.6);
        foreach (var pl in f.Places)
        {
            var p = f.R(pl.X, 70 + pl.Radius * 0.05f, pl.Z);
            float d = p.Length();
            if (d > labelRange || Vector3.Dot(p - camera.Position, look) < 5) continue;
            var sp = Raylib.GetWorldToScreen(p, camera);
            float size = Math.Clamp(26 - d / 80, 13, 26) - (pl.Tier >= 3 ? 2 : 0);
            var m = Measure(pl.Name, size);
            Panel(sp.X - m.X / 2 - 8 * u, sp.Y - 4 * u, m.X + 16 * u, m.Y + 8 * u, new Color(10, 40, 80, 170));
            Text(pl.Name, sp.X - m.X / 2, sp.Y, size, Color.White);
        }
        // 山の名前と標高
        foreach (var (name, px, pz, elev) in f.PeakList)
        {
            var p = f.R(px, f.TerrainHeight(px, pz) + 25, pz);
            float d = p.Length();
            if (d > Math.Max(5000, f.ViewRadius) || Vector3.Dot(p - camera.Position, look) < 5) continue;
            var sp = Raylib.GetWorldToScreen(p, camera);
            string label = $"▲{name} {elev:0}m";
            var m = Measure(label, 13);
            Text(label, sp.X - m.X / 2 + 1, sp.Y + 1, 13, new Color(0, 0, 0, 160));
            Text(label, sp.X - m.X / 2, sp.Y, 13, Rgb(255, 245, 220));
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
        var (near, nd) = f.NearestPlace(f.PX, f.PZ);
        Panel(16 * u, 16 * u, 430 * u, 150 * u);
        float x = 32 * u, y = 28 * u;
        Text(Game.FormatClock(game.GameMinutes), x, y, 22, Orange);
        y += 32 * u;
        Text("フリーモード（道路も速度も自由）", x, y, 18, Color.White);
        y += 28 * u;
        string elevText = IsFlyer(playerKind) && altitude > 0.5f ? "" : $"　標高 {Math.Max(0, groundLevel) / FreeWorld.VScale:N0} m";
        Text($"{near}付近（{nd / 1000:0.0} km）　向き：{Compass(heading)}{elevText}", x, y, 15, Rgb(220, 225, 235));
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
        var (placeName, d) = f.NearestPlace(wx, wz);
        string where = d < 3000 ? $"{placeName}付近" : "その地点";
        if (left)
        {
            BeginFade(() =>
            {
                f.PX = wx;
                f.PZ = wz;
                freeKmh = 0;
                freeY = groundLevel = f.SurfaceY(wx, wz);
                carAir = false;
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
