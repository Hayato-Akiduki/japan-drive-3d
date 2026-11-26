using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>タイトル画面：夕暮れの高速道路を背景に、左側にロゴと縦並びのメニュー。</summary>
public sealed partial class App
{
    private enum TitleAction { Resume, NewTour, Free, Room, Settings }

    private readonly record struct TitleItem(TitleAction Action, string Key, string Label, string Description);

    /// <summary>キーボード（↑↓）またはマウスで選んでいる項目。</summary>
    private int titleSel;

    /// <summary>タイトル背景の時刻（夕暮れ）。</summary>
    private const double TitleMinutes = 17.6 * 60;

    private List<TitleItem> TitleItems()
    {
        var items = new List<TitleItem>();
        if (CanResumeTour && save.Tour is { } progress)
            items.Add(new(TitleAction.Resume, "4", "日本一周を続ける", $"保存した旅の続きから。現在地：{progress.City}"));
        items.Add(new(TitleAction.NewTour, "1", CanResumeTour ? "日本一周を最初から" : "日本一周モード",
            newTourConfirm > 0
                ? "途中経過を上書きします。もう一度 1 キーかクリックで開始。"
                : "★の10都市をすべて巡って東京へ戻る。時間と費用を競おう"));
        items.Add(new(TitleAction.Free, "2", "フリーモード", "道路に関係なく日本中どこでも走れる。名所スタンプを集めよう"));
        items.Add(new(TitleAction.Room, "3", "マルチプレイ", "最大4人でルームを作って、いっしょに走る・競う"));
        items.Add(new(TitleAction.Settings, "S", "設定", "音量・速度上限・初期シフトモード（自動でセーブされます）"));
        return items;
    }

    private static float TitleLeft(int sw, float u) => Math.Max(24 * u, Math.Min(88 * u, sw * 0.06f));

    private static Rectangle TitleItemRect(int index, int sw, int sh, float u)
    {
        float x = TitleLeft(sw, u);
        float w = Math.Min(470 * u, sw - x - 24 * u);
        float h = 58 * u, gap = 6 * u;
        return new Rectangle(x, sh * 0.40f + index * (h + gap), w, h);
    }

    private static Rectangle TitleToggleRect(int index, int sw, int sh, float u)
    {
        float bw = 150 * u, bh = 30 * u, gap = 8 * u;
        float right = sw - 24 * u;
        return new Rectangle(right - (3 - index) * bw - (2 - index) * gap, sh - 76 * u, bw, bh);
    }

    private void UpdateTitle(float dt)
    {
        titleTime += dt;
        newTourConfirm = Math.Max(0, newTourConfirm - dt);
        playerS += 22 * dt;
        scene.PlayerS = playerS;
        traffic.Update(dt, playerS, playerU, 22);
        HandleUpdateKeys();

        var items = TitleItems();
        titleSel = Math.Clamp(titleSel, 0, items.Count - 1);
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Draw.Ui;
        var mouse = Raylib.GetMousePosition();

        if (Raylib.GetMouseDelta() != Vector2.Zero)
        {
            for (int i = 0; i < items.Count; i++)
                if (Raylib.CheckCollisionPointRec(mouse, TitleItemRect(i, sw, sh, u))) SelectTitleItem(i);
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressed(KeyboardKey.W))
            SelectTitleItem((titleSel + items.Count - 1) % items.Count);
        else if (Raylib.IsKeyPressed(KeyboardKey.Down))
            SelectTitleItem((titleSel + 1) % items.Count);
        else if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter) || Raylib.IsKeyPressed(KeyboardKey.Space))
            RunTitleAction(items[titleSel].Action);
        else if (Raylib.IsKeyPressed(KeyboardKey.One) || Raylib.IsKeyPressed(KeyboardKey.Kp1)) RunTitleAction(TitleAction.NewTour);
        else if (Raylib.IsKeyPressed(KeyboardKey.Two) || Raylib.IsKeyPressed(KeyboardKey.Kp2)) RunTitleAction(TitleAction.Free);
        else if (CanResumeTour && (Raylib.IsKeyPressed(KeyboardKey.Four) || Raylib.IsKeyPressed(KeyboardKey.Kp4)))
            RunTitleAction(TitleAction.Resume);
        else if (Raylib.IsKeyPressed(KeyboardKey.Three) || Raylib.IsKeyPressed(KeyboardKey.Kp3)) RunTitleAction(TitleAction.Room);
        else if (Raylib.IsKeyPressed(KeyboardKey.S)) RunTitleAction(TitleAction.Settings);
        else if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            bool handled = false;
            for (int i = 0; i < items.Count && !handled; i++)
            {
                if (!Raylib.CheckCollisionPointRec(mouse, TitleItemRect(i, sw, sh, u))) continue;
                titleSel = i;
                RunTitleAction(items[i].Action);
                handled = true;
            }
            for (int ti = 0; ti < 3 && !handled; ti++)
            {
                if (!Raylib.CheckCollisionPointRec(mouse, TitleToggleRect(ti, sw, sh, u))) continue;
                if (ti == 0) ToggleBgm();
                else if (ti == 1) ToggleSfx();
                else ToggleFreeSpeedLimit();
                handled = true;
            }
        }
        UpdateCamera(dt);
    }

    private void SelectTitleItem(int index)
    {
        if (index == titleSel) return;
        titleSel = index;
        Audio.Play(Audio.Select);
    }

    private void RunTitleAction(TitleAction action)
    {
        switch (action)
        {
            case TitleAction.Resume: StartGame(GameMode.Tour, resumeTour: true); break;
            case TitleAction.NewTour: RequestNewTour(); break;
            case TitleAction.Free: StartGame(GameMode.Free); break;
            case TitleAction.Room: OpenRoomMenu(); break;
            case TitleAction.Settings: OpenSettings(); break;
        }
        // 項目が増減しても同じ項目を選んだままにする
        int index = TitleItems().FindIndex(item => item.Action == action);
        if (index >= 0) titleSel = index;
    }

    /// <summary>タイトルのカメラ：車の斜め前をゆっくり行き来し、車は画面の右寄りに映す。</summary>
    private static (Vector3 Pos, Vector3 Target) TitleCamera(Vector3 car, float t)
    {
        float a = 0.75f + MathF.Sin(t * 0.11f) * 0.55f;
        float dist = 9.5f + MathF.Sin(t * 0.07f) * 1.5f;
        var pos = car + new Vector3(MathF.Sin(a) * dist, 1.7f + MathF.Sin(t * 0.09f) * 0.35f, MathF.Cos(a) * dist);
        var look = Vector3.Normalize(car - pos);
        var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
        var target = car + new Vector3(0, 0.9f, 0) - right * 3.2f;
        return (pos, target);
    }

    private void DrawTitle()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Ui;
        var mouse = Raylib.GetMousePosition();
        float intro = Math.Clamp(titleTime / 0.9f, 0, 1);
        intro = 1 - (1 - intro) * (1 - intro);
        int A(float a) => (int)Math.Clamp(a * intro, 0, 255);

        // 左側を暗くして文字を読みやすく、上下は映画のように少し締める
        Raylib.DrawRectangleGradientH(0, 0, (int)(sw * 0.66f), sh, new Color(6, 10, 22, A(225)), new Color(6, 10, 22, 0));
        Raylib.DrawRectangleGradientV(0, (int)(sh * 0.72f), sw, (int)(sh * 0.28f) + 1, new Color(0, 0, 0, 0), new Color(0, 0, 0, A(170)));
        Raylib.DrawRectangleGradientV(0, 0, sw, (int)(sh * 0.12f), new Color(0, 0, 0, A(120)), new Color(0, 0, 0, 0));

        DrawTitleLogo(sw, sh, u, intro);

        var items = TitleItems();
        titleSel = Math.Clamp(titleSel, 0, items.Count - 1);
        for (int i = 0; i < items.Count; i++)
            DrawTitleItem(items[i], TitleItemRect(i, sw, sh, u), i == titleSel, u, intro, i);

        var last = TitleItemRect(items.Count - 1, sw, sh, u);
        float descY = last.Y + last.Height + 16 * u;
        Raylib.DrawRectangle((int)last.X, (int)descY, (int)(2 * u), (int)(22 * u), new Color(255, 165, 40, A(200)));
        Text(items[titleSel].Description, last.X + 12 * u, descY + 2 * u, 16,
            items[titleSel].Action == TitleAction.NewTour && newTourConfirm > 0 ? Rgb(255, 190, 120, A(255)) : new Color(225, 232, 245, A(235)));
        if (tourResumeUnavailable)
            Text("保存した日本一周を読み込めませんでした", last.X + 12 * u, descY + 30 * u, 15, Rgb(255, 185, 120, A(255)));
        Text("↑↓ で選んで Enter　／　数字キー・クリックでも選べます", last.X, sh - 38 * u, 13, new Color(255, 255, 255, A(140)));

        DrawTitleRecords(sw, sh, u, intro);
        DrawTitleToggles(sw, sh, u, mouse, intro);
        DrawUpdateBanner(sw, sh, u);
        if (settingsOpen) DrawSettings(sw, sh, u, mouse);
        if (roomMenu) DrawRoomMenu(sw, sh, u);
    }

    private void DrawTitleLogo(int sw, int sh, float u, float intro)
    {
        float x = TitleLeft(sw, u) - (1 - intro) * 30 * u;
        float y = sh * 0.10f;
        int a = (int)(255 * intro);

        // 日の丸を思わせる赤い円を「日」の後ろに
        var sun = new Vector2(x + 44 * u, y + 52 * u);
        Raylib.DrawCircleV(sun, 74 * u, new Color(230, 60, 50, (int)(40 * intro)));
        Raylib.DrawCircleV(sun, 58 * u, new Color(214, 38, 46, (int)(235 * intro)));

        Text("日本一周", x + 4 * u, y + 6 * u, 92, new Color(0, 0, 0, (int)(140 * intro)));
        Text("日本一周", x, y, 92, new Color(255, 255, 255, a));

        float lineY = y + 118 * u;
        string drive = "D R I V E　　3 D";
        float dw = Measure(drive, 24).X;
        Text(drive, x + 2 * u, lineY, 24, new Color(255, 170, 50, a));
        float lineX = x + dw + 18 * u;
        float lineEnd = x + Math.Max(Measure("日本一周", 92).X, dw + 60 * u);
        if (lineEnd > lineX)
            Raylib.DrawRectangle((int)lineX, (int)(lineY + 13 * u), (int)(lineEnd - lineX), Math.Max(1, (int)(2 * u)), new Color(255, 170, 50, (int)(170 * intro)));

        Text("高速道路で日本をめぐり、各地の名所でスタンプを集めよう", x + 2 * u, lineY + 44 * u, 17,
            new Color(225, 232, 245, (int)(230 * intro)));
    }

    private void DrawTitleItem(TitleItem item, Rectangle r, bool selected, float u, float intro, int index)
    {
        // 上から順に少しずつ現れる
        float t = Math.Clamp(titleTime * 2.2f - index * 0.18f, 0, 1);
        t = 1 - (1 - t) * (1 - t);
        float ox = (1 - t) * -24 * u;
        int A(float alpha) => (int)Math.Clamp(alpha * t, 0, 255);
        var rr = new Rectangle(r.X + ox, r.Y, r.Width, r.Height);

        bool accent = item.Action == TitleAction.Resume;
        if (selected)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(titleTime * 4);
            Raylib.DrawRectangleGradientH((int)rr.X, (int)rr.Y, (int)rr.Width, (int)rr.Height,
                accent ? new Color(30, 110, 100, A(235)) : new Color(40, 62, 96, A(235)), new Color(20, 30, 50, 0));
            Raylib.DrawRectangle((int)rr.X, (int)rr.Y, (int)(5 * u), (int)rr.Height, new Color(255, 165, 40, A(220 + 35 * pulse)));
        }

        var badge = new Rectangle(rr.X + 20 * u, rr.Y + (rr.Height - 34 * u) / 2, 34 * u, 34 * u);
        if (selected)
            Raylib.DrawRectangleRounded(badge, 0.3f, 6, new Color(255, 165, 40, A(255)));
        else
            Raylib.DrawRectangleRoundedLinesEx(badge, 0.3f, 6, 1.5f * u, new Color(255, 255, 255, A(120)));
        TextCentered(item.Key, badge.X + badge.Width / 2, badge.Y + 6 * u, 19,
            selected ? new Color(20, 24, 36, A(255)) : new Color(255, 255, 255, A(170)));

        Color labelCol = selected ? new Color(255, 255, 255, A(255))
            : accent ? new Color(150, 235, 215, A(230)) : new Color(220, 228, 240, A(200));
        Text(item.Label, rr.X + 70 * u + (selected ? 4 * u : 0), rr.Y + (rr.Height - 26 * u) / 2 - 2 * u, 24, labelCol);
    }

    private void DrawTitleRecords(int sw, int sh, float u, float intro)
    {
        string tour = save.TourClears > 0
            ? $"日本一周 {save.TourClears}回クリア（最速 {Game.FormatDuration(save.BestTourMinutes)}）"
            : "日本一周 未クリア";
        string[] chips = { $"名所スタンプ {stamps.Count}/{Spots.All.Count}", tour, $"撃破 {destroyed}" };
        float x = TitleLeft(sw, u), y = sh - 76 * u;
        foreach (var chip in chips)
        {
            float w = Measure(chip, 14).X + 22 * u;
            if (x + w > sw * 0.6f) break;
            Panel(x, y, w, 30 * u, new Color(10, 16, 30, (int)(190 * intro)));
            Text(chip, x + 11 * u, y + 7 * u, 14, Rgb(255, 222, 160, (int)(255 * intro)));
            x += w + 8 * u;
        }
    }

    private void DrawTitleToggles(int sw, int sh, float u, Vector2 mouse, float intro)
    {
        (string label, bool on)[] toggles =
        {
            ($"F2  BGM {(Audio.BgmOn ? "ON" : "OFF")}", Audio.BgmOn),
            ($"F3  効果音 {(Audio.SfxOn ? "ON" : "OFF")}", Audio.SfxOn),
            ($"F4  速度上限 {(freeSpeedLimit ? "ON" : "OFF")}", freeSpeedLimit),
        };
        for (int ti = 0; ti < toggles.Length; ti++)
        {
            var tr = TitleToggleRect(ti, sw, sh, u);
            bool hover = !settingsOpen && !roomMenu && Raylib.CheckCollisionPointRec(mouse, tr);
            var (label, on) = toggles[ti];
            Panel(tr.X, tr.Y, tr.Width, tr.Height, new Color(10, 16, 30, (int)((hover ? 235 : 190) * intro)));
            Raylib.DrawRectangleRoundedLinesEx(tr, 0.3f, 6, 1.2f * u,
                hover ? Orange : on ? new Color(110, 170, 230, (int)(170 * intro)) : new Color(255, 255, 255, (int)(60 * intro)));
            Raylib.DrawCircleV(new Vector2(tr.X + 14 * u, tr.Y + tr.Height / 2), 4 * u,
                on ? Rgb(120, 230, 140, (int)(255 * intro)) : Rgb(110, 115, 130, (int)(255 * intro)));
            Text(label, tr.X + 26 * u, tr.Y + 7 * u, 13, on ? new Color(255, 255, 255, (int)(255 * intro)) : Rgb(170, 180, 200, (int)(255 * intro)));
        }
    }
}
