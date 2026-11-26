using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed partial class App
{
    private const int Drove = 1;
    private const int OpenedMap = 2;
    private const int TriedJunction = 4;
    private const int OpenedBook = 4;
    private float newTourConfirm;
    private bool tourResumeUnavailable;
    private bool CanResumeTour => save.Tour != null && !tourResumeUnavailable;

    private void RequestNewTour()
    {
        if (CanResumeTour && newTourConfirm <= 0)
        {
            newTourConfirm = 5;
            return;
        }
        StartGame(GameMode.Tour);
    }

    private void UpdateDrivingHints()
    {
        if (shotFile != null || onMoon && freeRoam || detour != null || game.Cleared) return;
        int hints = freeRoam ? save.FreeHints : save.TourHints;
        if (Math.Abs(CurrentKmh) >= 5) hints |= Drove;
        if (bigMap) hints |= OpenedMap;
        if (freeRoam)
        {
            if (stampBook) hints |= OpenedBook;
            save.FreeHints = hints;
        }
        else
        {
            if (game.PreferredNext != null || (!bigMap &&
                (Raylib.IsKeyPressed(KeyboardKey.Q) || Raylib.IsKeyPressed(KeyboardKey.E))))
                hints |= TriedJunction;
            save.TourHints = hints;
        }
    }

    private void DrawDrivingHint(int sw, float u)
    {
        if (shotFile != null || settingsOpen || stampBook || bigMap || detour != null ||
            (freeRoam && onMoon) || game.Cleared || fadeDir != 0) return;
        int hints = freeRoam ? save.FreeHints : save.TourHints;
        string? message = (hints & Drove) == 0 ? "↑ を押して発進"
            : (hints & OpenedMap) == 0 ? "M で地図を開いて行き先を探す"
            : freeRoam && (hints & OpenedBook) == 0 ? "B でスタンプ帳を開き、近くの未取得の名所を探す"
            : !freeRoam && (hints & TriedJunction) == 0 ? "Q / E で次の分岐を選ぶ"
            : null;
        if (message == null) return;
        float width = Measure(message, 18).X + 30 * u;
        Panel((sw - width) / 2, 62 * u, width, 39 * u, new Color(18, 39, 61, 230));
        TextCentered(message, sw / 2f, 71 * u, 18, Color.White);
    }

    private void DrawSaveStatus(int sw, int sh, float u)
    {
        if (!saveFailed) return;
        const string message = "保存できませんでした。次の自動保存で再試行します。";
        float width = Measure(message, 14).X + 24 * u;
        Panel((sw - width) / 2f, sh - 69 * u, width, 30 * u, Rgb(113, 45, 34));
        TextCentered(message, sw / 2f, sh - 63 * u, 14, Color.White);
    }
}
