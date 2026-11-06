"""
Natural Earth（1:10m、パブリックドメイン）の地図データから、日本の海岸線・湖・川を取り出して
JapanDrive/JapanGeo.cs（C# のデータ）を作る。

使い方:
  python -I tools/make-geo.py <Natural Earth を展開したフォルダ>
  （フォルダの中に ne_10m_land/ ne_10m_minor_islands/ ne_10m_lakes/ ne_10m_rivers_lake_centerlines/ がある）

ダウンロード元: https://www.naturalearthdata.com/  （naciscdn.org/naturalearth/10m/physical/*.zip）
"""
import math
import os
import struct
import sys

# 日本のまわり（この範囲に丸ごと入る島だけを使う：朝鮮半島・ロシア・台湾は入らない）
LON_MIN, LON_MAX = 122.5, 149.0
LAT_MIN, LAT_MAX = 24.0, 45.6


def read_dbf(path):
    with open(path, 'rb') as f:
        data = f.read()
    n = struct.unpack('<I', data[4:8])[0]
    header_len, rec_len = struct.unpack('<HH', data[8:12])
    fields = []
    pos = 32
    while data[pos] != 0x0D:
        name = data[pos:pos + 11].split(b'\0')[0].decode('ascii')
        length = data[pos + 16]
        fields.append((name, length))
        pos += 32
    rows = []
    for i in range(n):
        off = header_len + i * rec_len + 1
        row = {}
        for name, length in fields:
            row[name] = data[off:off + length].decode('utf-8', 'replace').strip()
            off += length
        rows.append(row)
    return rows


def read_shp(path):
    """形（各レコードのパーツ＝点の列のリスト）を返す。"""
    with open(path, 'rb') as f:
        data = f.read()
    pos = 100
    shapes = []
    while pos < len(data):
        _, length = struct.unpack('>II', data[pos:pos + 8])
        content = data[pos + 8:pos + 8 + length * 2]
        pos += 8 + length * 2
        stype = struct.unpack('<i', content[:4])[0]
        if stype not in (3, 5):
            shapes.append([])
            continue
        num_parts, num_points = struct.unpack('<ii', content[36:44])
        parts = list(struct.unpack('<%di' % num_parts, content[44:44 + 4 * num_parts]))
        pts_off = 44 + 4 * num_parts
        pts = [struct.unpack('<dd', content[pts_off + 16 * k:pts_off + 16 * k + 16]) for k in range(num_points)]
        parts.append(num_points)
        shapes.append([pts[parts[i]:parts[i + 1]] for i in range(num_parts)])
    return shapes


def inside_japan(ring):
    xs = [p[0] for p in ring]
    ys = [p[1] for p in ring]
    if min(xs) < LON_MIN or max(xs) > LON_MAX or min(ys) < LAT_MIN or max(ys) > LAT_MAX:
        return False
    cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
    # 韓国の島（済州島・釜山周辺・鬱陵島）は除く
    if cx < 128.5 and cy > 31.5:
        return False
    if 34.8 < cy < 38.6 and cx < 130.0:
        return False
    if 37.3 < cy < 37.7 and 130.6 < cx < 131.1:
        return False
    # ロシア・中国の大陸側（沿海地方など）は除く
    if cx < 139.0 and cy > 38.7:
        return False
    return True


# Natural Earth に入っていない主な川と湖（おおよその位置と形）
EXTRA_RIVERS = [
    ('信濃川', 3, [(35.95, 138.62), (36.2, 138.35), (36.65, 138.2), (36.95, 138.4), (37.15, 138.75), (37.45, 138.85), (37.75, 138.95), (37.93, 139.05)]),
    ('木曽川', 4, [(35.9, 137.65), (35.6, 137.6), (35.45, 137.3), (35.4, 137.0), (35.3, 136.75), (35.05, 136.72)]),
    ('天竜川', 4, [(36.05, 138.07), (35.7, 137.95), (35.3, 137.85), (35.0, 137.8), (34.66, 137.8)]),
    ('北上川', 4, [(39.95, 141.15), (39.7, 141.15), (39.3, 141.1), (38.9, 141.15), (38.6, 141.25), (38.43, 141.3)]),
    ('阿武隈川', 5, [(37.1, 140.2), (37.4, 140.4), (37.75, 140.5), (38.0, 140.7), (38.17, 140.92)]),
    ('淀川', 5, [(34.97, 135.9), (34.9, 135.75), (34.8, 135.6), (34.68, 135.43)]),
    ('筑後川', 5, [(33.3, 131.0), (33.35, 130.7), (33.3, 130.5), (33.15, 130.35)]),
    ('吉野川', 5, [(33.85, 133.6), (34.0, 133.8), (34.05, 134.1), (34.08, 134.58)]),
    ('四万十川', 6, [(33.3, 133.0), (33.1, 132.85), (32.94, 132.97)]),
    ('球磨川', 6, [(32.2, 130.9), (32.25, 130.7), (32.5, 130.6)]),
    ('十勝川', 5, [(43.2, 142.9), (42.95, 143.2), (42.75, 143.55), (42.68, 143.65)]),
    ('荒川', 6, [(36.0, 138.9), (36.1, 139.25), (35.9, 139.55), (35.65, 139.85)]),
]
# (名前, 緯度, 経度, 東西の半径 km, 南北の半径 km)
EXTRA_LAKES = [
    ('霞ヶ浦', 36.05, 140.35, 7, 10), ('猪苗代湖', 37.48, 140.10, 6.5, 6.5), ('浜名湖', 34.75, 137.60, 4, 6),
    ('宍道湖', 35.45, 132.95, 8, 3), ('十和田湖', 40.46, 140.88, 4.5, 4.5), ('支笏湖', 42.76, 141.33, 6, 4),
    ('洞爺湖', 42.60, 140.85, 4.5, 4.5), ('屈斜路湖', 43.62, 144.33, 6, 4), ('諏訪湖', 36.05, 138.08, 2, 2),
    ('田沢湖', 39.72, 140.66, 3, 3), ('中禅寺湖', 36.74, 139.47, 3, 2), ('阿寒湖', 43.45, 144.10, 2.5, 2.5),
    ('河口湖', 35.51, 138.76, 2, 1.2), ('八郎潟', 40.00, 140.02, 4, 4), ('サロマ湖', 44.15, 143.85, 10, 3),
]


def ellipse(lat, lon, a_km, b_km, n=28):
    pts = []
    for i in range(n):
        t = 2 * math.pi * i / n
        dlat = b_km / 111.0 * math.sin(t)
        dlon = a_km / (111.0 * math.cos(math.radians(lat))) * math.cos(t)
        pts.append((lon + dlon, lat + dlat))
    return pts


def area(ring):
    a = 0
    for i in range(len(ring)):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % len(ring)]
        a += x1 * y2 - x2 * y1
    return abs(a) / 2 * math.cos(math.radians(ring[0][1]))


def simplify(pts, tol):
    """ダグラス・ポイカー法で点を減らす（経度は緯度に合わせて縮める）。"""
    if len(pts) < 3:
        return pts
    k = math.cos(math.radians(pts[0][1]))
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        a, b = stack.pop()
        ax, ay = pts[a][0] * k, pts[a][1]
        bx, by = pts[b][0] * k, pts[b][1]
        dx, dy = bx - ax, by - ay
        l2 = dx * dx + dy * dy
        best, bi = 0, -1
        for i in range(a + 1, b):
            px, py = pts[i][0] * k, pts[i][1]
            if l2 == 0:
                d = math.hypot(px - ax, py - ay)
            else:
                t = max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / l2))
                d = math.hypot(px - ax - t * dx, py - ay - t * dy)
            if d > best:
                best, bi = d, i
        if best > tol and bi > 0:
            keep[bi] = True
            stack.append((a, bi))
            stack.append((bi, b))
    return [p for p, kp in zip(pts, keep) if kp]


def ring_simplify(ring, tol):
    # 閉じた輪は、いちばん離れた2点で分けてから減らす
    if ring[0] == ring[-1]:
        ring = ring[:-1]
    if len(ring) < 4:
        return ring
    far = max(range(len(ring)), key=lambda i: (ring[i][0] - ring[0][0]) ** 2 + (ring[i][1] - ring[0][1]) ** 2)
    a = simplify(ring[:far + 1], tol)
    b = simplify(ring[far:] + [ring[0]], tol)
    return a[:-1] + b[:-1]


def ints(pts):
    # 緯度・経度を 1万倍した整数で、緯度, 経度, 緯度, 経度… の順
    out = []
    for lon, lat in pts:
        out.append(str(round(lat * 10000)))
        out.append(str(round(lon * 10000)))
    return ', '.join(out)


def main():
    src = sys.argv[1]
    out_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'JapanDrive', 'JapanGeo.cs')

    land_rings = []
    for name in ('ne_10m_land', 'ne_10m_minor_islands'):
        for shape in read_shp(os.path.join(src, name, name + '.shp')):
            for ring in shape:
                if inside_japan(ring) and area(ring) > 0.00008:
                    land_rings.append(ring)
    land = [r for r in (ring_simplify(r, 0.0035) for r in land_rings) if len(r) >= 3]
    outline = [r for r in (ring_simplify(r, 0.02) for r in land_rings if area(r) > 0.004) if len(r) >= 3]

    lakes = []
    rows = read_dbf(os.path.join(src, 'ne_10m_lakes', 'ne_10m_lakes.dbf'))
    for shape, row in zip(read_shp(os.path.join(src, 'ne_10m_lakes', 'ne_10m_lakes.shp')), rows):
        for ring in shape:
            if inside_japan(ring) and area(ring) > 0.00002:
                r = ring_simplify(ring, 0.002)
                if len(r) >= 3:
                    lakes.append((row.get('name', ''), r))

    for name, lat, lon, a, b in EXTRA_LAKES:
        lakes.append((name, ellipse(lat, lon, a, b)))

    rivers = []
    rows = read_dbf(os.path.join(src, 'ne_10m_rivers_lake_centerlines', 'ne_10m_rivers_lake_centerlines.dbf'))
    for shape, row in zip(read_shp(os.path.join(src, 'ne_10m_rivers_lake_centerlines', 'ne_10m_rivers_lake_centerlines.shp')), rows):
        for line in shape:
            if len(line) >= 2 and inside_japan(line):
                r = simplify(line, 0.002)
                rank = int(float(row.get('scalerank') or 10))
                rivers.append((row.get('name', '').strip(), rank, r))
    for name, rank, line in EXTRA_RIVERS:
        rivers.append((name, rank, [(lon, lat) for lat, lon in line]))

    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('// このファイルは tools/make-geo.py が作ったものです（手で書き換えないでください）。\n')
        f.write('// 地図データ: Natural Earth 1:10m（パブリックドメイン） https://www.naturalearthdata.com/\n')
        f.write('// 座標は 緯度×10000, 経度×10000 の整数を交互に並べたもの。\n\n')
        f.write('namespace JapanDrive;\n\npublic static class JapanGeo\n{\n')
        f.write('    /// <summary>陸地（細かい海岸線）。</summary>\n    public static readonly int[][] Land =\n    {\n')
        for r in land:
            f.write('        new[] { %s },\n' % ints(r))
        f.write('    };\n\n')
        f.write('    /// <summary>地図表示用の粗い海岸線（大きな島だけ）。</summary>\n    public static readonly int[][] Outline =\n    {\n')
        for r in outline:
            f.write('        new[] { %s },\n' % ints(r))
        f.write('    };\n\n')
        f.write('    /// <summary>湖。</summary>\n    public static readonly (string Name, int[] Points)[] Lakes =\n    {\n')
        for name, r in lakes:
            f.write('        ("%s", new[] { %s }),\n' % (name.replace('"', ''), ints(r)))
        f.write('    };\n\n')
        f.write('    /// <summary>川（Rank が小さいほど大きな川）。</summary>\n    public static readonly (string Name, int Rank, int[] Points)[] Rivers =\n    {\n')
        for name, rank, r in rivers:
            f.write('        ("%s", %d, new[] { %s }),\n' % (name.replace('"', ''), rank, ints(r)))
        f.write('    };\n}\n')

    pts = sum(len(r) for r in land)
    print(f'land: {len(land)} rings / {pts} points, outline: {len(outline)} rings / {sum(len(r) for r in outline)} points')
    print(f'lakes: {len(lakes)} ({", ".join(sorted(set(n for n, _ in lakes if n)))})')
    print(f'rivers: {len(rivers)} ({", ".join(sorted(set(n for n, _, _ in rivers if n))[:40])})')
    print(f'wrote {out_path} ({os.path.getsize(out_path) // 1024} KB)')


if __name__ == '__main__':
    main()
