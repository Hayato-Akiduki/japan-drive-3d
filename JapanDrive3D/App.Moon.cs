using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// フリーモードの月面エリア（地球からの大気圏突破遷移・月面1/6G運転・月面HUD）。
/// </summary>
public sealed partial class App
{
    private const float MoonGravity = 3.65f;      // 地球(22 m/s^2)の約 1/6G
    private const float MoonReturnAlt = 5000f;    // 月面から宇宙船で地球へ帰還する高度（m）
    private float earthHeading;

    private void EnterMoon(bool landed = false)
    {
        earthHeading = heading;
        moon = new MoonWorld();
        onMoon = true;
        bigMap = false;
        stampBook = false;
        shells.Clear();
        effects.Clear();
        freeOnWater = false;
        isDrifting = false;
        driftYaw = 0f;
        pitch = bank = terrainPitch = terrainBank = 0f;
        carAir = false;
        carVy = 0f;
        climbRate = 0f;

        if (landed)
        {
            moon.PX = 0;
            moon.PZ = -38;
            heading = 0;
            freeY = moon.SurfaceY(moon.PX, moon.PZ);
            groundLevel = freeY;
            altitude = 0f;
            freeKmh = 0f;
            prevAirborne = false;
            game.AddLog("月面「静かの海ベース」にいます（重力 1/6G）。V で乗り物を切り替えられます！");
        }
        else
        {
            moon.PX = 0;
            moon.PZ = -95;
            heading = 0;
            freeY = 240f;
            groundLevel = moon.SurfaceY(moon.PX, moon.PZ);
            altitude = freeY - groundLevel;
            freeKmh = 45f;
            prevAirborne = true;
            Audio.Play(Audio.Whoosh, 0.9f);
            game.AddLog("大気圏を突破し、月（静かの海ベース上空）に到着しました！");
            game.AddLog("Shift で着陸し、V キーで車に乗り換えると月の重力（1/6G）で運転できます！");
        }
        moon.Altitude = altitude;
    }

    private void ReturnToEarth()
    {
        onMoon = false;
        moon = null;
        shells.Clear();
        effects.Clear();
        isDrifting = false;
        driftYaw = 0f;
        pitch = bank = 0f;
        carAir = false;
        carVy = 0f;
        heading = earthHeading;
        if (free != null)
        {
            float surf = free.SurfaceY(free.PX, free.PZ);
            freeY = surf + 800f;
            groundLevel = surf;
            altitude = 800f;
            free.Altitude = altitude;
        }
        freeKmh = 180f;
        prevAirborne = true;
        Audio.Play(Audio.Whoosh, 0.9f);
        game.AddLog("地球（日本の上空）へ帰還しました！");
    }

    private void UpdateMoon(float dt)
    {
        var m = moon!;
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

        float surface = m.SurfaceY(m.PX, m.PZ);
        bool airborne = flyer ? freeY > surface + 0.5f : carAir;
        freeOnWater = false;

        UpdateTransmission(dt, freeKmh, up, out float accelMul, out float engineBrakeDecel, out float gearCapKmh);

        // 加速・ギア制御（月面では空気抵抗がほぼなく、慣性が効きやすい）
        if (manualTransmission && IsGearboxVehicle)
        {
            if (manualGear == -1)
            {
                float revCap = freeSpeedLimit ? Math.Min(44f, FreeSpeedCapKmh(playerKind, false) * 0.55f) : 44f;
                if (up || down)
                {
                    freeKmh = freeKmh > 0.5f
                        ? Math.Max(0, freeKmh - 115 * dt)
                        : Math.Max(-revCap, freeKmh - Accel(playerKind) * 0.85f * dt);
                }
                else
                {
                    freeKmh -= freeKmh * 0.14f * dt;
                    if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
                }
            }
            else if (manualGear == 0)
            {
                if (down) freeKmh = freeKmh > 0 ? Math.Max(0, freeKmh - 95 * dt) : Math.Min(0, freeKmh + 95 * dt);
                else
                {
                    freeKmh -= freeKmh * 0.04f * dt;
                    if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
                }
            }
            else
            {
                if (engineBrakeDecel > 0f && (!up || freeKmh > gearCapKmh))
                    freeKmh = Math.Max(0f, freeKmh - engineBrakeDecel * 0.7f * dt);
                if (up && accelMul > 0f && !carAir)
                {
                    float nextKmh = freeKmh + Accel(playerKind) * accelMul * 0.92f * (1 + Math.Max(0, freeKmh) / 500f) * dt;
                    freeKmh = freeKmh <= gearCapKmh ? Math.Min(gearCapKmh, nextKmh) : freeKmh;
                }
                else if (down && !carAir)
                {
                    freeKmh = Math.Max(0, freeKmh - 105 * dt);
                }
                else if (engineBrakeDecel <= 0f)
                {
                    freeKmh -= freeKmh * (carAir ? 0.01f : 0.05f) * dt;
                    if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
                }
            }
        }
        else
        {
            float cap = freeSpeedLimit ? FreeSpeedCapKmh(playerKind, false) : float.MaxValue;
            float revCap = freeSpeedLimit ? Math.Min(30f, cap * 0.5f) : 30f;
            if (freeSpeedLimit && freeKmh > cap)
                freeKmh = Math.Max(cap, freeKmh - Math.Max(35f, (freeKmh - cap) * 1.1f) * dt);
            else if (up && (!carAir || flyer))
            {
                float nextKmh = freeKmh + Accel(playerKind) * (flyer ? 1f : 0.90f) * (1 + Math.Max(0, freeKmh) / 500f) * dt;
                freeKmh = Math.Min(cap, nextKmh);
            }
            else if (down && airborne) freeKmh = Math.Max(0, freeKmh - 50 * dt);
            else if (down && !carAir) freeKmh = freeKmh > 0 ? Math.Max(0, freeKmh - 105 * dt) : Math.Max(-revCap, freeKmh - 18 * dt);
            else
            {
                freeKmh -= freeKmh * (airborne ? 0.015f : 0.06f) * dt;
                if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
            }
        }

        // 月面 1/6G の低重力ドリフト＆ホップ（地上車両）
        bool canDrift = !flyer && !carAir && playerKind is VehicleKind.Sports or VehicleKind.Amphibious;
        bool spacePressed = Raylib.IsKeyPressed(KeyboardKey.Space);
        bool handbrake = canDrift && (Raylib.IsKeyDown(KeyboardKey.Space) || (shotFile != null && demoDrift && demoTime > 1.0f));
        if (!flyer && !carAir && spacePressed && Math.Abs(steer) <= 0.15f)
        {
            // 月面の低重力ホップ：直進・停車中に Space でふわっとジャンプできる
            carAir = true;
            carVy = 5.6f;
            freeY += carVy * dt;
            AddMoonDust(m.PX, surface + 0.2f, m.PZ, 1.1f);
            game.AddLog("月面ジャンプ！（重力 1/6G）");
        }
        else if (handbrake && Math.Abs(freeKmh) > 20f && Math.Abs(steer) > 0.05f)
        {
            isDrifting = true;
            float targetDrift = -steer * Math.Clamp(Math.Abs(freeKmh) * 0.34f, 18f, 42f);
            driftYaw += (targetDrift - driftYaw) * Math.Min(1f, dt * 5.2f);
            freeKmh -= freeKmh * 0.08f * dt;

            driftSmokeTimer -= dt;
            if (driftSmokeTimer <= 0f)
            {
                driftSmokeTimer = 0.06f;
                double carRad = (heading - driftYaw) * Math.PI / 180.0;
                double bx = m.PX - Math.Sin(carRad) * 1.9, bz = m.PZ - Math.Cos(carRad) * 1.9;
                double rx = Math.Cos(carRad) * 0.85, rz = -Math.Sin(carRad) * 0.85;
                AddMoonDust(bx + rx, freeY + 0.18f, bz + rz, 0.7f);
                AddMoonDust(bx - rx, freeY + 0.18f, bz - rz, 0.7f);
            }
        }
        else
        {
            isDrifting = false;
            driftYaw += (0f - driftYaw) * Math.Min(1f, dt * 3.8f); // 月面は横滑りの収束がゆっくり
        }

        // ステアリング（月面はタイヤ荷重が軽く少しゆったり曲がる）
        if (flyer && airborne)
            heading += steer * (playerKind == VehicleKind.Spaceship ? 55f : 48f) * dt;
        else if (!carAir)
        {
            float turn = 64f / (1 + Math.Abs(freeKmh) / 220f) * Math.Min(1, Math.Abs(freeKmh) / 7f);
            if (isDrifting) turn *= 1.48f;
            heading += steer * turn * dt * (freeKmh < 0 ? -1 : 1);
        }
        else
        {
            // 滞空中もわずかに姿勢制御できる
            heading += steer * 16f * dt;
        }
        heading = (heading % 360 + 360) % 360;
        steerYaw += (-steer * Math.Min(1f, Math.Abs(freeKmh) / 36f) * 6f - steerYaw) * Math.Min(1, dt * 6);

        float ms = freeKmh / 3.6f;
        double hr = heading * Math.PI / 180;

        // ---- 飛行（宇宙船・航空機） ----
        float prevAlt = altitude, vs = 0f;
        if (flyer)
        {
            if (playerKind == VehicleKind.Spaceship)
            {
                vs = (climb ? 28f : descend ? -24f : 0f) * (1f + Math.Max(0f, altitude) / 140f);
                if (!climb && !descend && airborne) vs = -MoonGravity * 0.35f; // 月の弱い重力でゆっくり沈下
                freeY += vs * dt;
                float target = climb ? 20f : descend ? -16f : airborne ? Math.Clamp(-freeKmh / 180f, -5f, 0f) : 0f;
                pitch += Math.Clamp(target - pitch, -38 * dt, 38 * dt);
            }
            else
            {
                // 月は大気が薄いため通常の航空機は補助スラスター相当で低空ホバー
                vs = (climb ? 10f : descend ? -12f : -MoonGravity * 0.6f);
                freeY += vs * dt;
                pitch += Math.Clamp((climb ? 10f : descend ? -10f : 0f) - pitch, -30 * dt, 30 * dt);
            }
        }

        // ---- 前進＆基地ドームとの当たり判定 ----
        double nx = m.PX + Math.Sin(hr) * ms * dt;
        double nz = m.PZ + Math.Cos(hr) * ms * dt;
        crashCooldown -= dt;
        bool move = true;
        if (m.HitObstacle(nx, nz, freeY + 0.8f, PlayerHalfWidth + 0.5f))
        {
            if (Math.Abs(freeKmh) > 12 && crashCooldown <= 0)
            {
                AddMoonDust(m.PX + Math.Sin(hr) * 2, freeY + 0.8f, m.PZ + Math.Cos(hr) * 2, 1.2f);
                Audio.Play(Audio.Thud, 0.7f);
                shake = Math.Max(shake, 0.4f);
                crashCooldown = 0.5f;
            }
            freeKmh = -freeKmh * 0.25f;
            move = false;
        }
        if (move && !flyer && !carAir && Math.Abs(ms * dt) > 0.001f)
        {
            float nh = m.SurfaceY(nx, nz);
            float slope = (nh - surface) / (float)Math.Abs(ms * dt);
            // 月面は重力が1/6なので急坂でも登りやすく、坂道加減速もマイルド
            freeKmh -= Math.Clamp(slope, -1.5f, 1.5f) * 9.5f * dt;
        }
        if (move)
        {
            m.PX = nx;
            m.PZ = nz;
            animDist += Math.Abs(ms) * dt;
        }

        // ---- 高さ・月の重力 1/6G 落下＆ジャンプ ----
        surface = m.SurfaceY(m.PX, m.PZ);
        if (flyer)
        {
            if (freeY < surface)
            {
                freeY = surface;
            }
            altitude = freeY - surface;
            groundLevel = surface;
            if (playerKind == VehicleKind.Spaceship && altitude > MoonReturnAlt && fadeDir == 0)
            {
                BeginFade(ReturnToEarth);
            }
        }
        else
        {
            if (carAir)
            {
                carVy -= MoonGravity * dt;
                freeY += carVy * dt;
                if (freeY <= surface)
                {
                    if (carVy < -3.5f)
                    {
                        AddMoonDust(m.PX, surface + 0.25f, m.PZ, Math.Min(2.4f, -carVy / 4.5f));
                        Audio.Play(Audio.Thud, Math.Min(0.8f, -carVy / 14f));
                        shake = Math.Max(shake, Math.Min(0.55f, -carVy / 18f));
                    }
                    carAir = false;
                    carVy = 0f;
                    freeY = surface;
                }
            }
            else
            {
                float prevY = freeY;
                // クレーターの縁や丘を越えると 1/6G で簡単にふわっと大ジャンプ！
                if (surface < prevY - 0.14f && Math.Abs(ms) > 7f && climbRate > 0.55f)
                {
                    carAir = true;
                    carVy = Math.Max(climbRate * 1.15f, 2.2f);
                    freeY += carVy * dt;
                    AddMoonDust(m.PX, prevY + 0.2f, m.PZ, 1.0f);
                    game.AddLog("月面大ジャンプ！（重力 1/6G）");
                }
                else
                {
                    freeY = surface < prevY ? Math.Max(surface, prevY - 8.5f * dt) : surface;
                }
                climbRate = (freeY - prevY) / Math.Max(dt, 0.001f);
            }
            groundLevel = surface;
            altitude = Math.Max(0f, freeY - surface);
        }

        // 坂に合わせて車体を傾ける（滞空中は少し前上がり→ゆっくり水平へ）
        if (!flyer)
        {
            double fx = Math.Sin(hr), fz = Math.Cos(hr);
            float hf = m.SurfaceY(m.PX + fx * 2, m.PZ + fz * 2), hb = m.SurfaceY(m.PX - fx * 2, m.PZ - fz * 2);
            float hl = m.SurfaceY(m.PX - fz * 1.3, m.PZ + fx * 1.3), hrr = m.SurfaceY(m.PX + fz * 1.3, m.PZ - fx * 1.3);
            float tp = carAir ? Math.Clamp(carVy * 2.2f, -24f, 24f) : MathF.Atan2(hf - hb, 4) * 180 / MathF.PI;
            float tb = carAir ? terrainBank * 0.96f : MathF.Atan2(hl - hrr, 2.6f) * 180 / MathF.PI;
            terrainPitch += (Math.Clamp(tp, -36, 36) - terrainPitch) * Math.Min(1, dt * (carAir ? 3.5f : 10f));
            terrainBank += (Math.Clamp(tb, -36, 36) - terrainBank) * Math.Min(1, dt * (carAir ? 3.5f : 10f));
        }

        if (!prevAirborne && altitude > 0.5f && flyer) game.AddLog("月面から離陸！");
        else if (prevAirborne && altitude <= 0.5f && flyer)
        {
            AddMoonDust(m.PX, surface + 0.25f, m.PZ, 2.0f);
            shake = Math.Max(shake, 0.3f);
            game.AddLog("月面に着陸しました！ V キーで車に乗り換えて 1/6G ドライブを楽しめます。");
        }
        prevAirborne = flyer && altitude > 0.5f;

        float bankTarget = flyer && altitude > 0.5f ? steer * (playerKind == VehicleKind.Spaceship ? 24f : 18f) : 0;
        bank += (bankTarget - bank) * Math.Min(1, dt * 3);
        rotor += dt * (playerKind == VehicleKind.Helicopter ? 1400 : flyer ? 450 + Math.Abs(freeKmh) * 6 : 0);
        rotor %= 360000;
        m.Altitude = altitude;

        // 月面ダストエフェクト更新
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            effects[i].T += dt;
            if (effects[i].T > 1.4f) effects.RemoveAt(i);
        }

        shake = Math.Max(0, shake - dt);
        blastFlash = Math.Max(0, blastFlash - dt * 3.8f);
        game.SetClock(game.GameMinutes + dt);
        resetConfirm -= dt;
        UpdateCamera(dt);
    }

    private void AddMoonDust(double x, float y, double z, float size)
    {
        effects.Add(new Fx { Kind = FxKind.Dust, Free = true, WX = x, WZ = z, Y = y, Size = size, Seed = rng.Next(1000) });
    }

    private void DrawMoonWorld3D()
    {
        var m = moon!;
        scene.FogScale = 100f; // 月は大気がないため霧フェードを無効化
        m.Draw(camera.Position, time);

        if (IsFlyer(playerKind))
        {
            // 月面への影
            if (altitude < 450)
            {
                var g = m.R(m.PX, groundLevel + 0.08f, m.PZ);
                float hw = playerKind == VehicleKind.Spaceship ? 5.6f : 4.5f;
                float hl = playerKind == VehicleKind.Spaceship ? 6.2f : 4.5f;
                float hr = heading * MathF.PI / 180;
                var fw = new Vector3(-MathF.Sin(hr), 0, MathF.Cos(hr));
                var sd = new Vector3(fw.Z, 0, -fw.X);
                byte a = (byte)(120 * (1 - altitude / 450));
                Rlgl.Begin(DrawMode.Quads);
                foreach (var v in new[] { g + fw * hl + sd * hw, g + fw * hl - sd * hw, g - fw * hl - sd * hw, g - fw * hl + sd * hw })
                {
                    Rlgl.Color4ub(0, 0, 0, a);
                    Rlgl.Vertex3f(v.X, v.Y, v.Z);
                }
                Rlgl.End();
            }
            if (!cockpit)
            {
                var pos = m.R(m.PX, freeY, m.PZ);
                Rlgl.PushMatrix();
                Rlgl.Translatef(pos.X, pos.Y, pos.Z);
                Rlgl.Rotatef(-heading, 0, 1, 0);
                Rlgl.Rotatef(-pitch, 1, 0, 0);
                Rlgl.Rotatef(bank, 0, 0, 1);
                Aircraft.Draw(scene, playerKind, PlayerColor, 10, rotor, altitude < 10);
                Rlgl.PopMatrix();
            }
        }
        else
        {
            // ジャンプ中も月面に落ちる濃い影を描くと高さが分かりやすい
            if (carAir)
            {
                var g = m.R(m.PX, groundLevel + 0.08f, m.PZ);
                float scale = Math.Clamp(1f - altitude / 45f, 0.35f, 1f);
                byte a = (byte)(135 * scale);
                Raylib.DrawCylinder(g, 1.8f * scale, 1.8f * scale, 0.04f, 12, new Color((byte)0, (byte)0, (byte)0, a));
            }
            if (!cockpit)
            {
                scene.DrawVehicleAt(m.R(m.PX, freeY, m.PZ), -heading + steerYaw + driftYaw, PlayerColor, playerKind, FreeSpin,
                    braking: game.Brake,
                    wheelSteer: playerKind == VehicleKind.Sports ? steerYaw * 2.5f - driftYaw * 0.55f : 0,
                    turretYaw: turretYaw, recoil: recoil, shadow: !carAir, pitch: terrainPitch, bank: terrainBank);
            }
        }

        // 月面レゴリスダスト（ゆっくり舞い上がる灰色の砂煙）
        foreach (var e in effects)
        {
            float t = e.T / 1.4f;
            float r = e.Size * (0.4f + t * 1.4f);
            byte a = (byte)Math.Clamp(165 * (1 - t), 0, 165);
            var p = m.R(e.WX, e.Y + t * 1.8f, e.WZ);
            Raylib.DrawSphereEx(p, r, 6, 7, new Color((byte)175, (byte)178, (byte)184, a));
        }
    }

    private void DrawMoonHud()
    {
        var m = moon!;
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Ui;

        // 左上：月面状況パネル
        Panel(16 * u, 16 * u, 460 * u, 162 * u, new Color(12, 18, 30, 215));
        float x = 32 * u, y = 26 * u;
        Text("MOON  静かの海エリア", x, y, 21, Rgb(140, 215, 255));
        float capNow = FreeSpeedCapKmh(playerKind, false);
        string limitBadge = freeSpeedLimit ? $"F4 速度上限：{capNow:0} km/h" : "F4 速度上限：OFF";
        var bm = Measure(limitBadge, 13);
        Panel(16 * u + 444 * u - bm.X - 14 * u, y + 1 * u, bm.X + 14 * u, 23 * u,
            freeSpeedLimit ? new Color(165, 95, 20, 220) : new Color(35, 55, 85, 200));
        Text(limitBadge, 16 * u + 444 * u - bm.X - 7 * u, y + 4 * u, 13,
            freeSpeedLimit ? Rgb(255, 225, 130) : Sub);
        y += 30 * u;

        string stateText = IsFlyer(playerKind)
            ? (altitude > 0.5f ? $"飛行中（高度 {altitude:N0} m）" : "月面着陸中（Vで車に乗換可）")
            : (carAir ? $"月面ジャンプ中！（高度 {altitude:0.0} m）" : "月面ドライブ中（重力 1/6G）");
        Text($"{VehicleName(playerKind)}：{stateText}", x, y, 17, Color.White);
        y += 26 * u;

        double baseDist = Math.Sqrt(m.PX * m.PX + m.PZ * m.PZ);
        Text($"静かの海ベースから {baseDist:N0} m　向き：{Compass(heading)}", x, y, 15, Rgb(210, 220, 235));
        y += 24 * u;
        Text(playerKind == VehicleKind.Spaceship
            ? $"地球へ帰還：Space で高度 {MoonReturnAlt:N0} m まで上昇"
            : "地球へ帰還：V で宇宙船に乗り換えて高度 5,000 m へ", x, y, 14, Rgb(145, 225, 255));
        y += 22 * u;
        Text("重力：1.62 m/s²（地球の約 1/6G）", x, y, 13, Rgb(255, 220, 145));

        // 上部中央：基地方向ガイド
        if (baseDist > 25)
        {
            double bearing = (Math.Atan2(-m.PX, -m.PZ) * 180.0 / Math.PI + 360.0) % 360.0;
            string gText = $"静かの海ベース（着陸パッド）  {Compass(bearing)}へ {baseDist:N0} m";
            var gm = Measure(gText, 15);
            float gw = gm.X + 40 * u;
            Panel((sw - gw) / 2f, 16 * u, gw, 32 * u, new Color(18, 30, 50, 200));
            TextCentered(gText, sw / 2f, 23 * u, 15, Rgb(255, 215, 110));
        }

        // 右上：簡易月面レーダー
        DrawMoonRadar(sw - 196 * u, 16 * u, 180 * u, u);
        DrawSpeedometer(sw - 170 * u, sh - 175 * u, u);
        DrawLog(16 * u, sh - 150 * u, u);

        string help = playerKind == VehicleKind.Spaceship
            ? "↑↓ 推進/減速　←→ 旋回　Space 上昇（高度5,000mで地球帰還）　Shift 降下・着陸　V 車に乗り換え　F4 速度上限　C 視点"
            : "↑↓ アクセル/ブレーキ　←→ ハンドル　Space 月面ジャンプ / ドリフト　H AT/MT切替　V 乗り物(宇宙船で帰還)　F4 速度上限　C 視点";
        var hm = Measure(help, 13);
        Text(help, (sw - hm.X) / 2, sh - 24 * u, 13, new Color(255, 255, 255, 175));
    }

    private void DrawMoonRadar(float x, float y, float size, float u)
    {
        var m = moon!;
        Panel(x, y, size, size, new Color(10, 16, 26, 205));
        float cx = x + size * 0.5f, cy = y + size * 0.5f;
        float r = size * 0.42f;
        Raylib.DrawCircleLines((int)cx, (int)cy, r, new Color((byte)80, (byte)110, (byte)150, (byte)140));
        Raylib.DrawCircleLines((int)cx, (int)cy, r * 0.5f, new Color((byte)80, (byte)110, (byte)150, (byte)90));
        Text("N", cx - 4 * u, y + 6 * u, 12, Sub);

        // 静かの海ベース (0, 0) の相対位置（1px = 6m）
        float bx = cx + (float)(-m.PX / 6.0) * u;
        float by = cy - (float)(-m.PZ / 6.0) * u;
        float dx = bx - cx, dy = by - cy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > r) { bx = cx + dx / dist * r; by = cy + dy / dist * r; }
        Raylib.DrawCircleV(new Vector2(bx, by), 4.5f * u, Orange);
        Text("基地", bx + 6 * u, by - 6 * u, 11, Rgb(255, 215, 130));

        // 自機マーカー（向き矢印）
        float hr = heading * MathF.PI / 180f;
        var tip = new Vector2(cx + MathF.Sin(hr) * 7 * u, cy - MathF.Cos(hr) * 7 * u);
        var lft = new Vector2(cx + MathF.Sin(hr - 2.4f) * 5 * u, cy - MathF.Cos(hr - 2.4f) * 5 * u);
        var rgt = new Vector2(cx + MathF.Sin(hr + 2.4f) * 5 * u, cy - MathF.Cos(hr + 2.4f) * 5 * u);
        Raylib.DrawTriangle(tip, rgt, lft, Rgb(120, 230, 255));
        Text("月面レーダー", x + 8 * u, y + size - 18 * u, 11, Sub);
    }
}
