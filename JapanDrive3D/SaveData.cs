using System.Text.Json;

namespace JapanDrive;

/// <summary>
/// セーブデータ。%LOCALAPPDATA%\JapanDrive3D\save.json に自動で保存する。
/// </summary>
public sealed class SaveData
{
    public Dictionary<string, double> Stamps { get; set; } = new();
    public int Destroyed { get; set; }
    public string Vehicle { get; set; } = nameof(VehicleKind.Sports);
    public bool Bgm { get; set; } = true;
    public bool Sfx { get; set; } = true;

    // 日本一周モードの記録
    public int TourClears { get; set; }
    public double BestTourMinutes { get; set; }   // 0 = まだ記録なし
    public int BestTourCost { get; set; }

    // フリーモードで最後にいた場所
    public double? FreeX { get; set; }
    public double? FreeZ { get; set; }
    public float FreeHeading { get; set; }

    /// <summary>保存先（環境変数 JAPANDRIVE_SAVE で変えられる：テスト用）。</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("JAPANDRIVE_SAVE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JapanDrive3D", "save.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static SaveData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SaveData>(File.ReadAllText(FilePath), Options) ?? new SaveData();
        }
        catch
        {
            // 壊れていたら新しく始める（元のファイルは残しておく）
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        return new SaveData();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // 保存できなくてもゲームは続ける
        }
    }
}
