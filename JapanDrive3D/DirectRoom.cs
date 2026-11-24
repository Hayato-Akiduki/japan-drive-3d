using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JapanDrive.Multiplayer;

namespace JapanDrive;

internal static class DirectRoom
{
    public const int Port = 5081;
    private const int MaxFrame = 8192;
    private const string Scheme = "japandrive";
    private static readonly byte[] DiscoveryReply = "JDR1"u8.ToArray();

    private sealed record Request(string? Code, string Action, Guid Token = default, PlayerPose? Pose = null);
    private sealed record Response(int Status, RoomTicket? Ticket = null, RoomSnapshot? Snapshot = null);

    public static bool TryInvite(string text, out Uri invite)
    {
        invite = null!;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Scheme ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
            uri.Port is <= 0 or > 65535 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0 || !RoomProtocol.IsCode(uri.AbsolutePath.TrimStart('/').ToUpperInvariant()) ||
            uri.AbsolutePath.Length != RoomProtocol.CodeLength + 1) return false;
        invite = uri;
        return true;
    }

    public static bool TryHostAddress(string address) => TryHostInvite(address, "AAAAAA", out _);

    private static bool TryHostInvite(string address, string code, out Uri invite) =>
        TryInvite($"{Scheme}://{address.Trim()}/{code}", out invite) ||
        TryInvite($"{Scheme}://{address.Trim()}:{Port}/{code}", out invite);

    public static Task<IRoomConnection> HostAsync(string advertisedAddress, RoomMode mode = RoomMode.Free) => Task.Run<IRoomConnection>(() =>
    {
        string code = RandomNumberGenerator.GetString(RoomProtocol.Alphabet, RoomProtocol.CodeLength);
        string endpoint = advertisedAddress.Trim();
        if (endpoint.Length == 0) return new Host(code, null, mode);
        if (!TryHostInvite(endpoint, code, out var invite)) throw new InvalidDataException();
        return new Host(code, invite.AbsoluteUri, mode);
    });

    public static async Task<IRoomConnection> JoinAsync(string input, string address = "")
    {
        string code;
        string host;
        int port;
        if (TryInvite(input, out var invite))
        {
            code = invite.AbsolutePath[1..].ToUpperInvariant();
            host = invite.IdnHost;
            port = invite.Port;
        }
        else
        {
            code = input.Trim().ToUpperInvariant();
            if (!RoomProtocol.IsCode(code)) throw new InvalidDataException();
            if (address.Length != 0)
            {
                if (!TryHostInvite(address, code, out var endpoint)) throw new InvalidDataException();
                host = endpoint.IdnHost;
                port = endpoint.Port;
            }
            else
            {
                host = (await DiscoverAsync(code)).ToString();
                port = Port;
            }
        }
        var response = await SendAsync(host, port, new Request(code, "join"));
        var ticket = response.Ticket;
        if (ticket == null || ticket.Code != code || ticket.Token == Guid.Empty ||
            ticket.Seat is < 2 or > RoomProtocol.Capacity || !Enum.IsDefined(ticket.Mode))
            throw new InvalidDataException();
        return new Guest(host, port, ticket);
    }

    private static async Task<IPAddress> DiscoverAsync(string code)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var query = Encoding.ASCII.GetBytes($"JDR1/{code}");
        var destinations = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                byte[] ip = address.Address.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < ip.Length; i++) ip[i] |= (byte)~mask[i];
                destinations.Add(new IPAddress(ip));
            }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var retry = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var sending = SendDiscoveryQueriesAsync(socket, query, destinations, retry.Token);
            try
            {
                while (true)
                {
                    UdpReceiveResult result;
                    try { result = await socket.ReceiveAsync(timeout.Token); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    if (result.RemoteEndPoint.Port == Port && result.Buffer.AsSpan().SequenceEqual(DiscoveryReply))
                        return result.RemoteEndPoint.Address;
                }
            }
            finally
            {
                retry.Cancel();
                await sending;
            }
        }
        catch (OperationCanceledException)
        {
            throw new HttpRequestException("同じLAN内にルームが見つかりません。コードと接続先を確認してください。",
                null, HttpStatusCode.NotFound);
        }
    }

    private static async Task SendDiscoveryQueriesAsync(UdpClient socket, byte[] query,
        IEnumerable<IPAddress> destinations, CancellationToken token)
    {
        try
        {
            while (true)
            {
                foreach (var address in destinations)
                {
                    try { await socket.SendAsync(query, new IPEndPoint(address, Port), token); }
                    catch (SocketException) { }
                }
                await Task.Delay(650, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<Response> SendAsync(string host, int port, Request request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await tcp.ConnectAsync(host, port, timeout.Token);
        using var stream = tcp.GetStream();
        await WriteAsync(stream, request, timeout.Token);
        var response = await ReadAsync<Response>(stream, timeout.Token);
        if (response.Status == 200) return response;
        string message = response.Status switch
        {
            404 => "ルームが見つかりません。ホスト名とコードを確認してください。",
            409 => "ルームが満員です（最大4人）。",
            423 => "試合中です。終了後に参加してください。",
            401 => "ホストとの接続が切れました。",
            _ => "ホストからエラーが返りました。",
        };
        throw new HttpRequestException(message, null, (HttpStatusCode)response.Status);
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] packet = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, json.Length);
        json.CopyTo(packet, 4);
        await stream.WriteAsync(packet, token);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrame) throw new InvalidDataException();
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token);
        return JsonSerializer.Deserialize<T>(buffer) ?? throw new InvalidDataException();
    }

    private sealed class Guest(string host, int port, RoomTicket ticket) : IRoomConnection
    {
        public RoomTicket Ticket { get; } = ticket;
        public string? Invite => null;

        public async Task<RoomSnapshot> ExchangeAsync(PlayerPose pose)
        {
            var response = await SendAsync(host, port, new Request(Ticket.Code, "pose", Ticket.Token, pose));
            if (response.Snapshot == null || !RoomProtocol.ValidSnapshot(response.Snapshot))
                throw new InvalidDataException();
            return response.Snapshot;
        }

        public async Task LeaveAsync()
        {
            try { await SendAsync(host, port, new Request(Ticket.Code, "leave", Ticket.Token)); }
            catch (Exception e) when (e is SocketException or IOException or InvalidDataException or
                OperationCanceledException or HttpRequestException or JsonException) { }
        }
    }

    private sealed class Host : IRoomConnection
    {
        private sealed class Member(RoomTicket ticket)
        {
            public RoomTicket Ticket { get; } = ticket;
            public long LastSeen { get; set; } = Environment.TickCount64;
            public PlayerPose? Pose { get; set; }
        }

        private readonly object gate = new();
        private readonly List<Member> members;
        private readonly RoomMatch match;
        private readonly TcpListener listener = new(IPAddress.Any, Port);
        private readonly UdpClient discovery;
        private readonly CancellationTokenSource close = new();
        private readonly SemaphoreSlim clientSlots = new(12);
        private readonly Task acceptLoop, discoveryLoop;
        public RoomTicket Ticket { get; }
        public string? Invite { get; }

        public Host(string code, string? invite, RoomMode mode)
        {
            Ticket = new RoomTicket(code, Guid.NewGuid(), 1, mode);
            match = new RoomMatch(mode);
            Invite = invite;
            members = [new Member(Ticket)];
            listener.Start(20);
            try { discovery = new UdpClient(new IPEndPoint(IPAddress.Any, Port)); }
            catch
            {
                listener.Stop();
                throw;
            }
            acceptLoop = AcceptAsync(close.Token);
            discoveryLoop = AnswerDiscoveryAsync(close.Token);
        }

        public Task<RoomSnapshot> ExchangeAsync(PlayerPose pose)
        {
            var snapshot = Handle(new Request(Ticket.Code, "pose", Ticket.Token, pose)).Snapshot;
            return snapshot == null ? Task.FromException<RoomSnapshot>(new InvalidDataException()) : Task.FromResult(snapshot);
        }

        public async Task LeaveAsync()
        {
            close.Cancel();
            listener.Stop();
            discovery.Dispose();
            await Task.WhenAll(acceptLoop, discoveryLoop);
            close.Dispose();
        }

        private async Task AcceptAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await clientSlots.WaitAsync(token);
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = ServeAsync(client, token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    client.NoDelay = true;
                    using var stream = client.GetStream();
                    var request = await ReadAsync<Request>(stream, timeout.Token);
                    var response = request.Token == Ticket.Token ? new Response(401) : Handle(request);
                    await WriteAsync(stream, response, timeout.Token);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or SocketException or JsonException or OperationCanceledException) { }
            finally { clientSlots.Release(); }
        }

        private async Task AnswerDiscoveryAsync(CancellationToken token)
        {
            var query = Encoding.ASCII.GetBytes($"JDR1/{Ticket.Code}");
            try
            {
                while (true)
                {
                    var packet = await discovery.ReceiveAsync(token);
                    if (packet.Buffer.AsSpan().SequenceEqual(query))
                        await discovery.SendAsync(DiscoveryReply, packet.RemoteEndPoint, token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private Response Handle(Request request)
        {
            if (request.Code != Ticket.Code) return new Response(404);
            lock (gate)
            {
                members.RemoveAll(member => member.Ticket.Seat != 1 && Environment.TickCount64 - member.LastSeen > 30_000);
                if (request.Action == "join")
                {
                    if (match.Locked) return new Response(423);
                    if (members.Count >= RoomProtocol.Capacity) return new Response(409);
                    int seat = Enumerable.Range(2, RoomProtocol.Capacity - 1).First(n => members.All(m => m.Ticket.Seat != n));
                    var guest = new RoomTicket(Ticket.Code, Guid.NewGuid(), seat, Ticket.Mode);
                    members.Add(new Member(guest));
                    return new Response(200, Ticket: guest);
                }
                var member = members.Find(m => m.Ticket.Token == request.Token);
                if (member == null) return new Response(401);
                if (request.Action == "leave")
                {
                    members.Remove(member);
                    return new Response(200);
                }
                if (request.Action != "pose" || request.Pose is not { } pose || !RoomProtocol.ValidPose(pose))
                    return new Response(400);
                member.LastSeen = Environment.TickCount64;
                member.Pose = pose;
                var players = members.Select(m => new RoomPlayer(m.Ticket.Seat, m.Pose)).ToArray();
                return new Response(200, Snapshot: match.Update(players, member.Ticket.Seat, pose, Environment.TickCount64));
            }
        }
    }
}
