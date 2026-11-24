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
app.MapPost("/v1/rooms", () => rooms.Create()).RequireRateLimiting("admission");
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
    private readonly Dictionary<string, List<Member>> rooms = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private sealed class Member(int seat)
    {
        public int Seat { get; } = seat;
        public Guid Token { get; } = Guid.NewGuid();
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public PlayerPose? Pose { get; set; }
    }

    public IResult Create()
    {
        lock (gate)
        {
            RemoveExpired();
            if (rooms.Count >= RoomLimit) return Results.StatusCode(503);
            string code;
            do { code = RandomNumberGenerator.GetString(RoomProtocol.Alphabet, RoomProtocol.CodeLength); }
            while (rooms.ContainsKey(code));
            var member = new Member(1);
            rooms.Add(code, [member]);
            return Results.Ok(new RoomTicket(code, member.Token, member.Seat));
        }
    }

    public IResult Join(string code)
    {
        lock (gate)
        {
            if (!TryRoom(code, out var room)) return Results.NotFound();
            if (room.Count >= RoomProtocol.Capacity) return Results.Conflict();
            int seat = Enumerable.Range(1, RoomProtocol.Capacity).First(n => room.All(member => member.Seat != n));
            var member = new Member(seat);
            room.Add(member);
            return Results.Ok(new RoomTicket(code, member.Token, seat));
        }
    }

    public IResult Exchange(string code, Guid token, PlayerPose pose)
    {
        if (!RoomProtocol.ValidPose(pose)) return Results.BadRequest();
        lock (gate)
        {
            if (!TryRoom(code, out var room)) return Results.NotFound();
            var member = room.Find(entry => entry.Token == token);
            if (member == null) return Results.Unauthorized();
            member.LastSeen = DateTimeOffset.UtcNow;
            member.Pose = pose;
            return Results.Ok(new RoomSnapshot(room.Select(entry => new RoomPlayer(entry.Seat, entry.Pose)).ToArray()));
        }
    }

    public IResult Leave(string code, Guid token)
    {
        lock (gate)
        {
            if (!TryRoom(code, out var room)) return Results.NotFound();
            if (room.RemoveAll(entry => entry.Token == token) == 0) return Results.Unauthorized();
            if (room.Count == 0) rooms.Remove(code);
            return Results.NoContent();
        }
    }

    private bool TryRoom(string code, out List<Member> room)
    {
        room = null!;
        if (!RoomProtocol.IsCode(code) || !rooms.TryGetValue(code, out var existing)) return false;
        existing.RemoveAll(member => member.LastSeen < DateTimeOffset.UtcNow - SessionTimeout);
        if (existing.Count == 0)
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
