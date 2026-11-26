"""
Natural Earth（1:50m、パブリックドメイン）の地図データから、ロシア（シベリア・ヨーロッパロシア・北極海の島々）の
海岸線と湖を取り出して JapanDrive/RussiaGeo.cs（C# のデータ）を作る。

使い方:
  python -I tools/make-russia-geo.py <Natural Earth を展開したフォルダ>
  （フォルダの中に ne_50m_land/ ne_50m_lakes/ がある）

- ユーラシア大陸は大きいので、北緯41度より北・東経20度より東だけを切り抜く。
  沿海州・ハバロフスク・樺太・千島・カムチャツカ南部は今まで通り JapanOutline.cs の手描きの海岸線を使う。
- 日付変更線より東（チュクチ半島など）は、ゲームの連続経度に合わせて +360 度する。
- カスピ海のように陸地にあいた穴は、湖と同じように水として扱う。
"""
import importlib.util
import os
import sys

_spec = importlib.util.spec_from_file_location(
    'sea', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'make-sea-geo.py'))
sea = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(sea)

# 島を丸ごと使う範囲
LAT_MIN, LAT_MAX = 41.0, 82.5
LON_MIN, LON_MAX = 20.0, 191.0

# ユーラシア大陸を切り抜く四角（lat_min, lat_max, lon_min, lon_max）
CONTINENT_BOXES = [
    (54.0, 82.5, 20.0, 191.0),   # 北緯54度より北（シベリア・ヨーロッパロシア北部・チュクチ）
    (41.0, 54.0, 20.0, 127.0),   # 北緯41〜54度の西側（ヨーロッパロシア南部・カザフ・モンゴル・満洲西部）
]

LAKE_NAMES = {
    'Lake Baikal': 'バイカル湖',
    'Lake Ladoga': 'ラドガ湖',
    'Lake Onega': 'オネガ湖',
    'Rybinsk Reservoir': 'ルイビンスク貯水池',
    'Lake Taymyr': 'タイミル湖',
    'Lake Balkhash': 'バルハシ湖',
    'South Aral Sea': 'アラル海',
    'North Aral Sea': 'アラル海',
    'Issyk-Kul': 'イシク・クル湖',
    'Lake Zaysan': 'ザイサン湖',
    'Khövsgöl Nuur': 'フブスグル湖',
    'Bratsk Reservoir': 'ブラーツク貯水池',
    'Samara Reservoir': 'クイビシェフ貯水池',
    'Lake Peipus': 'チュド湖',
    'Lake Saimaa': 'サイマー湖',
    'Vilyuy Reservoir': 'ヴィリュイ貯水池',
}


def shift(ring):
    """日付変更線より東（西経）を +360 度して、ゲームの連続経度にする。"""
    if max(p[0] for p in ring) <= -150:
        return [(p[0] + 360, p[1]) for p in ring]
    return ring


def signed_area(ring):
    a = 0.0
    for i in range(len(ring)):
        x0, y0 = ring[i - 1]
        x1, y1 = ring[i]
        a += x0 * y1 - x1 * y0
    return a / 2


def is_japan_side(ring):
    """日本・樺太・千島（手描きの海岸線がある島）は使わない。"""
    x0, x1, y0, y1 = sea.bbox(ring)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return 139.0 <= cx <= 157.0 and cy < 54.5


def inside(ring):
    x0, x1, y0, y1 = sea.bbox(ring)
    return LON_MIN <= x0 and x1 <= LON_MAX and LAT_MIN <= y0 and y1 <= LAT_MAX


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    src = sys.argv[1]
    land_rings, holes = [], []
    for shape in sea.read_shp(os.path.join(src, 'ne_50m_land', 'ne_50m_land.shp')):
        for raw in shape:
            if len(raw) < 4:
                continue
            ring = shift(raw)
            x0, x1, y0, y1 = sea.bbox(ring)
            hole = signed_area(ring) > 0  # シェープファイルでは外周が時計回り、穴が反時計回り
            if x1 - x0 > 60:  # ユーラシア大陸
                for box in CONTINENT_BOXES:
                    if sea.overlaps(ring, box):
                        # 先に間引いてから切り抜く（切り抜きの辺の上の点を間引かないように）
                        part = sea.clip(sea.thin(ring, 0.01), *box)
                        if len(part) >= 3:
                            land_rings.append(part)
            elif inside(ring) and not is_japan_side(ring) and (x1 - x0) + (y1 - y0) >= 0.05:
                (holes if hole else land_rings).append(sea.thin(ring, 0.01))
            elif hole and sea.overlaps(ring, CONTINENT_BOXES[1]):
                part = sea.clip(sea.thin(ring, 0.01), *CONTINENT_BOXES[1])
                if len(part) >= 3:
                    holes.append(part)

    lakes = [('カスピ海', h) for h in holes]
    lake_shapes = sea.read_shp(os.path.join(src, 'ne_50m_lakes', 'ne_50m_lakes.shp'))
    lake_rows = sea.read_dbf(os.path.join(src, 'ne_50m_lakes', 'ne_50m_lakes.dbf'))
    for shape, row in zip(lake_shapes, lake_rows):
        name = LAKE_NAMES.get(row.get('name', ''))
        if not name:
            continue
        for ring in shape:
            if len(ring) >= 4 and inside(ring) and signed_area(ring) < 0:
                lakes.append((name, sea.thin(ring, 0.004)))

    out = [
        '// このファイルは tools/make-russia-geo.py が作ったものです（手で書き換えないでください）。',
        '// 地図データ: Natural Earth 1:50m（パブリックドメイン） https://www.naturalearthdata.com/',
        '// 座標は 緯度×10000, 経度×10000 の整数を交互に並べたもの（日付変更線より東は +360 度）。',
        'namespace JapanDrive;',
        '',
        'internal static class RussiaGeo',
        '{',
        '    /// <summary>ロシア（シベリア・ヨーロッパロシア・北極海の島々）と周りの大陸の陸地。</summary>',
        '    internal static readonly int[][] Land =',
        '    {',
    ]
    total = 0
    for ring in land_rings:
        total += len(ring)
        out.append('        new int[] {')
        sea.write_ints(out, sea.encode(ring), '            ')
        out.append('        },')
    out += [
        '    };',
        '',
        '    /// <summary>大陸を切り抜いた四角の辺と日付変更線（ここに沿った線は海岸ではないので地図に描かない）。</summary>',
        '    internal static readonly double[] CutLats = { ' + ', '.join(sorted({repr(v) for b in CONTINENT_BOXES for v in b[:2]})) + ' };',
        '    internal static readonly double[] CutLons = { ' + ', '.join(sorted({repr(v) for b in CONTINENT_BOXES for v in b[2:]} | {repr(180.0)})) + ' };',
        '',
        '    /// <summary>ロシアと周りの主な湖（カスピ海を含む）。</summary>',
        '    internal static readonly (string Name, int[] Points)[] Lakes =',
        '    {',
    ]
    for name, ring in lakes:
        out.append(f'        ("{name}", new int[] {{')
        sea.write_ints(out, sea.encode(ring), '            ')
        out.append('        }),')
    out += ['    };', '}', '']

    dst = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'JapanDrive', 'RussiaGeo.cs')
    with open(dst, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('\n'.join(out))
    print(f'land rings: {len(land_rings)}, points: {total}, holes: {len(holes)}, lakes: {sorted({n for n, _ in lakes})}')


if __name__ == '__main__':
    main()
