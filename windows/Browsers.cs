// Supported browsers, their memory, and the settings saved for each.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RamLimiter
{
    // One browser's saved settings. The field names are the names in settings.json.
    sealed class Prefs
    {
        public bool enabled = true;
        public double limitGB = 4;
        public int interval = 60;
        public int graceMinutes = 3;
        public string icon = "nap";
        public List<string> neverSites = new List<string>();
        public bool protectTyped = true;
        public bool protectBusy = true;
        public bool removed; // stays ignored until its extension is reloaded
        public double checkCost; // CPU seconds per check on this PC
        public string freedDay;
        public double freedBytes;
        public long pausedUntil; // UTC ticks
        public long lastSeen; // UTC ticks
    }

    sealed class LogEntry
    {
        public string text;
        public string browser;
        public string url;
        public int? tab;
    }

    sealed class Saved
    {
        public List<string> browsers = new List<string>(); // listed in the menu
        public string selected;
        public bool loginConfigured;
        public List<LogEntry> log = new List<LogEntry>();
        public Dictionary<string, Prefs> prefs = new Dictionary<string, Prefs>();
    }

    static class Store
    {
        static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAMLimiter");
        static readonly string FileName = Path.Combine(Folder, "settings.json");

        public static Saved Data = Load();

        static Saved Load()
        {
            try
            {
                var saved = new JavaScriptSerializer().Deserialize<Saved>(File.ReadAllText(FileName, Encoding.UTF8));
                if (saved == null) return new Saved();
                if (saved.browsers == null) saved.browsers = new List<string>();
                if (saved.log == null) saved.log = new List<LogEntry>();
                if (saved.prefs == null) saved.prefs = new Dictionary<string, Prefs>();
                return saved;
            }
            catch (Exception) { return new Saved(); } // first run, or a damaged file
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = FileName + ".new";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(Data), new UTF8Encoding(false));
                if (File.Exists(FileName)) File.Replace(temp, FileName, null);
                else File.Move(temp, FileName);
            }
            catch (IOException) { } // tried again at the next change
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class Browser
    {
        public readonly string Id, Name;
        readonly string exe;
        readonly string[] roots; // folders the program is installed in
        readonly bool versioned; // Opera keeps its program in a folder named after the version

        public readonly Prefs Prefs;
        public long Used; // last measurement, for the extension
        public DateTime Measured = DateTime.MinValue; // MinValue: no reading
        public bool Measuring;
        public readonly List<Action> AfterMeasure = new List<Action>();
        public long BeforeUnload; // memory just before the unloads being reported
        public Report.TabCount Tabs;
        public List<string> OpenSites = new List<string>(); // kept in memory only
        public TcpClient Waiting; // the extension's open request for log clicks
        public DateTime LastWait = DateTime.MinValue;

        Browser(string id, string name, string exe, bool versioned, params string[] roots)
        {
            Id = id;
            Name = name;
            this.exe = exe;
            this.versioned = versioned;
            this.roots = roots;
            if (!Store.Data.prefs.TryGetValue(id, out Prefs) || Prefs == null)
                Store.Data.prefs[id] = Prefs = new Prefs();
            if (Prefs.neverSites == null) Prefs.neverSites = new List<string>();
        }

        // Installed for all users (Program Files) or for one user (Local AppData).
        static string[] Under(string folder)
        {
            return new[] {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            }.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.Combine(p, folder)).ToArray();
        }

        static string[] OperaRoots(string name)
        {
            return Under(name).Concat(Under(@"Programs\" + name)).ToArray();
        }

        public static readonly Browser[] All = {
            new Browser("opera", "Opera", "opera.exe", true, OperaRoots("Opera")),
            new Browser("operagx", "Opera GX", "opera.exe", true, OperaRoots("Opera GX")),
            new Browser("chrome", "Chrome", "chrome.exe", false, Under(@"Google\Chrome\Application")),
            new Browser("edge", "Edge", "msedge.exe", false, Under(@"Microsoft\Edge\Application")),
            new Browser("brave", "Brave", "brave.exe", false, Under(@"BraveSoftware\Brave-Browser\Application")),
            new Browser("vivaldi", "Vivaldi", "vivaldi.exe", false, Under(@"Vivaldi\Application")),
        };

        public bool Matches(string path)
        {
            if (path == null || !string.Equals(Path.GetFileName(path), exe, StringComparison.OrdinalIgnoreCase))
                return false;
            var folder = Path.GetDirectoryName(path);
            if (versioned && Regex.IsMatch(Path.GetFileName(folder), @"^\d+(\.\d+)+$"))
                folder = Path.GetDirectoryName(folder);
            return roots.Any(r => string.Equals(r, folder, StringComparison.OrdinalIgnoreCase));
        }

        // The program to start, or null if the browser isn't installed.
        public string Location
        {
            get { return roots.Select(r => Path.Combine(r, exe)).FirstOrDefault(File.Exists); }
        }

        /// This browser's processes, each with its private working set.
        public List<KeyValuePair<int, long>> Processes()
        {
            return Native.Snapshot(exe).Where(p => Matches(Native.PathOf(p.Key))).ToList();
        }

        public bool Open { get { return Processes().Count > 0; } }

        /// Task Manager's "Memory" column, added up over the browser's processes.
        /// Memory shared between processes isn't in it. 0 if not open or it can't be measured,
        /// so nothing gets unloaded on bad data.
        public long TotalBytes() { return Processes().Sum(p => p.Value); }

        public bool Paused { get { return Prefs.pausedUntil > DateTime.UtcNow.Ticks; } }
        public bool Active { get { return Prefs.enabled && !Paused; } }
        public int Interval
        {
            get { return new[] { 10, 20, 30, 60 }.Contains(Prefs.interval) ? Prefs.interval : 60; }
        }
        public double LimitGB { get { return Prefs.limitGB > 0 ? Prefs.limitGB : 4; } }
        public bool Connected
        {
            get
            {
                return Prefs.lastSeen > 0
                    && DateTime.UtcNow.Ticks - Prefs.lastSeen < TimeSpan.FromSeconds(Interval + 60).Ticks;
            }
        }
        public static string Today { get { return DateTime.Now.ToString("yyyy-MM-dd"); } }
        public double FreedToday { get { return Prefs.freedDay == Today ? Prefs.freedBytes : 0; } }

        public void ForgetReading()
        {
            Used = 0;
            Measured = DateTime.MinValue;
        }
    }

    static class Native
    {
        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll")]
        static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass,
                                              int reserved);

        /// (pid, private working set) of every process with this file name. One cheap call for all of them.
        /// The offsets are those of SYSTEM_PROCESS_INFORMATION on 64-bit Windows.
        public static List<KeyValuePair<int, long>> Snapshot(string exe)
        {
            var found = new List<KeyValuePair<int, long>>();
            int size = 1 << 20;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int needed;
                    int status = NtQuerySystemInformation(5, buffer, size, out needed);
                    if (status == unchecked((int)0xC0000004)) // buffer too small
                    {
                        size = Math.Max(needed, size) + (1 << 16);
                        continue;
                    }
                    if (status != 0) return found;
                    long offset = 0;
                    while (true)
                    {
                        var entry = new IntPtr(buffer.ToInt64() + offset);
                        int nameBytes = (ushort)Marshal.ReadInt16(entry, 56);
                        var namePointer = Marshal.ReadIntPtr(entry, 64);
                        if (namePointer != IntPtr.Zero
                            && string.Equals(Marshal.PtrToStringUni(namePointer, nameBytes / 2), exe,
                                             StringComparison.OrdinalIgnoreCase))
                            found.Add(new KeyValuePair<int, long>(
                                (int)Marshal.ReadInt64(entry, 80), Marshal.ReadInt64(entry, 8)));
                        int next = Marshal.ReadInt32(entry, 0);
                        if (next == 0) return found;
                        offset += next;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return found;
        }

        public static string PathOf(int pid)
        {
            var process = OpenProcess(0x1000 /* query limited information */, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(32768);
                int size = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
            }
            finally { CloseHandle(process); }
        }

        /// The process holding the client end of a loopback connection, or 0.
        public static int ConnectionOwner(int clientPort, int serverPort)
        {
            int size = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var table = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
                try
                {
                    // IPv4, connections with their owning process.
                    int result = GetExtendedTcpTable(table, ref size, false, 2, 4, 0);
                    if (result == 122) { size += 4096; continue; } // buffer too small
                    if (result != 0 || table == IntPtr.Zero) return 0;
                    int rows = Marshal.ReadInt32(table);
                    for (int i = 0; i < rows; i++)
                    {
                        int row = 4 + i * 24; // state, local address, local port, remote address, remote port, pid
                        if (Marshal.ReadInt32(table, row + 4) == 0x0100007F // 127.0.0.1
                            && Marshal.ReadInt32(table, row + 12) == 0x0100007F
                            && Port(Marshal.ReadInt32(table, row + 8)) == clientPort
                            && Port(Marshal.ReadInt32(table, row + 16)) == serverPort)
                            return Marshal.ReadInt32(table, row + 20);
                    }
                    return 0;
                }
                finally { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); }
            }
            return 0;
        }

        static int Port(int raw) { return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }

        delegate bool WindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        /// Brings the browser's topmost window to the front. Windows only allows this
        /// while we are the app being used, which is true just after a click in the log.
        public static void BringToFront(Browser browser)
        {
            var pids = new HashSet<int>(browser.Processes().Select(p => p.Key));
            EnumWindows((window, data) =>
            {
                int pid;
                GetWindowThreadProcessId(window, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
                if (IsIconic(window)) ShowWindow(window, 9 /* restore */);
                SetForegroundWindow(window);
                return false; // windows come front to back, so the first is the one
            }, IntPtr.Zero);
        }
    }
}
