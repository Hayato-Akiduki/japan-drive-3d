namespace JapanDrive.Multiplayer;

internal static class RoomProtocol
{
    public const int CodeLength = 6;
    public const int Capacity = 4;
    public const int MinCoursePoints = 2;
    public const int MaxCoursePoints = 12;
    public const double MaxRaceSeconds = 900;
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly HashSet<string> Vehicles =
        ["Sports", "Tank", "Amphibious", "Horse", "Hikyaku", "Helicopter", "Airplane", "Fighter", "Spaceship"];

    public static bool IsCode(string? code) =>
        code?.Length == CodeLength && code.All(Alphabet.Contains);

    public static bool ValidCourse(RaceCourse? course)
    {
        if (course == null) return true;
        if (course.Points is not { Length: >= MinCoursePoints and <= MaxCoursePoints }) return false;
        for (int i = 0; i < course.Points.Length; i++)
        {
            var p = course.Points[i];
            if (p == null || !double.IsFinite(p.X) || !double.IsFinite(p.Z)) return false;
            if (course.Moon)
            {
                if (p.X * p.X + p.Z * p.Z > 900.0 * 900.0) return false;
            }
            else if (Math.Abs(p.X) > 30_000_000 || Math.Abs(p.Z) > 30_000_000) return false;
            if (i > 0)
            {
                double dx = p.X - course.Points[i - 1].X, dz = p.Z - course.Points[i - 1].Z;
                if (dx * dx + dz * dz < 25.0) return false;
            }
        }
        return true;
    }

    public static bool ValidConfig(RoomConfig? config) =>
        config == null || (Enum.IsDefined(config.Mode) && (!config.UpdateCourse || ValidCourse(config.Course)));

    public static bool ValidPose(PlayerPose pose) =>
        pose.Vehicle != null && Vehicles.Contains(pose.Vehicle) &&
        Math.Abs(pose.X) <= (pose.Moon ? 1000 : 30_000_000) &&
        Math.Abs(pose.Z) <= (pose.Moon ? 1000 : 30_000_000) &&
        Math.Abs(pose.Y) <= 100_000 && Math.Abs(pose.Heading) <= 360 &&
        Math.Abs(pose.Pitch) <= 360 && Math.Abs(pose.Bank) <= 360 &&
        Math.Abs(pose.Speed) <= 100_000 && ValidConfig(pose.Config);

    public static bool ValidSnapshot(RoomSnapshot? snapshot)
    {
        if (snapshot?.Players == null || snapshot.Players.Length > Capacity) return false;
        if (!Enum.IsDefined(snapshot.Mode) || snapshot.Revision < 0 || !ValidCourse(snapshot.Course)) return false;
        if (snapshot.Match is not { } match) return true;
        return Enum.IsDefined(match.Phase) && match.Number >= 0 && match.It is >= 0 and <= Capacity &&
            match.Seconds is >= 0 and <= MaxRaceSeconds && match.Scores is { Length: <= Capacity } &&
            match.Scores.All(score => score is { Seat: >= 1 and <= Capacity, Points: >= 0 and <= MaxRaceSeconds }) &&
            match.Winners is { Length: <= Capacity } && match.Winners.All(seat => seat is >= 1 and <= Capacity);
    }
}

internal enum RoomMode { Free, Control, Race, Tag }
internal enum RoundPhase { Waiting, Countdown, Playing, Finished }
internal sealed record CoursePoint(double X, double Z);
internal sealed record RaceCourse(bool Moon, CoursePoint[] Points);
internal sealed record RoomConfig(RoomMode Mode, RaceCourse? Course = null, bool UpdateCourse = false);
internal sealed record RoomTicket(string Code, Guid Token, int Seat, RoomMode Mode = RoomMode.Free);
internal sealed record PlayerPose(bool Moon, string Vehicle, double X, double Z, float Y,
    float Heading, float Pitch, float Bank, float Speed, int Round = 0, bool StartRound = false,
    RoomConfig? Config = null);
internal sealed record RoomPlayer(int Seat, PlayerPose? Pose);
internal sealed record RoundScore(int Seat, double Points);
internal sealed record RoundSnapshot(int Number, RoundPhase Phase, double Seconds, int It, RoundScore[] Scores, int[] Winners);
internal sealed record RoomSnapshot(RoomPlayer[] Players, RoundSnapshot? Match = null,
    RoomMode Mode = RoomMode.Free, RaceCourse? Course = null, int Revision = 0);
