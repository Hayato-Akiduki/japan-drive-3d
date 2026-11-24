using System.Numerics;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed partial class App
{
    private const float SpaceTravelDuration = 4f;
    private const float SpaceTravelBlend = 0.85f;

    private sealed class SpaceTravel(bool toMoon)
    {
        public bool ToMoon { get; } = toMoon;
        public float Elapsed { get; set; }
    }

    private SpaceTravel? spaceTravel;
    private RenderTexture2D spaceFrame;
    private Texture2D earthDisc, moonDisc;
    private Vector3[]? travelStars;

    private void BeginSpaceTravel(bool toMoon)
    {
        spaceTravel = new SpaceTravel(toMoon);
        Audio.Play(Audio.Whoosh, 0.65f);
    }

    private void UpdateSpaceTravel(float dt)
    {
        var travel = spaceTravel!;
        travel.Elapsed += dt;
        if (travel.Elapsed >= SpaceTravelDuration - SpaceTravelBlend && onMoon != travel.ToMoon)
        {
            // 背景が飛行シーンに完全に隠れている間に到着先を準備する。
            if (travel.ToMoon) EnterMoon();
            else ReturnToEarth();
            UpdateCamera(dt);
        }
        if (travel.Elapsed >= SpaceTravelDuration)
        {
            spaceTravel = null;
            UnloadSpaceFrame();
        }
    }

    private static float Smooth(float value)
    {
        float t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private float PrepareSpaceTravelFrame(SpaceTravel travel, int width, int height)
    {
        float opacity = Smooth(Math.Min(travel.Elapsed, SpaceTravelDuration - travel.Elapsed) / SpaceTravelBlend);
        if (opacity <= 0 || width <= 0 || height <= 0) return 0;
        if (spaceFrame.Texture.Width != width || spaceFrame.Texture.Height != height)
        {
            UnloadSpaceFrame();
            spaceFrame = Raylib.LoadRenderTexture(width, height);
        }
        if (spaceFrame.Id == 0) return 0;

        if (earthDisc.Id == 0) earthDisc = CreatePlanetDisc(earth: true);
        if (moonDisc.Id == 0) moonDisc = CreatePlanetDisc(earth: false);
        if (travelStars == null)
        {
            var random = new Random(319);
            travelStars = new Vector3[160];
            for (int i = 0; i < travelStars.Length; i++)
                travelStars[i] = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
        }

        float t = travel.Elapsed / SpaceTravelDuration;
        Raylib.BeginTextureMode(spaceFrame);
        Raylib.ClearBackground(Rgb(5, 8, 20));
        Raylib.DrawRectangleGradientV(0, 0, width, height, Rgb(5, 8, 20), Rgb(14, 24, 46));
        foreach (var star in travelStars)
        {
            float x = ((star.X + 1 - t * (0.12f + star.Z * 0.22f)) % 1) * width;
            float y = star.Y * height;
            Raylib.DrawCircleV(new Vector2(x, y), (0.5f + star.Z) * Ui, Raylib.Fade(Rgb(217, 232, 255), 0.25f + star.Z * 0.7f));
        }

        bool toMoon = travel.ToMoon;
        float motion = Smooth(t);
        var origin = new Vector2(width * (0.19f - 0.18f * motion), height * (0.71f - 0.02f * motion));
        var destination = new Vector2(width * (0.79f - 0.06f * motion), height * (0.32f + 0.09f * motion));
        float originScale = (toMoon ? 0.44f : 0.39f) * (1 - 0.80f * motion);
        float destinationScale = 0.055f + (toMoon ? 0.31f : 0.40f) * motion;
        DrawPlanetDisc(toMoon ? earthDisc : moonDisc, origin, height * originScale);
        DrawPlanetDisc(toMoon ? moonDisc : earthDisc, destination, height * destinationScale);

        DrawTravelShip(t);

        var title = toMoon ? "月へ向けて飛行中" : "地球へ向けて飛行中";
        var status = t < 0.30f ? (toMoon ? "地球を離脱" : "月を離脱")
            : t < 0.68f ? "宇宙空間を航行中"
            : toMoon ? "静かの海の上空へ接近" : "日本の上空へ降下";
        TextCentered(title, width / 2f, height - 124 * Ui, 27, Color.White);
        TextCentered(status, width / 2f, height - 82 * Ui, 17, Rgb(178, 214, 243));
        Raylib.EndTextureMode();
        return opacity;
    }

    private void DrawTravelShip(float t)
    {
        var flightCamera = new Camera3D
        {
            Position = new Vector3(0, 9, -31),
            Target = new Vector3(0, 1, 0),
            Up = Vector3.UnitY,
            FovY = 42,
            Projection = CameraProjection.Perspective,
        };
        Rlgl.SetClipPlanes(0.05, 1200);
        Raylib.BeginMode3D(flightCamera);
        Rlgl.PushMatrix();
        float arc = MathF.Sin(t * MathF.PI);
        Rlgl.Translatef(8 - 18 * t, -1 + 2.6f * arc, 3 + 4 * t);
        Rlgl.Rotatef(-58f - 20f * t, 0, 1, 0);
        Rlgl.Rotatef(-14 + 19 * Smooth(t), 1, 0, 0);
        Rlgl.Rotatef(-18 * arc, 0, 0, 1);
        Aircraft.Draw(scene, VehicleKind.Spaceship, PlayerColor, 0, t * 950, gearDown: false);
        Rlgl.PopMatrix();
        Raylib.EndMode3D();
    }

    private static Texture2D CreatePlanetDisc(bool earth)
    {
        // 円盤の各ピクセルを球面に投影し、左上からの光で陰影を付ける。
        const int size = 256;
        Image image = Raylib.GenImageColor(size, size, Color.Blank);
        Vector3[] craters = earth ? [] :
        [
            new(-0.31f, 0.24f, 0.17f), new(0.34f, -0.32f, 0.15f), new(0.27f, 0.44f, 0.13f),
            new(-0.49f, -0.43f, 0.11f), new(-0.66f, 0.10f, 0.08f), new(0.12f, -0.63f, 0.08f),
            new(0.65f, 0.19f, 0.09f), new(-0.04f, -0.15f, 0.07f), new(-0.15f, 0.65f, 0.10f),
        ];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = (x + 0.5f) * 2 / size - 1;
            float ny = (y + 0.5f) * 2 / size - 1;
            float radiusSquared = nx * nx + ny * ny;
            if (radiusSquared >= 1) continue;
            float z = MathF.Sqrt(1 - radiusSquared);
            float grain = MathF.Sin(37 * nx + 19 * ny) * MathF.Sin(32 * ny + 12 * z);
            float terrain = MathF.Sin(9 * nx + 3 * z) * MathF.Cos(7 * ny - z) +
                0.38f * MathF.Sin(17 * nx - 12 * ny + 6 * z) + 0.07f * grain;
            float light = Math.Clamp(0.24f + 0.76f * (-nx * 0.54f - ny * 0.46f + z * 0.70f), 0.16f, 1);
            Color surface;
            if (earth)
            {
                surface = terrain > 0.32f ? Rgb(87, 154, 103) : Rgb(24, 100, 198);
                float clouds = MathF.Sin(21 * ny + 3 * MathF.Sin(6 * nx) + 4 * z) + 0.45f * terrain;
                surface = LerpColor(surface, Rgb(221, 234, 242), Math.Clamp((clouds - 0.71f) * 0.82f, 0, 0.72f));
            }
            else
            {
                float shade = 0.62f + 0.18f * terrain;
                foreach (var crater in craters)
                {
                    float dx = (nx - crater.X) / crater.Z, dy = (ny - crater.Y) / crater.Z;
                    float distance = MathF.Sqrt(dx * dx + dy * dy);
                    float bowl = 1 - Smooth(distance);
                    float rim = 1 - Smooth(Math.Abs(distance - 0.88f) / 0.25f);
                    shade -= 0.29f * bowl;
                    shade += 0.26f * rim * (-0.55f * dx - 0.45f * dy + 0.18f);
                }
                shade = Math.Clamp(shade, 0, 1);
                surface = LerpColor(Rgb(101, 105, 119), Rgb(217, 218, 224), shade);
            }
            var color = LerpColor(Color.Black, surface, light);
            color.A = (byte)(255 * Math.Clamp((1 - radiusSquared) * size / 3, 0, 1));
            Raylib.ImageDrawPixel(ref image, x, y, color);
        }
        var texture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        Raylib.SetTextureFilter(texture, TextureFilter.Bilinear);
        return texture;
    }

    private static void DrawPlanetDisc(Texture2D texture, Vector2 center, float radius)
    {
        Raylib.DrawTexturePro(texture, new Rectangle(0, 0, texture.Width, texture.Height),
            new Rectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2), Vector2.Zero, 0, Color.White);
    }

    private void DrawSpaceTravelFrame(int width, int height, float opacity)
    {
        Raylib.DrawTexturePro(spaceFrame.Texture, new Rectangle(0, 0, width, -height),
            new Rectangle(0, 0, width, height), Vector2.Zero, 0, Raylib.Fade(Color.White, opacity));
    }

    private void UnloadSpaceFrame()
    {
        if (spaceFrame.Id != 0) Raylib.UnloadRenderTexture(spaceFrame);
        spaceFrame = default;
    }

    private void UnloadSpaceTravelGraphics()
    {
        UnloadSpaceFrame();
        if (earthDisc.Id != 0) Raylib.UnloadTexture(earthDisc);
        if (moonDisc.Id != 0) Raylib.UnloadTexture(moonDisc);
    }
}
