using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CandadoApp
{
    // Bloqueo de pantalla al terminar la cuenta regresiva: una ventana por monitor; la principal lleva
    // la contraseña y los botones de energía. Es independiente del bloqueo de aplicaciones (LockSession).
    internal sealed class CountdownLock : IDisposable
    {
        private readonly Settings settings;
        private readonly List<CountdownLockForm> forms = new List<CountdownLockForm>();
        private readonly Timer guard = new Timer { Interval = 150 };
        private readonly Native.LowLevelKeyboardProc keyboardProc;
        private readonly Image image;
        private CountdownLockForm mainForm;
        private IntPtr keyboardHook;

        public event EventHandler Unlocked;

        public CountdownLock(Settings settings)
        {
            this.settings = settings;
            image = LoadImage(settings.LockImage);
            keyboardProc = OnKeyboard;
            guard.Tick += delegate { KeepOnTop(); };
        }

        public void Show()
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                CountdownLockForm form = new CountdownLockForm(screen, image, screen.Primary, settings);
                form.Unlocked += OnUnlocked;
                forms.Add(form);
                if (screen.Primary)
                    mainForm = form;
            }
            foreach (CountdownLockForm form in forms)
                form.Show();

            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, Native.GetModuleHandle(null), 0);
            KeepOnTop();
            guard.Start();
        }

        private static Image LoadImage(string path)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(path)))
                using (Image source = Image.FromStream(stream))
                    return new Bitmap(source);
            }
            catch (Exception e)
            {
                if (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                    return null;
                throw;
            }
        }

        private void KeepOnTop()
        {
            foreach (CountdownLockForm form in forms)
                Native.SetWindowPos(form.Handle, Native.HWND_TOPMOST, form.Target.X, form.Target.Y, form.Target.Width, form.Target.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

            IntPtr foreground = Native.GetForegroundWindow();
            if (mainForm == null || foreground == mainForm.Handle)
                return;

            uint process;
            uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out process);
            uint thisThread = Native.GetCurrentThreadId();
            Native.AttachThreadInput(foregroundThread, thisThread, true);
            Native.SetForegroundWindow(mainForm.Handle);
            Native.AttachThreadInput(foregroundThread, thisThread, false);
            mainForm.FocusPassword();
        }

        private static IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int key = Marshal.ReadInt32(data);
                int flags = Marshal.ReadInt32(data, 8);
                if (IsBlocked(key, (flags & Native.LLKHF_ALTDOWN) != 0, (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0))
                    return new IntPtr(1);
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private static bool IsBlocked(int key, bool alt, bool control)
        {
            Keys k = (Keys)key;
            if (k == Keys.LWin || k == Keys.RWin)
                return true;
            if (k == Keys.Escape)
                return alt || control;
            return alt && (k == Keys.Tab || k == Keys.F4 || k == Keys.Space);
        }

        private void OnUnlocked(object sender, EventArgs e)
        {
            if (Unlocked != null)
                Unlocked(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            guard.Stop();
            guard.Dispose();
            if (keyboardHook != IntPtr.Zero)
                Native.UnhookWindowsHookEx(keyboardHook);
            foreach (CountdownLockForm form in forms)
            {
                form.AllowClose = true;
                form.Close();
            }
            if (image != null)
                image.Dispose();
        }
    }

    internal sealed class CountdownLockForm : Form
    {
        private readonly Image image;
        private readonly Settings settings;
        private readonly float scale;
        private readonly RoundInput passwordInput = new RoundInput();
        private readonly Label errorLabel = new Label();
        private readonly Card unlockPanel = new Card { Fill = Theme.Window, Radius = 24 };
        private readonly RoundButton restartButton = new RoundButton();
        private readonly RoundButton shutdownButton = new RoundButton();
        private readonly Timer confirmReset = new Timer { Interval = 4000 };
        private Bitmap background;
        private RoundButton armedButton;

        public bool AllowClose;
        public Rectangle Target { get; private set; }

        public event EventHandler Unlocked;

        public CountdownLockForm(Screen screen, Image image, bool withControls, Settings settings)
        {
            Target = screen.Bounds;
            this.image = image;
            this.settings = settings;
            scale = Native.DpiScaleAt(new Point(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2));

            Text = Program.Name;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;
            BackColor = Color.Black;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;

            if (withControls)
            {
                BuildUnlockPanel();
                BuildPowerPanel();
            }
            confirmReset.Tick += delegate { Disarm(); };
        }

        private int Px(int value)
        {
            return (int)Math.Round(value * scale);
        }

        private Font Scaled(float pixels, FontStyle style = FontStyle.Regular)
        {
            return new Font("Segoe UI", pixels * scale, style, GraphicsUnit.Pixel);
        }

        private void BuildUnlockPanel()
        {
            unlockPanel.Size = new Size(Px(380), Px(214));

            Label title = new Label
            {
                Text = "Pantalla bloqueada",
                Font = Scaled(22, FontStyle.Bold),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(Px(28), Px(22), Px(324), Px(34))
            };

            passwordInput.Box.PasswordChar = '*';
            passwordInput.Font = Scaled(16);
            passwordInput.Bounds = new Rectangle(Px(28), Px(68), Px(324), Px(44));

            errorLabel.ForeColor = Theme.Danger;
            errorLabel.BackColor = Color.Transparent;
            errorLabel.Font = Scaled(13);
            errorLabel.TextAlign = ContentAlignment.MiddleCenter;
            errorLabel.Bounds = new Rectangle(Px(28), Px(116), Px(324), Px(20));

            RoundButton unlock = new RoundButton { Text = "Desbloquear", Font = Scaled(15, FontStyle.Bold), Radius = Px(12) };
            unlock.Bounds = new Rectangle(Px(28), Px(142), Px(324), Px(46));
            unlock.Click += delegate { TryUnlock(); };
            AcceptButton = unlock;

            unlockPanel.Controls.AddRange(new Control[] { title, passwordInput, errorLabel, unlock });
            Controls.Add(unlockPanel);
        }

        private void BuildPowerPanel()
        {
            StylePower(restartButton, "Reiniciar");
            restartButton.Click += delegate { Power(restartButton, "/r /t 0"); };
            StylePower(shutdownButton, "Apagar");
            shutdownButton.Click += delegate { Power(shutdownButton, "/s /t 0"); };
            Controls.Add(restartButton);
            Controls.Add(shutdownButton);
        }

        private void StylePower(RoundButton button, string title)
        {
            button.Text = title;
            button.Tag = title;
            button.Fill = Theme.Window;
            button.Font = Scaled(15);
            button.Radius = Px(12);
            button.Size = new Size(Px(140), Px(44));
        }

        // El primer clic solo arma el botón: una pulsación accidental no apaga el equipo.
        private void Power(RoundButton button, string arguments)
        {
            if (armedButton != button)
            {
                Disarm();
                armedButton = button;
                button.Text = "¿Confirmar?";
                button.Fill = Theme.Danger;
                button.Invalidate();
                confirmReset.Start();
                return;
            }

            Disarm();
            Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
        }

        private void Disarm()
        {
            confirmReset.Stop();
            if (armedButton == null)
                return;
            armedButton.Text = (string)armedButton.Tag;
            armedButton.Fill = Theme.Window;
            armedButton.Invalidate();
            armedButton = null;
        }

        private void TryUnlock()
        {
            bool valid = settings.Check(passwordInput.Box.Text);
            passwordInput.Box.Clear();
            if (valid)
            {
                if (Unlocked != null)
                    Unlocked(this, EventArgs.Empty);
                return;
            }
            errorLabel.Text = "Contraseña incorrecta";
            passwordInput.Box.Focus();
        }

        public void FocusPassword()
        {
            passwordInput.Box.Focus();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            passwordInput.Box.Focus();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int bottom = ClientSize.Height - shutdownButton.Height - Px(28);
            unlockPanel.Location = new Point((ClientSize.Width - unlockPanel.Width) / 2, (ClientSize.Height - unlockPanel.Height) / 2);
            shutdownButton.Location = new Point(ClientSize.Width - shutdownButton.Width - Px(28), bottom);
            restartButton.Location = new Point(shutdownButton.Left - restartButton.Width - Px(12), bottom);
        }

        // Los controles redondeados repintan el fondo tras sus esquinas: se reutiliza una copia ya escalada.
        private Bitmap ScaledBackground()
        {
            if (background != null && background.Size == ClientSize)
                return background;
            if (background != null)
                background.Dispose();

            background = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (Graphics g = Graphics.FromImage(background))
            {
                if (image != null)
                {
                    float ratio = Math.Max((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height);
                    float width = image.Width * ratio;
                    float height = image.Height * ratio;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, (ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
                }
                else
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(24, 25, 33), Color.FromArgb(44, 40, 66), 60f))
                        g.FillRectangle(brush, ClientRectangle);
                }
            }
            return background;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
                e.Graphics.DrawImageUnscaled(ScaledBackground(), 0, 0);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                confirmReset.Dispose();
                if (background != null)
                    background.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
