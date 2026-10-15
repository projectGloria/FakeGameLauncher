using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using DiscordQuestLauncher;

public static class LauncherRegressionTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string root;
    private static string pipePrefix;
    private static int checks;
    private static GameDatabase Database { get { return GameDatabase.Instance; } }

    private class TestForm : MainForm
    {
        public int DialogCount;
        public string DialogQuery;
        public int LaunchCount;
        public string TargetPath;
        public string LaunchId;
        public TestForm() : base(new string[] { "--launcher" }) { }
        protected override void ShowAddCustomGameDialog(string query) { DialogCount++; DialogQuery = query; }
        protected override void BeginAsyncLaunch(string target, string source, string name, string exe, string id, string icon, string mode)
        {
            LaunchCount++;
            TargetPath = target;
            LaunchId = id;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--dummy") { Program.Main(args); return 0; }
        root = args[0];
        pipePrefix = args[1];
        try
        {
            Set(Database, "_initStarted", true);
            Set(Database, "_initializationCompleted", true);
            AtomicPersistence();
            DetectionPaths();
            GameHistory();
            Database.SaveLastSimulatedGame(Game("", "Initial", "initial.exe"));
            SearchAndState();
            StartupAndHelperPaths();
            RpcProtocol();
            RpcLifecycle();
            HelperLifecycle();
            Console.WriteLine("PASS: " + checks + " application regression assertions");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        checks++;
    }
    private static object Field(object instance, string name)
    {
        return instance.GetType().GetField(name, Private).GetValue(instance);
    }
    private static object MainField(MainForm form, string name) { return typeof(MainForm).GetField(name, Private).GetValue(form); }
    private static void Set(object instance, string name, object value) { instance.GetType().GetField(name, Private).SetValue(instance, value); }
    private static void MainSet(MainForm form, string name, object value) { typeof(MainForm).GetField(name, Private).SetValue(form, value); }
    private static object Call(MainForm form, string name, params object[] args) { return typeof(MainForm).GetMethod(name, Private).Invoke(form, args); }
    private static void Mouse(SearchDropdownList list, int row)
    {
        typeof(SearchDropdownList).GetMethod("OnMouseDown", Private).Invoke(list,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, row * 44 + 12, 0) });
    }
    private static GameItem Game(string id, string name, string exe) { return new GameItem { id = id, name = name, exe = exe, icon = "" }; }
    private static void SeedIcon(GameItem game)
    {
        var images = (Dictionary<string, Image>)Field(IconCache.Instance, "_memoryCache");
        string iconId = string.IsNullOrEmpty(game.id) ? game.NormalizedName : game.id;
        foreach (int size in new int[] { 96, 36, 28 })
        {
            int radius = size == 96 ? 24 : size == 36 ? 8 : 6;
            string key = (game.id ?? "") + "_" + iconId + "_" + size + "_" + radius;
            if (!images.ContainsKey(key)) images[key] = new Bitmap(size, size);
        }
    }
    private static void DisposeForm(MainForm form)
    {
        Call(form, "StopDiscordRpc");
        Call(form, "KillDummy");
        Database.OnDatabaseUpdated -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnDatabaseLoaded", Private));
        Database.OnInitializationCompleted -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnDatabaseLoaded", Private));
        Database.OnQualifiedGamesChanged -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnQualifiedGamesUpdated", Private));
        foreach (string timer in new string[] { "timerQualification", "timerUIUpdate", "timerHoverCheck", "debounceSearchTimer" })
            ((System.Windows.Forms.Timer)MainField(form, timer)).Dispose();
        ((NotifyIcon)MainField(form, "trayIcon")).Dispose();
        form.Dispose();
    }
    private static void WaitUntil(Func<bool> condition, int milliseconds, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        Check(condition(), message);
    }

    private static void AtomicPersistence()
    {
        string path = Path.Combine(root, "atomic.json");
        GameDatabase.SafeAtomicWriteText(path, "old");
        GameDatabase.SafeAtomicWriteText(path, "new");
        Check(File.ReadAllText(path) == "new" && File.ReadAllText(path + ".bak") == "old", "atomic replacement and backup");
        bool threw = false;
        using (FileStream held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { GameDatabase.SafeAtomicWriteText(path, "blocked"); }
            catch (IOException) { threw = true; }
        }
        Check(threw && File.ReadAllText(path) == "new", "locked destination reports failure and preserves original");
        Check(Directory.GetFiles(root, "*.tmp").Length == 0, "failed atomic write cleans staging file");

        string first = new string('a', 65536), second = new string('b', 65536);
        GameDatabase.SafeAtomicWriteText(path, first);
        Exception readerError = null;
        int completeReads = 0;
        using (ManualResetEvent stop = new ManualResetEvent(false))
        using (ManualResetEvent started = new ManualResetEvent(false))
        {
            Thread reader = new Thread(() =>
            {
                while (!stop.WaitOne(0))
                {
                    try
                    {
                        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (StreamReader text = new StreamReader(stream))
                        {
                            string content = text.ReadToEnd();
                            if (content != first && content != second) throw new Exception("Reader observed a partial document.");
                            Interlocked.Increment(ref completeReads);
                            started.Set();
                        }
                    }
                    catch (IOException ex)
                    {
                        // Retry Windows replacement transitions; successful reads must contain a whole document.
                        int code = ex.HResult & 0xffff;
                        if (code != 2 && code != 32 && code != 33) { readerError = ex; started.Set(); break; }
                    }
                    catch (Exception ex) { readerError = ex; started.Set(); break; }
                }
            });
            reader.Start(); Check(started.WaitOne(3000), "concurrent reader starts");
            try { for (int i = 0; i < 25; i++) GameDatabase.SafeAtomicWriteText(path, i % 2 == 0 ? second : first); }
            finally { stop.Set(); reader.Join(); }
        }
        Check(readerError == null && completeReads > 0, "concurrent readers see complete documents: " + readerError);
    }

    private static void GameHistory()
    {
        Set(Database, "_qualifiedGamesCache", new List<QualifiedGameEntry>());
        Check(Database.RecordQualifiedGame(Game("100", "First", "game.exe"), 120), "first history save");
        Check(Database.RecordQualifiedGame(Game("200", "Second", "game.exe"), 180), "second history save");
        List<QualifiedGameEntry> history = Database.GetQualifiedGames();
        Check(history.Count == 2 && history[0].id == "200" && history[0].name == "Second" && history[0].total_seconds_played == 180,
            "distinct IDs with shared executables remain separate");
        Check(Database.RecordQualifiedGame(Game("200", "Renamed Second", "new.exe"), 120), "renamed identified game saves");
        history = Database.GetQualifiedGames();
        Check(history.Count == 2 && history[0].name == "Renamed Second" && history[0].exe == "new.exe" && history[0].total_seconds_played == 300,
            "same ID accumulates time and refreshes metadata");
        Check(Database.RecordQualifiedGame(Game("", "Custom One", "shared.exe"), 120) &&
            Database.RecordQualifiedGame(Game("", "Custom Two", "shared.exe"), 120), "custom game history saves");
        Check(Database.GetQualifiedGames().Count == 4, "custom games with shared executables remain separate");

        using (FileStream held = new FileStream(Database.QualifiedGamesFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(!Database.RecordQualifiedGame(Game("200", "Renamed Second", "new.exe"), 120), "failed history save reports failure");
        history = Database.GetQualifiedGames();
        Check(history.Find(x => x.id == "200").total_seconds_played == 300, "failed save does not modify cached totals");
        Check(Database.RecordQualifiedGame(Game("200", "Renamed Second", "new.exe"), 120), "failed history save can be retried");
        Check(Database.GetQualifiedGames().Find(x => x.id == "200").total_seconds_played == 420, "retry records session exactly once");
        Check(Directory.GetFiles(Database.CacheDirectory, "*.tmp").Length == 0, "history failure cleans staging files");
        Check(!Database.SaveLastSimulatedGame(Game("__ADD_CUSTOM__", "Placeholder", "placeholder.exe")), "placeholder cannot be persisted");
    }

    private static void DetectionPaths()
    {
        string json = "[{\"id\":\"1220537550809337856\",\"name\":\"Dragon's Dogma II\",\"executables\":[{\"os\":\"win32\",\"name\":\"dragons dogma 2/dd2.exe\",\"is_launcher\":false}]}," +
            "{\"id\":\"1428223713928810496\",\"name\":\"March of Giants\",\"executables\":[{\"os\":\"win32\",\"name\":\"thelineclient.exe\",\"is_launcher\":false}]}]";
        List<GameItem> games = GameDatabase.ParseDetectableApplications(json);
        Check(games.Count == 2 && games[0].exe == "dd2.exe" && games[0].executable_path == Path.Combine("dragons dogma 2", "dd2.exe"),
            "Discord folder-qualified executable metadata retains the folder and basename separately");
        Check(games[1].exe == "thelineclient.exe" && games[1].executable_path == "thelineclient.exe",
            "filename-only detection remains unchanged");
        var serializer = new JavaScriptSerializer();
        games = serializer.Deserialize<List<GameItem>>(serializer.Serialize(games));
        Check(games[0].executable_path == Path.Combine("dragons dogma 2", "dd2.exe"), "detection paths survive the game cache round trip");
        foreach (string invalid in new string[] { "../dd2.exe", "folder/../../dd2.exe", "C:\\Games\\dd2.exe", "/dd2.exe",
            "\\\\server\\share\\dd2.exe", "folder//dd2.exe", "folder/.. /dd2.exe", "folder./dd2.exe", "folder/dd2.exe:stream" })
            Check(GameDatabase.NormalizeExecutablePath(invalid) == null, "unsafe executable metadata is rejected: " + invalid);
        Set(Database, "_games", games);
        GameItem stale = Game(games[0].id, games[0].name, "dd2.exe"); SeedIcon(stale);
        TestForm form = new TestForm();
        try
        {
            IntPtr handle = form.Handle;
            Call(form, "SelectGame", stale);
            Call(form, "LaunchSimulation", "dd2.exe");
            Check(form.TargetPath.EndsWith(Path.Combine("dragons dogma 2", "dd2.exe"), StringComparison.OrdinalIgnoreCase) &&
                MainForm.IsOwnedHelperPath(form.TargetPath), "launching an old history entry resolves the refreshed path inside its session directory");
            Check(Database.GetLastSimulatedGame().executable_path == games[0].executable_path,
                "last selection preserves the resolved detection path");
            Call(form, "SelectGame", games[1]);
            Call(form, "LaunchSimulation", "thelineclient.exe");
            Check(Path.GetFileName(Path.GetDirectoryName(form.TargetPath)).Length == 32,
                "filename-only games still launch directly inside their unique session directory");
            string preserved = Path.Combine(root, "DiscordDummyTools", "unowned", "dd2.exe");
            Check(!MainForm.IsOwnedHelperPath(preserved), "cleanup refuses helper paths outside GUID-owned sessions");
        }
        finally { DisposeForm(form); }
    }

    private static void SearchAndState()
    {
        GameItem alpha = Game("", "Alpha", "alpha.exe"), beta = Game("", "Beta", "beta.exe");
        Set(Database, "_games", new List<GameItem> { alpha, beta });
        TestForm form = new TestForm();
        try
        {
            SearchDropdownList list = (SearchDropdownList)MainField(form, "searchDropdown");
            TextBox text = (TextBox)MainField(form, "txtInlineSearch");
            Call(form, "OpenInlineSearch"); text.Text = "Beta";
            Call(form, "OnInlineSearchKeyDown", form, new KeyEventArgs(Keys.Enter));
            Check(((GameItem)MainField(form, "selectedGame")).name == "Beta", "Enter refreshes a pending query");

            Call(form, "OpenInlineSearch"); text.Text = "New Custom";
            Call(form, "OnDebounceSearchTick", form, EventArgs.Empty);
            Mouse(list, 0);
            Check(form.DialogCount == 1 && form.DialogQuery == "New Custom", "mouse Add opens custom dialog");
            Check(((GameItem)MainField(form, "selectedGame")).id != "__ADD_CUSTOM__" && Database.GetLastSimulatedGame().id != "__ADD_CUSTOM__",
                "mouse Add never selects or saves a placeholder");
            Call(form, "OpenInlineSearch"); text.Text = "Another Custom";
            Call(form, "OnInlineSearchKeyDown", form, new KeyEventArgs(Keys.Enter));
            Check(form.DialogCount == 2 && form.DialogQuery == "Another Custom", "keyboard Add uses the same action");

            Call(form, "SelectGame", alpha);
            Call(form, "OpenInlineSearch"); text.Text = "Beta";
            Mouse(list, 0);
            Check(((GameItem)MainField(form, "selectedGame")).name == "Alpha" && list.SelectedItem.name == "Beta", "stale mouse results refresh before selection");
            Mouse(list, 0);
            Check(((GameItem)MainField(form, "selectedGame")).name == "Beta", "fresh mouse result selects the current query");
            Call(form, "CloseInlineSearch");

            Call(form, "StartRunningState", alpha);
            Call(form, "SelectGame", beta);
            Check(((GameItem)MainField(form, "selectedGame")) == alpha && ((HeroGameControl)MainField(form, "heroControl")).SelectedGame == alpha &&
                ((GameItem)MainField(form, "activeSimulatingGame")) == alpha, "selection cannot diverge from active simulation");
            Call(form, "OpenInlineSearch");
            Check(!((HeroGameControl)MainField(form, "heroControl")).IsSearchActive, "running game cannot open selection search");
            Call(form, "StopSimulation");
            Check(((GameItem)MainField(form, "selectedGame")) == alpha && !((HeroGameControl)MainField(form, "heroControl")).IsRunning,
                "stop retains a consistent idle selection");
            MainSet(form, "isLaunching", true);
            Call(form, "SelectGame", beta);
            Check(((GameItem)MainField(form, "selectedGame")) == alpha, "selection cannot change during launch");
            MainSet(form, "isLaunching", false);
            Call(form, "StartRunningState", beta);
            Call(form, "StopSimulation");
            Check(((GameItem)MainField(form, "selectedGame")) == beta && ((HeroGameControl)MainField(form, "heroControl")).SelectedGame == beta,
                "start synchronizes active, selected and displayed games");

            Stopwatch elapsed = new Stopwatch();
            foreach (FieldInfo field in typeof(Stopwatch).GetFields(Private))
                if (field.FieldType == typeof(long) && field.Name.ToLowerInvariant().Contains("elapsed")) field.SetValue(elapsed, 130L * Stopwatch.Frequency);
            Check(elapsed.Elapsed.TotalSeconds == 130, "qualification fixture represents a completed session");
            MainSet(form, "sessionStopwatch", elapsed); MainSet(form, "qualificationFinalRecorded", false);
            int before = Database.GetQualifiedGames().Count;
            using (FileStream held = new FileStream(Database.QualifiedGamesFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!(bool)Call(form, "TryRecordSession") && !(bool)MainField(form, "qualificationFinalRecorded"), "failed session save remains pending");
            Check((bool)Call(form, "TryRecordSession") && (bool)MainField(form, "qualificationFinalRecorded"), "pending session retries successfully");
            Check((bool)Call(form, "TryRecordSession") && Database.GetQualifiedGames().Count == before + 1 &&
                Database.GetQualifiedGames().Find(x => x.name == "Beta").total_seconds_played == 130, "repeated qualification check never duplicates a session");

            List<GameItem> results = new List<GameItem>();
            for (int i = 0; i < 16; i++) results.Add(Game("", "Result " + i, "result" + i + ".exe"));
            list.Size = new Size(288, 180); list.SetResults(results);
            for (int i = 0; i < 15; i++) list.MoveSelection(1);
            int top = (int)Field(list, "_firstVisibleIndex");
            Check(top <= 15 && top + 4 > 15, "last result is visible after keyboard navigation");
            GameItem clicked = null;
            list.ItemSelected += game => clicked = game;
            Mouse(list, 15 - top);
            Check(clicked == results[15], "mouse hit testing accounts for scroll position");
            list.MoveSelection(1);
            Check(list.SelectedItem == results[0] && (int)Field(list, "_firstVisibleIndex") == 0, "keyboard wrap scrolls back to first row");
            typeof(SearchDropdownList).GetMethod("OnMouseWheel", Private).Invoke(list,
                new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, -120) });
            Check((int)Field(list, "_firstVisibleIndex") > 0, "mouse wheel scrolls results");
            using (Bitmap image = new Bitmap(288, 180)) list.DrawToBitmap(image, new Rectangle(0, 0, 288, 180));
            Check(true, "scrolled results render without errors");
        }
        finally { DisposeForm(form); }
    }

    private static void StartupAndHelperPaths()
    {
        GameItem fc = Game("300", "EA SPORTS FC 27", "fc27.exe"); SeedIcon(fc);
        Set(Database, "_games", new List<GameItem>());
        Set(Database, "_initializationCompleted", false);
        TestForm form = new TestForm();
        try
        {
            MainSet(form, "startupGameArg", fc.name); MainSet(form, "hasBeenShown", true);
            IntPtr handle = form.Handle;
            Call(form, "TryLaunchStartupGame");
            Check(form.LaunchCount == 0 && (string)MainField(form, "startupGameArg") == fc.name, "startup waits for metadata");
            Set(Database, "_games", new List<GameItem> { fc });
            typeof(GameDatabase).GetMethod("CompleteInitialization", Private).Invoke(Database, null);
            WaitUntil(() => form.LaunchCount == 1, 2000, "database completion triggers deferred startup");
            Check(form.LaunchCount == 1 && form.LaunchId == "300", "startup launches using resolved application ID");
            Check(MainForm.IsOwnedHelperPath(form.TargetPath) && Path.GetFileName(form.TargetPath) == "fc27.exe", "FC 27 uses an owned helper path");
            string first = form.TargetPath;
            Call(form, "TryLaunchStartupGame"); Check(form.LaunchCount == 1, "startup launch executes once");
            Call(form, "LaunchSimulation", fc.exe);
            Check(form.TargetPath != first, "each launch has a distinct helper directory");
            string outside = Path.Combine(root, "unrelated.exe"); File.WriteAllText(outside, "keep");
            Check(!MainForm.IsOwnedHelperPath(outside), "cleanup rejects unrelated executables");
            string sibling = Path.Combine(root, "DiscordDummyTools-other", "game.exe");
            Check(!MainForm.IsOwnedHelperPath(sibling), "cleanup rejects a sibling with a matching prefix");
            Check(!MainForm.IsOwnedHelperPath(Path.Combine(root, "DiscordDummyTools", "..", "unrelated.exe")), "cleanup resolves traversal before checking ownership");
            MainForm.ScheduleTempCleanup(outside);
            Check(File.Exists(outside), "cleanup leaves unowned files intact");
        }
        finally { DisposeForm(form); }
        using (DiscordRpcClient missingId = new DiscordRpcClient("")) Check(!missingId.Connect(0), "missing IDs never use another game's RPC identity");
        Set(Database, "_initializationCompleted", false);
        int completed = 0; Action handler = () => completed++;
        Database.OnInitializationCompleted += handler;
        typeof(GameDatabase).GetMethod("CompleteInitialization", Private).Invoke(Database, null);
        typeof(GameDatabase).GetMethod("CompleteInitialization", Private).Invoke(Database, null);
        Database.OnInitializationCompleted -= handler;
        Check(Database.IsInitialized && completed == 1, "initialization completion notifies once, including an empty database");
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        byte[] bytes = new byte[count]; int offset = 0;
        while (offset < count) { int read = stream.Read(bytes, offset, count - offset); if (read == 0) throw new IOException(); offset += read; }
        return bytes;
    }
    private static string ReadFrame(Stream stream)
    {
        byte[] header = ReadBytes(stream, 8);
        return Encoding.UTF8.GetString(ReadBytes(stream, BitConverter.ToInt32(header, 4)));
    }
    private static void SendFrame(Stream stream, string json)
    {
        SendFrame(stream, 1, json);
    }
    private static void SendFrame(Stream stream, int opcode, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        stream.Write(BitConverter.GetBytes(opcode), 0, 4); stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
        stream.Write(bytes, 0, bytes.Length); stream.Flush();
    }
    private static void RpcProtocol()
    {
        using (DiscordRpcClient absent = new DiscordRpcClient("100"))
            Check(!absent.Connect(1) && !string.IsNullOrEmpty(absent.LastError), "missing Discord pipes expose an actionable diagnostic");
        Exception serverError = null;
        bool pongMatches = false, helperPidMatches = false;
        using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipePrefix + "0", PipeDirection.InOut))
        using (DiscordRpcClient client = new DiscordRpcClient("100", 7777))
        {
            Thread server = new Thread(() =>
            {
                try
                {
                    pipe.WaitForConnection(); ReadFrame(pipe);
                    // Reproduce a slow READY reply and a control frame before the handshake completes.
                    Thread.Sleep(2300);
                    string ping = "{\"message\":\"ping-ç\"}";
                    SendFrame(pipe, 3, ping);
                    byte[] header = ReadBytes(pipe, 8);
                    pongMatches = BitConverter.ToInt32(header, 0) == 4 &&
                        Encoding.UTF8.GetString(ReadBytes(pipe, BitConverter.ToInt32(header, 4))) == ping;
                    SendFrame(pipe, "{\"evt\":\"READY\"}");
                    var activity = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadFrame(pipe));
                    helperPidMatches = Convert.ToInt32(((Dictionary<string, object>)activity["args"])["pid"]) == 7777;
                    SendFrame(pipe, "{\"evt\":\"ERROR\",\"nonce\":\"" + activity["nonce"] + "\",\"data\":{\"message\":\"test rejection\"}}");
                    activity = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadFrame(pipe));
                    SendFrame(pipe, "{\"nonce\":\"" + activity["nonce"] + "\"}");
                }
                catch (Exception ex) { serverError = ex; }
            });
            server.IsBackground = true; server.Start();
            try
            {
                Check(client.Connect(100), "slow Discord handshake connects instead of timing out at two seconds");
                Check(!client.SetActivity("Game", "In Game", "", "", 0), "rejected activity is never reported as accepted");
                Check(pongMatches && helperPidMatches, "RPC responds to PING exactly and attributes activity to the helper PID");
                Check(client.LastError != null && client.LastError.Contains("test rejection"), "activity rejection reaches diagnostics");
                Check(client.SetActivity("Game", "In Game", "", "", 0) && client.LastError == null, "successful retry clears the previous diagnostic");
                Check(server.Join(2000) && serverError == null, "protocol emulator completes cleanly");
            }
            finally { pipe.Dispose(); client.Dispose(); server.Join(2000); }
        }
    }
    private static void RpcLifecycle()
    {
        int activities = 0; Exception serverError = null;
        using (ManualResetEvent ready = new ManualResetEvent(false))
        {
            Thread server = new Thread(() =>
            {
                try
                {
                    for (int session = 0; session < 2; session++)
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipePrefix + "0", PipeDirection.InOut))
                    {
                        ready.Set(); pipe.WaitForConnection();
                        try
                        {
                            ReadFrame(pipe); SendFrame(pipe, "{\"evt\":\"READY\"}");
                            while (true)
                            {
                                var payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadFrame(pipe));
                                SendFrame(pipe, "{\"nonce\":\"" + payload["nonce"] + "\"}");
                                Interlocked.Increment(ref activities);
                            }
                        }
                        catch (IOException) { }
                    }
                }
                catch (Exception ex) { serverError = ex; }
            });
            server.IsBackground = true; server.Start(); Check(ready.WaitOne(2000), "local RPC emulator starts");
            MainForm form = new MainForm(new string[] { "--launcher" });
            try
            {
                Call(form, "StartDiscordRpc", "100", "First", "");
                WaitUntil(() => activities >= 1, 4000, "first RPC session acknowledges activity");
                WaitUntil(() => (string)MainField(form, "rpcStatus") == "Discord activity acknowledged", 2000,
                    "visible status waits for the Discord acknowledgement");
                Thread oldWorker = (Thread)MainField(form, "rpcThread");
                Call(form, "StartDiscordRpc", "200", "Second", "");
                WaitUntil(() => activities >= 2, 4000, "second RPC session acknowledges activity");
                DiscordRpcClient current = (DiscordRpcClient)MainField(form, "rpcClient");
                Thread.Sleep(1100);
                Check(!oldWorker.IsAlive && current != null && current.IsConnected && object.ReferenceEquals(current, MainField(form, "rpcClient")),
                    "old RPC worker cannot dispose a new session");
                Thread newWorker = (Thread)MainField(form, "rpcThread");
                Call(form, "StopDiscordRpc");
                Check(newWorker.Join(2000) && MainField(form, "rpcClient") == null, "stopping RPC promptly wakes and closes worker");
                Check((string)MainField(form, "rpcStatus") == "Activity simulation only.", "stopping clears the acknowledged activity status");
                Check(server.Join(2000) && serverError == null, "RPC emulator closes cleanly");
            }
            finally { DisposeForm(form); }
        }
    }

    private static void HelperLifecycle()
    {
        File.WriteAllText(Path.Combine(root, "launch_mode.txt"), "hidden");
        GameItem game = Game("", "EA SPORTS FC 27", "fc27.exe");
        game.executable_path = Path.Combine("nested game", "bin", "fc27.exe");
        Database.SaveLastSimulatedGame(game);
        MainForm form = new MainForm(new string[] { "--launcher" });
        string target = null;
        try
        {
            IntPtr handle = form.Handle;
            Call(form, "SelectGame", game); Call(form, "LaunchSimulation", game.exe);
            WaitUntil(() => MainField(form, "dummyProcess") != null, 8000, "real helper process starts");
            Process helper = (Process)MainField(form, "dummyProcess");
            target = (string)MainField(form, "dummyExePath");
            Check(!helper.HasExited && MainForm.IsOwnedHelperPath(target), "helper runs inside the isolated owned directory");
            string sessionDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(target)));
            string otherSession = Path.Combine(Path.GetDirectoryName(sessionDirectory), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherSession);
            string unrelated = Path.Combine(otherSession, "keep.txt"); File.WriteAllText(unrelated, "keep");
            Check(target.EndsWith(game.executable_path, StringComparison.OrdinalIgnoreCase), "real helper launch preserves nested detection directories");
            Check(((Label)MainField(form, "lblDiscordStatus")).Text.Contains("quest progress is unverified"),
                "helper session explicitly labels its timer as unverified quest progress");
            Check(((GameItem)MainField(form, "activeSimulatingGame")).exe == "fc27.exe", "FC 27 launch preserves active game metadata");
            int pid = helper.Id;
            Call(form, "StopSimulation");
            bool exited;
            try { using (Process process = Process.GetProcessById(pid)) exited = process.HasExited; }
            catch (ArgumentException) { exited = true; }
            Check(exited, "Stop terminates the owned helper");
            WaitUntil(() => !File.Exists(target) && !Directory.Exists(Path.GetDirectoryName(target)), 15000, "cleanup deletes helper and its session directory");
            WaitUntil(() => !Directory.Exists(sessionDirectory), 3000, "nested cleanup removes empty directories through the owned session root");
            Check(File.Exists(unrelated), "nested cleanup preserves neighboring session files");
        }
        finally { DisposeForm(form); }
    }
}
