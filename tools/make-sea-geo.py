"""
Natural Earth（1:50m、パブリックドメイン）の地図データから、東南アジアの海岸線と湖を取り出して
JapanDrive/SoutheastAsiaGeo.cs（C# のデータ）を作る。

使い方:
  python -I tools/make-sea-geo.py <Natural Earth を展開したフォルダ>
  （フォルダの中に ne_50m_land/ ne_50m_lakes/ がある）

ダウンロード元: https://www.naturalearthdata.com/  （naciscdn.org/naturalearth/50m/physical/*.zip）

- 東南アジアの島（フィリピン・ボルネオ・スマトラ・ジャワ・スラウェシ・ニューギニアなど）は丸ごと使う。
- ユーラシア大陸はとても大きいので、東南アジア側（インドシナ半島・マレー半島・中国南西部）だけを切り抜く。
  台湾・朝鮮半島・中国東海岸は今まで通り JapanOutline.cs の手描きの海岸線を使う。
"""
import os
import struct
import sys

# 島を丸ごと使う範囲（オーストラリアや台湾は入らない）
LAT_MIN, LAT_MAX = -11.5, 23.0
LON_MIN, LON_MAX = 92.0, 156.0

# ユーラシア大陸を切り抜く四角（lat_min, lat_max, lon_min, lon_max）
CONTINENT_BOXES = [
    (-12.0, 56.0, 92.0, 110.0),   # インドシナ・マレー半島・中国南西部〜モンゴル
    (-12.0, 22.6, 105.0, 113.3),  # トンキン湾・雷州半島〜マカオ（香港より西の海岸）
]

# Natural Earth の英語名 → ゲーム内の湖の名前（ここにない湖は使わない）
LAKE_NAMES = {
    'Tonlé Sap': 'トンレサップ湖',
    'Nam Ngum Reservoir': 'ナムグム湖',
    'Danau Toba': 'トバ湖',
}


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
    """形（各レコードのパーツ＝点の列のリスト）を返す。点は (経度, 緯度)。"""
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


def bbox(ring):
    xs = [p[0] for p in ring]
    ys = [p[1] for p in ring]
    return min(xs), max(xs), min(ys), max(ys)


def inside(ring):
    x0, x1, y0, y1 = bbox(ring)
    return LON_MIN <= x0 and x1 <= LON_MAX and LAT_MIN <= y0 and y1 <= LAT_MAX


def clip(ring, lat_min, lat_max, lon_min, lon_max):
    """Sutherland–Hodgman で四角に切り抜く。"""
    def clip_edge(pts, inside_fn, cross_fn):
        out = []
        for i in range(len(pts)):
            a, b = pts[i - 1], pts[i]
            ia, ib = inside_fn(a), inside_fn(b)
            if ib:
                if not ia:
                    out.append(cross_fn(a, b))
                out.append(b)
            elif ia:
                out.append(cross_fn(a, b))
        return out

    def at_x(x):
        return lambda a, b: (x, a[1] + (b[1] - a[1]) * (x - a[0]) / (b[0] - a[0]))

    def at_y(y):
        return lambda a, b: (a[0] + (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]), y)

    pts = ring
    for inside_fn, cross_fn in (
        (lambda p: p[0] >= lon_min, at_x(lon_min)),
        (lambda p: p[0] <= lon_max, at_x(lon_max)),
        (lambda p: p[1] >= lat_min, at_y(lat_min)),
        (lambda p: p[1] <= lat_max, at_y(lat_max)),
    ):
        if not pts:
            break
        pts = clip_edge(pts, inside_fn, cross_fn)
    return pts


def overlaps(ring, box):
    lat_min, lat_max, lon_min, lon_max = box
    x0, x1, y0, y1 = bbox(ring)
    return x1 > lon_min and x0 < lon_max and y1 > lat_min and y0 < lat_max


def thin(ring, min_step=0.004):
    """ほぼ同じ位置の点を間引く（約400m未満）。"""
    out = []
    for p in ring:
        if not out or abs(p[0] - out[-1][0]) + abs(p[1] - out[-1][1]) >= min_step:
            out.append(p)
    return out


def encode(ring):
    vals = []
    for lon, lat in ring:
        vals.append(round(lat * 10000))
        vals.append(round(lon * 10000))
    return vals


def write_ints(out, vals, indent):
    for i in range(0, len(vals), 14):
        out.append(indent + ', '.join(str(v) for v in vals[i:i + 14]) + ',')


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    src = sys.argv[1]
    land_rings = []
    for shape in read_shp(os.path.join(src, 'ne_50m_land', 'ne_50m_land.shp')):
        for ring in shape:
            if len(ring) < 4:
                continue
            x0, x1, y0, y1 = bbox(ring)
            if inside(ring):
                if (x1 - x0) + (y1 - y0) >= 0.03:
                    land_rings.append(thin(ring))
            elif x1 - x0 > 60:  # ユーラシア大陸
                for box in CONTINENT_BOXES:
                    if overlaps(ring, box):
                        # 先に間引いてから切り抜く（切り抜きの辺の上の点を間引かないように）
                        part = clip(thin(ring), *box)
                        if len(part) >= 3:
                            land_rings.append(part)

    lakes = []
    lake_shapes = read_shp(os.path.join(src, 'ne_50m_lakes', 'ne_50m_lakes.shp'))
    lake_rows = read_dbf(os.path.join(src, 'ne_50m_lakes', 'ne_50m_lakes.dbf'))
    for shape, row in zip(lake_shapes, lake_rows):
        name = LAKE_NAMES.get(row.get('name', ''))
        if not name:
            continue
        for ring in shape:
            if len(ring) >= 4 and inside(ring):
                lakes.append((name, thin(ring, 0.002)))

    out = [
        '// このファイルは tools/make-sea-geo.py が作ったものです（手で書き換えないでください）。',
        '// 地図データ: Natural Earth 1:50m（パブリックドメイン） https://www.naturalearthdata.com/',
        '// 座標は 緯度×10000, 経度×10000 の整数を交互に並べたもの。',
        'namespace JapanDrive;',
        '',
        'internal static class SoutheastAsiaGeo',
        '{',
        '    /// <summary>東南アジアの陸地（インドシナ・マレー半島・フィリピン・インドネシア・ボルネオなど）。</summary>',
        '    internal static readonly int[][] Land =',
        '    {',
    ]
    total = 0
    for ring in land_rings:
        vals = encode(ring)
        total += len(ring)
        out.append('        new int[] {')
        write_ints(out, vals, '            ')
        out.append('        },')
    out += [
        '    };',
        '',
        '    /// <summary>大陸を切り抜いた四角の辺（ここに沿った線は海岸ではないので地図に描かない）。</summary>',
        '    internal static readonly double[] CutLats = { ' + ', '.join(sorted({repr(v) for b in CONTINENT_BOXES for v in b[:2]})) + ' };',
        '    internal static readonly double[] CutLons = { ' + ', '.join(sorted({repr(v) for b in CONTINENT_BOXES for v in b[2:]})) + ' };',
        '',
        '    /// <summary>東南アジアの主な湖。</summary>',
        '    internal static readonly (string Name, int[] Points)[] Lakes =',
        '    {',
    ]
    for name, ring in lakes:
        out.append(f'        ("{name}", new int[] {{')
        write_ints(out, encode(ring), '            ')
        out.append('        }),')
    out += ['    };', '}', '']

    dst = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'JapanDrive', 'SoutheastAsiaGeo.cs')
    with open(dst, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('\n'.join(out))
    print(f'land rings: {len(land_rings)}, points: {total}, lakes: {[n for n, _ in lakes]}')


if __name__ == '__main__':
    main()
