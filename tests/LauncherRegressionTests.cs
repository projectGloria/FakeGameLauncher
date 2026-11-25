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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

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
            Set(QuestDatabase.Instance, "_initStarted", true);
            AtomicPersistence();
            DetectionPaths();
            GameHistory();
            Database.SaveLastSimulatedGame(Game("", "Initial", "initial.exe"));
            SearchAndState();
            StartupAndHelperPaths();
            RpcProtocol();
            RpcLifecycle();
            HelperLifecycle();
            IconLoadingAndControlLifetime();
            WindowLifecycle();
            TestMultiInstanceSimulation();
            TestQueueSimulation();
            SearchDropdownLayout();
            SearchLightDismiss();
            QuestExplorerTests();
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
        Call(form, "KillAllExtraDummies");
        Database.OnDatabaseUpdated -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnDatabaseLoaded", Private));
        Database.OnInitializationCompleted -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnDatabaseLoaded", Private));
        Database.OnQualifiedGamesChanged -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnQualifiedGamesUpdated", Private));
        QuestDatabase.Instance.OnQuestsUpdated -= (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(MainForm).GetMethod("OnQuestsCatalogUpdated", Private));
        QuestsPanel qp = (QuestsPanel)MainField(form, "questsPanel");
        if (qp != null && !qp.IsDisposed) qp.Dispose();
        foreach (string timer in new string[] { "timerQualification", "timerUIUpdate", "timerHoverCheck", "debounceSearchTimer", "debounceExtraSearchTimer" })
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

    private static void IconLoadingAndControlLifetime()
    {
        IconCache cache = IconCache.Instance;
        Check(object.ReferenceEquals(cache.GetFallbackGamepadIcon(28, 6), cache.GetFallbackGamepadIcon(28, 6)),
            "repainting missing icons reuses the same bitmap");
        using (var path = Program.GetRoundedPath(new Rectangle(0, 0, 10, 10), 0))
            Check(path.PointCount > 0, "zero-radius shapes render as rectangles");
        GameItem game = Game("regression-icon", "Cached icon", "cached.exe");
        string iconPath = Path.Combine(cache.IconDirectory, game.id + "_icon.png");
        using (Bitmap image = new Bitmap(48, 48))
        {
            using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue);
            image.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        int first = 0, second = 0;
        Image loaded = null;
        int uiThread = Thread.CurrentThread.ManagedThreadId;
        bool background = false;
        lock (Field(cache, "_lock"))
        {
            cache.GetIcon(game, 28, 6, image => { loaded = image; background = Thread.CurrentThread.ManagedThreadId != uiThread; Interlocked.Increment(ref first); });
            cache.GetIcon(game, 28, 6, image => Interlocked.Increment(ref second));
        }
        WaitUntil(() => first == 1 && second == 1, 3000, "all subscribers receive a shared in-flight icon");
        Check(background && object.ReferenceEquals(loaded, cache.GetIcon(game, 28, 6)),
            "disk decoding runs off the UI thread and subsequent paints reuse its result");
        using (PillActionButton button = new PillActionButton())
        {
            button.Dispose();
            Check(Field(button, "_animTimer") == null, "disposing buttons releases animation timers");
        }
        using (HeroGameControl hero = new HeroGameControl())
        {
            hero.SetGame(Game("", "First", "first.exe"), false);
            hero.SetGame(Game("", "Second", "second.exe"), true);
            hero.SetGame(Game("", "Third", "third.exe"), false);
            Check(!((System.Windows.Forms.Timer)Field(hero, "_crossfadeTimer")).Enabled,
                "nonanimated selection cancels an earlier crossfade");
            hero.Dispose();
            Check(Field(hero, "_crossfadeTimer") == null, "disposing the hero releases its animation timer");
        }
    }

    private static void WindowLifecycle()
    {
        foreach (string mode in new string[] { "single", "behind" })
        {
            File.WriteAllText(Path.Combine(root, "launch_mode.txt"), mode);
            GameItem game = Game("", "Window regression", "window-test.exe");
            Database.SaveLastSimulatedGame(game);
            MainForm form = new MainForm(new string[] { "--launcher" });
            try
            {
                form.Show();
                Call(form, "SelectGame", game);
                Call(form, "LaunchSimulation", game.exe);
                WaitUntil(() => MainField(form, "dummyProcess") != null && (IntPtr)MainField(form, "dummyHwnd") != IntPtr.Zero,
                    8000, mode + ": helper window is discovered");
                IntPtr helper = (IntPtr)MainField(form, "dummyHwnd");
                // Visibility checks are meaningless if they target the wrong HWND (e.g. the hidden
                // "GDI+ Window"), so pin discovery to the helper's actual Form window.
                Check(Program.GetWindowClassName(helper).StartsWith("WindowsForms10.Window.", StringComparison.Ordinal),
                    mode + ": discovered helper window is the WinForms form, not a GDI+/IME/broadcast window (got " + Program.GetWindowClassName(helper) + ")");
                Check(Program.IsWindowVisible(helper), mode + ": helper starts underneath the visible launcher");
                // Taskbar click-to-minimize only works when the shell sees WS_MINIMIZEBOX | WS_SYSMENU.
                int style = GetWindowLong(form.Handle, -16);
                Check((style & 0x00020000) != 0 && (style & 0x00080000) != 0,
                    mode + ": launcher keeps WS_MINIMIZEBOX|WS_SYSMENU so a taskbar click minimizes it");
                // Drive the same SC_MINIMIZE path the taskbar uses, not just WindowState.
                Program.SendMessage(form.Handle, 0x0112, 0xF020, 0); // WM_SYSCOMMAND, SC_MINIMIZE
                Application.DoEvents();
                Check(Program.IsIconic(form.Handle) && !Program.IsWindowVisible(helper),
                    mode + ": SC_MINIMIZE (taskbar click) minimizes the launcher and hides the helper");
                Program.SendMessage(form.Handle, 0x0112, 0xF120, 0); // WM_SYSCOMMAND, SC_RESTORE
                Application.DoEvents();
                Check(!Program.IsIconic(form.Handle) && Program.IsWindowVisible(helper),
                    mode + ": SC_RESTORE (taskbar click) restores launcher and helper");
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    form.WindowState = FormWindowState.Minimized;
                    Application.DoEvents();
                    Check(!Program.IsWindowVisible(helper), mode + ": taskbar minimize hides the blank helper");
                    form.WindowState = FormWindowState.Normal;
                    Application.DoEvents();
                    Check(Program.IsWindowVisible(helper) && !Program.IsIconic(helper), mode + ": taskbar restore restores the helper");
                    form.MinimizeToTray();
                    Check(!form.Visible && !Program.IsWindowVisible(helper), mode + ": tray minimize hides both windows");
                    form.RestoreFromTray();
                    Application.DoEvents();
                    Check(form.Visible && form.WindowState == FormWindowState.Normal && Program.IsWindowVisible(helper),
                        mode + ": tray restore restores both windows");
                }
                // The launch callback can run after the user has already hidden the launcher.
                Call(form, "StopSimulation");
                Call(form, "LaunchSimulation", game.exe);
                form.MinimizeToTray();
                WaitUntil(() => MainField(form, "dummyProcess") != null && (IntPtr)MainField(form, "dummyHwnd") != IntPtr.Zero,
                    8000, mode + ": late helper is discovered while hidden");
                helper = (IntPtr)MainField(form, "dummyHwnd");
                Check(!Program.IsWindowVisible(helper), mode + ": late startup cannot leave a blank window behind");
                form.RestoreFromTray();
                Check(Program.IsWindowVisible(helper), mode + ": late helper restores normally");
                using (Bitmap screenshot = new Bitmap(420, 480))
                {
                    form.DrawToBitmap(screenshot, new Rectangle(0, 0, 420, 480));
                    Check(screenshot.GetPixel(210, 355).ToArgb() != Theme.BgBase.ToArgb(),
                        mode + ": launcher controls still paint after repeated restores");
                }
                Call(form, "StopSimulation");
            }
            finally { DisposeForm(form); }
        }
    }

    private static void TestMultiInstanceSimulation()
    {
        File.WriteAllText(Path.Combine(root, "launch_mode.txt"), "behind");
        GameItem alpha = Game("", "Multi Alpha", "multi-alpha.exe");
        GameItem beta = Game("", "Multi Beta", "multi-beta.exe");
        Database.SaveLastSimulatedGame(alpha);
        MainForm form = new MainForm(new string[] { "--launcher" });
        try
        {
            form.Show();
            Application.DoEvents();
            Control strip = (Control)MainField(form, "extraStripControl");
            Check(!strip.Visible, "multi-instance strip is hidden when idle");

            Call(form, "SelectGame", alpha);
            Call(form, "LaunchSimulation", alpha.exe);
            WaitUntil(() => MainField(form, "dummyProcess") != null && (IntPtr)MainField(form, "dummyHwnd") != IntPtr.Zero,
                8000, "multi-instance: primary helper starts");
            Check(strip.Visible, "multi-instance strip is visible when simulating");

            Process primaryProc = (Process)MainField(form, "dummyProcess");
            int primaryPid = primaryProc.Id;
            IntPtr primaryHwnd = (IntPtr)MainField(form, "dummyHwnd");
            string primaryPath = (string)MainField(form, "dummyExePath");

            List<ExtraGameInstance> extras = (List<ExtraGameInstance>)MainField(form, "extraInstances");
            QueueStripControl queueStrip = (QueueStripControl)MainField(form, "queueStripControl");
            Call(form, "LaunchExtraSimulation", beta);
            WaitUntil(() => extras.Count == 1 && extras[0].DummyProcess != null && extras[0].DummyHwnd != IntPtr.Zero,
                8000, "multi-instance: extra helper starts and its window is discovered");
            Check(queueStrip.Visible, "concurrent running games show horizontal chain in queueStripControl");
            using (Bitmap cbmp = new Bitmap(queueStrip.Width, queueStrip.Height))
                queueStrip.DrawToBitmap(cbmp, new Rectangle(0, 0, queueStrip.Width, queueStrip.Height));

            ExtraGameInstance extra = extras[0];
            Process extraProc = extra.DummyProcess;
            int extraPid = extraProc.Id;
            IntPtr extraHwnd = extra.DummyHwnd;
            Check(!primaryProc.HasExited && !extraProc.HasExited, "both primary and extra helper processes stay alive simultaneously");
            Check(!string.Equals(Path.GetDirectoryName(primaryPath), Path.GetDirectoryName(extra.DummyExePath), StringComparison.OrdinalIgnoreCase),
                "each helper runs from its own temp session directory");

            // Duplicate launches of already-running games must be ignored.
            Call(form, "LaunchExtraSimulation", alpha);
            Call(form, "LaunchExtraSimulation", beta);
            Application.DoEvents();
            Check(extras.Count == 1, "duplicate running games cannot be launched twice");

            form.MinimizeToTray();
            Application.DoEvents();
            Check(!form.Visible && !Program.IsWindowVisible(primaryHwnd) && !Program.IsWindowVisible(extraHwnd),
                "tray minimize hides launcher and all running helper windows");
            form.RestoreFromTray();
            Application.DoEvents();
            Check(form.Visible && Program.IsWindowVisible(primaryHwnd) && Program.IsWindowVisible(extraHwnd),
                "tray restore restores launcher and all running helper windows");

            // Stopping primary promotes the extra instance without killing the extra helper.
            Call(form, "StopSimulation");
            WaitUntil(() => HasProcessExited(primaryPid), 3000, "stopping primary terminates the primary helper");
            Check(!HasProcessExited(extraPid) && extras.Count == 0, "stopping primary promotes extra instance and keeps its helper alive");
            Check(((GameItem)MainField(form, "activeSimulatingGame")).exe == beta.exe &&
                  object.ReferenceEquals(MainField(form, "dummyProcess"), extraProc),
                "promoted game becomes the active primary simulation");

            // Stopping the promoted instance returns to idle.
            Call(form, "StopSimulation");
            WaitUntil(() => HasProcessExited(extraPid), 3000, "stopping promoted game terminates its helper");
            Check(!strip.Visible && !(bool)MainField(form, "isSimulatingMode"), "stopping final game hides strip and returns to idle");
        }
        finally { DisposeForm(form); }
    }

    private static bool HasProcessExited(int pid)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid)) return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void TestQueueSimulation()
    {
        File.WriteAllText(Path.Combine(root, "launch_mode.txt"), "behind");
        GameItem q1 = Game("", "Queue One", "queue-one.exe");
        GameItem q2 = Game("", "Queue Two", "queue-two.exe");
        Database.SaveLastSimulatedGame(q1);
        MainForm form = new MainForm(new string[] { "--launcher" });
        try
        {
            form.Show();
            Application.DoEvents();
            QueueStripControl strip = (QueueStripControl)MainField(form, "queueStripControl");
            Control extraStrip = (Control)MainField(form, "extraStripControl");
            Control hero = (Control)MainField(form, "heroControl");
            Control switcher = (Control)MainField(form, "switcherControl");
            List<QueueItem> queue = (List<QueueItem>)MainField(form, "queueItems");

            Check(!strip.Visible && hero.Visible && switcher.Visible, "queue strip is hidden initially while hero and switcher are visible");
            Call(form, "AddGameToQueue", q1);
            Call(form, "AddGameToQueue", q2);
            Call(form, "AddGameToQueue", q1); // duplicate ignored
            Check(queue.Count == 2 && strip.Visible && hero.Visible && switcher.Visible,
                "queue accepts unique games, shows horizontal queue strip, and keeps hero/switcher visible");
            Check(queue[0].PlannedPlaySeconds >= 900 && queue[0].PlannedPlaySeconds <= 1500,
                "queued game play duration is randomized between 15 and 25 minutes");
            Check(queue[0].PlannedBreakAfterSeconds >= 600 && queue[0].PlannedBreakAfterSeconds <= 1500,
                "queued inter-game break timer is randomized between 10 and 25 minutes");

            Call(form, "ToggleQueuePanel");
            Check(!strip.Visible && hero.Visible && switcher.Visible, "closing queue panel keeps hero and switcher visible");
            Call(form, "ToggleQueuePanel");
            Check(strip.Visible && hero.Visible && switcher.Visible, "reopening queue panel keeps hero and switcher visible");

            Call(form, "OnQueueRemoveRequested", 1);
            Check(queue.Count == 1, "removing pending queued game succeeds");
            Call(form, "AddGameToQueue", q2);
            Check(queue.Count == 2, "re-adding second game restores queue");

            // Override durations for fast deterministic end-to-end execution.
            queue[0].PlannedPlaySeconds = 1;
            queue[0].PlannedBreakAfterSeconds = 1;
            queue[1].PlannedPlaySeconds = 1;

            Call(form, "StartQueue");
            WaitUntil(() => MainField(form, "dummyProcess") != null && (bool)MainField(form, "isSimulatingMode"),
                8000, "queue: first game helper starts");
            int firstPid = ((Process)MainField(form, "dummyProcess")).Id;
            Check(extraStrip.Visible && strip.Visible, "post-launch action row and queue strip are visible while queue runs");
            using (Bitmap bmp = new Bitmap(strip.Width, strip.Height)) strip.DrawToBitmap(bmp, new Rectangle(0, 0, strip.Width, strip.Height));
            using (Bitmap ebmp = new Bitmap(extraStrip.Width, extraStrip.Height)) extraStrip.DrawToBitmap(ebmp, new Rectangle(0, 0, extraStrip.Width, extraStrip.Height));

            WaitUntil(() => (QueuePhase)MainField(form, "queuePhase") == QueuePhase.BreakCooldown,
                6000, "queue: first game completes and enters break cooldown");
            Check(queue[0].State == QueueItemState.Completed && HasProcessExited(firstPid),
                "queue: first game is marked Completed and its helper exited");
            Check(!((bool)MainField(form, "isSimulatingMode")), "queue: no game process runs during break cooldown");

            WaitUntil(() => (QueuePhase)MainField(form, "queuePhase") == QueuePhase.PlayingGame &&
                            (int)MainField(form, "queueActiveIndex") == 1 &&
                            MainField(form, "dummyProcess") != null,
                6000, "queue: break timer finishes and launches second game");
            int secondPid = ((Process)MainField(form, "dummyProcess")).Id;
            Check(((GameItem)MainField(form, "activeSimulatingGame")).exe == q2.exe,
                "queue: second game is now the active simulation");

            WaitUntil(() => !(bool)MainField(form, "isQueueRunning") &&
                            (QueuePhase)MainField(form, "queuePhase") == QueuePhase.Idle,
                6000, "queue: second game completes and finishes the queue");
            Check(queue[1].State == QueueItemState.Completed && HasProcessExited(secondPid),
                "queue: final game is marked Completed and its helper exited");

            // Verify cancelling during BreakCooldown resets cleanly to idle.
            queue[0].State = QueueItemState.Pending; queue[0].PlannedPlaySeconds = 1; queue[0].PlannedBreakAfterSeconds = 30;
            queue[1].State = QueueItemState.Pending; queue[1].PlannedPlaySeconds = 1;
            Call(form, "StartQueue");
            WaitUntil(() => (QueuePhase)MainField(form, "queuePhase") == QueuePhase.BreakCooldown,
                8000, "queue cancel test: reaches break cooldown");
            Call(form, "StopQueue");
            Check(!(bool)MainField(form, "isQueueRunning") && (QueuePhase)MainField(form, "queuePhase") == QueuePhase.Idle,
                "StopQueue during break cooldown returns cleanly to idle");

            // Adding a game to queue while a standalone game is already running auto-includes row 0.
            queue.Clear();
            Call(form, "ToggleQueuePanel");
            Call(form, "SelectGame", q1);
            Call(form, "LaunchSimulation", q1.exe);
            WaitUntil(() => MainField(form, "dummyProcess") != null && (bool)MainField(form, "isSimulatingMode"),
                8000, "auto-queue: standalone game starts");
            Call(form, "AddGameToQueue", q2);
            Check(queue.Count == 2 && queue[0].Game.exe == q1.exe && queue[0].State == QueueItemState.Running &&
                  queue[1].Game.exe == q2.exe && queue[1].State == QueueItemState.Pending &&
                  (bool)MainField(form, "isQueueRunning") && strip.Visible && hero.Visible && switcher.Visible,
                "adding to queue while running auto-includes the active game at slot 0 while keeping hero/switcher visible");
            Call(form, "StopQueue");
        }
        finally { DisposeForm(form); }
    }

    private static void SearchDropdownLayout()
    {
        // Each text line must get a box at least as tall as its font's real line height,
        // otherwise GDI clips descenders (the "overwatch.exe" bottom-half cut-off bug).
        using (SearchDropdownList list = new SearchDropdownList())
        {
            list.Size = new Size(288, 180);
            list.SetResults(new List<GameItem> { Game("", "Overwatch", "overwatch.exe"), Game("", "Dota 2", "dota2.exe") });
            int rowHeight = (int)typeof(SearchDropdownList).GetProperty("ItemHeight", Private).GetValue(list, null);
            int titleLine = (int)Field(list, "_titleLineHeight");
            int exeLine = (int)Field(list, "_exeLineHeight");
            const TextFormatFlags measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Check(exeLine >= TextRenderer.MeasureText("gjpqy.exe", FontManager.ListExeFont, Size.Empty, measure).Height,
                "dropdown exe line box fits the exe font's descenders");
            Check(titleLine >= TextRenderer.MeasureText("gjpqy", FontManager.SmallFont, Size.Empty, measure).Height,
                "dropdown title line box fits the title font");
            Check(titleLine + exeLine <= rowHeight, "dropdown title and exe lines fit inside one row");
            Check(FontManager.ListExeFont.Size < FontManager.SmallFont.Size + 0.01f, "dropdown exe line does not out-weigh the title");
            using (Bitmap bmp = new Bitmap(288, 180)) list.DrawToBitmap(bmp, new Rectangle(0, 0, 288, 180));
        }
    }

    private static System.Windows.Forms.Message ClickOn(Control target, Point client)
    {
        // WM_LBUTTONDOWN as Windows would post it to the child under the cursor (client coords in lParam).
        return System.Windows.Forms.Message.Create(target.Handle, 0x0201, (IntPtr)1, (IntPtr)((client.Y << 16) | (client.X & 0xFFFF)));
    }

    private static void SearchLightDismiss()
    {
        MainForm form = new MainForm(new string[] { "--launcher" });
        try
        {
            form.Show();
            Application.DoEvents();
            Panel search = (Panel)MainField(form, "pnlInlineSearch");
            Control dropdown = (Control)MainField(form, "searchDropdown");
            Control switcher = (Control)MainField(form, "switcherControl");
            Control status = (Control)MainField(form, "lblDiscordStatus");

            Call(form, "OpenInlineSearch");
            System.Windows.Forms.Message m = ClickOn(dropdown, new Point(20, 20));
            form.PreFilterMessage(ref m);
            Check(search.Visible && dropdown.Visible, "clicking inside the dropdown keeps search open");
            m = ClickOn(search, new Point(5, 5));
            form.PreFilterMessage(ref m);
            Check(search.Visible, "clicking inside the search box keeps search open");

            // Clicks on child controls never reach MainForm.OnMouseDown; this was the original bug.
            m = ClickOn(switcher, new Point(4, 4));
            Check(!form.PreFilterMessage(ref m), "outside click is passed through, not swallowed");
            Check(!search.Visible && !dropdown.Visible, "clicking a child control outside search closes the dropdown");

            Call(form, "OpenInlineSearch");
            m = ClickOn(status, new Point(4, 4));
            form.PreFilterMessage(ref m);
            Check(!search.Visible, "clicking the status label closes the dropdown");

            Call(form, "OpenInlineSearch");
            typeof(Form).GetMethod("OnDeactivate", Private).Invoke(form, new object[] { EventArgs.Empty });
            Check(!search.Visible && !dropdown.Visible, "losing focus to another app closes the dropdown");
            Check(!(bool)MainField(form, "_outsideClickFilterActive"), "message filter is removed once search closes");

            Call(form, "OpenInlineSearch");
        }
        finally { DisposeForm(form); }
        Check(!(bool)MainField(form, "_outsideClickFilterActive"), "disposing with search open unregisters the message filter");
    }

    private static void QuestExplorerTests()
    {
        Check(QuestDatabase.ParseCloudflareTraceCountry("fl=1\nloc=GB\ntls=TLSv1.3\n") == "UK",
            "cloudflare trace parses loc=GB and normalizes to UK");
        Check(QuestDatabase.NormalizeCountryCode("gb") == "UK", "country normalization maps gb to UK");

        DateTimeOffset now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        string tSoonStart = now.AddDays(-2).ToString("o");
        string tSoonEnd = now.AddHours(24).ToString("o");
        string tLaterStart = now.AddDays(-1).ToString("o");
        string tLaterEnd = now.AddDays(10).ToString("o");
        string tOldStart = now.AddDays(-20).ToString("o");
        string tOldEnd = now.AddDays(-5).ToString("o");

        string sampleQuestJson = "["
            + "{\"id\":\"q_desktop\",\"starts_at\":\"" + tSoonStart + "\",\"expires_at\":\"" + tSoonEnd + "\","
            + "\"application\":{\"id\":\"app_match_1\",\"name\":\"Apex Legends\",\"link\":\"https://example.com\"},"
            + "\"assets\":{\"game_tile_dark\":\"PLACEHOLDER\",\"hero\":\"quests/q_desktop/hero.jpg\"},"
            + "\"messages\":{\"quest_name\":\"Apex Orbs\",\"game_title\":\"Apex Legends\",\"game_publisher\":\"EA\"},"
            + "\"task_config_v2\":{\"tasks\":{\"PLAY_ON_DESKTOP\":{\"type\":\"PLAY_ON_DESKTOP\",\"target\":900,\"applications\":[{\"id\":\"app_match_1\"}]},\"PLAY_ON_PLAYSTATION\":{\"type\":\"PLAY_ON_PLAYSTATION\",\"target\":900}}},"
            + "\"rewards_config\":{\"rewards\":[{\"type\":4,\"orb_quantity\":700,\"messages\":{\"name\":\"700 Orbs\"}}]}},"
            + "{\"id\":\"q_video\",\"starts_at\":\"" + tLaterStart + "\",\"expires_at\":\"" + tLaterEnd + "\","
            + "\"application\":{\"id\":\"app_vid_2\",\"name\":\"Valorant\",\"link\":\"\"},"
            + "\"assets\":{\"game_tile_dark\":\"quests/q_video/tile.png\"},"
            + "\"messages\":{\"quest_name\":\"Valorant Frame\",\"game_title\":\"Valorant\",\"game_publisher\":\"Riot Games\"},"
            + "\"task_config_v2\":{\"tasks\":{\"WATCH_VIDEO\":{\"type\":\"WATCH_VIDEO\",\"target\":90}}},"
            + "\"rewards_config\":{\"rewards\":[{\"type\":3,\"messages\":{\"name\":\"Riot Avatar Decoration\",\"redemption_instructions_by_platform\":{\"0\":\"\\\"Redeem in client.\\\"\"}}}]}},"
            + "{\"id\":\"q_expired\",\"starts_at\":\"" + tOldStart + "\",\"expires_at\":\"" + tOldEnd + "\","
            + "\"application\":{\"id\":\"app_old_3\",\"name\":\"Old Game\"},"
            + "\"messages\":{\"quest_name\":\"Old Bundle\",\"game_title\":\"Old Game\",\"game_publisher\":\"OldPub\"},"
            + "\"task_config_v2\":{\"tasks\":{\"ACHIEVEMENT_IN_ACTIVITY\":{\"type\":\"ACHIEVEMENT_IN_ACTIVITY\",\"target\":0}}},"
            + "\"rewards_config\":{\"rewards\":[{\"type\":1,\"messages\":{\"name\":\"In-Game Code\"}}]}}"
            + "]";

        string sampleRegionJson = "{\"quests\":["
            + "{\"id\":\"q_desktop\",\"show_age_gate\":true,\"is_global\":false,\"regions\":{\"include\":[\"UK\",\"DE\"],\"exclude\":[\"RU\"]}},"
            + "{\"id\":\"q_video\",\"show_age_gate\":false,\"is_global\":false,\"regions\":[\"US\"]},"
            + "{\"id\":\"q_expired\",\"show_age_gate\":false,\"is_global\":true,\"regions\":[]}"
            + "]}";

        List<QuestItem> parsed = QuestDatabase.ParseQuestCatalogJson(sampleQuestJson, sampleRegionJson);
        Check(parsed.Count == 3, "parses all 3 quests from quest.json");

        QuestItem qDesk = parsed[0];
        QuestItem qVid = parsed[1];
        QuestItem qExp = parsed[2];

        Check(qDesk.HasDesktopTask && qDesk.DesktopTaskTargetSeconds == 900 && qDesk.HasOrbsReward && qDesk.IsAgeRestricted,
            "desktop orbs quest task, reward, and age gate parsed");
        Check(qDesk.IsActive(now) && qDesk.IsEndingSoon(now) && !qDesk.IsExpired(now),
            "quest expiring within 24h is active and ending soon");
        Check(qDesk.IsEligibleInRegion("GB") && qDesk.IsEligibleInRegion("DE") && !qDesk.IsEligibleInRegion("US") && !qDesk.IsEligibleInRegion("RU"),
            "object region include/exclude and GB->UK normalization work");

        Check(qVid.HasCollectibleReward && !qVid.HasDesktopTask && qVid.IsActive(now) && !qVid.IsEndingSoon(now),
            "video collectible quest parsed and not ending soon");
        Check(qVid.rewards[0].redemption_instructions == "Redeem in client.",
            "redemption instructions strip outer quotes");
        Check(qVid.IsEligibleInRegion("US") && !qVid.IsEligibleInRegion("UK"),
            "array region allowlist parsed correctly");

        Check(qExp.IsExpired(now) && !qExp.IsActive(now) && qExp.IsEligibleInRegion("TR"),
            "expired global quest status and region check work");

        HashSet<string> doneIds = new HashSet<string>(StringComparer.Ordinal) { "q_video" };
        Check(QuestDatabase.FilterQuests(parsed, "active", "", false, "", false, doneIds, "ending_soon", now).Count == 2, "active filter returns 2");
        Check(QuestDatabase.FilterQuests(parsed, "desktop", "", false, "", false, doneIds, "ending_soon", now).Count == 1, "desktop filter returns 1");
        Check(QuestDatabase.FilterQuests(parsed, "ending", "", false, "", false, doneIds, "ending_soon", now).Count == 1, "ending filter returns 1");
        Check(QuestDatabase.FilterQuests(parsed, "orbs", "", false, "", false, doneIds, "ending_soon", now).Count == 1, "orbs filter returns 1");
        Check(QuestDatabase.FilterQuests(parsed, "collectibles", "", false, "", false, doneIds, "ending_soon", now).Count == 1, "collectibles filter returns 1");
        Check(QuestDatabase.FilterQuests(parsed, "expired", "", false, "", false, doneIds, "ending_soon", now).Count == 1, "expired filter returns 1");
        Check(QuestDatabase.FilterQuests(parsed, "done", "", false, "", false, doneIds, "ending_soon", now).Count == 1, "done filter returns 1");
        Check(QuestDatabase.FilterQuests(parsed, "all", "", false, "", false, doneIds, "ending_soon", now).Count == 3, "all filter returns 3");
        Check(QuestDatabase.FilterQuests(parsed, "active", "riot", false, "", false, doneIds, "ending_soon", now).Count == 1, "search matches publisher");
        Check(QuestDatabase.FilterQuests(parsed, "active", "", true, "GB", false, doneIds, "ending_soon", now).Count == 1, "region filter with GB keeps UK quest only");
        Check(QuestDatabase.FilterQuests(parsed, "active", "", false, "", true, doneIds, "ending_soon", now).Count == 1, "hideCompleted hides q_video");

        List<QuestItem> byName = QuestDatabase.FilterQuests(parsed, "active", "", false, "", false, doneIds, "name", now);
        Check(byName[0].id == "q_desktop" && byName[1].id == "q_video", "sort by name orders alphabetically");
        List<QuestItem> byNewest = QuestDatabase.FilterQuests(parsed, "active", "", false, "", false, doneIds, "newest", now);
        Check(byNewest[0].id == "q_video" && byNewest[1].id == "q_desktop", "sort by newest orders latest starts_at first");

        // Deduplicate regional + global variants when country filter is active and flag as alsoGlobal
        List<QuestItem> dupList = new List<QuestItem>
        {
            new QuestItem { id = "q_dup_global", quest_name = "Illegal Soccer", application_id = "363445", expires_at = tSoonEnd, region = null },
            new QuestItem { id = "q_dup_uk", quest_name = "Illegal Soccer", application_id = "363445", expires_at = tSoonEnd, region = new QuestRegionInfo { is_global = false, include = new List<string> { "UK" } } }
        };
        HashSet<string> alsoGlobal;
        List<QuestItem> dedupUk = QuestDatabase.FilterQuests(dupList, "active", "", true, "UK", false, doneIds, "ending_soon", now, out alsoGlobal);
        Check(dedupUk.Count == 1 && dedupUk[0].id == "q_dup_uk" && alsoGlobal.Contains("q_dup_uk"),
            "UK region filter merges global+UK duplicates into single UK quest marked alsoGlobal");
        HashSet<string> noGlobal;
        List<QuestItem> allVariants = QuestDatabase.FilterQuests(dupList, "active", "", false, "UK", false, doneIds, "ending_soon", now, out noGlobal);
        Check(allVariants.Count == 2 && noGlobal.Count == 0, "unfiltered view preserves all regional variants");

        // Local state persistence round-trip
        QuestDatabase.Instance.SetQuestsForTesting(parsed, "GB");
        bool nowDone = QuestDatabase.Instance.ToggleCompleted("q_desktop");
        Check(nowDone && QuestDatabase.Instance.IsCompleted("q_desktop") && File.Exists(QuestDatabase.Instance.StateFilePath),
            "ToggleCompleted persists quest_state.json");
        ((HashSet<string>)Field(QuestDatabase.Instance, "_completedIds")).Clear();
        QuestDatabase.Instance.LoadUserState();
        Check(QuestDatabase.Instance.IsCompleted("q_desktop"), "LoadUserState restores completed IDs from disk");
        QuestDatabase.Instance.ToggleCompleted("q_desktop"); // toggle back clean

        // GameDatabase resolution by application.id + fallback
        lock (Field(Database, "_lock"))
        {
            List<GameItem> games = (List<GameItem>)Field(Database, "_games");
            games.Add(Game("app_match_1", "Apex Legends", "r5apex.exe"));
        }
        GameItem matched = QuestDatabase.Instance.ResolveGameForQuest(qDesk);
        Check(matched != null && matched.id == "app_match_1" && matched.exe == "r5apex.exe",
            "ResolveGameForQuest matches detectable game by application.id");
        GameItem fallback = QuestDatabase.Instance.ResolveGameForQuest(new QuestItem { application_id = "unlisted_99", application_name = "My Cool Game" });
        Check(fallback != null && fallback.exe == "MyCoolGame.exe",
            "ResolveGameForQuest infers executable when application.id is unlisted");

        // Flag icons & auto-filtering when selecting a country
        Check(FlagIcons.GetFlag("US").Size == new Size(18, 12), "US flag is 18x12");
        Check(FlagIcons.GetFlag("ES").Size == new Size(18, 12), "ES flag is 18x12");
        Check(object.ReferenceEquals(FlagIcons.GetFlag("GB"), FlagIcons.GetFlag("UK")), "GB flag normalizes to UK flag");
        Check(FlagIcons.GetGlobeIcon().Size == new Size(18, 12), "globe icon is 18x12");

        // Simulation sync: matches active quest without auto-marking Done, and clears cleanly
        Check(QuestDatabase.DoesQuestMatchGame(qDesk, matched), "DoesQuestMatchGame matches desktop quest by application.id");
        Check(QuestDatabase.DoesQuestMatchGame(qDesk, Game("", "Apex Legends", "apex.exe")), "DoesQuestMatchGame matches desktop quest by game name fallback");
        Check(!QuestDatabase.DoesQuestMatchGame(qVid, Game("app_vid_2", "Valorant", "valorant.exe")), "DoesQuestMatchGame ignores non-desktop quests");
        Check(QuestDatabase.Instance.GetActiveDesktopQuestTargetSeconds(matched, now) == 900,
            "GetActiveDesktopQuestTargetSeconds returns 900s for active uncompleted desktop quest");

        List<string> done1 = QuestDatabase.Instance.UpdateActiveSimulationProgress(new List<GameItem> { matched }, 900, now);
        Check(done1.Count == 0 && QuestDatabase.Instance.IsQuestSimulating("q_desktop") &&
              !QuestDatabase.Instance.IsCompleted("q_desktop"),
            "simulation sync marks quest as simulating without falsely auto-completing Done");

        // QuestsForm UI + Simulate/Stop/Queue callbacks + Flag region selection + Paint + Disposal
        using (QuestsForm qForm = new QuestsForm())
        {
            GameItem simGame = null;
            GameItem stopGame = null;
            GameItem queuedGame = null;
            qForm.SimulateRequested += g => simGame = g;
            qForm.StopSimulateRequested += g => stopGame = g;
            qForm.QueueRequested += g => queuedGame = g;
            qForm.SetFiltersForTesting("all", "", false, false, "ending_soon");
            qForm.Show();
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(qForm.Width, qForm.Height))
                qForm.DrawToBitmap(bmp, new Rectangle(0, 0, qForm.Width, qForm.Height));

            QuestsPanel panel = (QuestsPanel)Field(qForm, "_panel");
            QuestGridControl grid = (QuestGridControl)Field(panel, "_grid");
            panel.SelectRegionOverride("ES");
            Application.DoEvents();
            List<QuestItem> esQuests = (List<QuestItem>)Field(grid, "_quests");
            int[] esCounts = (int[])Field(panel, "_catCounts");
            Check((bool)Field(panel, "_filterRegionOnly") && esQuests.Count == 1 && esQuests[0].id == "q_expired" && esCounts[0] == 0,
                "selecting ES flag enables region filter, shows only ES-eligible quests, and updates region-aware counts");

            panel.SelectRegionOverride("US");
            Application.DoEvents();
            List<QuestItem> usQuests = (List<QuestItem>)Field(grid, "_quests");
            int[] usCounts = (int[])Field(panel, "_catCounts");
            Check((bool)Field(panel, "_filterRegionOnly") && usQuests.Count == 2 && usCounts[0] == 1,
                "selecting US flag enables region filter and updates region-aware active pill count");

            panel.SelectAllRegions();
            Application.DoEvents();
            List<QuestItem> allRegionQuests = (List<QuestItem>)Field(grid, "_quests");
            Check(!(bool)Field(panel, "_filterRegionOnly") && allRegionQuests.Count == 3,
                "selecting All Regions disables region filter and shows all quests");

            qForm.OpenDetailForTesting(qDesk);
            Application.DoEvents();
            using (Bitmap bmpDetail = new Bitmap(qForm.Width, qForm.Height))
                qForm.DrawToBitmap(bmpDetail, new Rectangle(0, 0, qForm.Width, qForm.Height));

            panel.HandleSimulateQuest(qDesk);
            panel.HandleStopSimulateQuest(qDesk);
            panel.HandleQueueQuest(qDesk);
            Check(simGame != null && simGame.exe == "r5apex.exe", "QuestsForm SimulateRequested emits resolved GameItem");
            Check(stopGame != null && stopGame.exe == "r5apex.exe", "QuestsForm StopSimulateRequested emits resolved GameItem");
            Check(queuedGame != null && queuedGame.exe == "r5apex.exe", "QuestsForm QueueRequested emits resolved GameItem");

            // Resize hit-testing: child grid passes bottom-right corner through (HTTRANSPARENT) to QuestsForm
            Point cornerScreen = qForm.PointToScreen(new Point(qForm.ClientSize.Width - 2, qForm.ClientSize.Height - 2));
            IntPtr cornerLParam = (IntPtr)(((cornerScreen.Y & 0xFFFF) << 16) | (cornerScreen.X & 0xFFFF));
            System.Windows.Forms.Message gridHit = System.Windows.Forms.Message.Create(grid.Handle, 0x0084, IntPtr.Zero, cornerLParam);
            object[] hitArgs = new object[] { grid, gridHit };
            Check((bool)typeof(QuestsForm).GetMethod("TryForwardResizeHitTest", BindingFlags.Public | BindingFlags.Static).Invoke(null, hitArgs) &&
                  ((System.Windows.Forms.Message)hitArgs[1]).Result == (IntPtr)(-1),
                "QuestGridControl forwards edge WM_NCHITTEST as HTTRANSPARENT (-1)");

            qForm.Close();
        }

        // Verify MainForm.OnQuestSimulateRequested launches simulation, btnAction stays inside left 420px column when docked, and Stop syncs
        TestForm hostForm = new TestForm();
        try
        {
            hostForm.Show();
            Application.DoEvents();
            Rectangle qBtn = (Rectangle)MainField(hostForm, "QuestsBtnRect");
            Check(qBtn.Top < 38, "Quests button sits in top-left header away from bottom status text");

            Call(hostForm, "OnQuestSimulateRequested", matched);
            Check(hostForm.LaunchCount == 1 && hostForm.LaunchId == "app_match_1",
                "OnQuestSimulateRequested selects and launches simulation for the quest game");
            Call(hostForm, "OnQuestQueueRequested", matched);
            List<QueueItem> hostQueue = (List<QueueItem>)MainField(hostForm, "queueItems");
            Check(hostQueue.Count == 1 && hostQueue[0].Game.id == "app_match_1" && hostQueue[0].PlannedPlaySeconds >= 960,
                "OnQuestQueueRequested adds quest game to queue with smart duration >= target + buffer");

            // Unified single-window expansion & collapse (0px gap) + btnAction remains centered in left 420px column
            typeof(MainForm).GetMethod("OpenQuestsWindow", Private).Invoke(hostForm, new object[] { });
            Application.DoEvents();
            QuestsPanel dockedPanel = (QuestsPanel)MainField(hostForm, "questsPanel");
            PillActionButton actionBtn = (PillActionButton)MainField(hostForm, "btnAction");
            actionBtn.SetRunningState(true);
            Check((bool)MainField(hostForm, "isQuestsDocked") && dockedPanel != null && dockedPanel.Visible &&
                  hostForm.ClientSize.Width >= 1140 && dockedPanel.Left == 421 &&
                  actionBtn.Left >= 0 && actionBtn.Right <= 420,
                "OpenQuestsWindow expands MainForm into unified window while keeping Launch/Stop button centered in left 420px column");

            // Simulate bidirectional Stop sync when activeSimulatingGame matches
            MainSet(hostForm, "isSimulatingMode", true);
            MainSet(hostForm, "activeSimulatingGame", matched);
            Call(hostForm, "SyncActiveQuestsProgress", 0);
            Check(QuestDatabase.Instance.IsQuestSimulating("q_desktop"), "SyncActiveQuestsProgress marks matching quest as simulating");
            Call(hostForm, "OnQuestStopRequested", matched);
            Check(!((bool)MainField(hostForm, "isSimulatingMode")) && !QuestDatabase.Instance.IsQuestSimulating("q_desktop"),
                "OnQuestStopRequested stops active simulation and clears simulating state on quest card");

            typeof(MainForm).GetMethod("OpenQuestsWindow", Private).Invoke(hostForm, new object[] { });
            Application.DoEvents();
            Check(!(bool)MainField(hostForm, "isQuestsDocked") && !dockedPanel.Visible &&
                  hostForm.ClientSize.Width == 420 && hostForm.ClientSize.Height == 480,
                "clicking Quests again collapses MainForm back to 420x480");
        }
        finally { DisposeForm(hostForm); }

        QuestDatabase.Instance.ClearActiveSimulation();
        Check(!QuestDatabase.Instance.IsQuestSimulating("q_desktop"), "ClearActiveSimulation clears simulating state");
    }
}
