using System.Net;
using System.Net.Sockets;
using System.Numerics;
using JapanDrive.Multiplayer;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed partial class App
{
    private bool roomMenu;
    private bool roomEditCode;
    private string relayUrl = Environment.GetEnvironmentVariable("JAPANDRIVE_RELAY_URL") ?? "";
    private string joinCode = "";
    private string roomMessage = "";
    private Task<IRoomConnection>? openingRoom;
    private IRoomConnection? room;
    private Task<RoomSnapshot>? exchangingRoom;
    private float nextRoomUpdate;
    private float copiedUntil;
    private int roomFailures;
    private RoomPlayer[] roomPlayers = [];
    private readonly Dictionary<int, RemoteVehicle> remoteVehicles = new();

    private sealed record RemoteVehicle(PlayerPose From, PlayerPose Target, float ReceivedAt);

    private static Rectangle RoomMenuButton(float u) => new(18 * u, 18 * u, 180 * u, 42 * u);

    private static Rectangle RoomPanel(int sw, int sh, float u)
    {
        float width = Math.Min(sw - 32 * u, 670 * u);
        return new Rectangle((sw - width) / 2, (sh - 390 * u) / 2, width, 390 * u);
    }

    private void OpenRoomMenu()
    {
        roomMessage = "";
        roomEditCode = false;
        roomMenu = true;
    }

    private static Rectangle RoomControl(Rectangle panel, int index, float u) => index switch
    {
        0 => new(panel.X + 24 * u, panel.Y + 99 * u, panel.Width - 48 * u, 44 * u),
        1 => new(panel.X + 24 * u, panel.Y + 182 * u, panel.Width - 48 * u, 44 * u),
        2 => new(panel.X + 24 * u, panel.Y + 251 * u, (panel.Width - 60 * u) / 2, 48 * u),
        _ => new(panel.X + panel.Width / 2 + 6 * u, panel.Y + 251 * u, (panel.Width - 60 * u) / 2, 48 * u),
    };

    private void UpdateRoomMenu()
    {
        if (openingRoom != null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            roomMenu = false;
            return;
        }
        var panel = RoomPanel(Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), Ui);
        if (Raylib.IsKeyPressed(KeyboardKey.Tab)) roomEditCode = !roomEditCode;
        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            var mouse = Raylib.GetMousePosition();
            for (int i = 0; i < 4; i++)
            {
                if (!Raylib.CheckCollisionPointRec(mouse, RoomControl(panel, i, Ui))) continue;
                if (i < 2) roomEditCode = i == 1;
                else ConnectRoom(join: i == 3);
                break;
            }
        }
        if (openingRoom != null) return;
        string value = roomEditCode ? joinCode : relayUrl;
        const int maxLength = 240;
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && value.Length > 0) value = value[..^1];
        if ((Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)) &&
            Raylib.IsKeyPressed(KeyboardKey.V))
        {
            string clipboard = (Raylib.GetClipboardText_() ?? "").Trim();
            value = new string(clipboard.Where(c => c is >= '!' and <= '~').Take(maxLength).ToArray());
        }
        for (int key = Raylib.GetCharPressed(); key > 0; key = Raylib.GetCharPressed())
        {
            if (value.Length >= maxLength || key is < '!' or > '~') continue;
            value += (char)key;
        }
        if (roomEditCode) joinCode = value;
        else relayUrl = value;
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            ConnectRoom(join: joinCode.Length > 0);
    }

    private void ConnectRoom(bool join)
    {
        string input = joinCode.Trim();
        string code = input.ToUpperInvariant();
        if (join && DirectRoom.TryInvite(input, out _))
        {
            roomMessage = "ホストに接続中…";
            openingRoom = DirectRoom.JoinAsync(input);
            return;
        }

        string endpoint = relayUrl.Trim();
        bool relay = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (join && !RoomProtocol.IsCode(code))
        {
            roomMessage = "6文字のコードか招待リンクを入力してください。";
            return;
        }
        if (!relay)
        {
            if (!join && endpoint.Length != 0 && !DirectRoom.TryHostAddress(endpoint))
            {
                roomMessage = "公開アドレスはIPv4かホスト名で入力してください。";
                return;
            }
            roomMessage = join ? "同じLAN内のルームを検索中…" : "ルームを作成中…";
            openingRoom = join ? DirectRoom.JoinAsync(code) : DirectRoom.HostAsync(endpoint);
            return;
        }
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var address) ||
            address.Scheme is not ("https" or "http") || address.Host.Length == 0 ||
            address.UserInfo.Length != 0 || address.Fragment.Length != 0 || address.Query.Length != 0)
        {
            roomMessage = "中継サーバーの URL を確認してください。";
            return;
        }
        roomMessage = "中継サーバーに接続中…";
        openingRoom = OpenRelayRoom(address, join ? code : null);
    }

    private static async Task<IRoomConnection> OpenRelayRoom(Uri address, string? code) =>
        await RoomConnection.OpenAsync(address, code);

    private void UpdateMultiplayer()
    {
        if (openingRoom is { IsCompleted: true } opening)
        {
            openingRoom = null;
            try
            {
                room = opening.GetAwaiter().GetResult();
                roomMenu = false;
                roomMessage = "";
                nextRoomUpdate = 0;
                copiedUntil = 0;
                StartGame(GameMode.Free);
            }
            catch (Exception exception) when (IsRoomError(exception))
            {
                roomMessage = exception is HttpRequestException { StatusCode: not null } ? exception.Message
                    : exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                        ? "そのPCではすでにルームが開かれています。"
                        : "接続できませんでした。ホストの起動状態と接続先を確認してください。";
            }
        }

        if (room == null) return;
        if (title || !freeRoam || free == null)
        {
            LeaveMultiplayer();
            return;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.F6))
        {
            Raylib.SetClipboardText(room.Ticket.Code);
            copiedUntil = time + 2.5f;
        }
        if (room.Invite is { } invite && Raylib.IsKeyPressed(KeyboardKey.F7))
        {
            Raylib.SetClipboardText(invite);
            copiedUntil = time + 2.5f;
        }
        if (exchangingRoom is { IsCompleted: true } exchange)
        {
            exchangingRoom = null;
            try
            {
                roomPlayers = exchange.GetAwaiter().GetResult().Players;
                UpdateRemoteVehicles(room.Ticket.Seat);
                roomFailures = 0;
            }
            catch (Exception exception) when (IsRoomError(exception))
            {
                roomFailures++;
                if (exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized } ||
                    roomFailures >= 3)
                {
                    LeaveMultiplayer();
                    game.AddLog("ルームとの接続が切れました。タイトルから再参加できます。");
                    return;
                }
            }
        }
        if (exchangingRoom == null && time >= nextRoomUpdate)
        {
            nextRoomUpdate = time + 0.1f;
            var pose = new PlayerPose(onMoon, playerKind.ToString(), moon?.PX ?? free.PX, moon?.PZ ?? free.PZ,
                freeY, heading, pitch, bank, freeKmh);
            exchangingRoom = room.ExchangeAsync(pose);
        }
    }

    private static bool IsRoomError(Exception exception) =>
        exception is HttpRequestException or OperationCanceledException or SocketException or IOException or
            InvalidDataException or System.Text.Json.JsonException or System.Net.NetworkInformation.NetworkInformationException;

    private void UpdateRemoteVehicles(int localSeat)
    {
        foreach (int seat in remoteVehicles.Keys.ToArray())
            if (!roomPlayers.Any(player => player?.Seat == seat && player.Pose != null)) remoteVehicles.Remove(seat);
        foreach (var player in roomPlayers)
        {
            if (player == null || player.Seat is < 1 or > RoomProtocol.Capacity ||
                player.Seat == localSeat || player.Pose is not { } pose || !RoomProtocol.ValidPose(pose)) continue;
            var from = pose;
            if (remoteVehicles.TryGetValue(player.Seat, out var old) && old.Target.Moon == pose.Moon)
            {
                var previous = InterpolateRemote(old);
                double dx = pose.X - previous.X, dz = pose.Z - previous.Z;
                if (dx * dx + dz * dz < 250 * 250 && Math.Abs(pose.Y - previous.Y) < 250) from = previous;
            }
            remoteVehicles[player.Seat] = new RemoteVehicle(from, pose, time);
        }
    }

    private PlayerPose InterpolateRemote(RemoteVehicle vehicle)
    {
        var from = vehicle.From;
        var target = vehicle.Target;
        float t = Math.Clamp((time - vehicle.ReceivedAt) / 0.1f, 0, 1);
        float turn = (target.Heading - from.Heading + 540) % 360 - 180;
        return target with
        {
            X = from.X + (target.X - from.X) * t, Z = from.Z + (target.Z - from.Z) * t,
            Y = from.Y + (target.Y - from.Y) * t, Heading = from.Heading + turn * t,
        };
    }

    private void LeaveMultiplayer()
    {
        if (room != null) _ = room.LeaveAsync();
        if (exchangingRoom != null) _ = IgnorePendingExchange(exchangingRoom);
        room = null;
        exchangingRoom = null;
        roomFailures = 0;
        roomPlayers = [];
        remoteVehicles.Clear();
    }

    private static async Task IgnorePendingExchange(Task<RoomSnapshot> pending)
    {
        try { await pending; } catch (Exception) { }
    }

    private void DrawRemoteVehicles()
    {
        foreach (var vehicle in remoteVehicles.Values)
        {
            var pose = InterpolateRemote(vehicle);
            if (pose.Moon != onMoon || !Enum.TryParse<VehicleKind>(pose.Vehicle, out var kind) ||
                !PlayerKinds.Contains(kind)) continue;
            Vector3 pos = onMoon ? moon!.R(pose.X, pose.Y, pose.Z) : free!.R(pose.X, pose.Y, pose.Z);
            float distance = Vector3.Distance(pos, camera.Position);
            if (!(distance < 1200)) continue;
            Color color = Rgb(100, 221, 240);
            if (IsFlyer(kind))
            {
                Rlgl.PushMatrix();
                Rlgl.Translatef(pos.X, pos.Y, pos.Z);
                Rlgl.Rotatef(-pose.Heading, 0, 1, 0);
                Rlgl.Rotatef(-pose.Pitch, 1, 0, 0);
                Rlgl.Rotatef(pose.Bank, 0, 0, 1);
                Aircraft.Draw(scene, kind, color, distance, time * 600, gearDown: false);
                Rlgl.PopMatrix();
            }
            else scene.DrawVehicleAt(pos, -pose.Heading, color, kind, (time * pose.Speed) % 360, shadow: false);
        }
    }

    private void DrawRoomButton(float u, Vector2 mouse)
    {
        var button = RoomMenuButton(u);
        bool hover = !settingsOpen && Raylib.CheckCollisionPointRec(mouse, button);
        Panel(button.X, button.Y, button.Width, button.Height, hover ? Rgb(36, 113, 118) : Rgb(25, 71, 85));
        TextCentered("4　マルチプレイ", button.X + button.Width / 2, button.Y + 11 * u, 18, Color.White);
    }

    private void DrawRoomMenu(int sw, int sh, float u)
    {
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(5, 10, 20, 220));
        var panel = RoomPanel(sw, sh, u);
        Panel(panel.X, panel.Y, panel.Width, panel.Height, Rgb(20, 36, 56));
        Text("マルチプレイ　最大4人", panel.X + 24 * u, panel.Y + 25 * u, 25, Color.White);
        Text("公開アドレス / 中継URL（LANなら空欄）", panel.X + 24 * u, panel.Y + 74 * u, 15, Sub);
        Text("6文字コード / 招待リンク（参加する場合）", panel.X + 24 * u, panel.Y + 157 * u, 15, Sub);
        for (int i = 0; i < 4; i++)
        {
            var rect = RoomControl(panel, i, u);
            bool active = i < 2 ? roomEditCode == (i == 1) :
                Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), rect) && openingRoom == null;
            Panel(rect.X, rect.Y, rect.Width, rect.Height, active ? Rgb(39, 101, 116) : Rgb(29, 54, 77));
            if (i < 2)
            {
                string value = i == 0 ? relayUrl : joinCode;
                int skip = 0;
                while (skip < value.Length && Measure(value[skip..], 18).X > rect.Width - 24 * u) skip++;
                Text(value[skip..], rect.X + 12 * u, rect.Y + 11 * u, 18, Color.White);
            }
            else TextCentered(i == 2 ? "新しいルームを作る" : "ルームに参加", rect.X + rect.Width / 2,
                rect.Y + 13 * u, 17, Color.White);
        }
        TextCentered(roomMessage, sw / 2f, panel.Y + 316 * u, 15, Rgb(255, 220, 154));
        TextCentered("Ctrl+V：貼り付け　Tab：入力欄の切替　Esc：戻る", sw / 2f, panel.Y + 350 * u, 13, Sub);
    }

    private void DrawRoomStatus(int sw, int sh, float u)
    {
        if (room == null) return;
        int count = Math.Max(1, roomPlayers.Length);
        int otherWorld = roomPlayers.Count(player => player?.Pose is { } pose && pose.Moon != onMoon);
        string status = roomFailures > 0 ? "ルームに再接続中…"
            : $"ルーム {room.Ticket.Code}　{count}/{RoomProtocol.Capacity}人　" +
                (time < copiedUntil ? "コピーしました" : room.Invite != null ? "F6：コード　F7：招待リンク" : "F6：コードをコピー");
        if (otherWorld > 0) status += $"　{(onMoon ? "地球" : "月")}に {otherWorld}人";
        float width = Measure(status, 15).X + 24 * u;
        Panel((sw - width) / 2, sh - 105 * u, width, 30 * u, Rgb(20, 57, 71));
        TextCentered(status, sw / 2f, sh - 99 * u, 15, Color.White);
    }

    private void DrawRoomMapPlayers(Func<PlayerPose, Vector2> project, float u)
    {
        foreach (var (seat, vehicle) in remoteVehicles)
        {
            var pose = InterpolateRemote(vehicle);
            if (pose.Moon != onMoon) continue;
            var point = project(pose);
            int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
            if (!(point.X >= 0 && point.X <= sw && point.Y >= 0 && point.Y <= sh)) continue;
            Raylib.DrawCircleV(point, 6 * u, Rgb(90, 225, 250));
            Text($"P{seat}", point.X + 8 * u, point.Y - 7 * u, 12, Color.White);
        }
    }
}
