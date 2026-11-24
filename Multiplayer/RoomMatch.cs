namespace JapanDrive.Multiplayer;

internal sealed class RoomMatch(RoomMode mode)
{
    internal static readonly (double X, double Z)[] RaceGates =
        [(85, -200), (235, -125), (265, 115), (25, 265), (-190, 100), (-145, -160)];
    internal static readonly (double X, double Z) ControlPoint = (0, 120);
    internal const double ControlRadius = 42;
    internal const double GateRadius = 30;
    internal static double SpawnX(int seat) => (seat - 2.5) * 9;
    internal const double SpawnZ = -280;

    private readonly Dictionary<int, double> scores = new();
    private int number, it;
    private int[] winners = [];
    private long startsAt, endsAt, updatedAt, nextTag;
    private RoundPhase phase;
    public RoomMode Mode { get; } = mode;
    public bool Locked => phase is RoundPhase.Countdown or RoundPhase.Playing;

    public RoomSnapshot Update(RoomPlayer[] players, int sender, PlayerPose pose, long now)
    {
        if (Mode == RoomMode.Free) return new(players);
        if (!Locked && sender == 1 && pose.StartRound && CanStart(players))
        {
            number++;
            scores.Clear();
            foreach (var player in players.Where(p => p.Pose != null)) scores.Add(player.Seat, 0);
            it = Mode == RoomMode.Tag ? scores.Keys.ElementAt(Random.Shared.Next(scores.Count)) : 0;
            startsAt = now + 4000;
            endsAt = startsAt + (Mode == RoomMode.Race ? 180_000 : 90_000);
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
            RoundPhase.Countdown => (startsAt - now) / 1000d,
            RoundPhase.Playing => (endsAt - now) / 1000d,
            _ => 0,
        };
        return new(players, new RoundSnapshot(number, phase, seconds, it,
            scores.Select(p => new RoundScore(p.Key, p.Value)).ToArray(), winners));
    }

    public static bool CanStart(RoomPlayer[] players) =>
        players.Length >= 2 && players.All(player => player?.Pose is { Moon: true, Vehicle: "Sports" });

    private void Play(RoomPlayer[] players, long now, double elapsed)
    {
        var active = players.Where(p => scores.ContainsKey(p.Seat) &&
            p.Pose is { Moon: true, Vehicle: "Sports" } pose && pose.Round == number).ToArray();
        switch (Mode)
        {
            case RoomMode.Control:
                var inside = active.Where(p => Near(p.Pose!, ControlPoint, ControlRadius)).ToArray();
                if (inside.Length == 1) scores[inside[0].Seat] += elapsed;
                break;
            case RoomMode.Race:
                foreach (var racer in active)
                {
                    int next = (int)scores[racer.Seat];
                    if (!Near(racer.Pose!, RaceGates[next], GateRadius)) continue;
                    if (++scores[racer.Seat] == RaceGates.Length)
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
