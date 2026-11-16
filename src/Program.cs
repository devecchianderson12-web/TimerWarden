using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace CandadoApp
{
    internal static class Program
    {
        public const string Name = "AppWarden";
        public const string BackgroundArgument = "--background";
        public const string UpdateRestartArgument = "--update-restart";

        private static Icon appIcon;

        public static Icon AppIcon
        {
            get { return appIcon ?? (appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)); }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, Name + ".Instancia", out created))
            using (EventWaitHandle openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Abrir"))
            {
                if (!created && args.Contains(UpdateRestartArgument))
                {
                    // Reinicio por actualización: esperar a que la instancia vieja cierre y tomar su lugar.
                    try { created = mutex.WaitOne(TimeSpan.FromSeconds(12)); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    openSignal.Set();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool openConfig = !args.Contains(BackgroundArgument) && !args.Contains(UpdateRestartArgument) && !JustLoggedOn();
                Application.Run(new TrayApp(openSignal, openConfig));
            }
        }

        // Al iniciar Windows arranca solo en la bandeja aunque el acceso de inicio no traiga --background
        // (acceso en la carpeta Inicio, entrada vieja del registro, Programador de tareas...).
        private static bool JustLoggedOn()
        {
            try
            {
                int session = Process.GetCurrentProcess().SessionId;
                DateTime? first = null;
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    using (p)
                    {
                        try
                        {
                            if (p.SessionId == session && (first == null || p.StartTime < first.Value))
                                first = p.StartTime;
                        }
                        catch { }
                    }
                }
                return first.HasValue && DateTime.Now - first.Value < TimeSpan.FromMinutes(2);
            }
            catch { return false; }
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int Id = 1;
        private bool registered;

        public event EventHandler Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams { Parent = Native.HWND_MESSAGE });
        }

        public bool Register(uint modifiers, uint key)
        {
            Unregister();
            registered = Native.RegisterHotKey(Handle, Id, modifiers | Native.MOD_NOREPEAT, key);
            return registered;
        }

        public void Unregister()
        {
            if (registered)
                Native.UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null)
                Pressed(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Settings settings = Settings.Load();
        private readonly Countdown countdown = new Countdown();
        private readonly ClockOverlay overlay;
        private readonly Watcher watcher;
        private readonly ConfigForm configForm;
        private readonly HotkeyWindow hotkey = new HotkeyWindow();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem clockToggleItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem countdownItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem protectionItem = new ToolStripMenuItem();
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 400 };
        private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        private readonly Queue<BlockedHit> queue = new Queue<BlockedHit>();
        private readonly RegisteredWaitHandle openWait;
        private LockSession appLock;        // bloqueo de aplicaciones
        private CountdownLock countdownLock; // bloqueo al terminar la cuenta
        private bool updateShown;

        public TrayApp(EventWaitHandle openSignal, bool openConfig)
        {
            overlay = new ClockOverlay(settings, countdown);
            watcher = new Watcher(settings);
            configForm = new ConfigForm(settings, countdown);
            Updater.CleanupOld();
            Startup.Heal();

            countdown.Finished += delegate { OnCountdownFinished(); };
            countdown.Changed += delegate { UpdateCountdownItem(); };
            countdownItem.Click += delegate
            {
                if (countdown.Active)
                    countdown.Stop();
                else if (settings.CountdownSeconds > 0)
                    countdown.Start(settings.CountdownSeconds);
            };

            ToolStripMenuItem openItem = new ToolStripMenuItem("Abrir " + Program.Name, null, delegate { OpenConfig(); });
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            clockToggleItem.Click += delegate { SetClockVisible(!settings.ClockVisible); };
            protectionItem.Click += delegate { ToggleProtection(); };
            menu.Items.Add(openItem);
            menu.Items.Add(clockToggleItem);
            menu.Items.Add(countdownItem);
            menu.Items.Add(protectionItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, delegate { Quit(); });
            Theme.StyleMenu(menu);

            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                e.Cancel = LockActive;
                overlay.KeepBelowMenu = !e.Cancel;
            };
            menu.Closed += delegate { overlay.KeepBelowMenu = false; };

            tray.Icon = Program.AppIcon;
            tray.Text = Program.Name;
            tray.ContextMenuStrip = menu;
            tray.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                    OpenConfig();
            };
            tray.Visible = true;

            overlay.OpenRequested += delegate { OpenConfig(); };
            overlay.MenuRequested += delegate
            {
                menu.Show(Cursor.Position);
                Native.SetForegroundWindow(menu.Handle); // sin foco, el menú no se cerraría al hacer clic fuera
            };
            configForm.SettingsChanged += delegate { ApplySettings(); };
            configForm.QuitRequested += delegate { Quit(); };
            hotkey.Pressed += delegate
            {
                if (!LockActive)
                    SetClockVisible(!settings.ClockVisible);
            };

            IntPtr handle = overlay.Handle; // necesario para BeginInvoke antes de mostrar la ventana
            openWait = ThreadPool.RegisterWaitForSingleObject(openSignal, delegate
            {
                overlay.BeginInvoke(new Action(OpenConfig));
            }, null, Timeout.Infinite, false);

            poll.Tick += delegate { Scan(); };
            poll.Start();

            updateTimer.Tick += delegate { CheckUpdate(); };
            updateTimer.Start();
            CheckUpdate();

            ApplySettings();
            UpdateCountdownItem();
            WarnIfIncomplete();
            if (openConfig)
                OpenConfig();
        }

        private bool LockActive
        {
            get { return appLock != null || countdownLock != null; }
        }

        private void ApplySettings()
        {
            settings.Save();
            clockToggleItem.Text = settings.ClockVisible ? "Ocultar reloj" : "Mostrar reloj";
            UpdateHotkey();
            UpdateProtectionItem();

            overlay.Visible = settings.ClockVisible;
            overlay.ApplySettings();
        }

        private void UpdateHotkey()
        {
            if (!settings.HotkeyEnabled)
            {
                hotkey.Unregister();
                configForm.SetHotkeyStatus("");
                return;
            }
            bool ok = hotkey.Register(settings.HotkeyModifiers, settings.HotkeyKey);
            configForm.SetHotkeyStatus(ok ? "" : "Otra aplicación ya usa esa combinación.");
        }

        private void SetClockVisible(bool visible)
        {
            settings.ClockVisible = visible;
            configForm.SetClockVisible(visible);
            ApplySettings();
        }

        private void UpdateCountdownItem()
        {
            countdownItem.Text = countdown.Active ? "Detener cuenta regresiva" : "Iniciar cuenta regresiva";
        }

        private void UpdateProtectionItem()
        {
            protectionItem.Text = settings.ProtectionEnabled ? "Desactivar protección" : "Activar protección";
        }

        private void ToggleProtection()
        {
            if (!settings.ProtectionEnabled && !settings.HasCredential)
            {
                OpenConfig();
                return;
            }
            settings.ProtectionEnabled = !settings.ProtectionEnabled;
            settings.Save();
            UpdateProtectionItem();
        }

        private void WarnIfIncomplete()
        {
            if (!settings.HasCredential || settings.Blocked.Count == 0)
                tray.ShowBalloonTip(5000, Program.Name,
                    "Abre la ventana para poner una contraseña y elegir qué proteger.", ToolTipIcon.Info);
        }

        // ---------- Bloqueo de aplicaciones ----------

        private void Scan()
        {
            foreach (BlockedHit hit in watcher.Poll())
                queue.Enqueue(hit);
            PumpQueue();
        }

        private void PumpQueue()
        {
            if (LockActive || queue.Count == 0)
                return;

            BlockedHit hit = queue.Dequeue();
            string family = hit.FileName;

            appLock = new LockSession(settings, hit.Name);
            appLock.Unlocked += delegate
            {
                watcher.AuthorizeFamily(family);
                DrainFamily(family);
                EndAppLock();
            };
            appLock.Cancelled += delegate
            {
                watcher.KillFamily(family);
                DrainFamily(family);
                EndAppLock();
            };
            appLock.Show();
        }

        private void DrainFamily(string family)
        {
            BlockedHit[] rest = queue.Where(h => h.FileName != family).ToArray();
            queue.Clear();
            foreach (BlockedHit h in rest)
                queue.Enqueue(h);
        }

        private void EndAppLock()
        {
            if (appLock != null)
            {
                appLock.Dispose();
                appLock = null;
            }
            PumpQueue();
        }

        // ---------- Bloqueo por cuenta regresiva ----------

        private void OnCountdownFinished()
        {
            if (settings.LockEnabled && settings.HasCredential)
            {
                ShowCountdownLock();
                return;
            }
            SystemSounds.Exclamation.Play();
            tray.ShowBalloonTip(5000, Program.Name, "La cuenta regresiva terminó.", ToolTipIcon.Info);
        }

        private void ShowCountdownLock()
        {
            if (LockActive)
                return;
            configForm.Hide();
            overlay.Hide();
            countdownLock = new CountdownLock(settings);
            countdownLock.Unlocked += delegate
            {
                countdownLock.Dispose();
                countdownLock = null;
                overlay.Visible = settings.ClockVisible;
            };
            IntPtr previous = Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            countdownLock.Show();
            Native.SetThreadDpiAwarenessContext(previous);
        }

        // ---------- Actualización ----------

        private void CheckUpdate()
        {
            if (updateShown)
                return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = Updater.CheckForUpdate();
                if (info == null)
                    return;
                try { configForm.BeginInvoke(new Action(delegate { ShowUpdate(info); })); }
                catch { }
            });
        }

        private void ShowUpdate(UpdateInfo info)
        {
            if (updateShown)
                return;
            updateShown = true;
            configForm.ShowUpdateAvailable(info);
            tray.ShowBalloonTip(5000, Program.Name, "Hay una actualización disponible. Abrí AppWarden para instalarla.", ToolTipIcon.Info);
        }

        // ---------- Ventana ----------

        private void OpenConfig()
        {
            if (LockActive)
                return;

            IntPtr previous = Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
            configForm.Show();
            Native.SetThreadDpiAwarenessContext(previous);
            if (configForm.WindowState == FormWindowState.Minimized)
                configForm.WindowState = FormWindowState.Normal;
            configForm.Activate();
        }

        private void Quit()
        {
            poll.Stop();
            updateTimer.Stop();
            openWait.Unregister(null);
            hotkey.Dispose();
            countdown.Dispose();
            if (appLock != null)
                appLock.Dispose();
            if (countdownLock != null)
                countdownLock.Dispose();
            tray.Visible = false;
            tray.Dispose();
            configForm.AllowClose = true;
            configForm.Close();
            overlay.Close();
            ExitThread();
        }
    }
}
