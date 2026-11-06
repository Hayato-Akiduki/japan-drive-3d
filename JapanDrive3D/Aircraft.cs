using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// ヘリコプターと飛行機（プロペラ機）のモデル。ローカル座標：+Z が機首、+Y が上。
/// </summary>
public static class Aircraft
{
    private static Scene scene = null!;
    private static float dist;

    private static Color C(Color c) => scene.FogC(c, dist);

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
    private static readonly Color White = Rgb(240, 240, 240);

    public static float Length(VehicleKind k) => k == VehicleKind.Airplane ? 7.5f : 9f;

    /// <param name="rotor">ローター・プロペラの回転角（度）</param>
    /// <param name="gearDown">脚を出しているか（飛行機）</param>
    public static void Draw(Scene sc, VehicleKind kind, Color body, float distance, float rotor, bool gearDown)
    {
        scene = sc;
        dist = distance;
        if (kind == VehicleKind.Helicopter) Helicopter(body, rotor);
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
}
