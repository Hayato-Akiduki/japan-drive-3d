using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace JapanDrive;

public sealed class MainForm : Form
{
    private const int SidebarWidth = 360;

    private Game game = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastTime;

    // カメラ（ワールド座標 = 経度/緯度を度単位で平面化したもの）
    private float camX, camY;
    private float zoom = 110f;           // 1度あたりのピクセル数
    private bool overview;
    private float viewX, viewY, viewZoom; // 今フレームで使った値
    private float carAngle;
    private bool upHintShown;

    private readonly RectangleF worldBounds;

    // 色・フォント
    private static readonly Color Sea = Color.FromArgb(166, 208, 236);
    private static readonly Color Land = Color.FromArgb(236, 232, 214);
    private static readonly Color SideBg = Color.FromArgb(28, 33, 44);
    private readonly Font fontSmall = new("Yu Gothic UI", 12f, GraphicsUnit.Pixel);
    private readonly Font fontNormal = new("Yu Gothic UI", 14f, GraphicsUnit.Pixel);
    private readonly Font fontBold = new("Yu Gothic UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font fontTitle = new("Yu Gothic UI", 21f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font fontSpeed = new("Yu Gothic UI", 40f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font fontCity = new("Yu Gothic UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);

    public MainForm()
    {
        Text = "日本一周ドライブ";
        ClientSize = new Size((int)(1360 * DpiScale), (int)(820 * DpiScale));
        MinimumSize = new Size((int)(1000 * DpiScale), (int)(640 * DpiScale));
        StartPosition = FormStartPosition.CenterScreen;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SideBg;

        var pts = JapanOutline.Islands.SelectMany(i => i).Select(p => Game.ToWorld(p.Lat, p.Lon)).ToList();
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        worldBounds = new RectangleF(minX, minY, maxX - minX, maxY - minY);

        var car = game.CarWorld;
        camX = car.X;
        camY = car.Y;

        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    /// <summary>DPI倍率。描画はすべて96DPI基準の論理座標で行い、最後にこの倍率で拡大する。</summary>
    private float DpiScale => DeviceDpi / 96f;
    private int LogicalWidth => (int)(ClientSize.Width / DpiScale);
    private int LogicalHeight => (int)(ClientSize.Height / DpiScale);

    private Rectangle MapRect => new(0, 0, LogicalWidth - SidebarWidth, LogicalHeight);

    private void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - lastTime);
        lastTime = now;

        game.Update(dt);

        var car = game.CarWorld;
        float k = (float)Math.Min(1, dt * 5);
        camX += (car.X - camX) * k;
        camY += (car.Y - camY) * k;
        Invalidate();
    }

    // ================= 入力 =================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Up:
                OnUpPressed();
                return true;
            case Keys.Down:
                game.Brake = true;
                return true;
            case Keys.Left:
            case Keys.Right:
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnUpPressed()
    {
        if (game.AtCity != null && !game.Accel)
        {
            var road = game.NextNavRoad();
            if (road != null) game.Depart(road);
            else if (!upHintShown)
            {
                game.AddLog("先に数字キーか地図クリックで道路を選んでください。");
                upHintShown = true;
            }
        }
        game.Accel = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up) game.Accel = false;
        if (e.KeyCode == Keys.Down) game.Brake = false;
        base.OnKeyUp(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        game.Accel = game.Brake = false;
        base.OnDeactivate(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int index = e.KeyCode switch
        {
            >= Keys.D1 and <= Keys.D9 => e.KeyCode - Keys.D1,
            >= Keys.NumPad1 and <= Keys.NumPad9 => e.KeyCode - Keys.NumPad1,
            _ => -1,
        };
        if (index >= 0 && game.AtCity != null && index < game.AtCity.Roads.Count)
        {
            game.Depart(game.AtCity.Roads[index]);
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.F: game.Refuel(); break;
            case Keys.U: game.UTurn(); break;
            case Keys.Space: game.FastForward = !game.FastForward; break;
            case Keys.M: overview = !overview; break;
            case Keys.A:
                game.AutoDrive = !game.AutoDrive;
                game.AddLog(game.AutoDrive ? "ナビ自動走行：ON" : "ナビ自動走行：OFF");
                break;
            case Keys.Escape: game.SetNav(null); break;
            case Keys.Oemplus: case Keys.Add: ChangeZoom(1.2f); break;
            case Keys.OemMinus: case Keys.Subtract: ChangeZoom(1 / 1.2f); break;
            case Keys.N:
                if (game.Cleared || MessageBox.Show(this, "最初からやり直しますか？", "日本一周ドライブ",
                        MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    game = new Game();
                    upHintShown = false;
                }
                break;
        }
        base.OnKeyDown(e);
    }

    private void ChangeZoom(float factor)
    {
        overview = false;
        zoom = Math.Clamp(zoom * factor, 40f, 500f);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ChangeZoom(e.Delta > 0 ? 1.15f : 1 / 1.15f);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var mouse = new PointF(e.X / DpiScale, e.Y / DpiScale);
        if (!MapRect.Contains(Point.Round(mouse)) || viewZoom == 0) return;

        City? hit = null;
        float best = 14;
        foreach (var c in MapData.Cities)
        {
            var p = ToScreen(Game.ToWorld(c));
            float d = MathF.Sqrt((p.X - mouse.X) * (p.X - mouse.X) + (p.Y - mouse.Y) * (p.Y - mouse.Y));
            if (d < best) { best = d; hit = c; }
        }
        if (hit == null) return;

        if (e.Button == MouseButtons.Left && game.AtCity != null)
        {
            var road = game.AtCity.Roads.Where(r => r.Other(game.AtCity) == hit).MinBy(r => r.Km);
            if (road != null)
            {
                game.Depart(road);
                return;
            }
        }
        game.SetNav(hit);
    }

    // ================= 描画 =================

    private PointF ToScreen(PointF w)
    {
        var r = MapRect;
        return new PointF(r.X + r.Width / 2f + (w.X - viewX) * viewZoom,
                          r.Y + r.Height / 2f + (w.Y - viewY) * viewZoom);
    }

    private PointF CityPt(City c) => ToScreen(Game.ToWorld(c));

    private void UpdateView()
    {
        var r = MapRect;
        if (overview)
        {
            viewZoom = Math.Min(r.Width / worldBounds.Width, r.Height / worldBounds.Height) * 0.95f;
            viewX = worldBounds.X + worldBounds.Width / 2;
            viewY = worldBounds.Y + worldBounds.Height / 2;
        }
        else
        {
            viewZoom = zoom;
            viewX = camX;
            viewY = camY;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(DpiScale, DpiScale);
        UpdateView();

        var mr = MapRect;
        g.SetClip(mr);
        DrawMap(g, mr);
        DrawHud(g, mr);
        if (game.Cleared) DrawClear(g, mr);
        g.ResetClip();

        DrawSidebar(g);
    }

    private void DrawMap(Graphics g, Rectangle mr)
    {
        using (var sea = new SolidBrush(Sea)) g.FillRectangle(sea, mr);

        using (var land = new SolidBrush(Land))
        using (var coast = new Pen(Color.FromArgb(150, 160, 150), 1.5f))
        {
            foreach (var island in JapanOutline.Islands)
            {
                var pts = island.Select(p => ToScreen(Game.ToWorld(p.Lat, p.Lon))).ToArray();
                g.FillPolygon(land, pts);
                g.DrawPolygon(coast, pts);
            }
        }

        // ナビのルート
        var path = game.NavPath();
        if (path != null)
        {
            var pts = new List<PointF>();
            if (game.Road != null) pts.Add(ToScreen(game.CarWorld));
            pts.AddRange(path.Select(CityPt));
            if (pts.Count >= 2)
            {
                using var navPen = new Pen(Color.FromArgb(200, 255, 140, 0), 11f)
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(navPen, pts.ToArray());
            }
        }

        // 道路
        using var expPen = new Pen(Color.FromArgb(46, 139, 87), 4f) { LineJoin = LineJoin.Round };
        using var bridgePen = new Pen(Color.FromArgb(120, 90, 200), 4f) { LineJoin = LineJoin.Round };
        using var ferryPen = new Pen(Color.FromArgb(30, 90, 200), 2.5f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
        foreach (var r in MapData.Roads)
        {
            var pen = r.Type switch
            {
                RoadType.Ferry => ferryPen,
                RoadType.Bridge or RoadType.MegaBridge => bridgePen,
                _ => expPen,
            };
            var pts = r.Points.Select(p => ToScreen(Game.ToWorld(p.Lat, p.Lon))).ToArray();
            g.DrawLines(pen, pts);
        }

        // 走行中の道路を強調
        if (game.Road != null)
        {
            using var cur = new Pen(Color.FromArgb(220, 30, 30), 3f) { LineJoin = LineJoin.Round };
            var pts = game.Road.Points.Select(p => ToScreen(Game.ToWorld(p.Lat, p.Lon))).ToArray();
            g.DrawLines(cur, pts);
        }

        // 道路名（拡大時のみ）
        if (viewZoom >= 100)
        {
            foreach (var r in MapData.Roads)
            {
                var (mLat, mLon) = r.Sample(0.5);
                var mid = ToScreen(Game.ToWorld(mLat, mLon));
                DrawLabel(g, r.Name, fontSmall, mid, Color.FromArgb(30, 70, 50), center: true);
            }
        }

        // 分岐の選択肢
        if (game.AtCity != null && !game.Cleared)
        {
            var navRoad = game.NextNavRoad();
            var a = CityPt(game.AtCity);
            using var choicePen = new Pen(Color.FromArgb(230, 255, 210, 0), 7f) { LineJoin = LineJoin.Round };
            for (int i = 0; i < game.AtCity.Roads.Count; i++)
            {
                var r = game.AtCity.Roads[i];
                var pts = r.Points.Select(p => ToScreen(Game.ToWorld(p.Lat, p.Lon))).ToArray();
                g.DrawLines(choicePen, pts);
                var np = r.NextPointFrom(game.AtCity);
                var b = ToScreen(Game.ToWorld(np.Lat, np.Lon));
                float dx = b.X - a.X, dy = b.Y - a.Y;
                float len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
                float t = MathF.Min(len * 0.5f, 60) / len;
                var badge = new PointF(a.X + dx * t, a.Y + dy * t);
                DrawBadge(g, (i + 1).ToString(), badge, r == navRoad ? Color.DarkOrange : Color.FromArgb(40, 40, 60));
            }
        }

        // 都市
        bool showAllNames = viewZoom >= 70;
        using var outline = new Pen(Color.FromArgb(60, 60, 60), 1.5f);
        foreach (var c in MapData.Cities)
        {
            var p = CityPt(c);
            bool goal = c.IsGoal || c == game.StartCity;
            float rad = goal ? 7 : 4;
            Color fill = c == game.StartCity ? Color.DeepSkyBlue
                : c.IsGoal ? (game.Visited.Contains(c) ? Color.LimeGreen : Color.Gold)
                : Color.White;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            g.DrawEllipse(outline, p.X - rad, p.Y - rad, rad * 2, rad * 2);

            if (c == game.NavTarget)
            {
                using var ring = new Pen(Color.OrangeRed, 3f);
                g.DrawEllipse(ring, p.X - rad - 5, p.Y - rad - 5, (rad + 5) * 2, (rad + 5) * 2);
            }

            if (showAllNames || goal)
            {
                string name = c.IsGoal && c != game.StartCity ? "★" + c.Name : c.Name;
                DrawLabel(g, name, fontCity, new PointF(p.X + rad + 3, p.Y - 9), Color.Black, center: false);
            }
        }

        DrawCar(g);
    }

    private void DrawCar(Graphics g)
    {
        var p = ToScreen(game.CarWorld);
        if (game.Road != null)
        {
            var a = CityPt(game.From);
            var b = CityPt(game.To);
            if (Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) > 0.1f)
                carAngle = MathF.Atan2(b.Y - a.Y, b.X - a.X) * 180 / MathF.PI;
        }

        var state = g.Save();
        g.TranslateTransform(p.X, p.Y);
        g.RotateTransform(carAngle);
        PointF[] body = { new(14, 0), new(-9, -8), new(-5, 0), new(-9, 8) };
        using (var fill = new SolidBrush(game.OnFerry ? Color.RoyalBlue : Color.Crimson)) g.FillPolygon(fill, body);
        using (var pen = new Pen(Color.White, 2f)) g.DrawPolygon(pen, body);
        g.Restore(state);
    }

    private void DrawBadge(Graphics g, string text, PointF center, Color color)
    {
        const float r = 11;
        using (var b = new SolidBrush(color)) g.FillEllipse(b, center.X - r, center.Y - r, r * 2, r * 2);
        using (var p = new Pen(Color.White, 2f)) g.DrawEllipse(p, center.X - r, center.Y - r, r * 2, r * 2);
        var size = g.MeasureString(text, fontBold);
        g.DrawString(text, fontBold, Brushes.White, center.X - size.Width / 2, center.Y - size.Height / 2);
    }

    private static void DrawLabel(Graphics g, string text, Font font, PointF pos, Color color, bool center)
    {
        if (center)
        {
            var size = g.MeasureString(text, font);
            pos = new PointF(pos.X - size.Width / 2, pos.Y - size.Height / 2);
        }
        using var halo = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) g.DrawString(text, font, halo, pos.X + dx, pos.Y + dy);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, pos);
    }

    private void DrawHud(Graphics g, Rectangle mr)
    {
        var box = new RectangleF(mr.X + 14, mr.Bottom - 132, 300, 118);
        using (var bg = new SolidBrush(Color.FromArgb(190, 20, 24, 32))) FillRound(g, bg, box, 12);

        Color speedColor = game.Speed > 120 ? Color.OrangeRed : game.Speed > Game.LegalSpeed ? Color.Gold : Color.White;
        using (var sb = new SolidBrush(speedColor))
            g.DrawString($"{game.Speed:0}", fontSpeed, sb, box.X + 12, box.Y + 6);
        g.DrawString("km/h", fontNormal, Brushes.Gainsboro, box.X + 100, box.Y + 32);
        g.DrawString(game.OnFerry ? "フェリー航行中" : $"制限 {Game.LegalSpeed:0}", fontSmall, Brushes.Silver, box.X + 170, box.Y + 14);
        if (game.TimeScale > 1)
            g.DrawString($"早送り ×{game.TimeScale:0}", fontBold, Brushes.Gold, box.X + 170, box.Y + 34);

        // 燃料メーター
        float ratio = (float)(game.Fuel / Game.TankSize);
        var bar = new RectangleF(box.X + 14, box.Y + 72, box.Width - 28, 14);
        g.DrawString($"燃料 {game.Fuel:0.0} / {Game.TankSize:0} L（約{game.Fuel * Game.KmPerLiter(Math.Max(game.Speed, 80)):0}km）",
            fontSmall, Brushes.Gainsboro, box.X + 12, box.Y + 88);
        g.FillRectangle(Brushes.DimGray, bar);
        Color fuelColor = ratio > 0.5f ? Color.LimeGreen : ratio > 0.2f ? Color.Orange : Color.Red;
        using (var fb = new SolidBrush(fuelColor)) g.FillRectangle(fb, bar.X, bar.Y, bar.Width * ratio, bar.Height);

        // 右上：操作ヒント
        string mode = overview ? "[M] 追従表示に戻る" : "[M] 全体地図";
        var size = g.MeasureString(mode, fontSmall);
        using (var bg = new SolidBrush(Color.FromArgb(160, 20, 24, 32)))
            FillRound(g, bg, new RectangleF(mr.Right - size.Width - 26, mr.Y + 12, size.Width + 14, size.Height + 8), 8);
        g.DrawString(mode, fontSmall, Brushes.White, mr.Right - size.Width - 19, mr.Y + 16);
    }

    private void DrawClear(Graphics g, Rectangle mr)
    {
        using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(dim, mr);
        var box = new RectangleF(mr.X + mr.Width / 2f - 230, mr.Y + mr.Height / 2f - 170, 460, 340);
        using (var bg = new SolidBrush(Color.FromArgb(245, 255, 252, 240))) FillRound(g, bg, box, 18);

        float y = box.Y + 24;
        using var big = new Font("Yu Gothic UI", 32f, FontStyle.Bold, GraphicsUnit.Pixel);
        var title = "日本一周 達成！";
        var ts = g.MeasureString(title, big);
        g.DrawString(title, big, Brushes.DarkOrange, box.X + (box.Width - ts.Width) / 2, y);
        y += 66;

        string[] lines =
        {
            $"所要時間　{Game.FormatDuration(game.GameMinutes - game.StartMinutes)}",
            $"走行距離　{game.Odometer:N0} km",
            $"高速料金　{game.TollPaid:N0} 円",
            $"燃料代　　{game.FuelPaid:N0} 円",
            $"反則金等　{game.FinePaid + game.RescuePaid:N0} 円（違反 {game.Violations} 回）",
            $"合計支出　{game.TotalPaid:N0} 円",
        };
        foreach (var line in lines)
        {
            g.DrawString(line, fontBold, Brushes.Black, box.X + 60, y);
            y += 30;
        }
        g.DrawString("[N] もう一度遊ぶ", fontNormal, Brushes.DimGray, box.X + 60, y + 10);
    }

    private static void FillRound(Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // ================= サイドバー =================

    private void DrawSidebar(Graphics g)
    {
        int x0 = LogicalWidth - SidebarWidth;
        using (var bg = new SolidBrush(SideBg)) g.FillRectangle(bg, x0, 0, SidebarWidth, LogicalHeight + 1);

        float x = x0 + 18, w = SidebarWidth - 36, y = 14;
        using var sub = new SolidBrush(Color.FromArgb(150, 160, 180));
        using var accent = new SolidBrush(Color.FromArgb(255, 200, 80));

        g.DrawString("日本一周ドライブ", fontTitle, Brushes.White, x, y);
        y += 36;
        g.DrawString(Game.FormatClock(game.GameMinutes), fontBold, accent, x, y);
        y += 26;

        // 現在の状況
        if (game.AtCity != null)
        {
            g.DrawString($"停車中：{game.AtCity.Name}", fontBold, Brushes.White, x, y);
        }
        else if (game.Road != null)
        {
            g.DrawString($"{game.Road.Name}  {game.To.Name}方面", fontBold, Brushes.White, x, y);
            y += 22;
            string eta = game.Speed > 1
                ? $"到着予想 {Game.FormatClock(game.GameMinutes + game.Remaining / game.Speed * 60)[^5..]}"
                : "";
            g.DrawString($"残り {game.Remaining:0} km　{eta}", fontNormal, Brushes.Gainsboro, x, y);
        }
        y += 26;

        if (game.NavTarget != null)
        {
            Game.ShortestPath(game.NavOrigin, game.NavTarget, out double km);
            km += game.Remaining;
            g.DrawString($"ナビ：{game.NavTarget.Name}まで {km:0} km（自動走行 {(game.AutoDrive ? "ON" : "OFF")}）",
                fontNormal, Brushes.Orange, x, y);
            y += 24;
        }

        g.DrawString($"走行 {game.Odometer:N0} km　支出 {game.TotalPaid:N0} 円", fontNormal, Brushes.Gainsboro, x, y);
        y += 22;
        g.DrawString($"（高速 {game.TollPaid:N0}／燃料 {game.FuelPaid:N0}／罰金等 {game.FinePaid + game.RescuePaid:N0}）",
            fontSmall, sub, x, y);
        y += 28;

        // 分岐
        if (game.AtCity != null && !game.Cleared)
        {
            g.DrawString("行き先を選択（数字キー／地図をクリック）", fontSmall, sub, x, y);
            y += 20;
            var navRoad = game.NextNavRoad();
            for (int i = 0; i < game.AtCity.Roads.Count; i++)
            {
                var r = game.AtCity.Roads[i];
                var dest = r.Other(game.AtCity);
                string star = dest.IsGoal && !game.Visited.Contains(dest) && dest != game.StartCity ? "★" : "";
                string line = $"{i + 1}. {r.Name} → {star}{dest.Name}  {r.Km:0}km  ¥{r.Toll:N0}";
                var brush = r == navRoad ? Brushes.Orange : Brushes.White;
                g.DrawString(line, fontNormal, brush, x, y);
                y += 22;
            }
            if (navRoad != null)
            {
                g.DrawString("↑キーでナビの道路へ出発", fontSmall, Brushes.Orange, x, y);
                y += 18;
            }
            y += 10;
        }

        // 目的地チェックリスト
        g.DrawString($"★目的地 {game.Visited.Count}/{game.GoalCount}（全部回って東京へ）", fontBold, Brushes.White, x, y);
        y += 24;
        var goals = MapData.Cities.Where(c => c.IsGoal && c != game.StartCity).ToList();
        int col = 0;
        foreach (var c in goals)
        {
            bool done = game.Visited.Contains(c);
            using var b = new SolidBrush(done ? Color.LimeGreen : Color.FromArgb(200, 200, 200));
            g.DrawString($"{(done ? "✔" : "・")}{c.Name}", fontNormal, b, x + col * (w / 3), y);
            if (++col == 3) { col = 0; y += 21; }
        }
        if (col != 0) y += 21;
        y += 10;

        // 操作説明
        string[] help =
        {
            "↑ アクセル／出発　↓ ブレーキ",
            "1～9 道路を選ぶ　左クリック 隣の都市へ",
            "右クリック ナビ目的地　Esc ナビ解除",
            "F 給油　U Uターン　Space 早送り",
            "A 自動走行切替　ホイール/+- ズーム　N リセット",
        };
        foreach (var h in help)
        {
            g.DrawString(h, fontSmall, sub, x, y);
            y += 18;
        }
        y += 8;

        // ログ（下から詰める）
        float logTop = y;
        float bottom = LogicalHeight - 12;
        using var logBrush = new SolidBrush(Color.FromArgb(215, 220, 230));
        var fmt = new StringFormat();
        float cursor = bottom;
        for (int i = game.Log.Count - 1; i >= 0; i--)
        {
            var size = g.MeasureString(game.Log[i], fontSmall, (int)w, fmt);
            cursor -= size.Height + 2;
            if (cursor < logTop) break;
            g.DrawString(game.Log[i], fontSmall, i == game.Log.Count - 1 ? Brushes.White : logBrush,
                new RectangleF(x, cursor, w, size.Height + 2), fmt);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            fontSmall.Dispose();
            fontNormal.Dispose();
            fontBold.Dispose();
            fontTitle.Dispose();
            fontSpeed.Dispose();
            fontCity.Dispose();
        }
        base.Dispose(disposing);
    }
}
