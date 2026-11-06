using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>自動更新の画面表示と操作（タイトル画面）。</summary>
public sealed partial class App
{
    private bool restartRequested;
    private bool demoUpdate;
    private bool updateNotified;
    private bool demoRestart;

    /// <summary>タイトル画面：U 更新、R 再起動、O ダウンロードページを開く。</summary>
    private void HandleUpdateKeys()
    {
        var st = Updater.Status;
        if (st == Updater.State.Available && (Raylib.IsKeyPressed(KeyboardKey.U) || demoUpdate))
        {
            Audio.Play(Audio.Select);
            Updater.StartUpdate();
        }
        if (st == Updater.State.Installed && (Raylib.IsKeyPressed(KeyboardKey.R) || demoRestart))
        {
            SaveNow();
            restartRequested = true;
        }
        if (st is Updater.State.Failed or Updater.State.Available && Raylib.IsKeyPressed(KeyboardKey.O))
            Updater.OpenReleasePage();
    }

    /// <summary>遊んでいる途中で新しい版が見つかったら、一度だけ知らせる。</summary>
    private void NotifyUpdateInGame()
    {
        if (updateNotified || title || Updater.Status != Updater.State.Available) return;
        updateNotified = true;
        game.AddLog($"新しいバージョン {Updater.LatestTag} があります。タイトル画面（N を2回）で更新できます。");
    }

    private void DrawUpdateBanner(int sw, int sh, float u)
    {
        // 右下に今のバージョン
        string ver = Updater.CurrentText + (Updater.Status == Updater.State.UpToDate && Updater.Message == "" ? "（最新版）" : "");
        var vm = Measure(ver, 13);
        Text(ver, sw - vm.X - 16 * u, sh - 26 * u, 13, new Color(255, 255, 255, 150));

        string? msg = null, sub = null;
        Color col = Rgb(255, 210, 120);
        float progress = -1;
        switch (Updater.Status)
        {
            case Updater.State.Available:
                msg = $"新しいバージョン {Updater.LatestTag} があります";
                sub = "U：今すぐ更新する　O：ダウンロードページを開く";
                break;
            case Updater.State.Downloading:
                msg = $"{Updater.LatestTag} をダウンロード中… {Updater.Progress * 100:0}%";
                progress = Updater.Progress;
                break;
            case Updater.State.Installed:
                msg = $"{Updater.LatestTag} に更新しました！";
                sub = "R：今すぐ再起動する（このまま遊んでも、次に起動したときから新しい版になります）";
                col = Rgb(150, 255, 160);
                break;
            case Updater.State.Failed:
                msg = Updater.Message;
                col = Rgb(255, 150, 130);
                break;
        }
        if (msg == null) return;

        float w = Math.Max(Measure(msg, 18).X, sub != null ? Measure(sub, 13).X : 0) + 48 * u;
        float h = (sub != null || progress >= 0 ? 66 : 42) * u;
        float x = (sw - w) / 2, y = sh * 0.06f;
        Panel(x, y, w, h, new Color(20, 30, 50, 230));
        Raylib.DrawRectangleRoundedLinesEx(new Rectangle(x, y, w, h), 0.2f, 6, 2 * u, col);
        TextCentered(msg, sw / 2f, y + 10 * u, 18, col);
        if (sub != null) TextCentered(sub, sw / 2f, y + 40 * u, 13, Rgb(220, 225, 235));
        if (progress >= 0)
        {
            var bar = new Rectangle(x + 24 * u, y + 44 * u, w - 48 * u, 10 * u);
            Raylib.DrawRectangleRec(bar, Rgb(60, 65, 80));
            Raylib.DrawRectangleRec(new Rectangle(bar.X, bar.Y, bar.Width * progress, bar.Height), col);
        }
    }
}
