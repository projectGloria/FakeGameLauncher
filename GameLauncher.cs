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
        public const string BuildId = "v17-window-lifecycle";
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

        public static IntPtr FindProcessWindow(int processId)
        {
            // Process.MainWindowHandle excludes tool windows and hidden windows.
            IntPtr result = IntPtr.Zero;
            EnumWindows((window, parameter) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)processId || GetWindowTextLength(window) == 0) return true;
                result = window;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

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
                this.ClientSize = new Size(360, 480);
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

        public GameItem ResolveGameMetadata(GameItem game)
        {
            if (game == null || string.IsNullOrEmpty(game.id)) return game;
            lock (_lock)
            {
                // History and last selection can predate a database refresh. Resolve by ID, never title.
                GameItem current = _games.Find(x => x.id == game.id);
                return current ?? game;
            }
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

        private static byte[] DownloadBytes(string url, int minBytes)
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

        private static string DictString(Dictionary<string, object> dict, string key)
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

        private static Dictionary<string, object> DictObject(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key] as Dictionary<string, object>;
        }

        private static object[] DictArray(Dictionary<string, object> dict, string key)
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
            int parentWidth = (this.Parent != null) ? this.Parent.ClientSize.Width : 360;
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
            this.Size = new Size(360, 180);

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
            this.Size = new Size(360, 40);
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
        private readonly int ItemHeight = 44;
        private readonly Action<Image> _iconLoaded;

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

                // 28px game icon
                Image iconImg = IconCache.Instance.GetIcon(item, 28, 6, _iconLoaded);
                if (iconImg != null)
                {
                    g.DrawImage(iconImg, new Rectangle(10, itemY + 8, 28, 28), 0, 0, iconImg.Width, iconImg.Height, GraphicsUnit.Pixel);
                }

                // Title (13px)
                Rectangle textRect = new Rectangle(46, itemY + 6, Width - 62, 18);
                TextRenderer.DrawText(g, item.name, FontManager.SmallFont, textRect, Theme.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // Exe (10px monospace)
                Rectangle exeRect = new Rectangle(46, itemY + 24, Width - 62, 14);
                TextRenderer.DrawText(g, item.exe, FontManager.ExeFont, exeRect, Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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

    #region Main Form

    public class MainForm : Form
    {
        private string originalLauncherPath = "";
        private string currentExeName = "";
        private string currentDisplayName = "";
        private bool isSimulatingMode = false;
        private bool isSwitchingProcesses = false;
        private bool minimizeToTrayEnabled = true;

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
        private bool _isHoveringTrayBtn = false;
        private bool _isHoveringAddBtn = false;

        private readonly Rectangle CloseRect = new Rectangle(330, 12, 18, 18);
        private readonly Rectangle MinimizeRect = new Rectangle(304, 12, 18, 18);
        private readonly Rectangle TrayBtnRect = new Rectangle(32, 436, 22, 22);
        private readonly Rectangle AddBtnRect = new Rectangle(306, 436, 22, 22);

        private Process dummyProcess;
        private string dummyExePath;
        private string dummyMode = "hidden";
        private IntPtr dummyHwnd = IntPtr.Zero;

        private GameItem selectedGame;
        private GameItem activeSimulatingGame;
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
                if (args.Length >= 6)
                {
                    minimizeToTrayEnabled = (args[5] == "1");
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
            // Fixed size 360x480, non-resizable, frameless with no visible title bar
            this.ClientSize = new Size(360, 480);
            this.MinimumSize = new Size(360, 480);
            this.MaximumSize = new Size(360, 480);
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
                if (dummyProcess != null && dummyHwnd == IntPtr.Zero) SyncDummyWindow();
                if (!Visible || WindowState == FormWindowState.Minimized) return;
                Point pt = this.PointToClient(Cursor.Position);
                bool inWin = this.ClientRectangle.Contains(pt);
                if (inWin != _isMouseInWindow)
                {
                    _isMouseInWindow = inWin;
                    Invalidate(new Rectangle(300, 10, 52, 24));
                }
            };
            timerHoverCheck.Start();

            debounceSearchTimer = new System.Windows.Forms.Timer();
            debounceSearchTimer.Interval = 120;
            debounceSearchTimer.Tick += OnDebounceSearchTick;

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
            heroControl.Size = new Size(360, 180);
            heroControl.SetGame(initialGame, false);
            heroControl.NameClicked += (s, e) => OpenInlineSearch();
            heroControl.EmptyMouseDown += (s, e) => Program.DragWindow(this.Handle);
            this.Controls.Add(heroControl);

            // 2. Switcher: row of 32px icons, 8px gap, centered
            switcherControl = new GameSwitcherControl(globalToolTip);
            switcherControl.Location = new Point(0, 244);
            switcherControl.Size = new Size(360, 40);
            switcherControl.GameSelected += (game) => SelectGame(game);
            switcherControl.EmptyMouseDown += (s, e) => Program.DragWindow(this.Handle);
            this.Controls.Add(switcherControl);
            RefreshSwitcherList();

            // 3. Action: sleek compact pill button (148x36 idle, 196x36 running)
            btnAction = new PillActionButton();
            btnAction.ActionText = "Launch";
            btnAction.ActionClicked += OnActionButtonClicked;
            this.Controls.Add(btnAction);
            btnAction.SetRunningState(false);
            globalToolTip.SetToolTip(btnAction, "The timer measures this local session, not Discord quest progress.");

            lblDiscordStatus = new Label();
            lblDiscordStatus.Location = new Point(16, 383);
            lblDiscordStatus.Size = new Size(328, 44);
            lblDiscordStatus.Font = FontManager.SmallFont;
            lblDiscordStatus.ForeColor = Theme.TextMuted;
            lblDiscordStatus.TextAlign = ContentAlignment.TopCenter;
            this.Controls.Add(lblDiscordStatus);
            RefreshDiscordStatus();

            // Floating inline search overlay (positioned exactly where the hero name is)
            pnlInlineSearch = new Panel();
            pnlInlineSearch.Location = new Point(48, 150);
            pnlInlineSearch.Size = new Size(264, 34);
            pnlInlineSearch.BackColor = Theme.BgInput;
            pnlInlineSearch.Visible = false;

            txtInlineSearch = new TextBox();
            txtInlineSearch.BorderStyle = BorderStyle.None;
            txtInlineSearch.BackColor = Theme.BgInput;
            txtInlineSearch.ForeColor = Theme.TextPrimary;
            txtInlineSearch.Font = FontManager.SearchFont;
            txtInlineSearch.Location = new Point(10, 8);
            txtInlineSearch.Size = new Size(244, 20);
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
            searchDropdown.Location = new Point(36, 188);
            searchDropdown.Size = new Size(288, 180);
            searchDropdown.Visible = false;
            searchDropdown.ItemSelected += (game) =>
            {
                if (EnsureSearchResultsCurrent()) return;
                AcceptSearchSelection(game);
            };
            this.Controls.Add(searchDropdown);
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
        }

        private void CloseInlineSearch()
        {
            debounceSearchTimer.Stop();
            pnlInlineSearch.Visible = false;
            searchDropdown.Visible = false;
            heroControl.IsSearchActive = false;
            this.ActiveControl = btnAction;
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

        private void OnActionButtonClicked(object sender, EventArgs e)
        {
            if (isSimulatingMode)
            {
                StopSimulation();
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

            if (mode != "classic")
            {
                // Copying and starting the helper may trigger antivirus scans.
                // runs off the UI thread so the window never freezes or goes blank.
                BeginAsyncLaunch(targetExePath, currentExePath, displayName, exeName, iconId, iconHash, mode);
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

            int trayFlag = minimizeToTrayEnabled ? 1 : 0;
            ProcessStartInfo cpsi = new ProcessStartInfo();
            cpsi.FileName = targetExePath;
            cpsi.Arguments = string.Format("--simulating \"{0}\" \"{1}\" {2} {3} {4} \"{5}\" \"{6}\" \"{7}\"",
                displayName, currentExePath, this.Location.X, this.Location.Y, trayFlag, exeName, iconId, iconHash);
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

        // Keep the real (visible) game window exactly underneath this launcher window.
        private void SyncDummyWindow()
        {
            if (dummyProcess == null || !DummyIsVisibleMode() || !this.IsHandleCreated) return;
            try
            {
                if (dummyHwnd == IntPtr.Zero)
                {
                    dummyProcess.Refresh();
                    dummyHwnd = dummyProcess.MainWindowHandle;
                    if (dummyHwnd == IntPtr.Zero) dummyHwnd = Program.FindProcessWindow(dummyProcess.Id);
                    if (dummyHwnd == IntPtr.Zero) return;
                }
                bool visible = this.Visible && this.WindowState != FormWindowState.Minimized;
                if (!visible)
                {
                    if (Program.IsWindowVisible(dummyHwnd)) Program.ShowWindow(dummyHwnd, 0);
                    return;
                }
                if (!Program.IsWindowVisible(dummyHwnd) || Program.IsIconic(dummyHwnd))
                    Program.ShowWindow(dummyHwnd, 4); // SW_SHOWNOACTIVATE
                Program.PlaceBelow(dummyHwnd, this.Handle, this.Location.X, this.Location.Y);
            }
            catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncDummyWindow();
            if (WindowState != FormWindowState.Minimized) Invalidate(true);
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

        private void KillDummy()
        {
            Process p = dummyProcess;
            string path = dummyExePath;
            dummyProcess = null;
            dummyExePath = null;
            dummyHwnd = IntPtr.Zero;

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
            RefreshDiscordStatus();
            UpdateTrayContextMenu();
        }

        private void StopSimulation()
        {
            StopDiscordRpc();
            KillDummy();
            if (sessionStopwatch != null) sessionStopwatch.Stop();
            bool recorded = TryRecordSession();
            if (!recorded)
                MessageBox.Show("Could not save this session. It will be retried before the next launch or exit; see launcher.log for details.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (recorded && !string.IsNullOrEmpty(originalLauncherPath) && File.Exists(originalLauncherPath))
            {
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
            else
            {
                isSimulatingMode = false;
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
                }

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

            if (isSimulatingMode && sessionStopwatch != null)
            {
                TimeSpan elapsed = sessionStopwatch.Elapsed;
                string timerStr = string.Format("{0:00}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
                if (btnAction != null)
                {
                    btnAction.TimerText = timerStr;
                }
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
                    long startEpoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    try
                    {
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
                            lock (_rpcLock)
                            {
                                if (!stop.WaitOne(0) && object.ReferenceEquals(rpcClient, client))
                                {
                                    string status = accepted ? "Discord activity acknowledged" : "Discord activity unavailable — retrying";
                                    if (rpcStatus != status) Program.LogError("RpcStatus", new Exception(status));
                                    rpcStatus = status;
                                    rpcStatusDetail = accepted ? "Discord accepted the activity display. Quest credit remains unverified." : client.LastError ?? "Discord did not acknowledge the activity update.";
                                }
                            }
                            if (stop.WaitOne(accepted ? 15000 : 3000)) break;
                        }
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

        private void RefreshDiscordStatus()
        {
            if (lblDiscordStatus == null) return;
            string text = rpcStatus + "\nLocal timer; quest progress is unverified.";
            if (lblDiscordStatus.Text != text) lblDiscordStatus.Text = text;
            if (globalToolTip.GetToolTip(lblDiscordStatus) != rpcStatusDetail)
                globalToolTip.SetToolTip(lblDiscordStatus, rpcStatusDetail);
        }

        private void UpdateTrayContextMenu()
        {
            if (trayIcon == null) return;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Theme.BgSurface;
            menu.ForeColor = Theme.TextPrimary;
            menu.ShowImageMargin = false;

            if (isSimulatingMode)
            {
                ToolStripMenuItem showItem = new ToolStripMenuItem("Show " + currentDisplayName, null, (s, e) => RestoreFromTray());
                showItem.Font = FontManager.ButtonFont;
                menu.Items.Add(showItem);
                menu.Items.Add("Stop", null, (s, e) => StopSimulation());
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
                if (minimizeToTrayEnabled)
                {
                    e.Cancel = true;
                    MinimizeToTray();
                    return;
                }
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
            StopDiscordRpc();
            KillDummy();
            GameDatabase.Instance.OnDatabaseUpdated -= OnDatabaseLoaded;
            GameDatabase.Instance.OnQualifiedGamesChanged -= OnQualifiedGamesUpdated;
            GameDatabase.Instance.OnInitializationCompleted -= OnDatabaseLoaded;

            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            if (timerQualification != null) { timerQualification.Stop(); timerQualification.Dispose(); }
            if (timerUIUpdate != null) { timerUIUpdate.Stop(); timerUIUpdate.Dispose(); }
            if (timerHoverCheck != null) { timerHoverCheck.Stop(); timerHoverCheck.Dispose(); }
            if (debounceSearchTimer != null) { debounceSearchTimer.Stop(); debounceSearchTimer.Dispose(); }

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
                GameDatabase.Instance.OnDatabaseUpdated -= OnDatabaseLoaded;
                GameDatabase.Instance.OnQualifiedGamesChanged -= OnQualifiedGamesUpdated;
                GameDatabase.Instance.OnInitializationCompleted -= OnDatabaseLoaded;
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

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00C00000; // WS_CAPTION
                cp.Style &= ~0x00040000; // WS_THICKFRAME
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

            bool hClose = CloseRect.Contains(e.Location);
            bool hMin = MinimizeRect.Contains(e.Location);
            bool hTray = TrayBtnRect.Contains(e.Location);
            bool hAdd = AddBtnRect.Contains(e.Location);

            if (hClose != _isHoveringClose || hMin != _isHoveringMinimize ||
                hTray != _isHoveringTrayBtn || hAdd != _isHoveringAddBtn)
            {
                _isHoveringClose = hClose;
                _isHoveringMinimize = hMin;
                _isHoveringTrayBtn = hTray;
                _isHoveringAddBtn = hAdd;

                if (hClose || hMin || hTray || hAdd)
                {
                    this.Cursor = Cursors.Hand;
                    if (hTray && globalToolTip != null) globalToolTip.SetToolTip(this, minimizeToTrayEnabled ? "Tray On" : "Tray Off");
                    else if (hAdd && globalToolTip != null) globalToolTip.SetToolTip(this, "Add Game");
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
                _isHoveringTrayBtn = false;
                _isHoveringAddBtn = false;
                this.Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                // Top-right window controls (visible on window hover)
                if (CloseRect.Contains(e.Location))
                {
                    if (minimizeToTrayEnabled) MinimizeToTray();
                    else this.Close();
                    return;
                }

                if (MinimizeRect.Contains(e.Location))
                {
                    this.WindowState = FormWindowState.Minimized;
                    return;
                }

                // Footer Buttons
                if (TrayBtnRect.Contains(e.Location))
                {
                    minimizeToTrayEnabled = !minimizeToTrayEnabled;
                    Invalidate();
                    return;
                }

                if (AddBtnRect.Contains(e.Location))
                {
                    ShowAddCustomGameDialog("");
                    return;
                }

                // Drag window by empty areas
                if (pnlInlineSearch.Visible && !pnlInlineSearch.Bounds.Contains(e.Location) && !searchDropdown.Bounds.Contains(e.Location))
                {
                    CloseInlineSearch();
                }

                Program.DragWindow(this.Handle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Subtle 1px window border inside the 16px rounded radius
            using (GraphicsPath path = Program.GetRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Program.WindowRadius))
            {
                using (Pen borderPen = new Pen(Theme.BorderSubtle, 1f))
                {
                    g.DrawPath(borderPen, path);
                }
            }

            // Top-right window controls: Close and minimize appear as two tiny icons, ONLY visible on window hover
            if (_isMouseInWindow)
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

            // Footer buttons:
            // Left = tray toggle, Right = add custom game (+). Active state shown by color only, never by text.
            Color trayColor = minimizeToTrayEnabled ? Theme.AccentViolet : (_isHoveringTrayBtn ? Theme.TextPrimary : Theme.TextMuted);
            using (Pen trayPen = new Pen(trayColor, 1.6f))
            {
                int tx = TrayBtnRect.X + 3;
                int ty = TrayBtnRect.Y + 3;
                // Tray basin
                g.DrawLine(trayPen, tx, ty + 8, tx, ty + 13);
                g.DrawLine(trayPen, tx, ty + 13, tx + 14, ty + 13);
                g.DrawLine(trayPen, tx + 14, ty + 13, tx + 14, ty + 8);
                // Down arrow
                g.DrawLine(trayPen, tx + 7, ty + 2, tx + 7, ty + 8);
                g.DrawLine(trayPen, tx + 4, ty + 5, tx + 7, ty + 8);
                g.DrawLine(trayPen, tx + 10, ty + 5, tx + 7, ty + 8);
            }

            // Right = Add custom game (+)
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
