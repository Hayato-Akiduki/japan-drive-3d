namespace JapanDrive;

internal sealed record TourRoad(string Name, string From, string To)
{
    public static TourRoad? Capture(Road? road, City? from = null) => road == null
        ? null : new(road.Name, (from ?? road.A).Name, road.Other(from ?? road.A).Name);

    public Road? Resolve() => TryFindCity(From, out var from) && TryFindCity(To, out var to)
        ? from.Roads.Find(road => road.Name == Name && road.Other(from) == to) : null;

    internal static bool TryFindCity(string? name, out City city)
    {
        city = null!;
        return !string.IsNullOrEmpty(name) && MapData.TryGet(name, out city) && city.Name == name;
    }
}

internal sealed record TourProgress(
    string City, TourRoad? Road, TourRoad? LastRoad, TourRoad? PreferredNext,
    double Traveled, double Fuel, double GameMinutes, double Odometer,
    int TollPaid, int FuelPaid, int FinePaid, int RescuePaid, int RepairPaid,
    int Crashes, int Violations, string[] Visited, string? NavTarget,
    bool FreeDrive, bool AutoDrive, string Vehicle);

public sealed partial class Game
{
    internal TourProgress CaptureTour(string vehicle) => new(
        JunctionCity.Name, TourRoad.Capture(Road, From), TourRoad.Capture(LastRoad),
        TourRoad.Capture(PreferredNext), Traveled, Fuel, GameMinutes, Odometer,
        TollPaid, FuelPaid, FinePaid, RescuePaid, RepairPaid, Crashes, Violations,
        Visited.Select(city => city.Name).ToArray(), NavTarget?.Name, FreeDrive, AutoDrive, vehicle);

    internal bool RestoreTour(TourProgress progress)
    {
        if (!TourRoad.TryFindCity(progress.City, out var city)) return false;
        var road = progress.Road?.Resolve();
        City departure = city;
        if (progress.Road is { } savedRoad &&
            (road == null || !TourRoad.TryFindCity(savedRoad.From, out departure) || road.Other(departure) != city))
            return false;
        if (!double.IsFinite(progress.Traveled) || progress.Traveled < 0 ||
            progress.Traveled > (road?.Km ?? 0) ||
            !double.IsFinite(progress.Fuel) || progress.Fuel < 0 || progress.Fuel > TankSize ||
            !double.IsFinite(progress.GameMinutes) || progress.GameMinutes < 0 ||
            !double.IsFinite(progress.Odometer) || progress.Odometer < 0)
            return false;

        AtCity = road == null ? city : null;
        Road = road;
        From = departure;
        To = city;
        LastRoad = progress.LastRoad?.Resolve();
        PreferredNext = progress.PreferredNext?.Resolve();
        if (PreferredNext != null && !city.Roads.Contains(PreferredNext)) PreferredNext = null;
        Traveled = progress.Traveled;
        Fuel = progress.Fuel;
        GameMinutes = progress.GameMinutes;
        Odometer = progress.Odometer;
        TollPaid = progress.TollPaid;
        FuelPaid = progress.FuelPaid;
        FinePaid = progress.FinePaid;
        RescuePaid = progress.RescuePaid;
        RepairPaid = progress.RepairPaid;
        Crashes = progress.Crashes;
        Violations = progress.Violations;
        foreach (string name in progress.Visited ?? [])
            if (TourRoad.TryFindCity(name, out var visited) && visited.IsGoal && visited != StartCity)
                Visited.Add(visited);
        if (TourRoad.TryFindCity(progress.NavTarget, out var destination)) NavTarget = destination;
        FreeDrive = progress.FreeDrive;
        AutoDrive = progress.AutoDrive;
        Log.Clear();
        AddLog("日本一周を再開しました。");
        return true;
    }
}
