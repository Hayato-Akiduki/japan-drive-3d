using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>サウンドの再生とセーブデータの読み書き。</summary>
public sealed partial class App
{
    private SaveData save = new();
    private bool Persist => shotFile == null || demoPersist;   // 確認用のデモ実行ではセーブしない
    private bool demoPersist;
    private float saveTimer;
    private int lastViolations, lastCrashes, lastToll;
    private bool clearRecorded;
    private double lastStepDist;

    private void LoadSave()
    {
        if (!Persist) return;
        save = SaveData.Load();
        foreach (var (k, v) in save.Stamps) stamps[k] = v;
        destroyed = save.Destroyed;
        if (Enum.TryParse(save.Vehicle, out VehicleKind kind) && Array.IndexOf(PlayerKinds, kind) >= 0) playerKind = kind;
        Audio.BgmOn = save.Bgm;
        Audio.SfxOn = save.Sfx;
    }

    private void SaveNow()
    {
        if (!Persist) return;
        save.Stamps = new Dictionary<string, double>(stamps);
        save.Destroyed = destroyed;
        save.Vehicle = playerKind.ToString();
        save.Bgm = Audio.BgmOn;
        save.Sfx = Audio.SfxOn;
        if (freeRoam && free != null && !title)
        {
            save.FreeX = free.PX;
            save.FreeZ = free.PZ;
            save.FreeHeading = heading;
        }
        save.Save();
        saveTimer = 0;
    }

    /// <summary>日本一周を達成したら記録を残す。</summary>
    private void RecordClear()
    {
        double minutes = game.GameMinutes - game.StartMinutes;
        bool best = save.BestTourMinutes <= 0 || minutes < save.BestTourMinutes;
        save.TourClears++;
        if (best) save.BestTourMinutes = minutes;
        if (save.BestTourCost <= 0 || game.TotalPaid < save.BestTourCost) save.BestTourCost = game.TotalPaid;
        if (best) game.AddLog("自己ベスト更新！");
        SaveNow();
    }

    private void HandleAudioKeys()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.F2))
        {
            Audio.BgmOn = !Audio.BgmOn;
            if (!title) game.AddLog(Audio.BgmOn ? "BGM：ON" : "BGM：OFF");
            SaveNow();
        }
        if (Raylib.IsKeyPressed(KeyboardKey.F3))
        {
            Audio.SfxOn = !Audio.SfxOn;
            if (!title) game.AddLog(Audio.SfxOn ? "効果音：ON" : "効果音：OFF");
            SaveNow();
        }
    }

    /// <summary>毎フレーム：エンジン音・足音・BGM・ゲーム中の出来事の音・自動セーブ。</summary>
    private void UpdateSound(float dt)
    {
        bool driving = !title && !bigMap && !stampBook && fade < 0.9f;
        Audio.Active = driving;
        Audio.Kind = playerKind;
        Audio.Kmh = game.OnFerry ? 0 : CurrentKmh;
        Audio.Throttle = Raylib.IsKeyDown(KeyboardKey.Up) || Raylib.IsKeyDown(KeyboardKey.W) ? 1 : 0;
        Audio.Rpm = engineRpm;
        Audio.RevLimiting = revLimiting;
        Audio.Drifting = isDrifting;
        Audio.Altitude = freeRoam ? altitude : 0;
        Audio.OnWater = freeRoam ? freeOnWater : pstate == PlayerState.Water;

        // ひづめ・足音：一定の距離を進むごとに鳴らす
        if (driving && playerKind is VehicleKind.Horse or VehicleKind.Hikyaku && Math.Abs(CurrentKmh) > 1)
        {
            bool horse = playerKind == VehicleKind.Horse;
            double dist = freeRoam ? animDist : playerS;
            double stride = horse ? 3.2 : 2.4;
            float[] beats = horse ? new[] { 0f, 0.12f, 0.5f, 0.62f } : new[] { 0f, 0.5f };
            double a = lastStepDist / stride, b = dist / stride;
            if (b - a > 2) a = b - 2;
            for (double c = Math.Floor(a); c <= b; c++)
                foreach (float beat in beats)
                {
                    double at = c + beat;
                    if (at > a && at <= b) Audio.Step(horse);
                }
            lastStepDist = dist;
        }
        else lastStepDist = freeRoam ? animDist : playerS;

        // ゲームの中で起きたこと（違反・事故・料金・達成）に音をつける
        if (!title)
        {
            if (game.Violations > lastViolations) Audio.Play(Audio.Siren, 0.8f);
            if (game.Crashes > lastCrashes) Audio.Play(Audio.Crash);
            if (game.TollPaid > lastToll && !freeRoam) Audio.Play(Audio.Etc, 0.6f);
            if (game.Cleared && !clearRecorded)
            {
                clearRecorded = true;
                Audio.Play(Audio.Fanfare);
                RecordClear();
            }
        }
        lastViolations = game.Violations;
        lastCrashes = game.Crashes;
        lastToll = game.TollPaid;

        Audio.SetBgm(title ? 0 : freeRoam ? 1 : 0);
        Audio.Update();

        saveTimer += dt;
        if (saveTimer > 30 && !title) SaveNow();
    }

    /// <summary>エフェクトに合わせた効果音。</summary>
    private static void FxSound(FxKind kind, double distance, float size)
    {
        switch (kind)
        {
            case FxKind.Fire: Audio.PlayAt(Audio.Explosion, distance, Math.Min(1, size / 1.2f)); break;
            case FxKind.Splash: Audio.PlayAt(Audio.Splash, distance, Math.Min(1, size / 2f)); break;
            case FxKind.Dust: Audio.PlayAt(Audio.Thud, distance, Math.Min(1, size / 1.5f)); break;
        }
    }

    /// <summary>タイトル画面に出す記録。</summary>
    private string RecordText()
    {
        string tour = save.TourClears > 0
            ? $"日本一周クリア {save.TourClears}回（最速 {Game.FormatDuration(save.BestTourMinutes)}）"
            : "日本一周 未クリア";
        return $"名所スタンプ {stamps.Count}/{Spots.All.Count}　{tour}　撃破 {destroyed}";
    }
}
