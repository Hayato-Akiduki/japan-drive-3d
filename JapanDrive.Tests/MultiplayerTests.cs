using JapanDrive.Multiplayer;

namespace JapanDrive.Tests;

public class MultiplayerTests
{
    private static PlayerPose Pose(bool moon = true, string vehicle = "Sports", double x = 0, double z = 0,
        int round = 0, bool start = false, RoomConfig? config = null) =>
        new(moon, vehicle, x, z, 0, 0, 0, 0, 0, round, start, config);

    [Fact]
    public void HostCanSwitchModesWhenNotLockedAndGuestCannot()
    {
        var match = new RoomMatch(RoomMode.Free);
        RoomPlayer[] players = [new(1, Pose(moon: false)), new(2, Pose(moon: false))];

        // ゲスト（Seat 2）からのモード変更は無視される
        var guestSnap = match.Update(players, 2, Pose(moon: false, config: new RoomConfig(RoomMode.Race)), 1000);
        Assert.Equal(RoomMode.Free, guestSnap.Mode);
        Assert.Equal(0, guestSnap.Revision);

        // ホスト（Seat 1）からのモード変更は反映され、Revision が増える
        var hostSnap = match.Update(players, 1, Pose(moon: false, config: new RoomConfig(RoomMode.Race)), 1100);
        Assert.Equal(RoomMode.Race, hostSnap.Mode);
        Assert.Equal(1, hostSnap.Revision);
        Assert.NotNull(hostSnap.Match);
        Assert.Equal(RoundPhase.Waiting, hostSnap.Match!.Phase);

        // 再びフリー走行へ戻せる
        var backSnap = match.Update(players, 1, Pose(moon: true, config: new RoomConfig(RoomMode.Free)), 1200);
        Assert.Equal(RoomMode.Free, backSnap.Mode);
        Assert.Null(backSnap.Match);
        Assert.Equal(2, backSnap.Revision);
    }

    [Fact]
    public void ValidCourseEnforcesBoundsPointCountAndSpacing()
    {
        Assert.True(RoomProtocol.ValidCourse(null));
        Assert.False(RoomProtocol.ValidCourse(new RaceCourse(true, [new(0, 0)])));
        Assert.False(RoomProtocol.ValidCourse(new RaceCourse(true, [new(0, 0), new(2, 2)]))); // 5m 未満
        Assert.False(RoomProtocol.ValidCourse(new RaceCourse(true, [new(0, 0), new(950, 0)]))); // 月面エリア外
        Assert.True(RoomProtocol.ValidCourse(new RaceCourse(true, [new(0, 0), new(200, 100)])));
        Assert.True(RoomProtocol.ValidCourse(new RaceCourse(false, [new(1000, 2000), new(3500, 4200), new(1000, 2000)]))); // 周回OK

        var tooMany = Enumerable.Range(0, RoomProtocol.MaxCoursePoints + 1)
            .Select(i => new CoursePoint(i * 100, 0)).ToArray();
        Assert.False(RoomProtocol.ValidCourse(new RaceCourse(false, tooMany)));
    }

    [Fact]
    public void CustomEarthCourseSupportsRaceStartCheckpointsAndWin()
    {
        var course = new RaceCourse(false, [new(1000, 2000), new(2000, 2000), new(2000, 3500)]);
        var match = new RoomMatch(RoomMode.Free);

        var p1Start = Pose(moon: false, x: 1000, z: 2000);
        var p2Start = Pose(moon: false, x: 1000, z: 2009);
        RoomPlayer[] players = [new(1, p1Start), new(2, p2Start)];

        // ホストが地球カスタムコースを設定＆レースモードに切替
        var cfg = new RoomConfig(RoomMode.Race, course, UpdateCourse: true);
        var configured = match.Update(players, 1, Pose(moon: false, x: 1000, z: 2000, config: cfg), 10_000);
        Assert.Equal(RoomMode.Race, configured.Mode);
        Assert.Equal(course, configured.Course);
        Assert.True(RoomProtocol.ValidSnapshot(configured));
        Assert.True(RoomMatch.CanStart(players, configured.Mode, configured.Course));
        Assert.False(RoomMatch.CanStart([new(1, Pose(moon: true)), new(2, Pose(moon: true))], configured.Mode, configured.Course));

        // 試合開始（カウントダウン 4秒）
        var cd = match.Update(players, 1, Pose(moon: false, x: 1000, z: 2000, start: true), 10_100);
        Assert.Equal(RoundPhase.Countdown, cd.Match!.Phase);

        // 試合中はモード変更・コース変更を拒否
        var blocked = match.Update(players, 1, Pose(moon: false, x: 1000, z: 2000,
            config: new RoomConfig(RoomMode.Free, null, UpdateCourse: true)), 11_000);
        Assert.Equal(RoomMode.Race, blocked.Mode);
        Assert.Equal(course, blocked.Course);

        // 4秒経過で開始 → P2 がチェックポイント1・2（ゴール）を順に通過
        int round = cd.Match.Number;
        var p2AtGate1 = Pose(moon: false, x: 2010, z: 2000, round: round);
        players = [new(1, Pose(moon: false, x: 1200, z: 2000, round: round)), new(2, p2AtGate1)];
        var afterGate1 = match.Update(players, 2, p2AtGate1, 14_200);
        Assert.Equal(RoundPhase.Playing, afterGate1.Match!.Phase);
        Assert.Equal(1, afterGate1.Match.Scores.Single(s => s.Seat == 2).Points);

        var p2AtGoal = Pose(moon: false, x: 2000, z: 3490, round: round);
        players = [new(1, Pose(moon: false, x: 1500, z: 2000, round: round)), new(2, p2AtGoal)];
        var finished = match.Update(players, 2, p2AtGoal, 14_400);
        Assert.Equal(RoundPhase.Finished, finished.Match!.Phase);
        Assert.Equal([2], finished.Match.Winners);

        // 試合終了後に既定コースへリセットできる
        var reset = match.Update(players, 1, Pose(moon: false, config: new RoomConfig(RoomMode.Race, null, UpdateCourse: true)), 15_000);
        Assert.Null(reset.Course);
    }

    [Fact]
    public void RaceSpawnFacesNextCheckpointAndScalesDurationForLongEarthCourse()
    {
        // 東向き (+X) のコース
        var eastCourse = new RaceCourse(false, [new(0, 0), new(5000, 0), new(10000, 0)]);
        var s1 = RoomMatch.RaceSpawn(eastCourse, 1);
        var s4 = RoomMatch.RaceSpawn(eastCourse, 4);
        Assert.InRange(s1.Heading, 89.5f, 90.5f);
        Assert.True(Math.Abs(s1.Z - s4.Z) > 20); // 横並び
        Assert.True(RoomMatch.RaceDurationMs(eastCourse) > 180_000);
        Assert.True(RoomMatch.RaceDurationMs(eastCourse) <= (long)RoomProtocol.MaxRaceSeconds * 1000);
    }
}
