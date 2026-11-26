// Tray app: measures each browser's memory and tells its extension when to unload tabs.
// Behaviour follows app/main.swift.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("RAM Limiter")]
[assembly: AssemblyProduct("RAM Limiter")]
[assembly: AssemblyDescription("A memory limit for your browser.")]
[assembly: AssemblyCopyright("MIT license")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]

namespace RamLimiter
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            if (args.Length > 0 && args[0] == "--once") // prints each open browser's memory
            {
                foreach (var browser in Browser.All.Where(b => b.Open))
                {
                    var clock = Stopwatch.StartNew();
                    var used = browser.TotalBytes();
                    Console.WriteLine("{0}: {1:0.000} GB, {2} processes, {3:0.0} ms", browser.Name,
                        used / App.GB, browser.Processes().Count, clock.Elapsed.TotalMilliseconds);
                }
                return;
            }
            if (args.Length > 1 && args[0] == "--icons") { Icons.WriteSheet(args[1]); return; }
            if (args.Length > 1 && args[0] == "--ico") { Icons.WriteIco(args[1]); return; }

            bool first;
            using (new Mutex(true, @"Local\RAMLimiter", out first))
            {
                if (!first) return; // already running
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }

    static class Login
    {
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // Where Windows notes that you switched a startup app off in Settings or Task Manager.
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string Name = "RAMLimiter";

        public static bool Enabled
        {
            get
            {
                using (var run = Registry.CurrentUser.OpenSubKey(Run))
                    if (run == null || run.GetValue(Name) == null) return false;
                using (var approved = Registry.CurrentUser.OpenSubKey(Approved))
                {
                    var state = approved == null ? null : approved.GetValue(Name) as byte[];
                    return state == null || state.Length == 0 || (state[0] & 1) == 0;
                }
            }
        }

        public static void Set(bool on)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(Run))
            {
                if (on) run.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else run.DeleteValue(Name, false);
            }
            // Ticking it in our menu overrides an earlier "off" from Windows settings.
            using (var approved = Registry.CurrentUser.OpenSubKey(Approved, true))
                if (approved != null) approved.DeleteValue(Name, false);
        }
    }

    // Draws the browser's name after "Browser" in grey, and the pictures in the Icon menu.
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public ToolStripItem Named; // the "Browser" item
        public string Name = "";
        public readonly Dictionary<ToolStripItem, Image> Pictures = new Dictionary<ToolStripItem, Image>();
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            base.OnRenderItemText(e);
            Image picture;
            if (Pictures.TryGetValue(e.Item, out picture))
                e.Graphics.DrawImageUnscaled(picture, e.TextRectangle.X,
                                             (e.Item.Height - picture.Height) / 2);
            if (e.Item != Named) return;
            var after = e.TextRectangle;
            after.X += TextRenderer.MeasureText(e.Text + "  ", e.TextFont).Width;
            after.Width = e.Item.Width - after.X;
            TextRenderer.DrawText(e.Graphics, Name, e.TextFont, after, SystemColors.GrayText,
                                  e.TextFormat | TextFormatFlags.NoPrefix);
        }
    }

    sealed class App : ApplicationContext
    {
        public const double GB = 1073741824.0;

        readonly Control ui = new Control(); // runs work on the interface thread
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly MenuRenderer renderer = new MenuRenderer();
        readonly ToolStripMenuItem browserItem = new ToolStripMenuItem("Browser");
        readonly ToolStripSeparator browserLine = new ToolStripSeparator();
        readonly ToolStripMenuItem toggle = new ToolStripMenuItem("Limiter on");
        readonly ToolStripMenuItem pauseMenu = new ToolStripMenuItem("Pause");
        readonly ToolStripMenuItem resume = new ToolStripMenuItem("Resume now");
        readonly ToolStripMenuItem typed = new ToolStripMenuItem("Protect unsaved text");
        readonly ToolStripMenuItem busy = new ToolStripMenuItem("Protect calls and uploads");
        readonly ToolStripMenuItem neverMenu = new ToolStripMenuItem("Never unload");
        readonly ToolStripMenuItem usage = new ToolStripMenuItem();
        readonly ToolStripMenuItem link = new ToolStripMenuItem();
        readonly ToolStripMenuItem freed = new ToolStripMenuItem();
        readonly ToolStripMenuItem tabCount = new ToolStripMenuItem();
        readonly ToolStripMenuItem login = new ToolStripMenuItem("Start at login");
        List<ToolStripMenuItem> limitItems, intervalItems, graceItems, iconItems;

        readonly Server server = new Server();
        bool serverError;
        Browser peeked; // the browser under the pointer in the Browser submenu
        double shown; // bytes on the icon right now
        double target; // bytes the icon is gliding towards
        readonly System.Windows.Forms.Timer glide = new System.Windows.Forms.Timer { Interval = 100 };
        bool sampling;
        string lastDrawn = ""; // what the tray currently shows, to skip identical redraws
        string lastChoices = "";
        IntPtr iconHandle;
        Form logWindow;
        ListBox logList;
        List<LogEntry> shownLog = new List<LogEntry>();

        static Saved Data { get { return Store.Data; } }

        /// Browsers whose extension has connected at some point, and haven't been removed.
        static List<Browser> Known
        {
            get { return Browser.All.Where(b => Data.browsers.Contains(b.Id)).ToList(); }
        }
        static Browser Selected
        {
            get
            {
                var known = Known;
                return known.FirstOrDefault(b => b.Id == Data.selected) ?? known.FirstOrDefault()
                    ?? Browser.All.FirstOrDefault(b => b.Location != null) ?? Browser.All[0];
            }
        }
        /// What the menu and tray show: the previewed browser, else the chosen one.
        Browser Current { get { return peeked ?? Selected; } }

        public App()
        {
            ui.CreateControl();
            var handle = ui.Handle;
            BuildMenu();
            tray.ContextMenuStrip = menu;
            tray.MouseUp += (s, e) =>
            {
                // A left click opens the same menu as a right click.
                if (e.Button == MouseButtons.Left)
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(tray, null);
            };

            glide.Tick += (s, e) =>
            {
                shown += (target - shown) * 0.4;
                if (Math.Abs(target - shown) < 40000000)
                {
                    shown = target;
                    glide.Stop();
                }
                ShowIcon(shown / GB, true);
            };

            // Turn on start at login the first time; afterwards the choice is respected.
            if (!Data.loginConfigured && TryLogin(true))
            {
                Data.loginConfigured = true;
                Store.Save();
            }
            // If the app's folder was moved, point the startup entry at where it is now.
            else if (Login.Enabled) TryLogin(true);

            server.OnStatus = (client, browser, report) => Post(() =>
            {
                Record(report, browser);
                Answer(client, browser);
            });
            server.OnJump = (client, browser) => Post(() => Hold(client, browser));
            serverError = !server.Start();

            Refresh();
            tray.Visible = true;
            Measure(Current);
            // Menu title and status only; keeps updating while the menu is open.
            var slow = new System.Windows.Forms.Timer { Interval = 5000 };
            slow.Tick += (s, e) => Refresh();
            slow.Start();
            var live = new System.Windows.Forms.Timer { Interval = 1000 };
            live.Tick += (s, e) => Sample();
            live.Start();
        }

        void Post(Action work) { ui.BeginInvoke(work); }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }

        // ---- Measuring ----

        // Only while a browser's limiter is active, off the interface thread.
        void Measure(Browser browser, Action done = null)
        {
            if (!browser.Active)
            {
                browser.ForgetReading();
                if (browser == Current) { glide.Stop(); shown = 0; }
                Refresh();
                if (done != null) done();
                return;
            }
            if ((DateTime.UtcNow - browser.Measured).TotalSeconds < 2)
            {
                Refresh();
                if (done != null) done();
                return;
            }
            if (done != null) browser.AfterMeasure.Add(done);
            if (browser.Measuring) return;
            browser.Measuring = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                double cost;
                var value = Read(browser, out cost);
                Post(() =>
                {
                    browser.Measuring = false;
                    if (browser.Active)
                    {
                        browser.Used = value;
                        browser.Measured = value > 0 ? DateTime.UtcNow : DateTime.MinValue; // 0: not open
                        if (browser == Current) Show(value);
                    }
                    else browser.ForgetReading();
                    // Smoothed, so one unusual check doesn't swing the figure.
                    if (value > 0)
                        browser.Prefs.checkCost = browser.Prefs.checkCost == 0 ? cost
                            : browser.Prefs.checkCost * 0.8 + cost * 0.2;
                    Refresh();
                    var waiting = browser.AfterMeasure.ToList();
                    browser.AfterMeasure.Clear();
                    foreach (var next in waiting) next();
                });
            });
        }

        static long Read(Browser browser, out double seconds)
        {
            var clock = Stopwatch.StartNew();
            long value;
            try { value = browser.TotalBytes(); }
            catch (Exception) { value = 0; }
            seconds = clock.Elapsed.TotalSeconds;
            return value;
        }

        // Once a second: moves the shown figure. Unloading never uses this.
        void Sample()
        {
            var browser = Current;
            if (sampling || !browser.Active || browser.Measured == DateTime.MinValue) return;
            sampling = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                double cost;
                var value = Read(browser, out cost);
                Post(() =>
                {
                    sampling = false;
                    if (browser != Current || !browser.Active) return;
                    if (value > 0) { Show(value); return; }
                    browser.ForgetReading(); // it was quit
                    glide.Stop();
                    shown = 0;
                    Refresh();
                });
            });
        }

        // Moves the icon smoothly to a new reading instead of jumping.
        void Show(double bytes)
        {
            target = bytes;
            // Changes too small to see aren't animated.
            if (shown == 0 || Math.Abs(bytes - shown) < 40000000)
            {
                glide.Stop();
                shown = bytes;
                ShowIcon(shown / GB, bytes > 0);
                if (menu.Visible) Refresh();
                return;
            }
            glide.Start();
        }

        // Switches the menu and tray to whichever browser is now current.
        void ShowCurrent()
        {
            glide.Stop();
            shown = Current.Active ? Current.Used : 0;
            Refresh();
            Measure(Current);
        }

        void Peek(Browser browser)
        {
            if (browser == peeked) return;
            peeked = browser;
            ShowCurrent();
        }

        // ---- Menu ----

        static ToolStripMenuItem Item(string text, Action click)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => click();
            return item;
        }

        List<ToolStripMenuItem> Choices<T>(string title, IEnumerable<KeyValuePair<string, T>> options,
                                           Action<T> choose)
        {
            var parent = new ToolStripMenuItem(title);
            var items = new List<ToolStripMenuItem>();
            foreach (var option in options)
            {
                var value = option.Value;
                var item = Item(option.Key, () => Change(() => choose(value)));
                item.Tag = value;
                items.Add(item);
                parent.DropDownItems.Add(item);
            }
            menu.Items.Add(parent);
            return items;
        }

        static KeyValuePair<string, T> Option<T>(string name, T value) { return new KeyValuePair<string, T>(name, value); }

        static string IntervalName(int seconds)
        {
            return seconds < 60 ? seconds + " seconds" : "1 minute";
        }

        void BuildMenu()
        {
            renderer.Named = browserItem;
            menu.Renderer = renderer;
            foreach (var info in new[] { usage, link, freed, tabCount }) info.Enabled = false;

            browserItem.DropDownItems.Add("-"); // filled when opened
            browserItem.DropDownOpening += (s, e) => ListBrowsers();
            // Leaving the submenu without clicking goes back to the chosen browser.
            browserItem.DropDownClosed += (s, e) => Peek(null);
            browserItem.DropDown.Renderer = renderer;
            menu.Items.Add(browserItem);
            menu.Items.Add(browserLine);

            toggle.Click += (s, e) => Change(() =>
            {
                Current.Prefs.pausedUntil = 0;
                Current.Prefs.enabled = !Current.Prefs.enabled;
            });
            menu.Items.Add(toggle);
            limitItems = Choices("Limit", new[] { 2, 3, 4, 5, 6, 8, 10, 12 }.Select(n => Option(n + " GB", n)),
                                 n => Current.Prefs.limitGB = n);
            intervalItems = Choices("Check intervals", new[] { 10, 20, 30, 60 }.Select(n => Option(IntervalName(n), n)),
                                    n => Current.Prefs.interval = n);
            graceItems = Choices("Grace period",
                new[] { 0, 2, 3, 5, 10 }.Select(n => Option(n == 0 ? "Off" : n + " minutes", n)),
                n => Current.Prefs.graceMinutes = n);
            iconItems = Choices("Icon", Icons.Styles.Select(s => Option(s[0], s[1])),
                                style => Current.Prefs.icon = style);
            // Each choice shows what it looks like; the name appears on hover.
            var iconMenu = (ToolStripDropDownMenu)iconItems[0].Owner;
            iconMenu.ShowCheckMargin = true;
            iconMenu.ShowImageMargin = false;
            iconMenu.ShowItemToolTips = true;
            iconMenu.Renderer = renderer;
            foreach (var item in iconItems)
            {
                item.ToolTipText = item.Text;
                item.AccessibleName = item.Text;
                // The picture is the label; the spaces make room for it.
                item.Text = new string(' ', PreviewSize.Width / TextRenderer.MeasureText("  ", menu.Font).Width * 2 + 4);
            }

            neverMenu.DropDownItems.Add("-"); // filled when opened
            neverMenu.DropDownOpening += (s, e) => ListSites();

            menu.Items.Add(neverMenu);

            foreach (var choice in new[] { Option("30 minutes", 30), Option("1 hour", 60), Option("2 hours", 120) })
            {
                var minutes = choice.Value;
                pauseMenu.DropDownItems.Add(Item(choice.Key,
                    () => Change(() => Current.Prefs.pausedUntil = DateTime.UtcNow.AddMinutes(minutes).Ticks)));
            }
            pauseMenu.DropDownItems.Add(new ToolStripSeparator());
            resume.Click += (s, e) => Change(() => Current.Prefs.pausedUntil = 0);
            pauseMenu.DropDownItems.Add(resume);
            menu.Items.Add(pauseMenu);
            typed.Click += (s, e) => Change(() => Current.Prefs.protectTyped = !Current.Prefs.protectTyped);
            busy.Click += (s, e) => Change(() => Current.Prefs.protectBusy = !Current.Prefs.protectBusy);
            menu.Items.AddRange(new ToolStripItem[] {
                typed, busy, new ToolStripSeparator(), usage, link, freed, tabCount, new ToolStripSeparator(),
            });
            login.Click += (s, e) => { TryLogin(!Login.Enabled); Refresh(); };
            menu.Items.Add(login);
            // These two open a window or end the app, so the menu closes for them.
            menu.Items.Add(Item("Log", () => { menu.Close(); ShowLog(); }));
            menu.Items.Add(Item("Quit", () => { menu.Close(); ExitThread(); }));
            StayOpen(menu);

            menu.Opening += (s, e) => { Measure(Current); Refresh(); };
            menu.Closed += (s, e) => Peek(null);
        }

        // Choosing something leaves the menu open; clicking outside it or pressing Esc closes it.
        static void StayOpen(ToolStripDropDown list)
        {
            list.Closing += (s, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            foreach (var item in list.Items.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems))
                StayOpen(item.DropDown);
        }

        static bool TryLogin(bool on)
        {
            try { Login.Set(on); return true; }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "RAM Limiter");
                return false;
            }
        }

        // Every setting here belongs to the current browser.
        void Change(Action change)
        {
            var browser = Current;
            change();
            Store.Save();
            Refresh();
            Measure(browser);
        }

        // The site list is rebuilt each time its submenu opens.
        void ListSites()
        {
            var browser = Current;
            neverMenu.DropDownItems.Clear();
            var chosen = browser.Prefs.neverSites.OrderBy(s => s, StringComparer.Ordinal).ToList();
            var others = browser.Connected
                ? browser.OpenSites.Except(chosen).OrderBy(s => s, StringComparer.Ordinal).ToList()
                : new List<string>();
            foreach (var site in chosen.Concat(others))
            {
                if (others.Count > 0 && chosen.Count > 0 && site == others[0])
                    neverMenu.DropDownItems.Add(new ToolStripSeparator());
                var name = site;
                var item = new ToolStripMenuItem(name.Replace("&", "&&"))
                {
                    CheckOnClick = true, Checked = chosen.Contains(name),
                };
                item.Click += (s, e) =>
                {
                    browser.Prefs.neverSites.Remove(name);
                    if (item.Checked) browser.Prefs.neverSites.Add(name);
                    Store.Save();
                };
                neverMenu.DropDownItems.Add(item);
            }
            string note = others.Count == 0 && !browser.Connected
                ? "Open sites appear when " + browser.Name + " is connected"
                : chosen.Count == 0 && others.Count == 0 ? "No sites open" : null;
            if (note != null) neverMenu.DropDownItems.Add(new ToolStripMenuItem(note) { Enabled = false });
        }

        // Resting the pointer on a row previews that browser; clicking keeps it; ✕ removes it.
        void ListBrowsers()
        {
            browserItem.DropDownItems.Clear();
            foreach (var b in Known)
            {
                var browser = b;
                var item = new ToolStripMenuItem(browser.Name + (browser.Open ? "" : "  (not open)"))
                {
                    Checked = browser == Selected, ShortcutKeyDisplayString = "   ✕", Tag = browser,
                };
                bool onCross = false;
                item.MouseDown += (s, e) => onCross = e.X >= item.Width - 36;
                item.MouseUp += (s, e) => onCross = e.X >= item.Width - 36;
                item.MouseEnter += (s, e) => Peek(browser);
                item.MouseLeave += (s, e) => { if (peeked == browser) Peek(null); };
                item.Click += (s, e) =>
                {
                    if (onCross) Remove(browser, item);
                    else Select(browser);
                    onCross = false;
                };
                browserItem.DropDownItems.Add(item);
            }
        }

        void Select(Browser browser)
        {
            Data.selected = browser.Id;
            Store.Save();
            peeked = null;
            CheckChosenRow();
            ShowCurrent();
        }

        void CheckChosenRow()
        {
            foreach (var row in browserItem.DropDownItems.OfType<ToolStripMenuItem>())
                row.Checked = row.Tag == Selected;
        }

        void Remove(Browser browser, ToolStripItem row)
        {
            browser.Prefs.removed = true;
            Data.browsers.Remove(browser.Id);
            if (Data.selected == browser.Id) Data.selected = null;
            Store.Save();
            peeked = null;
            // Changed after the click has finished.
            Post(() =>
            {
                if (Known.Count < 2) browserItem.DropDown.Close(); // the Browser item gets hidden
                else
                {
                    browserItem.DropDownItems.Remove(row);
                    CheckChosenRow();
                }
                ShowCurrent();
            });
        }

        // ---- What is shown ----

        void Refresh()
        {
            var browser = Current;
            var used = shown / GB;
            bool measuredYet = browser.Measured != DateTime.MinValue;
            ShowIcon(used, measuredYet);
            var known = Known;
            browserItem.Available = known.Count >= 2;
            browserLine.Available = known.Count >= 2;
            renderer.Name = browser.Name;
            browserItem.Invalidate();
            toggle.Checked = browser.Prefs.enabled;
            foreach (var item in limitItems) item.Checked = (int)item.Tag == browser.LimitGB;
            foreach (var item in graceItems) item.Checked = (int)item.Tag == browser.Prefs.graceMinutes;
            foreach (var item in iconItems) item.Checked = (string)item.Tag == browser.Prefs.icon;
            foreach (var item in intervalItems)
            {
                var seconds = (int)item.Tag;
                item.Checked = seconds == browser.Interval;
                // Share of this PC's whole processor: CPU time per check, over all cores for the interval.
                var percent = browser.Prefs.checkCost / (seconds * Environment.ProcessorCount) * 100;
                // Checks are so cheap here that the figure would usually round to a puzzling 0.0%.
                item.Text = IntervalName(seconds) + (browser.Prefs.checkCost <= 0 ? ""
                    : percent < 0.05 ? " (<0.1% CPU)"
                    : string.Format(CultureInfo.InvariantCulture, " (~{0:0.0}% CPU)", percent));
            }
            pauseMenu.Enabled = browser.Prefs.enabled;
            resume.Enabled = browser.Paused;
            pauseMenu.Text = browser.Paused
                ? "Paused until " + new DateTime(browser.Prefs.pausedUntil, DateTimeKind.Utc).ToLocalTime().ToString("t")
                : "Pause";
            typed.Checked = browser.Prefs.protectTyped;
            busy.Checked = browser.Prefs.protectBusy;
            login.Checked = Login.Enabled;
            if (!browser.Active)
                usage.Text = "Not measuring (limiter " + (browser.Prefs.enabled ? "paused" : "off") + ")";
            else if (!browser.Open)
                usage.Text = browser.Name + " is not open";
            else
                usage.Text = measuredYet ? browser.Name + " is using " + Number(used) + " GB" : "Measuring…";
            var today = browser.FreedToday;
            freed.Text = "Memory Freed Today: " + (today < GB
                ? (today / 1048576).ToString("0", CultureInfo.InvariantCulture) + " MB"
                : Number(today / GB) + " GB");
            tabCount.Available = browser.Connected && browser.Tabs != null;
            if (browser.Tabs != null)
                tabCount.Text = browser.Tabs.loaded + " of " + browser.Tabs.total + " tabs loaded (at last check)";
            link.Text = serverError ? "Error: port " + Server.Port + " unavailable"
                : browser.Name + " extension: " + (browser.Connected ? "connected" : "not connected");
        }

        static string Number(double value) { return value.ToString("0.0", CultureInfo.InvariantCulture); }

        // Coloured near the limit; faded when off or paused, which also empties drawn icons.
        void ShowIcon(double used, bool measuredYet)
        {
            var browser = Current;
            var style = browser.Prefs.icon;
            var limit = browser.LimitGB;
            bool paused = browser.Paused, light = Icons.LightTaskbar();
            double level = browser.Active && measuredYet ? used / limit : 0;
            var size = SystemInformation.SmallIconSize.Width;
            // The number inside the icon: tenths below 10 GB, whole above.
            // With no reading (off, paused, browser not open) every icon shows its empty state.
            bool reading = browser.Active && measuredYet;
            var digits = !reading ? "0" : used < 9.95 ? Number(used) : used.ToString("0");

            // Redraw only when something visible changes (0.1 GB, or 1% of the dial).
            var key = string.Join(" ", browser.Id, style, browser.Prefs.enabled, paused, measuredYet,
                                  (int)(level * 100), level > 1, Number(used), limit, light, size);
            if (key == lastDrawn) return;
            lastDrawn = key;

            // Off and paused look the same here (faded); the tooltip and menu say which.
            using (var image = Icons.Draw(style, level, size, light, reading ? digits : "0/" + limit, !browser.Active))
            {
                var handle = image.GetHicon();
                tray.Icon = Icon.FromHandle(handle);
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = handle;
            }
            // Windows allows 63 characters.
            tray.Text = !browser.Active
                ? browser.Name + ": limiter " + (browser.Prefs.enabled ? "paused" : "off")
                : browser.Name + ": " + Number(used) + " of " + limit + " GB";
            UpdateIconChoices(level, digits);
        }

        static Size PreviewSize
        {
            get
            {
                var small = SystemInformation.SmallIconSize.Width;
                return new Size(small * 3, small * 9 / 8);
            }
        }

        // The choices in the Icon menu show the live level too. Every choice is a picture
        // of the same size, so they line up in one column.
        void UpdateIconChoices(double level, string digits)
        {
            var key = (int)(level * 100) + " " + digits + " " + Current.LimitGB;
            if (key == lastChoices || iconItems == null) return;
            lastChoices = key;
            var size = PreviewSize;
            foreach (var item in iconItems)
            {
                var style = (string)item.Tag;
                var picture = new Bitmap(size.Width, size.Height);
                using (var g = Graphics.FromImage(picture))
                {
                    if (style == "numbers" || style == "text")
                    {
                        var word = style == "text" ? "RAM" : digits + "/" + Current.LimitGB;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        using (var brush = new SolidBrush(Icons.Warning(level) ?? SystemColors.ControlText))
                        using (var centred = new StringFormat
                        {
                            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                            FormatFlags = StringFormatFlags.NoWrap,
                        })
                            g.DrawString(word, menu.Font, brush, new RectangleF(Point.Empty, size), centred);
                    }
                    else
                        using (var icon = Icons.Draw(style, level, size.Height, true, "")) // menus are light
                            g.DrawImageUnscaled(icon, (size.Width - size.Height) / 2, 0);
                }
                Image old;
                renderer.Pictures.TryGetValue(item, out old);
                renderer.Pictures[item] = picture;
                if (old != null) old.Dispose();
                item.Invalidate();
            }
        }

        // ---- The extension ----

        void Record(Report report, Browser browser)
        {
            if (report.reloaded == true) browser.Prefs.removed = false;
            if (browser.Prefs.removed) { Store.Save(); return; }
            browser.Prefs.lastSeen = DateTime.UtcNow.Ticks;
            if (!Data.browsers.Contains(browser.Id))
            {
                Data.browsers.Add(browser.Id);
                if (Data.selected == null) Data.selected = browser.Id;
            }
            browser.Tabs = report.tabs;
            browser.OpenSites = report.sites.Distinct().ToList();
            if (report.unloaded.Count > 0)
            {
                browser.BeforeUnload = browser.Used;
                var entries = report.unloaded.AsEnumerable().Reverse().Select(u =>
                {
                    var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(u.time)
                        .ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
                    return new LogEntry
                    {
                        text = when + "   " + browser.Name + "   " + u.title + "   (" + u.site + ")",
                        browser = browser.Id,
                        url = u.url != null && u.url.StartsWith("http", StringComparison.Ordinal) ? u.url : null,
                        tab = u.tab,
                    };
                });
                Data.log = entries.Concat(Data.log).Take(200).ToList();
                RenderLog();
            }
            Store.Save();
        }

        // Approximate: the drop in the browser's memory across the unload.
        void CountFreed(Browser browser)
        {
            if (browser.BeforeUnload > browser.Used && browser.Used > 0)
            {
                browser.Prefs.freedBytes = browser.FreedToday + (browser.BeforeUnload - browser.Used);
                browser.Prefs.freedDay = Browser.Today;
                Store.Save();
                Refresh();
            }
            browser.BeforeUnload = 0;
        }

        void Answer(TcpClient client, Browser browser)
        {
            Action reply = () => Server.Send(client, new
            {
                enabled = browser.Active && !browser.Prefs.removed,
                limit = (long)(browser.LimitGB * GB),
                used = browser.Used,
                interval = browser.Interval,
                grace = browser.Prefs.graceMinutes * 60,
                never = browser.Prefs.neverSites,
                protectTyped = browser.Prefs.protectTyped,
                protectBusy = browser.Prefs.protectBusy,
            });
            if (browser.Prefs.removed) { reply(); return; } // nothing to measure or unload
            Measure(browser, () => { CountFreed(browser); reply(); });
        }

        // Held open until a log entry is clicked, so the jump happens at once.
        void Hold(TcpClient client, Browser browser)
        {
            if (browser.Waiting != null) browser.Waiting.Close();
            browser.Waiting = client;
            browser.LastWait = DateTime.UtcNow;
            var timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += (s, e) =>
            {
                timer.Dispose();
                if (browser.Waiting != client) return;
                browser.Waiting = null;
                Server.Send(client, new Dictionary<string, string>());
            };
            timer.Start();
        }

        // ---- Log ----

        void RenderLog()
        {
            if (logList == null || logList.IsDisposed) return;
            shownLog = Data.log.ToList();
            logList.BeginUpdate();
            logList.Items.Clear();
            int widest = 0;
            foreach (var entry in shownLog)
            {
                logList.Items.Add(entry.text ?? "");
                widest = Math.Max(widest, TextRenderer.MeasureText(entry.text ?? "", logList.Font).Width);
            }
            if (shownLog.Count == 0) logList.Items.Add("No tabs unloaded yet.");
            logList.HorizontalExtent = widest + 8;
            logList.EndUpdate();
        }

        void ShowLog()
        {
            if (logWindow == null || logWindow.IsDisposed)
            {
                var scale = SystemInformation.SmallIconSize.Width / 16f;
                logWindow = new Form
                {
                    Text = "Unload Log (newest first, click to reopen)",
                    ClientSize = new Size((int)(640 * scale), (int)(400 * scale)),
                    StartPosition = FormStartPosition.CenterScreen,
                    Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                    Font = SystemFonts.MessageBoxFont,
                };
                logList = new ListBox
                {
                    Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HorizontalScrollbar = true,
                    IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = (int)(18 * scale),
                };
                logList.MouseMove += (s, e) => logList.Cursor = Entry(e.Location) != null ? Cursors.Hand : Cursors.Default;
                logList.MouseClick += (s, e) =>
                {
                    var entry = e.Button == MouseButtons.Left ? Entry(e.Location) : null;
                    if (entry != null) Reopen(entry);
                };
                // Entries that can be clicked are coloured like links.
                logList.DrawItem += (s, e) =>
                {
                    if (e.Index < 0) return;
                    e.DrawBackground();
                    bool clickable = e.Index < shownLog.Count && shownLog[e.Index].url != null;
                    var color = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText
                        : clickable ? SystemColors.HotTrack : SystemColors.WindowText;
                    // Drawn from the left edge so horizontal scrolling moves it.
                    TextRenderer.DrawText(e.Graphics, (string)logList.Items[e.Index], e.Font, e.Bounds.Location,
                                          color, TextFormatFlags.NoPrefix);
                };
                logWindow.Controls.Add(logList);
            }
            RenderLog();
            logWindow.Show();
            logWindow.WindowState = FormWindowState.Normal;
            logWindow.Activate();
        }

        // The clickable entry under the pointer: one with a web address.
        LogEntry Entry(Point point)
        {
            int index = logList.IndexFromPoint(point);
            if (index < 0 || index >= shownLog.Count || !logList.GetItemRectangle(index).Contains(point)) return null;
            return shownLog[index].url != null ? shownLog[index] : null;
        }

        void Reopen(LogEntry entry)
        {
            Uri url;
            if (!Uri.TryCreate(entry.url, UriKind.Absolute, out url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)) return;
            var browser = Browser.All.FirstOrDefault(b => b.Id == entry.browser);
            if (browser == null || browser.Location == null) return;
            if (browser.Waiting != null && (DateTime.UtcNow - browser.LastWait).TotalSeconds < 25)
            {
                var client = browser.Waiting;
                browser.Waiting = null;
                Native.AllowSetForegroundWindow(-1); // lets the browser raise its own window
                if (Server.Send(client, new { url = entry.url, tab = entry.tab }))
                {
                    // Give the extension a moment to switch tabs, then raise that window.
                    var timer = new System.Windows.Forms.Timer { Interval = 300 };
                    timer.Tick += (s, e) => { timer.Dispose(); Native.BringToFront(browser); };
                    timer.Start();
                    return;
                }
            }
            // The extension isn't listening: just open the page. AbsoluteUri has no spaces or quotes.
            Native.AllowSetForegroundWindow(-1);
            try { Process.Start(browser.Location, "\"" + url.AbsoluteUri + "\""); }
            catch (Exception error) { MessageBox.Show(error.Message, "RAM Limiter"); }
        }
    }
}
