namespace JapanDrive.Tests;

/// <summary>テスト用の地理計算（距離・陸地／湖の判定）。</summary>
internal static class Geo
{
    /// <summary>2点間の距離（km）。経度は連続経度（日付変更線より東は 180 超）のまま扱う。</summary>
    public static double Km((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        const double R = 6371;
        double la1 = a.Lat * Math.PI / 180, la2 = b.Lat * Math.PI / 180;
        double dLa = la2 - la1, dLo = (b.Lon - a.Lon) * Math.PI / 180;
        double h = Math.Sin(dLa / 2) * Math.Sin(dLa / 2) + Math.Cos(la1) * Math.Cos(la2) * Math.Sin(dLo / 2) * Math.Sin(dLo / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static double PolylineKm((double Lat, double Lon)[] pts)
    {
        double sum = 0;
        for (int i = 0; i + 1 < pts.Length; i++) sum += Km(pts[i], pts[i + 1]);
        return sum;
    }

    /// <summary>折れ線をおよそ stepKm ごとに区切った点。</summary>
    public static IEnumerable<(double Lat, double Lon)> Sample((double Lat, double Lon)[] pts, double stepKm)
    {
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Km(pts[i], pts[i + 1]) / stepKm));
            for (int k = 0; k < n; k++)
            {
                double t = (double)k / n;
                yield return (pts[i].Lat + (pts[i + 1].Lat - pts[i].Lat) * t, pts[i].Lon + (pts[i + 1].Lon - pts[i].Lon) * t);
            }
        }
        yield return pts[^1];
    }

    public static readonly Polygons Land = new(JapanOutline.Detailed);
    public static readonly Polygons Lakes = new(JapanOutline.AllLakes.Select(l => l.Points));

    /// <summary>周り radiusKm 以内に陸があるか（海岸線の粗さを許すための判定）。</summary>
    public static bool NearLand(double lat, double lon, double radiusKm)
    {
        if (Land.Contains(lat, lon)) return true;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8;
            double dLat = radiusKm / 111.0 * Math.Sin(a);
            double dLon = radiusKm / (111.0 * Math.Cos(lat * Math.PI / 180)) * Math.Cos(a);
            if (Land.Contains(lat + dLat, lon + dLon)) return true;
        }
        return false;
    }
}

/// <summary>多角形の集まり（どれか1つの内側なら内側）。外接四角形で先にふるい分けて速くする。</summary>
internal sealed class Polygons
{
    private readonly (double MinLat, double MaxLat, double MinLon, double MaxLon, (double Lat, double Lon)[] Ring)[] rings;

    public Polygons(IEnumerable<(double Lat, double Lon)[]> source) =>
        rings = source.Where(r => r.Length >= 3)
            .Select(r => (r.Min(p => p.Lat), r.Max(p => p.Lat), r.Min(p => p.Lon), r.Max(p => p.Lon), r))
            .ToArray();

    public bool Contains(double lat, double lon)
    {
        foreach (var (minLat, maxLat, minLon, maxLon, ring) in rings)
        {
            if (lat < minLat || lat > maxLat || lon < minLon || lon > maxLon) continue;
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Lat > lat) != (b.Lat > lat) && lon < a.Lon + (lat - a.Lat) * (b.Lon - a.Lon) / (b.Lat - a.Lat))
                    inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }
}
