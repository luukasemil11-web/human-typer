// Human Typer for Windows.
//
// Paste text, press Start, click into your Google Doc during the countdown,
// and the text gets typed with realistic rhythm, pauses and corrected typos.
//
// Stop with Ctrl+Shift+9, Esc, the Stop button, or by moving the mouse into
// the top-left corner of the screen. While Ctrl/Alt/Win is held the typer
// waits, so its letters never turn into shortcuts like Ctrl+A.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HumanTyper
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    enum Kind { Type, Back, Wait }

    struct Act
    {
        public Kind Kind;
        public char Ch;
        public double Secs;
        public static Act Type(char c) => new Act { Kind = Kind.Type, Ch = c };
        public static Act Back() => new Act { Kind = Kind.Back };
        public static Act Wait(double s) => new Act { Kind = Kind.Wait, Secs = s };
    }

    class MainForm : Form
    {
        // QWERTY neighbours, used to make believable typos.
        static readonly Dictionary<char, string> Neighbours = new Dictionary<char, string>
        {
            ['q'] = "wa", ['w'] = "qeas", ['e'] = "wrds", ['r'] = "etdf", ['t'] = "ryfg", ['y'] = "tugh",
            ['u'] = "yihj", ['i'] = "uojk", ['o'] = "ipkl", ['p'] = "ol", ['a'] = "qwsz", ['s'] = "awedxz",
            ['d'] = "serfcx", ['f'] = "drtgvc", ['g'] = "ftyhbv", ['h'] = "gyujnb", ['j'] = "huikmn",
            ['k'] = "jiolm", ['l'] = "kop", ['z'] = "asx", ['x'] = "zsdc", ['c'] = "xdfv", ['v'] = "cfgb",
            ['b'] = "vghn", ['n'] = "bhjm", ['m'] = "njk",
        };
        const string SentenceEnd = ".!?";
        const string ClauseEnd = ",;:";

        const int HotkeyStop = 1, HotkeyEsc = 2;

        readonly Random rng = new Random();
        readonly TextBox text;
        readonly TrackBar wpm, typos, pauses, countdown;
        readonly Button startBtn, pauseBtn, stopBtn;
        readonly ProgressBar progress;
        readonly Label status;

        CancellationTokenSource cts;
        bool paused;
        string stopReason;

        public MainForm()
        {
            Text = "Human Typer";
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(460, 440);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "Paste your text:", AutoSize = true });
            text = new TextBox
            {
                Multiline = true, AcceptsReturn = true, AcceptsTab = true,
                ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            };
            layout.Controls.Add(text);

            var opts = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, AutoSize = true };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            wpm = Slider(opts, "Speed (WPM)", 15, 150, 55, v => v.ToString());
            typos = Slider(opts, "Typos (% of words)", 0, 150, 30, v => (v / 10.0).ToString("0.0"));
            pauses = Slider(opts, "Thinking pauses (% of sentences)", 0, 40, 8, v => v.ToString());
            countdown = Slider(opts, "Countdown (s)", 2, 15, 5, v => v.ToString());
            layout.Controls.Add(opts);

            var btns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 4) };
            startBtn = new Button { Text = "Start", AutoSize = true };
            pauseBtn = new Button { Text = "Pause", AutoSize = true, Enabled = false };
            stopBtn = new Button { Text = "Stop", AutoSize = true, Enabled = false };
            startBtn.Click += async (s, e) => await StartAsync();
            pauseBtn.Click += (s, e) => TogglePause();
            stopBtn.Click += (s, e) => Stop("Stopped.");
            btns.Controls.AddRange(new Control[] { startBtn, pauseBtn, stopBtn });
            layout.Controls.Add(btns);

            progress = new ProgressBar { Dock = DockStyle.Fill, Height = 18 };
            layout.Controls.Add(progress);
            status = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0) };
            layout.Controls.Add(status);
            SetStatus("Ready. To stop: Ctrl+Shift+9, Esc, or mouse to the top-left corner.");
        }

        TrackBar Slider(TableLayoutPanel parent, string label, int min, int max, int value, Func<int, string> fmt)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
            var bar = new TrackBar
            {
                Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill,
                TickStyle = TickStyle.None, AutoSize = false, Height = 30,
            };
            var val = new Label { Text = fmt(value), AutoSize = true, Anchor = AnchorStyles.Right };
            bar.ValueChanged += (s, e) => val.Text = fmt(bar.Value);
            parent.Controls.AddRange(new Control[] { lbl, bar, val });
            return bar;
        }

        void SetStatus(string s) => status.Text = s;

        // ---------- planning ----------

        List<Act> BuildActions(string s)
        {
            var acts = new List<Act>();
            double typoP = typos.Value / 1000.0;
            double pauseP = pauses.Value / 100.0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                char lower = char.ToLowerInvariant(ch);
                // Possibly fumble a letter, sometimes typing on a bit before noticing.
                if (Neighbours.TryGetValue(lower, out var near) && rng.NextDouble() < typoP / 4)
                {
                    char wrong = near[rng.Next(near.Length)];
                    if (char.IsUpper(ch)) wrong = char.ToUpperInvariant(wrong);
                    acts.Add(Act.Type(wrong));
                    int want = new[] { 0, 0, 1, 1, 2 }[rng.Next(5)];
                    int extra = 0;
                    while (extra < want && i + 1 + extra < s.Length && !char.IsWhiteSpace(s[i + 1 + extra]))
                    {
                        acts.Add(Act.Type(s[i + 1 + extra]));
                        extra++;
                    }
                    acts.Add(Act.Wait(Uniform(0.25, 0.7)));
                    for (int b = 0; b <= extra; b++) acts.Add(Act.Back());
                    acts.Add(Act.Wait(Uniform(0.08, 0.25)));
                }
                acts.Add(Act.Type(ch));
                if (SentenceEnd.IndexOf(ch) >= 0 && rng.NextDouble() < pauseP)
                    acts.Add(Act.Wait(Uniform(1.5, 4.5)));
            }
            return acts;
        }

        double CharDelay(char ch, char prev)
        {
            double b = 60.0 / (Math.Max(wpm.Value, 1) * 5);   // seconds per character
            double d = Gauss(b, b * 0.35);
            if (ch == ' ') d *= Uniform(1.0, 1.6);
            else if (ch == '\n') d *= Uniform(3, 6);
            else if (SentenceEnd.IndexOf(prev) >= 0) d *= Uniform(2.5, 4.5);
            else if (ClauseEnd.IndexOf(prev) >= 0) d *= Uniform(1.5, 2.5);
            else if (char.IsUpper(ch) || !char.IsLetterOrDigit(ch)) d *= Uniform(1.2, 1.8);  // shift / symbols
            if (rng.NextDouble() < 0.02) d += Uniform(0.3, 1.0);                             // micro-hesitation
            return Math.Max(d, 0.015);
        }

        double Uniform(double lo, double hi) => lo + rng.NextDouble() * (hi - lo);

        double Gauss(double mean, double sd)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        // ---------- running ----------

        async Task StartAsync()
        {
            string s = text.Text.Replace("\r\n", "\n");
            if (s.Trim().Length == 0)
            {
                SetStatus("Paste some text first.");
                return;
            }
            var acts = BuildActions(s);
            progress.Maximum = acts.Count;
            progress.Value = 0;
            var src = cts = new CancellationTokenSource();
            paused = false;
            stopReason = null;
            SetRunning(true);
            var ct = src.Token;

            try
            {
                await CountdownAsync(countdown.Value, "Click into your Google Doc now! Starting in {0}…", src);
                pauseBtn.Enabled = true;
                SetStatus("Typing… (Ctrl+Shift+9 or Esc to stop)");

                for (int i = 0; i < acts.Count; i++)
                {
                    await WaitWhilePausedAsync(src);
                    CheckFailsafe(src);
                    var a = acts[i];
                    double delay = 0;
                    if (a.Kind == Kind.Wait)
                    {
                        delay = a.Secs;
                    }
                    else
                    {
                        // Never type while Ctrl/Alt/Win is held, or letters become shortcuts.
                        while (Native.ShortcutModifierHeld()) await Task.Delay(30, ct);
                        if (a.Kind == Kind.Back)
                        {
                            Native.SendVirtualKey(Native.VK_BACK);
                            delay = Uniform(0.06, 0.14);
                        }
                        else
                        {
                            if (a.Ch == '\n') Native.SendVirtualKey(Native.VK_RETURN);
                            else if (a.Ch == '\t') Native.SendVirtualKey(Native.VK_TAB);
                            else Native.SendChar(a.Ch);
                            char next = i + 1 < acts.Count && acts[i + 1].Kind == Kind.Type ? acts[i + 1].Ch : ' ';
                            delay = CharDelay(next, a.Ch);
                        }
                    }
                    progress.Value = i + 1;
                    await SleepAsync(delay, src);
                }
                if (cts == src) Stop("Done!");
            }
            catch (OperationCanceledException)
            {
                // A newer run may already have started; only tidy up our own.
                if (cts == src || cts == null) Stop(stopReason ?? "Stopped.");
            }
        }

        async Task CountdownAsync(int seconds, string fmt, CancellationTokenSource src)
        {
            for (int n = seconds; n > 0; n--)
            {
                SetStatus(string.Format(fmt, n));
                await SleepAsync(1, src);
            }
        }

        async Task WaitWhilePausedAsync(CancellationTokenSource src)
        {
            if (!paused) return;
            while (paused) await Task.Delay(50, src.Token);
            pauseBtn.Enabled = false;
            await CountdownAsync(3, "Click back into the doc! Resuming in {0}…", src);
            pauseBtn.Enabled = true;
            SetStatus("Typing… (Ctrl+Shift+9 or Esc to stop)");
        }

        // Sleeps in small slices so the mouse-corner failsafe works during long pauses too.
        async Task SleepAsync(double seconds, CancellationTokenSource src)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                CheckFailsafe(src);
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return;
                await Task.Delay(left.TotalMilliseconds < 50 ? left : TimeSpan.FromMilliseconds(50), src.Token);
            }
        }

        void CheckFailsafe(CancellationTokenSource src)
        {
            var p = Cursor.Position;
            if (p.X <= 3 && p.Y <= 3 && !src.IsCancellationRequested)
            {
                stopReason = "Stopped by failsafe (mouse in corner).";
                src.Cancel();
            }
            src.Token.ThrowIfCancellationRequested();
        }

        void TogglePause()
        {
            if (cts == null) return;
            paused = !paused;
            pauseBtn.Text = paused ? "Resume" : "Pause";
            if (paused) SetStatus("Paused. Press Resume, then click back into the doc.");
        }

        void Stop(string msg)
        {
            if (cts != null && !cts.IsCancellationRequested)
            {
                stopReason = msg;
                cts.Cancel();
            }
            cts = null;
            paused = false;
            SetRunning(false);
            SetStatus(msg);
        }

        void SetRunning(bool running)
        {
            startBtn.Enabled = !running;
            stopBtn.Enabled = running;
            pauseBtn.Enabled = false;
            pauseBtn.Text = "Pause";
            text.ReadOnly = running;
            // The stop keys are only grabbed while typing, so Esc works normally otherwise.
            if (running)
            {
                Native.RegisterHotKey(Handle, HotkeyStop, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x39);
                Native.RegisterHotKey(Handle, HotkeyEsc, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
            }
            else
            {
                Native.UnregisterHotKey(Handle, HotkeyStop);
                Native.UnregisterHotKey(Handle, HotkeyEsc);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && cts != null)
                Stop("Stopped by hotkey.");
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (cts != null) Stop("Stopped.");
            base.OnFormClosing(e);
        }
    }

    static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
        public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
        const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        // The union must include MOUSEINPUT so INPUT has the size Windows expects.
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint n, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        static INPUT Key(ushort vk, ushort scan, uint flags) =>
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

        // Unicode input types any character regardless of keyboard layout.
        public static void SendChar(char c) => Send(Key(0, c, KEYEVENTF_UNICODE), Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));

        public static void SendVirtualKey(ushort vk) => Send(Key(vk, 0, 0), Key(vk, 0, KEYEVENTF_KEYUP));

        static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        public static bool ShortcutModifierHeld() => Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN);
    }
}
