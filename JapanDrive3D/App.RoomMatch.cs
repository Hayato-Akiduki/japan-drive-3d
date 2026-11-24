using System.Numerics;
using JapanDrive.Multiplayer;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed partial class App
{
    private static string RoomModeName(RoomMode mode) => mode switch
    {
        RoomMode.Control => "対戦（陣取り）",
        RoomMode.Race => "レース",
        RoomMode.Tag => "鬼ごっこ",
        _ => "フリー走行",
    };

    private void SpawnMatchVehicle(int seat)
    {
        if (moon is not { } arena) return;
        arena.PX = RoomMatch.SpawnX(seat);
        arena.PZ = RoomMatch.SpawnZ;
        freeY = groundLevel = arena.SurfaceY(arena.PX, arena.PZ);
        arena.Altitude = altitude = freeKmh = carVy = 0;
        pitch = bank = terrainPitch = terrainBank = steerYaw = 0;
        isDrifting = carAir = prevAirborne = false;
        manualGear = 1;
        heading = 0;
        UpdateCamera(0);
    }

    private (double X, double Z)? MatchTarget()
    {
        if (roomRound is not { Phase: RoundPhase.Playing or RoundPhase.Countdown } round || room == null) return null;
        if (room.Ticket.Mode == RoomMode.Control) return RoomMatch.ControlPoint;
        if (room.Ticket.Mode != RoomMode.Race) return null;
        var score = round.Scores.FirstOrDefault(entry => entry.Seat == room.Ticket.Seat);
        int gate = (int)(score?.Points ?? 0);
        return gate >= 0 && gate < RoomMatch.RaceGates.Length ? RoomMatch.RaceGates[gate] : null;
    }

    private void DrawMatchBeacon(MoonWorld arena)
    {
        if (MatchTarget() is not { } target) return;
        float ground = arena.SurfaceY(target.X, target.Z);
        var center = arena.R(target.X, ground + 1, target.Z);
        float radius = room!.Ticket.Mode == RoomMode.Control ? (float)RoomMatch.ControlRadius : (float)RoomMatch.GateRadius;
        Raylib.DrawCircle3D(center, radius, Vector3.UnitX, 90, Orange);
        Raylib.DrawCylinder(center, 0.7f, 1.6f, 17, 8, Orange);
        Raylib.DrawSphereEx(center + Vector3.UnitY * 17, 2.5f, 8, 10, Rgb(255, 219, 115));
    }

    private void DrawMatchStatus(int sw, int sh, float u)
    {
        var ticket = room!.Ticket;
        var mode = ticket.Mode;
        string rule = mode switch
        {
            RoomMode.Control => "オレンジの陣地を1人で確保すると得点",
            RoomMode.Race => $"オレンジの目印を順番に通過（全{RoomMatch.RaceGates.Length}か所）",
            _ => "鬼は相手に近づくと交代。逃げた時間で競う",
        };
        var round = roomRound;
        string status = round?.Phase switch
        {
            RoundPhase.Countdown => $"スタートまで {Math.Max(1, Math.Ceiling(round.Seconds)):0}",
            RoundPhase.Playing => $"残り {Math.Max(0, Math.Ceiling(round.Seconds)):0} 秒" +
                (mode == RoomMode.Tag ? round.It == ticket.Seat ? "　あなたが鬼です" : $"　鬼：P{round.It}" : ""),
            RoundPhase.Finished => (round.Winners.Length == 1 ? $"勝者：P{round.Winners[0]}"
                : round.Winners.Length > 1 ? $"同点：{string.Join("・", round.Winners.Select(seat => $"P{seat}"))}" : "試合終了") +
                (ticket.Seat == 1 && RoomMatch.CanStart(roomPlayers) ? "　F8：再戦" : ""),
            _ => ticket.Seat == 1 && RoomMatch.CanStart(roomPlayers)
                ? "F8：試合開始" : "参加者とホストの開始操作を待っています",
        };
        string scores = round == null || round.Scores.Length == 0 ? rule
            : string.Join("　", round.Scores.Select(score =>
                $"P{score.Seat}{(score.Seat == ticket.Seat ? "（自分）" : "")} " +
                (mode == RoomMode.Race ? $"{score.Points:0}/{RoomMatch.RaceGates.Length}" : $"{score.Points:0.0}秒")));
        string header = $"{RoomModeName(mode)}　｜　{status}";
        float width = Math.Max(Measure(header, 17).X, Measure(scores, 14).X) + 30 * u;
        Panel((sw - width) / 2, sh - 173 * u, width, 62 * u, Rgb(17, 41, 64));
        TextCentered(header, sw / 2f, sh - 163 * u, 17, Color.White);
        TextCentered(scores, sw / 2f, sh - 138 * u, 14, Rgb(188, 227, 245));
    }
}
