using System.Security.Cryptography;
using System.Threading.RateLimiting;
using JapanDrive.Multiplayer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2048);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admission", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 40,
            Window = TimeSpan.FromMinutes(1),
        }));
});
var app = builder.Build();
var rooms = new RoomDirectory();

app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok());
app.MapPost("/v1/rooms", (int? mode) => rooms.Create((RoomMode)(mode ?? 0))).RequireRateLimiting("admission");
app.MapPost("/v1/rooms/{code}/join", (string code) => rooms.Join(code)).RequireRateLimiting("admission");
app.MapPost("/v1/rooms/{code}/pose", (string code, PlayerPose pose, HttpRequest request) =>
    rooms.Exchange(code, ReadToken(request), pose));
app.MapDelete("/v1/rooms/{code}/session", (string code, HttpRequest request) =>
    rooms.Leave(code, ReadToken(request)));
app.Run();

static Guid ReadToken(HttpRequest request)
{
    const string prefix = "Bearer ";
    var value = request.Headers.Authorization.ToString();
    return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(value[prefix.Length..], out var token) ? token : Guid.Empty;
}

internal sealed class RoomDirectory
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private const int RoomLimit = 256;
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private sealed class Member(int seat)
    {
        public int Seat { get; } = seat;
        public Guid Token { get; } = Guid.NewGuid();
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public PlayerPose? Pose { get; set; }
    }

    private sealed class Room(RoomMode mode)
    {
        public List<Member> Members { get; } = [];
        public RoomMatch Match { get; } = new(mode);
    }

    public IResult Create(RoomMode mode)
    {
        if (!Enum.IsDefined(mode)) return Results.BadRequest();
        lock (gate)
        {
            RemoveExpired();
            if (rooms.Count >= RoomLimit) return Results.StatusCode(503);
            string code;
            do { code = RandomNumberGenerator.GetString(RoomProtocol.Alphabet, RoomProtocol.CodeLength); }
            while (rooms.ContainsKey(code));
            var member = new Member(1);
            var room = new Room(mode);
            room.Members.Add(member);
            rooms.Add(code, room);
            return Results.Ok(new RoomTicket(code, member.Token, member.Seat, mode));
        }
    }

    public IResult Join(string code)
    {
        lock (gate)
        {
            if (!TryRoom(code, out var room)) return Results.NotFound();
            if (room.Match.Locked) return Results.StatusCode(423);
            if (room.Members.Count >= RoomProtocol.Capacity) return Results.Conflict();
            int seat = Enumerable.Range(1, RoomProtocol.Capacity).First(n => room.Members.All(member => member.Seat != n));
            var member = new Member(seat);
            room.Members.Add(member);
            return Results.Ok(new RoomTicket(code, member.Token, seat, room.Match.Mode));
        }
    }

    public IResult Exchange(string code, Guid token, PlayerPose pose)
    {
        if (!RoomProtocol.ValidPose(pose)) return Results.BadRequest();
        lock (gate)
        {
            if (!TryRoom(code, out var room)) return Results.NotFound();
            var member = room.Members.Find(entry => entry.Token == token);
            if (member == null) return Results.Unauthorized();
            member.LastSeen = DateTimeOffset.UtcNow;
            member.Pose = pose;
            var players = room.Members.Select(entry => new RoomPlayer(entry.Seat, entry.Pose)).ToArray();
            return Results.Ok(room.Match.Update(players, member.Seat, pose, Environment.TickCount64));
        }
    }

    public IResult Leave(string code, Guid token)
    {
        lock (gate)
        {
            if (!TryRoom(code, out var room)) return Results.NotFound();
            if (room.Members.RemoveAll(entry => entry.Token == token) == 0) return Results.Unauthorized();
            if (room.Members.Count == 0) rooms.Remove(code);
            return Results.NoContent();
        }
    }

    private bool TryRoom(string code, out Room room)
    {
        room = null!;
        if (!RoomProtocol.IsCode(code) || !rooms.TryGetValue(code, out var existing)) return false;
        existing.Members.RemoveAll(member => member.LastSeen < DateTimeOffset.UtcNow - SessionTimeout);
        if (existing.Members.Count == 0)
        {
            rooms.Remove(code);
            return false;
        }
        room = existing;
        return true;
    }

    private void RemoveExpired()
    {
        foreach (string code in rooms.Keys.ToArray()) TryRoom(code, out _);
    }

}
