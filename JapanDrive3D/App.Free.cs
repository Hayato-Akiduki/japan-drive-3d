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

    private static bool IsFlyer(VehicleKind k) => k is VehicleKind.Helicopter or VehicleKind.Airplane or VehicleKind.Fighter;
    private bool IsGearboxVehicle => !IsFlyer(playerKind) && playerKind is not (VehicleKind.Horse or VehicleKind.Hikyaku);

    // ---- マニュアル操作（6速MT・タコメーター・ドリフト） ----
    private bool manualTransmission;
    private int manualGear = 1;     // -1: R（後退）, 0: N（ニュートラル）, 1〜6: 1速〜6速
    private float engineRpm = 850;
    private bool revLimiting;
    private float shiftAnim;
    private float driftYaw;
    private bool isDrifting;
    private float driftSmokeTimer;

    // デモ用
    private float demoClimb;
    private (double Lat, double Lon)? demoLatLon;
    private bool demoBigMap;
    private float demoAccel = 3.5f;
    private float demoSteer;
    private bool demoMt;
    private int? demoGear;
    private bool demoDrift;

    private float GearTopKmh(int gear)
    {
        float scale = playerKind == VehicleKind.Tank ? 0.62f : playerKind == VehicleKind.Amphibious ? 0.72f : 1.0f;
        float baseTop = gear switch
        {
            <= 1 => 48f,
            2 => 88f,
            3 => 132f,
            4 => 182f,
            5 => 245f,
            _ => 340f,
        };
        return baseTop * scale;
    }

    private int AutoGearForKmh(float kmh)
    {
        if (kmh < -1f) return -1;
        float a = Math.Abs(kmh);
        if (a < 38f) return 1;
        if (a < 72f) return 2;
        if (a < 112f) return 3;
        if (a < 156f) return 4;
        if (a < 210f) return 5;
        return 6;
    }

    private int ActiveGear => manualTransmission ? manualGear : AutoGearForKmh(CurrentKmh);

    private static string GearLabel(int gear, bool mt) => gear switch
    {
        < 0 => "R",
        0 => "N",
        _ => mt ? $"{gear}速" : $"D{gear}",
    };

    private void ShiftGear(int delta)
    {
        if (!IsGearboxVehicle) return;
        if (!manualTransmission)
        {
            manualTransmission = true;
            manualGear = AutoGearForKmh(CurrentKmh);
        }
        int next = Math.Clamp(manualGear + delta, -1, 6);
        if (next == manualGear) return;
        int prev = manualGear;
        manualGear = next;
        shiftAnim = 1.0f;
        Audio.Play(Audio.Shift, 0.85f, delta > 0 ? 1.05f : 0.92f);

        // シフトダウン時に回転数が跳ね上がるブリッピング演出
        if (delta < 0 && manualGear >= 1 && CurrentKmh > 10f)
        {
            float newTop = GearTopKmh(manualGear);
            engineRpm = Math.Clamp(900f + (CurrentKmh / newTop) * 7400f + 500f, 1500f, 9000f);
        }
        else if (delta > 0 && prev >= 1 && manualGear > prev)
        {
            engineRpm = Math.Max(1100f, engineRpm * 0.68f);
        }
    }

    private void HandleTransmissionKeys(bool allowQE)
    {
        if (!IsGearboxVehicle || bigMap || stampBook) return;

        if (Raylib.IsKeyPressed(KeyboardKey.H) || Raylib.IsKeyPressed(KeyboardKey.Tab))
        {
            manualTransmission = !manualTransmission;
            if (manualTransmission)
            {
                manualGear = AutoGearForKmh(CurrentKmh);
                shiftAnim = 1.0f;
                Audio.Play(Audio.Shift, 0.9f);
                game.AddLog($"ミッション：6速マニュアル（MT / 現在 {GearLabel(manualGear, true)}）に切替！ E・Shift・ホイール↑でシフトアップ、Q・Ctrl・ホイール↓でシフトダウン");
            }
            else
            {
                revLimiting = false;
                Audio.Play(Audio.Select, 0.8f);
                game.AddLog("ミッション：オートマ（AT）に切り替えました（H キーでいつでも MT に戻せます）。");
            }
        }

        float wheel = Raylib.GetMouseWheelMove();
        bool shiftUp = (allowQE && Raylib.IsKeyPressed(KeyboardKey.E))
            || Raylib.IsKeyPressed(KeyboardKey.RightShift)
            || Raylib.IsKeyPressed(KeyboardKey.LeftShift)
            || Raylib.IsKeyPressed(KeyboardKey.PageUp)
            || (manualTransmission && wheel > 0.1f);

        bool shiftDown = (allowQE && Raylib.IsKeyPressed(KeyboardKey.Q))
            || (playerKind != VehicleKind.Tank && (Raylib.IsKeyPressed(KeyboardKey.LeftControl) || Raylib.IsKeyPressed(KeyboardKey.RightControl)))
            || Raylib.IsKeyPressed(KeyboardKey.PageDown)
            || (manualTransmission && wheel < -0.1f);

        if (shiftUp) ShiftGear(1);
        else if (shiftDown) ShiftGear(-1);
    }

    private void UpdateTransmission(float dt, float kmh, bool throttle, out float accelMul, out float engineBrakeDecel, out float gearCapKmh)
    {
        shiftAnim = Math.Max(0f, shiftAnim - dt * 3.5f);
        if (!IsGearboxVehicle)
        {
            engineRpm = 850f;
            revLimiting = false;
            isDrifting = false;
            driftYaw = 0f;
            accelMul = 1f;
            engineBrakeDecel = 0f;
            gearCapKmh = 9999f;
            return;
        }

        int g = ActiveGear;
        float top = GearTopKmh(Math.Max(1, Math.Abs(g)));

        if (!manualTransmission)
        {
            revLimiting = false;
            float ratio = Math.Clamp(Math.Abs(kmh) / top, 0f, 1f);
            float targetRpm = Math.Abs(kmh) < 0.5f && !throttle ? 850f : 1450f + ratio * 5600f + (throttle ? 450f : 0f);
            engineRpm += (targetRpm - engineRpm) * Math.Min(1f, dt * 10f);
            accelMul = 1f;
            engineBrakeDecel = 0f;
            gearCapKmh = 9999f;
            return;
        }

        if (g == 0)
        {
            // N（ニュートラル）：アクセルで軽快に空吹かしできるが駆動力は伝わらない
            float targetRpm = throttle ? 8300f : 850f;
            engineRpm += (targetRpm - engineRpm) * Math.Min(1f, dt * (throttle ? 10f : 5.5f));
            revLimiting = throttle && engineRpm > 7900f;
            accelMul = 0f;
            engineBrakeDecel = 0f;
            gearCapKmh = 0f;
            return;
        }

        if (g < 0)
        {
            // R（リバース）
            float revTop = 44f;
            float revKmh = Math.Max(0f, -kmh);
            float targetRpm = 950f + (revKmh / revTop) * 7200f + (throttle ? 400f : 0f);
            engineRpm += (Math.Clamp(targetRpm, 850f, 8900f) - engineRpm) * Math.Min(1f, dt * 12f);
            revLimiting = throttle && revKmh >= revTop - 1f;
            accelMul = 0f;
            engineBrakeDecel = kmh > 1f ? 95f : 0f;
            gearCapKmh = 0f;
            return;
        }

        // 1速〜6速
        float gearRatio = Math.Max(0f, kmh) / top;
        float rpmTarget = 900f + gearRatio * 7500f + (throttle && kmh < 4f ? 650f : 0f);
        engineRpm += (Math.Clamp(rpmTarget, 820f, 9200f) - engineRpm) * Math.Min(1f, dt * 14f);

        bool hasHardCap = !(freeRoam && g == 6); // フリーモード6速はオーバードライブ（最高速無制限）
        gearCapKmh = hasHardCap ? top : 9999f;
        engineBrakeDecel = 0f;

        if (hasHardCap && kmh > top + 2.5f)
        {
            // 高速走行中に低いギアへシフトダウンしたときの強烈なエンジンブレーキ＆オーバーレブ
            revLimiting = true;
            accelMul = 0f;
            engineBrakeDecel = 85f + (kmh - top) * 2.0f;
            return;
        }

        if (hasHardCap && throttle && kmh >= top - 0.6f)
        {
            // レブリミッター作動（シフトアップするまで頭打ち）
            revLimiting = true;
            accelMul = 0f;
            return;
        }

        revLimiting = false;

        // パワーバンド（3,800〜7,800 rpm）で最大1.38倍の鋭い加速、低回転×高ギアではトルク不足（ノッキング気味）
        if (engineRpm >= 3800f && engineRpm <= 8100f)
        {
            accelMul = g == 1 ? 1.45f : g == 2 ? 1.40f : 1.35f;
        }
        else if (engineRpm < 2500f && g >= 3)
        {
            float lug = Math.Clamp((engineRpm - 900f) / 1600f, 0f, 1f);
            accelMul = 0.25f + lug * 0.60f;
        }
        else
        {
            accelMul = g <= 2 ? 1.25f : 1.0f;
        }

        // アクセルオフ時のエンジンブレーキ（低速ギアほど強く効く）
        if (!throttle && kmh > 2f)
        {
            engineBrakeDecel = (7 - g) * 4.8f * Math.Clamp(engineRpm / 5200f, 0.45f, 1.5f);
        }
    }

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
        freeOnWater = !free.OnRoad(free.PX, free.PZ) && free.IsInWater(free.PX, free.PZ);
        if (freeOnWater && !IsFlyer(playerKind))
        {
            float floatY = playerKind == VehicleKind.Amphibious ? FreeWorld.WaterY - 0.72f : FreeWorld.WaterY - 0.68f;
            freeY = groundLevel = Math.Max(freeY, floatY);
        }
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
        VehicleKind.Fighter => 120,
        VehicleKind.Sports => 60,
        VehicleKind.Tank => 24,
        VehicleKind.Amphibious => 28,
        VehicleKind.Horse => 32,
        VehicleKind.Hikyaku => 20,
        _ => 45,
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

        UpdateTransmission(dt, freeKmh, up, out float accelMul, out float engineBrakeDecel, out float gearCapKmh);

        // 加速・ギア制御・エンジンブレーキ
        if (manualTransmission && IsGearboxVehicle)
        {
            if (manualGear == -1)
            {
                // R（リバース）ギア：アクセル（↑/W）または↓/Sで力強くバック、前進中なら急制動
                if (up || down)
                {
                    freeKmh = freeKmh > 0.5f
                        ? Math.Max(0, freeKmh - 165 * dt)
                        : Math.Max(-44f, freeKmh - Accel(playerKind) * 0.85f * dt);
                }
                else
                {
                    freeKmh -= freeKmh * 0.25f * dt;
                    if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
                }
            }
            else if (manualGear == 0)
            {
                // N（ニュートラル）：アクセルは空吹かしになり、ブレーキまたは自然な惰性減速のみ
                if (down) freeKmh = freeKmh > 0 ? Math.Max(0, freeKmh - 140 * dt) : Math.Min(0, freeKmh + 140 * dt);
                else
                {
                    freeKmh -= freeKmh * 0.08f * dt;
                    if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
                }
            }
            else
            {
                // 1速〜6速：パワーバンド加速・レブリミッター・エンジンブレーキ
                if (engineBrakeDecel > 0f && (!up || freeKmh > gearCapKmh))
                    freeKmh = Math.Max(0f, freeKmh - engineBrakeDecel * dt);

                if (up && accelMul > 0f)
                {
                    float nextKmh = freeKmh + Accel(playerKind) * accelMul * (1 + Math.Max(0, freeKmh) / 500f) * dt;
                    freeKmh = freeKmh <= gearCapKmh ? Math.Min(gearCapKmh, nextKmh) : freeKmh;
                }
                else if (down)
                {
                    freeKmh = Math.Max(0, freeKmh - 150 * dt);
                }
                else if (engineBrakeDecel <= 0f)
                {
                    freeKmh -= freeKmh * 0.10f * dt;
                    if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
                }
            }
        }
        else
        {
            // AT（または航空機・馬など）：速度制限なし、踏み続けるほど速くなる
            if (up) freeKmh += Accel(playerKind) * (1 + Math.Max(0, freeKmh) / 500f) * dt;
            else if (down && airborne) freeKmh = Math.Max(0, freeKmh - 60 * dt);   // 空中ではバックしない
            else if (down) freeKmh = freeKmh > 0 ? Math.Max(0, freeKmh - 140 * dt) : Math.Max(-30, freeKmh - 20 * dt);
            else
            {
                freeKmh -= freeKmh * 0.12f * dt;
                if (Math.Abs(freeKmh) < 0.5f) freeKmh = 0;
            }
        }

        bool road = f.OnRoad(f.PX, f.PZ);
        bool water = !road && f.IsInWater(f.PX, f.PZ);
        if (water && !airborne && playerKind != VehicleKind.Amphibious && !flyer)
            freeKmh = Math.Clamp(freeKmh, -15, 30); // 水の中はゆっくり

        // サイドブレーキ＆ドリフト（地上車両で Space キー）
        bool canDrift = !flyer && !carAir && !water && playerKind is VehicleKind.Sports or VehicleKind.Amphibious;
        bool handbrake = canDrift && (Raylib.IsKeyDown(KeyboardKey.Space) || (shotFile != null && demoDrift && demoTime > 1.0f));
        if (handbrake && Math.Abs(freeKmh) > 24f && Math.Abs(steer) > 0.05f)
        {
            isDrifting = true;
            float targetDrift = -steer * Math.Clamp(Math.Abs(freeKmh) * 0.28f, 14f, 34f);
            driftYaw += (targetDrift - driftYaw) * Math.Min(1f, dt * 8.5f);
            // アクセルを踏みながらパワーバンドを維持すると速度を落とさず豪快なパワードリフトができる
            float scrub = (up && engineRpm > 3600f) ? 0.06f : 0.22f;
            freeKmh -= freeKmh * scrub * dt;

            driftSmokeTimer -= dt;
            if (driftSmokeTimer <= 0f)
            {
                driftSmokeTimer = 0.045f;
                double carRad = (heading - driftYaw) * Math.PI / 180.0;
                double bx = f.PX - Math.Sin(carRad) * 1.9, bz = f.PZ - Math.Cos(carRad) * 1.9;
                double rx = Math.Cos(carRad) * 0.85, rz = -Math.Sin(carRad) * 0.85;
                AddFxW(FxKind.Dust, bx + rx, freeY + 0.18f, bz + rz, 0.72f, playSound: false);
                AddFxW(FxKind.Dust, bx - rx, freeY + 0.18f, bz - rz, 0.72f, playSound: false);
            }
        }
        else
        {
            if (isDrifting && Math.Abs(driftYaw) > 12f && up && freeKmh > 30f && engineRpm > 3500f)
            {
                // ドリフト立ち上がり加速（グリップ回復ブースト）
                freeKmh += 10f;
            }
            isDrifting = false;
            driftYaw += (0f - driftYaw) * Math.Min(1f, dt * 7.5f);
            if (handbrake && Math.Abs(freeKmh) > 0.5f)
            {
                freeKmh = freeKmh > 0 ? Math.Max(0, freeKmh - 175 * dt) : Math.Min(0, freeKmh + 175 * dt);
            }
        }

        if (flyer && airborne)
            heading += steer * (playerKind == VehicleKind.Helicopter ? 60 : playerKind == VehicleKind.Fighter ? 55 : 45) * dt;
        else if (!carAir)
        {
            float turn = 75f / (1 + Math.Abs(freeKmh) / 250f) * Math.Min(1, Math.Abs(freeKmh) / 8f);
            if (isDrifting) turn *= 1.52f;
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
        else if (playerKind is VehicleKind.Airplane or VehicleKind.Fighter)
        {
            float stallKmh = playerKind == VehicleKind.Fighter ? 150 : 140;
            float maxPitch = playerKind == VehicleKind.Fighter ? 26 : 20;
            float target = climb ? maxPitch : descend ? -maxPitch : 0;
            if (!airborne && (freeKmh < stallKmh || target < 0)) target = 0;   // 遅いと離陸できない
            pitch += Math.Clamp(target - pitch, -45 * dt, 45 * dt);
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
            // 水際・海岸・川岸や道路への乗り上げ時は絶対に壁判定で押し戻さない（急峻な山の崖のみブロック）
            bool nearWaterOrRoad = surface < 0.35f || nh < 0.65f || f.OnRoad(nx, nz, 15.0);
            if (!nearWaterOrRoad && slope > 1.25f && nh > surface + 0.45f)
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
            else if (nearWaterOrRoad)
                freeKmh -= Math.Clamp(slope, -1f, 0.35f) * 14 * dt;   // 岸辺や浅瀬は力を保って上陸できる
            else
                freeKmh -= Math.Clamp(slope, -2, 2) * 35 * dt;        // 上り坂で遅く、下り坂で速く
        }
        if (move)
        {
            f.PX = nx;
            f.PZ = nz;
            animDist += Math.Abs(ms) * dt;
        }

        // ---- 高さ ----
        road = f.OnRoad(f.PX, f.PZ);
        bool land = road || !f.IsInWater(f.PX, f.PZ);
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
            float floatY = playerKind == VehicleKind.Amphibious
                ? FreeWorld.WaterY - 0.72f + MathF.Sin(time * 1.6f) * 0.12f
                : FreeWorld.WaterY - 0.68f;
            float targetY = land ? surface : Math.Max(surface, floatY);
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
        float bankTarget = flyer && altitude > 0.5f ? steer * (playerKind == VehicleKind.Helicopter ? 12 : playerKind == VehicleKind.Fighter ? 42 : 35) : 0;
        bank += (bankTarget - bank) * Math.Min(1, dt * 3);
        rotor += dt * (playerKind == VehicleKind.Helicopter ? 1400 : playerKind is VehicleKind.Airplane or VehicleKind.Fighter ? 400 + Math.Abs(freeKmh) * 6 : 0);
        rotor %= 360000;

        // 高いところ（山の上・空）ほど遠くまで見えるようにする
        scene.FogScale = 1 + Math.Max(0, freeY) / 110f;
        f.ViewRadius = Math.Min(22000, 1200 * scene.FogScale);
        f.Altitude = altitude;

        if (water != freeOnWater)
        {
            freeOnWater = water;
            AddFxW(FxKind.Splash, f.PX, FreeWorld.WaterY, f.PZ, 1.6f);
            if (water) game.AddLog(playerKind == VehicleKind.Amphibious ? "水に入りました！ 水上を航行します。" : "水に入りました（水の中はゆっくりしか進めません）。");
        }
        wakeTimer -= dt;
        if (water && Math.Abs(ms) > 2 && wakeTimer <= 0)
        {
            wakeTimer = 0.07f;
            AddFxW(FxKind.Wake, f.PX - Math.Sin(hr) * 4.5, FreeWorld.WaterY + 0.05f, f.PZ - Math.Cos(hr) * 4.5, 0.25f + Math.Abs(ms) * 0.012f);
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
        blastFlash = Math.Max(0, blastFlash - dt * 3.8f);

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

    /// <summary>建物を壊す（戦車の体当たり・砲撃・機関銃・爆撃）。</summary>
    private void DestroyBlock(Block b, bool quiet = false)
    {
        if (b.Destroyed) return;
        b.Destroyed = true;
        int flames = quiet ? 1 : 3;
        for (int i = 0; i < flames; i++) AddFxW(FxKind.Fire, b.X, b.H * (0.2f + i * 0.3f), b.Z, Math.Min(3.5f, 1.2f + b.H / 40), !quiet);
        AddFxW(FxKind.Dust, b.X, 1, b.Z, 3, !quiet);
        shake = Math.Max(shake, 0.6f);
        destroyed++;
    }

    private void HandleFreeKeys()
    {
        HandleTransmissionKeys(allowQE: true);
        if (Raylib.IsKeyPressed(KeyboardKey.V)) SwitchVehicle();
        if (Raylib.IsKeyPressed(KeyboardKey.C)) cockpit = !cockpit;
        if (Raylib.IsKeyPressed(KeyboardKey.B)) stampBook = true;
        if (Raylib.IsKeyPressed(KeyboardKey.M)) bigMap = !bigMap;
        if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket) || Raylib.IsKeyPressed(KeyboardKey.Comma)) CycleMiniMapZoom(-1);
        if (Raylib.IsKeyPressed(KeyboardKey.RightBracket) || Raylib.IsKeyPressed(KeyboardKey.Period)) CycleMiniMapZoom(1);
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

    // ---------------- 砲撃・機関銃・爆撃（フリーモード） ----------------

    private void FireFree(float ms)
    {
        var f = free!;
        double br = BarrelHeading * Math.PI / 180, hr = heading * Math.PI / 180;
        double dx = Math.Sin(br), dz = Math.Cos(br);
        const float speed = 230;
        shells.Add(new Shell
        {
            Kind = ProjectileKind.Cannon,
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

    private float HeliGunPitchDeg => altitude > 0.5f
        ? pitch - Math.Clamp(altitude * 0.25f + 5f, 4f, 34f)
        : 0.5f;

    /// <summary>ヘリコプターの左右機関銃ポッドから交互に高速連射する。</summary>
    private void FireHeliGun(float ms)
    {
        var f = free!;
        gunSide = -gunSide;
        double hr = heading * Math.PI / 180;
        double aimHeading = (heading - turretYaw + (rng.NextDouble() - 0.5) * 1.4) * Math.PI / 180;
        double aimPitch = (HeliGunPitchDeg + (rng.NextDouble() - 0.5) * 1.0) * Math.PI / 180;

        // 機体の左右（±1.05m）、前方（+2.3m）の銃口位置
        double fwdX = Math.Sin(hr), fwdZ = Math.Cos(hr);
        double rightX = fwdZ, rightZ = -fwdX;
        double mx = f.PX + fwdX * 2.3 + rightX * (gunSide * 1.05);
        double mz = f.PZ + fwdZ * 2.3 + rightZ * (gunSide * 1.05);
        float my = freeY + 1.18f;

        const float speed = 390;
        double cosP = Math.Cos(aimPitch), sinP = Math.Sin(aimPitch);
        double dx = Math.Sin(aimHeading) * cosP, dz = Math.Cos(aimHeading) * cosP;
        shells.Add(new Shell
        {
            Kind = ProjectileKind.Bullet,
            Free = true,
            WX = mx, WZ = mz, Y = my,
            Vx = (float)(dx * speed + fwdX * ms),
            Vz = (float)(dz * speed + fwdZ * ms),
            Vy = (float)(sinP * speed),
            Prev = f.R(mx, my, mz),
        });
        AddFxW(FxKind.Muzzle, mx, my, mz, 0.45f, playSound: false);
        reload = 0.07f;
        shake = Math.Max(shake, 0.08f);
        Audio.Play(Audio.MachineGun, 0.65f, 0.95f + (float)rng.NextDouble() * 0.12f);
    }

    private Vector3 PredictHeliGunAim()
    {
        var f = free!;
        double aimHeading = (heading - turretYaw) * Math.PI / 180;
        double aimPitch = HeliGunPitchDeg * Math.PI / 180;
        double cosP = Math.Cos(aimPitch), sinP = Math.Sin(aimPitch);
        double dx = Math.Sin(aimHeading) * cosP, dz = Math.Cos(aimHeading) * cosP;
        double tx = f.PX, tz = f.PZ;
        float ty = freeY + 1.18f;
        float vy = (float)(sinP * 390);
        const float dtStep = 0.02f;
        for (int i = 0; i < 30; i++)
        {
            tx += dx * 390 * dtStep;
            tz += dz * 390 * dtStep;
            vy -= 3.5f * dtStep;
            ty += vy * dtStep;
            float g = Math.Max(f.GroundY(tx, tz), f.RoofY(tx, tz));
            if (ty <= g) { ty = g + 0.2f; break; }
        }
        return f.R(tx, ty, tz);
    }

    /// <summary>戦闘機の翼下から大型航空爆弾を前方へ投下する。</summary>
    private void DropFighterBomb(float ms)
    {
        var f = free!;
        gunSide = -gunSide;
        double hr = heading * Math.PI / 180;
        double fwdX = Math.Sin(hr), fwdZ = Math.Cos(hr);
        double rightX = fwdZ, rightZ = -fwdX;
        double bx = f.PX + fwdX * 2.5 + rightX * (gunSide * 2.1);
        double bz = f.PZ + fwdZ * 2.5 + rightZ * (gunSide * 2.1);
        float by = freeY + 0.9f;

        // 画面前方の視界内で都市壊滅の大爆発・キノコ雲が一望できるよう、前方に大きく投射する
        float fwdSpd = ms + 115f;
        float pitchRad = pitch * MathF.PI / 180;
        float vy0 = altitude < 6f ? 10f : Math.Min(-2f, ms * MathF.Sin(pitchRad) - 5f);
        shells.Add(new Shell
        {
            Kind = ProjectileKind.Bomb,
            Free = true,
            WX = bx, WZ = bz, Y = by,
            Vx = (float)(fwdX * fwdSpd),
            Vz = (float)(fwdZ * fwdSpd),
            Vy = vy0,
            Prev = f.R(bx, by, bz),
        });
        reload = 1.15f;
        Audio.Play(Audio.BombDrop, 0.85f);
    }

    /// <summary>戦闘機の現在位置・速度から爆弾の着弾地点を予測する。</summary>
    private bool PredictBombImpact(out double ix, out float iy, out double iz)
    {
        var f = free!;
        double hr = heading * Math.PI / 180;
        float ms = freeKmh / 3.6f;
        float fwdSpd = ms + 115f;
        float pitchRad = pitch * MathF.PI / 180;
        double vx = Math.Sin(hr) * fwdSpd, vz = Math.Cos(hr) * fwdSpd;
        float vy = altitude < 6f ? 10f : Math.Min(-2f, ms * MathF.Sin(pitchRad) - 5f);

        ix = f.PX + Math.Sin(hr) * 2.5;
        iz = f.PZ + Math.Cos(hr) * 2.5;
        iy = freeY + 0.9f;
        const float step = 0.05f;
        for (int i = 0; i < 180; i++)
        {
            ix += vx * step;
            iz += vz * step;
            vy -= 38f * step;
            iy += vy * step;
            if (i < 3 && altitude < 6f) continue;
            float g = Math.Max(f.GroundY(ix, iz), f.RoofY(ix, iz));
            if (iy <= g)
            {
                iy = g;
                return true;
            }
        }
        iy = f.GroundY(ix, iz);
        return true;
    }

    /// <summary>爆弾の着弾：街をひとつ丸ごと吹き飛ばす超大型爆発（キノコ雲・衝撃波）。</summary>
    private void BombExplode(double x, float y, double z)
    {
        var f = free!;
        double camDist = Math.Sqrt((x - f.PX) * (x - f.PX) + (z - f.PZ) * (z - f.PZ));
        bool onWater = f.IsInWater(x, z) && !f.OnRoad(x, z, 10);

        // 中心部に超巨大爆発エフェクト（巨大火球・衝撃波リング・キノコ雲）を発生させる
        AddFxW(FxKind.MegaBlast, x, onWater ? FreeWorld.WaterY : y + 1.0f, z, 1.0f, playSound: false);
        Audio.PlayAt(Audio.Explosion, camDist * 0.25, 1.0f);
        Audio.Play(Audio.Explosion, Math.Clamp(1.0f - (float)(camDist / 4500.0), 0.35f, 1.0f), 0.58f);

        if (onWater)
        {
            AddFxW(FxKind.Splash, x, FreeWorld.WaterY, z, 14.0f, playSound: false);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI * 0.25;
                AddFxW(FxKind.Splash, x + Math.Cos(a) * 90, FreeWorld.WaterY, z + Math.Sin(a) * 90, 7.5f, playSound: false);
            }
        }

        // 半径1,350m（直径2.7km＝大都市丸ごと1つ分）以内の全ビルを一括破壊！
        const double blastR = 1350.0;
        int blocksDestroyedHere = 0;
        string? mainCityDestroyed = null;
        int maxCityHits = 0;
        var newlyDestroyed = new List<Block>();

        foreach (var place in f.Places)
        {
            double lim = place.Radius + blastR + 80;
            if (Math.Abs(x - place.X) > lim || Math.Abs(z - place.Z) > lim) continue;
            if (place.Blocks == null) _ = f.HitBlock(place.X, place.Z, y, 1); // 街区の建物を生成しておく
            if (place.Blocks == null) continue;

            int cityHits = 0;
            foreach (var b in place.Blocks)
            {
                if (b.Destroyed) continue;
                double dx = b.X - x, dz = b.Z - z;
                if (dx * dx + dz * dz <= blastR * blastR)
                {
                    b.Destroyed = true;
                    destroyed++;
                    blocksDestroyedHere++;
                    cityHits++;
                    newlyDestroyed.Add(b);
                }
            }
            if (cityHits > maxCityHits)
            {
                maxCityHits = cityHits;
                mainCityDestroyed = place.Name;
            }
        }

        // 破壊された街区の中から最大28箇所に二次火災・黒煙柱を立ち上らせる（負荷を抑えつつ大火災を表現）
        if (newlyDestroyed.Count > 0)
        {
            int step = Math.Max(1, newlyDestroyed.Count / 28);
            for (int i = 0; i < newlyDestroyed.Count; i += step)
            {
                var b = newlyDestroyed[i];
                AddFxW(FxKind.Fire, b.X, 2.0f, b.Z, Math.Min(6.5f, 2.8f + b.H / 25f), playSound: false);
            }
        }
        else if (!onWater)
        {
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6.0;
                double r = 60 + (i % 3) * 85;
                AddFxW(FxKind.Fire, x + Math.Cos(a) * r, y + 1.5f, z + Math.Sin(a) * r, 4.5f, playSound: false);
            }
        }

        // 半径1,500m以内の車両をすべて爆風で吹き飛ばす
        foreach (var c in f.Cars)
        {
            if (c.Flying) continue;
            double dx = c.X - x, dz = c.Z - z;
            double d2 = dx * dx + dz * dz;
            if (d2 <= 1500 * 1500)
            {
                double dist = Math.Max(1, Math.Sqrt(d2));
                f.Knock(c, dx / dist, dz / dist, 2.6f);
                AddFxW(FxKind.Fire, c.X, 1.2f, c.Z, 2.0f, playSound: false);
                destroyed++;
            }
        }

        if (blocksDestroyedHere >= 30 && mainCityDestroyed != null)
            game.AddLog($"超大型爆弾が炸裂！ {mainCityDestroyed}の街（建物 {blocksDestroyedHere}棟）を壊滅させました！");
        else if (blocksDestroyedHere > 0)
            game.AddLog($"超大型爆弾が炸裂！ 建物 {blocksDestroyedHere}棟を一気に吹き飛ばしました！");

        shake = Math.Max(shake, Math.Clamp(1.35f - (float)(camDist / 3500.0), 0.45f, 1.35f));
        blastFlash = Math.Max(blastFlash, Math.Clamp(0.45f - (float)(camDist / 4000.0), 0.12f, 0.45f));
    }

    /// <summary>フリーモードの砲弾・機関銃弾・爆弾を進める。終わったら true。</summary>
    private bool UpdateFreeShell(Shell sh, float dt)
    {
        var f = free!;
        sh.Prev = f.R(sh.WX, sh.Y, sh.WZ);
        float grav = sh.Kind == ProjectileKind.Bomb ? 38f : sh.Kind == ProjectileKind.Bullet ? 3.5f : 9.8f;
        float maxLife = sh.Kind == ProjectileKind.Bomb ? 12f : sh.Kind == ProjectileKind.Bullet ? 1.8f : 3.5f;
        int steps = sh.Kind == ProjectileKind.Bullet ? 3 : 2;

        for (int step = 0; step < steps; step++)
        {
            float h = dt / steps;
            sh.WX += sh.Vx * h;
            sh.WZ += sh.Vz * h;
            sh.Vy -= grav * h;
            sh.Y += sh.Vy * h;
            sh.Life += h;

            if (sh.Kind == ProjectileKind.Bomb)
            {
                var bld = f.HitBlock(sh.WX, sh.WZ, sh.Y, 1.8f);
                float g = f.GroundY(sh.WX, sh.WZ);
                if (bld != null || sh.Y <= g)
                {
                    BombExplode(sh.WX, bld != null ? Math.Min(sh.Y, bld.H) : g, sh.WZ);
                    return true;
                }
                if (sh.Life > maxLife) return true;
                continue;
            }

            var hit = f.HitTest(sh.WX, sh.WZ, sh.Y, sh.Kind == ProjectileKind.Bullet ? 1.8f : 1.5f);
            if (hit != null)
            {
                AddFxW(FxKind.Fire, hit.X, 1.2f, hit.Z, hit.Kind == VehicleKind.Truck ? 1.6f : 1.2f);
                float v = MathF.Sqrt(sh.Vx * sh.Vx + sh.Vz * sh.Vz);
                f.Knock(hit, sh.Vx / v, sh.Vz / v, sh.Kind == ProjectileKind.Bullet ? 0.85f : 1);
                destroyed++;
                return true;
            }
            var hitBld = f.HitBlock(sh.WX, sh.WZ, sh.Y, 0.3f);
            if (hitBld != null)
            {
                DestroyBlock(hitBld);
                return true;
            }
            float ground = f.GroundY(sh.WX, sh.WZ);
            if (sh.Y <= ground)
            {
                float sz = sh.Kind == ProjectileKind.Bullet ? (ground < 0 ? 0.7f : 0.45f) : (ground < 0 ? 1.6f : 0.8f);
                AddFxW(ground < 0 ? FxKind.Splash : FxKind.Fire, sh.WX, ground, sh.WZ, sz);
                return true;
            }
            if (sh.Life > maxLife) return true;
        }
        return false;
    }

    private void AddFxW(FxKind kind, double x, float y, double z, float size, bool playSound = true)
    {
        effects.Add(new Fx { Kind = kind, Free = true, WX = x, WZ = z, Y = y, Size = size, Seed = rng.Next(1000) });
        if (playSound && free != null) FxSound(kind, Math.Sqrt((x - free.PX) * (x - free.PX) + (z - free.PZ) * (z - free.PZ)), size);
    }

    // ---------------- 描画 ----------------

    private void DrawFreeWorld3D()
    {
        var f = free!;
        f.Draw(camera.Position, time);
        if (IsFlyer(playerKind))
        {
            DrawPlayerAircraft(f);
            f.DrawWater();
            DrawCombat3D();
            return;
        }
        if (!cockpit)
            scene.DrawVehicleAt(f.R(f.PX, freeY, f.PZ), -heading + steerYaw + driftYaw, PlayerColor, playerKind, FreeSpin,
                braking: game.Brake || (isDrifting && !Raylib.IsKeyDown(KeyboardKey.Up) && !Raylib.IsKeyDown(KeyboardKey.W)),
                wheelSteer: playerKind == VehicleKind.Sports ? steerYaw * 2.5f - driftYaw * 0.55f : 0,
                turretYaw: turretYaw, recoil: recoil, shadow: !freeOnWater && !carAir, pitch: terrainPitch, bank: terrainBank);
        f.DrawWater();
        DrawCombat3D();
    }

    private void DrawPlayerAircraft(FreeWorld f)
    {
        // 地面に落ちる影（高いほど薄く）
        if (altitude < 400)
        {
            var g = f.R(f.PX, groundLevel + 0.08f, f.PZ);
            float hw = playerKind is VehicleKind.Airplane or VehicleKind.Fighter ? 5f : 1.6f;
            float hl = playerKind == VehicleKind.Fighter ? 5.2f : playerKind == VehicleKind.Airplane ? 4f : 4.5f;
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

        if (blastFlash > 0.01f)
        {
            byte fa = (byte)Math.Clamp(blastFlash * 195f, 0, 210);
            Raylib.DrawRectangle(0, 0, sw, sh, new Color((byte)255, (byte)238, (byte)185, fa));
        }

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
        // 近くの空港を3Dの上に表示
        foreach (var ap in f.Airports)
        {
            var p = f.R(ap.X, 42, ap.Z);
            float d = p.Length();
            if (d > 2200 || Vector3.Dot(p - camera.Position, look) < 5) continue;
            var sp = Raylib.GetWorldToScreen(p, camera);
            string label = $"[空] {ap.Name}";
            var m = Measure(label, 14);
            Panel(sp.X - m.X / 2 - 7 * u, sp.Y - 3 * u, m.X + 14 * u, m.Y + 6 * u, new Color(15, 65, 95, 185));
            Text(label, sp.X - m.X / 2, sp.Y, 14, Rgb(140, 235, 255));
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

        string help = playerKind == VehicleKind.Helicopter
            ? "↑↓ 速度　←→ 旋回　Space 上昇　Shift 下降　マウス・Z/X 照準　左クリック・Ctrl長押し 機関銃　V 乗り物　M 地図　B スタンプ帳　C 視点"
            : playerKind == VehicleKind.Fighter
            ? "↑↓ 加速/減速　←→ 旋回　Space 上昇（150km/h〜）　Shift 下降　左クリック・Ctrl 爆弾投下　V 乗り物　M 地図　B スタンプ帳　C 視点"
            : IsFlyer(playerKind)
            ? "↑↓ 速度　←→ 旋回　Space 上昇　Shift 下降（140km/h 以上で離陸）　V 乗り物　M 地図・ワープ　B スタンプ帳　C 視点"
            : playerKind == VehicleKind.Tank
            ? "↑↓ 前進/後退　←→ ハンドル　H AT/MT切替(Q/E・Shift・ホイール ギア)　マウス・Z/X 砲塔　左クリック・Ctrl 発射　V 乗り物　M 地図"
            : "↑↓ アクセル/ブレーキ　←→ ハンドル　Space ドリフト　H AT/MT切替（E/Shift シフトUP・Q/Ctrl シフトDOWN）　V 乗り物　M 地図　B スタンプ帳　C 視点";
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
        if (UpdateBigMapCamera(out bool clickedLeft, out bool clickedRight)) return;
        if (!clickedLeft && !clickedRight) return;
        var (center, origin, scale) = BigMapView();
        var mouse = Raylib.GetMousePosition();
        var mp = new PointF(center.X + (mouse.X - origin.X) / scale, center.Y + (mouse.Y - origin.Y) / scale);
        var (wx, wz) = FreeWorld.FromMap(mp);
        var f = free!;

        // 空港アイコンの近くをクリックした場合は滑走路の端へ正確に合わせる
        float? snapHeading = null;
        string? snapName = null;
        foreach (var ap in f.Airports)
        {
            float dAp = Vector2.Distance(MapPt(FreeWorld.ToMap(ap.X, ap.Z), center, origin, scale), mouse);
            if (dAp < 14 * Ui)
            {
                wx = ap.X - ap.Dx * 190;
                wz = ap.Z - ap.Dz * 190;
                snapHeading = ap.Heading;
                snapName = ap.Name;
                break;
            }
        }

        var (placeName, d) = f.NearestPlace(wx, wz);
        string where = snapName ?? (d < 4500 ? $"{placeName}付近" : "その地点");
        if (clickedLeft)
        {
            BeginFade(() =>
            {
                f.PX = wx;
                f.PZ = wz;
                if (snapHeading is float shdg) heading = shdg;
                freeKmh = 0;
                altitude = pitch = bank = 0;
                freeY = groundLevel = f.SurfaceY(wx, wz);
                carAir = prevAirborne = false;
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
