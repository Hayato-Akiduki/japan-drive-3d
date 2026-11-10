using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// ヘリコプター・飛行機（プロペラ機）・戦闘機のモデル。ローカル座標：+Z が機首、+Y が上。
/// </summary>
public static class Aircraft
{
    private static Scene scene = null!;
    private static float dist;

    private static Color C(Color c, bool lit = false) => scene.FogC(c, dist, lit);

    private static void Box(float x, float y, float z, float w, float h, float d, Color c) =>
        Raylib.DrawCube(new Vector3(x, y, z), w, h, d, C(c));

    private static void Ellipsoid(float x, float y, float z, float sx, float sy, float sz, Color c)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x, y, z);
        Rlgl.Scalef(sx, sy, sz);
        Raylib.DrawSphereEx(Vector3.Zero, 1, 10, 14, C(c));
        Rlgl.PopMatrix();
    }

    private static readonly Color Glass = Rgb(60, 90, 120);
    private static readonly Color Dark = Rgb(35, 35, 40);
    private static readonly Color GunMetal = Rgb(55, 58, 65);
    private static readonly Color White = Rgb(240, 240, 240);

    public static float Length(VehicleKind k) => k switch
    {
        VehicleKind.Airplane => 7.5f,
        VehicleKind.Fighter => 11.2f,
        _ => 9f,
    };

    /// <param name="rotor">ローター・プロペラの回転角（度）</param>
    /// <param name="gearDown">脚を出しているか（飛行機・戦闘機）</param>
    public static void Draw(Scene sc, VehicleKind kind, Color body, float distance, float rotor, bool gearDown)
    {
        scene = sc;
        dist = distance;
        if (kind == VehicleKind.Helicopter) Helicopter(body, rotor);
        else if (kind == VehicleKind.Fighter) Fighter(body, rotor, gearDown);
        else Airplane(body, rotor, gearDown);
    }

    private static void Helicopter(Color body, float rotor)
    {
        Ellipsoid(0, 1.6f, 0.2f, 0.9f, 0.9f, 1.7f, body);                 // 胴体
        Ellipsoid(0, 1.85f, 1.05f, 0.75f, 0.65f, 0.9f, Glass);             // 風防
        Box(0, 1.15f, 0.1f, 1.5f, 0.12f, 2.2f, White);                     // 白いライン
        Raylib.DrawCylinderEx(new Vector3(0, 1.85f, -1.0f), new Vector3(0, 2.15f, -5.6f), 0.38f, 0.14f, 10, C(body));
        Box(0, 2.65f, -5.45f, 0.1f, 1.1f, 0.75f, body);                    // 垂直尾翼
        Box(0, 2.1f, -4.9f, 1.4f, 0.08f, 0.45f, body);                     // 水平尾翼
        // 左右のスタブウィングと重機関銃ポッド
        Box(0, 1.35f, 0.45f, 2.3f, 0.10f, 0.45f, body);
        foreach (float sx in new[] { -1.05f, 1.05f })
        {
            Box(sx, 1.18f, 0.55f, 0.28f, 0.24f, 0.95f, GunMetal);          // 機関銃ポッド本体
            Raylib.DrawCylinderEx(new Vector3(sx, 1.18f, 1.0f), new Vector3(sx, 1.18f, 2.25f), 0.06f, 0.05f, 8, C(Dark)); // 銃身
            Raylib.DrawCylinderEx(new Vector3(sx, 1.18f, 2.1f), new Vector3(sx, 1.18f, 2.3f), 0.08f, 0.08f, 8, C(GunMetal)); // マズル
        }
        // テールローター
        Rlgl.PushMatrix();
        Rlgl.Translatef(0.2f, 2.35f, -5.55f);
        Rlgl.Rotatef(rotor * 2.5f, 1, 0, 0);
        Raylib.DrawCube(Vector3.Zero, 0.04f, 1.4f, 0.14f, C(Dark));
        Raylib.DrawCube(Vector3.Zero, 0.04f, 0.14f, 1.4f, C(Dark));
        Rlgl.PopMatrix();
        // メインローター
        Raylib.DrawCylinder(new Vector3(0, 2.4f, 0.2f), 0.15f, 0.25f, 0.45f, 8, C(Dark));
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 2.9f, 0.2f);
        Rlgl.Rotatef(rotor, 0, 1, 0);
        Raylib.DrawCube(Vector3.Zero, 10f, 0.06f, 0.36f, C(Dark));
        Raylib.DrawCube(Vector3.Zero, 0.36f, 0.06f, 10f, C(Dark));
        Rlgl.PopMatrix();
        // スキッド（着陸そり）
        foreach (float sx in new[] { -1f, 1f })
        {
            Box(sx * 0.85f, 0.1f, 0.2f, 0.1f, 0.1f, 3.6f, Dark);
            Box(sx * 0.78f, 0.55f, 1.0f, 0.08f, 0.85f, 0.08f, Dark);
            Box(sx * 0.78f, 0.55f, -0.7f, 0.08f, 0.85f, 0.08f, Dark);
        }
        Box(0, 1.55f, 1.95f, 0.3f, 0.1f, 0.1f, Rgb(255, 60, 40));            // 衝突防止灯
    }

    private static void Airplane(Color body, float rotor, bool gearDown)
    {
        Raylib.DrawCylinderEx(new Vector3(0, 1.5f, -4.2f), new Vector3(0, 1.5f, 2.6f), 0.32f, 0.65f, 14, C(body));
        Raylib.DrawCylinderEx(new Vector3(0, 1.5f, 2.6f), new Vector3(0, 1.5f, 3.25f), 0.65f, 0.3f, 14, C(White));
        Ellipsoid(0, 2.05f, 0.7f, 0.45f, 0.38f, 1.1f, Glass);              // キャノピー
        Box(0, 1.3f, 0.6f, 10.6f, 0.14f, 1.6f, body);                      // 主翼
        Box(4.7f, 1.31f, 0.6f, 1.2f, 0.15f, 1.62f, White);                 // 翼端の白
        Box(-4.7f, 1.31f, 0.6f, 1.2f, 0.15f, 1.62f, White);
        Box(5.3f, 1.3f, 0.7f, 0.12f, 0.12f, 0.2f, Rgb(40, 220, 60));       // 航法灯（右緑・左赤）
        Box(-5.3f, 1.3f, 0.7f, 0.12f, 0.12f, 0.2f, Rgb(240, 40, 40));
        Box(0, 1.6f, -3.9f, 3.8f, 0.1f, 0.95f, body);                      // 水平尾翼
        Box(0, 2.35f, -3.95f, 0.12f, 1.5f, 1.05f, body);                   // 垂直尾翼
        Box(0, 2.75f, -4.0f, 0.13f, 0.5f, 0.8f, White);
        Box(0, 1.5f, -0.6f, 1.32f, 0.22f, 4.8f, White);                    // 胴体の白いライン
        // プロペラ
        Raylib.DrawSphereEx(new Vector3(0, 1.5f, 3.35f), 0.22f, 8, 8, C(White));
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 1.5f, 3.4f);
        Rlgl.Rotatef(rotor * 3, 0, 0, 1);
        Raylib.DrawCube(Vector3.Zero, 0.16f, 2.5f, 0.05f, C(Dark));
        Rlgl.PopMatrix();
        if (gearDown)
        {
            foreach (float sx in new[] { -1.3f, 1.3f })
            {
                Box(sx, 0.75f, 1.1f, 0.08f, 0.95f, 0.08f, Dark);
                Raylib.DrawCylinderEx(new Vector3(sx - 0.1f, 0.3f, 1.1f), new Vector3(sx + 0.1f, 0.3f, 1.1f), 0.3f, 0.3f, 12, C(Dark));
            }
            Box(0, 0.9f, -3.6f, 0.06f, 0.8f, 0.06f, Dark);
            Raylib.DrawCylinderEx(new Vector3(-0.06f, 0.5f, -3.6f), new Vector3(0.06f, 0.5f, -3.6f), 0.15f, 0.15f, 10, C(Dark));
        }
    }

    /// <summary>ジェット戦闘機（双発・後退翼・双垂直尾翼・翼下爆弾・アフターバーナー）。</summary>
    private static void Fighter(Color body, float rotor, bool gearDown)
    {
        var noseCol = Rgb(55, 60, 68);
        var wingTip = Rgb(220, 225, 230);
        var red = Rgb(215, 35, 40);

        // 胴体と鋭いレドーム（機首）
        Raylib.DrawCylinderEx(new Vector3(0, 1.45f, -4.6f), new Vector3(0, 1.45f, 2.4f), 0.58f, 0.52f, 12, C(body));
        Raylib.DrawCylinderEx(new Vector3(0, 1.45f, 2.4f), new Vector3(0, 1.4f, 4.3f), 0.52f, 0.26f, 12, C(body));
        Raylib.DrawCylinderEx(new Vector3(0, 1.4f, 4.3f), new Vector3(0, 1.35f, 5.8f), 0.26f, 0.02f, 12, C(noseCol));

        // 左右のエアインテーク＆エンジンナセル
        foreach (float sx in new[] { -0.72f, 0.72f })
        {
            Box(sx, 1.38f, -1.1f, 0.58f, 0.56f, 5.4f, body);
            Box(sx, 1.38f, 1.62f, 0.48f, 0.46f, 0.1f, Dark); // インテーク開口部
        }

        // バブルキャノピー
        Ellipsoid(0, 1.92f, 1.75f, 0.44f, 0.38f, 1.55f, Glass);

        // デルタ／後退主翼（三角形メッシュで後退角をつける）
        var cBody = C(body);
        var cTip = C(wingTip);
        Rlgl.Begin(DrawMode.Triangles);
        foreach (float sx in new[] { -1f, 1f })
        {
            Vector3 rFront = new(sx * 0.9f, 1.42f, 1.1f);
            Vector3 rRear = new(sx * 0.9f, 1.42f, -2.8f);
            Vector3 tFront = new(sx * 5.2f, 1.38f, -1.5f);
            Vector3 tRear = new(sx * 5.2f, 1.38f, -2.9f);
            Rlgl.Color4ub(cBody.R, cBody.G, cBody.B, 255);
            Rlgl.Vertex3f(rFront.X, rFront.Y, rFront.Z);
            Rlgl.Vertex3f(tFront.X, tFront.Y, tFront.Z);
            Rlgl.Vertex3f(rRear.X, rRear.Y, rRear.Z);

            Rlgl.Vertex3f(rRear.X, rRear.Y, rRear.Z);
            Rlgl.Vertex3f(tFront.X, tFront.Y, tFront.Z);
            Rlgl.Vertex3f(tRear.X, tRear.Y, tRear.Z);
        }
        Rlgl.End();
        // 主翼の厚みと日の丸マーク・翼端ミサイルレール
        Box(0, 1.40f, -1.2f, 8.2f, 0.09f, 1.6f, body);
        foreach (float sx in new[] { -1f, 1f })
        {
            Raylib.DrawCylinder(new Vector3(sx * 3.1f, 1.46f, -1.6f), 0.45f, 0.45f, 0.03f, 12, C(White));
            Raylib.DrawCylinder(new Vector3(sx * 3.1f, 1.48f, -1.6f), 0.32f, 0.32f, 0.03f, 12, C(red));
            Box(sx * 5.22f, 1.38f, -2.1f, 0.12f, 0.14f, 1.9f, wingTip);
            // 翼下パイロンと懸架された爆弾
            Box(sx * 2.1f, 1.18f, -1.1f, 0.10f, 0.32f, 0.8f, GunMetal);
            DrawBombModel(new Vector3(sx * 2.1f, 0.92f, -1.0f), 0.75f);
        }

        // 水平尾翼（全遊動式）
        Box(0, 1.38f, -4.15f, 4.4f, 0.08f, 1.25f, body);

        // 双垂直尾翼（外側に少し傾ける）
        foreach (float sx in new[] { -0.82f, 0.82f })
        {
            Rlgl.PushMatrix();
            Rlgl.Translatef(sx, 1.65f, -3.95f);
            Rlgl.Rotatef(-sx * 14f, 0, 0, 1);
            Box(0, 0.65f, 0, 0.11f, 1.55f, 1.45f, body);
            Box(0, 1.35f, -0.1f, 0.13f, 0.22f, 1.1f, red);
            Rlgl.PopMatrix();
        }

        // 双発ジェットノズルとアフターバーナーの炎
        float pulse = 0.85f + 0.25f * MathF.Sin(rotor * 0.25f);
        foreach (float sx in new[] { -0.48f, 0.48f })
        {
            Raylib.DrawCylinderEx(new Vector3(sx, 1.38f, -4.4f), new Vector3(sx, 1.38f, -5.05f), 0.34f, 0.28f, 12, C(Dark));
            Raylib.DrawCylinderEx(new Vector3(sx, 1.38f, -5.0f), new Vector3(sx, 1.38f, -5.0f - 1.5f * pulse),
                0.24f, 0.02f, 10, C(Rgb(255, 165, 50), lit: true));
            Raylib.DrawCylinderEx(new Vector3(sx, 1.38f, -5.0f), new Vector3(sx, 1.38f, -5.0f - 0.8f * pulse),
                0.16f, 0.04f, 8, C(Rgb(160, 220, 255), lit: true));
        }

        if (gearDown)
        {
            // 前脚と主脚
            Box(0, 0.75f, 2.6f, 0.08f, 1.0f, 0.08f, Dark);
            Raylib.DrawCylinderEx(new Vector3(-0.08f, 0.25f, 2.6f), new Vector3(0.08f, 0.25f, 2.6f), 0.22f, 0.22f, 10, C(Dark));
            foreach (float sx in new[] { -1.15f, 1.15f })
            {
                Box(sx, 0.75f, -0.9f, 0.10f, 1.0f, 0.10f, Dark);
                Raylib.DrawCylinderEx(new Vector3(sx - 0.1f, 0.28f, -0.9f), new Vector3(sx + 0.1f, 0.28f, -0.9f), 0.28f, 0.28f, 12, C(Dark));
            }
        }
    }

    /// <summary>航空爆弾の3Dモデル（翼下懸架・投下中の描画に共用）。</summary>
    public static void DrawBombModel(Vector3 pos, float scale = 1f, Scene? sc = null, float d = 0)
    {
        if (sc != null) { scene = sc; dist = d; }
        var bodyCol = C(Rgb(52, 62, 48));
        var bandCol = C(Rgb(230, 185, 40));
        var finCol = C(Dark);
        float r = 0.22f * scale, len = 1.1f * scale;
        Raylib.DrawCylinderEx(pos + new Vector3(0, 0, -len * 0.5f), pos + new Vector3(0, 0, len * 0.35f), r * 0.7f, r, 10, bodyCol);
        Raylib.DrawSphereEx(pos + new Vector3(0, 0, len * 0.35f), r, 8, 8, bodyCol);
        // 黄色い識別帯
        Raylib.DrawCylinderEx(pos + new Vector3(0, 0, len * 0.12f), pos + new Vector3(0, 0, len * 0.24f), r * 1.02f, r * 1.02f, 10, bandCol);
        // 安定尾翼（十字フィン）
        Raylib.DrawCube(pos + new Vector3(0, 0, -len * 0.45f), r * 3.0f, 0.04f * scale, len * 0.32f, finCol);
        Raylib.DrawCube(pos + new Vector3(0, 0, -len * 0.45f), 0.04f * scale, r * 3.0f, len * 0.32f, finCol);
    }
}
