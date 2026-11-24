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
    public float MasterVolume { get; set; } = 1f;
    public float BgmVolume { get; set; } = 1f;
    public float SfxVolume { get; set; } = 1f;
    public bool FreeSpeedLimit { get; set; }
    public bool ManualTransmission { get; set; }

    // 日本一周モードの記録
    public int TourClears { get; set; }
    public double BestTourMinutes { get; set; }   // 0 = まだ記録なし
    public int BestTourCost { get; set; }

    // フリーモードで最後にいた場所
    public double? FreeX { get; set; }
    public double? FreeZ { get; set; }
    public float FreeHeading { get; set; }

    [System.Text.Json.Serialization.JsonInclude]
    internal TourProgress? Tour { get; set; }
    public int TourHints { get; set; }
    public int FreeHints { get; set; }
    private bool recoveredBackup;

    /// <summary>保存先（環境変数 JAPANDRIVE_SAVE で変えられる：テスト用）。</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("JAPANDRIVE_SAVE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JapanDrive3D", "save.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static SaveData Load()
    {
        string path = FilePath;
        if (Read(path) is { } current) return current;
        if (Read(path + ".bak") is { } backup)
        {
            backup.recoveredBackup = true;
            return backup;
        }
        return new SaveData { recoveredBackup = true };
    }

    private static SaveData? Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(path), Options);
                if (data != null) data.Stamps ??= new();
                return data;
            }
        }
        catch
        {
            if (path == FilePath)
                try { File.Copy(path, path + ".broken", true); } catch { }
        }
        return null;
    }

    public bool Save()
    {
        try
        {
            string path = Path.GetFullPath(FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
                if (File.Exists(path) && !recoveredBackup) File.Replace(tmp, path, path + ".bak");
                else File.Move(tmp, path, true);
                recoveredBackup = false;
                return true;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }
}
