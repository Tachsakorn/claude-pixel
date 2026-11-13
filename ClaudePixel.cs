// Claude Pixel - tiny always-on-top pixel widget showing Claude usage limits + stats.
// Build: build.cmd  (uses the C# compiler that ships with Windows / .NET Framework 4)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool created;
        var mtx = new Mutex(true, "ClaudePixelWidget_single", out created);
        if (!created) return; // already running
        try { SetProcessDPIAware(); } catch { }
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        Application.Run(new Widget());
        GC.KeepAlive(mtx);
    }
}

class Limit { public string Label; public double Pct; public DateTime? Reset; }

class Widget : Form
{
    // ---------- logical pixel canvas ----------
    const int W = 112, H = 50;
    int scale = 3;
    readonly Bitmap buf = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    readonly int[] px = new int[W * H];

    // palette
    const int KEY = unchecked((int)0xFFFF00FF);   // transparent
    const int BG = unchecked((int)0xFF1E1D1A);
    const int BORDER = unchecked((int)0xFF4A4740);
    const int TEXT = unchecked((int)0xFFEDE9DF);
    const int DIM = unchecked((int)0xFF8C877C);
    const int OR = unchecked((int)0xFFD97757);
    const int ORL = unchecked((int)0xFFEB9A7E);
    const int RED = unchecked((int)0xFFE5534B);
    const int REDL = unchecked((int)0xFFF08A84);
    const int BARBG = unchecked((int)0xFF34322D);
    const int EYE = unchecked((int)0xFF141311);
    const int BLUE = unchecked((int)0xFF6CB6FF);

    // ---------- state ----------
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    int frame, page, tick;
    int hopT, blinkT, lookDir, nextBlink = 25, nextLook = 50, walkT;
    readonly Random rnd = new Random();

    List<Limit> limits = new List<Limit>();
    volatile bool fetching;
    bool authErr, netErr;
    DateTime lastOk = DateTime.MinValue;

    string sTokens = "-", sSessions = "-", sMsgs = "-", sFav = "-", sStreak = "-";
    DateTime statsMtime = DateTime.MinValue;

    readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    readonly string credPath, statsPath, histPath, cfgPath;
    readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    Point dragStart, formStart; bool dragging, moved;
    ContextMenuStrip menu;

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    public Widget()
    {
        credPath = Path.Combine(home, ".claude\\.credentials.json");
        statsPath = Path.Combine(home, ".claude\\stats-cache.json");
        histPath = Path.Combine(home, ".claude\\history.jsonl");
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePixel");
        Directory.CreateDirectory(dir);
        cfgPath = Path.Combine(dir, "settings.txt");

        Text = "Claude Pixel";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(255, 0, 255);
        TransparencyKey = BackColor;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        LoadCfg();
        BuildMenu();

        anim.Interval = 160; // ~6 fps: smooth enough for pixel art, near-zero CPU
        anim.Tick += OnTick;
        anim.Start();

        ReadStats();
        FetchUsageAsync();
        Render();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW: no alt-tab */ return cp; }
    }

    // ---------- settings ----------
    void LoadCfg()
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int x = -1, y = -1;
        try
        {
            if (File.Exists(cfgPath))
            {
                string[] p = File.ReadAllText(cfgPath).Trim().Split(',');
                x = int.Parse(p[0]); y = int.Parse(p[1]); scale = Math.Max(2, Math.Min(6, int.Parse(p[2]))); page = int.Parse(p[3]) % 2;
            }
        }
        catch { x = -1; }
        ClientSize = new Size(W * scale, H * scale);
        if (x < 0 || !OnAnyScreen(new Rectangle(x, y, Width, Height)))
        { x = wa.Right - Width - 12; y = wa.Bottom - Height - 12; }
        Location = new Point(x, y);
    }

    static bool OnAnyScreen(Rectangle r)
    {
        foreach (var s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) return true;
        return false;
    }

    void SaveCfg()
    {
        try { File.WriteAllText(cfgPath, Location.X + "," + Location.Y + "," + scale + "," + page); } catch { }
    }

    void BuildMenu()
    {
        menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, delegate { FetchUsageAsync(); ReadStats(); });
        menu.Items.Add("Switch page (or left-click)", null, delegate { page ^= 1; hopT = 6; SaveCfg(); });
        var size = new ToolStripMenuItem("Size");
        for (int s = 2; s <= 5; s++)
        {
            int ss = s;
            size.DropDownItems.Add(s + "x", null, delegate { SetScale(ss); });
        }
        menu.Items.Add(size);
        var top = new ToolStripMenuItem("Always on top") { Checked = true, CheckOnClick = true };
        top.CheckedChanged += delegate { TopMost = top.Checked; };
        menu.Items.Add(top);
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsAutoStart(), CheckOnClick = true };
        startup.CheckedChanged += delegate { SetAutoStart(startup.Checked); };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { SaveCfg(); Close(); });
        ContextMenuStrip = menu;
    }

    void SetScale(int s)
    {
        Point br = new Point(Right, Bottom); // keep bottom-right corner anchored
        scale = s;
        ClientSize = new Size(W * scale, H * scale);
        Location = new Point(br.X - Width, br.Y - Height);
        SaveCfg(); Invalidate();
    }

    const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    bool IsAutoStart()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ClaudePixel") != null; }
        catch { return false; }
    }
    void SetAutoStart(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue("ClaudePixel", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("ClaudePixel", false);
            }
        }
        catch { }
    }

    // ---------- mouse: drag to move, click to switch page ----------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { dragging = true; moved = false; dragStart = Cursor.Position; formStart = Location; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        Point c = Cursor.Position;
        int dx = c.X - dragStart.X, dy = c.Y - dragStart.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 3) moved = true;
        if (moved) Location = new Point(formStart.X + dx, formStart.Y + dy);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !dragging) return;
        dragging = false;
        if (!moved) { page ^= 1; hopT = 6; }
        SaveCfg();
    }

    // ---------- animation loop ----------
    void OnTick(object sender, EventArgs e)
    {
        frame++; tick++;
        if (hopT > 0) hopT--;
        if (blinkT > 0) blinkT--;
        if (walkT > 0) walkT--;
        if (--nextBlink <= 0) { blinkT = 2; nextBlink = 20 + rnd.Next(30); }
        if (--nextLook <= 0) { lookDir = rnd.Next(3) - 1; nextLook = 25 + rnd.Next(60); if (rnd.Next(4) == 0) walkT = 12; }

        if (tick % 375 == 0) FetchUsageAsync();   // every ~60s
        if (tick % 60 == 0) ReadStats();           // every ~10s (only re-parses if file changed)
        if (tick == 30) TrimMemory();

        Render();
        Invalidate();
    }

    void TrimMemory()
    {
        try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(buf, 0, 0, W * scale, H * scale);
    }

    // ---------- pixel drawing ----------
    void P(int x, int y, int c) { if (x >= 0 && y >= 0 && x < W && y < H) px[y * W + x] = c; }
    void Rect(int x, int y, int w, int h, int c) { for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) P(x + i, y + j, c); }

    static readonly Dictionary<char, string> F = new Dictionary<char, string> {
        {'0',"111101101101111"},{'1',"010110010010111"},{'2',"111001111100111"},{'3',"111001111001111"},
        {'4',"101101111001001"},{'5',"111100111001111"},{'6',"111100111101111"},{'7',"111001010010010"},
        {'8',"111101111101111"},{'9',"111101111001111"},
        {'A',"010101111101101"},{'B',"110101110101110"},{'C',"011100100100011"},{'D',"110101101101110"},
        {'E',"111100110100111"},{'F',"111100110100100"},{'G',"011100101101011"},{'H',"101101111101101"},
        {'I',"111010010010111"},{'J',"001001001101010"},{'K',"101101110101101"},{'L',"100100100100111"},
        {'M',"101111111101101"},{'N',"110101101101101"},{'O',"010101101101010"},{'P',"110101110100100"},
        {'Q',"010101101110011"},{'R',"110101110101101"},{'S',"011100010001110"},{'T',"111010010010010"},
        {'U',"101101101101111"},{'V',"101101101101010"},{'W',"101101111111101"},{'X',"101101010101101"},
        {'Y',"101101010010010"},{'Z',"111001010100111"},
        {'%',"101001010100101"},{'.',"000000000000010"},{':',"000010000010000"},{'/',"001001010100100"},
        {'-',"000000111000000"},{'=',"000111000111000"},{'!',"010010010000010"},{'?',"110001010000010"},{' ',"000000000000000"}
    };

    void Txt(int x, int y, string s, int c)
    {
        foreach (char ch in s.ToUpperInvariant())
        {
            string g;
            if (F.TryGetValue(ch, out g))
                for (int i = 0; i < 15; i++) if (g[i] == '1') P(x + i % 3, y + i / 3, c);
            x += 4;
        }
    }
    static int TW(string s) { return s.Length * 4 - 1; }
    void TxtR(int right, int y, string s, int c) { Txt(right - TW(s) + 1, y, s, c); }

    // ---------- mini Claude ----------
    // 12x8 sprite drawn at 2x. O = body, K = eye.
    void DrawClaude(int sx, int sy, bool worried)
    {
        int bob = (hopT > 0) ? new[] { 0, -2, -4, -5, -4, -2, 0 }[6 - hopT] : ((frame / 4) % 2 == 0 ? 0 : 1);
        int y0 = sy + bob;
        bool walking = fetching || walkT > 0;
        bool cheer = hopT > 0;
        int ex = lookDir;

        // shadow (shrinks while airborne)
        int sh = hopT > 0 ? 6 : 9;
        Rect(sx + 12 - sh, sy + 17, sh * 2, 1, BARBG);

        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 12; c++)
            {
                int col = 0;
                bool body = (r <= 5 && c >= 2 && c <= 9);
                bool arms = cheer ? (r >= 1 && r <= 2 && (c <= 1 || c >= 10)) : (r >= 3 && r <= 4 && (c <= 1 || c >= 10));
                if (cheer && (r == 3) && (c == 1 || c == 10)) arms = true; // shoulder joint
                bool leg = (r >= 6) && (c == 2 || c == 4 || c == 7 || c == 9);
                if (leg && walking && r == 7)
                {
                    bool phase = (frame / 2) % 2 == 0;
                    if (phase && (c == 2 || c == 7)) leg = false;
                    if (!phase && (c == 4 || c == 9)) leg = false;
                }
                if (body || arms || leg) col = OR;
                bool eye = (c == 3 + ex || c == 8 + ex) && (blinkT > 0 ? r == 2 : (r == 1 || r == 2));
                if (eye && body) col = EYE;
                if (col != 0) Rect(sx + c * 2, y0 + r * 2, 2, 2, col);
            }

        if (worried) // sweat drop
        {
            int d = (frame / 2) % 6;
            Rect(sx + 22, y0 + 1 + d, 2, 2, BLUE);
        }
    }

    // ---------- compose a frame ----------
    void Render()
    {
        for (int i = 0; i < px.Length; i++) px[i] = BG;
        // pixel border with notched corners
        for (int x = 1; x < W - 1; x++) { P(x, 0, BORDER); P(x, H - 1, BORDER); }
        for (int y = 1; y < H - 1; y++) { P(0, y, BORDER); P(W - 1, y, BORDER); }
        P(0, 0, KEY); P(W - 1, 0, KEY); P(0, H - 1, KEY); P(W - 1, H - 1, KEY);
        P(1, 1, BORDER); P(W - 2, 1, BORDER); P(1, H - 2, BORDER); P(W - 2, H - 2, BORDER);
        for (int y = 4; y < H - 4; y++) if (y % 2 == 0) P(32, y, BARBG);

        double maxPct = 0;
        foreach (var l in limits) if (l.Pct > maxPct) maxPct = l.Pct;
        DrawClaude(4, 14, maxPct >= 80);

        if (authErr || netErr) Txt(22, 5, "?", DIM);

        string st = authErr ? "LOGIN" : netErr ? "OFFLN" : lastOk == DateTime.MinValue ? "..." : lastOk.ToString("HH:mm");
        Txt(16 - TW(st) / 2, 40, st, DIM);

        if (page == 0) DrawLimits(); else DrawStats();

        // page dots
        Rect(W - 9, H - 4, 2, 1, page == 0 ? OR : BARBG);
        Rect(W - 6, H - 4, 2, 1, page == 1 ? OR : BARBG);

        var bd = buf.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(px, 0, bd.Scan0, px.Length);
        buf.UnlockBits(bd);
    }

    void DrawLimits()
    {
        const int X0 = 35, X1 = 108;
        if (limits.Count == 0)
        {
            if (authErr)
            {
                Txt(X0, 5, "LIVE LIMITS OFF", TEXT);
                Txt(X0, 14, "OPEN A TERMINAL", DIM);
                Txt(X0, 21, "RUN: CLAUDE", DIM);
                Txt(X0, 28, "THEN /LOGIN", DIM);
                Txt(X0, 38, "CLICK = STATS", OR);
            }
            else Txt(X0, 20, netErr ? "NO CONNECTION" : "LOADING...", DIM);
            return;
        }
        for (int i = 0; i < limits.Count && i < 3; i++)
        {
            var l = limits[i];
            int y = 4 + i * 15;
            bool hot = l.Pct >= 80;
            Txt(X0, y, l.Label, TEXT);
            if (l.Reset.HasValue) Txt(X0 + 20, y, Countdown(l.Reset.Value), DIM);
            TxtR(X1, y, ((int)Math.Round(l.Pct)) + "%", hot ? RED : TEXT);
            int bw = X1 - X0 + 1;
            Rect(X0, y + 7, bw, 4, BARBG);
            int fw = (int)Math.Round(bw * Math.Max(0, Math.Min(100, l.Pct)) / 100.0);
            if (l.Pct > 0 && fw < 1) fw = 1;
            Rect(X0, y + 7, fw, 4, hot ? RED : OR);
            Rect(X0, y + 7, fw, 1, hot ? REDL : ORL);
            for (int x = X0 + 6; x < X0 + bw; x += 6) Rect(x, y + 7, 1, 4, BG); // segmented look
        }
    }

    void DrawStats()
    {
        const int X0 = 35, X1 = 108;
        string[,] rows = { { "TOKENS", sTokens }, { "SESSIONS", sSessions }, { "MSGS", sMsgs }, { "FAV", sFav }, { "STREAK", sStreak } };
        for (int i = 0; i < 5; i++)
        {
            int y = 4 + i * 9;
            Txt(X0, y, rows[i, 0], DIM);
            TxtR(X1, y, rows[i, 1], i == 0 ? OR : TEXT);
        }
    }

    static string Countdown(DateTime resetUtc)
    {
        TimeSpan t = resetUtc - DateTime.UtcNow;
        if (t.TotalMinutes < 1) return "NOW";
        if (t.TotalDays >= 1) return (int)t.TotalDays + "D" + t.Hours + "H";
        if (t.TotalHours >= 1) return (int)t.TotalHours + "H" + t.Minutes + "M";
        return t.Minutes + "M";
    }

    // ---------- local stats (~/.claude/stats-cache.json) ----------
    void ReadStats()
    {
        try
        {
            if (!File.Exists(statsPath)) return;
            DateTime mt = File.GetLastWriteTimeUtc(statsPath);
            if (mt == statsMtime) return;
            statsMtime = mt;
            var d = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(statsPath));

            double total = 0, best = -1; string fav = "-";
            object mu;
            if (d.TryGetValue("modelUsage", out mu) && mu is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)mu)
                {
                    var m = kv.Value as Dictionary<string, object>;
                    if (m == null) continue;
                    double t = Num(m, "inputTokens") + Num(m, "outputTokens") + Num(m, "cacheReadInputTokens") + Num(m, "cacheCreationInputTokens");
                    total += t;
                    if (t > best) { best = t; fav = PrettyModel(kv.Key); }
                }
            sTokens = Fmt(total);
            sFav = fav;
            sSessions = ((long)Num(d, "totalSessions")).ToString();
            sMsgs = Fmt(Num(d, "totalMessages"));

            var days = new HashSet<string>();
            object da;
            if (d.TryGetValue("dailyActivity", out da) && da is System.Collections.IEnumerable)
                foreach (var o in (System.Collections.IEnumerable)da)
                {
                    var e = o as Dictionary<string, object>;
                    if (e != null && e.ContainsKey("date")) days.Add(Convert.ToString(e["date"]));
                }
            DateTime today = DateTime.Now.Date;
            try { if (File.Exists(histPath) && File.GetLastWriteTime(histPath).Date == today) days.Add(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            DateTime cur = days.Contains(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) ? today : today.AddDays(-1);
            int streak = 0;
            while (days.Contains(cur.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))) { streak++; cur = cur.AddDays(-1); }
            sStreak = streak + "D";
        }
        catch { }
    }

    static double Num(Dictionary<string, object> d, string k)
    {
        object v;
        if (d.TryGetValue(k, out v) && v != null) { try { return Convert.ToDouble(v); } catch { } }
        return 0;
    }

    static string Fmt(double n)
    {
        if (n >= 1e9) return (n / 1e9).ToString("0.00") + "B";
        if (n >= 1e6) return (n / 1e6).ToString("0.0") + "M";
        if (n >= 1e3) return (n / 1e3).ToString("0.0") + "K";
        return ((long)n).ToString();
    }

    static string PrettyModel(string id) // claude-opus-5-5 -> OPUS 5.5
    {
        string s = id.StartsWith("claude-") ? id.Substring(7) : id;
        int dash = s.IndexOf('-');
        if (dash < 0) return s.ToUpperInvariant();
        string name = s.Substring(0, dash), ver = s.Substring(dash + 1);
        int d2 = ver.IndexOf('-', ver.IndexOf('-') + 1);
        if (d2 > 0) ver = ver.Substring(0, d2); // drop date suffixes
        return (name + " " + ver.Replace('-', '.')).ToUpperInvariant();
    }

    // ---------- live limits (Anthropic OAuth usage endpoint) ----------
    void FetchUsageAsync()
    {
        if (fetching) return;
        fetching = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<Limit> res = null; bool aErr = false, nErr = false;
            try { res = FetchUsage(out aErr); if (res == null && !aErr) nErr = true; }
            catch { nErr = true; }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    fetching = false;
                    authErr = aErr; netErr = nErr;
                    if (res != null) { limits = res; lastOk = DateTime.Now; hopT = 6; }
                    TrimMemory();
                });
            }
            catch { fetching = false; }
        });
    }

    List<Limit> FetchUsage(out bool authError)
    {
        authError = false;
        if (!File.Exists(credPath)) { authError = true; return null; }
        string token, refresh;
        ReadTokens(out token, out refresh);
        if (token == null) { authError = true; return null; }

        int code; string body = Get(token, out code);
        if (code == 401 || code == 403)
        {
            if (refresh != null && TryRefresh(refresh)) { ReadTokens(out token, out refresh); body = Get(token, out code); }
            if (code == 401 || code == 403) { authError = true; return null; }
        }
        if (code != 200 || body == null) return null;
        return ParseUsage(body);
    }

    void ReadTokens(out string token, out string refresh)
    {
        token = refresh = null;
        try
        {
            var root = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(credPath));
            var o = root["claudeAiOauth"] as Dictionary<string, object>;
            if (o == null) return;
            object v;
            if (o.TryGetValue("accessToken", out v)) token = v as string;
            if (o.TryGetValue("refreshToken", out v)) refresh = v as string;
        }
        catch { }
    }

    static string Get(string token, out int code)
    {
        code = 0;
        try
        {
            var rq = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
            rq.Method = "GET"; rq.Timeout = 15000; rq.ReadWriteTimeout = 15000;
            rq.Headers["Authorization"] = "Bearer " + token;
            rq.Headers["anthropic-beta"] = "oauth-2025-04-20";
            rq.Accept = "application/json";
            rq.UserAgent = "claude-pixel-widget/1.0";
            using (var rs = (HttpWebResponse)rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
            { code = (int)rs.StatusCode; return sr.ReadToEnd(); }
        }
        catch (WebException we)
        {
            var r = we.Response as HttpWebResponse;
            if (r != null) { code = (int)r.StatusCode; r.Close(); }
            return null;
        }
    }

    bool TryRefresh(string refresh)
    {
        string payload = js.Serialize(new Dictionary<string, object> {
            { "grant_type", "refresh_token" }, { "refresh_token", refresh },
            { "client_id", "9d1c250a-e61b-44d9-88ed-5944d1962f5e" } });
        foreach (string url in new[] { "https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token" })
        {
            try
            {
                var rq = (HttpWebRequest)WebRequest.Create(url);
                rq.Method = "POST"; rq.ContentType = "application/json"; rq.Timeout = 15000;
                rq.UserAgent = "claude-pixel-widget/1.0";
                byte[] b = Encoding.UTF8.GetBytes(payload);
                using (var s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
                string resp;
                using (var rs = rq.GetResponse()) using (var sr = new StreamReader(rs.GetResponseStream())) resp = sr.ReadToEnd();
                var j = (Dictionary<string, object>)js.DeserializeObject(resp);
                object at, rt, ei;
                if (!j.TryGetValue("access_token", out at) || at == null) continue;
                j.TryGetValue("refresh_token", out rt);
                j.TryGetValue("expires_in", out ei);

                // write back, preserving everything else in the file
                var root = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(credPath));
                var o = (Dictionary<string, object>)root["claudeAiOauth"];
                o["accessToken"] = at;
                if (rt != null) o["refreshToken"] = rt;
                if (ei != null) o["expiresAt"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds + Convert.ToInt64(ei) * 1000;
                string tmp = credPath + ".pixeltmp";
                File.WriteAllText(tmp, js.Serialize(root));
                File.Copy(tmp, credPath, true);
                File.Delete(tmp);
                return true;
            }
            catch { }
        }
        return false;
    }

    List<Limit> ParseUsage(string body)
    {
        var d = js.DeserializeObject(body) as Dictionary<string, object>;
        if (d == null) return null;
        var list = new List<Limit>();

        // Newer API shape: "limits": [{kind, percent, resets_at, scope:{model:{display_name}}}]
        object la;
        if (d.TryGetValue("limits", out la) && la is System.Collections.IEnumerable)
        {
            foreach (var o in (System.Collections.IEnumerable)la)
            {
                var e = o as Dictionary<string, object>;
                if (e == null || !e.ContainsKey("percent") || e["percent"] == null) continue;
                string kind = Convert.ToString(e.ContainsKey("kind") ? e["kind"] : "");
                string label = kind == "session" ? "SESS" : kind == "weekly_all" ? "WEEK" : null;
                if (label == null)
                {
                    string name = null;
                    var sc = e.ContainsKey("scope") ? e["scope"] as Dictionary<string, object> : null;
                    if (sc != null)
                    {
                        var m = sc.ContainsKey("model") ? sc["model"] as Dictionary<string, object> : null;
                        if (m != null && m.ContainsKey("display_name")) name = m["display_name"] as string;
                        var sf = sc.ContainsKey("surface") ? sc["surface"] as Dictionary<string, object> : null;
                        if (name == null && sf != null && sf.ContainsKey("display_name")) name = sf["display_name"] as string;
                    }
                    label = (name ?? kind).Replace(" ", "").ToUpperInvariant();
                }
                if (label.Length > 4) label = label.Substring(0, 4);
                var l = new Limit { Label = label, Pct = Convert.ToDouble(e["percent"]) };
                object ra; DateTime dt;
                if (e.TryGetValue("resets_at", out ra) && ra is string &&
                    DateTime.TryParse((string)ra, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
                    l.Reset = dt;
                list.Add(l);
            }
            if (list.Count > 0) return list;
        }

        var order = new List<string>();
        if (d.ContainsKey("five_hour")) order.Add("five_hour");
        if (d.ContainsKey("seven_day")) order.Add("seven_day");
        foreach (var k in d.Keys) if (k.StartsWith("seven_day_") && !order.Contains(k)) order.Add(k);

        foreach (string k in order)
        {
            var e = d[k] as Dictionary<string, object>;
            if (e == null || !e.ContainsKey("utilization") || e["utilization"] == null) continue;
            var l = new Limit();
            l.Label = k == "five_hour" ? "SESS" : k == "seven_day" ? "WEEK" : k.Substring(10).Replace("_", "").ToUpperInvariant();
            if (l.Label.Length > 4) l.Label = l.Label.Substring(0, 4);
            l.Pct = Convert.ToDouble(e["utilization"]);
            object ra;
            DateTime dt;
            if (e.TryGetValue("resets_at", out ra) && ra is string &&
                DateTime.TryParse((string)ra, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
                l.Reset = dt;
            list.Add(l);
        }
        return list;
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { anim.Stop(); SaveCfg(); base.OnFormClosing(e); }
}
