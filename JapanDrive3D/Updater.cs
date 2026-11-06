using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace JapanDrive;

/// <summary>
/// 自動更新：GitHub の最新リリースを確認し、新しい版があればダウンロードして入れ替える。
/// 動いている exe は上書きできないが名前は変えられるので、
/// 「今の exe を .old にリネーム → 新しい exe を置く → 再起動」で更新する（別の更新用プログラムはいらない）。
/// </summary>
public static class Updater
{
    public enum State { Idle, Checking, UpToDate, Available, Downloading, Installed, Failed }

    private const string Repo = "Hayato-Akiduki/japan-drive-3d";
    private const string AssetName = "JapanDrive3D-win-x64.zip";
    public const string ReleasePage = "https://github.com/" + Repo + "/releases/latest";

    // テスト用：環境変数で確認先を差し替えられる
    private static string ApiUrl => Environment.GetEnvironmentVariable("JAPANDRIVE_UPDATE_API")
        ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static volatile State status = State.Idle;
    public static State Status => status;
    public static string LatestTag { get; private set; } = "";
    public static float Progress { get; private set; }
    public static string Message { get; private set; } = "";

    private static string? assetUrl;
    private static long assetSize;

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public static string CurrentText => $"v{Current.Major}.{Current.Minor}.{Current.Build}";

    private static string? ExePath => Environment.ProcessPath;
    private static string ExeDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
    private static string OldExe => Path.Combine(ExeDir, "JapanDrive3D.old.exe");

    /// <summary>配布用の単体 exe のときだけ入れ替えできる（開発中の dotnet run では入れ替えない）。</summary>
    public static bool CanSelfUpdate =>
        ExePath != null && ExePath.EndsWith("JapanDrive3D.exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.Combine(ExeDir, "JapanDrive3D.dll"));

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("JapanDrive3D-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>前回の更新で残った古い exe を消す（終了直後はまだ使用中のことがあるので何回か試す）。</summary>
    public static void CleanupOld()
    {
        Task.Run(async () =>
        {
            for (int i = 0; i < 10 && File.Exists(OldExe); i++)
            {
                try { File.Delete(OldExe); }
                catch { await Task.Delay(1500); }
            }
        });
    }

    /// <summary>新しい版があるか確認する（ネットにつながらないときは何もしない）。</summary>
    public static void StartCheck()
    {
        if (status != State.Idle) return;
        status = State.Checking;
        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string json = await Http.GetStringAsync(ApiUrl, cts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                {
                    status = State.UpToDate;
                    return;
                }
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() != AssetName) continue;
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    assetSize = asset.GetProperty("size").GetInt64();
                }
                LatestTag = tag;
                var cur = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
                var lat = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
                status = lat > cur && assetUrl != null ? State.Available : State.UpToDate;
            }
            catch
            {
                status = State.UpToDate; // オフラインなど：今の版のまま遊ぶ
                Message = "更新の確認ができませんでした（オフライン？）";
            }
        });
    }

    /// <summary>ダウンロードして exe を入れ替える。</summary>
    public static void StartUpdate()
    {
        if (status != State.Available || assetUrl == null) return;
        if (!CanSelfUpdate)
        {
            status = State.Failed;
            Message = "開発中の実行では自動更新できません（配布用の exe で使えます）";
            return;
        }
        status = State.Downloading;
        Progress = 0;
        Task.Run(async () =>
        {
            string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JapanDrive3D", "update");
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                string zip = Path.Combine(work, AssetName);

                // ダウンロード（進み具合を表示）。通信が一瞬切れることもあるので3回まで試す
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        await DownloadTo(zip);
                        break;
                    }
                    catch (Exception) when (attempt < 3)
                    {
                        Progress = 0;
                        await Task.Delay(2000);
                    }
                }

                string extract = Path.Combine(work, "new");
                ZipFile.ExtractToDirectory(zip, extract);
                string newExe = Directory.GetFiles(extract, "JapanDrive3D.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new FileNotFoundException("更新ファイルの中に JapanDrive3D.exe がありません");

                Install(newExe);
                UpdateInstalledVersion();
                Directory.Delete(work, true);
                status = State.Installed;
                Message = "";
            }
            catch (UnauthorizedAccessException)
            {
                status = State.Failed;
                Message = "このフォルダには書き込めないため自動更新できません。O キーでダウンロードページを開いて手動で更新してください";
            }
            catch (Exception e)
            {
                status = State.Failed;
                Message = "更新に失敗しました：" + e.Message + "（O キーでダウンロードページ）";
            }
        });
    }

    private static async Task DownloadTo(string zip)
    {
        using var res = await Http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? assetSize;
        await using var src = await res.Content.ReadAsStreamAsync();
        await using var dst = File.Create(zip);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            done += n;
            if (total > 0) Progress = (float)done / total;
        }
        if (assetSize > 0 && done != assetSize) throw new IOException("ダウンロードしたファイルの大きさが合いません");
    }

    private static void Install(string newExe)
    {
        string exe = ExePath!;
        if (File.Exists(OldExe)) File.Delete(OldExe);
        File.Move(exe, OldExe);   // 動いている exe でも名前は変えられる
        try
        {
            File.Copy(newExe, exe);
            // 遊び方メモなど、一緒に入っているファイルも新しくする
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(newExe)!))
            {
                if (Path.GetFileName(f).Equals("JapanDrive3D.exe", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(ExeDir, Path.GetFileName(f)), true);
            }
        }
        catch
        {
            // 失敗したら元に戻す
            if (File.Exists(exe)) File.Delete(exe);
            File.Move(OldExe, exe);
            throw;
        }
    }

    /// <summary>インストーラーで入れていたら、Windows の「アプリ」一覧に出るバージョンを書き換える。</summary>
    private static void UpdateInstalledVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JapanDrive3D", writable: true);
            key?.SetValue("DisplayVersion", LatestTag.TrimStart('v', 'V'));
        }
        catch
        {
            // 書き換えられなくても更新自体は終わっている
        }
    }

    /// <summary>新しい exe で起動し直す（呼んだ側はそのあとウィンドウを閉じる）。</summary>
    public static void Restart()
    {
        if (ExePath == null) return;
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = ExeDir });
    }

    public static void OpenReleasePage()
    {
        try { Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true }); } catch { }
    }
}
