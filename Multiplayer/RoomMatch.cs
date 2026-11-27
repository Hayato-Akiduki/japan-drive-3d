namespace JapanDrive.Multiplayer;

internal sealed class RoomMatch(RoomMode mode)
{
    internal static readonly (double X, double Z)[] RaceGates =
        [(85, -200), (235, -125), (265, 115), (25, 265), (-190, 100), (-145, -160)];
    internal static readonly (double X, double Z) ControlPoint = (0, 120);
    internal const double ControlRadius = 42;
    internal const double GateRadius = 30;
    internal const double EarthGateRadius = 55;
    internal static double SpawnX(int seat) => (seat - 2.5) * 9;
    internal const double SpawnZ = -280;

    private readonly Dictionary<int, double> scores = new();
    private int number, it;
    private int[] winners = [];
    private long startsAt, endsAt, updatedAt, nextTag;
    private RoundPhase phase;
    public RoomMode Mode { get; private set; } = mode;
    public RaceCourse? Course { get; private set; }
    public int Revision { get; private set; }
    public bool Locked => phase is RoundPhase.Countdown or RoundPhase.Playing;

    public static bool RaceWorldIsMoon(RaceCourse? course) => course?.Moon ?? true;

    public static bool MatchUsesMoon(RoomMode mode, RaceCourse? course) =>
        mode != RoomMode.Race || RaceWorldIsMoon(course);

    public static double RaceGateRadius(RaceCourse? course) =>
        RaceWorldIsMoon(course) ? GateRadius : EarthGateRadius;

    public static (double X, double Z)[] RaceCheckpoints(RaceCourse? course) =>
        course == null ? RaceGates : course.Points.Skip(1).Select(p => (p.X, p.Z)).ToArray();

    public static (double X, double Z, float Heading) RaceSpawn(RaceCourse? course, int seat)
    {
        double offset = (seat - 2.5) * 9;
        if (course == null) return (offset, SpawnZ, 0f);
        var start = course.Points[0];
        var next = course.Points[1];
        double dx = next.X - start.X, dz = next.Z - start.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        double fx = len > 0.001 ? dx / len : 0;
        double fz = len > 0.001 ? dz / len : 1;
        // 進行方向 (fx, fz) に対する右方向 (fz, -fx) へ横並びに配置
        double x = start.X + fz * offset;
        double z = start.Z - fx * offset;
        if (course.Moon)
        {
            double r2 = x * x + z * z;
            if (r2 > 890.0 * 890.0)
            {
                double s = 890.0 / Math.Sqrt(r2);
                x *= s;
                z *= s;
            }
        }
        float heading = (float)((Math.Atan2(fx, fz) * 180.0 / Math.PI + 360.0) % 360.0);
        return (x, z, heading);
    }

    public static long RaceDurationMs(RaceCourse? course)
    {
        if (course == null || course.Moon) return 180_000;
        double total = 0;
        for (int i = 1; i < course.Points.Length; i++)
        {
            double dx = course.Points[i].X - course.Points[i - 1].X;
            double dz = course.Points[i].Z - course.Points[i - 1].Z;
            total += Math.Sqrt(dx * dx + dz * dz);
        }
        long extra = (long)(total / 35.0 * 1000.0);
        return Math.Clamp(180_000 + extra, 180_000, (long)RoomProtocol.MaxRaceSeconds * 1000);
    }

    public RoomSnapshot Update(RoomPlayer[] players, int sender, PlayerPose pose, long now)
    {
        if (!Locked && sender == 1 && pose.Config is { } config && RoomProtocol.ValidConfig(config))
        {
            bool changed = false;
            if (config.Mode != Mode)
            {
                Mode = config.Mode;
                ResetRound();
                changed = true;
            }
            if (config.UpdateCourse && !CourseEquals(Course, config.Course))
            {
                Course = config.Course;
                if (Mode == RoomMode.Race) ResetRound();
                changed = true;
            }
            if (changed) Revision++;
        }

        if (Mode == RoomMode.Free) return new(players, null, Mode, Course, Revision);
        if (!Locked && sender == 1 && pose.StartRound && CanStart(players, Mode, Course))
        {
            number++;
            scores.Clear();
            foreach (var player in players.Where(p => p.Pose != null)) scores.Add(player.Seat, 0);
            it = Mode == RoomMode.Tag ? scores.Keys.ElementAt(Random.Shared.Next(scores.Count)) : 0;
            startsAt = now + 4000;
            endsAt = startsAt + (Mode == RoomMode.Race ? RaceDurationMs(Course) : 90_000);
            nextTag = startsAt + 1_500;
            phase = RoundPhase.Countdown;
            winners = [];
        }
        if (Locked)
        {
            foreach (int seat in scores.Keys.Where(seat => !players.Any(p => p.Seat == seat)).ToArray())
                scores.Remove(seat);
            if (scores.Count < 2) Finish(scores.Keys);
            else if (now >= startsAt)
            {
                if (phase == RoundPhase.Countdown) updatedAt = startsAt;
                phase = RoundPhase.Playing;
                double elapsed = Math.Clamp(Math.Min(now, endsAt) - updatedAt, 0, 250) / 1000d;
                updatedAt = now;
                Play(players, now, elapsed);
                if (phase == RoundPhase.Playing && now >= endsAt)
                {
                    double best = scores.Values.Max();
                    Finish(scores.Where(p => best - p.Value < 0.05).Select(p => p.Key));
                }
            }
        }
        double seconds = phase switch
        {
            RoundPhase.Countdown => Math.Max(0, (startsAt - now) / 1000d),
            RoundPhase.Playing => Math.Max(0, (endsAt - now) / 1000d),
            _ => 0,
        };
        return new(players, new RoundSnapshot(number, phase, seconds, it,
            scores.Select(p => new RoundScore(p.Key, p.Value)).ToArray(), winners),
            Mode, Course, Revision);
    }

    public static bool CanStart(RoomPlayer[] players, RoomMode mode = RoomMode.Control, RaceCourse? course = null)
    {
        if (mode == RoomMode.Free || players.Length < 2) return false;
        bool needMoon = MatchUsesMoon(mode, course);
        return players.All(player => player?.Pose is { Vehicle: "Sports" } p && p.Moon == needMoon);
    }

    private void ResetRound()
    {
        phase = RoundPhase.Waiting;
        scores.Clear();
        winners = [];
        it = 0;
    }

    private static bool CourseEquals(RaceCourse? a, RaceCourse? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Moon != b.Moon || a.Points.Length != b.Points.Length) return false;
        for (int i = 0; i < a.Points.Length; i++)
            if (Math.Abs(a.Points[i].X - b.Points[i].X) > 0.01 || Math.Abs(a.Points[i].Z - b.Points[i].Z) > 0.01)
                return false;
        return true;
    }

    private void Play(RoomPlayer[] players, long now, double elapsed)
    {
        bool needMoon = MatchUsesMoon(Mode, Course);
        var active = players.Where(p => scores.ContainsKey(p.Seat) &&
            p.Pose is { Vehicle: "Sports" } pose && pose.Moon == needMoon && pose.Round == number).ToArray();
        switch (Mode)
        {
            case RoomMode.Control:
                var inside = active.Where(p => Near(p.Pose!, ControlPoint, ControlRadius)).ToArray();
                if (inside.Length == 1) scores[inside[0].Seat] += elapsed;
                break;
            case RoomMode.Race:
                var gates = RaceCheckpoints(Course);
                double gateRadius = RaceGateRadius(Course);
                foreach (var racer in active)
                {
                    int next = (int)scores[racer.Seat];
                    if (next < 0 || next >= gates.Length) continue;
                    if (!Near(racer.Pose!, gates[next], gateRadius)) continue;
                    if (++scores[racer.Seat] == gates.Length)
                    {
                        Finish([racer.Seat]);
                        return;
                    }
                }
                break;
            case RoomMode.Tag:
                if (!scores.ContainsKey(it))
                {
                    it = scores.Keys.First();
                    nextTag = now + 1500;
                }
                foreach (var runner in active)
                    if (runner.Seat != it) scores[runner.Seat] += elapsed;
                var chaser = active.FirstOrDefault(p => p.Seat == it)?.Pose;
                if (now < nextTag || chaser == null) break;
                var caught = active.Where(p => p.Seat != it && Math.Abs(chaser.Y - p.Pose!.Y) <= 5 &&
                    Near(p.Pose!, (chaser.X, chaser.Z), 8))
                    .MinBy(p => DistanceSquared(p.Pose!, (chaser.X, chaser.Z)));
                if (caught != null)
                {
                    it = caught.Seat;
                    nextTag = now + 1500;
                }
                break;
        }
    }

    private void Finish(IEnumerable<int> seats)
    {
        phase = RoundPhase.Finished;
        winners = seats.ToArray();
    }

    private static bool Near(PlayerPose pose, (double X, double Z) point, double radius) =>
        DistanceSquared(pose, point) <= radius * radius;

    private static double DistanceSquared(PlayerPose pose, (double X, double Z) point)
    {
        double dx = pose.X - point.X, dz = pose.Z - point.Z;
        return dx * dx + dz * dz;
    }
}
