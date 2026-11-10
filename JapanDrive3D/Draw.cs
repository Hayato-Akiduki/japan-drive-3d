using System.Numerics;
using System.Text;
using Raylib_cs;

namespace JapanDrive;

/// <summary>日本語フォントと2D描画の小さなヘルパー。</summary>
public static class Draw
{
    public static Font Font;
    /// <summary>UI倍率（画面高さ900pxを基準）。</summary>
    public static float Ui = 1f;

    public static void LoadFont()
    {
        var chars = new SortedSet<int>();
        for (int c = 32; c < 127; c++) chars.Add(c);
        for (int c = 0xA0; c <= 0xFF; c++) chars.Add(c);
        for (int c = 0x2010; c <= 0x2027; c++) chars.Add(c);
        for (int c = 0x2190; c <= 0x2193; c++) chars.Add(c);
        for (int c = 0x25A0; c <= 0x25FF; c++) chars.Add(c);   // ▶ ● ◆ などの図形
        for (int c = 0x2600; c <= 0x2606; c++) chars.Add(c);   // ★ ☆
        chars.Add('※');
        chars.Add('〒');
        chars.Add('橙');
        for (int c = 0x3000; c <= 0x30FF; c++) chars.Add(c);   // 記号・ひらがな・カタカナ
        for (int c = 0xFF01; c <= 0xFF9F; c++) chars.Add(c);   // 全角英数・半角カナ

        // JIS第1水準漢字（約3000字）をShift_JISから列挙
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding(932);
        for (int lead = 0x88; lead <= 0x98; lead++)
        {
            for (int trail = 0x40; trail <= 0xFC; trail++)
            {
                if (trail == 0x7F) continue;
                string s = sjis.GetString(new[] { (byte)lead, (byte)trail });
                if (s.Length == 1 && s[0] >= 0x4E00 && s[0] <= 0x9FFF) chars.Add(s[0]);
            }
        }
        // 地名・道路名に第2水準の字があっても表示できるように
        foreach (var c in MapData.Cities) foreach (char ch in c.Name) chars.Add(ch);
        foreach (var r in MapData.Roads) foreach (char ch in r.Name) chars.Add(ch);
        foreach (var sp in Spots.All) foreach (char ch in sp.Name) chars.Add(ch);
        foreach (var t in GeoFeatures.Towns) foreach (char ch in t.Name) chars.Add(ch);
        foreach (var p in GeoFeatures.Peaks) foreach (char ch in p.Name) chars.Add(ch);
        foreach (var ap in GeoFeatures.Airports) foreach (char ch in ap.Name) chars.Add(ch);
        foreach (var lk in JapanOutline.AllLakes) foreach (char ch in lk.Name) chars.Add(ch);
        foreach (var rv in JapanOutline.AllRivers) foreach (char ch in rv.Name) chars.Add(ch);

        string? path = null;
        foreach (var ttc in new[] { "YuGothB.ttc", "BIZ-UDGothicB.ttc", "meiryob.ttc", "msgothic.ttc" })
        {
            path = ExtractFirstFont(Path.Combine(@"C:\Windows\Fonts", ttc));
            if (path != null) break;
        }
        path ??= new[] { @"C:\Windows\Fonts\NotoSansJP-VF.ttf" }.FirstOrDefault(File.Exists);
        var cps = chars.ToArray();
        Font = path != null ? Raylib.LoadFontEx(path, 40, cps, cps.Length) : Raylib.GetFontDefault();
        Raylib.SetTextureFilter(Font.Texture, TextureFilter.Bilinear);
    }

    /// <summary>
    /// raylibは.ttc（フォントコレクション）を直接読めないので、先頭のフォントを.ttfとして取り出す。
    /// テーブルの位置はファイル先頭からの絶対位置なので、先頭のテーブル目次をファイル頭に書き写すだけでよい。
    /// </summary>
    private static string? ExtractFirstFont(string ttcPath)
    {
        try
        {
            if (!File.Exists(ttcPath)) return null;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JapanDrive3D");
            string outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(ttcPath) + ".ttf");
            if (File.Exists(outPath)) return outPath;

            byte[] data = File.ReadAllBytes(ttcPath);
            static int U32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
            if (data[0] != 't' || data[1] != 't' || data[2] != 'c' || data[3] != 'f') return null;
            int offset = U32(data, 12);
            int numTables = (data[offset + 4] << 8) | data[offset + 5];
            int dirSize = 12 + 16 * numTables;
            // 目次を書き写す先（0～dirSize）にテーブル本体が無いことを確認
            for (int i = 0; i < numTables; i++)
                if (U32(data, offset + 12 + 16 * i + 8) < dirSize) return null;
            Buffer.BlockCopy(data, offset, data, 0, dirSize);

            Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, data);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    public static void Text(string s, float x, float y, float size, Color c)
    {
        s = s.Replace("\u3000", "  ");
        Raylib.DrawTextEx(Font, s, new Vector2(MathF.Round(x), MathF.Round(y)), size * Ui, 0, c);
    }

    public static void TextShadow(string s, float x, float y, float size, Color c)
    {
        Text(s, x + 2, y + 2, size, new Color(0, 0, 0, 160));
        Text(s, x, y, size, c);
    }

    public static Vector2 Measure(string s, float size) => Raylib.MeasureTextEx(Font, s.Replace("\u3000", "  "), size * Ui, 0);

    public static void TextCentered(string s, float cx, float y, float size, Color c)
    {
        var m = Measure(s, size);
        Text(s, cx - m.X / 2, y, size, c);
    }

    public static void Panel(float x, float y, float w, float h, Color? color = null)
    {
        Raylib.DrawRectangleRounded(new Rectangle(x, y, w, h), 0.12f, 8, color ?? new Color(15, 20, 30, 190));
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    public static Color Rgb(int r, int g, int b, int a = 255) => new((byte)r, (byte)g, (byte)b, (byte)a);
}
