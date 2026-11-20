// Three-Finger Drag for Windows precision touchpads.
// Build (no SDK needed, uses the compiler that ships with Windows):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:ThreeFingerDrag.exe ThreeFingerDrag.cs
//
// How speed works: finger travel is measured as a fraction of the touchpad's
// own coordinate range (never the millimetre size the touchpad claims, which
// some touchpads report wrongly), and turned into a fraction of the screen
// width. The curve below was fitted to the normal one-finger pointer.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native {
  [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort page; public ushort usage; public uint flags; public IntPtr hwnd; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }

  [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
  [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint headerSize);
  [DllImport("user32.dll")] public static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, IntPtr data, ref uint size);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
  [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, IntPtr caps);
  [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort count, IntPtr pp);
  [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, IntPtr report, uint len);
  [DllImport("hid.dll")] public static extern int HidP_GetUsages(int reportType, ushort page, ushort link, [Out] ushort[] list, ref uint count, IntPtr pp, IntPtr report, uint len);
  public const int HIDP_OK = 0x00110000;
}

class Settings {
  public bool Enabled = true;
  public int ReleaseDelayMs = 100;
  public double Speed = 1.0;
  public double Acceleration = 1.0;
  public int StartDelayMs = 40;
  public double StartDistance = 1.0;
  public bool Debug = false;

  public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackpadGestures"); } }
  public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

  const string Template =
    "; Three-finger drag settings.\r\n" +
    "; After editing, save and choose \"Reload settings\" from the tray icon.\r\n" +
    "[drag]\r\n" +
    "enabled=1\r\n" +
    "; Milliseconds the button stays held after the fingers lift, so a drag can be\r\n" +
    "; continued by putting three fingers back down. 0 = release at once.\r\n" +
    "release_delay_ms=100\r\n" +
    "; Pointer speed. 1.0 = same as the normal one-finger pointer, 2.0 = twice as\r\n" +
    "; fast, 0.5 = half. Allowed range 0.1 to 10.\r\n" +
    "speed=1.0\r\n" +
    "; Acceleration. 0 = none (distance depends only on how far the fingers move),\r\n" +
    "; 1.0 = default (faster finger movement travels further).\r\n" +
    "acceleration=1.0\r\n" +
    "; Three fingers must rest this long (milliseconds) and move this far\r\n" +
    "; (roughly millimetres) before a drag starts, so a three-finger tap never clicks.\r\n" +
    "start_delay_ms=40\r\n" +
    "start_distance_mm=1.0\r\n";

  static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

  public static Settings Load() {
    Settings s = new Settings();
    try {
      if (!File.Exists(FilePath)) { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, Template); }
      foreach (string raw in File.ReadAllLines(FilePath)) {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
        int eq = line.IndexOf('=');
        if (eq < 1) continue;
        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
        double v;
        if (!double.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
        if (key == "enabled") s.Enabled = v != 0;
        else if (key == "release_delay_ms") s.ReleaseDelayMs = (int)Clamp(v, 0, 5000);
        else if (key == "speed") s.Speed = Clamp(v, 0.1, 10);
        else if (key == "acceleration") s.Acceleration = Clamp(v, 0, 2);
        else if (key == "start_delay_ms") s.StartDelayMs = (int)Clamp(v, 0, 1000);
        else if (key == "start_distance_mm") s.StartDistance = Clamp(v, 0, 20);
        else if (key == "debug") s.Debug = v != 0;
      }
    } catch { }
    return s;
  }

  public static void SaveEnabled(bool on) {
    try {
      string[] lines = File.ReadAllLines(FilePath);
      bool found = false;
      for (int i = 0; i < lines.Length; i++) {
        if (lines[i].Trim().ToLowerInvariant().StartsWith("enabled")) { lines[i] = "enabled=" + (on ? "1" : "0"); found = true; }
      }
      if (found) File.WriteAllLines(FilePath, lines);
    } catch { }
  }
}

class Contact { public uint Id; public double X, Y; }

class Pad {
  public IntPtr Preparsed;
  public ushort[] Links = new ushort[0];
  public double RangeX = 1;
}

class Engine : NativeWindow {
  enum State { Idle, Pending, Dragging, Releasing, Blocked }

  // Reference width used only to turn start_distance_mm into touchpad counts.
  const double AssumedPadWidthMm = 120.0;
  static readonly IntPtr Tag = new IntPtr(0x33464447);

  public Settings Cfg = new Settings();
  Dictionary<IntPtr, Pad> pads = new Dictionary<IntPtr, Pad>();
  IntPtr buf = Marshal.AllocHGlobal(8192);
  Stopwatch clock = Stopwatch.StartNew();
  System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

  State state = State.Idle;
  Dictionary<uint, Contact> prev = new Dictionary<uint, Contact>();
  List<Contact> frame = new List<Contact>();
  int frameExpected, frameSeen;
  double lastFrameMs, lastReportMs, pendingSinceMs, pendingDist, releaseAtMs;
  double speedEma, posX, posY;
  Queue<double[]> window = new Queue<double[]>();
  double windowDist, windowMs, scanDtMs;
  uint lastScan; bool haveScan;
  double dbgCounts, dbgPx, dbgK, dbgV, dbgDt; int dbgFrames;
  bool buttonDown;

  public Engine() {
    CreateParams cp = new CreateParams();
    cp.Parent = new IntPtr(-3); // message-only window
    CreateHandle(cp);
    timer.Interval = 15;
    timer.Tick += delegate { OnTick(); };
    timer.Start();
  }

  public bool Listen() {
    Native.RAWINPUTDEVICE[] r = new Native.RAWINPUTDEVICE[1];
    r[0].page = 0x0D; r[0].usage = 0x05; r[0].flags = 0x100; r[0].hwnd = Handle; // touchpad, receive in background
    return Native.RegisterRawInputDevices(r, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
  }

  public void Reset() {
    ReleaseButton();
    state = State.Idle;
    prev.Clear();
  }

  Pad GetPad(IntPtr dev) {
    Pad p;
    if (pads.TryGetValue(dev, out p)) return p;
    p = new Pad();
    uint size = 0;
    Native.GetRawInputDeviceInfoW(dev, 0x20000005, IntPtr.Zero, ref size);
    if (size > 0) {
      p.Preparsed = Marshal.AllocHGlobal((int)size);
      Native.GetRawInputDeviceInfoW(dev, 0x20000005, p.Preparsed, ref size);
      IntPtr caps = Marshal.AllocHGlobal(64);
      if (Native.HidP_GetCaps(p.Preparsed, caps) == Native.HIDP_OK) {
        ushort n = (ushort)Marshal.ReadInt16(caps, 48);
        if (n > 0) {
          IntPtr vc = Marshal.AllocHGlobal(72 * n);
          Native.HidP_GetValueCaps(0, vc, ref n, p.Preparsed);
          List<ushort> links = new List<ushort>();
          for (int i = 0; i < n; i++) {
            int o = i * 72;
            ushort page = (ushort)Marshal.ReadInt16(vc, o);
            ushort usage = (ushort)Marshal.ReadInt16(vc, o + 56);
            if (page != 0x01 || usage != 0x30) continue;
            ushort link = (ushort)Marshal.ReadInt16(vc, o + 6);
            if (!links.Contains(link)) links.Add(link);
            double range = (double)Marshal.ReadInt32(vc, o + 44) - Marshal.ReadInt32(vc, o + 40);
            if (range > 1) p.RangeX = range;
          }
          links.Sort();
          p.Links = links.ToArray();
          Marshal.FreeHGlobal(vc);
        }
      }
      Marshal.FreeHGlobal(caps);
    }
    pads[dev] = p;
    return p;
  }

  protected override void WndProc(ref Message m) {
    if (m.Msg == 0x00FF) { // WM_INPUT
      uint size = 8192;
      if (Native.GetRawInputData(m.LParam, 0x10000003, buf, ref size, 24) != 0xFFFFFFFF && Marshal.ReadInt32(buf, 0) == 2) {
        IntPtr dev = Marshal.ReadIntPtr(buf, 8);
        int hidSize = Marshal.ReadInt32(buf, 24), count = Marshal.ReadInt32(buf, 28);
        Pad pad = GetPad(dev);
        if (pad.Links.Length > 0) {
          for (int i = 0; i < count; i++) OnReport(pad, new IntPtr(buf.ToInt64() + 32 + (long)i * hidSize), (uint)hidSize);
        }
      }
    }
    base.WndProc(ref m);
  }

  // A touchpad frame can span several reports; the first one carries the contact count.
  void OnReport(Pad pad, IntPtr rep, uint len) {
    uint cc;
    if (Native.HidP_GetUsageValue(0, 0x0D, 0, 0x54, out cc, pad.Preparsed, rep, len) != Native.HIDP_OK) cc = 0;
    if (cc > 0 || frameSeen >= frameExpected) {
      frame.Clear(); frameExpected = (int)cc; frameSeen = 0;
      // The touchpad's own scan clock (0.1 ms units) is steadier than message arrival times.
      uint scan; scanDtMs = 0;
      if (Native.HidP_GetUsageValue(0, 0x0D, 0, 0x56, out scan, pad.Preparsed, rep, len) == Native.HIDP_OK) {
        if (haveScan) scanDtMs = ((scan - lastScan) & 0xFFFF) * 0.1;
        lastScan = scan; haveScan = true;
      }
    }
    int take = Math.Min(pad.Links.Length, frameExpected - frameSeen);
    ushort[] usages = new ushort[16];
    for (int i = 0; i < take; i++) {
      ushort link = pad.Links[i];
      uint un = 16;
      bool tip = false;
      if (Native.HidP_GetUsages(0, 0x0D, link, usages, ref un, pad.Preparsed, rep, len) == Native.HIDP_OK) {
        for (int k = 0; k < un; k++) if (usages[k] == 0x42) tip = true;
      }
      if (!tip) continue;
      uint id, x, y;
      if (Native.HidP_GetUsageValue(0, 0x0D, link, 0x51, out id, pad.Preparsed, rep, len) != Native.HIDP_OK) id = link;
      if (Native.HidP_GetUsageValue(0, 0x01, link, 0x30, out x, pad.Preparsed, rep, len) != Native.HIDP_OK) continue;
      if (Native.HidP_GetUsageValue(0, 0x01, link, 0x31, out y, pad.Preparsed, rep, len) != Native.HIDP_OK) continue;
      Contact c = new Contact(); c.Id = id; c.X = x; c.Y = y;
      frame.Add(c);
    }
    frameSeen += Math.Max(take, 0);
    lastReportMs = clock.Elapsed.TotalMilliseconds;
    if (frameSeen >= frameExpected) OnFrame(pad, frame);
  }

  void OnFrame(Pad pad, List<Contact> contacts) {
    double now = clock.Elapsed.TotalMilliseconds;
    double dt = now - lastFrameMs;
    lastFrameMs = now;
    if (scanDtMs >= 1 && scanDtMs <= 100) dt = scanDtMs;
    if (dt < 2) dt = 2; else if (dt > 50) dt = 50;
    int n = contacts.Count;

    // Average movement of the fingers present in both this frame and the last,
    // so a finger landing or lifting never makes the pointer jump.
    double dx = 0, dy = 0; int common = 0;
    foreach (Contact c in contacts) {
      Contact p;
      if (prev.TryGetValue(c.Id, out p)) { dx += c.X - p.X; dy += c.Y - p.Y; common++; }
    }
    if (common > 0) { dx /= common; dy /= common; }
    prev.Clear();
    foreach (Contact c in contacts) prev[c.Id] = c;

    if (!Cfg.Enabled) { if (state != State.Idle) Reset(); return; }

    double dist = Math.Sqrt(dx * dx + dy * dy);
    switch (state) {
      case State.Idle:
        if (n == 3) { state = State.Pending; pendingSinceMs = now; pendingDist = 0; ClearSpeed(); }
        else if (n > 3) state = State.Blocked;
        break;
      case State.Pending:
        if (n != 3) { state = n > 3 ? State.Blocked : State.Idle; break; }
        pendingDist += dist * AssumedPadWidthMm / pad.RangeX;
        UpdateSpeed(dist, dt, pad);
        if (now - pendingSinceMs >= Cfg.StartDelayMs && pendingDist >= Cfg.StartDistance) BeginDrag();
        break;
      case State.Dragging:
        if (n == 3) { UpdateSpeed(dist, dt, pad); Move(dx, dy, pad); }
        else if (n > 3) { ReleaseButton(); state = State.Blocked; }
        else if (n == 0) StartRelease(now);
        break;
      case State.Releasing:
        if (n == 3) { state = State.Dragging; ClearSpeed(); }
        else if (n > 3) { ReleaseButton(); state = State.Blocked; }
        break;
      case State.Blocked:
        if (n == 0) state = State.Idle;
        break;
    }
  }

  void UpdateSpeed(double distCounts, double dtMs, Pad pad) {
    // Average speed over roughly the last 60 ms, in touchpad widths per second.
    window.Enqueue(new double[] { distCounts, dtMs });
    windowDist += distCounts; windowMs += dtMs;
    while (window.Count > 1 && windowMs - window.Peek()[1] >= 60) { double[] old = window.Dequeue(); windowDist -= old[0]; windowMs -= old[1]; }
    speedEma = windowMs > 0 ? windowDist / pad.RangeX / (windowMs / 1000.0) : 0;
    dbgV += speedEma; dbgDt += dtMs; dbgFrames++;
  }

  void ClearSpeed() { window.Clear(); windowDist = 0; windowMs = 0; speedEma = 0; }

  void DebugLog() {
    if (!Cfg.Debug || dbgFrames == 0 || dbgCounts <= 0) { dbgCounts = dbgPx = dbgK = dbgV = dbgDt = 0; dbgFrames = 0; return; }
    try {
      File.AppendAllText(Path.Combine(Settings.Dir, "debug.log"), string.Format(CultureInfo.InvariantCulture,
        "{0:HH:mm:ss} frames={1} avgDt={2:F2}ms counts={3:F0} px={4:F0} gain={5:F4}px/cnt avgK={6:F3} avgV={7:F4} screenW={8} virt={9}x{10} speed={11} accel={12}\r\n",
        DateTime.Now, dbgFrames, dbgDt / dbgFrames, dbgCounts, dbgPx, dbgPx / dbgCounts, dbgK / dbgCounts, dbgV / dbgFrames,
        Native.GetSystemMetrics(0), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79), Cfg.Speed, Cfg.Acceleration));
    } catch { }
    dbgCounts = dbgPx = dbgK = dbgV = dbgDt = 0; dbgFrames = 0;
  }

  // Screen widths travelled per touchpad width of finger travel, as a function
  // of finger speed. Fitted to the stock one-finger pointer: about 1.4 when
  // moving slowly, 2.6 at an ordinary pace, 3.4 for a fast swipe.
  double Gain() {
    const double flat = 2.6;
    double curve = 0.7 + 3.9 * speedEma / (speedEma + 0.2);
    double k = flat + Cfg.Acceleration * (curve - flat);
    if (k < 0.3) k = 0.3;
    return k * Cfg.Speed;
  }

  void BeginDrag() {
    Native.POINT p;
    Native.GetCursorPos(out p);
    posX = p.x; posY = p.y;
    Send(0x0002, false); // left button down
    buttonDown = true;
    state = State.Dragging;
  }

  void Move(double dxCounts, double dyCounts, Pad pad) {
    if (dxCounts == 0 && dyCounts == 0) return;
    double pxPerCount = Gain() * Native.GetSystemMetrics(0) / pad.RangeX;
    int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
    int vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
    posX += dxCounts * pxPerCount; posY += dyCounts * pxPerCount;
    double moved = Math.Sqrt(dxCounts * dxCounts + dyCounts * dyCounts);
    dbgCounts += moved; dbgPx += moved * pxPerCount; dbgK += moved * Gain();
    if (posX < vx) posX = vx; else if (posX > vx + vw - 1) posX = vx + vw - 1;
    if (posY < vy) posY = vy; else if (posY > vy + vh - 1) posY = vy + vh - 1;
    Send(0x0001, true);
  }

  void StartRelease(double now) {
    if (Cfg.ReleaseDelayMs <= 0) { ReleaseButton(); state = State.Idle; }
    else { state = State.Releasing; releaseAtMs = now + Cfg.ReleaseDelayMs; }
  }

  void ReleaseButton() {
    if (!buttonDown) return;
    Send(0x0004, false); // left button up
    DebugLog();
    buttonDown = false;
  }

  void OnTick() {
    double now = clock.Elapsed.TotalMilliseconds;
    if (state == State.Releasing && now >= releaseAtMs) { ReleaseButton(); state = State.Idle; }
    // Safety net: if the touchpad goes silent mid-drag, never leave the button held.
    else if ((state == State.Dragging || state == State.Pending) && now - lastReportMs > 400) {
      prev.Clear();
      if (state == State.Dragging) StartRelease(now); else state = State.Idle;
    }
  }

  void Send(uint flags, bool absoluteMove) {
    Native.INPUT[] inp = new Native.INPUT[1];
    inp[0].type = 0;
    inp[0].mi.flags = flags;
    inp[0].mi.extra = Tag;
    if (absoluteMove) {
      int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
      int vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
      inp[0].mi.dx = (int)Math.Round((posX - vx) * 65535.0 / Math.Max(1, vw - 1));
      inp[0].mi.dy = (int)Math.Round((posY - vy) * 65535.0 / Math.Max(1, vh - 1));
      inp[0].mi.flags = 0x0001 | 0x8000 | 0x4000; // move, absolute, whole virtual desktop
    }
    Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
  }
}

class TrayApp : ApplicationContext {
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  const string RunName = "ThreeFingerDrag";

  Engine engine = new Engine();
  NotifyIcon tray = new NotifyIcon();
  MenuItem enabledItem, startupItem;
  Icon iconOn, iconOff;

  public TrayApp() {
    iconOn = MakeIcon(Color.FromArgb(40, 130, 240));
    iconOff = MakeIcon(Color.FromArgb(140, 140, 140));
    engine.Cfg = Settings.Load();

    enabledItem = new MenuItem("Enabled", delegate { Toggle(); });
    startupItem = new MenuItem("Start with Windows", delegate { ToggleStartup(); });
    tray.ContextMenu = new ContextMenu(new MenuItem[] {
      enabledItem, startupItem, new MenuItem("-"),
      new MenuItem("Edit settings", delegate { try { Process.Start("notepad.exe", "\"" + Settings.FilePath + "\""); } catch { } }),
      new MenuItem("Reload settings", delegate { Reload(); }),
      new MenuItem("-"),
      new MenuItem("Exit", delegate { ExitThread(); })
    });
    tray.ContextMenu.Popup += delegate { startupItem.Checked = StartupEnabled(); };
    tray.DoubleClick += delegate { Toggle(); };
    Refresh();
    tray.Visible = true;

    if (!engine.Listen()) {
      MessageBox.Show("Could not start listening to the touchpad.", "Three-finger drag", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
  }

  void Refresh() {
    bool on = engine.Cfg.Enabled;
    enabledItem.Checked = on;
    tray.Icon = on ? iconOn : iconOff;
    tray.Text = on ? "Three-finger drag: on" : "Three-finger drag: off";
  }

  void Toggle() {
    engine.Cfg.Enabled = !engine.Cfg.Enabled;
    Settings.SaveEnabled(engine.Cfg.Enabled);
    engine.Reset();
    Refresh();
  }

  void Reload() {
    engine.Reset();
    engine.Cfg = Settings.Load();
    Refresh();
  }

  static string ExeCommand { get { return "\"" + Application.ExecutablePath + "\""; } }

  bool StartupEnabled() {
    try {
      using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) {
        string v = k == null ? null : k.GetValue(RunName) as string;
        return v != null && string.Equals(v.Trim(), ExeCommand, StringComparison.OrdinalIgnoreCase);
      }
    } catch { return false; }
  }

  void ToggleStartup() {
    try {
      using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey)) {
        if (StartupEnabled()) k.DeleteValue(RunName, false); else k.SetValue(RunName, ExeCommand);
      }
    } catch { }
  }

  static Icon MakeIcon(Color c) {
    using (Bitmap bmp = new Bitmap(32, 32)) {
      using (Graphics g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (SolidBrush b = new SolidBrush(c))
        using (Pen outline = new Pen(Color.White, 1.5f)) {
          Rectangle[] dots = { new Rectangle(1, 12, 9, 9), new Rectangle(11, 6, 9, 9), new Rectangle(21, 12, 9, 9) };
          foreach (Rectangle r in dots) { g.FillEllipse(b, r); g.DrawEllipse(outline, r); }
        }
      }
      IntPtr h = bmp.GetHicon();
      Icon icon = (Icon)Icon.FromHandle(h).Clone();
      Native.DestroyIcon(h);
      return icon;
    }
  }

  protected override void ExitThreadCore() {
    engine.Reset();
    tray.Visible = false;
    tray.Dispose();
    base.ExitThreadCore();
  }
}

static class Program {
  [STAThread]
  static void Main() {
    bool first;
    using (Mutex mutex = new Mutex(true, "Local\\TrackpadGestures.ThreeFingerDrag", out first)) {
      if (!first) return; // already running (this name is shared with the earlier build on purpose)
      try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
      catch { try { Native.SetProcessDPIAware(); } catch { } }
      Application.EnableVisualStyles();
      Application.Run(new TrayApp());
    }
  }
}

