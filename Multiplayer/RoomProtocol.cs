namespace JapanDrive.Multiplayer;

internal static class RoomProtocol
{
    public const int CodeLength = 6;
    public const int Capacity = 4;
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly HashSet<string> Vehicles =
        ["Sports", "Tank", "Amphibious", "Horse", "Hikyaku", "Helicopter", "Airplane", "Fighter", "Spaceship"];

    public static bool IsCode(string? code) =>
        code?.Length == CodeLength && code.All(Alphabet.Contains);

    public static bool ValidPose(PlayerPose pose) =>
        pose.Vehicle != null && Vehicles.Contains(pose.Vehicle) &&
        Math.Abs(pose.X) <= (pose.Moon ? 1000 : 30_000_000) &&
        Math.Abs(pose.Z) <= (pose.Moon ? 1000 : 30_000_000) &&
        Math.Abs(pose.Y) <= 100_000 && Math.Abs(pose.Heading) <= 360 &&
        Math.Abs(pose.Pitch) <= 360 && Math.Abs(pose.Bank) <= 360 &&
        Math.Abs(pose.Speed) <= 100_000;

    public static bool ValidSnapshot(RoomSnapshot? snapshot)
    {
        if (snapshot?.Players == null || snapshot.Players.Length > Capacity) return false;
        if (snapshot.Match is not { } match) return true;
        return Enum.IsDefined(match.Phase) && match.Number >= 0 && match.It is >= 0 and <= Capacity &&
            match.Seconds is >= 0 and <= 180 && match.Scores is { Length: <= Capacity } &&
            match.Scores.All(score => score is { Seat: >= 1 and <= Capacity, Points: >= 0 and <= 180 }) &&
            match.Winners is { Length: <= Capacity } && match.Winners.All(seat => seat is >= 1 and <= Capacity);
    }
}

internal enum RoomMode { Free, Control, Race, Tag }
internal enum RoundPhase { Waiting, Countdown, Playing, Finished }
internal sealed record RoomTicket(string Code, Guid Token, int Seat, RoomMode Mode = RoomMode.Free);
internal sealed record PlayerPose(bool Moon, string Vehicle, double X, double Z, float Y,
    float Heading, float Pitch, float Bank, float Speed, int Round = 0, bool StartRound = false);
internal sealed record RoomPlayer(int Seat, PlayerPose? Pose);
internal sealed record RoundScore(int Seat, double Points);
internal sealed record RoundSnapshot(int Number, RoundPhase Phase, double Seconds, int It, RoundScore[] Scores, int[] Winners);
internal sealed record RoomSnapshot(RoomPlayer[] Players, RoundSnapshot? Match = null);
