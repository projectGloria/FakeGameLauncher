using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiscordQuestLauncher
{
    public static class Program
    {
        public const string BuildId = "v18-helper-hwnd";
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        public static string GetWindowClassName(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static IntPtr FindProcessWindow(int processId)
        {
            // Process.MainWindowHandle returns 0 for the helper: ShowInTaskbar=false gives the form a
            // hidden WinForms parking-window owner, and MainWindowHandle skips owned windows.
            // "First titled window" is NOT good enough either: every WinForms process also owns hidden
            // top-level "GDI+ Window (x.exe)", ".NET-BroadcastEventWindow..." and "Default IME" windows,
            // and the helper form sits at HWND_BOTTOM, so EnumWindows (z-order) hits those first.
            // Grabbing the GDI+ window meant SW_SHOWNOACTIVATE turned it into a second taskbar button
            // while minimize hid it instead of the real 360x480 form, leaving a blank window on screen.
            // Only a titled WindowsForms10.Window.* is the actual Form (the parking window is untitled).
            IntPtr result = IntPtr.Zero;
            EnumWindows((window, parameter) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)processId || GetWindowTextLength(window) == 0) return true;
                if (!GetWindowClassName(window).StartsWith("WindowsForms10.Window.", StringComparison.Ordinal)) return true;
                result = window;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        public static void DragWindow(IntPtr handle)
        {
            try
            {
                ReleaseCapture();
                SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
            catch { }
        }

        // Current window corner radius (8 when Windows 11 rounds the window itself, 16 for the Region fallback)
        public static int WindowRadius = 16;

        private static bool IsWindows11OrNewer()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null);
                int build;
                return v != null && int.TryParse(v.ToString(), out build) && build >= 22000;
            }
            catch { return false; }
        }

        // Windows 11: let DWM round the window. Unlike Region clipping, DWM corners are anti-aliased.
        public static bool TryEnableDwmRounding(IntPtr handle)
        {
            if (!IsWindows11OrNewer()) return false;
            try
            {
                int round = 2; // DWMWCP_ROUND
                int hr = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
                if (hr != 0) return false;

                // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE, so DWM draws no extra border; the app paints its own.
                int none = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(handle, 34, ref none, sizeof(int));
                WindowRadius = 8;
                return true;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Moves hWnd to (x, y) and puts it directly beneath 'above' in the z-order, without resizing or activating it.
        public static void PlaceBelow(IntPtr hWnd, IntPtr above, int x, int y)
        {
            try { SetWindowPos(hWnd, above, x, y, 0, 0, 0x0001 | 0x0010); } catch { }
        }

        // launch_mode.txt next to the exe: "classic" (original two-window behaviour),
        // "single" (default: game window sits visibly under the launcher, no extra taskbar button),
        // "behind" (same, but with its own taskbar button) or "hidden" (invisible; Discord does not detect it).
        public static string GetLaunchMode()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launch_mode.txt");
                if (File.Exists(path))
                {
                    string m = File.ReadAllText(path).Trim().ToLowerInvariant();
                    if (m == "classic" || m == "hidden" || m == "behind" || m == "single") return m;
                }
            }
            catch { }
            return "single";
        }

        public static void EnableImmersiveDarkMode(IntPtr handle)
        {
            try
            {
                int darkMode = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Win10 20H1+ and Win11
                int hr = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
                if (hr != 0)
                {
                    DwmSetWindowAttribute(handle, 19, ref darkMode, sizeof(int));
                }
                // 33 = DWMWA_WINDOW_CORNER_PREFERENCE (2 = DWMWCP_ROUND)
                int cornerRound = 2;
                DwmSetWindowAttribute(handle, 33, ref cornerRound, sizeof(int));
            }
            catch { }
        }

        public static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void LogError(string context, Exception ex)
        {
            if (ex == null) return;
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordQuestLauncher");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "launcher.log");
                string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}\n{3}\n\n",
                    DateTime.Now, context ?? "General", ex.Message, ex.ToString());
                File.AppendAllText(logPath, entry);
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            catch { }
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
            }
            catch { }

            try { Program.LogError("Startup", new Exception("Build " + BuildId + " args=" + string.Join(" ", args ?? new string[0]))); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    Exception ex = e.ExceptionObject as Exception;
                    LogError("UnhandledException", ex ?? new Exception(e.ExceptionObject.ToString()));
                }
                catch { }
            };

            Application.ThreadException += (s, e) =>
            {
                try
                {
                    LogError("ThreadException", e.Exception);
                }
                catch { }
            };

            try
            {
                if (args != null && args.Length >= 2 && args[0].Equals("--dummy", StringComparison.OrdinalIgnoreCase))
                {
                    Application.Run(new DummyHostForm(args));
                }
                else
                {
                    Application.Run(new MainForm(args));
                }
            }
            catch (Exception ex)
            {
                try { LogError("MainCatch", ex); } catch { }
            }
        }
    }

    #region Hidden Dummy Host

    /// <summary>
    /// Runs inside the game-named copy of the launcher. It has no visible UI: an off-screen,
    /// fully transparent tool window (no taskbar button, no Alt-Tab entry) that keeps the Rich
    /// Presence alive and exits automatically when the launcher window that started it is gone.
    /// Args: --dummy &lt;parentPid&gt; &lt;displayName&gt; &lt;appId&gt; &lt;iconHash&gt;
    /// </summary>
    public class DummyHostForm : Form
    {
        private readonly int parentPid;
        private readonly string appName;
        private readonly string appId;
        private readonly bool visibleMode;
        private readonly bool taskbarButton;
        private System.Windows.Forms.Timer watchTimer;

        public DummyHostForm(string[] args)
        {
            int pid;
            parentPid = (args.Length > 1 && int.TryParse(args[1], out pid)) ? pid : 0;
            appName = args.Length > 2 ? args[2] : "Game";
            appId = args.Length > 3 ? args[3] : "";
            string dmode = args.Length > 5 ? args[5].ToLowerInvariant() : "hidden";
            visibleMode = (dmode == "behind" || dmode == "single");
            taskbarButton = (dmode == "behind");

            this.Text = appName;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;

            if (visibleMode)
            {
                // Normal, fully visible window - placed under the launcher window by the launcher.
                int x, y;
                if (!(args.Length > 7 && int.TryParse(args[6], out x) && int.TryParse(args[7], out y))) { x = 100; y = 100; }
                this.ShowInTaskbar = taskbarButton;
                this.BackColor = Theme.BgBase;
                this.ClientSize = new Size(420, 480);
                this.Location = new Point(x, y);
            }
            else
            {
                this.ShowInTaskbar = false;
                this.Location = new Point(-32000, -32000);
                this.Size = new Size(1, 1);
                this.Opacity = 0;
            }

            watchTimer = new System.Windows.Forms.Timer();
            watchTimer.Interval = 2000;
            watchTimer.Tick += (s, e) =>
            {
                if (!ParentAlive()) this.Close();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (!taskbarButton) cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: hidden from Alt-Tab
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        private bool ParentAlive()
        {
            if (parentPid <= 0) return true;
            try
            {
                using (Process p = Process.GetProcessById(parentPid))
                {
                    return !p.HasExited;
                }
            }
            catch { return false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // HWND_BOTTOM before the window is ever shown, so it can never flash above the launcher.
            if (visibleMode) Program.SetWindowPos(this.Handle, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (visibleMode) Program.SetWindowPos(this.Handle, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Program.SetWindowText(this.Handle, appName);
            watchTimer.Start();
            try { Program.LogError("Dummy", new Exception("started name=" + appName + " visible=" + visibleMode + " parent=" + parentPid + " appId=" + appId)); } catch { }
            // The parent owns RPC, including reconnection and visible diagnostics.
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (watchTimer != null) { watchTimer.Stop(); watchTimer.Dispose(); watchTimer = null; }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            MainForm.ScheduleTempCleanup(Application.ExecutablePath);
            Application.Exit();
        }
    }

    #endregion

    #region Color Palette & Typography

    public static class Theme
    {
        // Core Palette
        public static readonly Color BgBase        = Color.FromArgb(11, 11, 15);    // #0B0B0F
        public static readonly Color BgSurface     = Color.FromArgb(18, 18, 25);    // Floating surface
        public static readonly Color BgInput       = Color.FromArgb(16, 16, 23);
        public static readonly Color BgCardHover   = Color.FromArgb(28, 28, 38);

        // Text
        public static readonly Color TextPrimary   = Color.FromArgb(237, 237, 242); // #EDEDF2
        public static readonly Color TextMuted     = Color.FromArgb(110, 108, 122); // #6E6C7A
        public static readonly Color TextDim       = Color.FromArgb(70, 68, 80);

        // Accent Gradient: violet to blue
        public static readonly Color AccentViolet  = Color.FromArgb(124, 92, 255);  // #7C5CFF
        public static readonly Color AccentBlue    = Color.FromArgb(59, 130, 246);  // #3B82F6
        public static readonly Color AccentFocus   = Color.FromArgb(124, 92, 255);  // #7C5CFF

        // Status
        public static readonly Color StatusGreen   = Color.FromArgb(61, 220, 132);  // #3DDC84

        // Borders
        public static readonly Color BorderSubtle  = Color.FromArgb(28, 28, 38);
        public static readonly Color BorderHover   = Color.FromArgb(48, 48, 65);
    }

    public static class FontManager
    {
        private static Font _nameFont;
        private static Font _buttonFont;
        private static Font _exeFont;
        private static Font _timerFont;
        private static Font _searchFont;
        private static Font _smallFont;
        private static Font _listExeFont;
        private static Font _queueTinyFont;
        private static Font _cardTitleFont;
        private static Font _badgeFont;

        public static Font NameFont
        {
            get { return _nameFont ?? (_nameFont = ResolveFont(new string[] { "Inter", "Geist", "Segoe UI Variable Text", "Segoe UI" }, 18f, FontStyle.Bold)); }
        }

        public static Font ButtonFont
        {
            get { return _buttonFont ?? (_buttonFont = ResolveFont(new string[] { "Inter", "Geist", "Segoe UI Variable Display", "Segoe UI" }, 12.5f, FontStyle.Bold)); }
        }

        public static Font ExeFont
        {
            get { return _exeFont ?? (_exeFont = ResolveFont(new string[] { "Geist Mono", "JetBrains Mono", "Cascadia Code", "Consolas" }, 12f, FontStyle.Regular)); }
        }

        public static Font TimerFont
        {
            get { return _timerFont ?? (_timerFont = ResolveFont(new string[] { "Geist Mono", "JetBrains Mono", "Cascadia Code", "Consolas" }, 12f, FontStyle.Bold)); }
        }

        public static Font SearchFont
        {
            get { return _searchFont ?? (_searchFont = ResolveFont(new string[] { "Inter", "Geist", "Segoe UI" }, 14f, FontStyle.Regular)); }
        }

        public static Font SmallFont
        {
            get { return _smallFont ?? (_smallFont = ResolveFont(new string[] { "Inter", "Geist", "Segoe UI" }, 10f, FontStyle.Regular)); }
        }

        // Secondary exe line in dense lists (search dropdown). ExeFont (12pt) is sized for the hero card
        // and would out-weigh the 10pt title above it; this keeps title > exe hierarchy in a 44px row.
        public static Font ListExeFont
        {
            get { return _listExeFont ?? (_listExeFont = ResolveFont(new string[] { "Geist Mono", "JetBrains Mono", "Cascadia Code", "Consolas" }, 8.5f, FontStyle.Regular)); }
        }

        public static Font QueueTinyFont
        {
            get { return _queueTinyFont ?? (_queueTinyFont = ResolveFont(new string[] { "Geist Mono", "JetBrains Mono", "Cascadia Code", "Consolas" }, 7.25f, FontStyle.Bold)); }
        }

        public static Font CardTitleFont
        {
            get { return _cardTitleFont ?? (_cardTitleFont = ResolveFont(new string[] { "Inter", "Geist", "Segoe UI" }, 10.5f, FontStyle.Bold)); }
        }

        public static Font BadgeFont
        {
            get { return _badgeFont ?? (_badgeFont = ResolveFont(new string[] { "Inter", "Geist", "Segoe UI" }, 8f, FontStyle.Bold)); }
        }

        private static Font ResolveFont(string[] familyNames, float size, FontStyle style)
        {
            foreach (string name in familyNames)
            {
                try
                {
                    using (FontFamily ff = new FontFamily(name))
                    {
                        return new Font(name, size, style);
                    }
                }
                catch { }
            }
            return new Font("Segoe UI", size, style);
        }
    }

    #endregion

    #region Data Models

    public class GameItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string exe { get; set; }
        public string executable_path { get; set; }

        private string _normName;
        private string _normExe;
        private string _initials;

        public void PrecomputeSearchFields()
        {
            _normName = GameDatabase.NormalizeString(name);
            _normExe = GameDatabase.NormalizeString(exe);
            _initials = GameDatabase.GetInitials(name);
        }

        public string NormalizedName
        {
            get
            {
                if (_normName == null) PrecomputeSearchFields();
                return _normName;
            }
        }

        public string NormalizedExe
        {
            get
            {
                if (_normExe == null) PrecomputeSearchFields();
                return _normExe;
            }
        }

        public string Initials
        {
            get
            {
                if (_initials == null) PrecomputeSearchFields();
                return _initials;
            }
        }

        public string GetIconUrl(int size = 1024)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(icon)) return null;
            return string.Format("https://cdn.discordapp.com/app-icons/{0}/{1}.png?size={2}", id, icon, Math.Max(size, 1024));
        }

        public override string ToString()
        {
            return name ?? "Unknown Game";
        }
    }

    public class QualifiedGameEntry
    {
        public string id { get; set; }
        public string name { get; set; }
        public string icon { get; set; }
        public string exe { get; set; }
        public int total_seconds_played { get; set; }
        public string last_played { get; set; }
    }

    public class GameCacheData
    {
        public long cached_at { get; set; }
        public List<GameItem> games { get; set; }
    }

    /// <summary>
    /// Represents a single task requirement inside a Discord Quest (e.g. PLAY_ON_DESKTOP, WATCH_VIDEO).
    /// </summary>
    public class QuestTaskInfo
    {
        public string type { get; set; }
        public int target { get; set; }
        public string application_id { get; set; }

        public bool IsDesktopPlay
        {
            get { return string.Equals(type, "PLAY_ON_DESKTOP", StringComparison.OrdinalIgnoreCase); }
        }

        public string GetDisplayLabel()
        {
            string kind;
            string t = (type ?? "").ToUpperInvariant();
            if (t == "PLAY_ON_DESKTOP") kind = "Desktop";
            else if (t == "WATCH_VIDEO") kind = "Video";
            else if (t == "WATCH_VIDEO_ON_MOBILE") kind = "Mobile Video";
            else if (t == "PLAY_ON_PLAYSTATION") kind = "PlayStation";
            else if (t == "PLAY_ON_XBOX") kind = "Xbox";
            else if (t == "ACHIEVEMENT_IN_ACTIVITY") kind = "Activity";
            else kind = string.IsNullOrEmpty(t) ? "Task" : t.Replace('_', ' ');

            if (target <= 0) return kind;
            return kind + " \u00B7 " + FormatDuration(target);
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 60) return seconds + "s";
            int mins = seconds / 60;
            int rem = seconds % 60;
            if (rem == 0) return mins + "m";
            return mins + "m " + rem + "s";
        }
    }

    /// <summary>
    /// Represents a reward granted by completing a Discord Quest.
    /// </summary>
    public class QuestRewardInfo
    {
        public int type { get; set; }
        public string name { get; set; }
        public int orb_quantity { get; set; }
        public string asset { get; set; }
        public string redemption_instructions { get; set; }

        public bool IsOrbs
        {
            get { return type == 4 || orb_quantity > 0; }
        }

        public bool IsCollectible
        {
            get { return type == 3; }
        }

        public string GetTypeLabel()
        {
            if (IsOrbs) return "Discord Orbs";
            if (IsCollectible) return "Collectible";
            if (type == 1) return "In-Game Reward";
            return "Reward";
        }
    }

    /// <summary>
    /// Regional eligibility and age-gate metadata merged from the region tracker gist.
    /// </summary>
    public class QuestRegionInfo
    {
        public bool show_age_gate { get; set; }
        public bool is_global { get; set; }
        public List<string> include { get; set; }
        public List<string> exclude { get; set; }
    }

    /// <summary>
    /// Normalized Discord Quest catalog entry combining quest.json and regional eligibility metadata.
    /// </summary>
    public class QuestItem
    {
        public string id { get; set; }
        public string quest_name { get; set; }
        public string game_title { get; set; }
        public string game_publisher { get; set; }
        public string application_id { get; set; }
        public string application_name { get; set; }
        public string application_link { get; set; }
        public string starts_at { get; set; }
        public string expires_at { get; set; }
        public string hero_asset { get; set; }
        public string tile_asset { get; set; }
        public List<QuestTaskInfo> tasks { get; set; }
        public List<QuestRewardInfo> rewards { get; set; }
        public QuestRegionInfo region { get; set; }

        public DateTimeOffset StartsAtUtc
        {
            get
            {
                DateTimeOffset dto;
                if (!string.IsNullOrEmpty(starts_at) && DateTimeOffset.TryParse(starts_at, out dto))
                    return dto.ToUniversalTime();
                return DateTimeOffset.MinValue;
            }
        }

        public DateTimeOffset ExpiresAtUtc
        {
            get
            {
                DateTimeOffset dto;
                if (!string.IsNullOrEmpty(expires_at) && DateTimeOffset.TryParse(expires_at, out dto))
                    return dto.ToUniversalTime();
                return DateTimeOffset.MinValue;
            }
        }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(quest_name)) return quest_name;
                if (!string.IsNullOrWhiteSpace(game_title)) return game_title;
                if (!string.IsNullOrWhiteSpace(application_name)) return application_name;
                return "Discord Quest";
            }
        }

        public string DisplayGameName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(game_title)) return game_title;
                if (!string.IsNullOrWhiteSpace(application_name)) return application_name;
                return DisplayTitle;
            }
        }

        public bool IsExpired(DateTimeOffset nowUtc)
        {
            DateTimeOffset exp = ExpiresAtUtc;
            if (exp == DateTimeOffset.MinValue) return false;
            return exp <= nowUtc;
        }

        public bool IsActive(DateTimeOffset nowUtc)
        {
            DateTimeOffset exp = ExpiresAtUtc;
            if (exp == DateTimeOffset.MinValue) return false;
            return exp > nowUtc;
        }

        public bool IsEndingSoon(DateTimeOffset nowUtc)
        {
            if (!IsActive(nowUtc)) return false;
            return (ExpiresAtUtc - nowUtc) <= TimeSpan.FromHours(72);
        }

        public bool HasDesktopTask
        {
            get
            {
                if (tasks == null) return false;
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (tasks[i] != null && tasks[i].IsDesktopPlay) return true;
                }
                return false;
            }
        }

        public int DesktopTaskTargetSeconds
        {
            get
            {
                if (tasks == null) return 0;
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (tasks[i] != null && tasks[i].IsDesktopPlay) return tasks[i].target;
                }
                return 0;
            }
        }

        public string DesktopApplicationId
        {
            get
            {
                if (tasks != null)
                {
                    for (int i = 0; i < tasks.Count; i++)
                    {
                        if (tasks[i] != null && tasks[i].IsDesktopPlay && !string.IsNullOrEmpty(tasks[i].application_id))
                            return tasks[i].application_id;
                    }
                }
                return application_id ?? "";
            }
        }

        public bool HasOrbsReward
        {
            get
            {
                if (rewards == null) return false;
                for (int i = 0; i < rewards.Count; i++)
                {
                    if (rewards[i] != null && rewards[i].IsOrbs) return true;
                }
                return false;
            }
        }

        public bool HasCollectibleReward
        {
            get
            {
                if (rewards == null) return false;
                for (int i = 0; i < rewards.Count; i++)
                {
                    if (rewards[i] != null && rewards[i].IsCollectible) return true;
                }
                return false;
            }
        }

        public bool IsAgeRestricted
        {
            get { return region != null && region.show_age_gate; }
        }

        public bool IsEligibleInRegion(string countryCode)
        {
            string norm = QuestDatabase.NormalizeCountryCode(countryCode);
            if (string.IsNullOrEmpty(norm)) return true;
            if (region == null) return true;
            if (region.exclude != null && region.exclude.Contains(norm)) return false;
            if (region.is_global) return true;
            if (region.include != null && region.include.Count > 0) return region.include.Contains(norm);
            return true;
        }

        public string GetRegionSummary()
        {
            if (region == null) return "Global";
            if (region.is_global)
            {
                if (region.exclude != null && region.exclude.Count > 0)
                    return "Global (excl. " + FormatCountryPreview(region.exclude, 2) + ")";
                return "Global";
            }
            if (region.include != null && region.include.Count > 0)
                return "Allowed: " + FormatCountryPreview(region.include, 3);
            if (region.exclude != null && region.exclude.Count > 0)
                return "Excluded: " + FormatCountryPreview(region.exclude, 2);
            return "Global";
        }

        private static string FormatCountryPreview(List<string> list, int maxItems)
        {
            if (list == null || list.Count == 0) return "";
            int take = Math.Min(maxItems, list.Count);
            string[] slice = new string[take];
            for (int i = 0; i < take; i++) slice[i] = list[i];
            string text = string.Join(", ", slice);
            if (list.Count > take) text += " +" + (list.Count - take);
            return text;
        }

        public string GetCountdownLabel(DateTimeOffset nowUtc)
        {
            DateTimeOffset exp = ExpiresAtUtc;
            if (exp == DateTimeOffset.MinValue) return "Unknown";
            TimeSpan diff = exp - nowUtc;
            if (diff.TotalSeconds <= 0) return "Expired";
            if (diff.TotalDays >= 1.0) return ((int)Math.Floor(diff.TotalDays)) + "d left";
            if (diff.TotalHours >= 1.0) return ((int)Math.Floor(diff.TotalHours)) + "h left";
            int mins = Math.Max(1, (int)Math.Floor(diff.TotalMinutes));
            return mins + "m left";
        }

        public string GetPrimaryRewardLabel()
        {
            if (rewards == null || rewards.Count == 0 || rewards[0] == null) return "Reward";
            if (!string.IsNullOrWhiteSpace(rewards[0].name)) return rewards[0].name;
            if (rewards[0].orb_quantity > 0) return rewards[0].orb_quantity + " Orbs";
            return rewards[0].GetTypeLabel();
        }

        public string GetPrimaryTaskLabel()
        {
            if (tasks == null || tasks.Count == 0) return "Quest";
            for (int i = 0; i < tasks.Count; i++)
            {
                if (tasks[i] != null && tasks[i].IsDesktopPlay) return tasks[i].GetDisplayLabel();
            }
            return tasks[0] != null ? tasks[0].GetDisplayLabel() : "Quest";
        }

        public string GetDiscordQuestUrl()
        {
            return string.IsNullOrEmpty(id) ? "https://discord.com/quests" : ("https://discord.com/quests/" + id);
        }

        public string GetThumbnailAssetPath()
        {
            if (!string.IsNullOrEmpty(tile_asset) && !string.Equals(tile_asset, "PLACEHOLDER", StringComparison.OrdinalIgnoreCase))
                return tile_asset;
            if (!string.IsNullOrEmpty(hero_asset) && !string.Equals(hero_asset, "PLACEHOLDER", StringComparison.OrdinalIgnoreCase))
                return hero_asset;
            return null;
        }
    }

    public class QuestUserState
    {
        public List<string> completed_ids { get; set; }
        public Dictionary<string, int> progress_seconds { get; set; }
        public string region_override { get; set; }
        public bool filter_region_only { get; set; }
        public bool hide_completed { get; set; }
    }

    public class QuestCacheEnvelope
    {
        public long cached_at { get; set; }
        public string quests_json { get; set; }
        public string regions_json { get; set; }
    }

    #endregion

    #region Discord Named Pipe RPC Client

    public class DiscordRpcClient : IDisposable
    {
        private volatile NamedPipeClientStream _pipe;
        private readonly object _pipeLock = new object();
        private readonly string _clientId;
        private readonly int _activityProcessId;
        private int _nonce = 0;
        private volatile bool _disposed = false;
        private string _lastLoggedError;
        private volatile string _lastError;
        public string LastError { get { return _lastError; } }

        private void ReportRpcError(string message)
        {
            _lastError = message;
            // Avoid repeating the same error on every reconnect attempt.
            if (message == _lastLoggedError) return;
            _lastLoggedError = message;
            Program.LogError("DiscordRpc", new Exception("appId=" + _clientId + " " + message));
        }

        public bool IsConnected
        {
            get
            {
                NamedPipeClientStream pipe = _pipe;
                try { return !_disposed && pipe != null && pipe.IsConnected; }
                catch (ObjectDisposedException) { return false; }
            }
        }

        public DiscordRpcClient(string clientId) : this(clientId, Process.GetCurrentProcess().Id)
        {
        }

        public DiscordRpcClient(string clientId, int activityProcessId)
        {
            _clientId = clientId ?? "";
            if (activityProcessId <= 0) throw new ArgumentOutOfRangeException("activityProcessId");
            _activityProcessId = activityProcessId;
        }

        public bool Connect(int timeoutMs = 2000)
        {
            if (_disposed) return false;
            if (string.IsNullOrWhiteSpace(_clientId))
            {
                ReportRpcError("This game has no Discord application ID.");
                return false;
            }
            if (IsConnected) return true;
            _lastError = null;
            string connectionError = null;
            for (int pipeIdx = 0; pipeIdx < 10; pipeIdx++)
            {
                NamedPipeClientStream stream = null;
                bool connected = false;
                try
                {
                    string pipeName = "discord-ipc-" + pipeIdx;
                    stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    lock (_pipeLock)
                    {
                        if (_disposed) return false;
                        _pipe = stream;
                    }
                    stream.Connect(timeoutMs > 200 ? 200 : timeoutMs);
                    if (!_disposed && stream.IsConnected && Handshake())
                    {
                        connected = true;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // Missing pipe slots are normal; retain useful failures such as access denied.
                    if (!(ex is TimeoutException)) connectionError = ex.Message;
                }
                finally
                {
                    if (!connected)
                    {
                        if (stream != null) stream.Dispose();
                        lock (_pipeLock)
                        {
                            if (object.ReferenceEquals(_pipe, stream)) _pipe = null;
                        }
                    }
                }
                if (_disposed) return false;
            }
            if (string.IsNullOrEmpty(_lastError))
                ReportRpcError(connectionError == null ? "Discord desktop IPC is unavailable. Open Discord and check for updates." : "Discord IPC connection failed: " + connectionError);
            return false;
        }

        private bool Handshake()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> handshake = new Dictionary<string, object>();
                handshake["v"] = 1;
                handshake["client_id"] = _clientId;
                string json = ser.Serialize(handshake);
                WriteFrame(0, json);
                string response = ReadFrame();
                if (string.IsNullOrEmpty(response)) return false;
                Dictionary<string, object> payload = ser.Deserialize<Dictionary<string, object>>(response);
                if (payload.ContainsKey("evt") && object.Equals(payload["evt"], "READY"))
                {
                    _lastError = null;
                    return true;
                }
                ReportRpcError("Handshake rejected: " + response);
                return false;
            }
            catch (Exception ex)
            {
                ReportRpcError("Handshake failed: " + ex.Message);
                return false;
            }
        }

        public bool SetActivity(string details, string state, string largeImageKey, string largeImageText, long startTimestamp)
        {
            if (!IsConnected) return false;
            try
            {
                _nonce++;
                JavaScriptSerializer ser = new JavaScriptSerializer();

                Dictionary<string, object> timestamps = new Dictionary<string, object>();
                if (startTimestamp > 0) timestamps["start"] = startTimestamp;

                Dictionary<string, object> assets = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(largeImageKey))
                {
                    assets["large_image"] = largeImageKey;
                    if (!string.IsNullOrEmpty(largeImageText)) assets["large_text"] = largeImageText;
                }

                Dictionary<string, object> activity = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(details)) activity["details"] = details;
                if (!string.IsNullOrEmpty(state)) activity["state"] = state;
                if (timestamps.Count > 0) activity["timestamps"] = timestamps;
                if (assets.Count > 0) activity["assets"] = assets;
                activity["type"] = 0;

                Dictionary<string, object> args = new Dictionary<string, object>();
                args["pid"] = _activityProcessId;
                args["activity"] = activity;

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["cmd"] = "SET_ACTIVITY";
                payload["args"] = args;
                payload["nonce"] = _nonce.ToString();

                string json = ser.Serialize(payload);
                WriteFrame(1, json);
                // A successful write is not an accepted Rich Presence update.
                // Consume replies so ERROR messages and disconnects are visible.
                for (int i = 0; i < 10; i++)
                {
                    string response = ReadFrame();
                    if (string.IsNullOrEmpty(response)) throw new IOException("Discord closed the connection.");
                    Dictionary<string, object> reply = ser.Deserialize<Dictionary<string, object>>(response);
                    if (reply.ContainsKey("evt") && object.Equals(reply["evt"], "ERROR"))
                    {
                        ReportRpcError("Activity rejected: " + response);
                        return false;
                    }
                    if (reply.ContainsKey("nonce") && object.Equals(reply["nonce"], payload["nonce"]))
                    {
                        _lastLoggedError = null;
                        _lastError = null;
                        return true;
                    }
                }
                throw new IOException("Discord did not acknowledge the activity update.");
            }
            catch (Exception ex)
            {
                ReportRpcError("Activity update failed: " + ex.Message);
                try { if (_pipe != null) _pipe.Dispose(); } catch { }
                _pipe = null;
                return false;
            }
        }

        private void WriteFrame(int opcode, string json)
        {
            if (_pipe == null || !_pipe.IsConnected) return;
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(jsonBytes.Length).CopyTo(header, 4);

            _pipe.Write(header, 0, 8);
            _pipe.Write(jsonBytes, 0, jsonBytes.Length);
            _pipe.Flush();
        }

        private string ReadFrame()
        {
            if (_pipe == null || !_pipe.IsConnected) return null;
            for (int frame = 0; frame < 10; frame++)
            {
                byte[] header = new byte[8];
                ReadExactly(header);
                int opcode = BitConverter.ToInt32(header, 0);
                int length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 65536) throw new IOException("Invalid Discord frame length.");
                byte[] buffer = new byte[length];
                ReadExactly(buffer);
                if (opcode == 3)
                {
                    // PING must be answered with the exact same payload in PONG.
                    BitConverter.GetBytes(4).CopyTo(header, 0);
                    _pipe.Write(header, 0, header.Length);
                    _pipe.Write(buffer, 0, buffer.Length);
                    _pipe.Flush();
                    continue;
                }
                if (opcode == 4) continue;
                string json = Encoding.UTF8.GetString(buffer, 0, length);
                if (opcode == 2) throw new IOException("Discord closed RPC: " + json);
                if (opcode != 1) throw new IOException("Unexpected Discord opcode: " + opcode);
                return json;
            }
            throw new IOException("Too many Discord control frames.");
        }

        private void ReadExactly(byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                IAsyncResult pending = _pipe.BeginRead(buffer, offset, buffer.Length - offset, null, null);
                using (WaitHandle handle = pending.AsyncWaitHandle)
                {
                    if (!handle.WaitOne(5000))
                    {
                        _pipe.Dispose();
                        throw new IOException("Timed out waiting for Discord RPC.");
                    }
                    int count = _pipe.EndRead(pending);
                    if (count <= 0) throw new IOException("Discord RPC reached end of stream.");
                    offset += count;
                }
            }
        }

        public void Dispose()
        {
            NamedPipeClientStream pipe;
            lock (_pipeLock)
            {
                if (_disposed) return;
                _disposed = true;
                pipe = _pipe;
                _pipe = null;
            }
            if (pipe != null) pipe.Dispose();
        }
    }

    #endregion

    #region Game Database

    public class GameDatabase
    {
        private static GameDatabase _instance;
        public static GameDatabase Instance
        {
            get
            {
                if (_instance == null) _instance = new GameDatabase();
                return _instance;
            }
        }

        private List<GameItem> _games = new List<GameItem>();
        private readonly object _lock = new object();
        private bool _isUpdating = false;

        public event Action OnDatabaseUpdated;
        public event Action OnQualifiedGamesChanged;
        public event Action OnInitializationCompleted;
        private bool _initializationCompleted;

        public bool IsInitialized
        {
            get { lock (_lock) return _initializationCompleted; }
        }

        private void CompleteInitialization()
        {
            lock (_lock)
            {
                if (_initializationCompleted) return;
                _initializationCompleted = true;
            }
            Action completed = OnInitializationCompleted;
            if (completed != null)
            {
                try { completed(); }
                catch (Exception ex) { Program.LogError("InitializationCompleted", ex); }
            }
        }

        public int Count
        {
            get { lock (_lock) return _games.Count; }
        }

        public bool IsUpdating
        {
            get { lock (_lock) return _isUpdating; }
        }

        public string CacheDirectory
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordQuestLauncher");
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        public string CacheFilePath
        {
            get { return Path.Combine(CacheDirectory, "games_cache.json"); }
        }

        public string QualifiedGamesFilePath
        {
            get { return Path.Combine(CacheDirectory, "qualified_games.json"); }
        }

        public string CustomGamesFilePath
        {
            get { return Path.Combine(CacheDirectory, "custom_games.json"); }
        }

        public string LastSimulatedFilePath
        {
            get { return Path.Combine(CacheDirectory, "last_simulated.json"); }
        }

        private List<GameItem> _customGames = new List<GameItem>();

        public void LoadCustomGames()
        {
            try
            {
                string path = CustomGamesFilePath;
                if (!File.Exists(path))
                {
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom_games.json");
                }
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    List<GameItem> items = ser.Deserialize<List<GameItem>>(text);
                    if (items != null && items.Count > 0)
                    {
                        lock (_lock)
                        {
                            _customGames = items;
                            foreach (GameItem cg in items)
                            {
                                if (cg == null || string.IsNullOrWhiteSpace(cg.name)) continue;
                                GameItem existing = _games.Find(x => string.Equals(x.name, cg.name, StringComparison.OrdinalIgnoreCase) ||
                                                                    (!string.IsNullOrEmpty(x.id) && x.id == cg.id));
                                if (existing != null)
                                {
                                    if (string.IsNullOrEmpty(cg.id)) cg.id = existing.id;
                                    if (string.IsNullOrEmpty(cg.icon)) cg.icon = existing.icon;
                                    if (string.IsNullOrEmpty(cg.exe)) cg.exe = existing.exe;
                                    if (string.IsNullOrEmpty(cg.executable_path) && string.Equals(cg.exe, existing.exe, StringComparison.OrdinalIgnoreCase))
                                        cg.executable_path = existing.executable_path;
                                }
                                cg.PrecomputeSearchFields();

                                _games.RemoveAll(x => string.Equals(x.name, cg.name, StringComparison.OrdinalIgnoreCase) ||
                                                      (!string.IsNullOrEmpty(x.id) && x.id == cg.id));
                                _games.Insert(0, cg);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public bool AddCustomGame(GameItem game)
        {
            if (game == null || game.id == "__ADD_CUSTOM__" || string.IsNullOrWhiteSpace(game.name)) return false;
            lock (_lock)
            {
                GameItem existing = _games.Find(x => string.Equals(x.name, game.name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (string.IsNullOrEmpty(game.id)) game.id = existing.id;
                    if (string.IsNullOrEmpty(game.icon)) game.icon = existing.icon;
                }

                game.PrecomputeSearchFields();
                List<GameItem> customGames = new List<GameItem>(_customGames);
                customGames.RemoveAll(x => x == null || string.Equals(x.name, game.name, StringComparison.OrdinalIgnoreCase));
                customGames.Insert(0, game);

                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    string json = ser.Serialize(customGames);
                    SafeAtomicWriteText(CustomGamesFilePath, json);
                    _customGames = customGames;
                    _games.RemoveAll(x => string.Equals(x.name, game.name, StringComparison.OrdinalIgnoreCase));
                    _games.Insert(0, game);
                    try
                    {
                        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom_games.json");
                        SafeAtomicWriteText(localPath, json);
                    }
                    catch (Exception ex) { Program.LogError("CustomGamesMirror", ex); }
                }
                catch (Exception ex)
                {
                    Program.LogError("SaveCustomGames", ex);
                    return false;
                }
            }
            return true;
        }

        private bool _initStarted = false;

        // Non-blocking: parses the 3 MB cache on a worker thread so the window appears immediately.
        public void Initialize()
        {
            lock (_lock)
            {
                if (_initStarted) return;
                _initStarted = true;
            }
            ThreadPool.QueueUserWorkItem(_ => LoadInitialData());
        }

        // Blocking variant, only for the legacy code paths that need the database right away.
        public void InitializeSync()
        {
            lock (_lock)
            {
                if (_initStarted) return;
                _initStarted = true;
            }
            LoadInitialData();
        }

        private void LoadInitialData()
        {
            bool loaded = false;
            try
            {
                string localCache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games_cache.json");
                if (File.Exists(localCache))
                {
                    loaded = TryLoadCacheFile(localCache);
                }

                if (!loaded && File.Exists(CacheFilePath))
                {
                    loaded = TryLoadCacheFile(CacheFilePath);
                }

                LoadCustomGames();
            }
            catch { }

            if (Count > 0 && OnDatabaseUpdated != null)
            {
                try { OnDatabaseUpdated(); } catch { }
            }

            if (Count > 0) CompleteInitialization();

            // Always refresh in the background. Discord can rotate icon hashes and its
            // detectable-games endpoint changed from applications/detectable to games/detectable.
            // Keeping the refresh non-blocking preserves startup speed while ensuring cached
            // game metadata does not become the source of stale icons.
            RefreshFromDiscordAsync();
        }

        private bool TryLoadCacheFile(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return false;

                List<GameItem> parsed = null;
                if (text.TrimStart().StartsWith("["))
                {
                    parsed = ParseDetectableApplications(text);
                }
                else
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    GameCacheData cd = ser.Deserialize<GameCacheData>(text);
                    if (cd != null && cd.games != null && cd.games.Count > 0)
                    {
                        parsed = cd.games;
                    }
                }

                if (parsed != null && parsed.Count > 0)
                {
                    foreach (var g in parsed) g.PrecomputeSearchFields();
                    lock (_lock)
                    {
                        _games = parsed;
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void RefreshFromDiscordAsync()
        {
            lock (_lock)
            {
                if (_isUpdating) return;
                _isUpdating = true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string json = null;
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    string[] endpoints = new string[]
                    {
                        "https://discord.com/api/v10/games/detectable",
                        "https://discord.com/api/v9/games/detectable",
                        "https://discord.com/api/v9/applications/detectable"
                    };

                    foreach (string endpoint in endpoints)
                    {
                        try
                        {
                            using (WebClient wc = new TimeoutWebClient())
                            {
                                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                                string candidate = wc.DownloadString(endpoint);
                                if (!string.IsNullOrWhiteSpace(candidate))
                                {
                                    List<GameItem> testItems = ParseDetectableApplications(candidate);
                                    if (testItems != null && testItems.Count > 0)
                                    {
                                        json = candidate;
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(json))
                    {
                        List<GameItem> items = ParseDetectableApplications(json);
                        if (items != null && items.Count > 0)
                        {
                            foreach (var g in items) g.PrecomputeSearchFields();
                            lock (_lock)
                            {
                                _games = items;
                            }

                            try
                            {
                                GameCacheData cd = new GameCacheData
                                {
                                    cached_at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds,
                                    games = items
                                };
                                JavaScriptSerializer ser = new JavaScriptSerializer();
                                ser.MaxJsonLength = int.MaxValue;
                                string outJson = ser.Serialize(cd);
                                SafeAtomicWriteText(CacheFilePath, outJson);
                            }
                            catch { }

                            LoadCustomGames();

                            if (OnDatabaseUpdated != null)
                            {
                                try { OnDatabaseUpdated(); } catch { }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (_lock) _isUpdating = false;
                    CompleteInitialization();
                }
            });
        }

        private class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                request.Timeout = 10000;
                HttpWebRequest http = request as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = 10000;
                return request;
            }
        }

        public static string InferExecutableFromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "game.exe";
            string n = name.Trim();

            Match m = Regex.Match(n, @"(?:EA\s*Sports\s*)?UFC\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "ufc" + m.Groups[1].Value + ".exe";
            if (Regex.IsMatch(n, @"(?:EA\s*Sports\s*)?UFC$", RegexOptions.IgnoreCase)) return "ufc.exe";

            m = Regex.Match(n, @"\b(?:EA\s*Sports\s+)?FC\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "fc" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"FIFA\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "fifa" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"College\s*Football\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "cfb" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"Madden\s*NFL\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "madden" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"NBA\s*2K(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "nba2k" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"F1\s*(20\d\d|\d\d)", RegexOptions.IgnoreCase);
            if (m.Success) return "f1_" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"WWE\s*2K(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "wwe2k" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"NHL\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "nhl" + m.Groups[1].Value + ".exe";

            m = Regex.Match(n, @"Call\s*of\s*Duty.*Black\s*Ops\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "BlackOps" + m.Groups[1].Value + ".exe";
            m = Regex.Match(n, @"Call\s*of\s*Duty.*Modern\s*Warfare\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return "ModernWarfare" + m.Groups[1].Value + ".exe";

            string clean = Regex.Replace(n, @"[^\w]", "");
            if (!string.IsNullOrEmpty(clean)) return clean + ".exe";
            return "game.exe";
        }

        public static List<GameItem> ParseDetectableApplications(string json)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            object[] rawList = ser.DeserializeObject(json) as object[];
            if (rawList == null) return new List<GameItem>();

            List<GameItem> result = new List<GameItem>(rawList.Length / 2);
            foreach (object rawObj in rawList)
            {
                Dictionary<string, object> item = rawObj as Dictionary<string, object>;
                if (item == null) continue;

                string gameTitle = item.ContainsKey("name") && item["name"] != null ? item["name"].ToString().Trim() : "";
                if (string.IsNullOrEmpty(gameTitle)) continue;

                string cleanTitle = Regex.Replace(gameTitle ?? "", @"[^\w]", "").ToLowerInvariant();
                string bestExe = null;
                string bestPath = null;
                int bestScore = -1000;

                if (item.ContainsKey("executables"))
                {
                    object[] exes = item["executables"] as object[];
                    if (exes != null && exes.Length > 0)
                    {
                        foreach (object exeRaw in exes)
                        {
                            Dictionary<string, object> exeObj = exeRaw as Dictionary<string, object>;
                            if (exeObj == null) continue;

                            if (!exeObj.ContainsKey("os") || !object.Equals(exeObj["os"], "win32")) continue;
                            if (!exeObj.ContainsKey("name") || exeObj["name"] == null) continue;

                            string relativePath = NormalizeExecutablePath(exeObj["name"].ToString());
                            if (relativePath == null) continue;
                            string cleanExe = Path.GetFileName(relativePath);
                            if (string.IsNullOrWhiteSpace(cleanExe)) continue;

                            bool isLauncher = exeObj.ContainsKey("is_launcher") && object.Equals(exeObj["is_launcher"], true);
                            int score = 100;
                            if (isLauncher) score -= 50;

                            string lower = cleanExe.ToLowerInvariant();
                            if (lower.Contains("_be") || lower.Contains("_eac") || lower.Contains("easyanticheat") ||
                                lower.Contains("battleye") || lower.Contains("crash") || lower.Contains("unins") ||
                                lower.Contains("server") || lower.Contains("dedicated") || lower.Contains("editor") ||
                                lower.Contains("tools") || lower.Contains("patcher") || lower.Contains("updater"))
                            {
                                score -= 40;
                            }

                            if (cleanTitle.Length >= 3 && Path.GetFileNameWithoutExtension(cleanExe).ToLowerInvariant().Replace(" ", "").Replace("_", "").Contains(cleanTitle))
                            {
                                score += 35;
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestExe = cleanExe;
                                bestPath = relativePath;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(bestExe))
                {
                    bestExe = InferExecutableFromName(gameTitle);
                }

                if (!string.IsNullOrEmpty(bestExe))
                {
                    string id = item.ContainsKey("id") && item["id"] != null ? item["id"].ToString() : "";
                    string icon = "";
                    if (item.ContainsKey("icon_hash") && item["icon_hash"] != null) icon = item["icon_hash"].ToString();
                    else if (item.ContainsKey("icon") && item["icon"] != null) icon = item["icon"].ToString();

                    GameItem gi = new GameItem
                    {
                        id = id,
                        name = gameTitle,
                        icon = icon,
                        exe = bestExe,
                        executable_path = bestPath ?? bestExe
                    };
                    gi.PrecomputeSearchFields();
                    result.Add(gi);
                }
            }

            return result;
        }

        public static string NormalizeExecutablePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string path = raw.Trim().TrimStart('>').Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(path)) return null;
            string[] parts = path.Split(Path.DirectorySeparatorChar);
            foreach (string part in parts)
            {
                if (string.IsNullOrWhiteSpace(part) || part == "." || part == ".." ||
                    part != part.TrimEnd(' ', '.') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            }
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return path;
        }

        public GameItem FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock)
            {
                return _games.Find(x => string.Equals(x.id, id, StringComparison.Ordinal));
            }
        }

        public GameItem ResolveGameMetadata(GameItem game)
        {
            if (game == null || string.IsNullOrEmpty(game.id)) return game;
            return FindById(game.id) ?? game;
        }

        public static string NormalizeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        public static string GetInitials(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder();
            bool inWord = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c))
                {
                    if (!inWord)
                    {
                        sb.Append(char.ToLowerInvariant(c));
                        inWord = true;
                    }
                }
                else
                {
                    inWord = false;
                }
            }
            return sb.ToString();
        }

        public static string CleanSearchQuery(string query)
        {
            if (string.IsNullOrEmpty(query)) return "";
            string q = query.Trim();
            if (q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Substring(0, q.Length - 4);
            }
            return q.Trim();
        }

        public static List<string> ExpandAliases(string query)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(query)) return list;
            string q = query.ToLowerInvariant().Trim();

            if (q == "cod") { list.Add("call of duty"); }
            if (q == "bf") { list.Add("battlefield"); }
            if (q == "cs" || q == "csgo" || q == "cs2") { list.Add("counter-strike"); }
            if (q == "lol") { list.Add("league of legends"); }
            if (q == "r6" || q == "r6s") { list.Add("rainbow six"); }
            if (q == "ow" || q == "ow2") { list.Add("overwatch"); }
            if (q == "tft") { list.Add("teamfight tactics"); }
            if (q == "hs") { list.Add("hearthstone"); }
            if (q == "poe" || q == "poe2") { list.Add("path of exile"); }
            if (q == "wow") { list.Add("world of warcraft"); }
            if (q == "ffxiv" || q == "ff14") { list.Add("final fantasy xiv"); }
            if (q == "mc") { list.Add("minecraft"); }
            if (q == "rl") { list.Add("rocket league"); }
            if (q == "val") { list.Add("valorant"); }
            if (q == "wwm") { list.Add("where winds meet"); }
            if (q == "wuwa") { list.Add("wuthering waves"); }
            if (q == "hsr") { list.Add("honkai: star rail"); }
            if (q == "gi") { list.Add("genshin impact"); }
            if (q == "zzz") { list.Add("zenless zone zero"); }

            return list;
        }

        private class ScoredGame : IComparable<ScoredGame>
        {
            public GameItem Game;
            public int Score;

            public int CompareTo(ScoredGame other)
            {
                if (other == null) return -1;
                int cmp = other.Score.CompareTo(this.Score);
                if (cmp != 0) return cmp;
                return string.Compare(this.Game.name, other.Game.name, StringComparison.OrdinalIgnoreCase);
            }
        }

        public List<GameItem> Search(string query, int maxResults = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                lock (_lock)
                {
                    List<GameItem> def = new List<GameItem>();
                    for (int i = 0; i < Math.Min(maxResults, _games.Count); i++)
                    {
                        def.Add(_games[i]);
                    }
                    return def;
                }
            }

            string rawLower = query.Trim().ToLowerInvariant();
            string clean = CleanSearchQuery(rawLower);
            string normQuery = NormalizeString(clean);
            List<string> aliases = ExpandAliases(rawLower);

            List<ScoredGame> scored = new List<ScoredGame>();

            lock (_lock)
            {
                foreach (GameItem g in _games)
                {
                    int score = ScoreGame(g, rawLower, clean, normQuery, aliases);
                    if (score > 0)
                    {
                        scored.Add(new ScoredGame { Game = g, Score = score });
                    }
                }
            }

            scored.Sort();

            List<GameItem> results = new List<GameItem>();
            int limit = Math.Min(maxResults, scored.Count);
            for (int i = 0; i < limit; i++)
            {
                results.Add(scored[i].Game);
            }
            return results;
        }

        private int ScoreGame(GameItem g, string rawLower, string clean, string normQuery, List<string> aliases)
        {
            if (g == null) return 0;
            string gNameLower = (g.name ?? "").ToLowerInvariant();
            string gExeLower = (g.exe ?? "").ToLowerInvariant();
            string gNormName = g.NormalizedName;
            string gNormExe = g.NormalizedExe;

            if (gNameLower == rawLower || gNameLower == clean) return 1000;
            if (gExeLower == rawLower || gExeLower == clean || gExeLower == rawLower + ".exe") return 950;
            if (gNormName == normQuery && !string.IsNullOrEmpty(normQuery)) return 900;

            if (gNameLower.StartsWith(clean)) return 700 - (gNameLower.Length - clean.Length);
            if (gExeLower.StartsWith(clean)) return 650;
            if (gNormName.StartsWith(normQuery) && !string.IsNullOrEmpty(normQuery)) return 600;

            foreach (string alias in aliases)
            {
                if (gNameLower == alias) return 850;
                if (gNameLower.StartsWith(alias)) return 650;
                if (gNameLower.Contains(alias)) return 500;
            }

            if (gNameLower.Contains(clean)) return 400;
            if (gExeLower.Contains(clean)) return 350;
            if (!string.IsNullOrEmpty(normQuery) && gNormName.Contains(normQuery)) return 300;

            if (!string.IsNullOrEmpty(normQuery) && normQuery.Length >= 2 && g.Initials.StartsWith(normQuery))
            {
                return 250;
            }

            return 0;
        }

        public GameItem FindExactGame(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string lower = input.Trim().ToLowerInvariant();
            string clean = CleanSearchQuery(lower);
            string norm = NormalizeString(clean);

            lock (_lock)
            {
                foreach (GameItem g in _games)
                {
                    if (string.Equals(g.name, input, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(g.exe, input, StringComparison.OrdinalIgnoreCase) ||
                        (input.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(g.exe, input, StringComparison.OrdinalIgnoreCase)))
                    {
                        return g;
                    }
                }

                foreach (GameItem g in _games)
                {
                    if ((g.name ?? "").ToLowerInvariant() == clean ||
                        (g.exe ?? "").ToLowerInvariant() == clean ||
                        (g.exe ?? "").ToLowerInvariant() == clean + ".exe" ||
                        g.NormalizedName == norm)
                    {
                        return g;
                    }
                }
            }
            return null;
        }

        private List<QualifiedGameEntry> _qualifiedGamesCache = null;

        public static void SafeAtomicWriteText(string destinationPath, string text)
        {
            destinationPath = Path.GetFullPath(destinationPath);
            string dir = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(dir);
            string tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(destinationPath))
                {
                    File.Replace(tempPath, destinationPath, destinationPath + ".bak", true);
                }
                else File.Move(tempPath, destinationPath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (Exception ex) { Program.LogError("AtomicWriteCleanup", ex); }
            }
        }

        public List<QualifiedGameEntry> GetQualifiedGames()
        {
            lock (_lock)
            {
                if (_qualifiedGamesCache != null)
                {
                    return new List<QualifiedGameEntry>(_qualifiedGamesCache);
                }

                string path = QualifiedGamesFilePath;
                if (!File.Exists(path))
                {
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qualified_games.json");
                }
                if (File.Exists(path))
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            string text = File.ReadAllText(path);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                JavaScriptSerializer ser = new JavaScriptSerializer();
                                List<QualifiedGameEntry> list = ser.Deserialize<List<QualifiedGameEntry>>(text);
                                if (list != null)
                                {
                                    _qualifiedGamesCache = list;
                                    return new List<QualifiedGameEntry>(list);
                                }
                            }
                            break;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(50);
                        }
                        catch { break; }
                    }
                }
                _qualifiedGamesCache = new List<QualifiedGameEntry>();
                return new List<QualifiedGameEntry>();
            }
        }

        public bool RecordQualifiedGame(GameItem game, int sessionSeconds)
        {
            if (game == null || game.id == "__ADD_CUSTOM__") return false;
            if (sessionSeconds < 120) return true;
            try
            {
                lock (_lock)
                {
                    List<QualifiedGameEntry> list = new List<QualifiedGameEntry>();
                    foreach (QualifiedGameEntry entry in GetQualifiedGames())
                    {
                        if (entry == null) continue;
                        list.Add(new QualifiedGameEntry
                        {
                            id = entry.id, name = entry.name, icon = entry.icon, exe = entry.exe,
                            total_seconds_played = entry.total_seconds_played, last_played = entry.last_played
                        });
                    }
                    QualifiedGameEntry found = null;
                    foreach (var item in list)
                    {
                        bool sameGame = !string.IsNullOrEmpty(item.id) && !string.IsNullOrEmpty(game.id)
                            ? string.Equals(item.id, game.id, StringComparison.Ordinal)
                            : !string.IsNullOrWhiteSpace(game.name) && !string.IsNullOrWhiteSpace(game.exe) &&
                              string.Equals(item.name, game.name, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(item.exe, game.exe, StringComparison.OrdinalIgnoreCase);
                        if (sameGame)
                        {
                            found = item;
                            break;
                        }
                    }

                    if (found == null)
                    {
                        found = new QualifiedGameEntry
                        {
                            id = game.id ?? "",
                            name = game.name ?? "",
                            icon = game.icon ?? "",
                            exe = game.exe ?? "",
                            total_seconds_played = sessionSeconds,
                            last_played = DateTime.UtcNow.ToString("o")
                        };
                        list.Insert(0, found);
                    }
                    else
                    {
                        found.total_seconds_played += sessionSeconds;
                        found.last_played = DateTime.UtcNow.ToString("o");
                        found.name = game.name ?? found.name;
                        found.exe = game.exe ?? found.exe;
                        if (!string.IsNullOrEmpty(game.icon)) found.icon = game.icon;
                        if (!string.IsNullOrEmpty(game.id)) found.id = game.id;
                        list.Remove(found);
                        list.Insert(0, found);
                    }

                    if (list.Count > 12) list = list.GetRange(0, 12);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    string json = ser.Serialize(list);
                    SafeAtomicWriteText(QualifiedGamesFilePath, json);
                    _qualifiedGamesCache = new List<QualifiedGameEntry>(list);

                    try
                    {
                        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qualified_games.json");
                        SafeAtomicWriteText(localPath, json);
                    }
                    catch (Exception ex) { Program.LogError("QualifiedGamesMirror", ex); }
                }

                if (OnQualifiedGamesChanged != null)
                {
                    try { OnQualifiedGamesChanged(); } catch { }
                }

                SaveLastSimulatedGame(game);
                return true;
            }
            catch (Exception ex)
            {
                Program.LogError("RecordQualifiedGame", ex);
                return false;
            }
        }

        public GameItem GetLastSimulatedGame()
        {
            try
            {
                string path = LastSimulatedFilePath;
                if (!File.Exists(path))
                {
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_simulated.json");
                }
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        GameItem item = ser.Deserialize<GameItem>(json);
                        if (item != null && item.id != "__ADD_CUSTOM__" && (!string.IsNullOrEmpty(item.name) || !string.IsNullOrEmpty(item.exe)))
                        {
                            return item;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var qualified = GetQualifiedGames();
                if (qualified != null && qualified.Count > 0 && qualified[0] != null)
                {
                    var q = qualified[0];
                    return new GameItem
                    {
                        id = q.id,
                        name = q.name,
                        icon = q.icon,
                        exe = q.exe
                    };
                }
            }
            catch { }

            return null;
        }

        public bool SaveLastSimulatedGame(GameItem game)
        {
            if (game == null || game.id == "__ADD_CUSTOM__") return false;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(game);
                SafeAtomicWriteText(LastSimulatedFilePath, json);
                try
                {
                    string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_simulated.json");
                    SafeAtomicWriteText(localPath, json);
                }
                catch (Exception ex) { Program.LogError("LastSimulatedMirror", ex); }
                return true;
            }
            catch (Exception ex)
            {
                Program.LogError("SaveLastSimulatedGame", ex);
                return false;
            }
        }
    }

    #endregion

    #region Quest Database

    /// <summary>
    /// Manages the Discord Quests catalog, regional eligibility metadata, detected country, and local checklist state.
    /// </summary>
    public class QuestDatabase
    {
        public const string QuestFeedUrl = "https://raw.githubusercontent.com/xGustavvo/discord-api-tracker/refs/heads/main/quest.json";
        public const string RegionFeedUrl = "https://gist.githubusercontent.com/xGustavvo/3d08b7369eb34b50834815fd43176cae/raw";
        public const string GeoTraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

        private static QuestDatabase _instance;
        public static QuestDatabase Instance
        {
            get
            {
                if (_instance == null) _instance = new QuestDatabase();
                return _instance;
            }
        }

        private readonly object _lock = new object();
        private List<QuestItem> _quests = new List<QuestItem>();
        private HashSet<string> _completedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _progressSeconds = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _simulatingQuestIds = new HashSet<string>(StringComparer.Ordinal);
        private string _detectedCountry = "";
        private string _regionOverride = "";
        private bool _filterRegionOnly = false;
        private bool _hideCompleted = false;
        private DateTime _lastUpdatedUtc = DateTime.MinValue;
        private bool _initStarted = false;
        private bool _isUpdating = false;
        private bool _stateLoaded = false;

        public event Action OnQuestsUpdated;
        public event Action OnStateChanged;
        public event Action OnProgressUpdated;

        public string CacheFilePath
        {
            get { return Path.Combine(GameDatabase.Instance.CacheDirectory, "quests_cache.json"); }
        }

        public string StateFilePath
        {
            get { return Path.Combine(GameDatabase.Instance.CacheDirectory, "quest_state.json"); }
        }

        public bool IsUpdating
        {
            get { lock (_lock) return _isUpdating; }
        }

        public DateTime LastUpdatedUtc
        {
            get { lock (_lock) return _lastUpdatedUtc; }
        }

        public string DetectedCountry
        {
            get { lock (_lock) return _detectedCountry; }
        }

        public string RegionOverride
        {
            get { lock (_lock) return _regionOverride; }
        }

        public string EffectiveCountry
        {
            get
            {
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(_regionOverride)) return _regionOverride;
                    return _detectedCountry;
                }
            }
        }

        public bool FilterRegionOnly
        {
            get { lock (_lock) return _filterRegionOnly; }
        }

        public bool HideCompleted
        {
            get { lock (_lock) return _hideCompleted; }
        }

        public int Count
        {
            get { lock (_lock) return _quests.Count; }
        }

        public List<QuestItem> GetQuestsSnapshot()
        {
            lock (_lock) return new List<QuestItem>(_quests);
        }

        public int GetActiveCount(DateTimeOffset nowUtc)
        {
            int active = 0;
            lock (_lock)
            {
                for (int i = 0; i < _quests.Count; i++)
                {
                    if (_quests[i] != null && _quests[i].IsActive(nowUtc)) active++;
                }
            }
            return active;
        }

        public void Initialize()
        {
            lock (_lock)
            {
                if (_initStarted) return;
                _initStarted = true;
            }
            ThreadPool.QueueUserWorkItem(_ => LoadInitialData());
        }

        private void LoadInitialData()
        {
            EnsureStateLoaded();
            bool needRefresh = true;
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    string json = File.ReadAllText(CacheFilePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        ser.MaxJsonLength = int.MaxValue;
                        QuestCacheEnvelope env = ser.Deserialize<QuestCacheEnvelope>(json);
                        if (env != null && !string.IsNullOrWhiteSpace(env.quests_json))
                        {
                            List<QuestItem> parsed = ParseQuestCatalogJson(env.quests_json, env.regions_json);
                            if (parsed.Count > 0)
                            {
                                DateTime cachedAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(env.cached_at);
                                lock (_lock)
                                {
                                    _quests = parsed;
                                    _lastUpdatedUtc = cachedAt;
                                }
                                needRefresh = (DateTime.UtcNow - cachedAt) > TimeSpan.FromMinutes(15);
                                NotifyQuestsUpdated();
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Program.LogError("QuestCacheLoad", ex); }

            if (needRefresh) RefreshAsync(false);
        }

        public void RefreshAsync(bool force)
        {
            lock (_lock)
            {
                if (_isUpdating) return;
                if (!force && _lastUpdatedUtc != DateTime.MinValue && (DateTime.UtcNow - _lastUpdatedUtc) < TimeSpan.FromMinutes(2))
                    return;
                _isUpdating = true;
            }
            NotifyQuestsUpdated();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureStateLoaded();
                    string questJson = null;
                    string regionJson = null;
                    string traceText = null;

                    using (ManualResetEvent done = new ManualResetEvent(false))
                    {
                        int pending = 3;
                        ThreadPool.QueueUserWorkItem(__ =>
                        {
                            try
                            {
                                byte[] data = IconCache.DownloadBytes(QuestFeedUrl, 10);
                                if (data != null) questJson = Encoding.UTF8.GetString(data);
                            }
                            catch { }
                            finally { if (Interlocked.Decrement(ref pending) == 0) done.Set(); }
                        });
                        ThreadPool.QueueUserWorkItem(__ =>
                        {
                            try
                            {
                                byte[] data = IconCache.DownloadBytes(RegionFeedUrl, 10);
                                if (data != null) regionJson = Encoding.UTF8.GetString(data);
                            }
                            catch { }
                            finally { if (Interlocked.Decrement(ref pending) == 0) done.Set(); }
                        });
                        ThreadPool.QueueUserWorkItem(__ =>
                        {
                            try
                            {
                                byte[] data = IconCache.DownloadBytes(GeoTraceUrl, 4);
                                if (data != null) traceText = Encoding.UTF8.GetString(data);
                            }
                            catch { }
                            finally { if (Interlocked.Decrement(ref pending) == 0) done.Set(); }
                        });
                        done.WaitOne(18000);
                    }

                    string detected = ParseCloudflareTraceCountry(traceText);
                    if (!string.IsNullOrEmpty(detected))
                    {
                        lock (_lock) _detectedCountry = detected;
                    }

                    if (!string.IsNullOrWhiteSpace(questJson))
                    {
                        List<QuestItem> parsed = ParseQuestCatalogJson(questJson, regionJson);
                        if (parsed.Count > 0)
                        {
                            DateTime nowUtc = DateTime.UtcNow;
                            lock (_lock)
                            {
                                _quests = parsed;
                                _lastUpdatedUtc = nowUtc;
                            }
                            try
                            {
                                QuestCacheEnvelope env = new QuestCacheEnvelope
                                {
                                    cached_at = (long)(nowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                                    quests_json = questJson,
                                    regions_json = regionJson ?? ""
                                };
                                JavaScriptSerializer ser = new JavaScriptSerializer();
                                ser.MaxJsonLength = int.MaxValue;
                                GameDatabase.SafeAtomicWriteText(CacheFilePath, ser.Serialize(env));
                            }
                            catch (Exception ex) { Program.LogError("QuestCacheSave", ex); }
                        }
                    }
                }
                catch (Exception ex) { Program.LogError("QuestRefresh", ex); }
                finally
                {
                    lock (_lock) _isUpdating = false;
                    NotifyQuestsUpdated();
                }
            });
        }

        public void SetQuestsForTesting(List<QuestItem> quests, string detectedCountry)
        {
            lock (_lock)
            {
                _quests = quests != null ? new List<QuestItem>(quests) : new List<QuestItem>();
                if (detectedCountry != null) _detectedCountry = NormalizeCountryCode(detectedCountry);
                _lastUpdatedUtc = DateTime.UtcNow;
            }
            NotifyQuestsUpdated();
        }

        public void EnsureStateLoaded()
        {
            lock (_lock)
            {
                if (_stateLoaded) return;
                _stateLoaded = true;
            }
            LoadUserState();
        }

        public void LoadUserState()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return;
                string json = File.ReadAllText(StateFilePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                QuestUserState state = ser.Deserialize<QuestUserState>(json);
                if (state == null) return;
                lock (_lock)
                {
                    _completedIds.Clear();
                    if (state.completed_ids != null)
                    {
                        for (int i = 0; i < state.completed_ids.Count; i++)
                        {
                            string id = state.completed_ids[i];
                            if (!string.IsNullOrWhiteSpace(id)) _completedIds.Add(id.Trim());
                        }
                    }
                    _progressSeconds.Clear();
                    if (state.progress_seconds != null)
                    {
                        foreach (KeyValuePair<string, int> kv in state.progress_seconds)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
                                _progressSeconds[kv.Key.Trim()] = kv.Value;
                        }
                    }
                    _regionOverride = NormalizeCountryCode(state.region_override);
                    _filterRegionOnly = state.filter_region_only;
                    _hideCompleted = state.hide_completed;
                    _stateLoaded = true;
                }
            }
            catch (Exception ex) { Program.LogError("LoadQuestState", ex); }
        }

        public bool SaveUserState()
        {
            try
            {
                QuestUserState state;
                lock (_lock)
                {
                    state = new QuestUserState
                    {
                        completed_ids = new List<string>(_completedIds),
                        progress_seconds = new Dictionary<string, int>(_progressSeconds, StringComparer.Ordinal),
                        region_override = _regionOverride ?? "",
                        filter_region_only = _filterRegionOnly,
                        hide_completed = _hideCompleted
                    };
                }
                JavaScriptSerializer ser = new JavaScriptSerializer();
                GameDatabase.SafeAtomicWriteText(StateFilePath, ser.Serialize(state));
                return true;
            }
            catch (Exception ex)
            {
                Program.LogError("SaveQuestState", ex);
                return false;
            }
        }

        public bool IsCompleted(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return false;
            EnsureStateLoaded();
            lock (_lock) return _completedIds.Contains(questId);
        }

        public HashSet<string> GetCompletedIdsSnapshot()
        {
            EnsureStateLoaded();
            lock (_lock) return new HashSet<string>(_completedIds, StringComparer.Ordinal);
        }

        public int GetQuestProgressSeconds(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return 0;
            EnsureStateLoaded();
            lock (_lock)
            {
                int val;
                return _progressSeconds.TryGetValue(questId, out val) ? val : 0;
            }
        }

        public bool IsQuestSimulating(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return false;
            lock (_lock) return _simulatingQuestIds.Contains(questId);
        }

        public Dictionary<string, int> GetProgressSnapshot()
        {
            EnsureStateLoaded();
            lock (_lock) return new Dictionary<string, int>(_progressSeconds, StringComparer.Ordinal);
        }

        public HashSet<string> GetSimulatingQuestIdsSnapshot()
        {
            lock (_lock) return new HashSet<string>(_simulatingQuestIds, StringComparer.Ordinal);
        }

        public bool ToggleCompleted(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return false;
            EnsureStateLoaded();
            bool nowDone;
            lock (_lock)
            {
                if (_completedIds.Contains(questId))
                {
                    _completedIds.Remove(questId);
                    _progressSeconds.Remove(questId);
                    nowDone = false;
                }
                else
                {
                    _completedIds.Add(questId);
                    nowDone = true;
                }
            }
            SaveUserState();
            NotifyStateChanged();
            return nowDone;
        }

        public static bool DoesQuestMatchGame(QuestItem quest, GameItem game)
        {
            if (quest == null || game == null || !quest.HasDesktopTask) return false;
            string qAppId = quest.DesktopApplicationId;
            if (!string.IsNullOrEmpty(qAppId) && !string.IsNullOrEmpty(game.id) &&
                string.Equals(qAppId, game.id, StringComparison.Ordinal))
            {
                return true;
            }
            string gameNorm = GameDatabase.NormalizeString(game.name);
            if (string.IsNullOrEmpty(gameNorm)) return false;
            string qAppNorm = GameDatabase.NormalizeString(quest.application_name);
            string qTitleNorm = GameDatabase.NormalizeString(quest.game_title);
            return (!string.IsNullOrEmpty(qAppNorm) && qAppNorm == gameNorm) ||
                   (!string.IsNullOrEmpty(qTitleNorm) && qTitleNorm == gameNorm);
        }

        public int GetActiveDesktopQuestTargetSeconds(GameItem game, DateTimeOffset nowUtc)
        {
            if (game == null) return 0;
            EnsureStateLoaded();
            int maxTarget = 0;
            lock (_lock)
            {
                for (int i = 0; i < _quests.Count; i++)
                {
                    QuestItem q = _quests[i];
                    if (q == null || string.IsNullOrEmpty(q.id)) continue;
                    if (!q.IsActive(nowUtc) || _completedIds.Contains(q.id)) continue;
                    if (!DoesQuestMatchGame(q, game)) continue;
                    int target = q.DesktopTaskTargetSeconds;
                    if (target > maxTarget) maxTarget = target;
                }
            }
            return maxTarget;
        }

        public List<string> UpdateActiveSimulationProgress(IList<GameItem> activeGames, int deltaSeconds, DateTimeOffset nowUtc)
        {
            EnsureStateLoaded();
            List<string> newlyCompleted = new List<string>();
            lock (_lock)
            {
                _simulatingQuestIds.Clear();
                if (activeGames != null && activeGames.Count > 0)
                {
                    for (int i = 0; i < _quests.Count; i++)
                    {
                        QuestItem q = _quests[i];
                        if (q == null || string.IsNullOrEmpty(q.id) || !q.IsActive(nowUtc) || !q.HasDesktopTask) continue;

                        for (int g = 0; g < activeGames.Count; g++)
                        {
                            if (DoesQuestMatchGame(q, activeGames[g]))
                            {
                                _simulatingQuestIds.Add(q.id);
                                break;
                            }
                        }
                    }
                }
            }
            NotifyProgressUpdated();
            return newlyCompleted;
        }

        public void ClearActiveSimulation()
        {
            lock (_lock)
            {
                _simulatingQuestIds.Clear();
            }
            NotifyProgressUpdated();
        }

        public void SetFilterPreferences(bool filterRegionOnly, bool hideCompleted, string regionOverride)
        {
            EnsureStateLoaded();
            lock (_lock)
            {
                _filterRegionOnly = filterRegionOnly;
                _hideCompleted = hideCompleted;
                if (regionOverride != null) _regionOverride = NormalizeCountryCode(regionOverride);
            }
            SaveUserState();
            NotifyStateChanged();
        }

        public GameItem ResolveGameForQuest(QuestItem quest)
        {
            if (quest == null) return null;
            string appId = quest.DesktopApplicationId;
            if (!string.IsNullOrEmpty(appId))
            {
                GameItem byId = GameDatabase.Instance.FindById(appId);
                if (byId != null) return byId;
            }

            if (!string.IsNullOrWhiteSpace(quest.game_title))
            {
                GameItem byTitle = GameDatabase.Instance.FindExactGame(quest.game_title);
                if (byTitle != null) return byTitle;
            }

            if (!string.IsNullOrWhiteSpace(quest.application_name))
            {
                GameItem byAppName = GameDatabase.Instance.FindExactGame(quest.application_name);
                if (byAppName != null) return byAppName;
            }

            string fallbackName = !string.IsNullOrWhiteSpace(quest.application_name)
                ? quest.application_name
                : quest.DisplayGameName;
            return new GameItem
            {
                id = appId ?? "",
                name = fallbackName,
                exe = GameDatabase.InferExecutableFromName(fallbackName),
                icon = ""
            };
        }

        private void NotifyQuestsUpdated()
        {
            Action handler = OnQuestsUpdated;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Program.LogError("QuestsUpdated", ex); }
            }
        }

        private void NotifyStateChanged()
        {
            Action handler = OnStateChanged;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Program.LogError("QuestStateChanged", ex); }
            }
        }

        private void NotifyProgressUpdated()
        {
            Action handler = OnProgressUpdated;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Program.LogError("QuestProgressUpdated", ex); }
            }
        }

        public static string NormalizeCountryCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            string c = code.Trim().ToUpperInvariant();
            if (c == "GB") return "UK";
            return c;
        }

        public static string ParseCloudflareTraceCountry(string traceText)
        {
            if (string.IsNullOrWhiteSpace(traceText)) return "";
            string[] lines = traceText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeCountryCode(line.Substring(4));
                }
            }
            return "";
        }

        public static List<QuestItem> ParseQuestCatalogJson(string questJson, string regionJson)
        {
            List<QuestItem> result = new List<QuestItem>();
            if (string.IsNullOrWhiteSpace(questJson)) return result;

            Dictionary<string, QuestRegionInfo> regionMap = ParseRegionMapJson(regionJson);

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            object rootObj = ser.DeserializeObject(questJson);
            System.Collections.IEnumerable rawQuests = rootObj as System.Collections.IEnumerable;
            if (rawQuests == null || rootObj is string || rootObj is Dictionary<string, object>) return result;

            foreach (object obj in rawQuests)
            {
                Dictionary<string, object> dict = obj as Dictionary<string, object>;
                if (dict == null) continue;

                string id = IconCache.DictString(dict, "id");
                if (string.IsNullOrEmpty(id)) continue;

                Dictionary<string, object> app = IconCache.DictObject(dict, "application");
                Dictionary<string, object> assets = IconCache.DictObject(dict, "assets");
                Dictionary<string, object> messages = IconCache.DictObject(dict, "messages");
                Dictionary<string, object> taskConfig = IconCache.DictObject(dict, "task_config_v2")
                    ?? IconCache.DictObject(dict, "task_config");
                Dictionary<string, object> rewardsConfig = IconCache.DictObject(dict, "rewards_config");

                QuestItem quest = new QuestItem
                {
                    id = id,
                    quest_name = IconCache.DictString(messages, "quest_name") ?? "",
                    game_title = IconCache.DictString(messages, "game_title") ?? "",
                    game_publisher = IconCache.DictString(messages, "game_publisher") ?? "",
                    application_id = IconCache.DictString(app, "id") ?? "",
                    application_name = IconCache.DictString(app, "name") ?? "",
                    application_link = IconCache.DictString(app, "link") ?? "",
                    starts_at = IconCache.DictString(dict, "starts_at") ?? "",
                    expires_at = IconCache.DictString(dict, "expires_at") ?? "",
                    hero_asset = IconCache.DictString(assets, "hero") ?? IconCache.DictString(assets, "quest_bar_hero") ?? "",
                    tile_asset = IconCache.DictString(assets, "game_tile_dark") ?? IconCache.DictString(assets, "game_tile_light") ?? IconCache.DictString(assets, "game_tile") ?? "",
                    tasks = ParseTasks(taskConfig, IconCache.DictString(app, "id")),
                    rewards = ParseRewards(rewardsConfig),
                    region = regionMap.ContainsKey(id) ? regionMap[id] : null
                };
                result.Add(quest);
            }
            return result;
        }

        private static Dictionary<string, QuestRegionInfo> ParseRegionMapJson(string regionJson)
        {
            Dictionary<string, QuestRegionInfo> map = new Dictionary<string, QuestRegionInfo>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(regionJson)) return map;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(regionJson);
                object[] arr = IconCache.DictArray(root, "quests");
                if (arr == null) return map;

                for (int i = 0; i < arr.Length; i++)
                {
                    Dictionary<string, object> entry = arr[i] as Dictionary<string, object>;
                    if (entry == null) continue;
                    string id = IconCache.DictString(entry, "id");
                    if (string.IsNullOrEmpty(id)) continue;

                    QuestRegionInfo info = new QuestRegionInfo
                    {
                        show_age_gate = DictBool(entry, "show_age_gate"),
                        is_global = DictBool(entry, "is_global"),
                        include = new List<string>(),
                        exclude = new List<string>()
                    };

                    if (entry.ContainsKey("regions") && entry["regions"] != null)
                    {
                        Dictionary<string, object> regionObj = entry["regions"] as Dictionary<string, object>;
                        if (regionObj != null)
                        {
                            info.include = ExtractCountryList(regionObj.ContainsKey("include") ? regionObj["include"] : null);
                            info.exclude = ExtractCountryList(regionObj.ContainsKey("exclude") ? regionObj["exclude"] : null);
                        }
                        else
                        {
                            info.include = ExtractCountryList(entry["regions"]);
                        }
                    }
                    map[id] = info;
                }
            }
            catch (Exception ex) { Program.LogError("ParseRegionMap", ex); }
            return map;
        }

        private static List<string> ExtractCountryList(object raw)
        {
            List<string> list = new List<string>();
            System.Collections.IEnumerable seq = raw as System.Collections.IEnumerable;
            if (seq == null || raw is string) return list;
            foreach (object item in seq)
            {
                if (item == null) continue;
                string norm = NormalizeCountryCode(item.ToString());
                if (!string.IsNullOrEmpty(norm) && !list.Contains(norm)) list.Add(norm);
            }
            return list;
        }

        private static List<QuestTaskInfo> ParseTasks(Dictionary<string, object> taskConfig, string fallbackAppId)
        {
            List<QuestTaskInfo> list = new List<QuestTaskInfo>();
            Dictionary<string, object> tasksDict = IconCache.DictObject(taskConfig, "tasks");
            if (tasksDict == null) return list;

            foreach (KeyValuePair<string, object> kv in tasksDict)
            {
                Dictionary<string, object> tObj = kv.Value as Dictionary<string, object>;
                if (tObj == null) continue;
                string type = IconCache.DictString(tObj, "type") ?? kv.Key;
                int target = DictInt(tObj, "target");
                string appId = fallbackAppId ?? "";
                object[] apps = IconCache.DictArray(tObj, "applications");
                if (apps != null && apps.Length > 0)
                {
                    Dictionary<string, object> firstApp = apps[0] as Dictionary<string, object>;
                    string id = IconCache.DictString(firstApp, "id");
                    if (!string.IsNullOrEmpty(id)) appId = id;
                }
                list.Add(new QuestTaskInfo
                {
                    type = type,
                    target = target,
                    application_id = appId
                });
            }
            return list;
        }

        private static List<QuestRewardInfo> ParseRewards(Dictionary<string, object> rewardsConfig)
        {
            List<QuestRewardInfo> list = new List<QuestRewardInfo>();
            object[] rewardsArr = IconCache.DictArray(rewardsConfig, "rewards");
            if (rewardsArr == null) return list;

            for (int i = 0; i < rewardsArr.Length; i++)
            {
                Dictionary<string, object> rObj = rewardsArr[i] as Dictionary<string, object>;
                if (rObj == null) continue;
                Dictionary<string, object> msg = IconCache.DictObject(rObj, "messages");
                Dictionary<string, object> instrMap = IconCache.DictObject(msg, "redemption_instructions_by_platform");
                string instr = IconCache.DictString(instrMap, "0") ?? "";
                if (string.IsNullOrEmpty(instr) && instrMap != null)
                {
                    foreach (KeyValuePair<string, object> kv in instrMap)
                    {
                        if (kv.Value != null)
                        {
                            string candidate = kv.Value.ToString().Trim();
                            if (!string.IsNullOrEmpty(candidate) && !string.Equals(candidate, "PLACEHOLDER", StringComparison.OrdinalIgnoreCase))
                            {
                                instr = candidate;
                                break;
                            }
                        }
                    }
                }
                if (string.Equals(instr, "PLACEHOLDER", StringComparison.OrdinalIgnoreCase)) instr = "";
                if (instr.Length >= 2 && instr[0] == '"' && instr[instr.Length - 1] == '"')
                    instr = instr.Substring(1, instr.Length - 2).Trim();

                list.Add(new QuestRewardInfo
                {
                    type = DictInt(rObj, "type"),
                    name = IconCache.DictString(msg, "name") ?? "",
                    orb_quantity = DictInt(rObj, "orb_quantity"),
                    asset = IconCache.DictString(rObj, "asset") ?? "",
                    redemption_instructions = instr
                });
            }
            return list;
        }

        private static bool DictBool(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return false;
            if (dict[key] is bool) return (bool)dict[key];
            bool parsed;
            return bool.TryParse(dict[key].ToString(), out parsed) && parsed;
        }

        private static int DictInt(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return 0;
            object v = dict[key];
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)(double)v;
            if (v is decimal) return (int)(decimal)v;
            int parsed;
            return int.TryParse(v.ToString(), out parsed) ? parsed : 0;
        }

        public static List<QuestItem> FilterQuests(
            IList<QuestItem> source,
            string category,
            string query,
            bool regionOnly,
            string countryCode,
            bool hideCompleted,
            HashSet<string> completedIds,
            string sortMode,
            DateTimeOffset nowUtc)
        {
            HashSet<string> unused;
            return FilterQuests(source, category, query, regionOnly, countryCode, hideCompleted, completedIds, sortMode, nowUtc, out unused);
        }

        public static List<QuestItem> FilterQuests(
            IList<QuestItem> source,
            string category,
            string query,
            bool regionOnly,
            string countryCode,
            bool hideCompleted,
            HashSet<string> completedIds,
            string sortMode,
            DateTimeOffset nowUtc,
            out HashSet<string> alsoGlobalIds)
        {
            alsoGlobalIds = new HashSet<string>(StringComparer.Ordinal);
            List<QuestItem> filtered = new List<QuestItem>();
            if (source == null) return filtered;

            string cat = string.IsNullOrEmpty(category) ? "active" : category.ToLowerInvariant();
            string q = string.IsNullOrWhiteSpace(query) ? "" : query.Trim().ToLowerInvariant();
            string cc = NormalizeCountryCode(countryCode);

            for (int i = 0; i < source.Count; i++)
            {
                QuestItem item = source[i];
                if (item == null) continue;

                bool isDone = completedIds != null && !string.IsNullOrEmpty(item.id) && completedIds.Contains(item.id);
                if (hideCompleted && cat != "done" && isDone) continue;
                if (regionOnly && !string.IsNullOrEmpty(cc) && !item.IsEligibleInRegion(cc)) continue;

                bool matchesCategory;
                if (cat == "active") matchesCategory = item.IsActive(nowUtc);
                else if (cat == "desktop") matchesCategory = item.IsActive(nowUtc) && item.HasDesktopTask;
                else if (cat == "ending") matchesCategory = item.IsEndingSoon(nowUtc);
                else if (cat == "orbs") matchesCategory = item.IsActive(nowUtc) && item.HasOrbsReward;
                else if (cat == "collectibles") matchesCategory = item.IsActive(nowUtc) && item.HasCollectibleReward;
                else if (cat == "expired") matchesCategory = item.IsExpired(nowUtc);
                else if (cat == "done") matchesCategory = isDone;
                else matchesCategory = true;

                if (!matchesCategory) continue;

                if (q.Length > 0 && !MatchesQuery(item, q)) continue;
                filtered.Add(item);
            }

            string sort = string.IsNullOrEmpty(sortMode) ? "ending_soon" : sortMode.ToLowerInvariant();
            filtered.Sort((a, b) => CompareQuests(a, b, sort));

            if (regionOnly && !string.IsNullOrEmpty(cc) && filtered.Count > 1)
            {
                filtered = DeduplicateRegionalVariants(filtered, cc, alsoGlobalIds);
            }
            return filtered;
        }

        private static bool IsCountrySpecificMatch(QuestItem item, string normalizedCountry)
        {
            return item != null && item.region != null && !item.region.is_global &&
                   item.region.include != null && item.region.include.Contains(normalizedCountry);
        }

        private static bool IsGlobalOrUntagged(QuestItem item)
        {
            return item != null && (item.region == null || item.region.is_global);
        }

        private static List<QuestItem> DeduplicateRegionalVariants(List<QuestItem> sorted, string cc, HashSet<string> alsoGlobalIds)
        {
            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Count; i++)
            {
                QuestItem item = sorted[i];
                string key = GameDatabase.NormalizeString(item.DisplayTitle) + "|" + (item.application_id ?? "") + "|" + (item.expires_at ?? "");
                List<int> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }
                list.Add(i);
            }

            HashSet<int> keepIndices = new HashSet<int>();
            foreach (KeyValuePair<string, List<int>> kv in groups)
            {
                List<int> idxs = kv.Value;
                if (idxs.Count == 1)
                {
                    keepIndices.Add(idxs[0]);
                    continue;
                }
                int winnerIdx = -1;
                bool hasGlobal = false;
                for (int i = 0; i < idxs.Count; i++)
                {
                    QuestItem cand = sorted[idxs[i]];
                    if (winnerIdx < 0 && IsCountrySpecificMatch(cand, cc)) winnerIdx = idxs[i];
                    if (IsGlobalOrUntagged(cand)) hasGlobal = true;
                }
                bool pickedCountry = winnerIdx >= 0;
                if (!pickedCountry) winnerIdx = idxs[0];
                keepIndices.Add(winnerIdx);
                if (pickedCountry && hasGlobal && !string.IsNullOrEmpty(sorted[winnerIdx].id))
                {
                    alsoGlobalIds.Add(sorted[winnerIdx].id);
                }
            }

            List<QuestItem> deduped = new List<QuestItem>(keepIndices.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (keepIndices.Contains(i)) deduped.Add(sorted[i]);
            }
            return deduped;
        }

        private static bool MatchesQuery(QuestItem item, string lowerQuery)
        {
            if ((item.quest_name ?? "").ToLowerInvariant().Contains(lowerQuery)) return true;
            if ((item.game_title ?? "").ToLowerInvariant().Contains(lowerQuery)) return true;
            if ((item.application_name ?? "").ToLowerInvariant().Contains(lowerQuery)) return true;
            if ((item.game_publisher ?? "").ToLowerInvariant().Contains(lowerQuery)) return true;
            if (item.rewards != null)
            {
                for (int i = 0; i < item.rewards.Count; i++)
                {
                    QuestRewardInfo r = item.rewards[i];
                    if (r == null) continue;
                    if ((r.name ?? "").ToLowerInvariant().Contains(lowerQuery)) return true;
                    if (r.GetTypeLabel().ToLowerInvariant().Contains(lowerQuery)) return true;
                }
            }
            if (item.tasks != null)
            {
                for (int i = 0; i < item.tasks.Count; i++)
                {
                    QuestTaskInfo t = item.tasks[i];
                    if (t == null) continue;
                    if (t.GetDisplayLabel().ToLowerInvariant().Contains(lowerQuery)) return true;
                }
            }
            return false;
        }

        private static int CompareQuests(QuestItem a, QuestItem b, string sortMode)
        {
            if (sortMode == "name")
            {
                return string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase);
            }
            if (sortMode == "newest")
            {
                int cmp = b.StartsAtUtc.CompareTo(a.StartsAtUtc);
                if (cmp != 0) return cmp;
                return string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase);
            }
            // Default: ending_soon (earliest expiration first, unknown last)
            DateTimeOffset ea = a.ExpiresAtUtc == DateTimeOffset.MinValue ? DateTimeOffset.MaxValue : a.ExpiresAtUtc;
            DateTimeOffset eb = b.ExpiresAtUtc == DateTimeOffset.MinValue ? DateTimeOffset.MaxValue : b.ExpiresAtUtc;
            int expCmp = ea.CompareTo(eb);
            if (expCmp != 0) return expCmp;
            return string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Icon Cache

    public class IconCache
    {
        // Steam community icons first, official Discord icons as fallback.
        private static IconCache _instance;
        public static IconCache Instance
        {
            get
            {
                if (_instance == null) _instance = new IconCache();
                return _instance;
            }
        }

        private readonly Dictionary<string, Image> _memoryCache = new Dictionary<string, Image>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly object _lock = new object();

        public string IconDirectory
        {
            get
            {
                string dir = Path.Combine(GameDatabase.Instance.CacheDirectory, "icons_steam_v10");
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        private static string IconFileName(GameItem game)
        {
            return string.Format("{0}_{1}.png", game.id,
                string.IsNullOrEmpty(game.icon) ? "icon" : game.icon);
        }

        private string FindCachedIcon(GameItem game)
        {
            if (game == null || string.IsNullOrEmpty(game.id)) return null;

            // Only this cache is populated by the Steam-first icon downloader.
            // Legacy caches and local overrides may contain unrelated cover artwork.
            string path = Path.Combine(IconDirectory, IconFileName(game));
            return File.Exists(path) ? path : null;
        }

        private readonly Dictionary<string, List<Action<Image>>> _pendingCallbacks = new Dictionary<string, List<Action<Image>>>();
        private readonly Dictionary<string, DateTime> _retryAfter = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, Image> _fallbackIcons = new Dictionary<string, Image>();

        // Images are shared, immutable cache entries. Controls must not dispose them.
        public Image GetIcon(GameItem game, int size, int radius = 8, Action<Image> onLoaded = null)
        {
            if (game == null || string.IsNullOrEmpty(game.id)) return GetFallbackGamepadIcon(size, radius);
            string iconId = !string.IsNullOrEmpty(game.icon) ? game.icon : game.id;
            string key = string.Format("{0}_{1}_{2}_{3}", game.id, iconId, size, radius);
            lock (_lock)
            {
                Image cached;
                if (_memoryCache.TryGetValue(key, out cached)) return cached;
                DateTime retry;
                if (_retryAfter.TryGetValue(key, out retry) && DateTime.UtcNow < retry)
                    return GetFallbackGamepadIcon(size, radius);
                List<Action<Image>> callbacks;
                if (!_pendingCallbacks.TryGetValue(key, out callbacks))
                {
                    callbacks = new List<Action<Image>>();
                    _pendingCallbacks[key] = callbacks;
                }
                if (onLoaded != null && !callbacks.Contains(onLoaded)) callbacks.Add(onLoaded);
                if (_inFlight.Add(key))
                    ThreadPool.QueueUserWorkItem(_ => LoadIcon(game, size, radius, key));
            }
            return GetFallbackGamepadIcon(size, radius);
        }

        private void PublishIcon(string key, Image image, bool completed)
        {
            List<Action<Image>> callbacks;
            lock (_lock)
            {
                _memoryCache[key] = image;
                callbacks = _pendingCallbacks.ContainsKey(key)
                    ? new List<Action<Image>>(_pendingCallbacks[key]) : new List<Action<Image>>();
                if (completed) _pendingCallbacks.Remove(key);
            }
            foreach (Action<Image> callback in callbacks)
            {
                try { callback(image); }
                catch (Exception ex) { Program.LogError("IconLoaded", ex); }
            }
        }

        private void LoadIcon(GameItem game, int size, int radius, string key)
        {
            try
            {
                string diskPath = FindCachedIcon(game);
                bool fresh = diskPath != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(diskPath) < TimeSpan.FromDays(1);
                if (diskPath == null)
                {
                    string officialPath = Path.Combine(GameDatabase.Instance.CacheDirectory, "icons_official_v9", IconFileName(game));
                    if (File.Exists(officialPath)) diskPath = officialPath;
                }
                if (diskPath != null)
                {
                    try
                    {
                        using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(diskPath)))
                        using (Image source = Image.FromStream(stream))
                            PublishIcon(key, CreateRoundedThumbnail(source, size, radius), fresh);
                        if (fresh) return;
                    }
                    catch { }
                }
                byte[] data = FetchHighQualityIconData(game);
                if (data != null && data.Length > 0)
                {
                    using (MemoryStream stream = new MemoryStream(data))
                    using (Image source = Image.FromStream(stream))
                        PublishIcon(key, CreateRoundedThumbnail(source, size, radius), true);
                    try { File.WriteAllBytes(Path.Combine(IconDirectory, IconFileName(game)), data); } catch { }
                }
            }
            catch { }
            finally
            {
                lock (_lock)
                {
                    _pendingCallbacks.Remove(key);
                    _inFlight.Remove(key);
                    // Failed downloads must not restart on every repaint.
                    if (!_memoryCache.ContainsKey(key)) _retryAfter[key] = DateTime.UtcNow.AddMinutes(5);
                }
            }
        }

        private const string IconUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

        public static byte[] DownloadBytes(string url, int minBytes)
        {
            try
            {
                // The launcher targets classic .NET Framework. Explicitly enable TLS 1.2
                // so icon downloads do not depend on the machine's legacy defaults.
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = IconUserAgent;
                req.Accept = "*/*";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode < 200 || (int)resp.StatusCode >= 300) return null;
                    using (Stream input = resp.GetResponseStream())
                    using (MemoryStream output = new MemoryStream())
                    {
                        input.CopyTo(output);
                        byte[] b = output.ToArray();
                        if (b.Length >= minBytes) return b;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool IsDecodableImage(byte[] data, int minDim)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (Image img = Image.FromStream(ms))
                {
                    return img.Width >= minDim && img.Height >= minDim;
                }
            }
            catch { return false; }
        }

        private static byte[] DownloadDiscordAppImage(string appId, string hash)
        {
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(hash)) return null;

            // Use the public icon CDN directly; do not accept API-route artwork.
            string[] urls = new string[]
            {
                string.Format("https://cdn.discordapp.com/app-icons/{0}/{1}.png?size=1024", appId, hash),
                string.Format("https://media.discordapp.net/app-icons/{0}/{1}.png?size=1024", appId, hash)
            };

            foreach (string url in urls)
            {
                byte[] b = DownloadBytes(url, 256);
                if (b != null && IsDecodableImage(b, 16)) return b;
            }

            return null;
        }

        public static string DictString(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return null;
            string v = dict[key].ToString().Trim();
            return v.Length == 0 ? null : v;
        }

        private static Dictionary<string, object> DownloadJson(string url)
        {
            try
            {
                byte[] data = DownloadBytes(url, 2);
                if (data == null) return null;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 10 * 1024 * 1024;
                return serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data));
            }
            catch { return null; }
        }

        public static Dictionary<string, object> DictObject(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key] as Dictionary<string, object>;
        }

        public static object[] DictArray(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key)) return null;
            // Untyped JSON arrays deserialize as ArrayList on .NET Framework.
            System.Collections.IEnumerable values = dict[key] as System.Collections.IEnumerable;
            if (values == null || dict[key] is string) return null;
            List<object> items = new List<object>();
            foreach (object value in values) items.Add(value);
            return items.ToArray();
        }

        private static string NormalizeSteamName(string name)
        {
            // Trademark symbols are decoration, not part of the game identity.
            return GameDatabase.NormalizeString(Regex.Replace(name ?? "", "[™®©]", ""));
        }

        private static bool IsSameSteamGame(GameItem game, Dictionary<string, object> item)
        {
            // Steam type 0 is a base game. Exclude DLC, demos, tools and bundles.
            if (DictString(item, "success") != "1" || DictString(item, "type") != "0") return false;
            string name = NormalizeSteamName(DictString(item, "name"));
            return name.Length > 0 && name == NormalizeSteamName(game.name);
        }

        private static byte[] FetchSteamIconData(GameItem game, Dictionary<string, object> metadata)
        {
            HashSet<uint> candidates = new HashSet<uint>();
            object[] skus = DictArray(metadata, "third_party_skus");
            if (skus != null)
            {
                foreach (object value in skus)
                {
                    Dictionary<string, object> sku = value as Dictionary<string, object>;
                    uint appId;
                    if (DictString(sku, "distributor") == "steam" &&
                        uint.TryParse(DictString(sku, "sku"), out appId) && appId > 0)
                        candidates.Add(appId);
                }
            }

            byte[] icon = DownloadSteamCommunityIcon(game, candidates);
            if (icon != null) return icon;

            // Search only when no linked base-game icon was found. Never use the
            // first fuzzy result: titles must match and resolve to one base game.
            if (string.IsNullOrWhiteSpace(game.name)) return null;
            string searchName = Regex.Replace(game.name, "[™®©]", "").Trim();
            Dictionary<string, object> search = DownloadJson(
                "https://store.steampowered.com/api/storesearch/?term=" +
                Uri.EscapeDataString(searchName) + "&l=english&cc=US");
            object[] items = DictArray(search, "items");
            if (items == null) return null;
            HashSet<uint> matches = new HashSet<uint>();
            foreach (object value in items)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                uint appId;
                if (DictString(item, "type") == "app" &&
                    NormalizeSteamName(DictString(item, "name")) == NormalizeSteamName(game.name) &&
                    uint.TryParse(DictString(item, "id"), out appId) && appId > 0)
                    matches.Add(appId);
            }
            return DownloadSteamCommunityIcon(game, matches);
        }

        private static byte[] DownloadSteamCommunityIcon(GameItem game, HashSet<uint> appIds)
        {
            if (appIds.Count == 0) return null;
            List<Dictionary<string, object>> ids = new List<Dictionary<string, object>>();
            foreach (uint appId in appIds)
                ids.Add(new Dictionary<string, object> { { "appid", appId } });
            Dictionary<string, object> input = new Dictionary<string, object>
            {
                { "ids", ids },
                { "context", new Dictionary<string, object> { { "language", "english" }, { "country_code", "US" } } },
                { "data_request", new Dictionary<string, object> { { "include_assets", true } } }
            };
            string json = new JavaScriptSerializer().Serialize(input);
            Dictionary<string, object> response = DownloadJson(
                "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(json));
            object[] items = DictArray(DictObject(response, "response"), "store_items");
            if (items == null) return null;
            Dictionary<string, object> match = null;
            foreach (object value in items)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                uint appId;
                if (!IsSameSteamGame(game, item) ||
                    !uint.TryParse(DictString(item, "appid"), out appId) || !appIds.Contains(appId)) continue;
                if (match != null) return null; // Ambiguous editions retain Discord's icon.
                match = item;
            }
            if (match == null) return null;
            string hash = DictString(DictObject(match, "assets"), "community_icon");
            if (hash == null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$")) return null;
            string url = string.Format(
                "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{0}/{1}_full.jpg",
                DictString(match, "appid"), hash);
            byte[] bytes = DownloadBytes(url, 256);
            // Accept square icons only, never capsules, banners or library covers.
            if (bytes == null) return null;
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes))
                using (Image image = Image.FromStream(stream))
                    return image.Width >= 128 && image.Width == image.Height ? bytes : null;
            }
            catch { return null; }
        }

        private static byte[] FetchHighQualityIconData(GameItem game)
        {
            if (game == null || string.IsNullOrEmpty(game.id)) return null;
            Dictionary<string, object> metadata = DownloadJson(
                "https://discord.com/api/v10/oauth2/applications/" + game.id + "/rpc");

            byte[] steamIcon = FetchSteamIconData(game, metadata);
            if (steamIcon != null) return steamIcon;

            // Steam outages, missing icons and non-Steam games retain Discord art.
            string iconHash = DictString(metadata, "icon");
            if (iconHash != null && iconHash != game.icon)
            {
                byte[] updated = DownloadDiscordAppImage(game.id, iconHash);
                if (updated != null) return updated;
            }
            return DownloadDiscordAppImage(game.id, game.icon);
        }

        public static Bitmap CreateRoundedThumbnail(Image src, int size, int radius)
        {
            if (src == null) return null;

            // Supersample the rounded silhouette, including its alpha edge.
            // Graphics.SetClip creates a hard, jagged edge even with SmoothingMode.
            int renderSize = size * 4;
            int renderRadius = radius * 4;
            Bitmap bmp = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppArgb);
            using (Bitmap artwork = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppArgb))
            using (Graphics artworkGraphics = Graphics.FromImage(artwork))
            {
                artworkGraphics.Clear(Color.FromArgb(20, 20, 28));
                artworkGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                artworkGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                artworkGraphics.CompositingQuality = CompositingQuality.HighQuality;
                float scale = Math.Max((float)renderSize / src.Width, (float)renderSize / src.Height);
                int destW = (int)Math.Ceiling(src.Width * scale);
                int destH = (int)Math.Ceiling(src.Height * scale);
                int destX = (renderSize - destW) / 2;
                int destY = (renderSize - destH) / 2;
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    artworkGraphics.DrawImage(src, new Rectangle(destX, destY, destW, destH),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
                }

                using (Graphics g = Graphics.FromImage(bmp))
                using (GraphicsPath path = Program.GetRoundedPath(
                    new Rectangle(2, 2, renderSize - 4, renderSize - 4), renderRadius))
                using (TextureBrush texture = new TextureBrush(artwork, WrapMode.Clamp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    // FillPath antialiases the texture and background together,
                    // leaving fully transparent pixels outside the rounded tile.
                    g.FillPath(texture, path);
                }
            }
            return bmp;
        }

        public Image GetFallbackGamepadIcon(int size, int radius = 8)
        {
            string key = size + "_" + radius;
            lock (_lock)
            {
                Image image;
                if (!_fallbackIcons.TryGetValue(key, out image))
                {
                    image = CreateFallbackGamepadIcon(size, radius);
                    _fallbackIcons[key] = image;
                }
                return image;
            }
        }

        private Image CreateFallbackGamepadIcon(int size, int radius)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Rectangle rect = new Rectangle(0, 0, size, size);
                using (GraphicsPath path = Program.GetRoundedPath(rect, radius))
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(rect, Color.FromArgb(32, 32, 45), Color.FromArgb(20, 20, 30), 45f))
                    {
                        g.FillPath(lgb, path);
                    }
                    using (Pen p = new Pen(Color.FromArgb(45, 45, 62), 1f))
                    {
                        g.DrawPath(p, path);
                    }

                    // Centered controller icon
                    float scale = size / 48f;
                    using (Pen iconPen = new Pen(Theme.TextMuted, Math.Max(1.4f, 1.8f * scale)))
                    {
                        float cx = size / 2f;
                        float cy = size / 2f;
                        float bw = 22f * scale;
                        float bh = 14f * scale;
                        RectangleF body = new RectangleF(cx - bw / 2, cy - bh / 2, bw, bh);
                        using (GraphicsPath gp = Program.GetRoundedPath(Rectangle.Round(body), (int)(6 * scale)))
                        {
                            g.DrawPath(iconPen, gp);
                        }
                    }
                }
            }
                    return bmp;
        }
    }

    #endregion

    #region Quest Asset Cache

    /// <summary>
    /// Downloads and caches Discord Quest thumbnails with bounded memory usage.
    /// </summary>
    public class QuestAssetCache
    {
        private static QuestAssetCache _instance;
        public static QuestAssetCache Instance
        {
            get
            {
                if (_instance == null) _instance = new QuestAssetCache();
                return _instance;
            }
        }

        private const int MaxMemoryEntries = 120;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Image> _memoryCache = new Dictionary<string, Image>();
        private readonly LinkedList<string> _lruOrder = new LinkedList<string>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly Dictionary<string, DateTime> _retryAfter = new Dictionary<string, DateTime>();

        public string AssetDirectory
        {
            get
            {
                string dir = Path.Combine(GameDatabase.Instance.CacheDirectory, "quest_assets");
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        public Image GetThumbnail(string assetPath, int size, int radius, Action onLoaded)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || string.Equals(assetPath, "PLACEHOLDER", StringComparison.OrdinalIgnoreCase))
                return null;

            string key = assetPath + "_" + size + "_" + radius;
            lock (_lock)
            {
                Image cached;
                if (_memoryCache.TryGetValue(key, out cached)) return cached;
                DateTime retry;
                if (_retryAfter.TryGetValue(key, out retry) && DateTime.UtcNow < retry) return null;
                if (!_inFlight.Add(key)) return null;
            }

            ThreadPool.QueueUserWorkItem(_ => LoadThumbnail(assetPath, size, radius, key, onLoaded));
            return null;
        }

        private void LoadThumbnail(string assetPath, int size, int radius, string key, Action onLoaded)
        {
            bool loaded = false;
            try
            {
                string safeName = Regex.Replace(assetPath, @"[^a-zA-Z0-9._-]", "_");
                string diskPath = Path.Combine(AssetDirectory, safeName);
                byte[] data = null;
                if (File.Exists(diskPath))
                {
                    try { data = File.ReadAllBytes(diskPath); } catch { data = null; }
                }
                if (data == null || data.Length == 0)
                {
                    string cleanPath = assetPath.TrimStart('/');
                    string url = "https://cdn.discordapp.com/" + cleanPath;
                    data = IconCache.DownloadBytes(url, 64);
                    if (data != null && data.Length > 0)
                    {
                        try { File.WriteAllBytes(diskPath, data); } catch { }
                    }
                }
                if (data != null && data.Length > 0)
                {
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image src = Image.FromStream(ms))
                    {
                        Bitmap thumb = IconCache.CreateRoundedThumbnail(src, size, radius);
                        lock (_lock)
                        {
                            if (!_memoryCache.ContainsKey(key))
                            {
                                _memoryCache[key] = thumb;
                                _lruOrder.AddLast(key);
                                while (_lruOrder.Count > MaxMemoryEntries)
                                {
                                    string oldest = _lruOrder.First.Value;
                                    _lruOrder.RemoveFirst();
                                    _memoryCache.Remove(oldest);
                                }
                            }
                            else
                            {
                                thumb.Dispose();
                            }
                        }
                        loaded = true;
                    }
                }
            }
            catch { }
            finally
            {
                lock (_lock)
                {
                    _inFlight.Remove(key);
                    if (!loaded && !_memoryCache.ContainsKey(key))
                        _retryAfter[key] = DateTime.UtcNow.AddMinutes(5);
                }
                if (loaded && onLoaded != null)
                {
                    try { onLoaded(); } catch { }
                }
            }
        }
    }

    #endregion

    #region Minimal Custom UI Controls

    /// <summary>
    /// Pill-shaped primary action button with violet-to-blue gradient (#7C5CFF to #3B82F6).
    /// <summary>
    /// Sleek, compact pill action button with luminous gradient, specular glass highlight,
    /// micro-icons (play/stop), and a subtle divider timer badge.
    /// </summary>
    public class PillActionButton : Control
    {
        private bool _isHovered = false;
        private bool _isPressed = false;
        private float _hoverFactor = 0f;
        private System.Windows.Forms.Timer _animTimer;
        private string _actionText = "Launch";
        private string _timerText = "";
        private bool _isRunning = false;

        public event EventHandler ActionClicked;

        public PillActionButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            this.Cursor = Cursors.Hand;
            this.Size = new Size(148, 36);

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 15;
            _animTimer.Tick += (s, e) =>
            {
                float target = _isHovered ? 1f : 0f;
                if (Math.Abs(_hoverFactor - target) < 0.08f)
                {
                    _hoverFactor = target;
                    _animTimer.Stop();
                }
                else
                {
                    _hoverFactor += (target - _hoverFactor) * 0.35f;
                }
                Invalidate();
            };
        }

        public string ActionText
        {
            get { return _actionText; }
            set { _actionText = value; Invalidate(); }
        }

        public string TimerText
        {
            get { return _timerText; }
            set { _timerText = value; Invalidate(); }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public void SetRunningState(bool running)
        {
            _isRunning = running;
            int targetWidth = running ? 196 : 148;
            this.Size = new Size(targetWidth, 36);
            int parentWidth = (this.Parent is MainForm) ? 420 : ((this.Parent != null) ? this.Parent.ClientSize.Width : 420);
            this.Location = new Point((parentWidth - targetWidth) / 2, 338);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovered = true;
            _animTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovered = false;
            _isPressed = false;
            _animTimer.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isPressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_isPressed)
            {
                _isPressed = false;
                Invalidate();
                if (ClientRectangle.Contains(e.Location))
                {
                    if (ActionClicked != null) ActionClicked(this, EventArgs.Empty);
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                if (ActionClicked != null) ActionClicked(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private static Color LerpColor(Color from, Color to, float amount)
        {
            float amt = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)(from.R + (to.R - from.R) * amt);
            int g = (int)(from.G + (to.G - from.G) * amt);
            int b = (int)(from.B + (to.B - from.B) * amt);
            return Color.FromArgb(r, g, b);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _animTimer != null)
            {
                _animTimer.Dispose();
                _animTimer = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int pillRadius = Height / 2;

            // 1. Soft ambient glow behind the button
            Color glowColor = _isRunning ? Color.FromArgb(40, 220, 45, 80) : Color.FromArgb(35, 110, 80, 245);
            Rectangle glowRect = new Rectangle(1, 2, Width - 3, Height - 2);
            using (GraphicsPath gpGlow = Program.GetRoundedPath(glowRect, pillRadius))
            using (SolidBrush glowBrush = new SolidBrush(glowColor))
            {
                g.FillPath(glowBrush, gpGlow);
            }

            Rectangle mainRect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Program.GetRoundedPath(mainRect, pillRadius))
            {
                // 2. Refined luminous gradient fill
                Color c1, c2;
                if (!_isRunning)
                {
                    // Idle: rich violet to electric azure blue
                    Color base1 = Color.FromArgb(110, 74, 246);  // #6E4AF6
                    Color hover1 = Color.FromArgb(132, 98, 255); // #8462FF
                    Color base2 = Color.FromArgb(45, 115, 246);  // #2D73F6
                    Color hover2 = Color.FromArgb(70, 140, 255); // #468CFF

                    c1 = LerpColor(base1, hover1, _hoverFactor);
                    c2 = LerpColor(base2, hover2, _hoverFactor);
                    if (_isPressed)
                    {
                        c1 = Color.FromArgb(92, 58, 220);
                        c2 = Color.FromArgb(30, 95, 215);
                    }
                }
                else
                {
                    // Running: rich ruby/crimson to deep violet
                    Color base1 = Color.FromArgb(206, 45, 82);   // #CE2D52
                    Color hover1 = Color.FromArgb(225, 60, 98);  // #E13C62
                    Color base2 = Color.FromArgb(110, 56, 213);  // #6E38D5
                    Color hover2 = Color.FromArgb(130, 75, 230); // #824BE6

                    c1 = LerpColor(base1, hover1, _hoverFactor);
                    c2 = LerpColor(base2, hover2, _hoverFactor);
                    if (_isPressed)
                    {
                        c1 = Color.FromArgb(175, 30, 65);
                        c2 = Color.FromArgb(90, 40, 185);
                    }
                }

                using (LinearGradientBrush lgb = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), c1, c2, 0f))
                {
                    g.FillPath(lgb, path);
                }

                // 4. Subtle dark outer rim
                using (Pen rimPen = new Pen(Color.FromArgb(50, 0, 0, 0), 1f))
                {
                    g.DrawPath(rimPen, path);
                }
            }

            int centerY = Height / 2;

            if (!_isRunning)
            {
                // Idle state: [▶]  Launch
                Font btnFont = FontManager.ButtonFont;
                string text = _actionText;
                Size textSize = TextRenderer.MeasureText(g, text, btnFont, Size.Empty, TextFormatFlags.NoPadding);

                float iconW = 7f;
                float gap = 7f;
                float totalW = iconW + gap + textSize.Width;
                float startX = (Width - totalW) / 2f;

                // Play triangle icon
                PointF[] playPts = new PointF[]
                {
                    new PointF(startX, centerY - 4.5f),
                    new PointF(startX + iconW, centerY),
                    new PointF(startX, centerY + 4.5f)
                };
                using (SolidBrush wb = new SolidBrush(Color.White))
                {
                    g.FillPolygon(wb, playPts);
                }

                Point textPt = new Point((int)(startX + iconW + gap), centerY - textSize.Height / 2);
                TextRenderer.DrawText(g, text, btnFont, textPt, Color.White, TextFormatFlags.NoPadding);
            }
            else
            {
                // Running state: [■]  Stop  │  00:12:45
                Font btnFont = FontManager.ButtonFont;
                Font timerFont = FontManager.TimerFont;

                string label = "Stop";
                string timer = string.IsNullOrEmpty(_timerText) ? "00:00:00" : _timerText;

                Size labelSize = TextRenderer.MeasureText(g, label, btnFont, Size.Empty, TextFormatFlags.NoPadding);
                Size timerSize = TextRenderer.MeasureText(g, timer, timerFont, Size.Empty, TextFormatFlags.NoPadding);

                float iconW = 7f;
                float gap1 = 6f;
                float gap2 = 9f;
                float divW = 1f;
                float gap3 = 9f;

                float totalW = iconW + gap1 + labelSize.Width + gap2 + divW + gap3 + timerSize.Width;
                float startX = (Width - totalW) / 2f;

                // Stop square icon
                RectangleF stopRect = new RectangleF(startX, centerY - 3.5f, 7f, 7f);
                using (GraphicsPath sp = Program.GetRoundedPath(Rectangle.Round(stopRect), 1))
                using (SolidBrush wb = new SolidBrush(Color.White))
                {
                    g.FillPath(wb, sp);
                }

                float curX = startX + iconW + gap1;
                Point labelPt = new Point((int)curX, centerY - labelSize.Height / 2);
                TextRenderer.DrawText(g, label, btnFont, labelPt, Color.White, TextFormatFlags.NoPadding);

                curX += labelSize.Width + gap2;
                // Subtle vertical divider line
                using (Pen divPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f))
                {
                    g.DrawLine(divPen, (int)curX, centerY - 7, (int)curX, centerY + 7);
                }

                curX += divW + gap3;
                Point timerPt = new Point((int)curX, centerY - timerSize.Height / 2);
                TextRenderer.DrawText(g, timer, timerFont, timerPt, Color.FromArgb(235, 242, 255), TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>
    /// Hero control: 96px icon rounded 24px, 18px name, 12px monospace exe, running dot, crossfade motion.
    /// </summary>
    public class HeroGameControl : Control
    {
        private GameItem _game;
        private Image _currentIcon;
        private Image _prevIcon;
        private string _prevName;
        private string _prevExe;
        private float _crossfadeAlpha = 1.0f;
        private System.Windows.Forms.Timer _crossfadeTimer;
        private int _crossfadeStep = 0;
        private const int TotalSteps = 10; // 10 steps * 15ms = 150ms

        private bool _isRunning = false;
        private bool _isNameHovered = false;
        private bool _isSearchActive = false;
        private Rectangle _nameHitRect = Rectangle.Empty;

        public event EventHandler NameClicked;
        public event MouseEventHandler EmptyMouseDown;

        public HeroGameControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(420, 180);

            _crossfadeTimer = new System.Windows.Forms.Timer();
            _crossfadeTimer.Interval = 15;
            _crossfadeTimer.Tick += OnCrossfadeTick;
        }

        public GameItem SelectedGame
        {
            get { return _game; }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            set { _isRunning = value; Invalidate(); }
        }

        public bool IsSearchActive
        {
            get { return _isSearchActive; }
            set { _isSearchActive = value; Invalidate(); }
        }

        public void SetGame(GameItem newGame, bool animated = true)
        {
            if (newGame == null) return;

            if (_game != null && animated && (!string.Equals(_game.name, newGame.name) || !string.Equals(_game.exe, newGame.exe)))
            {
                _prevIcon = _currentIcon;
                _prevName = _game.name;
                _prevExe = _game.exe;
                _crossfadeAlpha = 0.0f;
                _crossfadeStep = 0;
                _crossfadeTimer.Start();
            }
            else
            {
                _crossfadeTimer.Stop();
                _crossfadeAlpha = 1.0f;
                _prevIcon = null;
            }

            _game = newGame;
            _currentIcon = IconCache.Instance.GetIcon(newGame, 96, 24, (img) =>
            {
                if (this.IsHandleCreated && _game == newGame)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed || _game != newGame) return;
                            _currentIcon = img;
                            Invalidate();
                        }));
                    }
                    catch { }
                }
            });

            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // A background disk load can finish before this control has a handle.
            if (_game != null) SetGame(_game, false);
        }

        private void OnCrossfadeTick(object sender, EventArgs e)
        {
            _crossfadeStep++;
            float t = (float)_crossfadeStep / TotalSteps;
            // 150ms ease-out quad
            _crossfadeAlpha = 1.0f - (1.0f - t) * (1.0f - t);

            if (_crossfadeStep >= TotalSteps)
            {
                _crossfadeAlpha = 1.0f;
                _crossfadeTimer.Stop();
                _prevIcon = null;
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _crossfadeTimer != null)
            {
                _crossfadeTimer.Dispose();
                _crossfadeTimer = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isSearchActive) return;

            bool hovered = _nameHitRect.Contains(e.Location);
            if (hovered != _isNameHovered)
            {
                _isNameHovered = hovered;
                this.Cursor = _isNameHovered ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_isNameHovered)
            {
                _isNameHovered = false;
                this.Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (!_isSearchActive && _nameHitRect.Contains(e.Location))
                {
                    if (NameClicked != null) NameClicked(this, EventArgs.Empty);
                }
                else
                {
                    if (EmptyMouseDown != null) EmptyMouseDown(this, e);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int iconSize = 96;
            int iconX = (Width - iconSize) / 2;
            int iconY = 8;
            Rectangle iconRect = new Rectangle(iconX, iconY, iconSize, iconSize);

            // Ambient shadow behind hero icon
            Rectangle shadowRect = new Rectangle(iconX - 2, iconY + 3, iconSize + 4, iconSize + 4);
            using (GraphicsPath shadowPath = Program.GetRoundedPath(shadowRect, 26))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(35, 0, 0, 0)))
            {
                g.FillPath(shadowBrush, shadowPath);
            }

            // Crossfade icon rendering
            if (_crossfadeAlpha < 1.0f && _prevIcon != null)
            {
                DrawImageAlpha(g, _prevIcon, iconRect, 1.0f - _crossfadeAlpha);
                DrawImageAlpha(g, _currentIcon, iconRect, _crossfadeAlpha);
            }
            else
            {
                if (_currentIcon != null)
                {
                    g.DrawImage(_currentIcon, iconRect, 0, 0, _currentIcon.Width, _currentIcon.Height, GraphicsUnit.Pixel);
                }
            }

            // If inline search is open, hide the static name and exe so the text input occupies this spot
            if (_isSearchActive) return;

            // Game Name (18px)
            string curName = _game != null ? _game.name : "Select Game";
            Font nameFont = FontManager.NameFont;
            Size nameSize = TextRenderer.MeasureText(g, curName, nameFont);

            int nameY = 116;
            int nameX = (Width - nameSize.Width) / 2;
            _nameHitRect = new Rectangle(nameX - 8, nameY - 2, nameSize.Width + 16, nameSize.Height + 4);

            // 6px green running dot next to game name
            if (_isRunning)
            {
                int dotSize = 6;
                int dotX = nameX - 14;
                int dotY = nameY + (nameSize.Height - dotSize) / 2;
                using (SolidBrush dotBrush = new SolidBrush(Theme.StatusGreen))
                {
                    g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                }
            }

            Color nameColor = _isNameHovered ? Color.White : Theme.TextPrimary;
            if (_crossfadeAlpha < 1.0f && !string.IsNullOrEmpty(_prevName))
            {
                Color prevColor = Color.FromArgb((int)((1.0f - _crossfadeAlpha) * 255), Theme.TextPrimary);
                Color nextColor = Color.FromArgb((int)(_crossfadeAlpha * 255), nameColor);
                TextRenderer.DrawText(g, _prevName, nameFont, new Point((Width - TextRenderer.MeasureText(g, _prevName, nameFont).Width) / 2, nameY), prevColor);
                TextRenderer.DrawText(g, curName, nameFont, new Point(nameX, nameY), nextColor);
            }
            else
            {
                TextRenderer.DrawText(g, curName, nameFont, new Point(nameX, nameY), nameColor);
            }

            // Exe Name (12px muted monospace)
            string curExe = _game != null ? _game.exe : "";
            if (!string.IsNullOrEmpty(curExe))
            {
                Font exeFont = FontManager.ExeFont;
                Size exeSize = TextRenderer.MeasureText(g, curExe, exeFont);
                int exeY = 146;
                int exeX = (Width - exeSize.Width) / 2;

                if (_crossfadeAlpha < 1.0f && !string.IsNullOrEmpty(_prevExe))
                {
                    Color prevExeColor = Color.FromArgb((int)((1.0f - _crossfadeAlpha) * 255), Theme.TextMuted);
                    Color nextExeColor = Color.FromArgb((int)(_crossfadeAlpha * 255), Theme.TextMuted);
                    TextRenderer.DrawText(g, _prevExe, exeFont, new Point((Width - TextRenderer.MeasureText(g, _prevExe, exeFont).Width) / 2, exeY), prevExeColor);
                    TextRenderer.DrawText(g, curExe, exeFont, new Point(exeX, exeY), nextExeColor);
                }
                else
                {
                    TextRenderer.DrawText(g, curExe, exeFont, new Point(exeX, exeY), Theme.TextMuted);
                }
            }
        }

        private static void DrawImageAlpha(Graphics g, Image img, Rectangle rect, float alpha)
        {
            if (img == null || alpha <= 0f) return;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            if (alpha >= 1f)
            {
                g.DrawImage(img, rect);
                return;
            }

            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha));
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(img, rect, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }

    /// <summary>
    /// Switcher: One row of small icons (32px, 8px gap), centered. Up to 6 recent games.
    /// Unselected icons are at 50% opacity and go to 100% on hover.
    /// Excludes the currently selected game. Tiny tooltip on hover only.
    /// </summary>
    public class GameSwitcherControl : Control
    {
        private List<GameItem> _recentGames = new List<GameItem>();
        private GameItem _excludedGame;
        private int _hoveredIndex = -1;
        private ToolTip _toolTip;
        private Dictionary<string, Image> _iconCache32 = new Dictionary<string, Image>();

        public event Action<GameItem> GameSelected;
        public event MouseEventHandler EmptyMouseDown;

        public GameSwitcherControl(ToolTip toolTip)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(420, 40);
            _toolTip = toolTip;
        }

        public void SetRecentGames(List<GameItem> games, GameItem currentHero)
        {
            _recentGames = games ?? new List<GameItem>();
            _excludedGame = currentHero;
            _hoveredIndex = -1;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _iconCache32.Clear();
        }

        private List<GameItem> GetVisibleGames()
        {
            List<GameItem> visible = new List<GameItem>();
            string exName = _excludedGame != null ? _excludedGame.name : "";
            string exExe = _excludedGame != null ? _excludedGame.exe : "";

            foreach (var g in _recentGames)
            {
                if (g == null) continue;
                if (!string.Equals(g.name, exName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(g.exe, exExe, StringComparison.OrdinalIgnoreCase))
                {
                    visible.Add(g);
                    if (visible.Count >= 6) break;
                }
            }
            return visible;
        }

        private Rectangle GetIconRect(int index, int totalCount)
        {
            int iconSize = 36;
            int gap = 8;
            int totalWidth = totalCount * iconSize + (totalCount - 1) * gap;
            int startX = (Width - totalWidth) / 2;
            int y = (Height - iconSize) / 2;
            return new Rectangle(startX + index * (iconSize + gap), y, iconSize, iconSize);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            List<GameItem> list = GetVisibleGames();
            int newHover = -1;

            for (int i = 0; i < list.Count; i++)
            {
                if (GetIconRect(i, list.Count).Contains(e.Location))
                {
                    newHover = i;
                    break;
                }
            }

            if (newHover != _hoveredIndex)
            {
                _hoveredIndex = newHover;
                this.Cursor = (_hoveredIndex >= 0) ? Cursors.Hand : Cursors.Default;
                if (_toolTip != null)
                {
                    if (_hoveredIndex >= 0 && _hoveredIndex < list.Count)
                    {
                        _toolTip.SetToolTip(this, list[_hoveredIndex].name);
                    }
                    else
                    {
                        _toolTip.SetToolTip(this, "");
                    }
                }
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                this.Cursor = Cursors.Default;
                if (_toolTip != null) _toolTip.SetToolTip(this, "");
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                List<GameItem> list = GetVisibleGames();
                for (int i = 0; i < list.Count; i++)
                {
                    if (GetIconRect(i, list.Count).Contains(e.Location))
                    {
                        if (GameSelected != null) GameSelected(list[i]);
                        return;
                    }
                }
                if (EmptyMouseDown != null) EmptyMouseDown(this, e);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            List<GameItem> list = GetVisibleGames();
            for (int i = 0; i < list.Count; i++)
            {
                GameItem item = list[i];
                Rectangle r = GetIconRect(i, list.Count);
                float opacity = (i == _hoveredIndex) ? 1.0f : 0.70f;

                Image iconImg = GetCached32(item);
                if (iconImg != null)
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = opacity;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                        g.DrawImage(iconImg, r, 0, 0, iconImg.Width, iconImg.Height, GraphicsUnit.Pixel, ia);
                    }
                }

                // If hovered, draw subtle glowing ring around it
                if (i == _hoveredIndex)
                {
                    using (GraphicsPath hoverPath = Program.GetRoundedPath(new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), 10))
                    using (Pen hoverPen = new Pen(Color.FromArgb(100, 124, 92, 255), 1.5f))
                    {
                        g.DrawPath(hoverPen, hoverPath);
                    }
                }
            }
        }

        private Image GetCached32(GameItem item)
        {
            string key = (item.id ?? "") + "_" + (item.icon ?? item.NormalizedName);
            if (_iconCache32.ContainsKey(key)) return _iconCache32[key];

            Image img = IconCache.Instance.GetIcon(item, 36, 8, (loaded) =>
            {
                if (this.IsHandleCreated)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            _iconCache32[key] = loaded;
                            Invalidate();
                        }));
                    }
                    catch { }
                }
            });
            _iconCache32[key] = img;
            return img;
        }
    }

    /// <summary>
    /// Sleek popup overlay list for inline search results
    /// </summary>
    public class SearchDropdownList : Control
    {
        private List<GameItem> _items = new List<GameItem>();
        private int _selectedIndex = 0;
        private int _hoveredIndex = -1;
        private int _firstVisibleIndex;
        private const int MinItemHeight = 44;
        private const int TextGap = 1;
        private int _itemHeight;   // 0 = not measured yet
        private int _titleLineHeight;
        private int _exeLineHeight;
        private readonly Action<Image> _iconLoaded;

        // Row geometry comes from the fonts' real line heights, not hard-coded pixel boxes.
        // GDI (TextRenderer) clips to the rect it's given, so a 14px box under a ~19px font silently
        // chopped the descenders off every exe line. Measuring once also survives font fallback
        // (Inter -> Segoe UI, Geist Mono -> Consolas) and non-100% DPI, where fixed boxes break.
        private int ItemHeight
        {
            get
            {
                if (_itemHeight == 0)
                {
                    const TextFormatFlags measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                    _titleLineHeight = TextRenderer.MeasureText("Ag", FontManager.SmallFont, Size.Empty, measure).Height;
                    _exeLineHeight = TextRenderer.MeasureText("Ag", FontManager.ListExeFont, Size.Empty, measure).Height;
                    _itemHeight = Math.Max(MinItemHeight, _titleLineHeight + TextGap + _exeLineHeight + 8);
                }
                return _itemHeight;
            }
        }

        private int VisibleRowCount { get { return Math.Max(1, Height / ItemHeight); } }

        private void EnsureSelectionVisible()
        {
            if (_selectedIndex < _firstVisibleIndex) _firstVisibleIndex = Math.Max(0, _selectedIndex);
            if (_selectedIndex >= _firstVisibleIndex + VisibleRowCount)
                _firstVisibleIndex = _selectedIndex - VisibleRowCount + 1;
            _firstVisibleIndex = Math.Max(0, Math.Min(_firstVisibleIndex, _items.Count - VisibleRowCount));
        }

        public event Action<GameItem> ItemSelected;

        public SearchDropdownList()
        {
            _iconLoaded = image =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(new Action(() => { if (!IsDisposed) Invalidate(); })); } catch { }
            };
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.BackColor = Theme.BgSurface;
            this.Cursor = Cursors.Hand;
        }

        public void SetResults(List<GameItem> results)
        {
            _items = results ?? new List<GameItem>();
            _selectedIndex = _items.Count > 0 ? 0 : -1;
            _hoveredIndex = -1;
            _firstVisibleIndex = 0;
            Invalidate();
        }

        public GameItem SelectedItem
        {
            get
            {
                if (_selectedIndex >= 0 && _selectedIndex < _items.Count) return _items[_selectedIndex];
                return null;
            }
        }

        public void MoveSelection(int delta)
        {
            if (_items.Count == 0) return;
            int next = _selectedIndex + delta;
            if (next < 0) next = _items.Count - 1;
            else if (next >= _items.Count) next = 0;
            _selectedIndex = next;
            _hoveredIndex = -1;
            EnsureSelectionVisible();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_items != null) EnsureSelectionVisible();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int rows = SystemInformation.MouseWheelScrollLines;
            if (rows < 0) rows = VisibleRowCount;
            _firstVisibleIndex = Math.Max(0, Math.Min(Math.Max(0, _items.Count - VisibleRowCount),
                _firstVisibleIndex - Math.Sign(e.Delta) * rows));
            _hoveredIndex = -1;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = _firstVisibleIndex + e.Y / ItemHeight;
            if (e.Y < 0 || e.Y >= VisibleRowCount * ItemHeight) idx = -1;
            if (idx >= 0 && idx < _items.Count && idx != _hoveredIndex)
            {
                _hoveredIndex = idx;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || e.Y < 0 || e.Y >= VisibleRowCount * ItemHeight) return;
            int idx = _firstVisibleIndex + e.Y / ItemHeight;
            if (idx >= 0 && idx < _items.Count)
            {
                _selectedIndex = idx;
                if (ItemSelected != null) ItemSelected(_items[idx]);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Background card with rounded border
            Rectangle cardRect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Program.GetRoundedPath(cardRect, 10))
            {
                using (SolidBrush bg = new SolidBrush(Theme.BgSurface))
                {
                    g.FillPath(bg, path);
                }
                using (Pen p = new Pen(Theme.BorderSubtle, 1f))
                {
                    g.DrawPath(p, path);
                }
                g.SetClip(path);
            }

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, "No matches", FontManager.SmallFont, cardRect, Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int count = Math.Min(_items.Count - _firstVisibleIndex, VisibleRowCount);
            for (int row = 0; row < count; row++)
            {
                int i = _firstVisibleIndex + row;
                GameItem item = _items[i];
                int itemY = row * ItemHeight;
                Rectangle itemRect = new Rectangle(0, itemY, Width, ItemHeight);

                bool isSelected = (i == _selectedIndex);
                bool isHovered = (i == _hoveredIndex);

                if (isSelected || isHovered)
                {
                    using (SolidBrush h = new SolidBrush(isSelected ? Color.FromArgb(36, 36, 52) : Color.FromArgb(28, 28, 40)))
                    {
                        g.FillRectangle(h, itemRect);
                    }
                }

                // 28px game icon, vertically centred in the row
                Image iconImg = IconCache.Instance.GetIcon(item, 28, 6, _iconLoaded);
                if (iconImg != null)
                {
                    g.DrawImage(iconImg, new Rectangle(10, itemY + (ItemHeight - 28) / 2, 28, 28), 0, 0, iconImg.Width, iconImg.Height, GraphicsUnit.Pixel);
                }

                // Title + exe stacked as one block centred in the row; each rect is exactly one measured
                // line tall so nothing gets clipped. NoPadding must match the flags used to measure.
                const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine |
                                             TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
                int textWidth = Width - 62;
                int blockTop = itemY + (ItemHeight - (_titleLineHeight + TextGap + _exeLineHeight)) / 2;
                Rectangle textRect = new Rectangle(46, blockTop, textWidth, _titleLineHeight);
                TextRenderer.DrawText(g, item.name, FontManager.SmallFont, textRect, Theme.TextPrimary, line);

                Rectangle exeRect = new Rectangle(46, textRect.Bottom + TextGap, textWidth, _exeLineHeight);
                TextRenderer.DrawText(g, item.exe, FontManager.ListExeFont, exeRect, Theme.TextMuted, line);
            }
            if (_items.Count > VisibleRowCount)
            {
                int trackHeight = Height - 12;
                int thumbHeight = Math.Max(12, trackHeight * VisibleRowCount / _items.Count);
                int thumbY = 6 + (trackHeight - thumbHeight) * _firstVisibleIndex / (_items.Count - VisibleRowCount);
                using (SolidBrush brush = new SolidBrush(Theme.TextDim))
                    g.FillRectangle(brush, Width - 7, thumbY, 3, thumbHeight);
            }
        }
    }

    #endregion

    public class ExtraGameInstance
    {
        public GameItem Game;
        public Process DummyProcess;
        public string DummyExePath;
        public string DummyMode;
        public IntPtr DummyHwnd;
        public Stopwatch Stopwatch;
        public bool QualificationFinalRecorded;
        public DiscordRpcClient RpcClient;
        public Thread RpcThread;
        public ManualResetEvent RpcStop;
        public readonly object RpcLock = new object();
    }

    public class MultiInstanceStripControl : Control
    {
        public const int MaxExtraInstances = 6;
        private const int ButtonWidth = 94;
        private const int ButtonHeight = 26;
        private const int ButtonGap = 10;

        private bool _canQueue = true;
        private bool _canLaunchAnother = true;
        private bool _isLaunchingExtra = false;
        private int _hoveredButton = -1; // -1 = none, 0 = Queue, 1 = + Launch
        private ToolTip _toolTip;

        public event EventHandler QueueButtonClicked;
        public event EventHandler AddInstanceClicked;
#pragma warning disable 67
        public event Action<ExtraGameInstance> InstanceStopRequested;
#pragma warning restore 67
        public event MouseEventHandler EmptyMouseDown;

        public MultiInstanceStripControl(ToolTip toolTip)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(420, 32);
            _toolTip = toolTip;
        }

        public void SetActionStates(bool canQueue, bool canLaunchAnother, bool launchingExtra)
        {
            _canQueue = canQueue;
            _canLaunchAnother = canLaunchAnother;
            _isLaunchingExtra = launchingExtra;
            RefreshTooltip();
            Invalidate();
        }

        public void SetInstances(List<ExtraGameInstance> instances, bool launchingExtra)
        {
            int count = instances != null ? instances.Count : 0;
            SetActionStates(_canQueue, count < MaxExtraInstances, launchingExtra);
        }

        public void RefreshTooltip()
        {
            if (_toolTip == null) return;
            string nextTip = "";
            if (_hoveredButton == 0 && _canQueue)
            {
                nextTip = "Add game to queue";
            }
            else if (_hoveredButton == 1 && _canLaunchAnother)
            {
                nextTip = _isLaunchingExtra ? "Starting..." : "Run another game at the same time";
            }
            if (_toolTip.GetToolTip(this) != nextTip)
            {
                _toolTip.SetToolTip(this, nextTip);
            }
        }

        private Rectangle GetButtonRect(int index)
        {
            int totalWidth = ButtonWidth * 2 + ButtonGap;
            int startX = (Width - totalWidth) / 2;
            int y = (Height - ButtonHeight) / 2;
            return new Rectangle(startX + index * (ButtonWidth + ButtonGap), y, ButtonWidth, ButtonHeight);
        }

        private bool IsButtonEnabled(int index)
        {
            if (index == 0) return _canQueue;
            if (index == 1) return _canLaunchAnother && !_isLaunchingExtra;
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int nextHover = -1;
            if (GetButtonRect(0).Contains(e.Location)) nextHover = 0;
            else if (GetButtonRect(1).Contains(e.Location)) nextHover = 1;

            if (nextHover != _hoveredButton)
            {
                _hoveredButton = nextHover;
                this.Cursor = IsButtonEnabled(_hoveredButton) ? Cursors.Hand : Cursors.Default;
                RefreshTooltip();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredButton != -1)
            {
                _hoveredButton = -1;
                this.Cursor = Cursors.Default;
                RefreshTooltip();
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (GetButtonRect(0).Contains(e.Location))
            {
                if (_canQueue && QueueButtonClicked != null) QueueButtonClicked(this, EventArgs.Empty);
                return;
            }
            if (GetButtonRect(1).Contains(e.Location))
            {
                if (_canLaunchAnother && !_isLaunchingExtra && AddInstanceClicked != null) AddInstanceClicked(this, EventArgs.Empty);
                return;
            }
            if (EmptyMouseDown != null) EmptyMouseDown(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            for (int i = 0; i < 2; i++)
            {
                Rectangle r = GetButtonRect(i);
                bool enabled = IsButtonEnabled(i);
                bool hovered = enabled && (_hoveredButton == i);

                using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 8))
                {
                    using (SolidBrush bg = new SolidBrush(hovered ? Theme.BgCardHover : Theme.BgSurface))
                    {
                        g.FillPath(bg, path);
                    }
                    using (Pen borderPen = new Pen(hovered ? Theme.AccentViolet : Theme.BorderSubtle, 1f))
                    {
                        g.DrawPath(borderPen, path);
                    }
                }

                Color fg = !enabled ? Theme.TextDim : (hovered ? Theme.TextPrimary : Theme.TextMuted);
                if (i == 0)
                {
                    using (Pen qPen = new Pen(fg, 1.4f))
                    {
                        int qx = r.X + 14;
                        int qy = r.Y + 7;
                        g.DrawLine(qPen, qx, qy + 2, qx + 9, qy + 2);
                        g.DrawLine(qPen, qx, qy + 6, qx + 7, qy + 6);
                        g.DrawLine(qPen, qx, qy + 10, qx + 6, qy + 10);
                        g.DrawLine(qPen, qx + 8, qy + 8, qx + 11, qy + 10);
                        g.DrawLine(qPen, qx + 11, qy + 10, qx + 8, qy + 12);
                    }
                    Rectangle textRect = new Rectangle(r.X + 26, r.Y, r.Width - 32, r.Height);
                    TextRenderer.DrawText(g, "Queue", FontManager.ListExeFont, textRect, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                else
                {
                    using (Pen plusPen = new Pen(fg, 1.5f))
                    {
                        int px = r.X + 17;
                        int py = r.Y + r.Height / 2;
                        g.DrawLine(plusPen, px, py - 4, px, py + 4);
                        g.DrawLine(plusPen, px - 4, py, px + 4, py);
                    }
                    Rectangle textRect = new Rectangle(r.X + 24, r.Y, r.Width - 30, r.Height);
                    string label = _isLaunchingExtra ? "..." : "Launch";
                    TextRenderer.DrawText(g, label, FontManager.ListExeFont, textRect, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }
    }

    public enum QueueItemState
    {
        Pending,
        Running,
        Completed
    }

    public enum QueuePhase
    {
        Idle,
        PlayingGame,
        BreakCooldown
    }

    public class QueueItem
    {
        public GameItem Game;
        public int PlannedPlaySeconds;
        public int PlannedBreakAfterSeconds;
        public QueueItemState State;
        public int ActualPlayedSeconds;
    }

    public class QueueStripControl : Control
    {
        public const int MaxQueueItems = 5;

        private bool _isConcurrentMode = false;
        private List<QueueItem> _items = new List<QueueItem>();
        private int _activeIndex = -1;
        private QueuePhase _phase = QueuePhase.Idle;
        private double _activeElapsedSeconds = 0;
        private double _breakElapsedSeconds = 0;
        private int _breakTargetSeconds = 0;

        private GameItem _concurrentPrimaryGame;
        private double _concurrentPrimaryElapsed = 0;
        private List<ExtraGameInstance> _concurrentExtras = new List<ExtraGameInstance>();

        private int _hoveredKind = 0; // 0 = none, 1 = tile, 2 = connector
        private int _hoveredIndex = -1;
        private ToolTip _toolTip;
        private Dictionary<string, Image> _iconCache = new Dictionary<string, Image>();

#pragma warning disable 67
        public event EventHandler AddGameClicked;
#pragma warning restore 67
        public event Action<int> RemoveGameRequested;
        public event Action<ExtraGameInstance> ConcurrentStopRequested;
        public event MouseEventHandler EmptyMouseDown;

        public QueueStripControl(ToolTip toolTip)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(420, 44);
            _toolTip = toolTip;
        }

        public void UpdateState(List<QueueItem> items, int activeIndex, QueuePhase phase,
            double activeElapsedSeconds, double breakElapsedSeconds, int breakTargetSeconds)
        {
            _isConcurrentMode = false;
            _items = items != null ? new List<QueueItem>(items) : new List<QueueItem>();
            _activeIndex = activeIndex;
            _phase = phase;
            _activeElapsedSeconds = activeElapsedSeconds;
            _breakElapsedSeconds = breakElapsedSeconds;
            _breakTargetSeconds = breakTargetSeconds;
            _concurrentExtras.Clear();
            ClampHover();
            RefreshTooltip();
            Invalidate();
        }

        public void UpdateConcurrentState(GameItem primaryGame, double primaryElapsedSeconds, List<ExtraGameInstance> extras)
        {
            _isConcurrentMode = true;
            _concurrentPrimaryGame = primaryGame;
            _concurrentPrimaryElapsed = primaryElapsedSeconds;
            _concurrentExtras = extras != null ? new List<ExtraGameInstance>(extras) : new List<ExtraGameInstance>();
            ClampHover();
            RefreshTooltip();
            Invalidate();
        }

        private void ClampHover()
        {
            int tileCount = GetTileCount();
            if (_hoveredKind == 1 && _hoveredIndex >= tileCount) { _hoveredKind = 0; _hoveredIndex = -1; }
            if (_hoveredKind == 2 && _hoveredIndex >= tileCount - 1) { _hoveredKind = 0; _hoveredIndex = -1; }
            if (_hoveredKind > 2) { _hoveredKind = 0; _hoveredIndex = -1; }
        }

        public void RefreshTooltip()
        {
            if (_toolTip == null) return;
            string tip = "";
            if (_isConcurrentMode)
            {
                if (_hoveredKind == 1 && _hoveredIndex == 0 && _concurrentPrimaryGame != null)
                {
                    string name = !string.IsNullOrEmpty(_concurrentPrimaryGame.name) ? _concurrentPrimaryGame.name : "Game";
                    tip = name + " — Elapsed: " + FormatDuration((int)Math.Floor(_concurrentPrimaryElapsed)) + " (Running)";
                }
                else if (_hoveredKind == 1 && _hoveredIndex >= 1 && _hoveredIndex - 1 < _concurrentExtras.Count)
                {
                    ExtraGameInstance inst = _concurrentExtras[_hoveredIndex - 1];
                    string name = inst.Game != null && !string.IsNullOrEmpty(inst.Game.name) ? inst.Game.name : "Game";
                    int elapsed = inst.Stopwatch != null ? (int)Math.Floor(inst.Stopwatch.Elapsed.TotalSeconds) : 0;
                    tip = name + " — Elapsed: " + FormatDuration(elapsed) + " — Click to stop";
                }
            }
            else if (_hoveredKind == 1 && _hoveredIndex >= 0 && _hoveredIndex < _items.Count)
            {
                QueueItem item = _items[_hoveredIndex];
                string name = item.Game != null && !string.IsNullOrEmpty(item.Game.name) ? item.Game.name : "Game";
                if (item.State == QueueItemState.Running)
                {
                    int elapsed = Math.Max(0, (int)Math.Floor(_activeElapsedSeconds));
                    int left = Math.Max(0, item.PlannedPlaySeconds - elapsed);
                    tip = name + " — Elapsed: " + FormatDuration(elapsed) + " / " +
                          FormatShortDuration(item.PlannedPlaySeconds) + " (" + FormatShortDuration(left) + " left)";
                }
                else if (item.State == QueueItemState.Completed)
                {
                    tip = name + " — Completed (" + FormatShortDuration(item.ActualPlayedSeconds) + ")";
                }
                else
                {
                    int mins = Math.Max(1, (int)Math.Round(item.PlannedPlaySeconds / 60.0));
                    tip = name + " — Queued (~" + mins + "m) — Click to remove";
                }
            }
            else if (_hoveredKind == 2 && _hoveredIndex >= 0 && _hoveredIndex < _items.Count - 1)
            {
                QueueItem current = _items[_hoveredIndex];
                QueueItem next = _items[_hoveredIndex + 1];
                string nextName = next.Game != null && !string.IsNullOrEmpty(next.Game.name) ? next.Game.name : "Next game";
                if (_phase == QueuePhase.BreakCooldown && _hoveredIndex == _activeIndex)
                {
                    int elapsed = Math.Max(0, (int)Math.Floor(_breakElapsedSeconds));
                    tip = "Break before " + nextName + " — Elapsed: " + FormatShortDuration(elapsed) + " / " +
                          FormatShortDuration(_breakTargetSeconds);
                }
                else
                {
                    int mins = Math.Max(1, (int)Math.Round(current.PlannedBreakAfterSeconds / 60.0));
                    tip = "Random break: ~" + mins + "m before " + nextName;
                }
            }

            if (_toolTip.GetToolTip(this) != tip)
            {
                _toolTip.SetToolTip(this, tip);
            }
        }

        private static string FormatDuration(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            int h = totalSeconds / 3600;
            int m = (totalSeconds % 3600) / 60;
            int s = totalSeconds % 60;
            return string.Format("{0:00}:{1:00}:{2:00}", h, m, s);
        }

        private static string FormatShortDuration(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            int m = totalSeconds / 60;
            int s = totalSeconds % 60;
            return string.Format("{0:00}:{1:00}", m, s);
        }

        private static string FormatCompactRemaining(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            if (totalSeconds >= 60) return ((totalSeconds + 59) / 60) + "m";
            return totalSeconds + "s";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _iconCache.Clear();
        }

        private int GetTileCount()
        {
            return _isConcurrentMode ? (1 + _concurrentExtras.Count) : _items.Count;
        }

        private void GetMetrics(out int tileSize, out int connWidth, out int startX)
        {
            int count = GetTileCount();
            if (count <= 3) { tileSize = 34; connWidth = _isConcurrentMode ? 36 : 44; }
            else if (count == 4) { tileSize = 30; connWidth = 36; }
            else { tileSize = 26; connWidth = 26; }

            int chainWidth = count > 0 ? (count * tileSize + (count - 1) * connWidth) : 0;
            startX = (Width - chainWidth) / 2;
        }

        private Rectangle GetTileRect(int index)
        {
            int tileSize, connWidth, startX;
            GetMetrics(out tileSize, out connWidth, out startX);
            int x = startX + index * (tileSize + connWidth);
            int y = (Height - tileSize) / 2;
            return new Rectangle(x, y, tileSize, tileSize);
        }

        private Rectangle GetConnectorRect(int index)
        {
            int tileSize, connWidth, startX;
            GetMetrics(out tileSize, out connWidth, out startX);
            int x = startX + index * (tileSize + connWidth) + tileSize;
            return new Rectangle(x, 0, connWidth, Height);
        }

        private Image GetCachedIcon(GameItem item, int size)
        {
            if (item == null) return null;
            string key = (item.id ?? "") + "_" + (item.icon ?? item.NormalizedName) + "_" + size;
            if (_iconCache.ContainsKey(key)) return _iconCache[key];

            Image img = IconCache.Instance.GetIcon(item, size, 7, (loaded) =>
            {
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (!this.IsDisposed)
                            {
                                _iconCache[key] = loaded;
                                Invalidate();
                            }
                        }));
                    }
                    catch { }
                }
            });
            _iconCache[key] = img;
            return img;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int count = GetTileCount();
            int newKind = 0;
            int newIndex = -1;

            for (int i = 0; i < count; i++)
            {
                if (GetTileRect(i).Contains(e.Location))
                {
                    newKind = 1;
                    newIndex = i;
                    break;
                }
                if (i < count - 1 && GetConnectorRect(i).Contains(e.Location))
                {
                    newKind = 2;
                    newIndex = i;
                    break;
                }
            }
            if (newKind != _hoveredKind || newIndex != _hoveredIndex)
            {
                _hoveredKind = newKind;
                _hoveredIndex = newIndex;
                bool hand = false;
                if (_hoveredKind == 1)
                {
                    if (_isConcurrentMode) hand = _hoveredIndex >= 1;
                    else if (_hoveredIndex >= 0 && _hoveredIndex < _items.Count) hand = _items[_hoveredIndex].State == QueueItemState.Pending;
                }
                this.Cursor = hand ? Cursors.Hand : Cursors.Default;
                RefreshTooltip();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredKind != 0)
            {
                _hoveredKind = 0;
                _hoveredIndex = -1;
                this.Cursor = Cursors.Default;
                RefreshTooltip();
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int count = GetTileCount();
            for (int i = 0; i < count; i++)
            {
                if (GetTileRect(i).Contains(e.Location))
                {
                    if (_isConcurrentMode)
                    {
                        if (i >= 1 && i - 1 < _concurrentExtras.Count && ConcurrentStopRequested != null)
                        {
                            ConcurrentStopRequested(_concurrentExtras[i - 1]);
                        }
                    }
                    else if (_items[i].State == QueueItemState.Pending && RemoveGameRequested != null)
                    {
                        RemoveGameRequested(i);
                    }
                    return;
                }
            }
            if (EmptyMouseDown != null) EmptyMouseDown(this, e);
        }

        private void PaintGameIcon(Graphics g, GameItem game, Rectangle r)
        {
            Image img = GetCachedIcon(game, r.Width);
            if (img != null)
            {
                g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
            }
            else
            {
                using (SolidBrush bg = new SolidBrush(Theme.BgSurface)) g.FillRectangle(bg, r);
                string initial = (game != null && !string.IsNullOrEmpty(game.name))
                    ? game.name.Substring(0, 1).ToUpperInvariant() : "G";
                TextRenderer.DrawText(g, initial, FontManager.SmallFont, r, Theme.TextPrimary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (_isConcurrentMode)
            {
                int count = GetTileCount();
                for (int i = 0; i < count; i++)
                {
                    GameItem game = (i == 0) ? _concurrentPrimaryGame : _concurrentExtras[i - 1].Game;
                    Rectangle r = GetTileRect(i);
                    bool hovered = (_hoveredKind == 1 && _hoveredIndex == i && i >= 1);

                    using (GraphicsPath clipPath = Program.GetRoundedPath(r, 7))
                    {
                        GraphicsState saved = g.Save();
                        g.SetClip(clipPath);
                        PaintGameIcon(g, game, r);
                        if (hovered)
                        {
                            using (SolidBrush hoverOverlay = new SolidBrush(Color.FromArgb(175, 11, 11, 15)))
                            {
                                g.FillRectangle(hoverOverlay, r);
                            }
                        }
                        g.Restore(saved);
                    }

                    using (GraphicsPath borderPath = Program.GetRoundedPath(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 7))
                    using (Pen borderPen = new Pen(hovered ? Theme.AccentViolet : Theme.BorderSubtle, 1f))
                    {
                        g.DrawPath(borderPen, borderPath);
                    }

                    if (hovered)
                    {
                        using (Pen xPen = new Pen(Color.White, 1.6f))
                        {
                            int cx = r.X + r.Width / 2;
                            int cy = r.Y + r.Height / 2;
                            g.DrawLine(xPen, cx - 4, cy - 4, cx + 4, cy + 4);
                            g.DrawLine(xPen, cx + 4, cy - 4, cx - 4, cy + 4);
                        }
                    }
                    else
                    {
                        Rectangle dotRect = new Rectangle(r.Right - 7, r.Bottom - 7, 6, 6);
                        using (SolidBrush bgDot = new SolidBrush(Theme.BgBase))
                        {
                            g.FillEllipse(bgDot, dotRect.X - 1, dotRect.Y - 1, dotRect.Width + 2, dotRect.Height + 2);
                        }
                        using (SolidBrush dotBrush = new SolidBrush(Theme.StatusGreen))
                        {
                            g.FillEllipse(dotBrush, dotRect);
                        }
                    }

                    // Plain line connector (---) with no arrow when running concurrent games without a queue
                    if (i < count - 1)
                    {
                        Rectangle cr = GetConnectorRect(i);
                        int lineY = cr.Y + cr.Height / 2;
                        using (Pen linePen = new Pen(Theme.TextMuted, 1.5f))
                        {
                            g.DrawLine(linePen, cr.X + 5, lineY, cr.Right - 5, lineY);
                        }
                    }
                }
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                QueueItem item = _items[i];
                Rectangle r = GetTileRect(i);
                bool tileHovered = (_hoveredKind == 1 && _hoveredIndex == i);

                using (GraphicsPath clipPath = Program.GetRoundedPath(r, 7))
                {
                    GraphicsState saved = g.Save();
                    g.SetClip(clipPath);
                    PaintGameIcon(g, item.Game, r);

                    if (item.State == QueueItemState.Running)
                    {
                        double ratio = item.PlannedPlaySeconds > 0
                            ? Math.Max(0.0, Math.Min(1.0, _activeElapsedSeconds / item.PlannedPlaySeconds))
                            : 0.0;
                        int fillW = (int)Math.Round(r.Width * ratio);
                        if (fillW > 0)
                        {
                            using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(115, 124, 92, 255)))
                            {
                                g.FillRectangle(fillBrush, r.X, r.Y, fillW, r.Height);
                            }
                        }

                        int bandHeight = 13;
                        Rectangle bandRect = new Rectangle(r.X, r.Bottom - bandHeight, r.Width, bandHeight);
                        using (SolidBrush bandBrush = new SolidBrush(Color.FromArgb(175, 11, 11, 15)))
                        {
                            g.FillRectangle(bandBrush, bandRect);
                        }
                        if (fillW > 0)
                        {
                            using (SolidBrush accentBar = new SolidBrush(Theme.StatusGreen))
                            {
                                g.FillRectangle(accentBar, r.X, r.Bottom - 2, fillW, 2);
                            }
                        }

                        int leftSeconds = Math.Max(0, item.PlannedPlaySeconds - (int)Math.Floor(_activeElapsedSeconds));
                        TextRenderer.DrawText(g, FormatCompactRemaining(leftSeconds), FontManager.QueueTinyFont, bandRect,
                            Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    else if (item.State == QueueItemState.Completed)
                    {
                        using (SolidBrush doneOverlay = new SolidBrush(Color.FromArgb(120, 11, 11, 15)))
                        {
                            g.FillRectangle(doneOverlay, r);
                        }
                    }
                    else if (tileHovered)
                    {
                        using (SolidBrush hoverOverlay = new SolidBrush(Color.FromArgb(175, 11, 11, 15)))
                        {
                            g.FillRectangle(hoverOverlay, r);
                        }
                    }

                    g.Restore(saved);
                }

                Color borderColor = Theme.BorderSubtle;
                float borderWidth = 1f;
                if (item.State == QueueItemState.Running) { borderColor = Theme.AccentViolet; borderWidth = 1.5f; }
                else if (item.State == QueueItemState.Completed) { borderColor = Theme.StatusGreen; }
                else if (tileHovered) { borderColor = Theme.AccentViolet; }

                using (GraphicsPath borderPath = Program.GetRoundedPath(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 7))
                using (Pen borderPen = new Pen(borderColor, borderWidth))
                {
                    g.DrawPath(borderPen, borderPath);
                }

                if (item.State == QueueItemState.Completed)
                {
                    using (Pen checkPen = new Pen(Theme.StatusGreen, 1.8f))
                    {
                        int cx = r.Right - 9;
                        int cy = r.Bottom - 7;
                        g.DrawLine(checkPen, cx - 3, cy, cx - 1, cy + 2);
                        g.DrawLine(checkPen, cx - 1, cy + 2, cx + 4, cy - 3);
                    }
                }
                else if (item.State == QueueItemState.Pending && tileHovered)
                {
                    using (Pen xPen = new Pen(Color.White, 1.6f))
                    {
                        int cx = r.X + r.Width / 2;
                        int cy = r.Y + r.Height / 2;
                        g.DrawLine(xPen, cx - 4, cy - 4, cx + 4, cy + 4);
                        g.DrawLine(xPen, cx + 4, cy - 4, cx - 4, cy + 4);
                    }
                }

                if (i < _items.Count - 1)
                {
                    Rectangle ar = GetConnectorRect(i);
                    bool activeBreak = (_phase == QueuePhase.BreakCooldown && i == _activeIndex);
                    Color arrowColor = activeBreak ? Theme.AccentViolet : Theme.TextMuted;
                    string timerText;
                    if (activeBreak)
                    {
                        int leftBreak = Math.Max(0, _breakTargetSeconds - (int)Math.Floor(_breakElapsedSeconds));
                        timerText = FormatShortDuration(leftBreak);
                    }
                    else
                    {
                        int mins = Math.Max(1, (int)Math.Round(item.PlannedBreakAfterSeconds / 60.0));
                        timerText = mins + "m";
                    }

                    Rectangle labelRect = new Rectangle(ar.X, ar.Y + 4, ar.Width, 14);
                    TextRenderer.DrawText(g, timerText, FontManager.QueueTinyFont, labelRect, arrowColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    int lineY = ar.Y + ar.Height / 2 + 6;
                    int x1 = ar.X + 4;
                    int x2 = ar.Right - 4;
                    using (Pen arrowPen = new Pen(arrowColor, 1.5f))
                    {
                        g.DrawLine(arrowPen, x1, lineY, x2, lineY);
                        g.DrawLine(arrowPen, x2 - 4, lineY - 3, x2, lineY);
                        g.DrawLine(arrowPen, x2 - 4, lineY + 3, x2, lineY);
                    }
                }
            }
        }
    }

    #region Quests Explorer UI

    /// <summary>
    /// Dark themed menu renderer used for region and sort dropdown menus in QuestsForm.
    /// </summary>
    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Theme.TextPrimary;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.BgSurface))
            {
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.ContentRectangle.Height / 2;
            using (Pen p = new Pen(Theme.BorderSubtle, 1f))
            {
                e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (SolidBrush b = new SolidBrush(e.Item.Selected ? Theme.BgCardHover : Theme.BgSurface))
            {
                e.Graphics.FillRectangle(b, rect);
            }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.BgSurface; } }
            public override Color MenuBorder { get { return Theme.BorderHover; } }
            public override Color MenuItemBorder { get { return Theme.BorderHover; } }
            public override Color MenuItemSelected { get { return Theme.BgCardHover; } }
            public override Color ImageMarginGradientBegin { get { return Theme.BgSurface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.BgSurface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.BgSurface; } }
            public override Color SeparatorDark { get { return Theme.BorderSubtle; } }
            public override Color SeparatorLight { get { return Theme.BorderSubtle; } }
        }
    }

    /// <summary>
    /// Crisp cached 18x12 GDI+ country flag and globe icons for region selectors and quest badges.
    /// </summary>
    public static class FlagIcons
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private static Bitmap _globe;

        public static string GetCountryDisplayName(string code)
        {
            string c = QuestDatabase.NormalizeCountryCode(code);
            if (c == "US") return "United States";
            if (c == "UK") return "United Kingdom";
            if (c == "DE") return "Germany";
            if (c == "CA") return "Canada";
            if (c == "TR") return "Turkey";
            if (c == "FR") return "France";
            if (c == "JP") return "Japan";
            if (c == "BR") return "Brazil";
            if (c == "AU") return "Australia";
            if (c == "ES") return "Spain";
            if (c == "PL") return "Poland";
            if (c == "KR") return "South Korea";
            return c;
        }

        public static Bitmap GetGlobeIcon()
        {
            lock (_lock)
            {
                if (_globe != null) return _globe;
                Bitmap bmp = new Bitmap(18, 12, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath clip = Program.GetRoundedPath(new Rectangle(0, 0, 18, 12), 2))
                    {
                        g.SetClip(clip);
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(26, 30, 48)))
                            g.FillRectangle(bg, 0, 0, 18, 12);
                        using (Pen p = new Pen(Color.FromArgb(130, 165, 255), 1.1f))
                        {
                            g.DrawEllipse(p, 4, 1, 9, 9);
                            g.DrawEllipse(p, 6, 1, 5, 9);
                            g.DrawLine(p, 4, 6, 13, 6);
                        }
                        g.ResetClip();
                    }
                    using (GraphicsPath border = Program.GetRoundedPath(new Rectangle(0, 0, 17, 11), 2))
                    using (Pen bp = new Pen(Color.FromArgb(65, 255, 255, 255), 1f))
                        g.DrawPath(bp, border);
                }
                _globe = bmp;
                return _globe;
            }
        }

        public static Bitmap GetFlag(string countryCode)
        {
            string c = QuestDatabase.NormalizeCountryCode(countryCode);
            if (string.IsNullOrEmpty(c)) return GetGlobeIcon();
            lock (_lock)
            {
                Bitmap cached;
                if (_cache.TryGetValue(c, out cached)) return cached;
                Bitmap bmp = RenderFlag(c);
                _cache[c] = bmp;
                return bmp;
            }
        }

        private static Bitmap RenderFlag(string c)
        {
            const int w = 18;
            const int h = 12;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath clip = Program.GetRoundedPath(new Rectangle(0, 0, w, h), 2))
                {
                    g.SetClip(clip);
                    DrawFlagGraphics(g, c, w, h);
                    g.ResetClip();
                }
                using (GraphicsPath border = Program.GetRoundedPath(new Rectangle(0, 0, w - 1, h - 1), 2))
                using (Pen bp = new Pen(Color.FromArgb(75, 0, 0, 0), 1f))
                    g.DrawPath(bp, border);
            }
            return bmp;
        }

        private static void DrawFlagGraphics(Graphics g, string c, int w, int h)
        {
            if (c == "US")
            {
                Fill(g, Color.White, 0, 0, w, h);
                using (SolidBrush red = new SolidBrush(Color.FromArgb(178, 34, 52)))
                {
                    for (int i = 0; i < 6; i += 2) g.FillRectangle(red, 0, i * 2, w, 2);
                }
                Fill(g, Color.FromArgb(60, 59, 110), 0, 0, 8, 7);
                using (SolidBrush star = new SolidBrush(Color.White))
                {
                    g.FillRectangle(star, 2, 2, 1, 1);
                    g.FillRectangle(star, 5, 2, 1, 1);
                    g.FillRectangle(star, 3, 4, 1, 1);
                    g.FillRectangle(star, 6, 4, 1, 1);
                }
                return;
            }
            if (c == "UK")
            {
                Fill(g, Color.FromArgb(1, 33, 105), 0, 0, w, h);
                using (Pen wDiag = new Pen(Color.White, 2.4f))
                using (Pen rDiag = new Pen(Color.FromArgb(200, 16, 46), 1.1f))
                {
                    g.DrawLine(wDiag, 0, 0, w, h);
                    g.DrawLine(wDiag, w, 0, 0, h);
                    g.DrawLine(rDiag, 0, 0, w, h);
                    g.DrawLine(rDiag, w, 0, 0, h);
                }
                Fill(g, Color.White, 7, 0, 4, h);
                Fill(g, Color.White, 0, 4, w, 4);
                Fill(g, Color.FromArgb(200, 16, 46), 8, 0, 2, h);
                Fill(g, Color.FromArgb(200, 16, 46), 0, 5, w, 2);
                return;
            }
            if (c == "DE") { HStripes(g, w, h, Color.FromArgb(20, 20, 24), Color.FromArgb(221, 0, 0), Color.FromArgb(255, 206, 0)); return; }
            if (c == "FR") { VStripes(g, w, h, Color.FromArgb(0, 85, 164), Color.White, Color.FromArgb(239, 65, 53)); return; }
            if (c == "IT") { VStripes(g, w, h, Color.FromArgb(0, 146, 70), Color.White, Color.FromArgb(206, 43, 55)); return; }
            if (c == "BE") { VStripes(g, w, h, Color.FromArgb(20, 20, 24), Color.FromArgb(253, 218, 36), Color.FromArgb(239, 51, 64)); return; }
            if (c == "IE") { VStripes(g, w, h, Color.FromArgb(22, 155, 98), Color.White, Color.FromArgb(255, 136, 62)); return; }
            if (c == "MX")
            {
                VStripes(g, w, h, Color.FromArgb(0, 104, 71), Color.White, Color.FromArgb(206, 17, 38));
                Fill(g, Color.FromArgb(140, 98, 57), 8, 5, 2, 2);
                return;
            }
            if (c == "NL") { HStripes(g, w, h, Color.FromArgb(174, 28, 40), Color.White, Color.FromArgb(33, 70, 139)); return; }
            if (c == "AT") { HStripes(g, w, h, Color.FromArgb(237, 41, 57), Color.White, Color.FromArgb(237, 41, 57)); return; }
            if (c == "AR")
            {
                HStripes(g, w, h, Color.FromArgb(116, 172, 223), Color.White, Color.FromArgb(116, 172, 223));
                Fill(g, Color.FromArgb(246, 180, 14), 8, 5, 2, 2);
                return;
            }
            if (c == "IN")
            {
                HStripes(g, w, h, Color.FromArgb(255, 153, 51), Color.White, Color.FromArgb(19, 136, 8));
                Fill(g, Color.FromArgb(0, 0, 128), 8, 5, 2, 2);
                return;
            }
            if (c == "PL")
            {
                Fill(g, Color.White, 0, 0, w, 6);
                Fill(g, Color.FromArgb(220, 20, 60), 0, 6, w, 6);
                return;
            }
            if (c == "ID")
            {
                Fill(g, Color.FromArgb(206, 17, 38), 0, 0, w, 6);
                Fill(g, Color.White, 0, 6, w, 6);
                return;
            }
            if (c == "UA")
            {
                Fill(g, Color.FromArgb(0, 87, 183), 0, 0, w, 6);
                Fill(g, Color.FromArgb(255, 215, 0), 0, 6, w, 6);
                return;
            }
            if (c == "ES")
            {
                Fill(g, Color.FromArgb(170, 21, 27), 0, 0, w, 3);
                Fill(g, Color.FromArgb(241, 191, 0), 0, 3, w, 6);
                Fill(g, Color.FromArgb(170, 21, 27), 0, 9, w, 3);
                Fill(g, Color.FromArgb(170, 21, 27), 4, 4, 2, 4);
                return;
            }
            if (c == "CA")
            {
                Fill(g, Color.FromArgb(255, 0, 0), 0, 0, 5, h);
                Fill(g, Color.White, 5, 0, 8, h);
                Fill(g, Color.FromArgb(255, 0, 0), 13, 0, 5, h);
                using (SolidBrush leaf = new SolidBrush(Color.FromArgb(255, 0, 0)))
                {
                    Point[] pts = new Point[] { new Point(9, 2), new Point(12, 6), new Point(9, 10), new Point(6, 6) };
                    g.FillPolygon(leaf, pts);
                }
                return;
            }
            if (c == "TR")
            {
                Fill(g, Color.FromArgb(227, 10, 23), 0, 0, w, h);
                using (SolidBrush wb = new SolidBrush(Color.White))
                using (SolidBrush rb = new SolidBrush(Color.FromArgb(227, 10, 23)))
                {
                    g.FillEllipse(wb, 4, 3, 6, 6);
                    g.FillEllipse(rb, 6, 3.6f, 5, 4.8f);
                    g.FillEllipse(wb, 10, 4.8f, 2.4f, 2.4f);
                }
                return;
            }
            if (c == "JP")
            {
                Fill(g, Color.White, 0, 0, w, h);
                using (SolidBrush red = new SolidBrush(Color.FromArgb(188, 0, 45)))
                    g.FillEllipse(red, 6, 3, 6, 6);
                return;
            }
            if (c == "BR")
            {
                Fill(g, Color.FromArgb(0, 156, 59), 0, 0, w, h);
                using (SolidBrush y = new SolidBrush(Color.FromArgb(255, 223, 0)))
                {
                    Point[] diamond = new Point[] { new Point(9, 2), new Point(16, 6), new Point(9, 10), new Point(2, 6) };
                    g.FillPolygon(y, diamond);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 39, 118)))
                    g.FillEllipse(b, 7, 4, 4, 4);
                return;
            }
            if (c == "AU" || c == "NZ")
            {
                Fill(g, Color.FromArgb(0, 0, 139), 0, 0, w, h);
                Fill(g, Color.White, 3, 0, 2, 6);
                Fill(g, Color.White, 0, 2, 8, 2);
                Fill(g, Color.FromArgb(220, 20, 40), 3, 0, 1, 6);
                Fill(g, Color.FromArgb(220, 20, 40), 0, 3, 8, 1);
                Color starCol = c == "NZ" ? Color.FromArgb(220, 30, 50) : Color.White;
                Fill(g, starCol, 13, 3, 2, 2);
                Fill(g, starCol, 11, 6, 2, 2);
                Fill(g, starCol, 14, 8, 2, 2);
                return;
            }
            if (c == "KR")
            {
                Fill(g, Color.White, 0, 0, w, h);
                using (SolidBrush red = new SolidBrush(Color.FromArgb(205, 46, 58)))
                using (SolidBrush blue = new SolidBrush(Color.FromArgb(0, 71, 160)))
                {
                    g.FillPie(red, 6, 3, 6, 6, 180, 180);
                    g.FillPie(blue, 6, 3, 6, 6, 0, 180);
                }
                Fill(g, Color.FromArgb(30, 30, 35), 2, 2, 2, 2);
                Fill(g, Color.FromArgb(30, 30, 35), 14, 2, 2, 2);
                Fill(g, Color.FromArgb(30, 30, 35), 2, 8, 2, 2);
                Fill(g, Color.FromArgb(30, 30, 35), 14, 8, 2, 2);
                return;
            }
            if (c == "SE") { CrossFlag(g, w, h, Color.FromArgb(0, 106, 167), Color.FromArgb(254, 204, 0)); return; }
            if (c == "FI") { CrossFlag(g, w, h, Color.White, Color.FromArgb(0, 47, 108)); return; }
            if (c == "DK") { CrossFlag(g, w, h, Color.FromArgb(198, 12, 48), Color.White); return; }
            if (c == "NO") { CrossFlag(g, w, h, Color.FromArgb(186, 12, 47), Color.FromArgb(0, 32, 91)); return; }
            if (c == "CH")
            {
                Fill(g, Color.FromArgb(213, 43, 30), 0, 0, w, h);
                Fill(g, Color.White, 8, 2, 2, 8);
                Fill(g, Color.White, 5, 5, 8, 2);
                return;
            }
            if (c == "PT")
            {
                Fill(g, Color.FromArgb(0, 102, 0), 0, 0, 7, h);
                Fill(g, Color.FromArgb(255, 0, 0), 7, 0, w - 7, h);
                Fill(g, Color.FromArgb(255, 204, 0), 6, 4, 2, 4);
                return;
            }
            if (c == "CN")
            {
                Fill(g, Color.FromArgb(222, 41, 16), 0, 0, w, h);
                Fill(g, Color.FromArgb(255, 222, 0), 3, 2, 3, 3);
                return;
            }
            if (c == "TW")
            {
                Fill(g, Color.FromArgb(254, 0, 0), 0, 0, w, h);
                Fill(g, Color.FromArgb(0, 0, 149), 0, 0, 9, 6);
                Fill(g, Color.White, 3, 2, 3, 2);
                return;
            }
            if (c == "SA")
            {
                Fill(g, Color.FromArgb(0, 108, 53), 0, 0, w, h);
                Fill(g, Color.White, 4, 5, 10, 2);
                return;
            }
            if (c == "AE")
            {
                HStripes(g, w, h, Color.FromArgb(0, 115, 47), Color.White, Color.FromArgb(20, 20, 24));
                Fill(g, Color.FromArgb(255, 0, 0), 0, 0, 5, h);
                return;
            }
            if (c == "SG")
            {
                Fill(g, Color.FromArgb(239, 51, 64), 0, 0, w, 6);
                Fill(g, Color.White, 0, 6, w, 6);
                Fill(g, Color.White, 3, 1, 3, 4);
                return;
            }
            if (c == "TH")
            {
                Fill(g, Color.FromArgb(165, 25, 49), 0, 0, w, h);
                Fill(g, Color.White, 0, 2, w, 8);
                Fill(g, Color.FromArgb(45, 42, 74), 0, 4, w, 4);
                return;
            }
            if (c == "VN")
            {
                Fill(g, Color.FromArgb(218, 37, 29), 0, 0, w, h);
                Fill(g, Color.FromArgb(255, 255, 0), 7, 4, 4, 4);
                return;
            }

            // Generic mini country-code badge fallback
            Fill(g, Color.FromArgb(36, 40, 60), 0, 0, w, h);
            string code = c.Length > 2 ? c.Substring(0, 2) : c;
            TextRenderer.DrawText(g, code, FontManager.QueueTinyFont, new Rectangle(0, 0, w, h), Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static void Fill(Graphics g, Color col, int x, int y, int w, int h)
        {
            using (SolidBrush b = new SolidBrush(col)) g.FillRectangle(b, x, y, w, h);
        }

        private static void HStripes(Graphics g, int w, int h, Color c1, Color c2, Color c3)
        {
            int sh = h / 3;
            Fill(g, c1, 0, 0, w, sh);
            Fill(g, c2, 0, sh, w, sh);
            Fill(g, c3, 0, sh * 2, w, h - sh * 2);
        }

        private static void VStripes(Graphics g, int w, int h, Color c1, Color c2, Color c3)
        {
            int sw = w / 3;
            Fill(g, c1, 0, 0, sw, h);
            Fill(g, c2, sw, 0, sw, h);
            Fill(g, c3, sw * 2, 0, w - sw * 2, h);
        }

        private static void CrossFlag(Graphics g, int w, int h, Color bg, Color cross)
        {
            Fill(g, bg, 0, 0, w, h);
            Fill(g, cross, 5, 0, 3, h);
            Fill(g, cross, 0, 5, w, 2);
        }
    }

    /// <summary>
    /// Double-buffered responsive card grid for browsing Discord Quests with a thin custom scrollbar.
    /// </summary>
    public class QuestGridControl : Control
    {
        private class CardHitTarget
        {
            public QuestItem Quest;
            public Rectangle CardRect;
            public Rectangle SimulateRect;
            public Rectangle QueueRect;
            public Rectangle OpenRect;
            public Rectangle DoneRect;
        }

        private List<QuestItem> _quests = new List<QuestItem>();
        private HashSet<string> _completedIds = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _simulatingIds = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _alsoGlobalIds = new HashSet<string>(StringComparer.Ordinal);
        private string _effectiveCountry = "";
        private bool _isLoading = false;
        private int _scrollOffset = 0;
        private int _contentHeight = 0;

        private readonly List<CardHitTarget> _targets = new List<CardHitTarget>();
        private Rectangle _resetFilterRect = Rectangle.Empty;
        private bool _hoverResetFilter = false;

        private int _hoveredCardIndex = -1;
        private string _hoveredButton = "";
        private int _pressedCardIndex = -1;
        private string _pressedButton = "";

        private bool _hoverScrollThumb = false;
        private bool _draggingScrollThumb = false;
        private int _dragStartY = 0;
        private int _dragStartScroll = 0;
        private Rectangle _scrollThumbRect = Rectangle.Empty;

        public event Action<QuestItem> QuestClicked;
        public event Action<QuestItem> SimulateClicked;
        public event Action<QuestItem> StopSimulateClicked;
        public event Action<QuestItem> QueueClicked;
        public event Action<QuestItem> OpenDiscordClicked;
        public event Action<QuestItem> ToggleDoneClicked;
        public event Action ResetFiltersClicked;

        public QuestGridControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            this.BackColor = Theme.BgBase;
        }

        public void SetData(List<QuestItem> quests, HashSet<string> completedIds, HashSet<string> simulatingIds, HashSet<string> alsoGlobalIds, string effectiveCountry, bool isLoading, bool resetScroll)
        {
            _quests = quests != null ? new List<QuestItem>(quests) : new List<QuestItem>();
            _completedIds = completedIds != null ? new HashSet<string>(completedIds, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            _simulatingIds = simulatingIds != null ? new HashSet<string>(simulatingIds, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            _alsoGlobalIds = alsoGlobalIds != null ? new HashSet<string>(alsoGlobalIds, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            _effectiveCountry = effectiveCountry ?? "";
            _isLoading = isLoading;
            if (resetScroll) _scrollOffset = 0;
            ClampScroll();
            Invalidate();
        }

        public void UpdateSimulatingIds(HashSet<string> simulatingIds)
        {
            _simulatingIds = simulatingIds != null ? new HashSet<string>(simulatingIds, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (QuestsForm.TryForwardResizeHitTest(this, ref m)) return;
            base.WndProc(ref m);
        }

        private int GetColumnCount()
        {
            return this.Width >= 980 ? 3 : 2;
        }

        private void ClampScroll()
        {
            const int padTop = 8;
            const int padBottom = 16;
            const int gap = 12;
            const int cardHeight = 158;

            int cols = GetColumnCount();
            int rows = _quests.Count == 0 ? 0 : (_quests.Count + cols - 1) / cols;
            _contentHeight = rows == 0 ? 0 : padTop + rows * cardHeight + (rows - 1) * gap + padBottom;
            int maxScroll = Math.Max(0, _contentHeight - this.Height);
            if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;
            if (_scrollOffset < 0) _scrollOffset = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_contentHeight <= this.Height) return;
            int step = (e.Delta > 0 ? -1 : 1) * 54;
            _scrollOffset += step;
            ClampScroll();
            UpdateHoverFromCursor();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingScrollThumb)
            {
                int trackHeight = Math.Max(1, this.Height - 16);
                int maxScroll = Math.Max(1, _contentHeight - this.Height);
                int thumbHeight = Math.Max(28, _scrollThumbRect.Height);
                int usableTrack = Math.Max(1, trackHeight - thumbHeight);
                int deltaY = e.Y - _dragStartY;
                _scrollOffset = _dragStartScroll + (int)Math.Round(deltaY * (double)maxScroll / usableTrack);
                ClampScroll();
                Invalidate();
                return;
            }
            UpdateHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_draggingScrollThumb)
            {
                _hoveredCardIndex = -1;
                _hoveredButton = "";
                _hoverResetFilter = false;
                _hoverScrollThumb = false;
                this.Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            this.Focus();

            if (!_scrollThumbRect.IsEmpty && _scrollThumbRect.Contains(e.Location))
            {
                _draggingScrollThumb = true;
                _dragStartY = e.Y;
                _dragStartScroll = _scrollOffset;
                this.Capture = true;
                return;
            }

            if (_quests.Count == 0 && !_resetFilterRect.IsEmpty && _resetFilterRect.Contains(e.Location))
            {
                _pressedCardIndex = -2;
                Invalidate();
                return;
            }

            for (int i = 0; i < _targets.Count; i++)
            {
                CardHitTarget t = _targets[i];
                if (!t.CardRect.Contains(e.Location)) continue;
                _pressedCardIndex = i;
                if (!t.SimulateRect.IsEmpty && t.SimulateRect.Contains(e.Location)) _pressedButton = "sim";
                else if (!t.QueueRect.IsEmpty && t.QueueRect.Contains(e.Location)) _pressedButton = "queue";
                else if (!t.OpenRect.IsEmpty && t.OpenRect.Contains(e.Location)) _pressedButton = "open";
                else if (!t.DoneRect.IsEmpty && t.DoneRect.Contains(e.Location)) _pressedButton = "done";
                else _pressedButton = "card";
                Invalidate();
                return;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingScrollThumb)
            {
                _draggingScrollThumb = false;
                this.Capture = false;
                UpdateHover(e.Location);
                Invalidate();
                return;
            }

            int pIdx = _pressedCardIndex;
            string pBtn = _pressedButton;
            _pressedCardIndex = -1;
            _pressedButton = "";

            if (e.Button == MouseButtons.Left)
            {
                if (pIdx == -2 && !_resetFilterRect.IsEmpty && _resetFilterRect.Contains(e.Location))
                {
                    if (ResetFiltersClicked != null) ResetFiltersClicked();
                }
                else if (pIdx >= 0 && pIdx < _targets.Count)
                {
                    CardHitTarget t = _targets[pIdx];
                    if (pBtn == "sim" && t.SimulateRect.Contains(e.Location))
                    {
                        bool simRunning = t.Quest != null && !string.IsNullOrEmpty(t.Quest.id) && _simulatingIds.Contains(t.Quest.id);
                        if (simRunning) { if (StopSimulateClicked != null) StopSimulateClicked(t.Quest); }
                        else { if (SimulateClicked != null) SimulateClicked(t.Quest); }
                    }
                    else if (pBtn == "queue" && t.QueueRect.Contains(e.Location))
                    {
                        if (QueueClicked != null) QueueClicked(t.Quest);
                    }
                    else if (pBtn == "open" && t.OpenRect.Contains(e.Location))
                    {
                        if (OpenDiscordClicked != null) OpenDiscordClicked(t.Quest);
                    }
                    else if (pBtn == "done" && t.DoneRect.Contains(e.Location))
                    {
                        if (ToggleDoneClicked != null) ToggleDoneClicked(t.Quest);
                    }
                    else if (pBtn == "card" && t.CardRect.Contains(e.Location))
                    {
                        if (QuestClicked != null) QuestClicked(t.Quest);
                    }
                }
            }
            UpdateHover(e.Location);
            Invalidate();
        }

        private void UpdateHoverFromCursor()
        {
            Point pt = this.PointToClient(Cursor.Position);
            if (this.ClientRectangle.Contains(pt)) UpdateHover(pt);
        }

        private void UpdateHover(Point pt)
        {
            int newCard = -1;
            string newBtn = "";
            bool newReset = !_resetFilterRect.IsEmpty && _resetFilterRect.Contains(pt);
            bool newThumb = !_scrollThumbRect.IsEmpty && _scrollThumbRect.Contains(pt);

            if (!newThumb && !newReset)
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    CardHitTarget t = _targets[i];
                    if (!t.CardRect.Contains(pt)) continue;
                    newCard = i;
                    if (!t.SimulateRect.IsEmpty && t.SimulateRect.Contains(pt)) newBtn = "sim";
                    else if (!t.QueueRect.IsEmpty && t.QueueRect.Contains(pt)) newBtn = "queue";
                    else if (!t.OpenRect.IsEmpty && t.OpenRect.Contains(pt)) newBtn = "open";
                    else if (!t.DoneRect.IsEmpty && t.DoneRect.Contains(pt)) newBtn = "done";
                    else newBtn = "card";
                    break;
                }
            }

            if (newCard != _hoveredCardIndex || newBtn != _hoveredButton ||
                newReset != _hoverResetFilter || newThumb != _hoverScrollThumb)
            {
                _hoveredCardIndex = newCard;
                _hoveredButton = newBtn;
                _hoverResetFilter = newReset;
                _hoverScrollThumb = newThumb;
                this.Cursor = (newCard >= 0 || newReset || newThumb) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private void OnThumbnailLoaded()
        {
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                try { this.BeginInvoke(new Action(Invalidate)); } catch { }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            _targets.Clear();
            _resetFilterRect = Rectangle.Empty;
            _scrollThumbRect = Rectangle.Empty;

            if (_quests.Count == 0)
            {
                DrawEmptyState(g);
                return;
            }

            const int padX = 18;
            const int padTop = 8;
            const int gap = 12;
            const int cardHeight = 158;

            int cols = GetColumnCount();
            int usableWidth = Math.Max(200, this.Width - padX * 2 - (cols - 1) * gap);
            int cardWidth = usableWidth / cols;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

            for (int i = 0; i < _quests.Count; i++)
            {
                int row = i / cols;
                int col = i % cols;
                int x = padX + col * (cardWidth + gap);
                int y = padTop + row * (cardHeight + gap) - _scrollOffset;

                if (y + cardHeight < -10 || y > this.Height + 10) continue;

                Rectangle cardRect = new Rectangle(x, y, cardWidth, cardHeight);
                CardHitTarget hit = DrawQuestCard(g, _quests[i], cardRect, _targets.Count, nowUtc);
                _targets.Add(hit);
            }

            DrawThinScrollbar(g);
        }

        private void DrawEmptyState(Graphics g)
        {
            string title = _isLoading ? "Loading Discord Quests..." : "No quests match your filters";
            string sub = _isLoading ? "Fetching latest quests and regional eligibility..." : "Try clearing your search or switching category tabs.";

            int cy = Math.Max(80, this.Height / 2 - 42);
            TextRenderer.DrawText(g, title, FontManager.ButtonFont,
                new Rectangle(24, cy, this.Width - 48, 28), Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, sub, FontManager.SmallFont,
                new Rectangle(24, cy + 30, this.Width - 48, 22), Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (!_isLoading)
            {
                _resetFilterRect = new Rectangle((this.Width - 132) / 2, cy + 64, 132, 32);
                bool pressed = _pressedCardIndex == -2;
                Color bg = pressed ? Theme.AccentFocus : (_hoverResetFilter ? Theme.BgCardHover : Theme.BgSurface);
                Color border = _hoverResetFilter ? Theme.AccentViolet : Theme.BorderHover;
                using (GraphicsPath p = Program.GetRoundedPath(_resetFilterRect, 8))
                {
                    using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, "Reset Filters", FontManager.SmallFont, _resetFilterRect, Theme.TextPrimary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private CardHitTarget DrawQuestCard(Graphics g, QuestItem q, Rectangle cardRect, int targetIdx, DateTimeOffset nowUtc)
        {
            CardHitTarget hit = new CardHitTarget { Quest = q, CardRect = cardRect };
            bool isDone = !string.IsNullOrEmpty(q.id) && _completedIds.Contains(q.id);
            bool cardHover = _hoveredCardIndex == targetIdx;

            Color bg = cardHover ? Color.FromArgb(23, 23, 32) : Theme.BgSurface;
            Color border = cardHover ? Theme.BorderHover : Theme.BorderSubtle;
            using (GraphicsPath cardPath = Program.GetRoundedPath(cardRect, 12))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, cardPath);
                using (Pen p = new Pen(border, 1f)) g.DrawPath(p, cardPath);
            }

            // Thumbnail (54x54)
            Rectangle thumbRect = new Rectangle(cardRect.X + 12, cardRect.Y + 12, 54, 54);
            string assetPath = q.GetThumbnailAssetPath();
            Image thumb = assetPath != null ? QuestAssetCache.Instance.GetThumbnail(assetPath, 54, 10, OnThumbnailLoaded) : null;
            if (thumb != null)
            {
                g.DrawImage(thumb, thumbRect);
            }
            else
            {
                using (GraphicsPath tp = Program.GetRoundedPath(thumbRect, 10))
                {
                    using (SolidBrush tb = new SolidBrush(Theme.BgInput)) g.FillPath(tb, tp);
                    using (Pen tpen = new Pen(Theme.BorderSubtle, 1f)) g.DrawPath(tpen, tp);
                }
                string initials = GameDatabase.GetInitials(q.DisplayTitle);
                if (initials.Length > 2) initials = initials.Substring(0, 2);
                if (string.IsNullOrEmpty(initials)) initials = "Q";
                TextRenderer.DrawText(g, initials.ToUpperInvariant(), FontManager.ButtonFont, thumbRect, Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // Top-right badges (Countdown + 18+)
            string countdown = q.GetCountdownLabel(nowUtc);
            bool endingSoon = q.IsEndingSoon(nowUtc);
            bool expired = q.IsExpired(nowUtc);
            Color cdBg = expired ? Color.FromArgb(42, 26, 30) : (endingSoon ? Color.FromArgb(50, 38, 18) : Color.FromArgb(26, 26, 38));
            Color cdFg = expired ? Color.FromArgb(235, 110, 120) : (endingSoon ? Color.FromArgb(245, 185, 75) : Theme.TextMuted);

            int badgeRight = cardRect.Right - 12;
            int cdWidth = Math.Max(52, TextRenderer.MeasureText(countdown, FontManager.BadgeFont).Width + 12);
            Rectangle cdRect = new Rectangle(badgeRight - cdWidth, cardRect.Y + 12, cdWidth, 20);
            DrawPill(g, cdRect, cdBg, Color.Transparent, countdown, cdFg, FontManager.BadgeFont, 6);
            badgeRight = cdRect.X - 6;

            if (q.IsAgeRestricted)
            {
                Rectangle ageRect = new Rectangle(badgeRight - 30, cardRect.Y + 12, 30, 20);
                DrawPill(g, ageRect, Color.FromArgb(46, 26, 36), Color.Transparent, "18+", Color.FromArgb(240, 130, 145), FontManager.BadgeFont, 6);
                badgeRight = ageRect.X - 6;
            }

            // Title & Publisher
            int textX = thumbRect.Right + 12;
            int titleWidth = Math.Max(60, badgeRight - textX);
            Rectangle titleRect = new Rectangle(textX, cardRect.Y + 11, titleWidth, 22);
            TextRenderer.DrawText(g, q.DisplayTitle, FontManager.CardTitleFont, titleRect, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            string pub = !string.IsNullOrWhiteSpace(q.game_publisher) ? q.game_publisher : q.DisplayGameName;
            Rectangle pubRect = new Rectangle(textX, cardRect.Y + 34, cardRect.Right - textX - 12, 18);
            TextRenderer.DrawText(g, pub, FontManager.ListExeFont, pubRect, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Middle metadata pills: Reward + Task + Region
            int metaY = cardRect.Y + 76;
            int metaX = cardRect.X + 12;
            int maxMetaX = cardRect.Right - 12;

            string rewardText = q.GetPrimaryRewardLabel();
            Color rewBg = q.HasOrbsReward ? Color.FromArgb(35, 29, 58) : (q.HasCollectibleReward ? Color.FromArgb(42, 32, 22) : Color.FromArgb(26, 28, 38));
            Color rewFg = q.HasOrbsReward ? Color.FromArgb(175, 152, 255) : (q.HasCollectibleReward ? Color.FromArgb(245, 195, 105) : Theme.TextPrimary);
            int rewW = Math.Min(maxMetaX - metaX, Math.Max(56, TextRenderer.MeasureText(rewardText, FontManager.BadgeFont).Width + 14));
            if (rewW > 30)
            {
                DrawPill(g, new Rectangle(metaX, metaY, rewW, 22), rewBg, Color.Transparent, rewardText, rewFg, FontManager.BadgeFont, 6);
                metaX += rewW + 6;
            }

            string taskText = q.GetPrimaryTaskLabel();
            bool isDesktop = q.HasDesktopTask;
            bool isSim = !string.IsNullOrEmpty(q.id) && _simulatingIds.Contains(q.id);
            Color taskBg = isDesktop ? Color.FromArgb(22, 44, 34) : Color.FromArgb(24, 24, 34);
            Color taskFg = isDesktop ? Theme.StatusGreen : Theme.TextMuted;
            int taskW = Math.Min(maxMetaX - metaX, Math.Max(50, TextRenderer.MeasureText(taskText, FontManager.BadgeFont).Width + 14));
            if (taskW > 42)
            {
                DrawPill(g, new Rectangle(metaX, metaY, taskW, 22), taskBg, Color.Transparent, taskText, taskFg, FontManager.BadgeFont, 6);
                metaX += taskW + 6;
            }

            if (metaX + 54 <= maxMetaX)
            {
                bool eligible = string.IsNullOrEmpty(_effectiveCountry) || q.IsEligibleInRegion(_effectiveCountry);
                bool hasAllowlist = q.region != null && !q.region.is_global && q.region.include != null && q.region.include.Count > 0;
                string badgeCc = "";
                string regText;
                if (hasAllowlist)
                {
                    string normEff = QuestDatabase.NormalizeCountryCode(_effectiveCountry);
                    badgeCc = (!string.IsNullOrEmpty(normEff) && q.region.include.Contains(normEff)) ? normEff : q.region.include[0];
                    regText = q.region.include.Count == 1 ? badgeCc : (badgeCc + " +" + (q.region.include.Count - 1));
                }
                else
                {
                    regText = "Global";
                }
                Color regBg = eligible ? Color.FromArgb(24, 28, 38) : Color.FromArgb(48, 28, 24);
                Color regFg = eligible ? Theme.TextMuted : Color.FromArgb(240, 150, 110);
                Bitmap regIcon = string.IsNullOrEmpty(badgeCc) ? null : FlagIcons.GetFlag(badgeCc);
                int iconExtra = regIcon != null ? 22 : 0;
                int regW = Math.Min(maxMetaX - metaX, TextRenderer.MeasureText(regText, FontManager.BadgeFont).Width + 12 + iconExtra);
                if (regW >= 44)
                {
                    Rectangle regRect = new Rectangle(metaX, metaY, regW, 22);
                    if (regIcon != null)
                        DrawPillWithIcon(g, regRect, regBg, Color.Transparent, regIcon, regText, regFg, FontManager.BadgeFont, 6);
                    else
                        DrawPill(g, regRect, regBg, Color.Transparent, regText, regFg, FontManager.BadgeFont, 6);
                    metaX = regRect.Right + 6;
                }
            }

            if (!string.IsNullOrEmpty(q.id) && _alsoGlobalIds.Contains(q.id) && metaX + 46 <= maxMetaX)
            {
                int globW = Math.Min(maxMetaX - metaX, TextRenderer.MeasureText("Global", FontManager.BadgeFont).Width + 12);
                if (globW >= 44)
                {
                    DrawPill(g, new Rectangle(metaX, metaY, globW, 22), Color.FromArgb(24, 28, 38), Color.Transparent, "Global", Theme.TextMuted, FontManager.BadgeFont, 6);
                }
            }

            // Bottom action buttons row
            int btnY = cardRect.Bottom - 38;
            int btnH = 26;
            int bx = cardRect.X + 12;

            if (isDesktop)
            {
                int simW = 74;
                hit.SimulateRect = new Rectangle(bx, btnY, simW, btnH);
                bool hSim = cardHover && _hoveredButton == "sim";
                bool pSim = _pressedCardIndex == targetIdx && _pressedButton == "sim";
                Color simBg = isSim
                    ? (pSim ? Color.FromArgb(185, 45, 65) : (hSim ? Color.FromArgb(235, 78, 98) : Color.FromArgb(215, 58, 78)))
                    : (pSim ? Theme.AccentBlue : (hSim ? Color.FromArgb(140, 112, 255) : Theme.AccentViolet));
                DrawPill(g, hit.SimulateRect, simBg, Color.Transparent, isSim ? "Stop" : "Simulate", Color.White, FontManager.BadgeFont, 7);
                bx += simW + 6;

                int qW = 68;
                hit.QueueRect = new Rectangle(bx, btnY, qW, btnH);
                bool hQ = cardHover && _hoveredButton == "queue";
                bool pQ = _pressedCardIndex == targetIdx && _pressedButton == "queue";
                Color qBg = pQ ? Theme.BorderHover : (hQ ? Theme.BgCardHover : Theme.BgInput);
                DrawPill(g, hit.QueueRect, qBg, hQ ? Theme.BorderHover : Theme.BorderSubtle, "+ Queue", Theme.TextPrimary, FontManager.BadgeFont, 7);
                bx += qW + 6;
            }

            int doneW = isDone ? 66 : 56;
            hit.DoneRect = new Rectangle(cardRect.Right - 12 - doneW, btnY, doneW, btnH);
            bool hDone = cardHover && _hoveredButton == "done";
            Color dBg = isDone ? Color.FromArgb(24, 52, 38) : (hDone ? Theme.BgCardHover : Theme.BgInput);
            Color dBorder = isDone ? Color.FromArgb(46, 120, 82) : (hDone ? Theme.BorderHover : Theme.BorderSubtle);
            Color dFg = isDone ? Theme.StatusGreen : Theme.TextMuted;
            DrawPill(g, hit.DoneRect, dBg, dBorder, isDone ? "\u2713 Done" : "Done", dFg, FontManager.BadgeFont, 7);

            int openW = isDesktop ? 56 : Math.Min(120, hit.DoneRect.X - bx - 8);
            if (openW >= 46)
            {
                hit.OpenRect = new Rectangle(bx, btnY, openW, btnH);
                bool hOpen = cardHover && _hoveredButton == "open";
                Color oBg = hOpen ? Theme.BgCardHover : Theme.BgInput;
                string openLabel = isDesktop ? "Open" : "Open Quest";
                DrawPill(g, hit.OpenRect, oBg, hOpen ? Theme.BorderHover : Theme.BorderSubtle, openLabel, Theme.TextPrimary, FontManager.BadgeFont, 7);
            }

            return hit;
        }

        private void DrawThinScrollbar(Graphics g)
        {
            if (_contentHeight <= this.Height) return;
            int trackHeight = Math.Max(1, this.Height - 16);
            int thumbHeight = Math.Max(28, (int)Math.Round(trackHeight * ((double)this.Height / _contentHeight)));
            int maxScroll = Math.Max(1, _contentHeight - this.Height);
            int maxThumbTop = Math.Max(1, trackHeight - thumbHeight);
            int thumbTop = 8 + (int)Math.Round(maxThumbTop * ((double)_scrollOffset / maxScroll));

            _scrollThumbRect = new Rectangle(this.Width - 8, thumbTop, 5, thumbHeight);
            Color thumbColor = (_draggingScrollThumb || _hoverScrollThumb) ? Theme.TextMuted : Theme.BorderHover;
            using (GraphicsPath p = Program.GetRoundedPath(_scrollThumbRect, 2))
            using (SolidBrush b = new SolidBrush(thumbColor))
            {
                g.FillPath(b, p);
            }
        }

        public static void DrawPill(Graphics g, Rectangle rect, Color fill, Color border, string text, Color fg, Font font, int radius)
        {
            using (GraphicsPath p = Program.GetRoundedPath(rect, radius))
            {
                if (fill.A > 0)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                }
                if (border.A > 0)
                {
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
                }
            }
            if (!string.IsNullOrEmpty(text))
            {
                Rectangle textRect = new Rectangle(rect.X + 4, rect.Y, Math.Max(1, rect.Width - 8), rect.Height);
                TextRenderer.DrawText(g, text, font, textRect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        public static void DrawPillWithIcon(Graphics g, Rectangle rect, Color fill, Color border, Image icon, string text, Color fg, Font font, int radius)
        {
            using (GraphicsPath p = Program.GetRoundedPath(rect, radius))
            {
                if (fill.A > 0)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                }
                if (border.A > 0)
                {
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
                }
            }
            int textX = rect.X + 6;
            if (icon != null)
            {
                int iy = rect.Y + (rect.Height - icon.Height) / 2;
                g.DrawImage(icon, new Rectangle(rect.X + 7, iy, icon.Width, icon.Height));
                textX = rect.X + 7 + icon.Width + 5;
            }
            if (!string.IsNullOrEmpty(text))
            {
                Rectangle textRect = new Rectangle(textX, rect.Y, Math.Max(1, rect.Right - textX - 5), rect.Height);
                TextRenderer.DrawText(g, text, font, textRect, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>
    /// Inline detail drawer/modal inside QuestsForm showing full schedule, regional allowlist, rewards, and actions.
    /// </summary>
    public class QuestDetailOverlay : Control
    {
        private QuestItem _quest;
        private bool _isDone;
        private bool _isSimulating;
        private string _effectiveCountry = "";

        private Rectangle _cardRect;
        private Rectangle _closeRect;
        private Rectangle _simRect;
        private Rectangle _queueRect;
        private Rectangle _openRect;
        private Rectangle _doneRect;
        private string _hoveredBtn = "";

        public event Action CloseRequested;
        public event Action<QuestItem> SimulateClicked;
        public event Action<QuestItem> StopSimulateClicked;
        public event Action<QuestItem> QueueClicked;
        public event Action<QuestItem> OpenDiscordClicked;
        public event Action<QuestItem> ToggleDoneClicked;

        public QuestDetailOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            this.Visible = false;
        }

        public QuestItem CurrentQuest
        {
            get { return _quest; }
        }

        public void ShowQuest(QuestItem quest, bool isDone, bool isSimulating, string effectiveCountry)
        {
            _quest = quest;
            _isDone = isDone;
            _isSimulating = isSimulating;
            _effectiveCountry = effectiveCountry ?? "";
            _hoveredBtn = "";
            this.Visible = quest != null;
            if (this.Visible)
            {
                this.BringToFront();
                this.Focus();
            }
            Invalidate();
        }

        public void UpdateProgressState(bool isDone, bool isSimulating)
        {
            _isDone = isDone;
            _isSimulating = isSimulating;
            Invalidate();
        }

        public void HideOverlay()
        {
            _quest = null;
            this.Visible = false;
            _hoveredBtn = "";
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                if (CloseRequested != null) CloseRequested();
                e.Handled = true;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (QuestsForm.TryForwardResizeHitTest(this, ref m)) return;
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string next = HitButton(e.Location);
            if (next != _hoveredBtn)
            {
                _hoveredBtn = next;
                this.Cursor = string.IsNullOrEmpty(next) ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_cardRect.Contains(e.Location))
            {
                if (CloseRequested != null) CloseRequested();
                return;
            }
            string hit = HitButton(e.Location);
            if (hit == "close") { if (CloseRequested != null) CloseRequested(); }
            else if (hit == "sim" && _quest != null)
            {
                if (_isSimulating) { if (StopSimulateClicked != null) StopSimulateClicked(_quest); }
                else { if (SimulateClicked != null) SimulateClicked(_quest); }
            }
            else if (hit == "queue" && _quest != null) { if (QueueClicked != null) QueueClicked(_quest); }
            else if (hit == "open" && _quest != null) { if (OpenDiscordClicked != null) OpenDiscordClicked(_quest); }
            else if (hit == "done" && _quest != null) { if (ToggleDoneClicked != null) ToggleDoneClicked(_quest); }
        }

        private string HitButton(Point pt)
        {
            if (!_closeRect.IsEmpty && _closeRect.Contains(pt)) return "close";
            if (!_simRect.IsEmpty && _simRect.Contains(pt)) return "sim";
            if (!_queueRect.IsEmpty && _queueRect.Contains(pt)) return "queue";
            if (!_openRect.IsEmpty && _openRect.Contains(pt)) return "open";
            if (!_doneRect.IsEmpty && _doneRect.Contains(pt)) return "done";
            return "";
        }

        private void OnThumbnailLoaded()
        {
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                try { this.BeginInvoke(new Action(Invalidate)); } catch { }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_quest == null) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (SolidBrush dim = new SolidBrush(Color.FromArgb(196, 6, 6, 10)))
            {
                g.FillRectangle(dim, this.ClientRectangle);
            }

            int w = Math.Min(600, Math.Max(420, this.Width - 64));
            int h = Math.Min(476, Math.Max(360, this.Height - 48));
            _cardRect = new Rectangle((this.Width - w) / 2, (this.Height - h) / 2, w, h);

            using (GraphicsPath cp = Program.GetRoundedPath(_cardRect, 14))
            {
                using (SolidBrush sb = new SolidBrush(Theme.BgSurface)) g.FillPath(sb, cp);
                using (Pen pen = new Pen(Theme.BorderHover, 1f)) g.DrawPath(pen, cp);
            }

            _closeRect = new Rectangle(_cardRect.Right - 36, _cardRect.Y + 14, 24, 24);
            Color closeCol = _hoveredBtn == "close" ? Color.White : Theme.TextMuted;
            using (Pen xp = new Pen(closeCol, 1.8f))
            {
                g.DrawLine(xp, _closeRect.X + 6, _closeRect.Y + 6, _closeRect.Right - 6, _closeRect.Bottom - 6);
                g.DrawLine(xp, _closeRect.Right - 6, _closeRect.Y + 6, _closeRect.X + 6, _closeRect.Bottom - 6);
            }

            // Header artwork + title
            Rectangle thumbRect = new Rectangle(_cardRect.X + 20, _cardRect.Y + 18, 64, 64);
            string asset = _quest.GetThumbnailAssetPath();
            Image img = asset != null ? QuestAssetCache.Instance.GetThumbnail(asset, 64, 12, OnThumbnailLoaded) : null;
            if (img != null) g.DrawImage(img, thumbRect);
            else
            {
                using (GraphicsPath tp = Program.GetRoundedPath(thumbRect, 12))
                using (SolidBrush tb = new SolidBrush(Theme.BgInput)) g.FillPath(tb, tp);
            }

            int hx = thumbRect.Right + 14;
            int hw = _closeRect.X - hx - 8;
            TextRenderer.DrawText(g, _quest.DisplayTitle, FontManager.ButtonFont,
                new Rectangle(hx, _cardRect.Y + 18, hw, 26), Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            string pub = !string.IsNullOrWhiteSpace(_quest.game_publisher) ? _quest.game_publisher : _quest.DisplayGameName;
            TextRenderer.DrawText(g, pub, FontManager.SmallFont,
                new Rectangle(hx, _cardRect.Y + 44, hw, 18), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            string dateLine = FormatDateRange(_quest);
            TextRenderer.DrawText(g, dateLine + "  \u00B7  " + _quest.GetCountdownLabel(nowUtc), FontManager.ListExeFont,
                new Rectangle(hx, _cardRect.Y + 64, hw, 18), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Region eligibility box
            int y = thumbRect.Bottom + 14;
            bool eligible = string.IsNullOrEmpty(_effectiveCountry) || _quest.IsEligibleInRegion(_effectiveCountry);
            string ccDisplay = string.IsNullOrEmpty(_effectiveCountry) ? "Global" : _effectiveCountry;
            string regHeadline = (_quest.region == null || _quest.region.is_global)
                ? "Available Worldwide"
                : (eligible ? "Your Region (" + ccDisplay + ") Is Eligible" : "Your Region (" + ccDisplay + ") Is Restricted");
            string regDetail = BuildFullRegionDetail(_quest.region);

            Rectangle regBox = new Rectangle(_cardRect.X + 20, y, _cardRect.Width - 40, 48);
            Color regBg = eligible ? Color.FromArgb(20, 40, 32) : Color.FromArgb(46, 26, 22);
            Color regBorder = eligible ? Color.FromArgb(40, 96, 68) : Color.FromArgb(110, 56, 44);
            Color regFg = eligible ? Theme.StatusGreen : Color.FromArgb(245, 160, 120);
            using (GraphicsPath rp = Program.GetRoundedPath(regBox, 8))
            {
                using (SolidBrush rb = new SolidBrush(regBg)) g.FillPath(rb, rp);
                using (Pen rpen = new Pen(regBorder, 1f)) g.DrawPath(rpen, rp);
            }
            TextRenderer.DrawText(g, regHeadline, FontManager.BadgeFont,
                new Rectangle(regBox.X + 12, regBox.Y + 6, regBox.Width - 24, 18), regFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, regDetail, FontManager.ListExeFont,
                new Rectangle(regBox.X + 12, regBox.Y + 25, regBox.Width - 24, 18), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            y = regBox.Bottom + 14;

            // Tasks section
            TextRenderer.DrawText(g, "Tasks & Requirements", FontManager.BadgeFont,
                new Rectangle(_cardRect.X + 20, y, _cardRect.Width - 40, 18), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);
            y += 22;
            if (_quest.tasks != null && _quest.tasks.Count > 0)
            {
                for (int i = 0; i < Math.Min(3, _quest.tasks.Count); i++)
                {
                    QuestTaskInfo task = _quest.tasks[i];
                    if (task == null) continue;
                    string tLabel = "\u2022 " + task.GetDisplayLabel();
                    if (task.IsDesktopPlay) tLabel += "  (Simulatable in FakeGameLauncher)";
                    TextRenderer.DrawText(g, tLabel, FontManager.SmallFont,
                        new Rectangle(_cardRect.X + 22, y, _cardRect.Width - 44, 20),
                        task.IsDesktopPlay ? Theme.StatusGreen : Theme.TextPrimary,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    y += 22;
                }
            }
            else
            {
                TextRenderer.DrawText(g, "\u2022 See quest details on Discord", FontManager.SmallFont,
                    new Rectangle(_cardRect.X + 22, y, _cardRect.Width - 44, 20), Theme.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.NoPadding);
                y += 22;
            }

            y += 8;
            TextRenderer.DrawText(g, "Quest Rewards", FontManager.BadgeFont,
                new Rectangle(_cardRect.X + 20, y, _cardRect.Width - 40, 18), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);
            y += 22;
            string instrPreview = "";
            if (_quest.rewards != null && _quest.rewards.Count > 0)
            {
                for (int i = 0; i < Math.Min(2, _quest.rewards.Count); i++)
                {
                    QuestRewardInfo r = _quest.rewards[i];
                    if (r == null) continue;
                    string rText = "\u2022 " + (string.IsNullOrWhiteSpace(r.name) ? r.GetTypeLabel() : r.name) + " (" + r.GetTypeLabel() + ")";
                    TextRenderer.DrawText(g, rText, FontManager.SmallFont,
                        new Rectangle(_cardRect.X + 22, y, _cardRect.Width - 44, 20), Theme.TextPrimary,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    y += 22;
                    if (string.IsNullOrEmpty(instrPreview) && !string.IsNullOrWhiteSpace(r.redemption_instructions))
                        instrPreview = r.redemption_instructions.Replace("\r", " ").Replace("\n", " ");
                }
            }

            if (!string.IsNullOrEmpty(instrPreview))
            {
                int availH = Math.Max(24, (_cardRect.Bottom - 78) - y);
                TextRenderer.DrawText(g, instrPreview, FontManager.ListExeFont,
                    new Rectangle(_cardRect.X + 22, y + 2, _cardRect.Width - 44, availH), Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }

            // Disclaimer + bottom buttons
            TextRenderer.DrawText(g, "Local checklist only \u2014 quest completion is tracked by Discord.", FontManager.ListExeFont,
                new Rectangle(_cardRect.X + 20, _cardRect.Bottom - 68, _cardRect.Width - 40, 18), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int btnY = _cardRect.Bottom - 44;
            int bx = _cardRect.X + 20;
            _simRect = Rectangle.Empty;
            _queueRect = Rectangle.Empty;

            if (_quest.HasDesktopTask)
            {
                _simRect = new Rectangle(bx, btnY, 146, 30);
                Color simBg = _isSimulating
                    ? (_hoveredBtn == "sim" ? Color.FromArgb(235, 78, 98) : Color.FromArgb(215, 58, 78))
                    : (_hoveredBtn == "sim" ? Theme.AccentBlue : Theme.AccentViolet);
                QuestGridControl.DrawPill(g, _simRect, simBg, Color.Transparent,
                    _isSimulating ? "Stop Simulation" : "Simulate in Launcher", Color.White, FontManager.BadgeFont, 8);
                bx += 154;

                _queueRect = new Rectangle(bx, btnY, 108, 30);
                QuestGridControl.DrawPill(g, _queueRect,
                    _hoveredBtn == "queue" ? Theme.BgCardHover : Theme.BgInput, Theme.BorderHover,
                    "+ Add to Queue", Theme.TextPrimary, FontManager.BadgeFont, 8);
                bx += 116;
            }

            _doneRect = new Rectangle(_cardRect.Right - 20 - 118, btnY, 118, 30);
            QuestGridControl.DrawPill(g, _doneRect,
                _isDone ? Color.FromArgb(24, 52, 38) : (_hoveredBtn == "done" ? Theme.BgCardHover : Theme.BgInput),
                _isDone ? Color.FromArgb(46, 120, 82) : Theme.BorderHover,
                _isDone ? "Completed \u2713" : "Mark Completed",
                _isDone ? Theme.StatusGreen : Theme.TextPrimary, FontManager.BadgeFont, 8);

            int openW = Math.Min(138, _doneRect.X - bx - 8);
            _openRect = openW >= 80 ? new Rectangle(bx, btnY, openW, 30) : Rectangle.Empty;
            if (!_openRect.IsEmpty)
            {
                QuestGridControl.DrawPill(g, _openRect,
                    _hoveredBtn == "open" ? Theme.BgCardHover : Theme.BgInput, Theme.BorderHover,
                    "Open in Discord", Theme.TextPrimary, FontManager.BadgeFont, 8);
            }
        }

        private static string FormatDateRange(QuestItem q)
        {
            string s = q.StartsAtUtc == DateTimeOffset.MinValue ? "?" : q.StartsAtUtc.ToLocalTime().ToString("MMM d");
            string e = q.ExpiresAtUtc == DateTimeOffset.MinValue ? "?" : q.ExpiresAtUtc.ToLocalTime().ToString("MMM d, HH:mm");
            return "Starts " + s + " \u2022 Expires " + e;
        }

        private static string BuildFullRegionDetail(QuestRegionInfo region)
        {
            if (region == null) return "Region data unavailable (treated as global).";
            if (region.is_global)
            {
                if (region.exclude != null && region.exclude.Count > 0)
                    return "Global except: " + string.Join(", ", region.exclude.ToArray());
                return "Available in all supported Discord regions.";
            }
            if (region.include != null && region.include.Count > 0)
            {
                int cap = Math.Min(16, region.include.Count);
                string[] slice = new string[cap];
                for (int i = 0; i < cap; i++) slice[i] = region.include[i];
                string list = string.Join(", ", slice);
                if (region.include.Count > cap) list += " +" + (region.include.Count - cap) + " more";
                return "Allowed: " + list;
            }
            return "Global";
        }
    }

    /// <summary>
    /// Reusable Discord Quests explorer panel hosted inside MainForm (unified docked mode) or QuestsForm.
    /// </summary>
    public class QuestsPanel : UserControl
    {
        private class PillRegion
        {
            public string Key;
            public string Label;
            public Rectangle Rect;
        }

        private const int TitleBarHeight = 44;
        private const int CategoryBarHeight = 38;
        private const int FilterBarHeight = 42;

        private string _activeCategory = "active";
        private string _sortMode = "ending_soon";
        private bool _filterRegionOnly = false;
        private bool _hideCompleted = false;
        private string _regionOverride = "";
        private readonly int[] _catCounts = new int[8];

        private readonly List<PillRegion> _categoryPills = new List<PillRegion>();
        private Rectangle _refreshRect;
        private Rectangle _minRect;
        private Rectangle _maxRect;
        private Rectangle _closeRect;
        private Rectangle _hideDoneRect;
        private Rectangle _regionToggleRect;
        private Rectangle _regionMenuRect;
        private Rectangle _sortRect;
        private Rectangle _clearSearchRect;

        private string _hoveredElement = "";
        private string _pressedElement = "";

        private Panel _pnlSearch;
        private TextBox _txtSearch;
        private System.Windows.Forms.Timer _debounceSearch;
        private QuestGridControl _grid;
        private QuestDetailOverlay _detailOverlay;
        private ContextMenuStrip _regionMenu;
        private ContextMenuStrip _sortMenu;

        public bool ShowOuterBorder { get; set; }

        public event Action<GameItem> SimulateRequested;
        public event Action<GameItem> StopSimulateRequested;
        public event Action<GameItem> QueueRequested;
        public event Action CloseRequested;
        public event Action MinimizeRequested;
        public event Action MaximizeToggleRequested;
        public event Action DragRequested;

        public QuestsPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.BackColor = Theme.BgBase;
            this.ForeColor = Theme.TextPrimary;

            QuestDatabase.Instance.EnsureStateLoaded();
            _filterRegionOnly = QuestDatabase.Instance.FilterRegionOnly;
            _hideCompleted = QuestDatabase.Instance.HideCompleted;
            _regionOverride = QuestDatabase.Instance.RegionOverride ?? "";

            BuildChildControls();
            QuestDatabase.Instance.OnQuestsUpdated += HandleDatabaseChanged;
            QuestDatabase.Instance.OnStateChanged += HandleDatabaseChanged;
            QuestDatabase.Instance.OnProgressUpdated += HandleProgressUpdated;
            RefreshView(true);
        }

        private void BuildChildControls()
        {
            _debounceSearch = new System.Windows.Forms.Timer();
            _debounceSearch.Interval = 120;
            _debounceSearch.Tick += (s, e) =>
            {
                _debounceSearch.Stop();
                RefreshView(true);
            };

            _pnlSearch = new Panel();
            _pnlSearch.BackColor = Theme.BgInput;
            _pnlSearch.Location = new Point(18, TitleBarHeight + CategoryBarHeight + 5);
            _pnlSearch.Size = new Size(280, 32);

            _txtSearch = new TextBox();
            _txtSearch.BorderStyle = BorderStyle.None;
            _txtSearch.BackColor = Theme.BgInput;
            _txtSearch.ForeColor = Theme.TextPrimary;
            _txtSearch.Font = FontManager.SmallFont;
            _txtSearch.Location = new Point(10, 8);
            _txtSearch.Size = new Size(242, 18);
            _txtSearch.TextChanged += (s, e) =>
            {
                _pnlSearch.Invalidate();
                _debounceSearch.Stop();
                _debounceSearch.Start();
            };
            _pnlSearch.Controls.Add(_txtSearch);
            _pnlSearch.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle br = new Rectangle(0, 0, _pnlSearch.Width - 1, _pnlSearch.Height - 1);
                using (GraphicsPath bp = Program.GetRoundedPath(br, 8))
                using (Pen pen = new Pen(_txtSearch.Focused ? Theme.AccentFocus : Theme.BorderSubtle, 1f))
                {
                    pe.Graphics.DrawPath(pen, bp);
                }
                if (string.IsNullOrEmpty(_txtSearch.Text))
                {
                    TextRenderer.DrawText(pe.Graphics, "Search quests, games, or rewards...", FontManager.SmallFont,
                        new Point(10, 7), Theme.TextMuted);
                }
            };
            _txtSearch.GotFocus += (s, e) => _pnlSearch.Invalidate();
            _txtSearch.LostFocus += (s, e) => _pnlSearch.Invalidate();
            this.Controls.Add(_pnlSearch);

            _grid = new QuestGridControl();
            _grid.QuestClicked += q =>
            {
                if (q != null)
                {
                    _detailOverlay.ShowQuest(
                        q,
                        QuestDatabase.Instance.IsCompleted(q.id),
                        QuestDatabase.Instance.IsQuestSimulating(q.id),
                        QuestDatabase.Instance.EffectiveCountry);
                }
            };
            _grid.SimulateClicked += HandleSimulateQuest;
            _grid.StopSimulateClicked += HandleStopSimulateQuest;
            _grid.QueueClicked += HandleQueueQuest;
            _grid.OpenDiscordClicked += HandleOpenQuestUrl;
            _grid.ToggleDoneClicked += q =>
            {
                if (q != null && !string.IsNullOrEmpty(q.id)) QuestDatabase.Instance.ToggleCompleted(q.id);
            };
            _grid.ResetFiltersClicked += ResetAllFilters;
            this.Controls.Add(_grid);

            _detailOverlay = new QuestDetailOverlay();
            _detailOverlay.Dock = DockStyle.Fill;
            _detailOverlay.CloseRequested += () => _detailOverlay.HideOverlay();
            _detailOverlay.SimulateClicked += HandleSimulateQuest;
            _detailOverlay.StopSimulateClicked += HandleStopSimulateQuest;
            _detailOverlay.QueueClicked += HandleQueueQuest;
            _detailOverlay.OpenDiscordClicked += HandleOpenQuestUrl;
            _detailOverlay.ToggleDoneClicked += q =>
            {
                if (q != null && !string.IsNullOrEmpty(q.id))
                {
                    bool done = QuestDatabase.Instance.ToggleCompleted(q.id);
                    _detailOverlay.UpdateProgressState(
                        done,
                        QuestDatabase.Instance.IsQuestSimulating(q.id));
                }
            };
            this.Controls.Add(_detailOverlay);

            _regionMenu = new ContextMenuStrip();
            _regionMenu.Renderer = new DarkMenuRenderer();
            _regionMenu.ShowImageMargin = true;
            _regionMenu.ImageScalingSize = new Size(18, 12);
            BuildRegionMenu();

            _sortMenu = new ContextMenuStrip();
            _sortMenu.Renderer = new DarkMenuRenderer();
            _sortMenu.ShowImageMargin = false;
            BuildSortMenu();

            LayoutChildren();
        }

        private void BuildRegionMenu()
        {
            _regionMenu.Items.Clear();

            ToolStripMenuItem allItem = new ToolStripMenuItem("All Regions", FlagIcons.GetGlobeIcon());
            allItem.Click += (s, e) => SelectAllRegions();
            _regionMenu.Items.Add(allItem);

            string detected = QuestDatabase.Instance.DetectedCountry;
            string autoLabel = string.IsNullOrEmpty(detected) ? "Auto (Detect)" : ("Auto (" + detected + ")");
            Image autoImg = string.IsNullOrEmpty(detected) ? (Image)FlagIcons.GetGlobeIcon() : (Image)FlagIcons.GetFlag(detected);
            ToolStripMenuItem autoItem = new ToolStripMenuItem(autoLabel, autoImg);
            autoItem.Click += (s, e) => SelectAutoRegion();
            _regionMenu.Items.Add(autoItem);
            _regionMenu.Items.Add(new ToolStripSeparator());

            string[] codes = new string[] { "US", "UK", "DE", "CA", "TR", "FR", "JP", "BR", "AU", "ES", "PL", "KR" };
            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                string label = code + "  " + FlagIcons.GetCountryDisplayName(code);
                ToolStripMenuItem item = new ToolStripMenuItem(label, FlagIcons.GetFlag(code));
                item.Click += (s, e) => SelectRegionOverride(code);
                _regionMenu.Items.Add(item);
            }
        }

        private void BuildSortMenu()
        {
            _sortMenu.Items.Clear();
            AddSortOption("Ending Soon", "ending_soon");
            AddSortOption("Newest", "newest");
            AddSortOption("Quest Name", "name");
        }

        private void AddSortOption(string label, string key)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Click += (s, e) =>
            {
                _sortMode = key;
                RefreshView(true);
                Invalidate();
            };
            _sortMenu.Items.Add(item);
        }

        public void SelectAllRegions()
        {
            _regionOverride = "";
            _filterRegionOnly = false;
            QuestDatabase.Instance.SetFilterPreferences(false, _hideCompleted, "");
            RefreshView(true);
            Invalidate();
        }

        public void SelectAutoRegion()
        {
            _regionOverride = "";
            _filterRegionOnly = !string.IsNullOrEmpty(QuestDatabase.Instance.DetectedCountry);
            QuestDatabase.Instance.SetFilterPreferences(_filterRegionOnly, _hideCompleted, "");
            RefreshView(true);
            Invalidate();
        }

        public void SelectRegionOverride(string code)
        {
            _regionOverride = QuestDatabase.NormalizeCountryCode(code);
            _filterRegionOnly = !string.IsNullOrEmpty(_regionOverride);
            QuestDatabase.Instance.SetFilterPreferences(_filterRegionOnly, _hideCompleted, _regionOverride);
            RefreshView(true);
            Invalidate();
        }

        public void HandleSimulateQuest(QuestItem quest)
        {
            if (quest == null || !quest.HasDesktopTask) return;
            GameItem game = QuestDatabase.Instance.ResolveGameForQuest(quest);
            if (game != null && SimulateRequested != null) SimulateRequested(game);
        }

        public void HandleStopSimulateQuest(QuestItem quest)
        {
            if (quest == null || !quest.HasDesktopTask) return;
            GameItem game = QuestDatabase.Instance.ResolveGameForQuest(quest);
            if (game != null && StopSimulateRequested != null) StopSimulateRequested(game);
        }

        public void HandleQueueQuest(QuestItem quest)
        {
            if (quest == null || !quest.HasDesktopTask) return;
            GameItem game = QuestDatabase.Instance.ResolveGameForQuest(quest);
            if (game != null && QueueRequested != null) QueueRequested(game);
        }

        private static void HandleOpenQuestUrl(QuestItem quest)
        {
            if (quest == null) return;
            try { Process.Start(quest.GetDiscordQuestUrl()); }
            catch (Exception ex) { Program.LogError("OpenQuestUrl", ex); }
        }

        public void ResetAllFilters()
        {
            _activeCategory = "active";
            _txtSearch.Text = "";
            _filterRegionOnly = false;
            _hideCompleted = false;
            QuestDatabase.Instance.SetFilterPreferences(false, false, _regionOverride);
            RefreshView(true);
            Invalidate();
        }

        public void SetFiltersForTesting(string category, string query, bool regionOnly, bool hideCompleted, string sortMode)
        {
            if (!string.IsNullOrEmpty(category)) _activeCategory = category;
            if (query != null) _txtSearch.Text = query;
            _filterRegionOnly = regionOnly;
            _hideCompleted = hideCompleted;
            if (!string.IsNullOrEmpty(sortMode)) _sortMode = sortMode;
            RefreshView(true);
        }

        public void OpenDetailForTesting(QuestItem quest)
        {
            string qid = quest != null ? quest.id : "";
            _detailOverlay.ShowQuest(
                quest,
                QuestDatabase.Instance.IsCompleted(qid),
                QuestDatabase.Instance.IsQuestSimulating(qid),
                QuestDatabase.Instance.EffectiveCountry);
        }

        public bool HandleEscape()
        {
            if (_detailOverlay != null && _detailOverlay.Visible)
            {
                _detailOverlay.HideOverlay();
                return true;
            }
            if (_txtSearch != null && !string.IsNullOrEmpty(_txtSearch.Text))
            {
                _txtSearch.Text = "";
                return true;
            }
            return false;
        }

        private void HandleDatabaseChanged()
        {
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                try { this.BeginInvoke(new Action(() => RefreshView(false))); } catch { }
            }
        }

        private void HandleProgressUpdated()
        {
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        HashSet<string> sim = QuestDatabase.Instance.GetSimulatingQuestIdsSnapshot();
                        if (_grid != null) _grid.UpdateSimulatingIds(sim);
                        if (_detailOverlay != null && _detailOverlay.Visible && _detailOverlay.CurrentQuest != null)
                        {
                            string qid = _detailOverlay.CurrentQuest.id ?? "";
                            _detailOverlay.UpdateProgressState(QuestDatabase.Instance.IsCompleted(qid), sim.Contains(qid));
                        }
                    }));
                }
                catch { }
            }
        }

        public void RefreshView(bool resetScroll)
        {
            List<QuestItem> all = QuestDatabase.Instance.GetQuestsSnapshot();
            HashSet<string> done = QuestDatabase.Instance.GetCompletedIdsSnapshot();
            HashSet<string> sim = QuestDatabase.Instance.GetSimulatingQuestIdsSnapshot();
            string effectiveCc = QuestDatabase.Instance.EffectiveCountry;
            string q = _txtSearch != null ? _txtSearch.Text : "";
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

            string[] keys = new string[] { "active", "desktop", "ending", "orbs", "collectibles", "expired", "done", "all" };
            for (int i = 0; i < keys.Length; i++)
            {
                _catCounts[i] = QuestDatabase.FilterQuests(
                    all, keys[i], "", _filterRegionOnly, effectiveCc, _hideCompleted, done, _sortMode, nowUtc).Count;
            }

            HashSet<string> alsoGlobalIds;
            List<QuestItem> filtered = QuestDatabase.FilterQuests(
                all, _activeCategory, q, _filterRegionOnly, effectiveCc, _hideCompleted, done, _sortMode, nowUtc, out alsoGlobalIds);

            if (_grid != null)
                _grid.SetData(filtered, done, sim, alsoGlobalIds, effectiveCc, QuestDatabase.Instance.IsUpdating, resetScroll);

            if (_detailOverlay != null && _detailOverlay.Visible && _detailOverlay.CurrentQuest != null)
            {
                string qid = _detailOverlay.CurrentQuest.id ?? "";
                _detailOverlay.UpdateProgressState(done.Contains(qid), sim.Contains(qid));
            }

            Invalidate(new Rectangle(0, 0, this.Width, TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (QuestsForm.TryForwardResizeHitTest(this, ref m)) return;
            base.WndProc(ref m);
        }

        private void LayoutChildren()
        {
            int topOffset = TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4;
            if (_grid != null)
            {
                _grid.Location = new Point(0, topOffset);
                _grid.Size = new Size(this.ClientSize.Width, Math.Max(100, this.ClientSize.Height - topOffset));
            }
            if (_pnlSearch != null)
            {
                int searchW = Math.Max(160, Math.Min(340, this.ClientSize.Width - 488));
                _pnlSearch.Location = new Point(18, TitleBarHeight + CategoryBarHeight + 5);
                _pnlSearch.Size = new Size(searchW, 32);
                if (_txtSearch != null) _txtSearch.Size = new Size(Math.Max(80, searchW - 24), 18);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string next = HitChromeElement(e.Location);
            if (next != _hoveredElement)
            {
                _hoveredElement = next;
                this.Cursor = string.IsNullOrEmpty(next) ? Cursors.Default : Cursors.Hand;
                Invalidate(new Rectangle(0, 0, this.Width, TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!string.IsNullOrEmpty(_hoveredElement))
            {
                _hoveredElement = "";
                this.Cursor = Cursors.Default;
                Invalidate(new Rectangle(0, 0, this.Width, TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4));
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && e.Y <= TitleBarHeight && string.IsNullOrEmpty(HitChromeElement(e.Location)))
            {
                if (MaximizeToggleRequested != null) MaximizeToggleRequested();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            string hit = HitChromeElement(e.Location);
            if (string.IsNullOrEmpty(hit))
            {
                if (e.Y <= TitleBarHeight + CategoryBarHeight && DragRequested != null) DragRequested();
                return;
            }
            _pressedElement = hit;
            Invalidate(new Rectangle(0, 0, this.Width, TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string pressed = _pressedElement;
            _pressedElement = "";
            if (e.Button != MouseButtons.Left || string.IsNullOrEmpty(pressed)) return;
            string hit = HitChromeElement(e.Location);
            if (hit != pressed)
            {
                Invalidate(new Rectangle(0, 0, this.Width, TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4));
                return;
            }

            if (hit == "close") { if (CloseRequested != null) CloseRequested(); return; }
            else if (hit == "min") { if (MinimizeRequested != null) MinimizeRequested(); return; }
            else if (hit == "max") { if (MaximizeToggleRequested != null) MaximizeToggleRequested(); }
            else if (hit == "refresh") QuestDatabase.Instance.RefreshAsync(true);
            else if (hit == "hide_done")
            {
                _hideCompleted = !_hideCompleted;
                QuestDatabase.Instance.SetFilterPreferences(_filterRegionOnly, _hideCompleted, _regionOverride);
                RefreshView(true);
            }
            else if (hit == "region_toggle")
            {
                _filterRegionOnly = !_filterRegionOnly;
                QuestDatabase.Instance.SetFilterPreferences(_filterRegionOnly, _hideCompleted, _regionOverride);
                RefreshView(true);
            }
            else if (hit == "region_menu")
            {
                BuildRegionMenu();
                _regionMenu.Show(this, new Point(_regionMenuRect.X, _regionMenuRect.Bottom + 4));
            }
            else if (hit == "sort")
            {
                _sortMenu.Show(this, new Point(_sortRect.X, _sortRect.Bottom + 4));
            }
            else if (hit == "clear_search")
            {
                _txtSearch.Text = "";
            }
            else if (hit.StartsWith("cat:", StringComparison.Ordinal))
            {
                _activeCategory = hit.Substring(4);
                RefreshView(true);
            }
            Invalidate(new Rectangle(0, 0, this.Width, TitleBarHeight + CategoryBarHeight + FilterBarHeight + 4));
        }

        private string HitChromeElement(Point pt)
        {
            if (_closeRect.Contains(pt)) return "close";
            if (_maxRect.Contains(pt)) return "max";
            if (_minRect.Contains(pt)) return "min";
            if (_refreshRect.Contains(pt)) return "refresh";
            if (_hideDoneRect.Contains(pt)) return "hide_done";
            if (_regionToggleRect.Contains(pt)) return "region_toggle";
            if (_regionMenuRect.Contains(pt)) return "region_menu";
            if (_sortRect.Contains(pt)) return "sort";
            if (!_clearSearchRect.IsEmpty && _clearSearchRect.Contains(pt)) return "clear_search";
            for (int i = 0; i < _categoryPills.Count; i++)
            {
                if (_categoryPills[i].Rect.Contains(pt)) return "cat:" + _categoryPills[i].Key;
            }
            return "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Titlebar
            TextRenderer.DrawText(g, "Discord Quests", FontManager.ButtonFont,
                new Rectangle(18, 10, 150, 24), Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int totalCount = QuestDatabase.Instance.Count;
            string statusText = QuestDatabase.Instance.IsUpdating
                ? "Syncing quests..."
                : (totalCount == 0 ? "Loading catalog..." : _catCounts[0] + " active \u00B7 " + FormatUpdatedAgo(QuestDatabase.Instance.LastUpdatedUtc));

            TextRenderer.DrawText(g, statusText, FontManager.ListExeFont,
                new Rectangle(162, 12, Math.Max(80, this.ClientSize.Width - 320), 22), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            Form parentForm = this.FindForm();
            bool isMax = parentForm != null && parentForm.WindowState == FormWindowState.Maximized;

            int rightX = this.ClientSize.Width - 16;
            _closeRect = new Rectangle(rightX - 28, 8, 28, 28);
            _maxRect = new Rectangle(_closeRect.X - 32, 8, 28, 28);
            _minRect = new Rectangle(_maxRect.X - 32, 8, 28, 28);
            _refreshRect = new Rectangle(_minRect.X - 36, 8, 28, 28);

            DrawTitleIconBtn(g, _refreshRect, "refresh", "\u21BB", Theme.TextPrimary);
            DrawTitleIconBtn(g, _minRect, "min", "\u2013", Theme.TextPrimary);
            DrawTitleIconBtn(g, _maxRect, "max", isMax ? "\u2752" : "\u25A1", Theme.TextPrimary);
            DrawTitleIconBtn(g, _closeRect, "close", "\u2715", Color.FromArgb(245, 110, 115));

            // Category pills
            DrawCategoryStrip(g);

            // Filter toolbar right side (next to search box)
            int filterY = TitleBarHeight + CategoryBarHeight + 5;
            int fx = this.ClientSize.Width - 18;

            string sortLabel = "Sort: " + (_sortMode == "name" ? "Name" : (_sortMode == "newest" ? "Newest" : "Ending Soon"));
            int sortW = Math.Max(108, TextRenderer.MeasureText(sortLabel, FontManager.BadgeFont).Width + 20);
            _sortRect = new Rectangle(fx - sortW, filterY, sortW, 32);
            DrawToolbarChip(g, _sortRect, "sort", sortLabel, false);
            fx = _sortRect.X - 8;

            _regionMenuRect = new Rectangle(fx - 26, filterY, 26, 32);
            DrawToolbarChip(g, _regionMenuRect, "region_menu", "\u25BE", false);
            fx = _regionMenuRect.X - 2;

            string cc = QuestDatabase.Instance.EffectiveCountry;
            string regLabel = string.IsNullOrEmpty(cc) ? "All Regions" : ("Available in " + cc);
            Image regFlag = string.IsNullOrEmpty(cc) ? (Image)FlagIcons.GetGlobeIcon() : (Image)FlagIcons.GetFlag(cc);
            int regW = Math.Max(114, TextRenderer.MeasureText(regLabel, FontManager.BadgeFont).Width + 40);
            _regionToggleRect = new Rectangle(fx - regW, filterY, regW, 32);
            DrawToolbarChipWithIcon(g, _regionToggleRect, "region_toggle", regFlag, regLabel, _filterRegionOnly);
            fx = _regionToggleRect.X - 8;

            _hideDoneRect = new Rectangle(fx - 92, filterY, 92, 32);
            DrawToolbarChip(g, _hideDoneRect, "hide_done", "Hide Done", _hideCompleted);

            if (_txtSearch != null && !string.IsNullOrEmpty(_txtSearch.Text))
            {
                _clearSearchRect = new Rectangle(_pnlSearch.Right + 6, filterY + 4, 24, 24);
                DrawTitleIconBtn(g, _clearSearchRect, "clear_search", "\u2715", Theme.TextMuted);
            }
            else
            {
                _clearSearchRect = Rectangle.Empty;
            }

            if (ShowOuterBorder && !isMax)
            {
                using (GraphicsPath borderPath = Program.GetRoundedPath(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 16))
                using (Pen borderPen = new Pen(Theme.BorderSubtle, 1f))
                {
                    g.DrawPath(borderPen, borderPath);
                }
            }
        }

        private void DrawTitleIconBtn(Graphics g, Rectangle rect, string key, string glyph, Color hoverColor)
        {
            bool hov = _hoveredElement == key;
            bool prs = _pressedElement == key;
            if (hov || prs)
            {
                Color bg = prs ? Theme.BorderHover : Theme.BgCardHover;
                using (GraphicsPath p = Program.GetRoundedPath(rect, 7))
                using (SolidBrush b = new SolidBrush(bg))
                {
                    g.FillPath(b, p);
                }
            }
            Color fg = hov ? hoverColor : Theme.TextMuted;
            TextRenderer.DrawText(g, glyph, FontManager.SmallFont, rect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawToolbarChip(Graphics g, Rectangle rect, string key, string label, bool active)
        {
            bool hov = _hoveredElement == key;
            bool prs = _pressedElement == key;
            Color fill = active ? Color.FromArgb(46, 36, 92) : (prs ? Theme.BorderHover : (hov ? Theme.BgCardHover : Theme.BgSurface));
            Color border = active ? Theme.AccentViolet : (hov ? Theme.BorderHover : Theme.BorderSubtle);
            Color fg = active ? Color.White : Theme.TextPrimary;
            QuestGridControl.DrawPill(g, rect, fill, border, label, fg, FontManager.BadgeFont, 8);
        }

        private void DrawToolbarChipWithIcon(Graphics g, Rectangle rect, string key, Image icon, string label, bool active)
        {
            bool hov = _hoveredElement == key;
            bool prs = _pressedElement == key;
            Color fill = active ? Color.FromArgb(46, 36, 92) : (prs ? Theme.BorderHover : (hov ? Theme.BgCardHover : Theme.BgSurface));
            Color border = active ? Theme.AccentViolet : (hov ? Theme.BorderHover : Theme.BorderSubtle);
            Color fg = active ? Color.White : Theme.TextPrimary;
            QuestGridControl.DrawPillWithIcon(g, rect, fill, border, icon, label, fg, FontManager.BadgeFont, 8);
        }

        private void DrawCategoryStrip(Graphics g)
        {
            string[] keys = new string[] { "active", "desktop", "ending", "orbs", "collectibles", "expired", "done", "all" };
            string[] prefixes = new string[] { "Active ", "Desktop ", "Ending ", "Orbs ", "Collectibles ", "Expired ", "Done ", "All " };

            _categoryPills.Clear();
            int x = 18;
            int y = TitleBarHeight + 4;
            for (int i = 0; i < keys.Length; i++)
            {
                string label = prefixes[i] + _catCounts[i];
                int w = Math.Max(56, TextRenderer.MeasureText(label, FontManager.BadgeFont).Width + 18);
                if (x + w > this.ClientSize.Width - 18) break;
                Rectangle rect = new Rectangle(x, y, w, 28);
                bool selected = string.Equals(_activeCategory, keys[i], StringComparison.OrdinalIgnoreCase);
                bool hovered = _hoveredElement == ("cat:" + keys[i]);
                Color fill = selected ? Theme.AccentViolet : (hovered ? Theme.BgCardHover : Theme.BgSurface);
                Color border = selected ? Theme.AccentViolet : (hovered ? Theme.BorderHover : Theme.BorderSubtle);
                Color fg = selected ? Color.White : (keys[i] == "desktop" ? Theme.StatusGreen : Theme.TextMuted);
                QuestGridControl.DrawPill(g, rect, fill, border, label, fg, FontManager.BadgeFont, 8);
                _categoryPills.Add(new PillRegion { Key = keys[i], Label = label, Rect = rect });
                x += w + 6;
            }
        }

        private static string FormatUpdatedAgo(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "not synced";
            TimeSpan span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return ((int)Math.Floor(span.TotalMinutes)) + "m ago";
            if (span.TotalDays < 1) return ((int)Math.Floor(span.TotalHours)) + "h ago";
            return ((int)Math.Floor(span.TotalDays)) + "d ago";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                QuestDatabase.Instance.OnQuestsUpdated -= HandleDatabaseChanged;
                QuestDatabase.Instance.OnStateChanged -= HandleDatabaseChanged;
                QuestDatabase.Instance.OnProgressUpdated -= HandleProgressUpdated;
                if (_debounceSearch != null) { _debounceSearch.Stop(); _debounceSearch.Dispose(); _debounceSearch = null; }
                if (_regionMenu != null) { _regionMenu.Dispose(); _regionMenu = null; }
                if (_sortMenu != null) { _sortMenu.Dispose(); _sortMenu = null; }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Standalone frameless window wrapper hosting a QuestsPanel.
    /// </summary>
    public class QuestsForm : Form
    {
        public const int ResizeGrip = 8;

        private readonly QuestsPanel _panel;

        public event Action<GameItem> SimulateRequested;
        public event Action<GameItem> StopSimulateRequested;
        public event Action<GameItem> QueueRequested;

        public QuestsForm()
        {
            this.ClientSize = new Size(920, 640);
            this.MinimumSize = new Size(720, 520);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.BgBase;
            this.ForeColor = Theme.TextPrimary;
            this.DoubleBuffered = true;
            this.Text = "Discord Quests";
            this.KeyPreview = true;

            _panel = new QuestsPanel();
            _panel.Dock = DockStyle.Fill;
            _panel.ShowOuterBorder = true;
            _panel.SimulateRequested += g => { if (SimulateRequested != null) SimulateRequested(g); };
            _panel.StopSimulateRequested += g => { if (StopSimulateRequested != null) StopSimulateRequested(g); };
            _panel.QueueRequested += g => { if (QueueRequested != null) QueueRequested(g); };
            _panel.CloseRequested += () => this.Close();
            _panel.MinimizeRequested += () => { this.WindowState = FormWindowState.Minimized; };
            _panel.MaximizeToggleRequested += ToggleMaximize;
            _panel.DragRequested += () => Program.DragWindow(this.Handle);
            this.Controls.Add(_panel);
        }

        public void SetFiltersForTesting(string category, string query, bool regionOnly, bool hideCompleted, string sortMode)
        {
            _panel.SetFiltersForTesting(category, query, regionOnly, hideCompleted, sortMode);
        }

        public void OpenDetailForTesting(QuestItem quest)
        {
            _panel.OpenDetailForTesting(quest);
        }

        private void ToggleMaximize()
        {
            this.WindowState = (this.WindowState == FormWindowState.Maximized)
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.IsHandleCreated && !Program.TryEnableDwmRounding(this.Handle))
            {
                if (this.WindowState == FormWindowState.Maximized) this.Region = null;
                else
                {
                    using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(Point.Empty, this.ClientSize), 16))
                        this.Region = new Region(path);
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                if (_panel != null && _panel.HandleEscape()) e.Handled = true;
                else { this.Close(); e.Handled = true; }
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00C00000; // WS_CAPTION
                cp.Style &= ~0x00040000; // WS_THICKFRAME
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX
                cp.Style |= 0x00010000; // WS_MAXIMIZEBOX
                cp.Style |= 0x00080000; // WS_SYSMENU
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Program.EnableImmersiveDarkMode(this.Handle);
            if (!Program.TryEnableDwmRounding(this.Handle))
            {
                using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(Point.Empty, this.ClientSize), 16))
                    this.Region = new Region(path);
            }
        }

        public static bool TryForwardResizeHitTest(Control child, ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            if (m.Msg != WM_NCHITTEST || child == null) return false;
            Form parent = child.FindForm();
            if (parent == null || parent.WindowState != FormWindowState.Normal || parent.MaximumSize == parent.MinimumSize && parent.MaximumSize != Size.Empty)
                return false;
            Point screenPt = new Point(unchecked((short)(m.LParam.ToInt64() & 0xFFFF)),
                unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            Point pt = parent.PointToClient(screenPt);
            if (pt.X < ResizeGrip || pt.X >= parent.ClientSize.Width - ResizeGrip ||
                pt.Y < ResizeGrip || pt.Y >= parent.ClientSize.Height - ResizeGrip)
            {
                m.Result = (IntPtr)(-1); // HTTRANSPARENT: let parent Form handle edge/corner resize
                return true;
            }
            return false;
        }

        public static bool HandleFormResizeHitTest(Form form, ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            if (m.Msg != WM_NCHITTEST || form == null || form.WindowState != FormWindowState.Normal) return false;
            Point pt = form.PointToClient(new Point(unchecked((short)(m.LParam.ToInt64() & 0xFFFF)),
                unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF))));
            bool left = pt.X < ResizeGrip;
            bool right = pt.X >= form.ClientSize.Width - ResizeGrip;
            bool top = pt.Y < ResizeGrip;
            bool bottom = pt.Y >= form.ClientSize.Height - ResizeGrip;
            if (top && left) { m.Result = (IntPtr)13; return true; }
            if (top && right) { m.Result = (IntPtr)14; return true; }
            if (bottom && left) { m.Result = (IntPtr)16; return true; }
            if (bottom && right) { m.Result = (IntPtr)17; return true; }
            if (left) { m.Result = (IntPtr)10; return true; }
            if (right) { m.Result = (IntPtr)11; return true; }
            if (top) { m.Result = (IntPtr)12; return true; }
            if (bottom) { m.Result = (IntPtr)15; return true; }
            return false;
        }

        protected override void WndProc(ref Message m)
        {
            if (HandleFormResizeHitTest(this, ref m)) return;
            base.WndProc(ref m);
        }
    }

    #endregion

    #region Main Form

    public class MainForm : Form, IMessageFilter
    {
        private string originalLauncherPath = "";
        private string currentExeName = "";
        private string currentDisplayName = "";
        private bool isSimulatingMode = false;
        private bool isSwitchingProcesses = false;

        private Stopwatch sessionStopwatch;
        private System.Windows.Forms.Timer timerQualification;
        private System.Windows.Forms.Timer timerUIUpdate;
        private System.Windows.Forms.Timer timerHoverCheck;
        private NotifyIcon trayIcon;
        private ToolTip globalToolTip;

        private bool hasSaved2MinQualification = false;
        private bool qualificationFinalRecorded = false;

        // Visual controls
        private HeroGameControl heroControl;
        private GameSwitcherControl switcherControl;
        private PillActionButton btnAction;
        private Label lblDiscordStatus;
        private Panel pnlInlineSearch;
        private TextBox txtInlineSearch;
        private SearchDropdownList searchDropdown;
        private System.Windows.Forms.Timer debounceSearchTimer;

        // Window hover state for top-right close / minimize buttons
        private bool _isMouseInWindow = false;
        private bool _isHoveringClose = false;
        private bool _isHoveringMinimize = false;
        private bool _isHoveringAddBtn = false;
        private bool _isHoveringQuestsBtn = false;
        private int _activeQuestCount = -1;
        private QuestsPanel questsPanel;
        private bool isQuestsDocked = false;
        private Size lastExpandedSize = new Size(1340, 640);

        private readonly Rectangle CloseRect = new Rectangle(390, 12, 18, 18);
        private readonly Rectangle MinimizeRect = new Rectangle(364, 12, 18, 18);
        private readonly Rectangle AddBtnRect = new Rectangle(376, 440, 22, 22);
        private readonly Rectangle QuestsBtnRect = new Rectangle(14, 10, 92, 24);

        private Process dummyProcess;
        private string dummyExePath;
        private string dummyMode = "hidden";
        private IntPtr dummyHwnd = IntPtr.Zero;

        private GameItem selectedGame;
        private GameItem activeSimulatingGame;
        private readonly List<ExtraGameInstance> extraInstances = new List<ExtraGameInstance>();
        private bool isLaunchingExtra = false;
        private MultiInstanceStripControl extraStripControl;
        private Panel pnlExtraSearch;
        private TextBox txtExtraSearch;
        private SearchDropdownList extraSearchDropdown;
        private System.Windows.Forms.Timer debounceExtraSearchTimer;
        private string extraSearchResultsQuery = "";
        private readonly List<QueueItem> queueItems = new List<QueueItem>();
        private readonly Random queueRandom = new Random();
        private bool isQueuePanelVisible = false;
        private bool isQueueRunning = false;
        private QueuePhase queuePhase = QueuePhase.Idle;
        private int queueActiveIndex = -1;
        private Stopwatch queueBreakStopwatch;
        private int queueBreakTargetSeconds = 0;
        private QueueStripControl queueStripControl;
        private bool isPickerForQueue = false;
        private string startupGameArg = null;
        private bool hasBeenShown;
        private string searchResultsQuery = "";

        // Discord RPC Presence
        private DiscordRpcClient rpcClient;
        private Thread rpcThread;
        private ManualResetEvent rpcStop;
        private readonly object _rpcLock = new object();
        private volatile string rpcStatus = "Activity simulation only.";
        private volatile string rpcStatusDetail = "Quest progress is not available through Discord Rich Presence.";

        public MainForm(string[] args)
        {
            globalToolTip = new ToolTip();
            globalToolTip.AutoPopDelay = 3000;
            globalToolTip.InitialDelay = 200;
            globalToolTip.ReshowDelay = 150;

            GameDatabase.Instance.OnDatabaseUpdated += OnDatabaseLoaded;
            GameDatabase.Instance.OnQualifiedGamesChanged += OnQualifiedGamesUpdated;
            GameDatabase.Instance.OnInitializationCompleted += OnDatabaseLoaded;
            QuestDatabase.Instance.OnQuestsUpdated += OnQuestsCatalogUpdated;

            InitializeWindow();

            string procName = Process.GetCurrentProcess().ProcessName;
            bool isRenamedBinary = !procName.Equals("GameLauncher", StringComparison.OrdinalIgnoreCase) &&
                                   !procName.Equals("DiscordQuestLauncher", StringComparison.OrdinalIgnoreCase) &&
                                   !procName.Equals("powershell", StringComparison.OrdinalIgnoreCase) &&
                                   !procName.Equals("pwsh", StringComparison.OrdinalIgnoreCase);

            if (args != null && args.Length >= 2 && args[0].Equals("--simulating", StringComparison.OrdinalIgnoreCase))
            {
                currentDisplayName = args[1];
                originalLauncherPath = args.Length >= 3 ? args[2] : "";
                if (args.Length >= 5)
                {
                    int x, y;
                    if (int.TryParse(args[3], out x) && int.TryParse(args[4], out y) && x > -5000 && y > -5000)
                    {
                        this.StartPosition = FormStartPosition.Manual;
                        this.Location = new Point(x, y);
                    }
                }
                currentExeName = args.Length >= 7 && !string.IsNullOrEmpty(args[6]) ? args[6] : Path.GetFileName(Application.ExecutablePath);

                string iconId = args.Length >= 8 ? args[7] : "";
                string iconHash = args.Length >= 9 ? args[8] : "";

                if (!string.IsNullOrEmpty(iconId))
                {
                    activeSimulatingGame = new GameItem { id = iconId, icon = iconHash, name = currentDisplayName, exe = currentExeName };
                }
                else
                {
                    EnsureDatabaseInitializedSync();
                    activeSimulatingGame = GameDatabase.Instance.FindExactGame(currentDisplayName) ?? GameDatabase.Instance.FindExactGame(currentExeName) ??
                        new GameItem { name = currentDisplayName, exe = currentExeName };
                }

                StartRunningState(activeSimulatingGame);
            }
            else if (args != null && args.Length >= 1 && args[0].Equals("--launcher", StringComparison.OrdinalIgnoreCase))
            {
                EnsureDatabaseInitialized();
                if (args.Length >= 3)
                {
                    int x, y;
                    if (int.TryParse(args[1], out x) && int.TryParse(args[2], out y) && x > -5000 && y > -5000)
                    {
                        this.StartPosition = FormStartPosition.Manual;
                        this.Location = new Point(x, y);
                    }
                }
            }
            else if (isRenamedBinary)
            {
                string exePath = Application.ExecutablePath;
                bool isInDummyDir = IsOwnedHelperPath(exePath);
                bool isInSteamCommon = exePath.IndexOf(Path.Combine("steamapps", "common"), StringComparison.OrdinalIgnoreCase) >= 0;

                if (isInDummyDir || isInSteamCommon)
                {
                    currentDisplayName = FormatDisplayName(procName);
                    currentExeName = Path.GetFileName(exePath);
                    originalLauncherPath = "";
                    EnsureDatabaseInitializedSync();
                    activeSimulatingGame = GameDatabase.Instance.FindExactGame(procName) ??
                        new GameItem { name = currentDisplayName, exe = currentExeName };
                    StartRunningState(activeSimulatingGame);
                }
                else
                {
                    EnsureDatabaseInitialized();
                }
            }
            else if (args != null && args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith("--"))
            {
                EnsureDatabaseInitialized();
                startupGameArg = args[0];
            }
            else
            {
                EnsureDatabaseInitialized();
            }
        }

        private void EnsureDatabaseInitialized()
        {
            GameDatabase.Instance.Initialize();
        }

        private void EnsureDatabaseInitializedSync()
        {
            if (GameDatabase.Instance.Count == 0 && !GameDatabase.Instance.IsUpdating)
            {
                GameDatabase.Instance.InitializeSync();
            }
        }

        private void InitializeWindow()
        {
            // Fixed size 420x480, non-resizable, frameless with no visible title bar
            this.ClientSize = new Size(420, 480);
            this.MinimumSize = new Size(420, 480);
            this.MaximumSize = new Size(420, 480);
            this.FormBorderStyle = FormBorderStyle.None;
            this.ControlBox = false;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.BgBase;
            this.ForeColor = Theme.TextPrimary;
            this.DoubleBuffered = true;
            this.Icon = CreateAppIcon();


            // Tray Icon
            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon;
            trayIcon.Text = "Quest Launcher " + Program.BuildId;
            trayIcon.Visible = true;
            trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
            };
            trayIcon.DoubleClick += (s, e) => RestoreFromTray();
            UpdateTrayContextMenu();

            // Background timers
            timerQualification = new System.Windows.Forms.Timer();
            timerQualification.Interval = 2000;
            timerQualification.Tick += OnQualificationTick;

            timerUIUpdate = new System.Windows.Forms.Timer();
            timerUIUpdate.Interval = 1000;
            timerUIUpdate.Tick += OnUITick;

            timerHoverCheck = new System.Windows.Forms.Timer();
            timerHoverCheck.Interval = 80;
            this.LocationChanged += (s, e) => SyncDummyWindow();

            timerHoverCheck.Tick += (s, e) =>
            {
                bool needsSync = (dummyProcess != null && dummyHwnd == IntPtr.Zero);
                if (!needsSync)
                {
                    for (int i = 0; i < extraInstances.Count; i++)
                    {
                        if (extraInstances[i].DummyProcess != null && extraInstances[i].DummyHwnd == IntPtr.Zero)
                        {
                            needsSync = true;
                            break;
                        }
                    }
                }
                if (needsSync) SyncDummyWindow();
                if (!Visible || WindowState == FormWindowState.Minimized) return;
                Point pt = this.PointToClient(Cursor.Position);
                bool inWin = this.ClientRectangle.Contains(pt);
                if (inWin != _isMouseInWindow)
                {
                    _isMouseInWindow = inWin;
                    Invalidate(new Rectangle(360, 10, 52, 24));
                }
            };
            timerHoverCheck.Start();

            debounceSearchTimer = new System.Windows.Forms.Timer();
            debounceSearchTimer.Interval = 120;
            debounceSearchTimer.Tick += OnDebounceSearchTick;

            debounceExtraSearchTimer = new System.Windows.Forms.Timer();
            debounceExtraSearchTimer.Interval = 120;
            debounceExtraSearchTimer.Tick += OnDebounceExtraSearchTick;

            BuildMinimalUI();
        }

        private void BuildMinimalUI()
        {
            // Resolve initial selected game (last simulated or fallback)
            GameItem initialGame = GameDatabase.Instance.GetLastSimulatedGame();
            if (initialGame == null)
            {
                initialGame = new GameItem
                {
                    id = "1437509662303059998",
                    name = "Where Winds Meet",
                    icon = "54b48542501564ce5e9837f7fa3171a5",
                    exe = "where winds meet.exe"
                };
            }
            selectedGame = initialGame;

            // 1. Hero (~50% height)
            heroControl = new HeroGameControl();
            heroControl.Location = new Point(0, 38);
            heroControl.Size = new Size(420, 180);
            heroControl.SetGame(initialGame, false);
            heroControl.NameClicked += (s, e) => OpenInlineSearch();
            heroControl.EmptyMouseDown += (s, e) => Program.DragWindow(this.Handle);
            this.Controls.Add(heroControl);

            // 2. Switcher: row of 32px icons, 8px gap, centered
            switcherControl = new GameSwitcherControl(globalToolTip);
            switcherControl.Location = new Point(0, 244);
            switcherControl.Size = new Size(420, 40);
            switcherControl.GameSelected += (game) => SelectGame(game);
            switcherControl.EmptyMouseDown += (s, e) => Program.DragWindow(this.Handle);
            this.Controls.Add(switcherControl);
            RefreshSwitcherList();

            queueStripControl = new QueueStripControl(globalToolTip);
            queueStripControl.Location = new Point(0, 288);
            queueStripControl.Size = new Size(420, 44);
            queueStripControl.Visible = false;
            queueStripControl.RemoveGameRequested += OnQueueRemoveRequested;
            queueStripControl.ConcurrentStopRequested += (inst) => StopExtraSimulation(inst, true);
            queueStripControl.EmptyMouseDown += (s, e) => Program.DragWindow(this.Handle);
            this.Controls.Add(queueStripControl);

            // 3. Action: sleek compact pill button (148x36 idle, 196x36 running)
            btnAction = new PillActionButton();
            btnAction.ActionText = "Launch";
            btnAction.ActionClicked += OnActionButtonClicked;
            this.Controls.Add(btnAction);
            btnAction.SetRunningState(false);
            globalToolTip.SetToolTip(btnAction, "The timer measures this local session, not Discord quest progress.");

            extraStripControl = new MultiInstanceStripControl(globalToolTip);
            extraStripControl.Location = new Point(0, 382);
            extraStripControl.Size = new Size(420, 32);
            extraStripControl.Visible = false;
            extraStripControl.QueueButtonClicked += (s, e) => OpenQueueGamePicker();
            extraStripControl.AddInstanceClicked += (s, e) => OpenExtraGamePicker();
            extraStripControl.EmptyMouseDown += (s, e) => Program.DragWindow(this.Handle);
            this.Controls.Add(extraStripControl);

            lblDiscordStatus = new Label();
            lblDiscordStatus.Location = new Point(26, 383);
            lblDiscordStatus.Size = new Size(368, 44);
            lblDiscordStatus.Font = FontManager.SmallFont;
            lblDiscordStatus.ForeColor = Theme.TextMuted;
            lblDiscordStatus.TextAlign = ContentAlignment.TopCenter;
            this.Controls.Add(lblDiscordStatus);
            RefreshDiscordStatus();

            // Floating inline search overlay (positioned exactly where the hero name is)
            pnlInlineSearch = new Panel();
            pnlInlineSearch.Location = new Point(58, 150);
            pnlInlineSearch.Size = new Size(304, 34);
            pnlInlineSearch.BackColor = Theme.BgInput;
            pnlInlineSearch.Visible = false;

            txtInlineSearch = new TextBox();
            txtInlineSearch.BorderStyle = BorderStyle.None;
            txtInlineSearch.BackColor = Theme.BgInput;
            txtInlineSearch.ForeColor = Theme.TextPrimary;
            txtInlineSearch.Font = FontManager.SearchFont;
            txtInlineSearch.Location = new Point(10, 8);
            txtInlineSearch.Size = new Size(284, 20);
            txtInlineSearch.TextChanged += (s, e) =>
            {
                pnlInlineSearch.Invalidate();
                debounceSearchTimer.Stop();
                debounceSearchTimer.Start();
            };
            txtInlineSearch.KeyDown += OnInlineSearchKeyDown;
            pnlInlineSearch.Controls.Add(txtInlineSearch);

            pnlInlineSearch.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bRect = new Rectangle(0, 0, pnlInlineSearch.Width - 1, pnlInlineSearch.Height - 1);
                using (GraphicsPath bp = Program.GetRoundedPath(bRect, 8))
                {
                    using (Pen ring = new Pen(Theme.AccentFocus, 1.5f))
                    {
                        pe.Graphics.DrawPath(ring, bp);
                    }
                }

                // Placeholder: "Search"
                if (string.IsNullOrEmpty(txtInlineSearch.Text))
                {
                    TextRenderer.DrawText(pe.Graphics, "Search", FontManager.SearchFont, new Point(10, 8), Theme.TextMuted);
                }
            };
            this.Controls.Add(pnlInlineSearch);

            searchDropdown = new SearchDropdownList();
            searchDropdown.Location = new Point(46, 188);
            searchDropdown.Size = new Size(328, 180);
            searchDropdown.Visible = false;
            searchDropdown.ItemSelected += (game) =>
            {
                if (EnsureSearchResultsCurrent()) return;
                AcceptSearchSelection(game);
            };
            this.Controls.Add(searchDropdown);

            // Overlay picker for launching additional concurrent game instances while simulating
            pnlExtraSearch = new Panel();
            pnlExtraSearch.Location = new Point(58, 150);
            pnlExtraSearch.Size = new Size(304, 34);
            pnlExtraSearch.BackColor = Theme.BgInput;
            pnlExtraSearch.Visible = false;

            txtExtraSearch = new TextBox();
            txtExtraSearch.BorderStyle = BorderStyle.None;
            txtExtraSearch.BackColor = Theme.BgInput;
            txtExtraSearch.ForeColor = Theme.TextPrimary;
            txtExtraSearch.Font = FontManager.SearchFont;
            txtExtraSearch.Location = new Point(10, 8);
            txtExtraSearch.Size = new Size(284, 20);
            txtExtraSearch.TextChanged += (s, e) =>
            {
                pnlExtraSearch.Invalidate();
                debounceExtraSearchTimer.Stop();
                debounceExtraSearchTimer.Start();
            };
            txtExtraSearch.KeyDown += OnExtraSearchKeyDown;
            pnlExtraSearch.Controls.Add(txtExtraSearch);

            pnlExtraSearch.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bRect = new Rectangle(0, 0, pnlExtraSearch.Width - 1, pnlExtraSearch.Height - 1);
                using (GraphicsPath bp = Program.GetRoundedPath(bRect, 8))
                {
                    using (Pen ring = new Pen(Theme.AccentFocus, 1.5f))
                    {
                        pe.Graphics.DrawPath(ring, bp);
                    }
                }
                if (string.IsNullOrEmpty(txtExtraSearch.Text))
                {
                    string hint = isPickerForQueue ? "Add game to queue..." : "Run another game...";
                    TextRenderer.DrawText(pe.Graphics, hint, FontManager.SearchFont, new Point(10, 8), Theme.TextMuted);
                }
            };
            this.Controls.Add(pnlExtraSearch);

            extraSearchDropdown = new SearchDropdownList();
            extraSearchDropdown.Location = new Point(46, 188);
            extraSearchDropdown.Size = new Size(328, 180);
            extraSearchDropdown.Visible = false;
            extraSearchDropdown.ItemSelected += (game) =>
            {
                if (EnsureExtraSearchResultsCurrent()) return;
                AcceptExtraSearchSelection(game);
            };
            this.Controls.Add(extraSearchDropdown);
        }

        private void SelectGame(GameItem game)
        {
            if (game == null || game.id == "__ADD_CUSTOM__" || isSimulatingMode || isLaunching) return;
            selectedGame = game;
            heroControl.SetGame(game, true);
            RefreshSwitcherList();
            if (!GameDatabase.Instance.SaveLastSimulatedGame(game))
                MessageBox.Show("Could not save the selected game. See launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void RefreshSwitcherList()
        {
            List<QualifiedGameEntry> qualified = GameDatabase.Instance.GetQualifiedGames();
            List<GameItem> list = new List<GameItem>();

            foreach (var q in qualified)
            {
                list.Add(new GameItem { id = q.id, name = q.name, icon = q.icon, exe = q.exe });
            }

            // Fallback list of popular games if user hasn't played 6 games yet
            if (list.Count < 6)
            {
                List<GameItem> popular = GameDatabase.Instance.Search("", 12);
                foreach (var p in popular)
                {
                    if (list.Count >= 8) break;
                    if (!list.Exists(x => string.Equals(x.name, p.name, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(p);
                    }
                }
            }

            switcherControl.SetRecentGames(list, selectedGame);
        }

        private void OpenInlineSearch()
        {
            if (isSimulatingMode || isLaunching) return;
            if (pnlInlineSearch.Visible) return;
            heroControl.IsSearchActive = true;
            pnlInlineSearch.Visible = true;
            pnlInlineSearch.BringToFront();
            txtInlineSearch.Text = "";
            txtInlineSearch.Focus();

            searchDropdown.Visible = true;
            searchDropdown.BringToFront();
            List<GameItem> initialMatches = GameDatabase.Instance.Search("", 10);
            searchDropdown.SetResults(initialMatches);
            searchResultsQuery = "";

            // Only listen while search is open: the filter sees every message on the UI thread.
            if (!_outsideClickFilterActive)
            {
                Application.AddMessageFilter(this);
                _outsideClickFilterActive = true;
            }
        }

        private void CloseInlineSearch()
        {
            debounceSearchTimer.Stop();
            pnlInlineSearch.Visible = false;
            searchDropdown.Visible = false;
            heroControl.IsSearchActive = false;
            RemoveOutsideClickFilterIfIdle();
            this.ActiveControl = btnAction;
        }

        private void OpenExtraGamePicker()
        {
            if (!isSimulatingMode || isQueueRunning || isLaunching || isLaunchingExtra) return;
            if (extraInstances.Count >= MultiInstanceStripControl.MaxExtraInstances) return;
            if (pnlExtraSearch.Visible) return;

            isPickerForQueue = false;
            pnlExtraSearch.Invalidate();
            pnlExtraSearch.Visible = true;
            pnlExtraSearch.BringToFront();
            txtExtraSearch.Text = "";
            txtExtraSearch.Focus();

            extraSearchDropdown.Visible = true;
            extraSearchDropdown.BringToFront();
            List<GameItem> initialMatches = FilterNonRunningGames(GameDatabase.Instance.Search("", 15));
            extraSearchDropdown.SetResults(initialMatches);
            extraSearchResultsQuery = "";

            if (!_outsideClickFilterActive)
            {
                Application.AddMessageFilter(this);
                _outsideClickFilterActive = true;
            }
        }

        private void OpenQueueGamePicker()
        {
            if (isLaunching || extraInstances.Count > 0 || queueItems.Count >= QueueStripControl.MaxQueueItems) return;
            if (pnlExtraSearch.Visible) return;

            isPickerForQueue = true;
            pnlExtraSearch.Invalidate();
            pnlExtraSearch.Visible = true;
            pnlExtraSearch.BringToFront();
            txtExtraSearch.Text = "";
            txtExtraSearch.Focus();

            extraSearchDropdown.Visible = true;
            extraSearchDropdown.BringToFront();
            List<GameItem> initialMatches = FilterNonQueuedGames(GameDatabase.Instance.Search("", 15));
            extraSearchDropdown.SetResults(initialMatches);
            extraSearchResultsQuery = "";

            if (!_outsideClickFilterActive)
            {
                Application.AddMessageFilter(this);
                _outsideClickFilterActive = true;
            }
        }

        private void CloseExtraGamePicker()
        {
            if (debounceExtraSearchTimer != null) debounceExtraSearchTimer.Stop();
            if (pnlExtraSearch != null) pnlExtraSearch.Visible = false;
            if (extraSearchDropdown != null) extraSearchDropdown.Visible = false;
            RemoveOutsideClickFilterIfIdle();
            if (btnAction != null && !btnAction.IsDisposed) this.ActiveControl = btnAction;
        }

        private bool _outsideClickFilterActive;

        private void RemoveOutsideClickFilter()
        {
            if (!_outsideClickFilterActive) return;
            Application.RemoveMessageFilter(this);
            _outsideClickFilterActive = false;
        }

        private void RemoveOutsideClickFilterIfIdle()
        {
            bool inlineOpen = pnlInlineSearch != null && pnlInlineSearch.Visible;
            bool extraOpen = pnlExtraSearch != null && pnlExtraSearch.Visible;
            if (!inlineOpen && !extraOpen) RemoveOutsideClickFilter();
        }

        // Light-dismiss for the search overlay. Mouse messages go to the HWND under the cursor, so
        // MainForm.OnMouseDown never sees clicks on the hero, switcher, pill button or status label.
        // A thread-level IMessageFilter runs before dispatch to ANY control, so one check covers them all.
        // We return false (don't swallow it) so the click still does its normal job, e.g. clicking a
        // switcher icon closes search AND selects that game, like any native popup.
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
            const int WM_NCLBUTTONDOWN = 0x00A1, WM_NCRBUTTONDOWN = 0x00A4;
            bool inlineOpen = pnlInlineSearch != null && pnlInlineSearch.Visible;
            bool extraOpen = pnlExtraSearch != null && pnlExtraSearch.Visible;
            if (!inlineOpen && !extraOpen) return false;

            Point screen;
            if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN)
            {
                // Client messages carry coordinates relative to m.HWnd (signed 16-bit each).
                Program.POINT pt = new Program.POINT();
                pt.X = unchecked((short)(m.LParam.ToInt64() & 0xFFFF));
                pt.Y = unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                Program.ClientToScreen(m.HWnd, ref pt);
                screen = new Point(pt.X, pt.Y);
            }
            else if (m.Msg == WM_NCLBUTTONDOWN || m.Msg == WM_NCRBUTTONDOWN)
            {
                // Non-client messages already carry screen coordinates.
                screen = new Point(unchecked((short)(m.LParam.ToInt64() & 0xFFFF)), unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            }
            else return false;

            if (!IsInsideSearchUi(screen))
            {
                if (inlineOpen) CloseInlineSearch();
                if (extraOpen) CloseExtraGamePicker();
            }
            return false;
        }

        private bool IsInsideSearchUi(Point screenPoint)
        {
            bool inInline = pnlInlineSearch != null && pnlInlineSearch.Visible &&
                (RectangleToScreen(pnlInlineSearch.Bounds).Contains(screenPoint) ||
                 (searchDropdown != null && searchDropdown.Visible && RectangleToScreen(searchDropdown.Bounds).Contains(screenPoint)));
            bool inExtra = pnlExtraSearch != null && pnlExtraSearch.Visible &&
                (RectangleToScreen(pnlExtraSearch.Bounds).Contains(screenPoint) ||
                 (extraSearchDropdown != null && extraSearchDropdown.Visible && RectangleToScreen(extraSearchDropdown.Bounds).Contains(screenPoint)));
            return inInline || inExtra;
        }

        // Clicking another app / Alt-Tab never produces a mouse message on our thread, so close on focus loss too.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (pnlInlineSearch != null && pnlInlineSearch.Visible) CloseInlineSearch();
            if (pnlExtraSearch != null && pnlExtraSearch.Visible) CloseExtraGamePicker();
        }

        private void OnDebounceSearchTick(object sender, EventArgs e)
        {
            debounceSearchTimer.Stop();
            string q = txtInlineSearch.Text.Trim();
            List<GameItem> matches = GameDatabase.Instance.Search(q, 15);

            if (!string.IsNullOrEmpty(q) && !matches.Exists(m => string.Equals(m.name, q, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(new GameItem
                {
                    id = "__ADD_CUSTOM__",
                    name = "+ Add \"" + q + "\"",
                    exe = GameDatabase.InferExecutableFromName(q),
                    icon = ""
                });
            }

            searchDropdown.SetResults(matches);
            searchResultsQuery = q;
        }

        private bool EnsureSearchResultsCurrent()
        {
            if (string.Equals(searchResultsQuery, txtInlineSearch.Text.Trim(), StringComparison.Ordinal)) return false;
            OnDebounceSearchTick(this, EventArgs.Empty);
            return true;
        }

        private void AcceptSearchSelection(GameItem game)
        {
            if (game == null || isSimulatingMode || isLaunching) return;
            string query = txtInlineSearch.Text;
            CloseInlineSearch();
            if (game.id == "__ADD_CUSTOM__") ShowAddCustomGameDialog(query);
            else SelectGame(game);
        }

        private void OnInlineSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                EnsureSearchResultsCurrent();
                searchDropdown.MoveSelection(1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                EnsureSearchResultsCurrent();
                searchDropdown.MoveSelection(-1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EnsureSearchResultsCurrent();
                GameItem sel = searchDropdown.SelectedItem;
                if (sel != null)
                {
                    AcceptSearchSelection(sel);
                }
                else if (!string.IsNullOrWhiteSpace(txtInlineSearch.Text))
                {
                    GameItem exact = GameDatabase.Instance.FindExactGame(txtInlineSearch.Text);
                    if (exact != null) SelectGame(exact);
                    CloseInlineSearch();
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CloseInlineSearch();
            }
        }

        private void OnDebounceExtraSearchTick(object sender, EventArgs e)
        {
            debounceExtraSearchTimer.Stop();
            string q = txtExtraSearch.Text.Trim();
            List<GameItem> raw = GameDatabase.Instance.Search(q, 15);
            List<GameItem> matches = isPickerForQueue ? FilterNonQueuedGames(raw) : FilterNonRunningGames(raw);
            extraSearchDropdown.SetResults(matches);
            extraSearchResultsQuery = q;
        }

        private bool EnsureExtraSearchResultsCurrent()
        {
            if (string.Equals(extraSearchResultsQuery, txtExtraSearch.Text.Trim(), StringComparison.Ordinal)) return false;
            OnDebounceExtraSearchTick(this, EventArgs.Empty);
            return true;
        }

        private void AcceptExtraSearchSelection(GameItem game)
        {
            if (game == null || isLaunching) return;
            bool forQueue = isPickerForQueue;
            CloseExtraGamePicker();
            if (forQueue)
            {
                AddGameToQueue(game);
            }
            else
            {
                if (!isSimulatingMode || isLaunchingExtra) return;
                LaunchExtraSimulation(game);
            }
        }

        private void OnExtraSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                EnsureExtraSearchResultsCurrent();
                extraSearchDropdown.MoveSelection(1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                EnsureExtraSearchResultsCurrent();
                extraSearchDropdown.MoveSelection(-1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EnsureExtraSearchResultsCurrent();
                GameItem sel = extraSearchDropdown.SelectedItem;
                if (sel != null) AcceptExtraSearchSelection(sel);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CloseExtraGamePicker();
            }
        }

        private void OnActionButtonClicked(object sender, EventArgs e)
        {
            if (isQueueRunning)
            {
                StopQueue();
            }
            else if (isSimulatingMode)
            {
                StopSimulation();
            }
            else if (isQueuePanelVisible && HasPendingQueueItems())
            {
                StartQueue();
            }
            else
            {
                if (selectedGame != null)
                {
                    LaunchSimulation(selectedGame.exe);
                }
            }
        }

        private void LaunchSimulation(string rawInput)
        {
            if (isLaunching || isSimulatingMode) return;
            if (!TryRecordSession())
            {
                MessageBox.Show("Could not save the previous session. Resolve the save error before starting another game; see launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string input = (rawInput ?? "").Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(input)) return;
            selectedGame = GameDatabase.Instance.ResolveGameMetadata(selectedGame);

            string exeName = input;
            string displayName = "";
            string iconId = "";
            string iconHash = "";

            if (selectedGame != null && selectedGame.id != "__ADD_CUSTOM__" &&
                (string.Equals(selectedGame.name, input, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(selectedGame.exe, input, StringComparison.OrdinalIgnoreCase)))
            {
                exeName = selectedGame.exe;
                displayName = selectedGame.name;
                iconId = selectedGame.id ?? "";
                iconHash = selectedGame.icon ?? "";
            }
            else
            {
                GameItem match = GameDatabase.Instance.FindExactGame(input);
                if (match != null)
                {
                    SelectGame(match);
                    exeName = match.exe;
                    displayName = match.name;
                    iconId = match.id ?? "";
                    iconHash = match.icon ?? "";
                }
                else
                {
                    if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeName = GameDatabase.InferExecutableFromName(input);
                    }
                    displayName = FormatDisplayName(input);
                }
            }

            GameDatabase.Instance.SaveLastSimulatedGame(new GameItem
            {
                id = iconId,
                name = displayName,
                icon = iconHash,
                exe = exeName,
                executable_path = selectedGame != null && selectedGame.id == iconId ? selectedGame.executable_path : null
            });

            exeName = Path.GetFileName(exeName);
            if (string.IsNullOrEmpty(exeName) || exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";

            // A unique owned directory prevents collisions with other sessions and real games.
            string relativeExePath = selectedGame != null && selectedGame.id == iconId &&
                !string.IsNullOrEmpty(selectedGame.executable_path) ? selectedGame.executable_path : exeName;
            relativeExePath = GameDatabase.NormalizeExecutablePath(relativeExePath);
            if (relativeExePath == null || !string.Equals(Path.GetFileName(relativeExePath), exeName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("This game's executable path is invalid. Refresh its metadata or edit the custom game.", "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string sessionDir = Path.Combine(Path.GetTempPath(), "DiscordDummyTools", Guid.NewGuid().ToString("N"));
            string targetExePath = Path.Combine(sessionDir, relativeExePath);
            string tempDir = Path.GetDirectoryName(targetExePath);
            try { Directory.CreateDirectory(tempDir); }
            catch (Exception ex)
            {
                Program.LogError("CreateHelperDirectory", ex);
                MessageBox.Show("Could not create the helper directory:\n" + ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string currentExePath = Application.ExecutablePath;

            string mode = Program.GetLaunchMode();

            if (mode != "classic" || isQueueRunning)
            {
                // Copying and starting the helper may trigger antivirus scans.
                // runs off the UI thread so the window never freezes or goes blank.
                string effectiveMode = (mode == "classic") ? "behind" : mode;
                BeginAsyncLaunch(targetExePath, currentExePath, displayName, exeName, iconId, iconHash, effectiveMode);
                return;
            }

            // ---- classic mode: original synchronous behaviour ----
            try
            {
                File.Copy(currentExePath, targetExePath, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to create dummy executable:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ScheduleTempCleanup(targetExePath);
                return;
            }

            ProcessStartInfo cpsi = new ProcessStartInfo();
            cpsi.FileName = targetExePath;
            cpsi.Arguments = string.Format("--simulating \"{0}\" \"{1}\" {2} {3} 1 \"{4}\" \"{5}\" \"{6}\"",
                displayName, currentExePath, this.Location.X, this.Location.Y, exeName, iconId, iconHash);
            cpsi.UseShellExecute = true;
            try
            {
                Process.Start(cpsi);
                isSwitchingProcesses = true;
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start dummy executable:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ScheduleTempCleanup(targetExePath);
            }
        }

        private bool isLaunching = false;

        protected virtual void BeginAsyncLaunch(string targetExePath, string currentExePath, string displayName, string exeName, string iconId, string iconHash, string mode)
        {
            if (isLaunching) return;
            isLaunching = true;

            int myPid = Process.GetCurrentProcess().Id;
            int locX = this.Location.X;
            int locY = this.Location.Y;
            string safeName = (displayName ?? "").Replace("\"", "'");

            GameItem running = selectedGame;
            if (running == null || running.id == "__ADD_CUSTOM__" ||
                (!string.Equals(running.name, displayName, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(running.exe, exeName, StringComparison.OrdinalIgnoreCase)))
            {
                running = new GameItem { id = iconId, name = displayName, icon = iconHash, exe = exeName };
            }

            if (btnAction != null) btnAction.ActionText = "Starting...";
            switcherControl.Enabled = false;
            CloseInlineSearch();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Process started = null;
                string error = null;
                try
                {
                    File.Copy(currentExePath, targetExePath, false);

                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = targetExePath;
                    psi.Arguments = string.Format("--dummy {0} \"{1}\" \"{2}\" \"{3}\" {4} {5} {6}",
                        myPid, safeName, iconId, iconHash, mode, locX, locY);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(targetExePath);
                    started = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        isLaunching = false;
                        if (started == null)
                        {
                            if (btnAction != null) btnAction.ActionText = "Launch";
                            switcherControl.Enabled = true;
                            ScheduleTempCleanup(targetExePath);
                            MessageBox.Show("Failed to start dummy executable:\n" + (error ?? "unknown error"), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        dummyProcess = started;
                        dummyExePath = targetExePath;
                        dummyMode = mode;
                        dummyHwnd = IntPtr.Zero;
                        StartRunningState(running);
                        SyncDummyWindow();
                    }));
                }
                catch
                {
                    // Form was closed while launching: don't leave an orphan behind.
                    try { if (started != null && !started.HasExited) started.Kill(); } catch { }
                    if (started != null) started.Dispose();
                    ScheduleTempCleanup(targetExePath);
                }
            });
        }

        private bool DummyIsVisibleMode()
        {
            return dummyMode == "behind" || dummyMode == "single";
        }

        private bool IsSameGameIdentity(GameItem a, GameItem b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.exe) && !string.IsNullOrEmpty(b.exe) &&
                string.Equals(a.exe, b.exe, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(a.name) && !string.IsNullOrEmpty(b.name) &&
                string.Equals(a.name, b.name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool IsGameAlreadyRunning(GameItem game)
        {
            if (game == null) return false;
            if (IsSameGameIdentity(activeSimulatingGame, game)) return true;
            for (int i = 0; i < extraInstances.Count; i++)
            {
                if (IsSameGameIdentity(extraInstances[i].Game, game)) return true;
            }
            return false;
        }

        private List<GameItem> FilterNonRunningGames(List<GameItem> source)
        {
            List<GameItem> filtered = new List<GameItem>();
            if (source == null) return filtered;
            for (int i = 0; i < source.Count; i++)
            {
                GameItem item = source[i];
                if (item == null || item.id == "__ADD_CUSTOM__") continue;
                if (!IsGameAlreadyRunning(item)) filtered.Add(item);
            }
            return filtered;
        }

        private List<GameItem> FilterNonQueuedGames(List<GameItem> source)
        {
            List<GameItem> filtered = new List<GameItem>();
            if (source == null) return filtered;
            for (int i = 0; i < source.Count; i++)
            {
                GameItem item = source[i];
                if (item == null || item.id == "__ADD_CUSTOM__") continue;
                bool queued = isSimulatingMode && !isQueueRunning && activeSimulatingGame != null &&
                              IsSameGameIdentity(activeSimulatingGame, item);
                for (int q = 0; q < queueItems.Count && !queued; q++)
                {
                    if (IsSameGameIdentity(queueItems[q].Game, item)) queued = true;
                }
                if (!queued) filtered.Add(item);
            }
            return filtered;
        }

        private bool HasPendingQueueItems()
        {
            for (int i = 0; i < queueItems.Count; i++)
            {
                if (queueItems[i].State == QueueItemState.Pending) return true;
            }
            return false;
        }

        private void RefreshQueueStrip()
        {
            if (queueStripControl == null) return;
            bool showQueue = (isQueueRunning && queueItems.Count > 0) || (isQueuePanelVisible && queueItems.Count > 0);
            if (showQueue)
            {
                queueStripControl.Visible = true;
                double activeElapsed = (isQueueRunning && queuePhase == QueuePhase.PlayingGame && sessionStopwatch != null)
                    ? sessionStopwatch.Elapsed.TotalSeconds : 0.0;
                double breakElapsed = (isQueueRunning && queuePhase == QueuePhase.BreakCooldown && queueBreakStopwatch != null)
                    ? queueBreakStopwatch.Elapsed.TotalSeconds : 0.0;
                queueStripControl.UpdateState(queueItems, queueActiveIndex, queuePhase, activeElapsed, breakElapsed, queueBreakTargetSeconds);
            }
            else if (isSimulatingMode && extraInstances.Count > 0)
            {
                queueStripControl.Visible = true;
                double primaryElapsed = sessionStopwatch != null ? sessionStopwatch.Elapsed.TotalSeconds : 0.0;
                queueStripControl.UpdateConcurrentState(activeSimulatingGame, primaryElapsed, extraInstances);
            }
            else
            {
                queueStripControl.Visible = false;
            }
        }

        private void UpdateIdleActionButton()
        {
            if (isSimulatingMode || isQueueRunning || isLaunching || btnAction == null) return;
            btnAction.ActionText = (isQueuePanelVisible && HasPendingQueueItems()) ? "Start Queue" : "Launch";
        }

        private void ToggleQueuePanel()
        {
            if (isQueueRunning)
            {
                isQueuePanelVisible = true;
                RefreshQueueStrip();
                return;
            }
            isQueuePanelVisible = !isQueuePanelVisible;
            RefreshQueueStrip();
            UpdateIdleActionButton();
            Invalidate();
        }

        private int ResolvePlannedPlaySeconds(GameItem game)
        {
            int fallback = queueRandom.Next(15 * 60, 25 * 60 + 1);
            int questTarget = QuestDatabase.Instance.GetActiveDesktopQuestTargetSeconds(game, DateTimeOffset.UtcNow);
            if (questTarget > 0)
            {
                int withBuffer = questTarget + queueRandom.Next(60, 151);
                return Math.Max(fallback, withBuffer);
            }
            return fallback;
        }

        private void AddGameToQueue(GameItem rawGame)
        {
            if (rawGame == null || rawGame.id == "__ADD_CUSTOM__") return;
            if (queueItems.Count >= QueueStripControl.MaxQueueItems) return;

            if (isSimulatingMode && !isQueueRunning && activeSimulatingGame != null && queueItems.Count == 0)
            {
                QueueItem currentItem = new QueueItem();
                currentItem.Game = activeSimulatingGame;
                currentItem.PlannedPlaySeconds = ResolvePlannedPlaySeconds(activeSimulatingGame);
                currentItem.PlannedBreakAfterSeconds = queueRandom.Next(10 * 60, 25 * 60 + 1);
                currentItem.State = QueueItemState.Running;
                currentItem.ActualPlayedSeconds = 0;
                queueItems.Add(currentItem);
                isQueueRunning = true;
                queuePhase = QueuePhase.PlayingGame;
                queueActiveIndex = 0;
                UpdateMultiInstanceLayout();
                UpdateTrayContextMenu();
            }

            if (queueItems.Count >= QueueStripControl.MaxQueueItems) return;

            GameItem resolved = GameDatabase.Instance.ResolveGameMetadata(rawGame) ?? rawGame;
            for (int i = 0; i < queueItems.Count; i++)
            {
                if (IsSameGameIdentity(queueItems[i].Game, resolved))
                {
                    isQueuePanelVisible = true;
                    RefreshQueueStrip();
                    UpdateIdleActionButton();
                    Invalidate();
                    return;
                }
            }

            QueueItem item = new QueueItem();
            item.Game = resolved;
            item.PlannedPlaySeconds = ResolvePlannedPlaySeconds(resolved);
            item.PlannedBreakAfterSeconds = queueRandom.Next(10 * 60, 25 * 60 + 1);
            item.State = QueueItemState.Pending;
            item.ActualPlayedSeconds = 0;
            queueItems.Add(item);

            isQueuePanelVisible = true;
            RefreshQueueStrip();
            UpdateMultiInstanceLayout();
            UpdateIdleActionButton();
            Invalidate();
        }

        private void OnQueueRemoveRequested(int index)
        {
            if (index < 0 || index >= queueItems.Count) return;
            if (queueItems[index].State != QueueItemState.Pending) return;
            queueItems.RemoveAt(index);
            if (isQueueRunning && index < queueActiveIndex) queueActiveIndex--;
            RefreshQueueStrip();
            UpdateMultiInstanceLayout();
            UpdateIdleActionButton();
            Invalidate();
        }

        private void StartQueue()
        {
            if (isSimulatingMode || isLaunching || isQueueRunning || queueItems.Count == 0) return;
            if (!HasPendingQueueItems())
            {
                for (int i = 0; i < queueItems.Count; i++)
                {
                    queueItems[i].State = QueueItemState.Pending;
                    queueItems[i].ActualPlayedSeconds = 0;
                }
            }

            int startIdx = -1;
            for (int i = 0; i < queueItems.Count; i++)
            {
                if (queueItems[i].State == QueueItemState.Pending) { startIdx = i; break; }
            }
            if (startIdx < 0) return;

            isQueueRunning = true;
            isQueuePanelVisible = true;
            queueActiveIndex = startIdx;
            queuePhase = QueuePhase.PlayingGame;
            queueItems[queueActiveIndex].State = QueueItemState.Running;

            GameItem game = queueItems[queueActiveIndex].Game;
            SelectGame(game);
            RefreshQueueStrip();
            UpdateMultiInstanceLayout();
            UpdateTrayContextMenu();
            Invalidate();
            LaunchSimulation(game.exe);
        }

        private void StopQueue()
        {
            isQueueRunning = false;
            queuePhase = QueuePhase.Idle;
            if (queueBreakStopwatch != null) queueBreakStopwatch.Stop();
            queueBreakStopwatch = null;
            queueBreakTargetSeconds = 0;
            for (int i = 0; i < queueItems.Count; i++)
            {
                if (queueItems[i].State == QueueItemState.Running) queueItems[i].State = QueueItemState.Pending;
            }
            queueActiveIndex = -1;

            if (isSimulatingMode)
            {
                StopSimulation();
                return;
            }

            switcherControl.Enabled = true;
            if (timerUIUpdate != null) timerUIUpdate.Stop();
            if (timerQualification != null) timerQualification.Stop();
            this.Text = "Quest Launcher";
            Program.SetWindowText(this.Handle, "Quest Launcher");
            if (heroControl != null) heroControl.IsRunning = false;
            if (btnAction != null)
            {
                btnAction.TimerText = "";
                btnAction.SetRunningState(false);
                UpdateIdleActionButton();
            }
            RefreshQueueStrip();
            UpdateMultiInstanceLayout();
            UpdateTrayContextMenu();
            RefreshDiscordStatus();
            Invalidate();
        }

        private void AdvanceQueueFromGameComplete()
        {
            if (!isQueueRunning || queuePhase != QueuePhase.PlayingGame || queueActiveIndex < 0 || queueActiveIndex >= queueItems.Count) return;

            QueueItem finished = queueItems[queueActiveIndex];
            finished.State = QueueItemState.Completed;
            finished.ActualPlayedSeconds = sessionStopwatch != null ? (int)sessionStopwatch.Elapsed.TotalSeconds : 0;

            StopDiscordRpc();
            KillDummy();
            if (sessionStopwatch != null) sessionStopwatch.Stop();
            TryRecordSession();
            isSimulatingMode = false;
            if (timerQualification != null) timerQualification.Stop();

            if (queueActiveIndex + 1 < queueItems.Count)
            {
                queuePhase = QueuePhase.BreakCooldown;
                queueBreakTargetSeconds = finished.PlannedBreakAfterSeconds;
                queueBreakStopwatch = Stopwatch.StartNew();

                GameItem nextGame = queueItems[queueActiveIndex + 1].Game;
                if (heroControl != null)
                {
                    heroControl.SetGame(nextGame, true);
                    heroControl.IsRunning = false;
                }
                if (btnAction != null)
                {
                    int m = queueBreakTargetSeconds / 60;
                    int s = queueBreakTargetSeconds % 60;
                    btnAction.ActionText = "Stop";
                    btnAction.TimerText = string.Format("{0:00}:{1:00}", m, s);
                    btnAction.SetRunningState(true);
                }
                this.Text = "Queue Break — " + (nextGame != null ? nextGame.name : "Next");
                Program.SetWindowText(this.Handle, this.Text);
                if (timerUIUpdate != null && !timerUIUpdate.Enabled) timerUIUpdate.Start();
                RefreshQueueStrip();
                UpdateMultiInstanceLayout();
                UpdateTrayContextMenu();
                RefreshDiscordStatus();
            }
            else
            {
                isQueueRunning = false;
                queuePhase = QueuePhase.Idle;
                queueActiveIndex = -1;
                switcherControl.Enabled = true;
                if (timerUIUpdate != null) timerUIUpdate.Stop();
                this.Text = "Quest Launcher";
                Program.SetWindowText(this.Handle, "Quest Launcher");
                if (heroControl != null) heroControl.IsRunning = false;
                if (btnAction != null)
                {
                    btnAction.TimerText = "";
                    btnAction.SetRunningState(false);
                    UpdateIdleActionButton();
                }
                RefreshQueueStrip();
                UpdateMultiInstanceLayout();
                UpdateTrayContextMenu();
                RefreshSwitcherList();
                RefreshDiscordStatus();
                Invalidate();
            }
        }

        private void UpdateMultiInstanceLayout()
        {
            bool showActionRow = isSimulatingMode || isQueueRunning;
            bool canQueue = extraInstances.Count == 0 && queueItems.Count < QueueStripControl.MaxQueueItems && !isLaunching;
            bool canLaunchAnother = isSimulatingMode && !isQueueRunning &&
                                    extraInstances.Count < MultiInstanceStripControl.MaxExtraInstances && !isLaunching;
            if (extraStripControl != null)
            {
                extraStripControl.Visible = showActionRow;
                if (showActionRow) extraStripControl.SetActionStates(canQueue, canLaunchAnother, isLaunchingExtra);
            }
            if (lblDiscordStatus != null)
            {
                if (showActionRow)
                {
                    lblDiscordStatus.Location = new Point(56, 420);
                    lblDiscordStatus.Size = new Size(308, 34);
                }
                else
                {
                    lblDiscordStatus.Location = new Point(26, 383);
                    lblDiscordStatus.Size = new Size(368, 44);
                }
            }
        }

        private void LaunchExtraSimulation(GameItem rawGame)
        {
            if (!isSimulatingMode || isLaunching || isLaunchingExtra || rawGame == null || rawGame.id == "__ADD_CUSTOM__") return;
            if (extraInstances.Count >= MultiInstanceStripControl.MaxExtraInstances) return;
            if (IsGameAlreadyRunning(rawGame)) return;

            GameItem resolved = GameDatabase.Instance.ResolveGameMetadata(rawGame) ?? rawGame;
            if (IsGameAlreadyRunning(resolved)) return;

            string exeName = Path.GetFileName((resolved.exe ?? "").Trim().Trim('"', '\''));
            if (string.IsNullOrEmpty(exeName)) exeName = GameDatabase.InferExecutableFromName(resolved.name ?? "Game");
            if (exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";

            string displayName = !string.IsNullOrWhiteSpace(resolved.name) ? resolved.name.Trim() : FormatDisplayName(exeName);
            string iconId = resolved.id ?? "";
            string iconHash = resolved.icon ?? "";

            string relativeExePath = !string.IsNullOrEmpty(resolved.executable_path) ? resolved.executable_path : exeName;
            relativeExePath = GameDatabase.NormalizeExecutablePath(relativeExePath);
            if (relativeExePath == null || !string.Equals(Path.GetFileName(relativeExePath), exeName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("This game's executable path is invalid. Refresh its metadata or edit the custom game.", "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string sessionDir = Path.Combine(Path.GetTempPath(), "DiscordDummyTools", Guid.NewGuid().ToString("N"));
            string targetExePath = Path.Combine(sessionDir, relativeExePath);
            string tempDir = Path.GetDirectoryName(targetExePath);
            try { Directory.CreateDirectory(tempDir); }
            catch (Exception ex)
            {
                Program.LogError("CreateExtraHelperDirectory", ex);
                MessageBox.Show("Could not create the helper directory:\n" + ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string currentExePath = !string.IsNullOrEmpty(originalLauncherPath) && File.Exists(originalLauncherPath)
                ? originalLauncherPath : Application.ExecutablePath;
            string mode = Program.GetLaunchMode();
            string helperMode = mode == "classic" ? "behind" : mode;
            int myPid = Process.GetCurrentProcess().Id;
            int locX = this.Location.X;
            int locY = this.Location.Y;
            string safeName = (displayName ?? "").Replace("\"", "'");
            GameItem running = new GameItem
            {
                id = iconId,
                name = displayName,
                icon = iconHash,
                exe = exeName,
                executable_path = resolved.executable_path
            };

            isLaunchingExtra = true;
            UpdateMultiInstanceLayout();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Process started = null;
                string error = null;
                try
                {
                    File.Copy(currentExePath, targetExePath, false);
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = targetExePath;
                    psi.Arguments = string.Format("--dummy {0} \"{1}\" \"{2}\" \"{3}\" {4} {5} {6}",
                        myPid, safeName, iconId, iconHash, helperMode, locX, locY);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(targetExePath);
                    started = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        isLaunchingExtra = false;
                        if (!isSimulatingMode || this.IsDisposed || started == null)
                        {
                            try { if (started != null && !started.HasExited) started.Kill(); } catch { }
                            if (started != null) started.Dispose();
                            ScheduleTempCleanup(targetExePath);
                            UpdateMultiInstanceLayout();
                            if (started == null && !this.IsDisposed && isSimulatingMode)
                            {
                                MessageBox.Show("Failed to start dummy executable:\n" + (error ?? "unknown error"), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            return;
                        }

                        ExtraGameInstance inst = new ExtraGameInstance();
                        inst.Game = running;
                        inst.DummyProcess = started;
                        inst.DummyExePath = targetExePath;
                        inst.DummyMode = helperMode;
                        inst.DummyHwnd = IntPtr.Zero;
                        inst.Stopwatch = Stopwatch.StartNew();
                        extraInstances.Add(inst);
                        StartExtraDiscordRpc(inst);
                        UpdateMultiInstanceLayout();
                        RefreshQueueStrip();
                        UpdateTrayContextMenu();
                        SyncDummyWindow();
                        SyncActiveQuestsProgress(0);
                    }));
                }
                catch
                {
                    try { if (started != null && !started.HasExited) started.Kill(); } catch { }
                    if (started != null) started.Dispose();
                    ScheduleTempCleanup(targetExePath);
                }
            });
        }

        // Keep the real (visible) game window(s) underneath this launcher window.
        private void SyncDummyWindow()
        {
            if (!this.IsHandleCreated) return;
            bool visible = this.Visible && this.WindowState != FormWindowState.Minimized;
            SyncSingleHelperWindow(dummyProcess, ref dummyHwnd, dummyMode, visible);
            for (int i = 0; i < extraInstances.Count; i++)
            {
                ExtraGameInstance inst = extraInstances[i];
                IntPtr hwnd = inst.DummyHwnd;
                SyncSingleHelperWindow(inst.DummyProcess, ref hwnd, inst.DummyMode, visible);
                inst.DummyHwnd = hwnd;
            }
        }

        private void SyncSingleHelperWindow(Process proc, ref IntPtr hwnd, string mode, bool visible)
        {
            if (proc == null || (mode != "behind" && mode != "single") || !this.IsHandleCreated) return;
            try
            {
                if (hwnd == IntPtr.Zero)
                {
                    proc.Refresh();
                    hwnd = proc.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) hwnd = Program.FindProcessWindow(proc.Id);
                    if (hwnd == IntPtr.Zero) return;
                }
                if (!visible)
                {
                    if (Program.IsWindowVisible(hwnd)) Program.ShowWindow(hwnd, 0);
                    return;
                }
                if (!Program.IsWindowVisible(hwnd) || Program.IsIconic(hwnd))
                    Program.ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
                Program.PlaceBelow(hwnd, this.Handle, this.Location.X, this.Location.Y);
            }
            catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (isQuestsDocked && questsPanel != null && !questsPanel.IsDisposed)
            {
                questsPanel.Bounds = new Rectangle(421, 1, Math.Max(200, this.ClientSize.Width - 422), Math.Max(100, this.ClientSize.Height - 2));
            }
            UpdateWindowRegion();
            SyncDummyWindow();
            if (WindowState != FormWindowState.Minimized) Invalidate(true);
        }

        private void UpdateWindowRegion()
        {
            if (!this.IsHandleCreated) return;
            if (!Program.TryEnableDwmRounding(this.Handle))
            {
                if (this.WindowState == FormWindowState.Maximized) this.Region = null;
                else
                {
                    using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(Point.Empty, this.ClientSize), 16))
                        this.Region = new Region(path);
                }
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncDummyWindow();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SyncDummyWindow();
        }

        private static void TerminateProcessAndCleanup(Process p, string path)
        {
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(1500);
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            if (!string.IsNullOrEmpty(path)) ScheduleTempCleanup(path);
        }

        private void KillDummy()
        {
            Process p = dummyProcess;
            string path = dummyExePath;
            dummyProcess = null;
            dummyExePath = null;
            dummyHwnd = IntPtr.Zero;
            TerminateProcessAndCleanup(p, path);
        }

        private void KillAllExtraDummies()
        {
            for (int i = extraInstances.Count - 1; i >= 0; i--)
            {
                ExtraGameInstance inst = extraInstances[i];
                StopExtraDiscordRpc(inst);
                if (inst.Stopwatch != null) inst.Stopwatch.Stop();
                TryRecordExtraSession(inst);
                TerminateProcessAndCleanup(inst.DummyProcess, inst.DummyExePath);
                inst.DummyProcess = null;
                inst.DummyExePath = null;
                inst.DummyHwnd = IntPtr.Zero;
            }
            extraInstances.Clear();
        }

        private void StopExtraSimulation(ExtraGameInstance inst, bool showSaveError)
        {
            if (inst == null) return;
            extraInstances.Remove(inst);
            StopExtraDiscordRpc(inst);
            if (inst.Stopwatch != null) inst.Stopwatch.Stop();
            bool recorded = TryRecordExtraSession(inst);
            if (!recorded && showSaveError)
                MessageBox.Show("Could not save this session. See launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            TerminateProcessAndCleanup(inst.DummyProcess, inst.DummyExePath);
            inst.DummyProcess = null;
            inst.DummyExePath = null;
            inst.DummyHwnd = IntPtr.Zero;
            UpdateMultiInstanceLayout();
            RefreshQueueStrip();
            UpdateTrayContextMenu();
            RefreshSwitcherList();
            SyncActiveQuestsProgress(0);
        }

        private void StopAllSimulations()
        {
            KillAllExtraDummies();
            StopSimulation();
        }

        private void StartRunningState(GameItem game)
        {
            isSimulatingMode = true;
            activeSimulatingGame = game;
            selectedGame = game;
            currentDisplayName = game != null ? game.name : "Game";
            currentExeName = game != null ? game.exe : "game.exe";

            this.Text = currentDisplayName;
            Program.SetWindowText(this.Handle, currentDisplayName);

            if (heroControl != null)
            {
                heroControl.SetGame(game, false);
                heroControl.IsRunning = true;
            }
            switcherControl.Enabled = false;
            CloseInlineSearch();
            RefreshSwitcherList();

            if (btnAction != null)
            {
                btnAction.ActionText = "Stop";
                btnAction.TimerText = "00:00:00";
                btnAction.SetRunningState(true);
            }

            sessionStopwatch = Stopwatch.StartNew();
            hasSaved2MinQualification = false;
            qualificationFinalRecorded = false;

            timerQualification.Start();
            timerUIUpdate.Start();

            StartDiscordRpc(game != null ? game.id : "", currentDisplayName, game != null ? game.icon : "");
            UpdateMultiInstanceLayout();
            RefreshQueueStrip();
            RefreshDiscordStatus();
            UpdateTrayContextMenu();
            SyncActiveQuestsProgress(0);
        }

        private void SyncActiveQuestsProgress(int deltaSeconds)
        {
            List<GameItem> activeGames = new List<GameItem>();
            if (activeSimulatingGame != null) activeGames.Add(activeSimulatingGame);
            for (int i = 0; i < extraInstances.Count; i++)
            {
                if (extraInstances[i] != null && extraInstances[i].Game != null)
                    activeGames.Add(extraInstances[i].Game);
            }
            QuestDatabase.Instance.UpdateActiveSimulationProgress(activeGames, deltaSeconds, DateTimeOffset.UtcNow);
        }

        private void StopSimulation()
        {
            if (isQueueRunning)
            {
                isQueueRunning = false;
                queuePhase = QueuePhase.Idle;
                if (queueBreakStopwatch != null) queueBreakStopwatch.Stop();
                queueBreakStopwatch = null;
                queueBreakTargetSeconds = 0;
                for (int i = 0; i < queueItems.Count; i++)
                {
                    if (queueItems[i].State == QueueItemState.Running) queueItems[i].State = QueueItemState.Pending;
                }
                queueActiveIndex = -1;
            }
            StopDiscordRpc();
            KillDummy();
            if (sessionStopwatch != null) sessionStopwatch.Stop();
            QuestDatabase.Instance.ClearActiveSimulation();
            bool recorded = TryRecordSession();
            if (!recorded)
                MessageBox.Show("Could not save this session. It will be retried before the next launch or exit; see launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (recorded && !string.IsNullOrEmpty(originalLauncherPath) && File.Exists(originalLauncherPath))
            {
                KillAllExtraDummies();
                isSwitchingProcesses = true;
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = originalLauncherPath;
                psi.Arguments = string.Format("--launcher {0} {1}", this.Location.X, this.Location.Y);
                psi.UseShellExecute = true;
                try { Process.Start(psi); } catch { }

                ScheduleTempCleanup(Application.ExecutablePath);
                this.Close();
            }
            else if (extraInstances.Count > 0)
            {
                ExtraGameInstance next = extraInstances[0];
                extraInstances.RemoveAt(0);
                StopExtraDiscordRpc(next);

                activeSimulatingGame = next.Game;
                selectedGame = next.Game;
                currentDisplayName = next.Game != null ? next.Game.name : "Game";
                currentExeName = next.Game != null ? next.Game.exe : "game.exe";
                dummyProcess = next.DummyProcess;
                dummyExePath = next.DummyExePath;
                dummyMode = next.DummyMode;
                dummyHwnd = next.DummyHwnd;
                sessionStopwatch = next.Stopwatch ?? Stopwatch.StartNew();
                qualificationFinalRecorded = next.QualificationFinalRecorded;
                hasSaved2MinQualification = sessionStopwatch.Elapsed.TotalSeconds >= 120;

                this.Text = currentDisplayName;
                Program.SetWindowText(this.Handle, currentDisplayName);
                if (heroControl != null)
                {
                    heroControl.SetGame(next.Game, false);
                    heroControl.IsRunning = true;
                }
                if (btnAction != null)
                {
                    TimeSpan elapsed = sessionStopwatch.Elapsed;
                    btnAction.ActionText = "Stop";
                    btnAction.TimerText = string.Format("{0:00}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
                    btnAction.SetRunningState(true);
                }

                StartDiscordRpc(next.Game != null ? next.Game.id : "", currentDisplayName, next.Game != null ? next.Game.icon : "");
                UpdateMultiInstanceLayout();
                RefreshQueueStrip();
                RefreshDiscordStatus();
                UpdateTrayContextMenu();
                RefreshSwitcherList();
                SyncDummyWindow();
            }
            else
            {
                isSimulatingMode = false;
                CloseExtraGamePicker();
                switcherControl.Enabled = true;
                if (sessionStopwatch != null) sessionStopwatch.Stop();
                timerUIUpdate.Stop();
                timerQualification.Stop();

                this.Text = "Quest Launcher";
                Program.SetWindowText(this.Handle, "Quest Launcher");

                if (heroControl != null) heroControl.IsRunning = false;
                if (btnAction != null)
                {
                    btnAction.ActionText = "Launch";
                    btnAction.TimerText = "";
                    btnAction.SetRunningState(false);
                    UpdateIdleActionButton();
                }

                RefreshQueueStrip();
                UpdateMultiInstanceLayout();
                UpdateTrayContextMenu();
                RefreshSwitcherList();
                RefreshDiscordStatus();
            }
        }

        private bool TryRecordSession()
        {
            if (qualificationFinalRecorded || sessionStopwatch == null || sessionStopwatch.Elapsed.TotalSeconds < 120 || activeSimulatingGame == null) return true;
            qualificationFinalRecorded = GameDatabase.Instance.RecordQualifiedGame(activeSimulatingGame, (int)sessionStopwatch.Elapsed.TotalSeconds);
            return qualificationFinalRecorded;
        }

        private bool TryRecordExtraSession(ExtraGameInstance inst)
        {
            if (inst == null || inst.QualificationFinalRecorded || inst.Stopwatch == null ||
                inst.Stopwatch.Elapsed.TotalSeconds < 120 || inst.Game == null) return true;
            inst.QualificationFinalRecorded = GameDatabase.Instance.RecordQualifiedGame(inst.Game, (int)inst.Stopwatch.Elapsed.TotalSeconds);
            return inst.QualificationFinalRecorded;
        }

        private void OnUITick(object sender, EventArgs e)
        {
            RefreshDiscordStatus();
            if (isSimulatingMode && dummyProcess != null)
            {
                SyncDummyWindow();
                bool dead = false;
                try { dead = dummyProcess.HasExited; } catch { dead = true; }
                if (dead)
                {
                    StopSimulation();
                    return;
                }
            }

            if (isSimulatingMode && extraInstances.Count > 0)
            {
                for (int i = extraInstances.Count - 1; i >= 0; i--)
                {
                    ExtraGameInstance inst = extraInstances[i];
                    if (inst.DummyProcess != null)
                    {
                        bool extraDead = false;
                        try { extraDead = inst.DummyProcess.HasExited; } catch { extraDead = true; }
                        if (extraDead) StopExtraSimulation(inst, false);
                    }
                }
            }

            if (isSimulatingMode && sessionStopwatch != null)
            {
                TimeSpan elapsed = sessionStopwatch.Elapsed;
                string timerStr = string.Format("{0:00}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
                if (btnAction != null)
                {
                    btnAction.TimerText = timerStr;
                }
            }

            if (isQueueRunning && queuePhase == QueuePhase.PlayingGame && isSimulatingMode &&
                sessionStopwatch != null && queueActiveIndex >= 0 && queueActiveIndex < queueItems.Count)
            {
                if (sessionStopwatch.Elapsed.TotalSeconds >= queueItems[queueActiveIndex].PlannedPlaySeconds)
                {
                    AdvanceQueueFromGameComplete();
                    return;
                }
                RefreshQueueStrip();
            }
            else if (isQueueRunning && queuePhase == QueuePhase.BreakCooldown)
            {
                double elapsedBreak = queueBreakStopwatch != null ? queueBreakStopwatch.Elapsed.TotalSeconds : queueBreakTargetSeconds;
                if (elapsedBreak >= queueBreakTargetSeconds)
                {
                    if (queueBreakStopwatch != null) queueBreakStopwatch.Stop();
                    queueBreakStopwatch = null;
                    queueActiveIndex++;
                    if (queueActiveIndex < queueItems.Count)
                    {
                        queuePhase = QueuePhase.PlayingGame;
                        queueItems[queueActiveIndex].State = QueueItemState.Running;
                        GameItem nextGame = queueItems[queueActiveIndex].Game;
                        SelectGame(nextGame);
                        RefreshQueueStrip();
                        UpdateMultiInstanceLayout();
                        UpdateTrayContextMenu();
                        LaunchSimulation(nextGame.exe);
                    }
                    else
                    {
                        StopQueue();
                    }
                    return;
                }
                int rem = Math.Max(0, queueBreakTargetSeconds - (int)Math.Floor(elapsedBreak));
                if (btnAction != null) btnAction.TimerText = string.Format("{0:00}:{1:00}", rem / 60, rem % 60);
                RefreshQueueStrip();
            }

            if (isSimulatingMode && extraStripControl != null && extraStripControl.Visible)
            {
                extraStripControl.RefreshTooltip();
            }
            if (isSimulatingMode && !isQueueRunning && extraInstances.Count > 0 && queueStripControl != null && queueStripControl.Visible)
            {
                queueStripControl.RefreshTooltip();
            }
        }

        private void OnQualificationTick(object sender, EventArgs e)
        {
            if (sessionStopwatch != null && isSimulatingMode && !hasSaved2MinQualification)
            {
                if (sessionStopwatch.Elapsed.TotalSeconds >= 120)
                {
                    hasSaved2MinQualification = true;
                    timerQualification.Stop();
                }
            }
        }

        private static void RunRpcWorkerLoop(DiscordRpcClient client, ManualResetEvent stop, string appName, string iconHash, Action<bool, string> onPoll)
        {
            long startEpoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            while (!stop.WaitOne(0))
            {
                bool accepted = false;
                try
                {
                    if (!client.IsConnected) client.Connect(1000);
                    if (!stop.WaitOne(0) && client.IsConnected)
                        accepted = client.SetActivity(appName, "In Game", iconHash, appName, startEpoch);
                }
                catch (Exception ex) { Program.LogError("RpcWorker", ex); }
                if (onPoll != null) onPoll(accepted, client.LastError);
                if (stop.WaitOne(accepted ? 15000 : 3000)) break;
            }
        }

        private void StartDiscordRpc(string appId, string appName, string iconHash)
        {
            StopDiscordRpc();
            if (string.IsNullOrWhiteSpace(appId))
            {
                rpcStatus = "Discord activity unavailable";
                rpcStatusDetail = "This game has no Discord application ID. Add its ID to enable Rich Presence.";
                return;
            }
            lock (_rpcLock)
            {
                // Attribute activity to the helper while keeping the connection owned by the launcher.
                int activityPid = dummyProcess == null ? Process.GetCurrentProcess().Id : dummyProcess.Id;
                DiscordRpcClient client = new DiscordRpcClient(appId, activityPid);
                ManualResetEvent stop = new ManualResetEvent(false);
                rpcClient = client;
                rpcStatus = "Connecting to Discord...";
                rpcStatusDetail = "Waiting for Discord desktop to acknowledge this activity.";
                rpcStop = stop;
                rpcThread = new Thread(() =>
                {
                    try
                    {
                        RunRpcWorkerLoop(client, stop, appName, iconHash, (accepted, lastError) =>
                        {
                            lock (_rpcLock)
                            {
                                if (!stop.WaitOne(0) && object.ReferenceEquals(rpcClient, client))
                                {
                                    string status = accepted ? "Discord activity acknowledged" : "Discord activity unavailable — retrying";
                                    if (rpcStatus != status) Program.LogError("RpcStatus", new Exception(status));
                                    rpcStatus = status;
                                    rpcStatusDetail = accepted ? "Discord accepted the activity display. Quest credit remains unverified." : lastError ?? "Discord did not acknowledge the activity update.";
                                }
                            }
                        });
                    }
                    finally
                    {
                        client.Dispose();
                        lock (_rpcLock)
                        {
                            if (object.ReferenceEquals(rpcClient, client))
                            {
                                rpcClient = null;
                                rpcThread = null;
                                rpcStop = null;
                            }
                        }
                        stop.Dispose();
                    }
                });
                rpcThread.IsBackground = true;
                rpcThread.Name = "DiscordRpcWorker";
                rpcThread.Start();
            }
        }

        private void StopDiscordRpc()
        {
            Thread worker;
            DiscordRpcClient client;
            lock (_rpcLock)
            {
                if (rpcStop != null) rpcStop.Set();
                worker = rpcThread;
                client = rpcClient;
                rpcThread = null;
                rpcClient = null;
                rpcStop = null;
                rpcStatus = "Activity simulation only.";
                rpcStatusDetail = "Quest progress is not available through Discord Rich Presence.";
            }
            // Close pending pipe reads/connects and wake the delay before joining outside the lock.
            if (client != null) client.Dispose();
            if (worker != null && worker != Thread.CurrentThread && worker.IsAlive) worker.Join(400);
        }

        private void StartExtraDiscordRpc(ExtraGameInstance inst)
        {
            if (inst == null || inst.Game == null || string.IsNullOrWhiteSpace(inst.Game.id)) return;
            StopExtraDiscordRpc(inst);
            lock (inst.RpcLock)
            {
                int activityPid = inst.DummyProcess == null ? Process.GetCurrentProcess().Id : inst.DummyProcess.Id;
                DiscordRpcClient client = new DiscordRpcClient(inst.Game.id, activityPid);
                ManualResetEvent stop = new ManualResetEvent(false);
                inst.RpcClient = client;
                inst.RpcStop = stop;
                string appName = inst.Game.name ?? "Game";
                string iconHash = inst.Game.icon ?? "";
                inst.RpcThread = new Thread(() =>
                {
                    try
                    {
                        RunRpcWorkerLoop(client, stop, appName, iconHash, null);
                    }
                    finally
                    {
                        client.Dispose();
                        lock (inst.RpcLock)
                        {
                            if (object.ReferenceEquals(inst.RpcClient, client))
                            {
                                inst.RpcClient = null;
                                inst.RpcThread = null;
                                inst.RpcStop = null;
                            }
                        }
                        stop.Dispose();
                    }
                });
                inst.RpcThread.IsBackground = true;
                inst.RpcThread.Name = "DiscordRpcExtraWorker";
                inst.RpcThread.Start();
            }
        }

        private void StopExtraDiscordRpc(ExtraGameInstance inst)
        {
            if (inst == null) return;
            Thread worker;
            DiscordRpcClient client;
            lock (inst.RpcLock)
            {
                if (inst.RpcStop != null) inst.RpcStop.Set();
                worker = inst.RpcThread;
                client = inst.RpcClient;
                inst.RpcThread = null;
                inst.RpcClient = null;
                inst.RpcStop = null;
            }
            if (client != null) client.Dispose();
            if (worker != null && worker != Thread.CurrentThread && worker.IsAlive) worker.Join(400);
        }

        private void RefreshDiscordStatus()
        {
            if (lblDiscordStatus == null) return;
            string text;
            string tip;
            if (isQueueRunning && queuePhase == QueuePhase.BreakCooldown)
            {
                string nextName = (queueActiveIndex + 1 < queueItems.Count && queueItems[queueActiveIndex + 1].Game != null)
                    ? queueItems[queueActiveIndex + 1].Game.name : "next game";
                text = "Queue break — next: " + nextName + "\nWaiting before launching next game.";
                tip = "Random cooldown between queued games.";
            }
            else
            {
                text = rpcStatus + "\nLocal timer; quest progress is unverified.";
                tip = rpcStatusDetail;
            }
            if (lblDiscordStatus.Text != text) lblDiscordStatus.Text = text;
            if (globalToolTip.GetToolTip(lblDiscordStatus) != tip)
                globalToolTip.SetToolTip(lblDiscordStatus, tip);
        }

        private void UpdateTrayContextMenu()
        {
            if (trayIcon == null) return;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Theme.BgSurface;
            menu.ForeColor = Theme.TextPrimary;
            menu.ShowImageMargin = false;

            if (isQueueRunning && queuePhase == QueuePhase.BreakCooldown)
            {
                ToolStripMenuItem showItem = new ToolStripMenuItem("Show (Queue Break)", null, (s, e) => RestoreFromTray());
                showItem.Font = FontManager.ButtonFont;
                menu.Items.Add(showItem);
                menu.Items.Add("Stop Queue", null, (s, e) => StopQueue());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => ExitApplication());
            }
            else if (isQueueRunning && isSimulatingMode)
            {
                ToolStripMenuItem showItem = new ToolStripMenuItem("Show " + currentDisplayName + " (Queue)", null, (s, e) => RestoreFromTray());
                showItem.Font = FontManager.ButtonFont;
                menu.Items.Add(showItem);
                menu.Items.Add("Stop Queue", null, (s, e) => StopQueue());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => ExitApplication());
            }
            else if (isSimulatingMode)
            {
                string showLabel = extraInstances.Count > 0
                    ? "Show (" + (1 + extraInstances.Count) + " running)"
                    : "Show " + currentDisplayName;
                ToolStripMenuItem showItem = new ToolStripMenuItem(showLabel, null, (s, e) => RestoreFromTray());
                showItem.Font = FontManager.ButtonFont;
                menu.Items.Add(showItem);
                menu.Items.Add("Stop " + currentDisplayName, null, (s, e) => StopSimulation());
                for (int i = 0; i < extraInstances.Count; i++)
                {
                    ExtraGameInstance extra = extraInstances[i];
                    string extraName = extra.Game != null && !string.IsNullOrEmpty(extra.Game.name) ? extra.Game.name : "Game";
                    menu.Items.Add("Stop " + extraName, null, (s, e) => StopExtraSimulation(extra, true));
                }
                if (extraInstances.Count > 0)
                {
                    menu.Items.Add("Stop All", null, (s, e) => StopAllSimulations());
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => ExitApplication());
            }
            else
            {
                ToolStripMenuItem showItem = new ToolStripMenuItem("Show", null, (s, e) => RestoreFromTray());
                showItem.Font = FontManager.ButtonFont;
                menu.Items.Add(showItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => ExitApplication());
            }
            ContextMenuStrip previous = trayIcon.ContextMenuStrip;
            trayIcon.ContextMenuStrip = menu;
            if (previous != null) previous.Dispose();
        }

        public void MinimizeToTray()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(MinimizeToTray));
                return;
            }
            this.Hide();
        }

        public void RestoreFromTray()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(RestoreFromTray));
                return;
            }
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.WindowState = FormWindowState.Normal;
            }
            this.Show();
            Program.ShowWindow(this.Handle, 9);
            this.BringToFront();
            this.Activate();
            Program.SetForegroundWindow(this.Handle);
            SyncDummyWindow();
            Invalidate(true);
        }

        private bool isExplicitExit = false;

        public void ExitApplication()
        {
            StopDiscordRpc();
            isExplicitExit = true;
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!isExplicitExit && !isSwitchingProcesses && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }

            base.OnFormClosing(e);
            if (e.Cancel) return;
            if (!TryRecordSession() && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                MessageBox.Show("Could not save this session. Resolve the save error before exiting; see launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (sessionStopwatch != null) sessionStopwatch.Stop();
            if (queueBreakStopwatch != null) queueBreakStopwatch.Stop();
            isQueueRunning = false;
            QuestDatabase.Instance.ClearActiveSimulation();
            StopDiscordRpc();
            KillDummy();
            KillAllExtraDummies();
            GameDatabase.Instance.OnDatabaseUpdated -= OnDatabaseLoaded;
            GameDatabase.Instance.OnQualifiedGamesChanged -= OnQualifiedGamesUpdated;
            GameDatabase.Instance.OnInitializationCompleted -= OnDatabaseLoaded;
            QuestDatabase.Instance.OnQuestsUpdated -= OnQuestsCatalogUpdated;
            if (questsPanel != null && !questsPanel.IsDisposed) { questsPanel.Dispose(); questsPanel = null; }

            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            if (timerQualification != null) { timerQualification.Stop(); timerQualification.Dispose(); }
            if (timerUIUpdate != null) { timerUIUpdate.Stop(); timerUIUpdate.Dispose(); }
            if (timerHoverCheck != null) { timerHoverCheck.Stop(); timerHoverCheck.Dispose(); }
            if (debounceSearchTimer != null) { debounceSearchTimer.Stop(); debounceSearchTimer.Dispose(); }
            if (debounceExtraSearchTimer != null) { debounceExtraSearchTimer.Stop(); debounceExtraSearchTimer.Dispose(); }

            if (isSimulatingMode && !isSwitchingProcesses)
            {
                ScheduleTempCleanup(Application.ExecutablePath);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Application's filter list holds a strong ref to us; drop it or the form leaks.
                RemoveOutsideClickFilter();
                GameDatabase.Instance.OnDatabaseUpdated -= OnDatabaseLoaded;
                GameDatabase.Instance.OnQualifiedGamesChanged -= OnQualifiedGamesUpdated;
                GameDatabase.Instance.OnInitializationCompleted -= OnDatabaseLoaded;
                QuestDatabase.Instance.OnQuestsUpdated -= OnQuestsCatalogUpdated;
                if (questsPanel != null && !questsPanel.IsDisposed) { questsPanel.Dispose(); questsPanel = null; }
                if (globalToolTip != null) globalToolTip.Dispose();
                if (trayIcon != null && trayIcon.ContextMenuStrip != null) trayIcon.ContextMenuStrip.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnDatabaseLoaded()
        {
            if (this.IsHandleCreated)
            {
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        RefreshSwitcherList();
                        TryLaunchStartupGame();
                    }));
                }
                catch { }
            }
        }

        private void OnQualifiedGamesUpdated()
        {
            if (this.IsHandleCreated)
            {
                try { this.BeginInvoke(new Action(RefreshSwitcherList)); } catch { }
            }
        }

        private void OnQuestsCatalogUpdated()
        {
            int active = QuestDatabase.Instance.GetActiveCount(DateTimeOffset.UtcNow);
            if (this.IsHandleCreated && !this.IsDisposed)
            {
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        _activeQuestCount = active;
                        Invalidate(QuestsBtnRect);
                    }));
                }
                catch { }
            }
        }

        private void ToggleMainMaximize()
        {
            if (!isQuestsDocked) return;
            this.WindowState = (this.WindowState == FormWindowState.Maximized)
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        protected virtual void OpenQuestsWindow()
        {
            if (isQuestsDocked)
            {
                if (this.WindowState == FormWindowState.Maximized)
                    this.WindowState = FormWindowState.Normal;
                if (this.ClientSize.Width > 420)
                    lastExpandedSize = this.ClientSize;
                isQuestsDocked = false;
                if (questsPanel != null) questsPanel.Visible = false;
                this.MinimumSize = new Size(420, 480);
                this.MaximumSize = new Size(420, 480);
                this.ClientSize = new Size(420, 480);
                UpdateWindowRegion();
                Invalidate(true);
                return;
            }

            QuestDatabase.Instance.Initialize();
            if (questsPanel == null || questsPanel.IsDisposed)
            {
                questsPanel = new QuestsPanel();
                questsPanel.ShowOuterBorder = false;
                questsPanel.SimulateRequested += OnQuestSimulateRequested;
                questsPanel.StopSimulateRequested += OnQuestStopRequested;
                questsPanel.QueueRequested += OnQuestQueueRequested;
                questsPanel.DragRequested += () => Program.DragWindow(this.Handle);
                questsPanel.MinimizeRequested += () => { this.WindowState = FormWindowState.Minimized; };
                questsPanel.MaximizeToggleRequested += ToggleMainMaximize;
                questsPanel.CloseRequested += () => { if (isQuestsDocked) OpenQuestsWindow(); };
                this.Controls.Add(questsPanel);
            }

            Rectangle work = Screen.FromControl(this).WorkingArea;
            int minW = Math.Min(1140, Math.Max(720, work.Width - 16));
            int targetW = Math.Min(work.Width - 16, Math.Max(minW, lastExpandedSize.Width));
            int targetH = Math.Min(work.Height - 16, Math.Max(540, lastExpandedSize.Height));

            isQuestsDocked = true;
            this.MaximumSize = Size.Empty;
            this.MinimumSize = new Size(minW, Math.Min(520, work.Height - 16));
            this.ClientSize = new Size(targetW, targetH);

            int nx = this.Location.X;
            int ny = this.Location.Y;
            if (nx + this.Width > work.Right) nx = Math.Max(work.Left, work.Right - this.Width);
            if (ny + this.Height > work.Bottom) ny = Math.Max(work.Top, work.Bottom - this.Height);
            if (nx < work.Left) nx = work.Left;
            if (ny < work.Top) ny = work.Top;
            if (nx != this.Location.X || ny != this.Location.Y)
                this.Location = new Point(nx, ny);

            questsPanel.Bounds = new Rectangle(421, 1, Math.Max(200, this.ClientSize.Width - 422), Math.Max(100, this.ClientSize.Height - 2));
            questsPanel.Visible = true;
            questsPanel.BringToFront();
            questsPanel.RefreshView(false);
            UpdateWindowRegion();
            Invalidate(true);
        }

        private void OnQuestSimulateRequested(GameItem game)
        {
            if (game == null) return;
            GameItem resolved = GameDatabase.Instance.ResolveGameMetadata(game) ?? game;
            if (isSimulatingMode || isLaunching)
            {
                AddGameToQueue(resolved);
                RestoreFromTray();
                return;
            }
            SelectGame(resolved);
            RestoreFromTray();
            LaunchSimulation(resolved.exe);
        }

        private void OnQuestQueueRequested(GameItem game)
        {
            if (game == null) return;
            GameItem resolved = GameDatabase.Instance.ResolveGameMetadata(game) ?? game;
            AddGameToQueue(resolved);
            RestoreFromTray();
        }

        private void OnQuestStopRequested(GameItem game)
        {
            if (game == null) return;
            GameItem resolved = GameDatabase.Instance.ResolveGameMetadata(game) ?? game;
            if (isSimulatingMode && activeSimulatingGame != null &&
                (IsSameGameIdentity(activeSimulatingGame, resolved) ||
                 (!string.IsNullOrEmpty(resolved.id) && string.Equals(activeSimulatingGame.id, resolved.id, StringComparison.Ordinal))))
            {
                StopSimulation();
                return;
            }
            for (int i = 0; i < extraInstances.Count; i++)
            {
                ExtraGameInstance inst = extraInstances[i];
                if (inst != null && inst.Game != null &&
                    (IsSameGameIdentity(inst.Game, resolved) ||
                     (!string.IsNullOrEmpty(resolved.id) && string.Equals(inst.Game.id, resolved.id, StringComparison.Ordinal))))
                {
                    StopExtraSimulation(inst, true);
                    return;
                }
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00C00000; // WS_CAPTION
                cp.Style &= ~0x00040000; // WS_THICKFRAME
                // The taskbar only minimizes the active window on click if it has WS_MINIMIZEBOX
                // (the shell checks the style before sending SC_MINIMIZE). WS_SYSMENU is needed for that bit to count.
                // With WS_CAPTION stripped, neither paints anything; they only re-enable the shell behaviour
                // (taskbar click-to-minimize, Win+Down, Alt+Space menu) that ControlBox/MinimizeBox=false removed.
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX
                cp.Style |= 0x00010000; // WS_MAXIMIZEBOX
                cp.Style |= 0x00080000; // WS_SYSMENU
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (isQuestsDocked && QuestsForm.HandleFormResizeHitTest(this, ref m)) return;
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && isQuestsDocked && questsPanel != null && questsPanel.Visible && questsPanel.HandleEscape())
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Program.EnableImmersiveDarkMode(this.Handle);
            if (!Program.TryEnableDwmRounding(this.Handle))
            {
                // Windows 10 / older: fall back to a clipped (non-anti-aliased) rounded Region
                Program.WindowRadius = 16;
                using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(Point.Empty, this.ClientSize), 16))
                    this.Region = new Region(path);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            hasBeenShown = true;
            string title = isSimulatingMode ? currentDisplayName : "Quest Launcher";
            this.Text = title;
            Program.SetWindowText(this.Handle, title);

            if (GameDatabase.Instance.Count > 0) RefreshSwitcherList();
            if (!isSimulatingMode) QuestDatabase.Instance.Initialize();

            this.BeginInvoke(new Action(TryLaunchStartupGame));
        }

        private void TryLaunchStartupGame()
        {
            if (!hasBeenShown || string.IsNullOrEmpty(startupGameArg) || isLaunching || isSimulatingMode) return;
            if (!GameDatabase.Instance.IsInitialized)
            {
                btnAction.ActionText = "Loading...";
                return;
            }
            string arg = startupGameArg;
            startupGameArg = null;
            btnAction.ActionText = "Launch";
            GameItem found = GameDatabase.Instance.FindExactGame(arg);
            if (found == null)
            {
                MessageBox.Show("Could not find \"" + arg + "\" in the game database. Search for it or add a custom game before launching.",
                    "Game not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectGame(found);
            LaunchSimulation(found.exe);
        }

        #region Window Hover Controls, Dragging & Footer

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool inWin = ClientRectangle.Contains(e.Location);
            if (inWin != _isMouseInWindow)
            {
                _isMouseInWindow = inWin;
                Invalidate();
            }

            bool hClose = !isQuestsDocked && CloseRect.Contains(e.Location);
            bool hMin = !isQuestsDocked && MinimizeRect.Contains(e.Location);
            bool hAdd = AddBtnRect.Contains(e.Location);
            bool hQuests = QuestsBtnRect.Contains(e.Location);

            if (hClose != _isHoveringClose || hMin != _isHoveringMinimize || hAdd != _isHoveringAddBtn || hQuests != _isHoveringQuestsBtn)
            {
                _isHoveringClose = hClose;
                _isHoveringMinimize = hMin;
                _isHoveringAddBtn = hAdd;
                _isHoveringQuestsBtn = hQuests;

                if (hClose || hMin || hAdd || hQuests)
                {
                    this.Cursor = Cursors.Hand;
                    if (hAdd && globalToolTip != null) globalToolTip.SetToolTip(this, "Add Game");
                    else if (hQuests && globalToolTip != null) globalToolTip.SetToolTip(this, "Browse Discord Quests");
                    else if (globalToolTip != null) globalToolTip.SetToolTip(this, "");
                }
                else
                {
                    this.Cursor = Cursors.Default;
                    if (globalToolTip != null) globalToolTip.SetToolTip(this, "");
                }
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Point pt = this.PointToClient(Cursor.Position);
            if (!this.ClientRectangle.Contains(pt))
            {
                _isMouseInWindow = false;
                _isHoveringClose = false;
                _isHoveringMinimize = false;
                _isHoveringAddBtn = false;
                _isHoveringQuestsBtn = false;
                this.Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                // Top-right window controls (visible on window hover in compact mode)
                if (!isQuestsDocked && CloseRect.Contains(e.Location))
                {
                    MinimizeToTray();
                    return;
                }

                if (!isQuestsDocked && MinimizeRect.Contains(e.Location))
                {
                    this.WindowState = FormWindowState.Minimized;
                    return;
                }

                // Footer Buttons
                if (QuestsBtnRect.Contains(e.Location))
                {
                    OpenQuestsWindow();
                    return;
                }

                if (AddBtnRect.Contains(e.Location))
                {
                    ShowAddCustomGameDialog("");
                    return;
                }

                // Drag window by empty areas (search light-dismiss is handled in PreFilterMessage)
                Program.DragWindow(this.Handle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Subtle 1px window border inside the 16px rounded radius
            if (this.WindowState != FormWindowState.Maximized)
            {
                using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Program.WindowRadius))
                {
                    using (Pen borderPen = new Pen(Theme.BorderSubtle, 1f))
                    {
                        g.DrawPath(borderPen, path);
                    }
                }
            }

            if (isQuestsDocked)
            {
                using (Pen sep = new Pen(Theme.BorderSubtle, 1f))
                {
                    g.DrawLine(sep, 420, 1, 420, Height - 2);
                }
            }

            // Top-right window controls: Close and minimize appear as two tiny icons, ONLY visible on window hover in compact mode
            if (_isMouseInWindow && !isQuestsDocked)
            {
                // Minimize (-)
                Color minColor = _isHoveringMinimize ? Theme.TextPrimary : Theme.TextMuted;
                using (Pen minPen = new Pen(minColor, 1.6f))
                {
                    g.DrawLine(minPen, MinimizeRect.X + 4, MinimizeRect.Y + 9, MinimizeRect.X + 14, MinimizeRect.Y + 9);
                }

                // Close (X)
                Color closeColor = _isHoveringClose ? Color.FromArgb(255, 100, 100) : Theme.TextMuted;
                using (Pen closePen = new Pen(closeColor, 1.6f))
                {
                    g.DrawLine(closePen, CloseRect.X + 4, CloseRect.Y + 4, CloseRect.X + 14, CloseRect.Y + 14);
                    g.DrawLine(closePen, CloseRect.X + 14, CloseRect.Y + 4, CloseRect.X + 4, CloseRect.Y + 14);
                }
            }

            // Top-left header button: Quests Explorer
            string qLabel = _activeQuestCount > 0 ? ("Quests \u00B7 " + _activeQuestCount) : "Quests";
            Color qFill = isQuestsDocked ? Color.FromArgb(46, 36, 92) : (_isHoveringQuestsBtn ? Theme.BgCardHover : Theme.BgSurface);
            Color qBorder = (isQuestsDocked || _isHoveringQuestsBtn) ? Theme.AccentViolet : Theme.BorderSubtle;
            Color qText = (isQuestsDocked || _isHoveringQuestsBtn) ? Theme.TextPrimary : Theme.TextMuted;
            QuestGridControl.DrawPill(g, QuestsBtnRect, qFill, qBorder, qLabel, qText, FontManager.BadgeFont, 8);

            // Footer button: Add custom game (+)
            Color addColor = _isHoveringAddBtn ? Theme.TextPrimary : Theme.TextMuted;
            using (Pen addPen = new Pen(addColor, 1.8f))
            {
                int ax = AddBtnRect.X + 3;
                int ay = AddBtnRect.Y + 3;
                g.DrawLine(addPen, ax + 7, ay + 1, ax + 7, ay + 13);
                g.DrawLine(addPen, ax + 1, ay + 7, ax + 13, ay + 7);
            }
        }

        #endregion

        #region Helpers & Custom Game Dialog

        protected virtual void ShowAddCustomGameDialog(string initialQuery)
        {
            if (isSimulatingMode || isLaunching) return;
            using (Form dialog = new Form())
            {
                dialog.Text = "Add Game";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(340, 240);
                dialog.BackColor = Theme.BgBase;
                dialog.ForeColor = Theme.TextPrimary;
                Program.EnableImmersiveDarkMode(dialog.Handle);

                Label lblName = new Label();
                lblName.Text = "Name";
                lblName.Font = FontManager.SmallFont;
                lblName.ForeColor = Theme.TextMuted;
                lblName.Location = new Point(20, 16);
                lblName.Size = new Size(300, 16);
                dialog.Controls.Add(lblName);

                TextBox txtName = new TextBox();
                txtName.Location = new Point(20, 36);
                txtName.Size = new Size(300, 24);
                txtName.Font = FontManager.SearchFont;
                txtName.BackColor = Theme.BgInput;
                txtName.ForeColor = Theme.TextPrimary;
                txtName.BorderStyle = BorderStyle.FixedSingle;
                txtName.Text = initialQuery != null ? initialQuery.Trim() : "";
                dialog.Controls.Add(txtName);

                Label lblExe = new Label();
                lblExe.Text = "Executable";
                lblExe.Font = FontManager.SmallFont;
                lblExe.ForeColor = Theme.TextMuted;
                lblExe.Location = new Point(20, 72);
                lblExe.Size = new Size(300, 16);
                dialog.Controls.Add(lblExe);

                TextBox txtExe = new TextBox();
                txtExe.Location = new Point(20, 92);
                txtExe.Size = new Size(300, 24);
                txtExe.Font = FontManager.ExeFont;
                txtExe.BackColor = Theme.BgInput;
                txtExe.ForeColor = Theme.StatusGreen;
                txtExe.BorderStyle = BorderStyle.FixedSingle;
                txtExe.Text = GameDatabase.InferExecutableFromName(txtName.Text);
                dialog.Controls.Add(txtExe);

                bool exeManuallyEdited = false;
                bool suppressExeAutoUpdate = false;
                txtExe.TextChanged += (s, e) => { if (!suppressExeAutoUpdate) exeManuallyEdited = true; };
                txtName.TextChanged += (s, e) =>
                {
                    if (!exeManuallyEdited)
                    {
                        suppressExeAutoUpdate = true;
                        txtExe.Text = GameDatabase.InferExecutableFromName(txtName.Text);
                        suppressExeAutoUpdate = false;
                    }
                };

                Label lblId = new Label();
                lblId.Text = "App ID (Optional)";
                lblId.Font = FontManager.SmallFont;
                lblId.ForeColor = Theme.TextMuted;
                lblId.Location = new Point(20, 128);
                lblId.Size = new Size(300, 16);
                dialog.Controls.Add(lblId);

                TextBox txtId = new TextBox();
                txtId.Location = new Point(20, 148);
                txtId.Size = new Size(300, 24);
                txtId.Font = FontManager.SearchFont;
                txtId.BackColor = Theme.BgInput;
                txtId.ForeColor = Theme.TextPrimary;
                txtId.BorderStyle = BorderStyle.FixedSingle;
                dialog.Controls.Add(txtId);

                PillActionButton btnSave = new PillActionButton();
                btnSave.Text = "Save";
                btnSave.ActionText = "Save";
                btnSave.Location = new Point(20, 186);
                btnSave.Size = new Size(300, 38);
                btnSave.ActionClicked += (s, e) =>
                {
                    string gName = txtName.Text.Trim();
                    string gExe = txtExe.Text.Trim();
                    string gId = txtId.Text.Trim();

                    if (string.IsNullOrEmpty(gName)) return;
                    if (string.IsNullOrEmpty(gExe)) gExe = GameDatabase.InferExecutableFromName(gName);
                    if (!gExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) gExe += ".exe";

                    GameItem custom = new GameItem
                    {
                        name = gName,
                        exe = gExe,
                        id = gId,
                        icon = ""
                    };

                    if (!GameDatabase.Instance.AddCustomGame(custom))
                    {
                        MessageBox.Show(dialog, "Could not save the custom game. See launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    SelectGame(custom);
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };
                dialog.Controls.Add(btnSave);

                dialog.ShowDialog(this);
            }
        }

        public static void ScheduleTempCleanup(string exePath)
        {
            try
            {
                if (!IsOwnedHelperPath(exePath)) return;
                string encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(exePath)));
                string encodedRoot = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    GetOwnedHelperDirectory(exePath)));
                string script = "$target=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPath + "')); " +
                    "$root=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedRoot + "')); " +
                    "$target=[IO.Path]::GetFullPath($target); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){exit}; " +
                    "for($attempt=0;$attempt -lt 10;$attempt++){Start-Sleep -Milliseconds 1000; " +
                    "try { if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Force -ErrorAction Stop}; " +
                    "$parent=[IO.Path]::GetDirectoryName($target); " +
                    "while([string]::Equals($parent,$root,[StringComparison]::OrdinalIgnoreCase) -or $parent.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){" +
                    "if([IO.Directory]::Exists($parent)){[IO.Directory]::Delete($parent,$false)}; if([string]::Equals($parent,$root,[StringComparison]::OrdinalIgnoreCase)){break}; " +
                    "$parent=[IO.Path]::GetDirectoryName($parent)}; break } catch {}}";
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex) { Program.LogError("HelperCleanup", ex); }
        }

        public static bool IsOwnedHelperPath(string exePath)
        {
            return GetOwnedHelperDirectory(exePath) != null;
        }

        private static string GetOwnedHelperDirectory(string exePath)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DiscordDummyTools")) + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(exePath);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
                string[] parts = path.Substring(root.Length).Split(Path.DirectorySeparatorChar);
                Guid session;
                if (parts.Length < 2 || !Guid.TryParseExact(parts[0], "N", out session)) return null;
                return Path.Combine(root, parts[0]);
            }
            catch { return null; }
        }

        public static string FormatDisplayName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Game";
            string name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            Match mFc = Regex.Match(name, @"^(?:ea\s*sports\s*)?fc\s*(\d+)$", RegexOptions.IgnoreCase);
            if (mFc.Success) return "EA Sports FC " + mFc.Groups[1].Value;
            Match mFifa = Regex.Match(name, @"^fifa\s*(\d+)$", RegexOptions.IgnoreCase);
            if (mFifa.Success) return "FIFA " + mFifa.Groups[1].Value;
            Match mUfc = Regex.Match(name, @"^(?:ea\s*sports\s*)?ufc\s*(\d+)$", RegexOptions.IgnoreCase);
            if (mUfc.Success) return "EA Sports UFC " + mUfc.Groups[1].Value;

            if (name.IndexOf("where winds meet", StringComparison.OrdinalIgnoreCase) >= 0) return "Where Winds Meet";
            if (name.IndexOf("wuthering waves", StringComparison.OrdinalIgnoreCase) >= 0) return "Wuthering Waves";
            if (name.IndexOf("starrail", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("honkai", StringComparison.OrdinalIgnoreCase) >= 0) return "Honkai: Star Rail";
            if (name.IndexOf("genshin", StringComparison.OrdinalIgnoreCase) >= 0) return "Genshin Impact";
            if (name.IndexOf("zenless", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(name, @"\bzzz\b", RegexOptions.IgnoreCase)) return "Zenless Zone Zero";
            if (name.IndexOf("warframe", StringComparison.OrdinalIgnoreCase) >= 0) return "Warframe";

            string cleaned = name.Replace('_', ' ').Replace('-', ' ').Trim();
            try
            {
                System.Globalization.TextInfo ti = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
                return ti.ToTitleCase(cleaned.ToLowerInvariant());
            }
            catch
            {
                return cleaned;
            }
        }

        private static Icon CreateAppIcon()
        {
            try
            {
                // Embedded so copied executables retain the window and tray icon.
                using (Stream stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("AppIcon.ico"))
                {
                    if (stream == null) return SystemIcons.Application;
                    using (Icon icon = new Icon(stream, new Size(32, 32)))
                        return (Icon)icon.Clone();
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        #endregion
    }

    #endregion
}
