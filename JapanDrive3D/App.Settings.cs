using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>タイトル・走行画面共通の設定アイコンと設定画面（音量調整など）。</summary>
public sealed partial class App
{
    private bool settingsOpen;
    private int draggingSlider = -1; // 0: マスター, 1: BGM, 2: 効果音
    private bool demoSettings;

    private Rectangle SettingsButtonRect(int sw, float u) =>
        new(title ? sw - 118 * u : sw / 2f - 50 * u, (title ? 18 : 68) * u, 100 * u, 42 * u);

    private static Rectangle SettingsPanelRect(int sw, int sh, float u)
    {
        float w = 640 * u, h = 540 * u;
        return new Rectangle((sw - w) / 2f, (sh - h) / 2f, w, h);
    }

    private static Rectangle SettingsCloseIconRect(Rectangle p, float u) =>
        new(p.X + p.Width - 48 * u, p.Y + 14 * u, 34 * u, 34 * u);

    private static Rectangle SettingsSliderTrackRect(Rectangle p, int index, float u)
    {
        float y = p.Y + 124 * u + index * 58 * u;
        return new Rectangle(p.X + 248 * u, y + 10 * u, 282 * u, 10 * u);
    }

    private static Rectangle SettingsSliderHitRect(Rectangle track, float u) =>
        new(track.X - 10 * u, track.Y - 12 * u, track.Width + 20 * u, track.Height + 24 * u);

    private static Rectangle SettingsAudioToggleRect(Rectangle p, int index, float u)
    {
        // index 1: BGM, index 2: 効果音
        float y = p.Y + 124 * u + index * 58 * u;
        return new Rectangle(p.X + 166 * u, y, 66 * u, 30 * u);
    }

    private static Rectangle SettingsSpeedLimitRect(Rectangle p, float u) =>
        new(p.X + p.Width - 156 * u, p.Y + 340 * u, 128 * u, 34 * u);

    private static Rectangle SettingsTransmissionRect(Rectangle p, float u) =>
        new(p.X + p.Width - 156 * u, p.Y + 402 * u, 128 * u, 34 * u);

    private static Rectangle SettingsResetButtonRect(Rectangle p, float u) =>
        new(p.X + 28 * u, p.Y + p.Height - 58 * u, 176 * u, 38 * u);

    private static Rectangle SettingsCloseButtonRect(Rectangle p, float u) =>
        new(p.X + p.Width - 178 * u, p.Y + p.Height - 58 * u, 150 * u, 38 * u);

    private void OpenSettings()
    {
        settingsOpen = true;
        draggingSlider = -1;
        Audio.Play(Audio.Select, 0.8f);
    }

    private void CloseSettings()
    {
        settingsOpen = false;
        draggingSlider = -1;
        Audio.Play(Audio.Select, 0.7f);
        SaveNow();
    }

    private void UpdateSettings()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.F1))
        {
            CloseSettings();
            return;
        }

        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        float u = Ui;
        var mouse = Raylib.GetMousePosition();
        var panel = SettingsPanelRect(sw, sh, u);

        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            if (Raylib.CheckCollisionPointRec(mouse, SettingsCloseIconRect(panel, u)) ||
                Raylib.CheckCollisionPointRec(mouse, SettingsCloseButtonRect(panel, u)) ||
                !Raylib.CheckCollisionPointRec(mouse, panel))
            {
                CloseSettings();
                return;
            }

            // スライダーのクリック開始
            for (int i = 0; i < 3; i++)
            {
                var track = SettingsSliderTrackRect(panel, i, u);
                if (Raylib.CheckCollisionPointRec(mouse, SettingsSliderHitRect(track, u)))
                {
                    draggingSlider = i;
                    ApplySliderValue(i, mouse.X, track);
                    return;
                }
            }

            // BGM / 効果音 ON・OFF
            if (Raylib.CheckCollisionPointRec(mouse, SettingsAudioToggleRect(panel, 1, u)))
            {
                ToggleBgm();
                return;
            }
            if (Raylib.CheckCollisionPointRec(mouse, SettingsAudioToggleRect(panel, 2, u)))
            {
                ToggleSfx();
                if (Audio.SfxOn) Audio.Play(Audio.Select, 0.8f);
                return;
            }

            // フリーモード速度上限
            if (Raylib.CheckCollisionPointRec(mouse, SettingsSpeedLimitRect(panel, u)))
            {
                ToggleFreeSpeedLimit();
                return;
            }

            // 初期シフトモード（AT / 6速MT）
            if (Raylib.CheckCollisionPointRec(mouse, SettingsTransmissionRect(panel, u)))
            {
                manualTransmission = !manualTransmission;
                if (!title && manualTransmission) manualGear = AutoGearForKmh(CurrentKmh);
                revLimiting = false;
                Audio.Play(Audio.Select, 0.8f);
                SaveNow();
                return;
            }

            // 初期設定に戻す
            if (Raylib.CheckCollisionPointRec(mouse, SettingsResetButtonRect(panel, u)))
            {
                Audio.MasterVolume = 1f;
                Audio.BgmVolume = 1f;
                Audio.SfxVolume = 1f;
                Audio.BgmOn = true;
                Audio.SfxOn = true;
                freeSpeedLimit = false;
                manualTransmission = false;
                revLimiting = false;
                Audio.Play(Audio.Select, 0.9f);
                SaveNow();
                return;
            }
        }

        if (draggingSlider >= 0)
        {
            var track = SettingsSliderTrackRect(panel, draggingSlider, u);
            if (Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                ApplySliderValue(draggingSlider, mouse.X, track);
            }
            if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            {
                if (draggingSlider is 0 or 2) Audio.Play(Audio.Select, 0.8f);
                SaveNow();
                draggingSlider = -1;
            }
        }
    }

    private static void ApplySliderValue(int index, float mouseX, Rectangle track)
    {
        float raw = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        float val = MathF.Round(raw * 100f) / 100f;
        switch (index)
        {
            case 0: Audio.MasterVolume = val; break;
            case 1: Audio.BgmVolume = val; break;
            case 2: Audio.SfxVolume = val; break;
        }
    }

    private void DrawInGameSettings()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        var mouse = Raylib.GetMousePosition();
        if (!bigMap && !stampBook && fadeDir == 0) DrawSettingsButton(sw, Ui, mouse);
        if (settingsOpen) DrawSettings(sw, sh, Ui, mouse);
    }

    /// <summary>歯車アイコンの設定ボタン（走行中は上部中央）。</summary>
    private void DrawSettingsButton(int sw, float u, Vector2 mouse)
    {
        var r = SettingsButtonRect(sw, u);
        bool hover = !settingsOpen && Raylib.CheckCollisionPointRec(mouse, r);
        Color bg = hover ? new Color(34, 56, 88, 240) : new Color(15, 25, 40, 215);
        Panel(r.X, r.Y, r.Width, r.Height, bg);
        if (hover) Raylib.DrawRectangleRoundedLinesEx(r, 0.25f, 6, 1.8f * u, Orange);
        else Raylib.DrawRectangleRoundedLinesEx(r, 0.25f, 6, 1.2f * u, new Color(160, 180, 210, 110));

        var gearCenter = new Vector2(r.X + 24 * u, r.Y + r.Height / 2f);
        DrawGearIcon(gearCenter, 10.5f * u, hover ? Orange : Color.White, bg);
        Text("設定", r.X + 44 * u, r.Y + 11 * u, 18, hover ? Color.White : Rgb(230, 235, 245));
    }

    /// <summary>図形で歯車アイコンを描画する。</summary>
    private static void DrawGearIcon(Vector2 c, float r, Color fg, Color hole)
    {
        const int teeth = 8;
        float toothInner = r * 0.62f;
        float toothOuter = r * 1.12f;
        float toothWidth = r * 0.36f;
        for (int i = 0; i < teeth; i++)
        {
            float a = i * (MathF.PI * 2f / teeth);
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Raylib.DrawLineEx(c + dir * toothInner, c + dir * toothOuter, toothWidth, fg);
        }
        Raylib.DrawCircleV(c, r * 0.78f, fg);
        Raylib.DrawCircleV(c, r * 0.36f, hole);
    }

    private void DrawSettings(int sw, int sh, float u, Vector2 mouse)
    {
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(8, 12, 22, 200));

        var p = SettingsPanelRect(sw, sh, u);
        var panelBg = new Color(18, 28, 46, 246);
        Panel(p.X, p.Y, p.Width, p.Height, panelBg);
        Raylib.DrawRectangleRoundedLinesEx(p, 0.08f, 8, 2f * u, new Color(120, 155, 200, 160));

        // ヘッダー
        DrawGearIcon(new Vector2(p.X + 38 * u, p.Y + 34 * u), 12f * u, Orange, panelBg);
        Text("設定", p.X + 60 * u, p.Y + 19 * u, 28, Color.White);

        Text(title ? "変更は自動保存されます" : "一時停止中・変更は自動保存されます", p.X + 146 * u, p.Y + 30 * u, 14, Sub);
        var closeIcon = SettingsCloseIconRect(p, u);
        bool closeHover = Raylib.CheckCollisionPointRec(mouse, closeIcon);
        Panel(closeIcon.X, closeIcon.Y, closeIcon.Width, closeIcon.Height,
            closeHover ? new Color(170, 55, 55, 230) : new Color(36, 48, 70, 210));
        TextCentered("×", closeIcon.X + closeIcon.Width / 2f, closeIcon.Y + 5 * u, 22, Color.White);

        Raylib.DrawLineEx(
            new Vector2(p.X + 24 * u, p.Y + 64 * u),
            new Vector2(p.X + p.Width - 24 * u, p.Y + 64 * u),
            1.5f * u, new Color(120, 145, 180, 90));

        // ---- サウンドセクション ----
        Text("サウンド", p.X + 28 * u, p.Y + 80 * u, 20, Orange);

        (string label, float value, bool enabled, bool hasToggle)[] sliders =
        {
            ("マスター音量", Audio.MasterVolume, true, false),
            ("BGM", Audio.BgmVolume, Audio.BgmOn, true),
            ("効果音・エンジン", Audio.SfxVolume, Audio.SfxOn, true),
        };

        for (int i = 0; i < sliders.Length; i++)
        {
            var (label, value, enabled, hasToggle) = sliders[i];
            float rowY = p.Y + 124 * u + i * 58 * u;
            Text(label, p.X + 28 * u, rowY + 5 * u, 17, enabled ? Color.White : Sub);

            if (hasToggle)
            {
                var tr = SettingsAudioToggleRect(p, i, u);
                bool th = Raylib.CheckCollisionPointRec(mouse, tr);
                Color tbg = th
                    ? (enabled ? new Color(48, 108, 165, 240) : new Color(70, 80, 100, 235))
                    : (enabled ? new Color(28, 76, 128, 225) : new Color(40, 48, 64, 215));
                Panel(tr.X, tr.Y, tr.Width, tr.Height, tbg);
                if (th) Raylib.DrawRectangleRoundedLinesEx(tr, 0.25f, 4, 1.5f * u, Orange);
                TextCentered(enabled ? "ON" : "OFF", tr.X + tr.Width / 2f, tr.Y + 6 * u, 15, enabled ? Color.White : Sub);
            }

            var track = SettingsSliderTrackRect(p, i, u);
            bool hover = draggingSlider == i || Raylib.CheckCollisionPointRec(mouse, SettingsSliderHitRect(track, u));
            Raylib.DrawRectangleRounded(track, 0.5f, 4, new Color(48, 60, 82, 235));

            float fillW = track.Width * Math.Clamp(value, 0f, 1f);
            if (fillW > 1f)
            {
                Color fillCol = enabled ? (hover ? Rgb(255, 185, 70) : Orange) : Rgb(105, 115, 132);
                Raylib.DrawRectangleRounded(new Rectangle(track.X, track.Y, fillW, track.Height), 0.5f, 4, fillCol);
            }

            float knobX = track.X + fillW;
            float knobY = track.Y + track.Height / 2f;
            float knobR = (hover ? 10.5f : 9f) * u;
            Raylib.DrawCircleV(new Vector2(knobX, knobY), knobR, enabled ? Color.White : Rgb(160, 168, 180));
            if (hover) Raylib.DrawCircleLinesV(new Vector2(knobX, knobY), knobR, Orange);

            Text($"{(int)MathF.Round(value * 100f),3}%", track.X + track.Width + 16 * u, rowY + 5 * u, 17, enabled ? Color.White : Sub);
        }

        // 区切り線
        Raylib.DrawLineEx(
            new Vector2(p.X + 24 * u, p.Y + 298 * u),
            new Vector2(p.X + p.Width - 24 * u, p.Y + 298 * u),
            1.5f * u, new Color(120, 145, 180, 90));

        // ---- ドライブ設定セクション ----
        Text("ドライブ設定", p.X + 28 * u, p.Y + 310 * u, 20, Orange);

        // フリーモード速度上限
        Text("フリーモード速度上限", p.X + 28 * u, p.Y + 340 * u, 17, Color.White);
        Text("乗り物ごとのリアル上限を適用（F4キーでも切替可）", p.X + 28 * u, p.Y + 364 * u, 12, Sub);
        DrawSettingsOptionButton(SettingsSpeedLimitRect(p, u), freeSpeedLimit ? "ON（リアル）" : "OFF（無制限）", freeSpeedLimit, mouse, u);

        // 初期シフトモード
        Text(title ? "初期シフトモード" : "シフトモード", p.X + 28 * u, p.Y + 402 * u, 17, Color.White);
        Text("ギア操作を選択（走行中は H キーでも切替可）", p.X + 28 * u, p.Y + 426 * u, 12, Sub);
        DrawSettingsOptionButton(SettingsTransmissionRect(p, u), manualTransmission ? "6速 MT" : "AT（自動）", manualTransmission, mouse, u);

        // ---- フッターボタン ----
        var resetBtn = SettingsResetButtonRect(p, u);
        bool resetHover = Raylib.CheckCollisionPointRec(mouse, resetBtn);
        Panel(resetBtn.X, resetBtn.Y, resetBtn.Width, resetBtn.Height,
            resetHover ? new Color(65, 76, 98, 240) : new Color(38, 46, 64, 225));
        if (resetHover) Raylib.DrawRectangleRoundedLinesEx(resetBtn, 0.2f, 6, 1.5f * u, Orange);
        TextCentered("初期設定に戻す", resetBtn.X + resetBtn.Width / 2f, resetBtn.Y + 10 * u, 16, Color.White);

        var closeBtn = SettingsCloseButtonRect(p, u);
        bool closeBtnHover = Raylib.CheckCollisionPointRec(mouse, closeBtn);
        Panel(closeBtn.X, closeBtn.Y, closeBtn.Width, closeBtn.Height,
            closeBtnHover ? new Color(255, 175, 55, 245) : new Color(220, 138, 28, 235));
        TextCentered("閉じる (Esc)", closeBtn.X + closeBtn.Width / 2f, closeBtn.Y + 10 * u, 16, Color.White);
    }

    private static void DrawSettingsOptionButton(Rectangle r, string label, bool active, Vector2 mouse, float u)
    {
        bool hover = Raylib.CheckCollisionPointRec(mouse, r);
        Color bg = hover
            ? (active ? new Color(48, 108, 165, 240) : new Color(65, 76, 98, 235))
            : (active ? new Color(28, 76, 128, 225) : new Color(40, 48, 64, 215));
        Panel(r.X, r.Y, r.Width, r.Height, bg);
        if (hover) Raylib.DrawRectangleRoundedLinesEx(r, 0.2f, 6, 1.5f * u, Orange);
        TextCentered(label, r.X + r.Width / 2f, r.Y + 8 * u, 15, active ? Color.White : Rgb(220, 226, 236));
    }
}
