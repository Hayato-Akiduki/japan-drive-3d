using System.Numerics;
using PointF = System.Drawing.PointF;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed partial class App
{
    private static readonly Color Orange = Rgb(255, 165, 40);
    private static readonly Color Sub = Rgb(170, 180, 200);
    private static readonly Color SignGreen = Rgb(0, 110, 72);

    private void DrawHud()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Ui;

        if (detour == null) DrawSignTexts();
        if (crashCooldown > 1.8f)
            Raylib.DrawRectangle(0, 0, sw, sh, new Color(255, 0, 0, (int)(90 * (crashCooldown - 1.8f) / 0.7f)));

        DrawInfoPanel(16 * u, 16 * u);
        DrawMiniMap(sw - 316 * u, 16 * u, 300 * u);
        if (detour != null) DrawDetourHud(sw, sh, u);
        else
        {
            DrawExitSign(sw, u);
            if (game.Road != null) DrawNextJunction(sw - 316 * u, 328 * u, 300 * u);
            DrawDetourPrompt(sw, sh, u);
        }
        DrawSpeedometer(sw - 170 * u, sh - 175 * u, u);
        DrawLog(16 * u, sh - 150 * u, u);
        DrawCrosshair(u);

        string help = detour != null
            ? "↑↓ アクセル/ブレーキ　←→ ハンドル　Enter 高速に戻る　B スタンプ帳　C 視点"
            : playerKind == VehicleKind.Tank
                ? "↑↓ 走る/止まる　←→ 車線　マウス・Z/X 砲塔　左クリック・Ctrl 発射　V 車両切替　C 砲手視点　Q/E 次の分岐　P 寄り道　M 地図"
                : playerKind == VehicleKind.Amphibious
                    ? "↑↓ 走る/止まる　←→ 車線（橋の左端から海へダイブ）　V 車両切替　Q/E 次の分岐　P 寄り道　B スタンプ帳　M 地図　C 視点"
                    : "↑↓ アクセル/ブレーキ　←→ 車線　Q/E・1-9 次の分岐　T おまかせ　P 寄り道　V 車両切替　B スタンプ帳　M 地図　C 視点　F 給油";
        var hm = Measure(help, 13);
        Text(help, (sw - hm.X) / 2, sh - 24 * u, 13, new Color(255, 255, 255, 170));

        if (game.AtCity != null && detour == null && fadeDir == 0 && !game.Cleared && !bigMap) DrawJunction(sw, sh, u);
        DrawStampPopup(sw, sh, u);
        if (bigMap) DrawBigMap(sw, sh, u);
        if (stampBook) DrawStampBook(sw, sh, u);
        if (game.Cleared) DrawClear(sw, sh, u);
    }

    // ================= 分岐・寄り道・スタンプ =================

    /// <summary>次の分岐で進む道路の一覧（走行中）。</summary>
    private void DrawNextJunction(float x, float y, float w)
    {
        float u = Ui;
        var city = game.To;
        var planned = game.PlannedRoad();
        bool byNav = game.NavTarget != null && game.AutoDrive;
        float h = (58 + city.Roads.Count * 24) * u;
        Panel(x, y, w, h);
        string head = !game.FreeDrive && !byNav ? $"次の分岐：{city.Name}（停車して選ぶ）"
            : byNav ? $"次の分岐：{city.Name}（ナビ）" : $"次の分岐：{city.Name}";
        Text(head, x + 12 * u, y + 8 * u, 15, Color.White);
        Text("Q/E・数字で変更　T おまかせ切替", x + 12 * u, y + 30 * u, 12, Sub);
        float cy = y + 52 * u;
        for (int i = 0; i < city.Roads.Count; i++)
        {
            var r = city.Roads[i];
            var dest = r.Other(city);
            bool sel = r == planned && (game.FreeDrive || byNav);
            string turn = Game.TurnLabel(Game.TurnAngle(city, game.Road, r));
            string star = game.Mode == GameMode.Tour && dest.IsGoal && !game.Visited.Contains(dest) && dest != game.StartCity ? "★" : "";
            var col = sel ? Orange : r == game.Road ? Rgb(120, 125, 140) : Rgb(225, 230, 240);
            Text($"{(sel ? "▶" : "　")}{i + 1} {turn}　{r.Name}→{star}{dest.Name}", x + 10 * u, cy, 14, col);
            cy += 24 * u;
        }
    }

    /// <summary>名所への寄り道を案内する。</summary>
    private void DrawDetourPrompt(int sw, int sh, float u)
    {
        if (game.OnFerry || game.Cleared) return;
        var city = game.JunctionCity;
        string msg;
        Color col;
        if (pendingDetour != null)
        {
            msg = $"{pendingDetour.City.Name}で降りて「{pendingDetour.Name}」へ寄り道します（P で取り消し）";
            col = Orange;
        }
        else
        {
            var spot = SpotFor(city);
            if (spot == null) return;
            if (game.AtCity == null && game.Remaining > 60) return;
            bool got = stamps.ContainsKey(spot.Key);
            msg = $"P：{city.Name}の名所「{spot.Name}」に寄り道{(got ? "（スタンプ獲得済み）" : "してスタンプをもらう")}";
            col = got ? Rgb(200, 205, 215) : Rgb(255, 220, 120);
        }
        var m = Measure(msg, 15);
        float x = (sw - m.X) / 2 - 14 * u, y = sh - 66 * u;
        Panel(x, y, m.X + 28 * u, 32 * u, new Color(60, 20, 20, 200));
        Text(msg, x + 14 * u, y + 7 * u, 15, col);
    }

    private void DrawDetourHud(int sw, int sh, float u)
    {
        var d = detour!;
        string main = d.Arrived ? d.Spot.Name : $"{d.Spot.Name}まで {Math.Max(0, d.EndS - playerS):0} m";
        string sub = d.Arrived ? "Enter（または ↑）で高速道路に戻る" : $"一般道（{d.Spot.City.Name}）　制限 60km/h";
        var m = Measure(main, 26);
        var m2 = Measure(sub, 15);
        float w = Math.Max(m.X, m2.X) + 40 * u;
        float x = (sw - w) / 2, y = 16 * u;
        Raylib.DrawRectangleRounded(new Rectangle(x, y, w, 76 * u), 0.15f, 6, Rgb(30, 70, 150));
        Raylib.DrawRectangleRoundedLinesEx(new Rectangle(x + 3 * u, y + 3 * u, w - 6 * u, 70 * u), 0.15f, 6, 2 * u, Color.White);
        TextCentered(main, sw / 2f, y + 8 * u, 26, Color.White);
        TextCentered(sub, sw / 2f, y + 46 * u, 15, Color.White);
    }

    /// <summary>スタンプの絵（丸い枠と名所名）。</summary>
    private static void DrawStamp(Vector2 c, float r, Spot spot, Color ink, float rotation, string? date)
    {
        Raylib.DrawRing(c, r * 0.86f, r, 0, 360, 48, ink);
        Raylib.DrawRing(c, r * 0.74f, r * 0.78f, 0, 360, 48, ink);
        float size = Math.Clamp(r * 1.25f / Math.Max(3, spot.Name.Length) / Ui, 8, r * 0.36f / Ui);
        var name = spot.Name.Replace("（", "\n（");
        var lines = name.Split('\n');
        float total = lines.Length * size * 1.15f * Ui;
        float y = c.Y - total / 2 - r * 0.08f;
        foreach (var line in lines)
        {
            var m = Measure(line, size);
            Text(line, c.X - m.X / 2, y, size, ink);
            y += size * 1.15f * Ui;
        }
        TextCentered(spot.City.Name, c.X, c.Y + r * 0.42f, Math.Max(8, r * 0.17f / Ui), ink);
        if (date != null) TextCentered(date, c.X, c.Y - r * 0.62f, Math.Max(7, r * 0.13f / Ui), ink);
    }

    private void DrawStampPopup(int sw, int sh, float u)
    {
        if (popupSpot == null || popupTime > 4.5f) return;
        float t = popupTime;
        float scale = t < 0.25f ? 2.2f - 1.2f * (t / 0.25f) : 1;
        byte alpha = (byte)(t > 3.8f ? 255 * Math.Max(0, (4.5f - t) / 0.7f) : 255);
        // 名所が見えるよう、スタンプは画面の右側に押す
        var center = new Vector2(sw - 200 * u, sh * 0.47f);
        float r = 100 * u * scale;
        Raylib.DrawCircleV(center, r * 1.08f, new Color((byte)255, (byte)250, (byte)240, (byte)(alpha * 0.85f)));
        var ink = new Color((byte)200, (byte)30, (byte)40, alpha);
        stamps.TryGetValue(popupSpot.Key, out double when);
        DrawStamp(center, r, popupSpot, ink, 0, Game.FormatClock(when));
        string msg = popupNew ? $"スタンプGET！（{stamps.Count}/{Spots.All.Count}）" : "スタンプは獲得済みです";
        TextCentered(msg, center.X, center.Y + r * 1.15f, 20, new Color((byte)255, (byte)230, (byte)120, alpha));
    }

    private void DrawStampBook(int sw, int sh, float u)
    {
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(20, 15, 10, 235));
        float pw = Math.Min(sw - 40 * u, 1500 * u), ph = sh - 40 * u;
        float px = (sw - pw) / 2, py = 20 * u;
        Panel(px, py, pw, ph, new Color(245, 238, 220, 255));
        TextCentered($"名所スタンプ帳　{stamps.Count} / {Spots.All.Count}", sw / 2f, py + 16 * u, 28, Rgb(90, 50, 30));
        TextCentered("B・Esc：閉じる（一時停止中）", sw / 2f, py + 56 * u, 13, Rgb(120, 100, 80));

        int cols = Spots.All.Count > 100 ? 14 : Spots.All.Count > 70 ? 12 : 10;
        int rows = (Spots.All.Count + cols - 1) / cols;
        float cellW = (pw - 40 * u) / cols;
        float cellH = (ph - 100 * u) / rows;
        float r = Math.Min(cellW, cellH) * 0.42f;
        for (int i = 0; i < Spots.All.Count; i++)
        {
            var spot = Spots.All[i];
            var c = new Vector2(px + 20 * u + (i % cols + 0.5f) * cellW, py + 86 * u + (i / cols + 0.5f) * cellH);
            if (stamps.TryGetValue(spot.Key, out double when))
                DrawStamp(c, r, spot, Rgb(200, 30, 40), 0, Game.FormatClock(when)[..^6]);
            else
            {
                Raylib.DrawRingLines(c, r * 0.86f, r, 0, 360, 36, Rgb(190, 180, 165));
                var m = Measure(spot.Name, 11);
                if (m.X > r * 1.9f) m = Measure(spot.Name[..Math.Min(spot.Name.Length, 6)], 11);
                TextCentered(spot.Name.Length > 7 ? spot.Name[..7] : spot.Name, c.X, c.Y - 14 * u, 11, Rgb(160, 150, 135));
                TextCentered(spot.City.Name, c.X, c.Y + 4 * u, 11, Rgb(170, 160, 145));
            }
        }
    }

    private void DrawTitle()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Ui;
        Raylib.DrawRectangleGradientV(0, 0, sw, sh, new Color(0, 0, 0, 40), new Color(0, 0, 0, 170));
        TextCentered("日本一周ドライブ 3D", sw / 2f + 3 * u, sh * 0.16f + 3 * u, 64, new Color(0, 0, 0, 150));
        TextCentered("日本一周ドライブ 3D", sw / 2f, sh * 0.16f, 64, Color.White);
        TextCentered("高速道路で日本をめぐり、各地の名所でスタンプを集めよう", sw / 2f, sh * 0.16f + 84 * u, 20, Rgb(230, 235, 245));

        float w = 640 * u, h = 96 * u, x = (sw - w) / 2;
        float y = sh * 0.46f;
        string[,] items =
        {
            { "1", "日本一周モード", "★の10都市をすべて巡って東京へ戻る。時間と費用を競おう" },
            { "2", "フリーモード", "道路に関係なく日本中どこでも走れる。速度制限なし。名所スタンプを集めよう" },
        };
        for (int i = 0; i < 2; i++)
        {
            float yy = y + i * (h + 18 * u);
            Panel(x, yy, w, h, new Color(15, 25, 40, 220));
            Raylib.DrawCircleV(new Vector2(x + 48 * u, yy + h / 2), 26 * u, Orange);
            TextCentered(items[i, 0], x + 48 * u, yy + h / 2 - 17 * u, 30, Color.White);
            Text(items[i, 1], x + 96 * u, yy + 16 * u, 26, Color.White);
            Text(items[i, 2], x + 96 * u, yy + 56 * u, 15, Sub);
        }
        TextCentered("数字キーで選んでください", sw / 2f, y + 2 * (h + 18 * u) + 10 * u, 16, new Color(255, 255, 255, 200));
        TextCentered(RecordText(), sw / 2f, y + 2 * (h + 18 * u) + 44 * u, 16, Rgb(255, 220, 150));
        TextCentered($"F2 BGM {(Audio.BgmOn ? "ON" : "OFF")}　F3 効果音 {(Audio.SfxOn ? "ON" : "OFF")}　（記録は自動でセーブされます）",
            sw / 2f, y + 2 * (h + 18 * u) + 72 * u, 13, new Color(255, 255, 255, 170));
        DrawUpdateBanner(sw, sh, u);
    }

    /// <summary>3Dの案内標識に文字を重ねる。</summary>
    private void DrawSignTexts()
    {
        float focal = Raylib.GetScreenHeight() / 2f / MathF.Tan(camera.FovY * MathF.PI / 360f);
        string line1 = game.Road != null ? $"{game.To.Name}  {game.Remaining:0} km" : "";
        string line2 = game.Road?.Name ?? "";
        foreach (var p in scene.Signs)
        {
            var rel = p - camera.Position;
            float dist = rel.Length();
            if (rel.Z < 4 || dist > 320 || line1 == "") continue;
            var sp = Raylib.GetWorldToScreen(p, camera);
            float px = 0.8f * focal / dist;
            if (px < 4) continue;
            var m1 = Raylib.MeasureTextEx(Draw.Font, line1, px, 0);
            var m2 = Raylib.MeasureTextEx(Draw.Font, line2, px * 0.65f, 0);
            byte a = (byte)Math.Clamp(255 * (1 - (dist - 200) / 120), 0, 255);
            var white = new Color((byte)255, (byte)255, (byte)255, a);
            Raylib.DrawTextEx(Draw.Font, line1, new Vector2(sp.X - m1.X / 2, sp.Y - px * 0.95f), px, 0, white);
            Raylib.DrawTextEx(Draw.Font, line2, new Vector2(sp.X - m2.X / 2, sp.Y + px * 0.15f), px * 0.65f, 0, white);
        }
    }

    private void DrawInfoPanel(float x, float y)
    {
        float u = Ui;
        float w = 430 * u;
        var goals = MapData.Cities.Where(c => c.IsGoal && c != game.StartCity).ToList();
        bool tour = game.Mode == GameMode.Tour;
        float h = ((game.NavTarget != null ? 26 : 0) + (tour ? 246 : 170)) * u;
        Panel(x, y, w, h);
        float cx = x + 16 * u, cy = y + 12 * u;

        Text(Game.FormatClock(game.GameMinutes), cx, cy, 22, Orange);
        if (game.TimeScale > 1) Text($"早送り ×{game.TimeScale:0}", cx + 230 * u, cy + 4 * u, 16, Rgb(255, 220, 80));
        cy += 32 * u;

        if (detour != null)
            Text($"寄り道中：{detour.Spot.City.Name}の一般道", cx, cy, 18, Color.White);
        else if (game.AtCity != null)
            Text($"停車中：{game.AtCity.Name}", cx, cy, 18, Color.White);
        else if (game.Road != null)
        {
            string eta = game.Speed > 1
                ? "  到着予想 " + Game.FormatClock(game.GameMinutes + game.Remaining / game.Speed * 60)[^5..]
                : "";
            Text($"{game.Road.Name}　{game.To.Name}方面", cx, cy, 18, Color.White);
            cy += 26 * u;
            Text($"残り {game.Remaining:0} km{eta}", cx, cy, 15, Rgb(220, 225, 235));
        }
        cy += 28 * u;

        if (game.NavTarget != null)
        {
            Game.ShortestPath(game.NavOrigin, game.NavTarget, out double km);
            Text($"ナビ：{game.NavTarget.Name}まで {km + game.Remaining:0} km（自動走行 {(game.AutoDrive ? "ON" : "OFF")}）",
                cx, cy, 15, Orange);
            cy += 26 * u;
        }

        Text($"走行 {game.Odometer:N0} km　支出 {game.TotalPaid:N0} 円", cx, cy, 15, Rgb(220, 225, 235));
        cy += 22 * u;
        Text($"高速 {game.TollPaid:N0}／燃料 {game.FuelPaid:N0}／罰金・修理 {game.FinePaid + game.RescuePaid + game.RepairPaid:N0}",
            cx, cy, 13, Sub);
        cy += 26 * u;

        Text($"名所スタンプ {stamps.Count}/{Spots.All.Count}（B：スタンプ帳）", cx, cy, 15, Rgb(255, 200, 200));
        cy += 26 * u;
        if (!tour) return;

        Text($"★目的地 {game.Visited.Count}/{game.GoalCount}（全部回って東京へ）", cx, cy, 15, Color.White);
        cy += 24 * u;
        for (int i = 0; i < goals.Count; i++)
        {
            bool done = game.Visited.Contains(goals[i]);
            Text((done ? "●" : "○") + goals[i].Name, cx + i % 5 * 80 * u, cy + i / 5 * 22 * u, 14,
                done ? Rgb(110, 230, 110) : Rgb(200, 200, 200));
        }
    }

    private void DrawExitSign(int sw, float u)
    {
        if (game.Road == null || game.OnFerry || game.Remaining > 15) return;
        string main = $"{game.To.Name}  {game.Remaining:0.0} km";
        string sub = game.Mode == GameMode.Tour && game.To.IsGoal && !game.Visited.Contains(game.To) && game.To != game.StartCity ? "★目的地" : "JCT / IC";
        if (pendingDetour != null) sub = "出口 → " + pendingDetour.Name;
        else if (game.FreeDrive || (game.NavTarget != null && game.AutoDrive))
        {
            var r = game.PlannedRoad();
            if (r != null && game.NavTarget != game.To) sub += $"　→ {r.Name}（{r.Other(game.To).Name}方面）";
        }
        var m = Measure(main, 26);
        var m2 = Measure(sub, 15);
        float w = Math.Max(m.X, m2.X) + 40 * u;
        float x = (sw - w) / 2, y = 16 * u;
        Raylib.DrawRectangleRounded(new Rectangle(x, y, w, 76 * u), 0.15f, 6, SignGreen);
        Raylib.DrawRectangleRoundedLinesEx(new Rectangle(x + 3 * u, y + 3 * u, w - 6 * u, 70 * u), 0.15f, 6, 2 * u, Color.White);
        TextCentered(main, sw / 2f, y + 8 * u, 26, Color.White);
        TextCentered(sub, sw / 2f, y + 46 * u, 15, Color.White);
    }

    private void DrawSpeedometer(float cx, float cy, float u)
    {
        var center = new Vector2(cx, cy);
        float r = 130 * u;
        Raylib.DrawCircleV(center, r + 10 * u, new Color(15, 20, 30, 200));
        const float a0 = 135, a1 = 405;
        Raylib.DrawRing(center, r - 14 * u, r, a0, a1, 48, new Color(70, 75, 90, 255));
        float kmh = CurrentKmh;
        float limit = freeRoam ? float.MaxValue : detour != null ? 60 : (float)Game.LegalSpeed;
        float gaugeMax = freeRoam ? Math.Max(200f, MathF.Ceiling(Math.Abs(kmh) / 100f + 0.3f) * 100) : (float)Game.MaxSpeed;
        float t = Math.Abs(kmh) / gaugeMax;
        Color c = kmh > limit + 20 ? Rgb(255, 80, 60) : kmh > limit ? Rgb(255, 210, 60) : Rgb(80, 200, 255);
        if (t > 0.002f) Raylib.DrawRing(center, r - 14 * u, r, a0, a0 + (a1 - a0) * t, 48, c);
        // 制限速度の目盛り
        float la = (a0 + (a1 - a0) * Math.Min(1, limit / gaugeMax)) * MathF.PI / 180;
        if (!freeRoam) Raylib.DrawLineEx(center + new Vector2(MathF.Cos(la), MathF.Sin(la)) * (r - 20 * u),
            center + new Vector2(MathF.Cos(la), MathF.Sin(la)) * (r + 4 * u), 3 * u, Color.White);

        string sp = $"{kmh:0}";
        var m = Measure(sp, 56);
        Text(sp, cx - m.X / 2, cy - 52 * u, 56, game.OnFerry ? Rgb(120, 170, 255) : Color.White);
        TextCentered(game.OnFerry ? "フェリー航行中" : pstate == PlayerState.Water ? "km/h（海上）" : "km/h", cx, cy + 8 * u, 15, Sub);
        TextCentered(VehicleName(playerKind), cx, cy + 74 * u, 14, Rgb(255, 220, 150));
        if (freeRoam && IsFlyer(playerKind))
            TextCentered($"高度 {altitude:N0} m", cx, cy + 40 * u, 20, altitude > 0.5f ? Rgb(140, 220, 255) : Sub);

        if (!freeRoam)
        {
            float ratio = (float)(game.Fuel / Game.TankSize);
            var bar = new Rectangle(cx - 70 * u, cy + 40 * u, 140 * u, 10 * u);
            Raylib.DrawRectangleRec(bar, Rgb(60, 60, 70));
            Color fc = ratio > 0.5f ? Rgb(90, 220, 90) : ratio > 0.2f ? Rgb(255, 170, 40) : Rgb(255, 60, 60);
            Raylib.DrawRectangleRec(new Rectangle(bar.X, bar.Y, bar.Width * ratio, bar.Height), fc);
            TextCentered($"燃料 {game.Fuel:0.0}L", cx, cy + 54 * u, 13, Sub);
        }
    }

    private void DrawLog(float x, float y, float u)
    {
        int n = Math.Min(5, game.Log.Count);
        if (n == 0) return;
        Panel(x, y, 640 * u, 124 * u, new Color(15, 20, 30, 150));
        for (int i = 0; i < n; i++)
        {
            string s = game.Log[game.Log.Count - n + i];
            Text(s, x + 12 * u, y + 8 * u + i * 22 * u, 14, i == n - 1 ? Color.White : Rgb(190, 195, 210));
        }
    }

    private void DrawJunction(int sw, int sh, float u)
    {
        var city = game.AtCity!;
        var navRoad = game.PlannedRoad();
        float w = 560 * u;
        float h = (90 + city.Roads.Count * 34 + (navRoad != null ? 26 : 0)) * u;
        float x = (sw - w) / 2, y = sh * 0.3f;
        Panel(x, y, w, h, new Color(10, 30, 25, 225));
        TextCentered($"{city.Name}　どの道路へ進みますか？", sw / 2f, y + 14 * u, 22, Color.White);
        TextCentered("数字キーで選択（F 給油・M 地図でナビ設定）", sw / 2f, y + 46 * u, 13, Sub);
        float cy = y + 76 * u;
        for (int i = 0; i < city.Roads.Count; i++)
        {
            var r = city.Roads[i];
            var dest = r.Other(city);
            string star = dest.IsGoal && !game.Visited.Contains(dest) && dest != game.StartCity ? "★" : "";
            var col = r == navRoad ? Orange : Color.White;
            Raylib.DrawCircleV(new Vector2(x + 34 * u, cy + 12 * u), 13 * u, r == navRoad ? Orange : Rgb(60, 90, 80));
            TextCentered($"{i + 1}", x + 34 * u, cy + 1 * u, 18, Color.White);
            Text($"{r.Name} → {star}{dest.Name}", x + 60 * u, cy, 18, col);
            Text($"{r.Km:0} km　¥{r.Toll:N0}", x + w - 170 * u, cy + 2 * u, 15, Sub);
            cy += 34 * u;
        }
        if (navRoad != null) TextCentered("↑キーでオレンジの道路へ出発（Q/E で変更・P で名所へ寄り道）", sw / 2f, cy + 2 * u, 15, Orange);
    }

    // ================= 地図（拡大・スクロール・詳細表示） =================

    private float bigMapZoom = 1.0f;
    private Vector2 bigMapPan = Vector2.Zero;
    private bool centerMapOnPlayer;
    private bool bigMapDragging;
    private bool bigMapMoved;
    private Vector2 bigMapDragStart;
    private Vector2 bigMapPanStart;
    private int miniMapZoomIdx = 1;
    private static readonly float[] MiniMapZooms = { 0.5f, 1.0f, 2.0f, 3.6f };

    private static readonly (string Name, int Rank, PointF[] Pts)[] DecodedRivers =
        JapanOutline.AllRivers.Select(r => (r.Name, r.Rank, r.Points.Select(p => Game.ToWorld(p.Lat, p.Lon)).ToArray())).ToArray();

    private static readonly (string Name, PointF[] Pts)[] DecodedLakes =
        JapanOutline.AllLakes.Select(l => (l.Name, l.Points.Select(p => Game.ToWorld(p.Lat, p.Lon)).ToArray())).ToArray();

    private static readonly PointF[][] DecodedIslands =
        JapanOutline.Islands.Select(ring => ring.Select(p => Game.ToWorld(p.Lat, p.Lon)).ToArray()).ToArray();

    private static readonly PointF[][] DecodedDetailedIslands =
        JapanOutline.Detailed.Select(ring => ring.Select(p => Game.ToWorld(p.Lat, p.Lon)).ToArray()).ToArray();

    private void CycleMiniMapZoom(int dir)
    {
        miniMapZoomIdx = Math.Clamp(miniMapZoomIdx + dir, 0, MiniMapZooms.Length - 1);
        string label = miniMapZoomIdx switch { 0 => "広域", 1 => "標準", 2 => "詳細", _ => "超詳細" };
        game.AddLog($"ミニマップ縮尺：{label}（×{MiniMapZooms[miniMapZoomIdx]:0.0}）");
    }

    private Vector2 MapPt(PointF w, Vector2 center, Vector2 origin, float scale) =>
        new(origin.X + (w.X - center.X) * scale, origin.Y + (w.Y - center.Y) * scale);

    private void DrawNetwork(Vector2 center, Vector2 origin, float scale, float u, bool labels, Rectangle? clip = null)
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        var viewRect = clip ?? new Rectangle(-120, -120, sw + 240, sh + 240);
        bool InView(Vector2 p, float margin = 80) =>
            p.X >= viewRect.X - margin && p.X <= viewRect.X + viewRect.Width + margin &&
            p.Y >= viewRect.Y - margin && p.Y <= viewRect.Y + viewRect.Height + margin;

        var to = (PointF p) => MapPt(p, center, origin, scale);
        float effZoom = scale / (38f * u); // 1.0 付近が日本全体表示、大きいほど拡大

        // 全カテゴリのラベル重なり防止リスト（主要都市のラベル領域を最優先で予約）
        var placedLabels = new List<Rectangle>();
        bool TryDrawLabel(string text, float x, float y, float fSize, Color col, bool force = false, Color? bgBox = null)
        {
            var m = Measure(text, fSize);
            var rect = new Rectangle(x - 2 * u, y - 1 * u, m.X + 4 * u, m.Y + 2 * u);
            if (!force && placedLabels.Any(r => Raylib.CheckCollisionRecs(r, rect)))
                return false;
            placedLabels.Add(rect);
            if (bgBox is Color bg)
                Raylib.DrawRectangleRounded(new Rectangle(x - 4 * u, y - 2 * u, m.X + 8 * u, m.Y + 4 * u), 0.3f, 4, bg);
            else
                Text(text, x + 1, y + 1, fSize, new Color((byte)0, (byte)0, (byte)0, (byte)185));
            Text(text, x, y, fSize, col);
            return true;
        }

        // 1) 市街地サークル（拡大時）
        if (effZoom > 1.6f && free != null)
        {
            foreach (var pl in free.Places)
            {
                var p = to(FreeWorld.ToMap(pl.X, pl.Z));
                float rPx = (float)(pl.Radius / FreeWorld.M) * scale;
                if (rPx < 3 * u || !InView(p, rPx)) continue;
                Raylib.DrawCircleV(p, rPx, new Color((byte)180, (byte)165, (byte)140, (byte)38));
                if (effZoom > 2.8f)
                    Raylib.DrawCircleLines((int)p.X, (int)p.Y, rPx, new Color((byte)210, (byte)195, (byte)160, (byte)70));
            }
        }

        // 2) 海岸線（拡大時は小島やリアス式海岸まで精密な Detailed を使う）
        var coastlines = effZoom > 1.45f ? DecodedDetailedIslands : DecodedIslands;
        Color coastCol = effZoom > 1.45f ? Rgb(232, 222, 188) : Rgb(225, 215, 180);
        float coastThick = (effZoom > 2.2f ? 2.4f : 1.9f) * u;
        foreach (var island in coastlines)
        {
            for (int i = 0; i < island.Length; i++)
            {
                var a = to(island[i]);
                var b = to(island[(i + 1) % island.Length]);
                if (!InView(a, 40) && !InView(b, 40)) continue;
                Raylib.DrawLineEx(a, b, coastThick, coastCol);
            }
        }

        // 3) 一級河川（拡大率に応じて支流まで表示）
        foreach (var (_, rank, pts) in DecodedRivers)
        {
            if (effZoom < 1.25f && rank > 5) continue;
            float rThick = (rank <= 4 ? 1.8f : 1.2f) * u;
            var rCol = rank <= 4 ? Rgb(75, 150, 225) : Rgb(60, 125, 195);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var a = to(pts[i]);
                var b = to(pts[i + 1]);
                if (!InView(a, 20) && !InView(b, 20)) continue;
                Raylib.DrawLineEx(a, b, rThick, rCol);
            }
        }

        // 4) 湖沼ライン
        foreach (var (_, lake) in DecodedLakes)
        {
            if (lake.Length == 0) continue;
            var first = to(lake[0]);
            if (!InView(first, 60)) continue;
            for (int i = 0; i < lake.Length; i++)
            {
                var a = to(lake[i]);
                var b = to(lake[(i + 1) % lake.Length]);
                Raylib.DrawLineEx(a, b, 1.8f * u, Rgb(105, 185, 245));
            }
        }

        // 5) 山岳・標高マーカー（アイコンのみ先に描画）
        foreach (var (pName, lat, lon, elev) in GeoFeatures.Peaks)
        {
            bool majorPeak = elev >= 2400 || pName is "富士山" or "旭岳" or "岩手山" or "鳥海山" or "大山" or "石鎚山" or "阿蘇山" or "桜島" or "宮之浦岳" or "漢拏山" or "ロパチン山";
            if (effZoom < 1.3f && !majorPeak) continue;
            if (effZoom < 2.0f && elev < 1400 && !majorPeak) continue;
            var p = to(Game.ToWorld(lat, lon));
            if (!InView(p, 30)) continue;

            Color peakCol = elev >= 2800 ? Rgb(245, 250, 255) : elev >= 1800 ? Rgb(215, 185, 145) : Rgb(155, 185, 135);
            float triR = (majorPeak ? 5.5f : 4.2f) * u;
            Raylib.DrawPoly(p, 3, triR, -90, peakCol);
        }

        // 6) ナビ経路
        var path = game.NavPath();
        if (path != null)
        {
            var prev = to(CarMapPos);
            foreach (var c in path)
            {
                var p = to(Game.ToWorld(c));
                Raylib.DrawLineEx(prev, p, 6 * u, new Color(255, 140, 0, 200));
                prev = p;
            }
        }

        // 7) 高速道路・海上橋・フェリー航路（ライン描画）
        foreach (var r in MapData.Roads)
        {
            var pa = to(Game.ToWorld(r.A));
            var pb = to(Game.ToWorld(r.B));
            if (!InView(pa, 60) && !InView(pb, 60)) continue;
            var col = r.Type switch
            {
                RoadType.Ferry => Rgb(90, 155, 255),
                RoadType.Bridge => Rgb(185, 135, 255),
                RoadType.MegaBridge => Rgb(255, 175, 65),
                _ => Rgb(70, 195, 110),
            };
            float roadW = (effZoom > 2.0f ? 3.2f : 2.5f) * u;
            Raylib.DrawLineEx(pa, pb, roadW, col);
        }
        if (game.Road != null)
            Raylib.DrawLineEx(to(Game.ToWorld(game.From)), to(Game.ToWorld(game.To)), 4.0f * u, Rgb(255, 75, 75));

        // 8) 地方都市・町マーカー（拡大時に段階表示）
        if (effZoom > 1.55f)
        {
            foreach (var (tName, lat, lon, tier) in GeoFeatures.Towns)
            {
                if (effZoom < 2.4f && tier >= 3) continue;
                if (MapData.Cities.Any(c => c.Name == tName)) continue;
                var p = to(Game.ToWorld(lat, lon));
                if (!InView(p, 20)) continue;
                Raylib.DrawCircleV(p, (tier <= 1 ? 3.2f : 2.4f) * u, Rgb(185, 195, 210));
            }
        }

        // 9) 空港アイコン
        foreach (var (_, lat, lon, hdg) in GeoFeatures.Airports)
        {
            var p = to(Game.ToWorld(lat, lon));
            if (!InView(p, 30)) continue;
            float rad = hdg * MathF.PI / 180f;
            var dir = new Vector2(MathF.Sin(rad), -MathF.Cos(rad)) * (6.5f * u);
            Raylib.DrawLineEx(p - dir, p + dir, 2.6f * u, Rgb(80, 225, 255));
            Raylib.DrawCircleV(p, 3.2f * u, Rgb(140, 240, 255));
        }

        // 10) 名所スタンプ地点アイコン
        foreach (var spot in Spots.All)
        {
            PointF mapP;
            if (freeRoam && free != null && free.SpotPos.TryGetValue(spot, out var spos))
                mapP = FreeWorld.ToMap(spos.X, spos.Z);
            else if (GeoFeatures.RealSpots.TryGetValue(spot.Name, out var real))
                mapP = Game.ToWorld(real.Lat, real.Lon);
            else
            {
                var cp = Game.ToWorld(spot.City);
                mapP = new PointF(cp.X + 0.08f, cp.Y - 0.06f);
            }
            var sp = to(mapP);
            if (!InView(sp, 30)) continue;
            bool got = stamps.ContainsKey(spot.Key);
            Raylib.DrawPoly(sp, 4, (effZoom > 1.8f ? 5.2f : 4.2f) * u, 45, got ? Rgb(145, 155, 165) : Rgb(255, 90, 90));
        }

        if (freeRoam && waypoint is (double wx, double wz))
        {
            var wp = to(FreeWorld.ToMap(wx, wz));
            Raylib.DrawCircleLines((int)wp.X, (int)wp.Y, 9 * u, Rgb(255, 160, 40));
            Raylib.DrawCircleV(wp, 3.5f * u, Rgb(255, 160, 40));
        }

        // 11) 主要都市マーカー＆最優先ラベル
        static bool IsKeyOverseas(string name) => name is "豊原" or "オハ" or "ソウル" or "釜山" or "済州" or "台北" or "高雄";
        foreach (var c in MapData.Cities.OrderByDescending(c => c.IsGoal || c == game.StartCity || c == game.NavTarget ? 2 : IsKeyOverseas(c.Name) ? 1 : 0))
        {
            var p = to(Game.ToWorld(c));
            if (!InView(p, 40)) continue;
            bool goal = c.IsGoal;
            bool overseasHub = IsKeyOverseas(c.Name);
            Color fill = c == game.StartCity ? Rgb(60, 190, 255)
                : goal ? (game.Visited.Contains(c) ? Rgb(90, 220, 90) : Rgb(255, 210, 40))
                : overseasHub ? Rgb(255, 165, 110)
                : Color.White;
            Raylib.DrawCircleV(p, (goal ? 5.8f : overseasHub ? 4.8f : 3.8f) * u, fill);
            if (c == game.NavTarget) Raylib.DrawCircleLines((int)p.X, (int)p.Y, 10 * u, Rgb(255, 90, 40));

            if (labels || goal || overseasHub)
            {
                string text = (goal && c != game.StartCity ? "★" : "") + c.Name;
                float fSize = labels ? (goal ? 14.5f : overseasHub ? 13.8f : 13f) : 12f;
                bool force = (goal || overseasHub || c == game.StartCity || c == game.NavTarget) && effZoom >= 1.5f;
                TryDrawLabel(text, p.X + 6 * u, p.Y - 9 * u, fSize, goal ? Rgb(255, 240, 150) : overseasHub ? Rgb(255, 215, 175) : Color.White, force);
            }
        }

        // 12) サブラベル群（優先度順：空港 → 名所 → 山岳 → 地方都市 → 湖沼 → 道路名）
        if (labels)
        {
            if (effZoom > 1.45f)
            {
                foreach (var (apName, lat, lon, _) in GeoFeatures.Airports)
                {
                    var p = to(Game.ToWorld(lat, lon));
                    if (!InView(p, 30)) continue;
                    TryDrawLabel($"[空]{apName}", p.X + 6 * u, p.Y - 7 * u, 11.5f, Rgb(135, 235, 255));
                }
            }

            if (effZoom > 2.2f)
            {
                foreach (var spot in Spots.All)
                {
                    PointF mapP = freeRoam && free != null && free.SpotPos.TryGetValue(spot, out var spos)
                        ? FreeWorld.ToMap(spos.X, spos.Z)
                        : GeoFeatures.RealSpots.TryGetValue(spot.Name, out var real)
                            ? Game.ToWorld(real.Lat, real.Lon)
                            : new PointF(Game.ToWorld(spot.City).X + 0.08f, Game.ToWorld(spot.City).Y - 0.06f);
                    var sp = to(mapP);
                    if (!InView(sp, 30)) continue;
                    bool got = stamps.ContainsKey(spot.Key);
                    TryDrawLabel((got ? "●" : "★") + spot.Name, sp.X + 6 * u, sp.Y - 7 * u, 11f, got ? Rgb(185, 190, 195) : Rgb(255, 215, 130));
                }
            }

            foreach (var (pName, lat, lon, elev) in GeoFeatures.Peaks)
            {
                bool majorPeak = elev >= 2400 || pName is "富士山" or "旭岳" or "岩手山" or "鳥海山" or "大山" or "石鎚山" or "阿蘇山" or "桜島" or "宮之浦岳";
                if (!(effZoom > 2.3f || (effZoom > 1.35f && majorPeak))) continue;
                var p = to(Game.ToWorld(lat, lon));
                if (!InView(p, 30)) continue;
                string lbl = effZoom > 1.8f ? $"▲{pName} {elev:0}m" : $"▲{pName}";
                TryDrawLabel(lbl, p.X + 5 * u, p.Y - 6 * u, 11f, Rgb(235, 225, 195));
            }

            if (effZoom > 1.85f)
            {
                foreach (var (tName, lat, lon, tier) in GeoFeatures.Towns)
                {
                    if (effZoom <= 2.8f && tier > 2) continue;
                    if (MapData.Cities.Any(c => c.Name == tName)) continue;
                    var p = to(Game.ToWorld(lat, lon));
                    if (!InView(p, 20)) continue;
                    TryDrawLabel(tName, p.X + 5 * u, p.Y - 7 * u, 11f, Rgb(195, 205, 220));
                }
            }

            if (effZoom > 2.4f)
            {
                foreach (var (lakeName, lake) in DecodedLakes)
                {
                    if (lake.Length == 0 || string.IsNullOrEmpty(lakeName)) continue;
                    var first = to(lake[0]);
                    if (!InView(first, 40)) continue;
                    TryDrawLabel(lakeName, first.X + 4 * u, first.Y - 6 * u, 11f, Rgb(135, 205, 255));
                }
            }

            if (effZoom > 2.6f)
            {
                foreach (var r in MapData.Roads)
                {
                    var pa = to(Game.ToWorld(r.A));
                    var pb = to(Game.ToWorld(r.B));
                    if (Vector2.Distance(pa, pb) <= 75 * u) continue;
                    var mid = (pa + pb) * 0.5f;
                    if (!InView(mid, 10)) continue;
                    string rText = effZoom > 3.8f ? $"{r.Name} ({r.Km:0}km)" : r.Name;
                    var rm = Measure(rText, 11f);
                    TryDrawLabel(rText, mid.X - rm.X / 2, mid.Y - rm.Y / 2, 11f, Rgb(185, 245, 200), false, new Color((byte)10, (byte)45, (byte)30, (byte)195));
                }
            }
        }

        // 13) 自車マーカー
        var carP = to(CarMapPos);
        float rot = CarMapRot;
        Raylib.DrawPoly(carP, 3, 9.5f * u, rot, Rgb(255, 50, 50));
        Raylib.DrawPolyLinesEx(carP, 3, 9.5f * u, rot, 2 * u, Color.White);
    }

    private void DrawMiniMap(float x, float y, float size)
    {
        float u = Ui;
        var rect = new Rectangle(x, y, size, size);
        Raylib.DrawRectangleRounded(rect, 0.06f, 6, new Color(18, 42, 72, 220));
        Raylib.BeginScissorMode((int)x, (int)y, (int)size, (int)size);
        var car = CarMapPos;
        float zoom = MiniMapZooms[miniMapZoomIdx];
        DrawNetwork(new Vector2(car.X, car.Y), new Vector2(x + size / 2, y + size / 2), 75 * u * zoom, u, zoom >= 1.8f, rect);
        Raylib.EndScissorMode();
        Raylib.DrawRectangleRoundedLinesEx(rect, 0.06f, 6, 1.5f * u, new Color(100, 145, 195, 140));

        // 方位記号（N）と操作案内
        Text("N▲", x + size - 32 * u, y + 6 * u, 12, Rgb(255, 210, 110));
        Text($"M：全体地図　[ ]：縮尺×{zoom:0.0}", x + 8 * u, y + size - 22 * u, 12, Sub);
    }

    private (Vector2 baseCenter, float baseScale) BaseBigMapBounds()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        var pts = DecodedIslands.SelectMany(i => i).ToList();
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        float scale = Math.Min(sw * 0.88f / (maxX - minX), (sh - 130 * Ui) / (maxY - minY));
        return (new Vector2((minX + maxX) / 2, (minY + maxY) / 2), scale);
    }

    private (Vector2 center, Vector2 origin, float scale) BigMapView()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        var (baseCenter, baseScale) = BaseBigMapBounds();
        if (centerMapOnPlayer)
        {
            var cp = CarMapPos;
            bigMapPan = new Vector2(cp.X - baseCenter.X, cp.Y - baseCenter.Y);
            centerMapOnPlayer = false;
        }
        return (baseCenter + bigMapPan, new Vector2(sw / 2f, sh / 2f + 16 * Ui), baseScale * bigMapZoom);
    }

    /// <summary>全体地図のズーム・スクロール操作を処理する。ドラッグ中なら true を返す。</summary>
    private bool UpdateBigMapCamera(out bool clickedLeft, out bool clickedRight)
    {
        clickedLeft = false;
        clickedRight = false;
        float dt = Math.Min(Raylib.GetFrameTime(), 0.05f);
        var (baseCenter, baseScale) = BaseBigMapBounds();
        var (center, origin, scale) = BigMapView();
        var mouse = Raylib.GetMousePosition();

        // マウスホイールまたは +/- キーで拡大・縮小（カーソル位置を中心にズーム）
        float wheel = Raylib.GetMouseWheelMove();
        if (Raylib.IsKeyDown(KeyboardKey.Equal) || Raylib.IsKeyDown(KeyboardKey.KpAdd) || Raylib.IsKeyDown(KeyboardKey.PageUp))
            wheel += dt * 4.5f;
        if (Raylib.IsKeyDown(KeyboardKey.Minus) || Raylib.IsKeyDown(KeyboardKey.KpSubtract) || Raylib.IsKeyDown(KeyboardKey.PageDown))
            wheel -= dt * 4.5f;

        if (Math.Abs(wheel) > 0.001f)
        {
            var worldUnderMouse = new Vector2(center.X + (mouse.X - origin.X) / scale, center.Y + (mouse.Y - origin.Y) / scale);
            bigMapZoom = Math.Clamp(bigMapZoom * MathF.Pow(1.22f, wheel), 1.0f, 16.0f);
            float newScale = baseScale * bigMapZoom;
            var newCenter = worldUnderMouse - new Vector2((mouse.X - origin.X) / newScale, (mouse.Y - origin.Y) / newScale);
            bigMapPan = newCenter - baseCenter;
        }

        // 矢印キー・WASDでスクロール
        float panSpeed = 4.2f / bigMapZoom * dt;
        if (Raylib.IsKeyDown(KeyboardKey.Left) || Raylib.IsKeyDown(KeyboardKey.A)) bigMapPan.X -= panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.Right) || Raylib.IsKeyDown(KeyboardKey.D)) bigMapPan.X += panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.Up) || Raylib.IsKeyDown(KeyboardKey.W)) bigMapPan.Y -= panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.Down) || Raylib.IsKeyDown(KeyboardKey.S)) bigMapPan.Y += panSpeed;

        // Space：現在地にフォーカス、R / Home：全体表示にリセット
        if (Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            var cp = CarMapPos;
            bigMapPan = new Vector2(cp.X - baseCenter.X, cp.Y - baseCenter.Y);
            if (bigMapZoom < 2.8f) bigMapZoom = 2.8f;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.R) || Raylib.IsKeyPressed(KeyboardKey.Home))
        {
            bigMapZoom = 1.0f;
            bigMapPan = Vector2.Zero;
        }

        // マウスドラッグでスクロール、ドラッグなしの短押しでクリック判定
        bool anyDown = Raylib.IsMouseButtonDown(MouseButton.Left) || Raylib.IsMouseButtonDown(MouseButton.Right) || Raylib.IsMouseButtonDown(MouseButton.Middle);
        if (Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsMouseButtonPressed(MouseButton.Middle))
        {
            bigMapDragging = true;
            bigMapMoved = false;
            bigMapDragStart = mouse;
            bigMapPanStart = bigMapPan;
        }
        if (bigMapDragging && anyDown)
        {
            var delta = mouse - bigMapDragStart;
            if (delta.Length() > 6f) bigMapMoved = true;
            if (bigMapMoved)
                bigMapPan = bigMapPanStart - delta / (baseScale * bigMapZoom);
        }
        if (bigMapDragging && !anyDown)
        {
            bool wasMoved = bigMapMoved;
            bigMapDragging = false;
            bigMapMoved = false;
            if (wasMoved) return true;
        }

        if (!bigMapMoved)
        {
            if (Raylib.IsMouseButtonReleased(MouseButton.Left)) clickedLeft = true;
            if (Raylib.IsMouseButtonReleased(MouseButton.Right)) clickedRight = true;
        }
        bigMapPan.X = Math.Clamp(bigMapPan.X, -24f, 24f);
        bigMapPan.Y = Math.Clamp(bigMapPan.Y, -24f, 24f);
        return bigMapMoved;
    }

    private void DrawBigMap(int sw, int sh, float u)
    {
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(14, 32, 56, 244));
        var (center, origin, scale) = BigMapView();
        DrawNetwork(center, origin, scale, u, true);

        // 上部バー：操作ガイド
        Panel(16 * u, 10 * u, sw - 32 * u, 38 * u, new Color(10, 22, 38, 220));
        string topHelp = freeRoam
            ? $"ホイール・+/-：拡大縮小（×{bigMapZoom:0.0}）　ドラッグ・矢印：移動　Space：現在地　R：全体　左クリック：ワープ　右クリック：目的地　M・Esc：閉じる"
            : $"ホイール・+/-：拡大縮小（×{bigMapZoom:0.0}）　ドラッグ・矢印：移動　Space：現在地　R：全体　左クリック：出発/ナビ　右クリック：ナビ　M・Esc：閉じる";
        TextCentered(topHelp, sw / 2f, 19 * u, 14, Color.White);

        // 左下：縮尺スケールバー＆凡例パネル
        DrawMapLegendAndScale(18 * u, sh - 148 * u, scale, u);

        // 右下：マウスカーソル位置の詳細インスペクタ
        DrawMapHoverInspector(sw, sh, center, origin, scale, u);
    }

    private void DrawMapLegendAndScale(float x, float y, float scale, float u)
    {
        Panel(x, y, 320 * u, 130 * u, new Color(10, 22, 38, 215));
        float cx = x + 14 * u, cy = y + 10 * u;
        Text($"地図の凡例（拡大率 ×{bigMapZoom:0.0}）", cx, cy, 14, Orange);
        cy += 22 * u;
        Text("●水色：出発地　★黄/緑：目的地都市　●白：主要IC都市", cx, cy, 12, Rgb(225, 230, 240));
        cy += 18 * u;
        Text("◆赤/灰：名所スタンプ　[空]水色：空港　▲：主な山岳", cx, cy, 12, Rgb(225, 230, 240));
        cy += 18 * u;
        Text("■緑：高速道　■紫：連絡橋　■橙：海上大橋　■青：航路", cx, cy, 12, Rgb(200, 220, 235));
        cy += 24 * u;

        // 縮尺バー（画面上で約 90〜160px になるキリの良い実距離 km を計算）
        // マップ座標 1.0 = 緯度1度 ≒ 実距離 111 km（ゲーム内 FreeWorld.M メートル）
        double[] niceKms = { 5, 10, 20, 50, 100, 200, 500 };
        double chosenKm = 200;
        foreach (double km in niceKms)
        {
            double px = (km / 111.0) * scale;
            if (px >= 65 * u && px <= 170 * u) { chosenKm = km; break; }
        }
        float barPx = (float)((chosenKm / 111.0) * scale);
        Raylib.DrawLineEx(new Vector2(cx, cy + 8 * u), new Vector2(cx + barPx, cy + 8 * u), 3 * u, Color.White);
        Raylib.DrawLineEx(new Vector2(cx, cy + 2 * u), new Vector2(cx, cy + 14 * u), 2 * u, Color.White);
        Raylib.DrawLineEx(new Vector2(cx + barPx, cy + 2 * u), new Vector2(cx + barPx, cy + 14 * u), 2 * u, Color.White);
        Text($"{chosenKm:0} km", cx + barPx + 10 * u, cy, 13, Color.White);
    }

    private void DrawMapHoverInspector(int sw, int sh, Vector2 center, Vector2 origin, float scale, float u)
    {
        var mouse = Raylib.GetMousePosition();
        var mp = new PointF(center.X + (mouse.X - origin.X) / scale, center.Y + (mouse.Y - origin.Y) / scale);
        double lat = 37.0 - mp.Y;
        double lon = 137.0 + mp.X / Math.Cos(36 * Math.PI / 180);

        string titleLine = $"北緯 {lat:0.00}° 東経 {lon:0.00}°";
        string subLine1 = "左クリック：この場所へワープ / 右クリック：目的地に設定";
        string subLine2 = "";
        Color titleCol = Rgb(180, 220, 255);

        float bestD = 18 * u;

        // 1) 主要都市のホバー判定
        foreach (var c in MapData.Cities)
        {
            float d = Vector2.Distance(MapPt(Game.ToWorld(c), center, origin, scale), mouse);
            if (d < bestD)
            {
                bestD = d;
                string goalTag = c.IsGoal ? "（★日本一周 目的地）" : "";
                titleLine = $"都市：{c.Name}{goalTag}";
                titleCol = c.IsGoal ? Rgb(255, 220, 90) : Color.White;
                var roads = string.Join("・", c.Roads.Select(r => $"{r.Name}({r.Other(c).Name} {r.Km:0}km)").Take(4));
                subLine1 = $"接続道路：{roads}";
                var citySpots = Spots.At(c).Select(s => (stamps.ContainsKey(s.Key) ? "●" : "★") + s.Name);
                subLine2 = $"名所：{string.Join(" / ", citySpots)}";
            }
        }

        // 2) 空港のホバー判定
        foreach (var (apName, aLat, aLon, hdg) in GeoFeatures.Airports)
        {
            float d = Vector2.Distance(MapPt(Game.ToWorld(aLat, aLon), center, origin, scale), mouse);
            if (d < bestD)
            {
                bestD = d;
                titleLine = $"[空] {apName}（滑走路方位 {hdg:0}°）";
                titleCol = Rgb(120, 235, 255);
                subLine1 = "戦闘機・旅客機・ヘリの離着陸が可能な500m級滑走路と管制塔";
                subLine2 = "左クリックで滑走路端へ直接ワープして離陸準備できます";
            }
        }

        // 3) 名所のホバー判定
        foreach (var spot in Spots.All)
        {
            PointF spPt = freeRoam && free != null && free.SpotPos.TryGetValue(spot, out var spos)
                ? FreeWorld.ToMap(spos.X, spos.Z)
                : GeoFeatures.RealSpots.TryGetValue(spot.Name, out var real)
                    ? Game.ToWorld(real.Lat, real.Lon)
                    : new PointF(Game.ToWorld(spot.City).X + 0.08f, Game.ToWorld(spot.City).Y - 0.06f);
            float d = Vector2.Distance(MapPt(spPt, center, origin, scale), mouse);
            if (d < bestD)
            {
                bestD = d;
                bool got = stamps.ContainsKey(spot.Key);
                titleLine = $"{(got ? "●" : "★")} 名所：{spot.Name}（{spot.City.Name}）";
                titleCol = got ? Rgb(180, 235, 180) : Rgb(255, 200, 120);
                subLine1 = got ? "スタンプ獲得済み！" : "未獲得の名所スタンプ（近づくとスタンプGET）";
                subLine2 = $"北緯 {lat:0.00}° 東経 {lon:0.00}°";
            }
        }

        // 4) 山岳のホバー判定
        foreach (var (pName, pLat, pLon, elev) in GeoFeatures.Peaks)
        {
            float d = Vector2.Distance(MapPt(Game.ToWorld(pLat, pLon), center, origin, scale), mouse);
            if (d < bestD)
            {
                bestD = d;
                titleLine = $"▲ {pName}（標高 {elev:N0} m）";
                titleCol = Rgb(245, 235, 200);
                subLine1 = $"ゲーム内山頂高さ：{elev * FreeWorld.VScale:0} m　北緯 {pLat:0.00}° 東経 {pLon:0.00}°";
                subLine2 = "車や戦車で登山したり、航空機で山頂上空を遊覧できます";
            }
        }

        float pw = 520 * u, ph = (string.IsNullOrEmpty(subLine2) ? 72 : 94) * u;
        float px = sw - pw - 18 * u, py = sh - ph - 18 * u;
        Panel(px, py, pw, ph, new Color(10, 22, 38, 220));
        Text(titleLine, px + 14 * u, py + 10 * u, 16, titleCol);
        Text(subLine1, px + 14 * u, py + 36 * u, 13, Rgb(220, 225, 235));
        if (!string.IsNullOrEmpty(subLine2))
            Text(subLine2, px + 14 * u, py + 58 * u, 13, Rgb(190, 210, 230));
    }

    private void HandleBigMapMouse()
    {
        if (UpdateBigMapCamera(out bool left, out bool right)) return;
        if (!left && !right) return;

        var (center, origin, scale) = BigMapView();
        var mouse = Raylib.GetMousePosition();
        City? hit = null;
        float best = 18 * Ui;
        foreach (var c in MapData.Cities)
        {
            float d = Vector2.Distance(MapPt(Game.ToWorld(c), center, origin, scale), mouse);
            if (d < best) { best = d; hit = c; }
        }
        if (hit == null) return;

        if (left && game.AtCity != null)
        {
            var road = game.AtCity.Roads.Where(r => r.Other(game.AtCity) == hit).MinBy(r => r.Km);
            if (road != null)
            {
                game.Depart(road);
                bigMap = false;
                return;
            }
        }
        game.SetNav(hit);
    }

    private void DrawClear(int sw, int sh, float u)
    {
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(0, 0, 0, 140));
        float w = 520 * u, h = 440 * u, x = (sw - w) / 2, y = (sh - h) / 2;
        Panel(x, y, w, h, new Color(255, 252, 240, 245));
        TextCentered("日本一周 達成！", sw / 2f, y + 24 * u, 40, Rgb(230, 120, 0));
        string[] lines =
        {
            $"所要時間　{Game.FormatDuration(game.GameMinutes - game.StartMinutes)}",
            $"走行距離　{game.Odometer:N0} km",
            $"高速料金　{game.TollPaid:N0} 円",
            $"燃料代　　{game.FuelPaid:N0} 円",
            $"罰金・修理　{game.FinePaid + game.RescuePaid + game.RepairPaid:N0} 円（違反 {game.Violations}回・事故 {game.Crashes}回）",
            $"合計支出　{game.TotalPaid:N0} 円",
            $"名所スタンプ　{stamps.Count} / {Spots.All.Count}",
        };
        float cy = y + 100 * u;
        foreach (var line in lines)
        {
            Text(line, x + 50 * u, cy, 19, Rgb(30, 30, 30));
            cy += 34 * u;
        }
        Text("N：もう一度遊ぶ", x + 50 * u, cy + 12 * u, 16, Rgb(90, 90, 90));
    }
}
