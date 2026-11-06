using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// ゲームならではの要素：戦車（砲撃）・水陸両用車（海上走行・橋からダイブ）。
/// </summary>
public sealed partial class App
{
    private enum PlayerState { Road, Falling, Water }

    private enum FxKind { Fire, Splash, Muzzle, Wake, Dust }

    private sealed class Shell
    {
        public bool Free;
        public double WX, WZ;
        public float Vx, Vz;
        public double S;
        public float U, Y, Vs, Vu, Vy, Life;
        public Vector3 Prev;
    }

    private sealed class Fx
    {
        public bool Free;
        public double WX, WZ;
        public double S;
        public float U, Y, T, Size;
        public FxKind Kind;
        public int Seed;
    }

    private static readonly VehicleKind[] PlayerKinds =
        { VehicleKind.Sports, VehicleKind.Tank, VehicleKind.Amphibious, VehicleKind.Horse, VehicleKind.Hikyaku,
          VehicleKind.Helicopter, VehicleKind.Airplane };

    private VehicleKind playerKind = VehicleKind.Sports;
    private PlayerState pstate = PlayerState.Road;
    private float playerY, playerVy;
    private float turretYaw, turretTarget, recoil, reload;
    private int destroyed;
    private float wakeTimer;
    private readonly List<Shell> shells = new();
    private readonly List<Fx> effects = new();

    // ---------------- 車両の性能 ----------------

    private static string VehicleName(VehicleKind k) => k switch
    {
        VehicleKind.Tank => "戦車",
        VehicleKind.Amphibious => "水陸両用輸送車",
        VehicleKind.Horse => "馬",
        VehicleKind.Hikyaku => "飛脚",
        VehicleKind.Helicopter => "ヘリコプター",
        VehicleKind.Airplane => "飛行機",
        _ => "スポーツカー",
    };

    private Color PlayerColor => playerKind switch
    {
        VehicleKind.Tank => Rgb(92, 102, 64),
        VehicleKind.Amphibious => Rgb(112, 114, 92),
        VehicleKind.Horse => Rgb(118, 72, 38),
        VehicleKind.Helicopter => Rgb(230, 175, 35),
        VehicleKind.Airplane => Rgb(200, 40, 45),
        _ => Rgb(200, 25, 35),
    };

    private float PlayerLength => CarModels.Length(playerKind);
    private float PlayerHalfWidth => playerKind switch
    {
        VehicleKind.Tank => 1.95f,
        VehicleKind.Amphibious => 1.9f,
        VehicleKind.Horse => 0.5f,
        VehicleKind.Hikyaku => 0.35f,
        VehicleKind.Helicopter or VehicleKind.Airplane => 1.5f,
        _ => 0.9f,
    };

    private double VehicleSpeedCap => pstate == PlayerState.Water ? 45 : playerKind switch
    {
        VehicleKind.Tank => 70,
        VehicleKind.Amphibious => 72,
        VehicleKind.Horse => 70,
        VehicleKind.Hikyaku => 35,
        _ => Game.MaxSpeed,
    };

    private void ApplyVehicle()
    {
        game.Amphibious = playerKind == VehicleKind.Amphibious;
        game.FuelFactor = playerKind switch
        {
            VehicleKind.Tank => 1.6,
            VehicleKind.Amphibious => 1.3,
            VehicleKind.Horse or VehicleKind.Hikyaku => 0,   // 燃料いらず
            _ => 1,
        };
        game.SpeedCap = VehicleSpeedCap;
    }

    private void SwitchVehicle()
    {
        if (!freeRoam && (pstate != PlayerState.Road || game.OnSea))
        {
            game.AddLog("海の上では乗り換えできません。");
            return;
        }
        int i = Array.IndexOf(PlayerKinds, playerKind);
        var next = PlayerKinds[(i + 1) % PlayerKinds.Length];
        // 空を飛ぶ乗り物はフリーモードだけ
        while (!freeRoam && IsFlyer(next)) next = PlayerKinds[(Array.IndexOf(PlayerKinds, next) + 1) % PlayerKinds.Length];
        BeginFade(() =>
        {
            playerKind = next;
            turretYaw = turretTarget = 0;
            altitude = 0;
            pitch = bank = 0;
            Audio.Play(next == VehicleKind.Horse ? Audio.Neigh : Audio.Whoosh, 0.8f);
            SaveNow();
            ApplyVehicle();
            string extra = next switch
            {
                VehicleKind.Tank => "（マウス・Z/X で砲塔、左クリック・Ctrl で発射）",
                VehicleKind.Amphibious => "（フェリー航路を自力で渡れます。橋の左端から海へダイブも！）",
                VehicleKind.Horse => "（武士が駆ける。燃料いりません）",
                VehicleKind.Hikyaku => "（江戸の飛脚が走って届ける。燃料いりません）",
                VehicleKind.Helicopter => "（Space で上昇・Shift で下降。空から日本を見渡そう）",
                VehicleKind.Airplane => "（↑で加速し、140km/h 以上で Space を押すと離陸。遅すぎると失速します）",
                _ => "",
            };
            game.AddLog($"{VehicleName(next)}に乗り換えました{extra}");
        });
    }

    // ---------------- 操縦・水上 ----------------

    private void SteerPlayer(float dt, float steer, float ms)
    {
        if (game.OnFerry || pstate == PlayerState.Falling) return;
        if (pstate == PlayerState.Water)
        {
            playerU = Math.Clamp(playerU + steer * 4f * Math.Min(1f, ms / 4f) * dt, -60, 60);
            return;
        }
        float rate = (playerKind == VehicleKind.Sports ? 5.5f : 3.8f) * Math.Min(1f, ms / 8f);
        playerU += steer * rate * dt;
        float lo = -9.6f + PlayerHalfWidth, hi = -0.5f - PlayerHalfWidth;
        var env = scene.EnvAt(playerS);
        if (playerKind == VehicleKind.Amphibious && env is Env.Bridge or Env.MegaBridge) lo = -12f;
        if ((playerU < lo || playerU > hi) && ms > 12 && playerKind == VehicleKind.Sports)
        {
            game.Slow(1 - 0.8 * dt);  // ガードレールにこする
            shake = Math.Max(shake, 0.15f);
        }
        playerU = Math.Clamp(playerU, lo, hi);
    }

    private void UpdateVehicleState(float dt, float ms)
    {
        var env = scene.EnvAt(playerS);
        float sea = Scene.SeaLevel(env);
        switch (pstate)
        {
            case PlayerState.Road:
                playerY = 0;
                if (playerKind != VehicleKind.Amphibious) break;
                if (env == Env.Ferry) EnterWater(sea, "海へ入りました！ 自力で航行します。");
                else if (env is Env.Bridge or Env.MegaBridge && playerU < -10.3f)
                {
                    pstate = PlayerState.Falling;
                    playerVy = 3;
                    game.AddLog(env == Env.MegaBridge ? "高さ70mの大橋から海へダイブ！" : "橋から海へダイブ！");
                }
                break;

            case PlayerState.Falling:
                playerVy -= 16 * dt;
                playerY += playerVy * dt;
                playerU -= 3f * dt;
                if (!Scene.IsSea(env))
                {
                    pstate = PlayerState.Road;
                    playerY = 0;
                    playerU = Traffic.DrivingLane;
                }
                else if (playerY <= sea - 0.9f) EnterWater(sea, null);
                break;

            case PlayerState.Water:
                if (!Scene.IsSea(env))
                {
                    pstate = PlayerState.Road;
                    playerY = 0;
                    playerU = Traffic.DrivingLane;
                    AddFx(FxKind.Splash, playerS, playerU, 0, 1.5f);
                    game.AddLog("上陸しました！ 道路に戻ります。");
                    break;
                }
                playerY = sea - 0.9f + MathF.Sin(time * 1.6f) * 0.15f;
                wakeTimer -= dt;
                if (ms > 2 && wakeTimer <= 0)
                {
                    wakeTimer = 0.07f;
                    AddFx(FxKind.Wake, playerS - 4.5, playerU + (float)(rng.NextDouble() - 0.5) * 3, sea + 0.05f, 0.25f + ms * 0.012f);
                }
                break;
        }
        game.SpeedCap = VehicleSpeedCap;
    }

    private void EnterWater(float sea, string? message)
    {
        pstate = PlayerState.Water;
        playerY = sea - 0.9f;
        AddFx(FxKind.Splash, playerS, playerU, sea, playerVy < -20 ? 4f : 2f);
        shake = Math.Max(shake, playerVy < -20 ? 0.8f : 0.3f);
        playerVy = 0;
        if (message != null) game.AddLog(message);
    }

    // ---------------- 砲撃 ----------------

    private void UpdateCombat(float dt, float ms)
    {
        // 砲塔の旋回：マウスを動かすとマウスの方向、Z/X キーでも回せる
        if (playerKind == VehicleKind.Tank && !bigMap && !stampBook)
        {
            var delta = Raylib.GetMouseDelta();
            if (Math.Abs(delta.X) > 0.5f && shotFile == null)
            {
                float sw = Raylib.GetScreenWidth();
                turretTarget = -(Raylib.GetMouseX() - sw / 2) / (sw / 2) * 120;
            }
            if (Raylib.IsKeyDown(KeyboardKey.Z)) turretTarget += 90 * dt;
            if (Raylib.IsKeyDown(KeyboardKey.X)) turretTarget -= 90 * dt;
            turretTarget = Math.Clamp(turretTarget, -170, 170);
            turretYaw += Math.Clamp(turretTarget - turretYaw, -75 * dt, 75 * dt);

            bool fire = Raylib.IsMouseButtonPressed(MouseButton.Left) ||
                        Raylib.IsKeyPressed(KeyboardKey.LeftControl) || Raylib.IsKeyPressed(KeyboardKey.RightControl);
            if (shotFile != null && demoFire) fire = true;
            if (fire) Fire(ms);
        }
        reload = Math.Max(0, reload - dt);
        recoil = Math.Max(0, recoil - dt * 2.5f);

        UpdateShells(dt);
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            effects[i].T += dt;
            if (effects[i].T > FxLife(effects[i].Kind)) effects.RemoveAt(i);
        }
    }

    private void Fire(float ms)
    {
        if (reload > 0) return;
        if (freeRoam)
        {
            FireFree(ms);
            return;
        }
        if (detour != null)
        {
            game.AddLog("名所の近くでは発砲できません。");
            reload = 1;
            return;
        }
        if (pstate != PlayerState.Road || game.OnSea) return;

        float a = (turretYaw + steerYaw) * MathF.PI / 180;
        float ds = MathF.Cos(a), du = -MathF.Sin(a);
        const float speed = 230;
        double s0 = playerS + 0.2 + ds * 7.6;
        float u0 = playerU + du * 7.6f;
        shells.Add(new Shell
        {
            S = s0, U = u0, Y = 2.05f,
            Vs = ds * speed + ms, Vu = du * speed, Vy = 3f,
            Prev = scene.P(s0, u0, 2.05f),
        });
        AddFx(FxKind.Muzzle, s0, u0, 2.05f, 1);
        Audio.Play(Audio.Cannon, 0.9f);
        recoil = 1;
        reload = 1.4f;
        shake = Math.Max(shake, 0.35f);
    }

    private void UpdateShells(float dt)
    {
        for (int i = shells.Count - 1; i >= 0; i--)
        {
            var sh = shells[i];
            if (sh.Free)
            {
                if (UpdateFreeShell(sh, dt)) shells.RemoveAt(i);
                continue;
            }
            sh.Prev = scene.P(sh.S, sh.U, sh.Y);
            bool done = false;
            // 速いので2分割して当たり判定する
            for (int step = 0; step < 2 && !done; step++)
            {
                float h = dt / 2;
                sh.S += sh.Vs * h;
                sh.U += sh.Vu * h;
                sh.Vy -= 9.8f * h;
                sh.Y += sh.Vy * h;
                sh.Life += h;

                var hit = traffic.HitTest(sh.S, sh.U, sh.Y);
                if (hit != null)
                {
                    AddFx(FxKind.Fire, hit.S, hit.U, 1.2f, hit.Truck ? 1.6f : 1.2f);
                    traffic.Knock(hit, sh.Vu >= 0 ? 1 : -1, 1);
                    destroyed++;
                    done = true;
                    break;
                }

                var env = scene.EnvAt(sh.S);
                bool offDeck = Scene.IsSea(env) && Math.Abs(sh.U) > 10.6f;
                float floor = offDeck ? Scene.SeaLevel(env) : 0;
                if (sh.Y <= floor)
                {
                    AddFx(offDeck ? FxKind.Splash : FxKind.Fire, sh.S, sh.U, floor, offDeck ? 1.6f : 0.8f);
                    done = true;
                }
                else if (scene.IsTunnel(sh.S) && Math.Abs(sh.U) > 10.4f)
                {
                    AddFx(FxKind.Fire, sh.S, Math.Sign(sh.U) * 10.2f, sh.Y, 0.8f);
                    done = true;
                }
                else if (sh.Life > 3.5f) done = true;
            }
            if (done) shells.RemoveAt(i);
        }
    }

    // ---------------- エフェクト ----------------

    private void AddFx(FxKind kind, double s, float u, float y, float size)
    {
        effects.Add(new Fx { Kind = kind, S = s, U = u, Y = y, Size = size, Seed = rng.Next(1000) });
        FxSound(kind, Math.Abs(s - playerS), size);
    }

    private static float FxLife(FxKind k) => k switch
    {
        FxKind.Fire => 2.6f,
        FxKind.Splash => 1.8f,
        FxKind.Muzzle => 1.0f,
        FxKind.Wake => 0.9f,
        _ => 1.2f,
    };

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, (byte)Math.Clamp(a * 255, 0, 255));

    private void DrawCombat3D()
    {
        // 砲弾（曳光弾）
        foreach (var sh in shells)
        {
            var p = sh.Free ? free!.R(sh.WX, sh.Y, sh.WZ) : scene.P(sh.S, sh.U, sh.Y);
            Raylib.DrawSphereEx(p, 0.25f, 6, 6, Rgb(255, 230, 150));
            Raylib.DrawLine3D(sh.Prev, p, Rgb(255, 200, 90));
        }

        foreach (var e in effects)
        {
            if (e.Free && free == null) continue;
            var b = e.Free ? free!.R(e.WX, e.Y, e.WZ) : scene.P(e.S, e.U, e.Y);
            float t = e.T, z = e.Size;
            switch (e.Kind)
            {
                case FxKind.Fire:
                {
                    if (t < 0.12f) Raylib.DrawSphereEx(b, z * 2.4f, 10, 10, WithAlpha(Rgb(255, 250, 210), 0.95f));
                    if (t < 0.9f)
                    {
                        var col = Lerp(Rgb(255, 210, 80), Rgb(200, 60, 20), t / 0.9f);
                        Raylib.DrawSphereEx(b + new Vector3(0, t * 1.5f, 0), z * (1.3f + 3 * t), 10, 10, WithAlpha(col, 0.85f * (1 - t / 0.9f)));
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        float rise = t * 4 + i * 0.9f;
                        float off = MathF.Sin(e.Seed + i * 2.1f) * z;
                        Raylib.DrawSphereEx(b + new Vector3(off, rise, MathF.Cos(e.Seed + i) * z), z * (0.9f + t * 1.3f), 8, 8,
                            WithAlpha(scene.FogC(Rgb(70, 68, 66), 0), 0.55f * (1 - t / 2.6f)));
                    }
                    if (t < 1.3f)
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            float ang = (e.Seed + i * 60) * MathF.PI / 180;
                            var dir = new Vector3(MathF.Cos(ang), 1.2f + (i % 3) * 0.3f, MathF.Sin(ang));
                            var pos = b + dir * (t * 11 * z) + new Vector3(0, -9 * t * t, 0);
                            Raylib.DrawCube(pos, 0.3f * z, 0.3f * z, 0.3f * z, Rgb(40, 35, 30));
                        }
                    }
                    break;
                }
                case FxKind.Splash:
                case FxKind.Wake:
                {
                    float life = FxLife(e.Kind);
                    int n = e.Kind == FxKind.Wake ? 2 : 9;
                    for (int i = 0; i < n; i++)
                    {
                        float ang = (e.Seed + i * 40) * MathF.PI / 180;
                        float spread = z * (1 + t * 3);
                        float up = e.Kind == FxKind.Wake ? 0.1f : z * (9 * t - 6 * t * t);
                        var pos = b + new Vector3(MathF.Cos(ang) * spread * 0.6f, Math.Max(0, up) * (0.6f + (i % 3) * 0.3f), MathF.Sin(ang) * spread * 0.6f);
                        Raylib.DrawSphereEx(pos, z * (0.5f + t * 0.6f), 6, 6, WithAlpha(Rgb(235, 245, 250), 0.8f * (1 - t / life)));
                    }
                    if (e.Kind == FxKind.Splash)
                        Raylib.DrawCircle3D(b + new Vector3(0, 0.15f, 0), z * (1 + t * 6), Vector3.UnitX, 90, WithAlpha(Rgb(240, 250, 255), 1 - t / life));
                    break;
                }
                case FxKind.Muzzle:
                {
                    if (t < 0.08f) Raylib.DrawSphereEx(b, 1.3f, 8, 8, WithAlpha(Rgb(255, 230, 120), 0.95f));
                    if (!cockpit) Raylib.DrawSphereEx(b + new Vector3(0, t * 1.2f, 0), 0.5f + t * 1.6f, 8, 8, WithAlpha(Rgb(190, 190, 185), 0.3f * (1 - t)));
                    break;
                }
                default:
                    Raylib.DrawSphereEx(b + new Vector3(0, t, 0), z * (0.6f + t), 6, 6, WithAlpha(Rgb(170, 160, 140), 0.5f * (1 - t / 1.2f)));
                    break;
            }
        }
    }

    /// <summary>戦車の照準と装填表示。</summary>
    private void DrawCrosshair(float u)
    {
        if (playerKind != VehicleKind.Tank || detour != null) return;
        float a = (turretYaw + steerYaw) * MathF.PI / 180;
        float ds = MathF.Cos(a), du = -MathF.Sin(a);
        const float t = 0.6f;
        Vector3 aim;
        if (freeRoam && free != null)
        {
            double br = BarrelHeading * Math.PI / 180;
            double reach = 7.6 + 230 * t;
            aim = free.R(free.PX + Math.Sin(br) * reach, freeY + 2.05f + 3 * t - 4.9f * t * t, free.PZ + Math.Cos(br) * reach);
        }
        else aim = scene.P(playerS + ds * (7.6 + 230 * t), playerU + du * (7.6f + 230 * t), 2.05f + 3 * t - 4.9f * t * t);
        var toAim = aim - camera.Position;
        var look = Vector3.Normalize(camera.Target - camera.Position);
        if (Vector3.Dot(toAim, look) > 1)
        {
            var sp = Raylib.GetWorldToScreen(aim, camera);
            float r = 18 * u;
            var col = reload > 0 ? new Color(255, 120, 80, 220) : new Color(120, 255, 140, 230);
            Raylib.DrawRingLines(sp, r, r + 1.5f * u, 0, 360, 24, col);
            Raylib.DrawLineEx(sp - new Vector2(r * 1.8f, 0), sp - new Vector2(r * 0.6f, 0), 2 * u, col);
            Raylib.DrawLineEx(sp + new Vector2(r * 0.6f, 0), sp + new Vector2(r * 1.8f, 0), 2 * u, col);
            Raylib.DrawLineEx(sp + new Vector2(0, r * 0.6f), sp + new Vector2(0, r * 1.8f), 2 * u, col);
            if (reload > 0) Raylib.DrawRing(sp, r + 3 * u, r + 6 * u, -90, -90 + 360 * (1 - reload / 1.4f), 24, col);
        }
        string info = $"撃破 {destroyed}　砲塔 {(turretYaw > 3 ? "左" : turretYaw < -3 ? "右" : "正面")} {Math.Abs(turretYaw):0}°　{(reload > 0 ? "装填中…" : "発射可能")}";
        var m = Measure(info, 16);
        float x = (Raylib.GetScreenWidth() - m.X) / 2;
        Panel(x - 12 * u, 100 * u, m.X + 24 * u, 30 * u, new Color(30, 40, 20, 200));
        Text(info, x, 105 * u, 16, reload > 0 ? Rgb(255, 180, 120) : Rgb(170, 255, 170));
    }
}
