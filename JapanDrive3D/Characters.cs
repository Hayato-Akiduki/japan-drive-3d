using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

/// <summary>
/// 馬（武士が乗る）と飛脚。走る足の動きをアニメーションする。
/// ローカル座標：+Z が前、+Y が上。
/// </summary>
public static class Characters
{
    private static Scene scene = null!;
    private static float dist;

    private static Color C(Color c) => scene.FogC(c, dist);

    private static void Box(float x, float y, float z, float w, float h, float d, Color c) =>
        Raylib.DrawCube(new Vector3(x, y, z), w, h, d, C(c));

    /// <summary>関節（x, y, z）を中心に X 軸まわりに angle 度回した棒を下向きに描く。</summary>
    private static void Limb(float x, float y, float z, float angle, float len, float thick, Color c, Color? tip = null)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x, y, z);
        Rlgl.Rotatef(angle, 1, 0, 0);
        Raylib.DrawCube(new Vector3(0, -len / 2, 0), thick, len, thick, C(c));
        if (tip is Color t) Raylib.DrawCube(new Vector3(0, -len + 0.06f, 0.03f), thick * 1.15f, 0.12f, thick * 1.4f, C(t));
        Rlgl.PopMatrix();
    }

    private static readonly Color Skin = Rgb(225, 180, 140);
    private static readonly Color Black = Rgb(30, 28, 28);

    public static float Length(VehicleKind k) => k == VehicleKind.Horse ? 2.6f : 0.8f;

    public static void Draw(Scene sc, VehicleKind kind, Color body, float distance, float phase, bool shadow)
    {
        scene = sc;
        dist = distance;
        if (shadow)
        {
            float hw = kind == VehicleKind.Horse ? 0.45f : 0.3f, hl = kind == VehicleKind.Horse ? 1.3f : 0.35f;
            Rlgl.Begin(DrawMode.Quads);
            Rlgl.Color4ub(0, 0, 0, (byte)(scene.Night ? 45 : 85));
            Rlgl.Vertex3f(hw, 0.035f, -hl);
            Rlgl.Vertex3f(-hw, 0.035f, -hl);
            Rlgl.Vertex3f(-hw, 0.035f, hl);
            Rlgl.Vertex3f(hw, 0.035f, hl);
            Rlgl.End();
        }
        if (kind == VehicleKind.Horse) Horse(body, phase);
        else Hikyaku(phase);
    }

    private static void Horse(Color coat, float phase)
    {
        float bob = MathF.Abs(MathF.Sin(phase)) * 0.1f;
        var dark = Mul(coat, 0.45f);

        // 脚（ギャロップ：左右と前後で少しずつずらす）
        float[] offs = { 0, 0.5f, MathF.PI, MathF.PI + 0.5f };
        (float x, float z)[] hips = { (0.22f, 0.72f), (-0.22f, 0.72f), (0.22f, -0.72f), (-0.22f, -0.72f) };
        for (int i = 0; i < 4; i++)
        {
            float a = MathF.Sin(phase + offs[i]) * 38;
            Limb(hips[i].x, 1.05f + bob, hips[i].z, a, 1.0f, 0.15f, coat, Black);
        }

        // 胴体（楕円体）
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 1.35f + bob, 0);
        Rlgl.Scalef(0.42f, 0.45f, 1.05f);
        Raylib.DrawSphereEx(Vector3.Zero, 1, 10, 12, C(coat));
        Rlgl.PopMatrix();

        // 首・頭・たてがみ
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 1.6f + bob, 0.85f);
        Rlgl.Rotatef(38 + MathF.Sin(phase * 2) * 4, 1, 0, 0);
        Raylib.DrawCube(new Vector3(0, 0.42f, 0), 0.3f, 0.95f, 0.42f, C(coat));
        Raylib.DrawCube(new Vector3(0, 0.45f, -0.22f), 0.1f, 0.9f, 0.08f, C(dark));
        Rlgl.Translatef(0, 0.88f, 0.05f);
        Rlgl.Rotatef(70, 1, 0, 0);
        Raylib.DrawCube(new Vector3(0, 0.05f, 0.25f), 0.26f, 0.3f, 0.7f, C(coat));
        Raylib.DrawCube(new Vector3(0, 0.0f, 0.6f), 0.22f, 0.24f, 0.08f, C(dark));        // 鼻先
        Raylib.DrawCube(new Vector3(0.09f, 0.22f, -0.02f), 0.06f, 0.18f, 0.06f, C(coat)); // 耳
        Raylib.DrawCube(new Vector3(-0.09f, 0.22f, -0.02f), 0.06f, 0.18f, 0.06f, C(coat));
        Raylib.DrawCube(new Vector3(0.14f, 0.12f, 0.2f), 0.02f, 0.06f, 0.06f, C(Black));  // 目
        Raylib.DrawCube(new Vector3(-0.14f, 0.12f, 0.2f), 0.02f, 0.06f, 0.06f, C(Black));
        Rlgl.PopMatrix();

        // しっぽ
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 1.55f + bob, -1.02f);
        Rlgl.Rotatef(-35 + MathF.Sin(phase) * 12, 1, 0, 0);
        Raylib.DrawCube(new Vector3(0, -0.4f, 0), 0.14f, 0.85f, 0.14f, C(dark));
        Rlgl.PopMatrix();

        // 鞍と乗り手（陣笠の侍）
        float ry = 1.82f + bob;
        Box(0, ry, -0.05f, 0.62f, 0.1f, 0.65f, Rgb(160, 40, 35));
        var kimono = Rgb(45, 70, 140);
        Box(0.3f, ry - 0.2f, 0.05f, 0.14f, 0.5f, 0.2f, kimono);        // 足
        Box(-0.3f, ry - 0.2f, 0.05f, 0.14f, 0.5f, 0.2f, kimono);
        Box(0, ry + 0.38f, -0.05f, 0.44f, 0.62f, 0.3f, kimono);        // 胴
        Box(0, ry + 0.12f, -0.05f, 0.46f, 0.1f, 0.32f, Rgb(200, 170, 60)); // 帯
        Limb(0.26f, ry + 0.62f, 0, -60, 0.5f, 0.11f, kimono, Skin);    // 手綱を持つ腕
        Limb(-0.26f, ry + 0.62f, 0, -60, 0.5f, 0.11f, kimono, Skin);
        Raylib.DrawSphereEx(new Vector3(0, ry + 0.86f, -0.03f), 0.15f, 8, 8, C(Skin));
        Raylib.DrawCylinder(new Vector3(0, ry + 0.95f, -0.03f), 0.02f, 0.36f, 0.14f, 12, C(Rgb(35, 30, 30)));
        // 背中の旗指物
        Box(0, ry + 1.0f, -0.25f, 0.03f, 1.2f, 0.03f, Rgb(90, 70, 50));
        Box(-0.18f, ry + 1.35f, -0.25f, 0.34f, 0.55f, 0.02f, Rgb(220, 40, 40));
    }

    private static void Hikyaku(float phase)
    {
        float bob = MathF.Abs(MathF.Sin(phase)) * 0.07f;
        float swing = MathF.Sin(phase) * 42;
        var cloth = Rgb(40, 60, 130);

        // 脚（すね当てつき）と足
        Limb(0.11f, 0.92f + bob, 0, swing, 0.9f, 0.12f, Skin, Rgb(170, 140, 90));
        Limb(-0.11f, 0.92f + bob, 0, -swing, 0.9f, 0.12f, Skin, Rgb(170, 140, 90));

        // 上半身は前かがみ
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, 0.92f + bob, 0);
        Rlgl.Rotatef(14, 1, 0, 0);
        Raylib.DrawCube(new Vector3(0, 0.06f, 0), 0.32f, 0.16f, 0.2f, C(Rgb(240, 240, 235)));   // ふんどし
        Raylib.DrawCube(new Vector3(0, 0.36f, 0), 0.34f, 0.5f, 0.2f, C(Skin));                   // 胴
        Raylib.DrawCube(new Vector3(0, 0.38f, 0.06f), 0.3f, 0.36f, 0.12f, C(cloth));            // 腹掛け
        Raylib.DrawSphereEx(new Vector3(0, 0.78f, 0.03f), 0.12f, 8, 8, C(Skin));                // 頭
        Raylib.DrawCylinder(new Vector3(0, 0.79f, 0.03f), 0.125f, 0.125f, 0.05f, 10, C(Rgb(250, 250, 250))); // はちまき
        Raylib.DrawCube(new Vector3(0, 0.88f, -0.04f), 0.07f, 0.06f, 0.12f, C(Black));          // ちょんまげ
        // 腕を振る
        Limb(0.22f, 0.58f, 0, -swing * 0.8f - 10, 0.55f, 0.09f, Skin);
        // 右肩に担いだ棒と状箱
        Raylib.DrawCube(new Vector3(-0.2f, 0.66f, 0.15f), 0.04f, 0.04f, 1.3f, C(Rgb(140, 100, 60)));
        Raylib.DrawCube(new Vector3(-0.2f, 0.5f, 0.75f), 0.26f, 0.22f, 0.32f, C(Rgb(25, 20, 20)));
        Raylib.DrawCube(new Vector3(-0.2f, 0.5f, 0.92f), 0.18f, 0.1f, 0.01f, C(Rgb(200, 160, 50)));  // 紋
        Limb(-0.22f, 0.58f, 0, -70, 0.4f, 0.09f, Skin);
        Rlgl.PopMatrix();
    }

    private static Color Mul(Color c, float k) =>
        new((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k), c.A);
}
