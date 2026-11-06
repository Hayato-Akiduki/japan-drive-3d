namespace JapanDrive;

/// <summary>
/// 日本の島の海岸線（緯度, 経度）。地図（ミニマップ・全体地図・2D版）に使う。
/// 元データは Natural Earth（JapanGeo.cs、tools/make-geo.py で作成）。
/// </summary>
public static class JapanOutline
{
    /// <summary>地図表示用の粗い海岸線（大きな島だけ）。</summary>
    public static readonly (double Lat, double Lon)[][] Islands = JapanGeo.Outline.Select(Decode).ToArray();

    /// <summary>細かい海岸線（小さな島も含む）。</summary>
    public static readonly (double Lat, double Lon)[][] Detailed = JapanGeo.Land.Select(Decode).ToArray();

    /// <summary>「緯度×10000, 経度×10000」を交互に並べた整数を、緯度・経度の組に戻す。</summary>
    public static (double Lat, double Lon)[] Decode(int[] a)
    {
        var pts = new (double, double)[a.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = (a[i * 2] / 10000.0, a[i * 2 + 1] / 10000.0);
        return pts;
    }
}
