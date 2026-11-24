using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JapanDrive.Multiplayer;

namespace JapanDrive;

internal interface IRoomConnection
{
    RoomTicket Ticket { get; }
    string? Invite { get; }
    Task<RoomSnapshot> ExchangeAsync(PlayerPose pose);
    Task LeaveAsync();
}

internal sealed class RoomConnection(Uri address, RoomTicket ticket) : IRoomConnection
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
        MaxResponseContentBufferSize = 8192,
    };

    public RoomTicket Ticket { get; } = ticket;
    public string? Invite => null;

    public static async Task<RoomConnection> OpenAsync(Uri address, string? code, RoomMode mode = RoomMode.Free)
    {
        string path = code == null ? $"v1/rooms?mode={(int)mode}" : $"v1/rooms/{code}/join";
        using var response = await Http.PostAsync(new Uri(address, path), null);
        CheckResponse(response);
        var ticket = await response.Content.ReadFromJsonAsync<RoomTicket>();
        if (ticket == null || !RoomProtocol.IsCode(ticket.Code) || code != null && code != ticket.Code ||
            ticket.Seat is < 1 or > RoomProtocol.Capacity || ticket.Token == Guid.Empty ||
            !Enum.IsDefined(ticket.Mode) || code == null && ticket.Mode != mode)
            throw new InvalidDataException("中継サーバーから無効な応答が返りました。");
        return new RoomConnection(address, ticket);
    }

    public async Task<RoomSnapshot> ExchangeAsync(PlayerPose pose)
    {
        using var request = Request(HttpMethod.Post, "pose");
        request.Content = JsonContent.Create(pose);
        using var response = await Http.SendAsync(request);
        CheckResponse(response);
        var snapshot = await response.Content.ReadFromJsonAsync<RoomSnapshot>();
        if (snapshot == null || !RoomProtocol.ValidSnapshot(snapshot))
            throw new InvalidDataException("中継サーバーから無効な応答が返りました。");
        return snapshot;
    }

    public async Task LeaveAsync()
    {
        try
        {
            using var request = Request(HttpMethod.Delete, "session");
            using var response = await Http.SendAsync(request);
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
    }

    private HttpRequestMessage Request(HttpMethod method, string action)
    {
        var request = new HttpRequestMessage(method, new Uri(address, $"v1/rooms/{Ticket.Code}/{action}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Ticket.Token.ToString());
        return request;
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string message = response.StatusCode switch
        {
            HttpStatusCode.NotFound => "ルームが見つかりません。コードとサーバーを確認してください。",
            HttpStatusCode.Conflict => "ルームが満員です（最大4人）。",
            HttpStatusCode.Locked => "試合中です。終了後に参加してください。",
            HttpStatusCode.Unauthorized => "ルームの接続が切れました。",
            HttpStatusCode.TooManyRequests => "接続の試行が多すぎます。少し待って再試行してください。",
            HttpStatusCode.ServiceUnavailable => "サーバーが混み合っています。少し待って再試行してください。",
            _ => "中継サーバーでエラーが発生しました。",
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
