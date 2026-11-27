using System.Numerics;
using JapanDrive.Multiplayer;
using Raylib_cs;
using static JapanDrive.Draw;

namespace JapanDrive;

public sealed partial class App
{
    private bool courseEditing;
    private bool courseDraftMoon;
    private readonly List<CoursePoint> courseDraft = new();

    private static string RoomModeName(RoomMode mode) => mode switch
    {
        RoomMode.Control => "対戦（陣取り）",
        RoomMode.Race => "レース",
        RoomMode.Tag => "鬼ごっこ",
        _ => "フリー走行",
    };

    private static string RoomModeLabel(RoomMode mode, RaceCourse? course) => mode switch
    {
        RoomMode.Race when course != null => $"レース（自作・{(course.Moon ? "月面" : "地球")}）",
        _ => RoomModeName(mode),
    };

    private static bool SameCourse(RaceCourse? a, RaceCourse? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Moon != b.Moon || a.Points.Length != b.Points.Length) return false;
        for (int i = 0; i < a.Points.Length; i++)
            if (Math.Abs(a.Points[i].X - b.Points[i].X) > 0.01 || Math.Abs(a.Points[i].Z - b.Points[i].Z) > 0.01)
                return false;
        return true;
    }

    private void CycleRoomMode()
    {
        if (!CanEditRoom) return;
        var next = (RoomMode)(((int)roomMode + 1) % 4);
        QueueRoomConfig(next, updateCourse: false);
        ApplyRoomState(next, roomCourse, roomRevision, announce: true);
    }

    private void QueueRoomConfig(RoomMode nextMode, bool updateCourse, RaceCourse? nextCourse = null)
    {
        if (!CanEditRoom) return;
        pendingRoomConfig = new RoomConfig(nextMode, nextCourse, updateCourse);
        Audio.Play(Audio.Select, 0.85f);
    }

    private void ApplyRoomState(RoomMode nextMode, RaceCourse? nextCourse, int revision, bool announce)
    {
        bool modeChanged = nextMode != roomMode;
        bool courseChanged = !SameCourse(nextCourse, roomCourse);
        bool prevNeedMoon = RoomMatch.MatchUsesMoon(roomMode, roomCourse);
        roomMode = nextMode;
        roomCourse = nextCourse;
        roomRevision = Math.Max(roomRevision, revision);

        if (modeChanged || (roomMode == RoomMode.Race && courseChanged))
            roomRound = null;

        bool nextNeedMoon = RoomMatch.MatchUsesMoon(roomMode, roomCourse);
        if (CompetitiveRoom)
        {
            if (modeChanged || (roomMode == RoomMode.Race && (courseChanged || prevNeedMoon != nextNeedMoon)))
                PrepareMatchArena();
        }
        if (announce)
        {
            if (modeChanged)
                game.AddLog($"ルームのモードが「{RoomModeLabel(roomMode, roomCourse)}」に変わりました。");
            else if (courseChanged)
                game.AddLog(roomCourse == null
                    ? "レースコースが既定の月面コースに戻りました。"
                    : $"新しい自作コース（{(roomCourse.Moon ? "月面" : "地球")}・全{roomCourse.Points.Length}地点）が共有されました。");
        }
    }

    private void PrepareMatchArena()
    {
        if (room == null) return;
        playerKind = VehicleKind.Sports;
        ApplyVehicle();
        bool needMoon = RoomMatch.MatchUsesMoon(roomMode, roomCourse);
        if (needMoon && !onMoon) EnterMoon(landed: true);
        else if (!needMoon && onMoon) ReturnToEarth();
        SpawnMatchVehicle(room.Ticket.Seat);
    }

    private void SpawnMatchVehicle(int seat)
    {
        bool needMoon = RoomMatch.MatchUsesMoon(roomMode, roomCourse);
        var (sx, sz, shdg) = roomMode == RoomMode.Race
            ? RoomMatch.RaceSpawn(roomCourse, seat)
            : (RoomMatch.SpawnX(seat), RoomMatch.SpawnZ, 0f);
        if (needMoon)
        {
            if (moon is not { } arena) return;
            arena.PX = sx;
            arena.PZ = sz;
            freeY = groundLevel = arena.SurfaceY(arena.PX, arena.PZ);
            arena.Altitude = altitude = freeKmh = carVy = 0;
            pitch = bank = terrainPitch = terrainBank = steerYaw = 0;
            isDrifting = carAir = prevAirborne = false;
            manualGear = 1;
            heading = shdg;
            UpdateCamera(0);
            return;
        }
        if (free is not { } world) return;
        world.PX = sx;
        world.PZ = sz;
        heading = shdg;
        world.Cars.Clear();
        world.UpdateNear();
        float surf = world.SurfaceY(sx, sz);
        bool inWater = !world.OnRoad(sx, sz) && world.IsInWater(sx, sz);
        freeOnWater = inWater;
        freeY = groundLevel = inWater ? Math.Max(surf, FreeWorld.WaterY - 0.68f) : surf;
        world.Altitude = altitude = freeKmh = carVy = 0;
        pitch = bank = terrainPitch = terrainBank = steerYaw = 0;
        isDrifting = carAir = prevAirborne = false;
        manualGear = 1;
        UpdateCamera(0);
    }

    private (double X, double Z, int Index, int Total)? ActiveRaceTarget()
    {
        if (room == null || roomMode != RoomMode.Race) return null;
        if (RoomMatch.RaceWorldIsMoon(roomCourse) != onMoon) return null;
        var gates = RoomMatch.RaceCheckpoints(roomCourse);
        if (gates.Length == 0) return null;
        int gate = 0;
        if (roomRound is { Phase: RoundPhase.Playing or RoundPhase.Countdown } round)
        {
            var score = round.Scores.FirstOrDefault(entry => entry.Seat == room.Ticket.Seat);
            gate = (int)(score?.Points ?? 0);
        }
        if (gate < 0 || gate >= gates.Length) return null;
        return (gates[gate].X, gates[gate].Z, gate + 1, gates.Length);
    }

    private (double X, double Z)? MatchTarget()
    {
        if (room == null) return null;
        if (roomMode == RoomMode.Control)
            return onMoon && roomRound is { Phase: RoundPhase.Playing or RoundPhase.Countdown } ? RoomMatch.ControlPoint : null;
        if (roomMode == RoomMode.Race && roomRound is { Phase: RoundPhase.Playing or RoundPhase.Countdown } &&
            ActiveRaceTarget() is { } target)
            return (target.X, target.Z);
        return null;
    }

    private void DrawMoonMatchBeacon(MoonWorld arena)
    {
        if (MatchTarget() is { } target)
        {
            float ground = arena.SurfaceY(target.X, target.Z);
            var center = arena.R(target.X, ground + 1, target.Z);
            float radius = roomMode == RoomMode.Control ? (float)RoomMatch.ControlRadius : (float)RoomMatch.RaceGateRadius(roomCourse);
            Raylib.DrawCircle3D(center, radius, Vector3.UnitX, 90, Orange);
            Raylib.DrawCylinder(center, 0.7f, 1.6f, 17, 8, Orange);
            Raylib.DrawSphereEx(center + Vector3.UnitY * 17, 2.5f, 8, 10, Rgb(255, 219, 115));
        }
        var preview = VisibleCoursePoints(moonMap: true);
        if (preview.Length == 0) return;
        for (int i = 0; i < preview.Length; i++)
        {
            var pt = preview[i];
            if (MatchTarget() is { } active && Math.Abs(pt.X - active.X) < 1 && Math.Abs(pt.Z - active.Z) < 1) continue;
            float g = arena.SurfaceY(pt.X, pt.Z);
            var c = arena.R(pt.X, g + 0.6f, pt.Z);
            Color col = i == 0 ? Rgb(105, 235, 165) : Rgb(120, 210, 255);
            Raylib.DrawCylinder(c, 0.45f, 0.85f, 10, 6, col);
            Raylib.DrawSphereEx(c + Vector3.UnitY * 10.5f, 1.4f, 6, 8, col);
        }
    }

    private void DrawEarthMatchBeacon(FreeWorld world)
    {
        var active = ActiveRaceTarget();
        if (active is { } target)
        {
            float ground = Math.Max(world.SurfaceY(target.X, target.Z), FreeWorld.WaterY);
            var center = world.R(target.X, ground + 1.5f, target.Z);
            float radius = (float)RoomMatch.RaceGateRadius(roomCourse);
            Raylib.DrawCircle3D(center, radius, Vector3.UnitX, 90, Orange);
            Raylib.DrawCircle3D(center + Vector3.UnitY * 18, radius * 0.7f, Vector3.UnitX, 90, Rgb(255, 215, 115));
            Raylib.DrawCylinder(center, 1.8f, 3.6f, 95, 8, new Color((byte)255, (byte)155, (byte)35, (byte)215));
            Raylib.DrawSphereEx(center + Vector3.UnitY * 98, 6.5f, 8, 10, Rgb(255, 225, 120));
        }
        var preview = VisibleCoursePoints(moonMap: false);
        for (int i = 0; i < preview.Length; i++)
        {
            var pt = preview[i];
            if (active is { } t && Math.Abs(pt.X - t.X) < 1 && Math.Abs(pt.Z - t.Z) < 1) continue;
            float g = Math.Max(world.SurfaceY(pt.X, pt.Z), FreeWorld.WaterY);
            var c = world.R(pt.X, g + 1.0f, pt.Z);
            if (c.LengthSquared() > 12000f * 12000f) continue;
            Color col = i == 0 ? Rgb(105, 235, 165) : Rgb(115, 215, 255);
            Raylib.DrawCylinder(c, 1.0f, 2.0f, 45, 6, col);
            Raylib.DrawSphereEx(c + Vector3.UnitY * 47, 3.6f, 6, 8, col);
        }
    }

    private CoursePoint[] VisibleCoursePoints(bool moonMap)
    {
        if (room == null) return [];
        if (courseEditing && courseDraftMoon == moonMap) return courseDraft.ToArray();
        if (courseDraft.Count > 0 && courseDraftMoon == moonMap && IsRoomHost && !RoomLocked && roomCourse == null)
            return courseDraft.ToArray();
        if (roomCourse is { } custom && custom.Moon == moonMap) return custom.Points;
        if (moonMap && roomMode == RoomMode.Race && roomCourse == null)
        {
            var list = new List<CoursePoint>(RoomMatch.RaceGates.Length + 1) { new(0, RoomMatch.SpawnZ) };
            list.AddRange(RoomMatch.RaceGates.Select(g => new CoursePoint(g.X, g.Z)));
            return list.ToArray();
        }
        return [];
    }

    private void BeginCourseEdit(bool moonMap)
    {
        if (!CanEditRoom) return;
        courseEditing = true;
        courseDraftMoon = moonMap;
        courseDraft.Clear();
        if (roomCourse is { } existing && existing.Moon == moonMap)
            courseDraft.AddRange(existing.Points);
        Audio.Play(Audio.Select, 0.8f);
    }

    private bool TryAddCoursePoint(double x, double z, bool moonMap)
    {
        if (!CanEditRoom) return false;
        if (courseDraft.Count >= RoomProtocol.MaxCoursePoints)
        {
            game.AddLog($"チェックポイントは最大 {RoomProtocol.MaxCoursePoints} か所までです。");
            return false;
        }
        if (moonMap && x * x + z * z > MoonWorld.AreaRadius * MoonWorld.AreaRadius)
        {
            game.AddLog("月面探索エリア（橙の円）の内側を選んでください。");
            return false;
        }
        if (courseDraft.Count > 0)
        {
            var prev = courseDraft[^1];
            double dx = x - prev.X, dz = z - prev.Z;
            if (dx * dx + dz * dz < 25.0)
            {
                game.AddLog("直前の地点から 5m 以上離して置いてください。");
                return false;
            }
        }
        courseDraft.Add(new CoursePoint(Math.Round(x, 1), Math.Round(z, 1)));
        Audio.Play(Audio.Select, 0.95f, 1.05f);
        return true;
    }

    private void AddCurrentPositionToCourse()
    {
        if (!CanEditRoom) return;
        double x = onMoon ? moon!.PX : free!.PX;
        double z = onMoon ? moon!.PZ : free!.PZ;
        if (courseDraft.Count == 0 || courseDraftMoon != onMoon)
        {
            courseDraft.Clear();
            courseDraftMoon = onMoon;
        }
        if (!TryAddCoursePoint(x, z, onMoon)) return;
        string area = onMoon ? "月面" : "地球";
        string hint = courseDraft.Count >= RoomProtocol.MinCoursePoints
            ? "Shift+F10（または M の地図）でコース確定・共有できます。"
            : "あと1か所以上追加するとコースを確定できます。";
        game.AddLog($"コース地点 #{courseDraft.Count} を追加（{area}・{courseDraft.Count}/{RoomProtocol.MaxCoursePoints}）。{hint}");
    }

    private void CommitCourseDraft()
    {
        if (!CanEditRoom) return;
        if (courseDraft.Count < RoomProtocol.MinCoursePoints)
        {
            game.AddLog($"コースは最低 {RoomProtocol.MinCoursePoints} 地点（スタート＋ゴール）必要です。");
            return;
        }
        var course = new RaceCourse(courseDraftMoon, courseDraft.ToArray());
        if (!RoomProtocol.ValidCourse(course))
        {
            game.AddLog("コースの地点が範囲外か近すぎます。配置を確認してください。");
            return;
        }
        courseEditing = false;
        QueueRoomConfig(roomMode, updateCourse: true, nextCourse: course);
        ApplyRoomState(roomMode, course, roomRevision, announce: false);
        string area = course.Moon ? "月面" : "地球";
        game.AddLog(roomMode == RoomMode.Race
            ? $"自作コース（{area}・全{course.Points.Length}地点）を共有しました！ 2人以上で F8 を押すとレース開始です。"
            : $"自作コース（{area}・全{course.Points.Length}地点）を共有しました！ F9 で「レース」に切り替えて F8 で開始できます。");
    }

    private void ResetCustomCourse()
    {
        if (!CanEditRoom) return;
        courseEditing = false;
        courseDraft.Clear();
        QueueRoomConfig(roomMode, updateCourse: true, nextCourse: null);
        ApplyRoomState(roomMode, null, roomRevision, announce: false);
        game.AddLog("レースコースを既定の月面コースに戻しました。");
    }

    private void DrawCourseOnMap(Func<double, double, Vector2> toScreen, bool moonMap, float u, bool labels)
    {
        var points = VisibleCoursePoints(moonMap);
        if (points.Length == 0) return;

        Color lineCol = courseEditing ? Rgb(255, 215, 95) : Rgb(255, 165, 55);
        for (int i = 0; i + 1 < points.Length; i++)
        {
            var a = toScreen(points[i].X, points[i].Z);
            var b = toScreen(points[i + 1].X, points[i + 1].Z);
            Raylib.DrawLineEx(a, b, (labels ? 3.2f : 2.0f) * u, lineCol);
        }

        var active = ActiveRaceTarget();
        for (int i = 0; i < points.Length; i++)
        {
            var p = toScreen(points[i].X, points[i].Z);
            bool isStart = i == 0;
            bool isGoal = i == points.Length - 1 && points.Length >= 2;
            bool isCurrentTarget = active is { } t && Math.Abs(points[i].X - t.X) < 1 && Math.Abs(points[i].Z - t.Z) < 1;
            Color fill = isCurrentTarget ? Orange : isStart ? Rgb(80, 220, 145) : isGoal ? Rgb(255, 105, 95) : Rgb(255, 195, 85);
            float r = (labels ? (isCurrentTarget ? 11f : 9f) : (isCurrentTarget ? 5.5f : 4f)) * u;
            Raylib.DrawCircleV(p, r, fill);
            if (isCurrentTarget)
                Raylib.DrawCircleLines((int)p.X, (int)p.Y, r + 4 * u, Color.White);
            if (labels)
            {
                string tag = isStart ? "S" : isGoal ? "G" : $"{i}";
                TextCentered(tag, p.X, p.Y - 6.5f * u, 13, Rgb(15, 22, 34));
            }
        }
    }

    private static Rectangle[] CourseEditorButtons(int sw, int sh, float u, bool editing, bool hasCourse, bool moonMap)
    {
        float y = moonMap ? sh - 60 * u : 92 * u;
        float h = 36 * u;
        float gap = 10 * u;
        if (editing)
        {
            float[] widths = [135 * u, 105 * u, 185 * u, 130 * u];
            float total = widths.Sum() + gap * (widths.Length - 1);
            float x = (sw - total) / 2f;
            var rects = new Rectangle[widths.Length];
            for (int i = 0; i < widths.Length; i++)
            {
                rects[i] = new Rectangle(x, y, widths[i], h);
                x += widths[i] + gap;
            }
            return rects;
        }
        else
        {
            float[] widths = hasCourse ? [185 * u, 210 * u, 175 * u] : [185 * u, 210 * u];
            float total = widths.Sum() + gap * (widths.Length - 1);
            float x = (sw - total) / 2f;
            var rects = new Rectangle[widths.Length];
            for (int i = 0; i < widths.Length; i++)
            {
                rects[i] = new Rectangle(x, y, widths[i], h);
                x += widths[i] + gap;
            }
            return rects;
        }
    }

    private bool HandleCourseEditorInput(int sw, int sh, float u, bool moonMap,
        bool clickedLeft, bool clickedRight, Func<Vector2, (double X, double Z)?> screenToWorld)
    {
        if (!CanEditRoom) return false;
        if (Raylib.IsKeyPressed(KeyboardKey.K))
        {
            if (courseEditing) courseEditing = false;
            else BeginCourseEdit(moonMap);
            return true;
        }
        if (courseEditing)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && courseDraft.Count > 0)
            {
                courseDraft.RemoveAt(courseDraft.Count - 1);
                return true;
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            {
                CommitCourseDraft();
                return true;
            }
        }

        var buttons = CourseEditorButtons(sw, sh, u, courseEditing, roomCourse != null, moonMap);
        var mouse = Raylib.GetMousePosition();
        if (clickedLeft)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                if (!Raylib.CheckCollisionPointRec(mouse, buttons[i])) continue;
                if (courseEditing)
                {
                    if (i == 0 && courseDraft.Count > 0) courseDraft.RemoveAt(courseDraft.Count - 1);
                    else if (i == 1) courseDraft.Clear();
                    else if (i == 2) CommitCourseDraft();
                    else if (i == 3) courseEditing = false;
                }
                else
                {
                    if (i == 0) BeginCourseEdit(moonMap);
                    else if (i == 1) CycleRoomMode();
                    else if (i == 2) ResetCustomCourse();
                }
                return true;
            }
        }

        if (!courseEditing) return false;
        if (clickedLeft && screenToWorld(mouse) is (double wx, double wz))
        {
            TryAddCoursePoint(wx, wz, moonMap);
            return true;
        }
        if (clickedRight && courseDraft.Count > 0)
        {
            courseDraft.RemoveAt(courseDraft.Count - 1);
            return true;
        }
        return false;
    }

    private void DrawCourseEditorBar(int sw, int sh, float u, bool moonMap)
    {
        if (!CanEditRoom) return;
        var buttons = CourseEditorButtons(sw, sh, u, courseEditing, roomCourse != null, moonMap);
        var mouse = Raylib.GetMousePosition();
        string info = courseEditing
            ? $"コース作成中（{(moonMap ? "月面" : "地球")}：{courseDraft.Count}/{RoomProtocol.MaxCoursePoints}地点）　左クリック：地点追加　右クリック：1つ戻す"
            : $"ホスト操作：現在のモード「{RoomModeLabel(roomMode, roomCourse)}」　コースを作って全員と競えます";
        float infoW = Measure(info, 14).X + 28 * u;
        float infoY = moonMap ? sh - 98 * u : 56 * u;
        Panel((sw - infoW) / 2f, infoY, infoW, 30 * u, new Color(14, 30, 50, 225));
        TextCentered(info, sw / 2f, infoY + 7 * u, 14, courseEditing ? Rgb(255, 224, 130) : Rgb(185, 225, 250));

        string[] labels = courseEditing
            ? ["1つ戻す (BS)", "全消去", "確定して共有 (Enter)", "キャンセル (Esc)"]
            : roomCourse != null
                ? ["コースを作る (K)", $"モード切替 (F9)", "既定コースに戻す"]
                : ["コースを作る (K)", $"モード切替 (F9)"];
        for (int i = 0; i < buttons.Length; i++)
        {
            var r = buttons[i];
            bool enabled = !(courseEditing && i == 2 && courseDraft.Count < RoomProtocol.MinCoursePoints);
            bool hover = enabled && Raylib.CheckCollisionPointRec(mouse, r);
            Color bg = !enabled ? Rgb(35, 45, 58)
                : courseEditing && i == 2 ? (hover ? Rgb(215, 130, 25) : Rgb(175, 100, 20))
                : hover ? Rgb(46, 112, 132) : Rgb(26, 56, 82);
            Panel(r.X, r.Y, r.Width, r.Height, bg);
            TextCentered(labels[i], r.X + r.Width / 2, r.Y + 9 * u, 15, enabled ? Color.White : Sub);
        }
    }

    private void DrawMatchStatus(int sw, int sh, float u)
    {
        var ticket = room!.Ticket;
        var mode = roomMode;
        var gates = RoomMatch.RaceCheckpoints(roomCourse);
        string rule = mode switch
        {
            RoomMode.Control => "オレンジの陣地を1人で確保すると得点",
            RoomMode.Race => $"オレンジの目印を順番に通過（全{gates.Length}か所）",
            _ => "鬼は相手に近づくと交代。逃げた時間で競う",
        };
        var round = roomRound;
        bool canStart = RoomMatch.CanStart(roomPlayers, mode, roomCourse);
        string status = round?.Phase switch
        {
            RoundPhase.Countdown => $"スタートまで {Math.Max(1, Math.Ceiling(round.Seconds)):0}",
            RoundPhase.Playing => $"残り {Math.Max(0, Math.Ceiling(round.Seconds)):0} 秒" +
                (mode == RoomMode.Tag ? round.It == ticket.Seat ? "　あなたが鬼です" : $"　鬼：P{round.It}" : ""),
            RoundPhase.Finished => (round.Winners.Length == 1 ? $"勝者：P{round.Winners[0]}"
                : round.Winners.Length > 1 ? $"同点：{string.Join("・", round.Winners.Select(seat => $"P{seat}"))}" : "試合終了") +
                (IsRoomHost && canStart ? "　F8：再戦" : ""),
            _ => IsRoomHost && canStart
                ? "F8：試合開始" : "参加者とホストの開始操作を待っています",
        };
        string scores = round == null || round.Scores.Length == 0 ? rule
            : string.Join("　", round.Scores.Select(score =>
                $"P{score.Seat}{(score.Seat == ticket.Seat ? "（自分）" : "")} " +
                (mode == RoomMode.Race ? $"{score.Points:0}/{gates.Length}" : $"{score.Points:0.0}秒")));
        string header = $"{RoomModeLabel(mode, roomCourse)}　｜　{status}";
        float width = Math.Max(Measure(header, 17).X, Measure(scores, 14).X) + 30 * u;
        Panel((sw - width) / 2, sh - 173 * u, width, 62 * u, Rgb(17, 41, 64));
        TextCentered(header, sw / 2f, sh - 163 * u, 17, Color.White);
        TextCentered(scores, sw / 2f, sh - 138 * u, 14, Rgb(188, 227, 245));
    }
}
