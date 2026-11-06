using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed class Vehicle
{
    public double S;
    public float U, TargetU;
    public float Speed, Desired;   // m/s
    public Color Color;
    public VehicleKind Kind;
    public bool Oncoming;
    public bool Braking;
    // 吹き飛ばされて宙を舞っている間の状態
    public bool Flying;
    public float Y, Vy, Vu, Roll, Spin, FlyTime;
    public bool Truck => Kind == VehicleKind.Truck;
    public float Length => CarModels.Length(Kind);
}

/// <summary>ほかの車（同じ方向と対向車線）。</summary>
public sealed class Traffic
{
    public const float DrivingLane = -6.2f;  // 左の走行車線
    public const float PassingLane = -2.6f;  // 右の追越車線
    public const float PlayerLength = 4.6f;

    private static readonly float[] OppLanes = { 2.6f, 6.2f };
    private static readonly Color[] Palette =
    {
        Rgb(240, 240, 240), Rgb(30, 30, 35), Rgb(150, 155, 165), Rgb(40, 70, 150),
        Rgb(160, 30, 40), Rgb(210, 190, 60), Rgb(60, 120, 80), Rgb(230, 230, 220),
    };

    private readonly Random rng = new();
    public List<Vehicle> Cars { get; } = new();

    public void Reset(double playerS)
    {
        Cars.Clear();
        for (int i = 0; i < 11; i++) Cars.Add(NewCar(false, playerS + 60 + i * 75 + rng.NextDouble() * 30));
        for (int i = 0; i < 8; i++) Cars.Add(NewCar(true, playerS + rng.NextDouble() * 900));
    }

    private Vehicle NewCar(bool oncoming, double s)
    {
        double roll = rng.NextDouble();
        var kind = roll < 0.22 ? VehicleKind.Truck : roll < 0.42 ? VehicleKind.Kei
            : roll < 0.65 ? VehicleKind.Minivan : roll < 0.7 ? VehicleKind.Sports : VehicleKind.Sedan;
        bool truck = kind == VehicleKind.Truck;
        float desired = truck ? 22 + (float)rng.NextDouble() * 3 : 24 + (float)rng.NextDouble() * 8;
        float lane = oncoming ? OppLanes[rng.Next(2)]
            : truck || rng.NextDouble() < 0.7 ? DrivingLane : PassingLane;
        return new Vehicle
        {
            S = s,
            U = lane,
            TargetU = lane,
            Speed = desired,
            Desired = desired,
            Color = Palette[rng.Next(Palette.Length)],
            Kind = kind,
            Oncoming = oncoming,
        };
    }

    private bool LaneFree(float lane, double from, double to, Vehicle self, double playerS, float playerU)
    {
        if (Math.Abs(playerU - lane) < 2.2f && playerS > from && playerS < to) return false;
        foreach (var o in Cars)
        {
            if (o == self || o.Oncoming || o.Flying) continue;
            if (Math.Abs(o.TargetU - lane) < 1f && o.S > from && o.S < to) return false;
        }
        return true;
    }

    public void Update(float dt, double playerS, float playerU, float playerSpeed,
        float playerLength = PlayerLength, bool playerOnRoad = true)
    {
        if (!playerOnRoad) playerU = 99; // 海上などにいるときは道路上の障害物にならない
        foreach (var v in Cars)
        {
            if (v.Flying)
            {
                v.FlyTime += dt;
                v.Vy -= 20 * dt;
                v.Y += v.Vy * dt;
                v.U += v.Vu * dt;
                v.S += (v.Oncoming ? -v.Speed : v.Speed) * dt;
                v.Roll += v.Spin * dt;
                if (v.Y < 0)
                {
                    v.Y = 0;
                    v.Vy = Math.Abs(v.Vy) * 0.35f;   // 1回バウンドする
                    v.Vu *= 0.6f;
                    v.Speed *= 0.6f;
                    v.Spin *= 0.5f;
                }
                if (v.FlyTime > 3.2f)
                    Respawn(v, v.Oncoming ? playerS + 800 + rng.NextDouble() * 300 : playerS + 700 + rng.NextDouble() * 250);
                continue;
            }

            if (v.Oncoming)
            {
                v.S -= v.Speed * dt;
                if (v.S < playerS - 120) Respawn(v, playerS + 800 + rng.NextDouble() * 300);
                continue;
            }

            // 前の車（プレイヤー含む）との車間
            double gap = double.MaxValue;
            float leadSpeed = v.Desired;
            foreach (var o in Cars)
            {
                if (o == v || o.Oncoming || o.Flying || Math.Abs(o.U - v.U) > 2.2f || o.S <= v.S) continue;
                double g = o.S - v.S - (o.Length + v.Length) / 2;
                if (g < gap) { gap = g; leadSpeed = o.Speed; }
            }
            if (Math.Abs(playerU - v.U) < 2.2f && playerS > v.S)
            {
                double g = playerS - v.S - (playerLength + v.Length) / 2;
                if (g < gap) { gap = g; leadSpeed = playerSpeed; }
            }

            float target = v.Desired;
            if (gap < 60) target = Math.Min(target, leadSpeed + (float)(gap - 12) * 0.35f);
            target = Math.Max(0, target);

            // 追い越し・走行車線への復帰（日本は左側通行）
            bool inDriving = v.TargetU == DrivingLane;
            if (gap < 50 && leadSpeed < v.Desired - 2 && !v.Truck && inDriving &&
                LaneFree(PassingLane, v.S - 20, v.S + 45, v, playerS, playerU))
                v.TargetU = PassingLane;
            else if (!inDriving && rng.NextDouble() < dt * 0.5 &&
                     LaneFree(DrivingLane, v.S - 15, v.S + 70, v, playerS, playerU))
                v.TargetU = DrivingLane;

            v.Braking = target < v.Speed - 0.5f;
            v.Speed += Math.Clamp(target - v.Speed, -12 * dt, 3 * dt);
            v.U += Math.Clamp(v.TargetU - v.U, -2.5f * dt, 2.5f * dt);
            v.S += v.Speed * dt;

            if (v.S < playerS - 250) Respawn(v, playerS + 700 + rng.NextDouble() * 250);
            else if (v.S > playerS + 1000) Respawn(v, playerS - 220);
        }
    }

    private void Respawn(Vehicle v, double s)
    {
        var n = NewCar(v.Oncoming, s);
        v.S = n.S;
        v.U = v.TargetU = n.U;
        v.Speed = v.Desired = n.Desired;
        v.Color = n.Color;
        v.Kind = n.Kind;
        v.Flying = false;
        v.Y = v.Vy = v.Vu = v.Roll = v.Spin = v.FlyTime = 0;
        // 出現位置に他の車がいたら少しずらす
        foreach (var o in Cars)
            if (o != v && o.Oncoming == v.Oncoming && Math.Abs(o.U - v.U) < 1 && Math.Abs(o.S - v.S) < 15)
                v.S += v.Oncoming ? 25 : (s > o.S ? 25 : -25);
    }

    public Vehicle? CheckCollision(double playerS, float playerU, float playerLength = PlayerLength, float playerHalfWidth = 0.9f)
    {
        foreach (var v in Cars)
        {
            if (v.Oncoming || v.Flying) continue;
            if (Math.Abs(v.S - playerS) < (v.Length + playerLength) / 2 - 0.3 && Math.Abs(v.U - playerU) < 0.9f + playerHalfWidth)
                return v;
        }
        return null;
    }

    /// <summary>砲弾などが当たったか（対向車も含む）。</summary>
    public Vehicle? HitTest(double s, float u, float y)
    {
        foreach (var v in Cars)
        {
            if (v.Flying) continue;
            float height = v.Truck ? 3.7f : 1.9f;
            if (Math.Abs(v.S - s) < v.Length / 2 + 0.8 && Math.Abs(v.U - u) < 1.4f && y < height + 0.4f && y > -0.5f)
                return v;
        }
        return null;
    }

    /// <summary>車を吹き飛ばす（漫画的にくるくる回って飛んでいく）。</summary>
    public void Knock(Vehicle v, float sideDir, float power)
    {
        v.Flying = true;
        v.FlyTime = 0;
        v.Y = 0.2f;
        v.Vy = (v.Truck ? 9 : 14) * power;
        v.Vu = sideDir * (4 + (float)rng.NextDouble() * 4) * power;
        v.Spin = (rng.NextDouble() < 0.5 ? -1 : 1) * (220 + (float)rng.NextDouble() * 260) * power;
        v.Speed *= 0.5f;
    }
}
