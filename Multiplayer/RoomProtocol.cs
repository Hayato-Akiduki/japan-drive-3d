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
}

internal sealed record RoomTicket(string Code, Guid Token, int Seat);
internal sealed record PlayerPose(bool Moon, string Vehicle, double X, double Z, float Y,
    float Heading, float Pitch, float Bank, float Speed);
internal sealed record RoomPlayer(int Seat, PlayerPose? Pose);
internal sealed record RoomSnapshot(RoomPlayer[] Players);
