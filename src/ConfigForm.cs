using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CandadoApp
{
    internal sealed class ConfigForm : Form
    {
        private const int SidebarWidth = 216;
        private const int ContentLeft = 236;
        private const int ContentWidth = 648;
        private const int CardTop = 146;
        private const int CardHeight = 544;
        private const int Inner = 28;
        private const int FieldLeft = 190;
        private const int FieldWidth = 430;
        private const int RowHeight = 54;

        private readonly Settings settings;
        private readonly Countdown countdown;
        private readonly Banner banner = new Banner();
        private readonly List<NavButton> navButtons = new List<NavButton>();
        private readonly List<Card> pages = new List<Card>();
        private readonly List<string[]> pageTitles = new List<string[]>();

        // Reloj / temporizador
        private readonly RoundButton countdownButton = new RoundButton();
        private readonly RoundButton stopButton = new RoundButton();
        private readonly List<RoundSpinner> durationBoxes = new List<RoundSpinner>();
        private readonly Label hotkeyStatus = new Label();
        private readonly RoundInput hotkeyBox = new RoundInput();
        private readonly ToggleSwitch visibleBox = new ToggleSwitch("Mostrar el reloj");
        private readonly ColorDialog colorDialog = new ColorDialog { FullOpen = true };
        private readonly Timer bannerTimer = new Timer { Interval = 250 };

        // Protección de aplicaciones
        private readonly AppList appList = new AppList();
        private readonly Label passwordStatus = new Label();
        private readonly ToggleSwitch protectionBox = new ToggleSwitch("Protección activa");

        // Actualización
        private readonly Card updatePanel = new Card { Fill = Theme.Field, Radius = 14, Visible = false };
        private readonly RoundButton updateButton = new RoundButton();
        private UpdateInfo updateInfo;

        private Card page;
        private int rowY;
        private int navY;

        public bool AllowClose;

        public event EventHandler SettingsChanged;
        public event EventHandler QuitRequested;

        public ConfigForm(Settings settings, Countdown countdown)
        {
            this.settings = settings;
            this.countdown = countdown;
            countdown.Changed += delegate { RefreshCountdown(); };

            Text = "Abrir " + Program.Name;
            Icon = Program.AppIcon;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(10);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(ContentLeft + ContentWidth + 20, CardTop + CardHeight + 20);

            banner.Bounds = new Rectangle(ContentLeft, 42, ContentWidth, 88);
            Controls.Add(banner);
            MakeDraggable(banner);
            MakeDraggable(this);

            IconButton close = new IconButton("");
            close.Bounds = new Rectangle(ClientSize.Width - 44, 8, 32, 28);
            close.Click += delegate { Hide(); };
            IconButton minimize = new IconButton("");
            minimize.Bounds = new Rectangle(ClientSize.Width - 80, 8, 32, 28);
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(close);
            Controls.Add(minimize);

            navY = 92;
            NavSection("Reloj");
            AddPage("", "Apariencia", "Fuente, tamaño y colores del reloj", BuildAppearance);
            AddPage("", "Posición", "Elige dónde aparece en tu pantalla", BuildPlacement);
            NavSection("Temporizador");
            AddPage("", "Cuenta regresiva", "Define el tiempo y ponlo en marcha", BuildCountdown);
            NavSection("Protección");
            AddPage("", "Aplicaciones", "Programas que pedirán la contraseña", BuildApps);
            AddPage("", "Contraseña", "Clave y activación de la protección", BuildPassword);
            AddPage("", "Bloqueo", "Pantalla de bloqueo e imagen", BuildBlock);
            NavSection("Sistema");
            AddPage("", "Sistema", "Atajo, visibilidad y arranque", BuildSystem);

            NavButton quit = new NavButton("", "Salir del programa") { Bounds = new Rectangle(16, ClientSize.Height - 62, SidebarWidth - 32, 44) };
            quit.Click += delegate
            {
                if (QuitRequested != null)
                    QuitRequested(this, EventArgs.Empty);
            };
            Controls.Add(quit);

            // Panel de actualización, abajo en la barra lateral (oculto hasta que haya una versión nueva).
            updatePanel.Bounds = new Rectangle(16, ClientSize.Height - 150, SidebarWidth - 32, 78);
            Label updateTitle = new Label
            {
                Text = "Actualización disponible",
                AutoSize = false,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                Bounds = new Rectangle(12, 10, SidebarWidth - 56, 20)
            };
            updateButton.Text = "Actualizar";
            updateButton.Font = Theme.UiFont(10, FontStyle.Bold);
            updateButton.Bounds = new Rectangle(12, 36, SidebarWidth - 56, 32);
            updateButton.Click += delegate { StartUpdate(); };
            updatePanel.Controls.Add(updateTitle);
            updatePanel.Controls.Add(updateButton);
            Controls.Add(updatePanel);
            updatePanel.BringToFront();

            bannerTimer.Tick += delegate { RefreshBanner(); };
            RefreshCountdown();
            ShowPage(0);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundWindow(this, true);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            bannerTimer.Enabled = Visible;
            RefreshBanner();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme.Prepare(e.Graphics);
            using (Pen line = new Pen(Theme.Line))
                e.Graphics.DrawLine(line, SidebarWidth, 0, SidebarWidth, ClientSize.Height);
            e.Graphics.DrawIcon(Program.AppIcon, new Rectangle(24, 28, 28, 28));
            using (Font logo = Theme.UiFont(13, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Theme.Text))
                e.Graphics.DrawString(Program.Name, logo, brush, 58, 29);
        }

        private void MakeDraggable(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                    return;
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
            };
        }

        public void SetClockVisible(bool visible)
        {
            visibleBox.Checked = visible;
        }

        public void SetHotkeyStatus(string message)
        {
            hotkeyStatus.Text = message;
        }

        public void ShowUpdateAvailable(UpdateInfo info)
        {
            updateInfo = info;
            updateButton.Enabled = true;
            updateButton.Text = "Actualizar";
            updatePanel.Visible = true;
            updatePanel.BringToFront();
        }

        private void StartUpdate()
        {
            if (updateInfo == null)
                return;
            if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
            {
                OpenReleasePage();
                return;
            }
            updateButton.Enabled = false;
            updateButton.Text = "Descargando…";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = Updater.ApplyUpdate(updateInfo);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (ok)
                        {
                            Application.Exit();
                        }
                        else
                        {
                            updateButton.Enabled = true;
                            updateButton.Text = "Actualizar";
                            OpenReleasePage();
                        }
                    }));
                }
                catch { }
            });
        }

        private void OpenReleasePage()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(updateInfo.PageUrl) { UseShellExecute = true }); }
            catch { }
        }

        private void Changed()
        {
            if (SettingsChanged != null)
                SettingsChanged(this, EventArgs.Empty);
        }

        private void NavSection(string title)
        {
            Label label = new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                Font = Theme.UiFont(8.5f),
                Location = new Point(28, navY)
            };
            Controls.Add(label);
            navY += 26;
        }

        private void AddPage(string glyph, string title, string subtitle, Action build)
        {
            int index = pages.Count;
            NavButton button = new NavButton(glyph, title) { Bounds = new Rectangle(16, navY, SidebarWidth - 32, 42) };
            button.Click += delegate { ShowPage(index); };
            Controls.Add(button);
            navButtons.Add(button);
            navY += 46;

            page = new Card { Bounds = new Rectangle(ContentLeft, CardTop, ContentWidth, CardHeight), Visible = false };
            Controls.Add(page);
            pages.Add(page);
            pageTitles.Add(new[] { title, subtitle });
            rowY = Inner;
            build();
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                navButtons[i].Selected = i == index;
            }
            banner.SetTitle(pageTitles[index][0], pageTitles[index][1]);
        }

        private void RefreshBanner()
        {
            banner.SetTime(countdown.Active ? countdown.Text : DateTime.Now.ToString("HH:mm:ss"));
        }

        // ---------- Reloj ----------

        private void BuildAppearance()
        {
            RoundCombo fonts = new RoundCombo { Font = Font };
            fonts.Items.AddRange(FontFamily.Families.Select(f => f.Name));
            int font = fonts.Items.IndexOf(settings.FontName);
            fonts.SelectedIndex = font >= 0 ? font : fonts.Items.IndexOf("Segoe UI");
            fonts.SelectedIndexChanged += delegate
            {
                settings.FontName = fonts.SelectedText;
                Changed();
            };
            Row("Fuente", fonts);

            Slider("Tamaño", 10, 200, settings.FontSize, v => v + " pt", v => settings.FontSize = v);
            Slider("Grosor", 1, 10, settings.Weight, v => v == Settings.NormalWeight ? "Normal" : v.ToString(), v => settings.Weight = v);
            Slider("Opacidad del fondo", 0, 100, settings.BackOpacity, v => v + " %", v => settings.BackOpacity = v);

            Title("Color del texto", rowY);
            page.Controls.Add(ColorSwatch(settings.TextColor, v => settings.TextColor = v, FieldLeft));
            Title("Color del fondo", rowY, FieldLeft + 150);
            page.Controls.Add(ColorSwatch(settings.BackColor, v => settings.BackColor = v, FieldLeft + 280));
            rowY += RowHeight;

            ToggleSwitch hours = Toggle("24 horas", settings.Use24Hours, v => { settings.Use24Hours = v; Changed(); });
            ToggleSwitch seconds = Toggle("Mostrar segundos", settings.ShowSeconds, v => { settings.ShowSeconds = v; Changed(); });
            hours.Bounds = new Rectangle(Inner, rowY, 250, 36);
            seconds.Bounds = new Rectangle(Inner + 300, rowY, 292, 36);
            page.Controls.Add(hours);
            page.Controls.Add(seconds);
        }

        private void BuildPlacement()
        {
            Title("Esquina", rowY);
            PositionPicker picker = new PositionPicker
            {
                Bounds = new Rectangle(FieldLeft, rowY, 340, 190),
                Value = settings.Position
            };
            picker.ValueChanged += delegate
            {
                settings.Position = picker.Value;
                Changed();
            };
            page.Controls.Add(picker);
            rowY += 190 + 22;

            RoundCombo monitors = new RoundCombo { Font = Font };
            monitors.Items.Add("Principal");
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                monitors.Items.Add("Pantalla " + (i + 1) + " (" + screens[i].Bounds.Width + "×" + screens[i].Bounds.Height + ")");
            monitors.SelectedIndex = Array.FindIndex(screens, s => s.DeviceName == settings.Monitor) + 1;
            monitors.SelectedIndexChanged += delegate
            {
                settings.Monitor = monitors.SelectedIndex == 0 ? "" : screens[monitors.SelectedIndex - 1].DeviceName;
                Changed();
            };
            Row("Monitor", monitors);

            Slider("Margen", 0, 200, settings.Margin, v => v + " px", v => settings.Margin = v);
        }

        // ---------- Temporizador ----------

        private void BuildCountdown()
        {
            int seconds = settings.CountdownSeconds;
            RoundSpinner hours = Spinner(99, seconds / 3600);
            RoundSpinner minutes = Spinner(59, seconds % 3600 / 60);
            RoundSpinner secondsBox = Spinner(59, seconds % 60);
            EventHandler update = delegate
            {
                settings.CountdownSeconds = hours.Value * 3600 + minutes.Value * 60 + secondsBox.Value;
                Changed();
            };

            Title("Duración", rowY);
            int x = FieldLeft;
            foreach (KeyValuePair<RoundSpinner, string> item in new Dictionary<RoundSpinner, string> { { hours, "h" }, { minutes, "min" }, { secondsBox, "s" } })
            {
                item.Key.Bounds = new Rectangle(x, rowY, 84, 38);
                item.Key.ValueChanged += update;
                page.Controls.Add(item.Key);
                durationBoxes.Add(item.Key);
                Label unit = new Label { Text = item.Value, AutoSize = true, ForeColor = Theme.Muted, BackColor = Color.Transparent, Location = new Point(x + 90, rowY + 9) };
                page.Controls.Add(unit);
                x += 134;
            }
            rowY += RowHeight + 6;

            FullRow(Toggle("Mostrar la cuenta en el reloj", settings.ShowCountdown, v => { settings.ShowCountdown = v; Changed(); }));

            rowY += 12;
            countdownButton.Text = "Iniciar";
            countdownButton.Font = Theme.UiFont(10, FontStyle.Bold);
            countdownButton.Bounds = new Rectangle(Inner, rowY, 200, 44);
            countdownButton.Click += delegate { StartOrPause(); };
            stopButton.Text = "Detener";
            stopButton.Fill = Theme.Field;
            stopButton.Font = Theme.UiFont(10);
            stopButton.Bounds = new Rectangle(Inner + 212, rowY, 130, 44);
            stopButton.Click += delegate { countdown.Stop(); };
            page.Controls.Add(countdownButton);
            page.Controls.Add(stopButton);
        }

        private void StartOrPause()
        {
            if (countdown.Active)
            {
                countdown.TogglePause();
                return;
            }
            if (settings.CountdownSeconds > 0)
                countdown.Start(settings.CountdownSeconds);
        }

        private void RefreshCountdown()
        {
            foreach (RoundSpinner box in durationBoxes)
                box.Enabled = !countdown.Active;
            stopButton.Enabled = countdown.Active;
            countdownButton.Text = !countdown.Active ? "Iniciar" : (countdown.Paused ? "Reanudar" : "Pausar");
        }

        // ---------- Protección de aplicaciones ----------

        private void BuildApps()
        {
            appList.Bounds = new Rectangle(Inner, Inner, ContentWidth - Inner * 2, CardHeight - Inner - 68);
            appList.Removed += delegate(object sender, string fileName)
            {
                settings.Blocked.RemoveAll(b => b.FileName == fileName);
                RefreshApps();
                Changed();
            };
            page.Controls.Add(appList);
            RefreshApps();

            int y = CardHeight - 56;
            RoundButton addExe = new RoundButton { Text = "Agregar aplicación (.exe)", Font = Theme.UiFont(10, FontStyle.Bold), Bounds = new Rectangle(Inner, y, 290, 40) };
            addExe.Click += delegate { AddFromFile(); };
            RoundButton addSearch = new RoundButton { Text = "Buscar aplicación", Fill = Theme.Field, Font = Theme.UiFont(10), Bounds = new Rectangle(Inner + 302, y, 240, 40) };
            addSearch.Click += delegate { AddFromSearch(); };
            page.Controls.Add(addExe);
            page.Controls.Add(addSearch);
        }

        private void RefreshApps()
        {
            appList.Show(settings.Blocked);
        }

        private void AddBlocked(BlockedApp app)
        {
            if (app == null || app.FileName.Length == 0 || settings.IsBlocked(app.FileName))
                return;
            settings.Blocked.Add(app);
            RefreshApps();
            Changed();
        }

        private void AddFromFile()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Programas|*.exe", Title = "Elegir la aplicación a bloquear" })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    AddBlocked(new BlockedApp(dialog.FileName));
            }
        }

        private void AddFromSearch()
        {
            List<BlockedApp> apps = Watcher.InstalledAndRunning().Where(a => !settings.IsBlocked(a.FileName)).ToList();
            using (AppSearch picker = new AppSearch(apps))
            {
                if (picker.ShowDialog(this) == DialogResult.OK)
                    AddBlocked(picker.Selected);
            }
        }

        private void BuildPassword()
        {
            protectionBox.Checked = settings.ProtectionEnabled && settings.HasCredential;
            protectionBox.CheckedChanged += delegate
            {
                if (protectionBox.Checked && !settings.HasCredential)
                {
                    protectionBox.Checked = false;
                    passwordStatus.ForeColor = Theme.Danger;
                    passwordStatus.Text = "Primero guarda una contraseña o un PIN.";
                    return;
                }
                settings.ProtectionEnabled = protectionBox.Checked;
                Changed();
            };
            FullRow(protectionBox);

            rowY += 6;
            RoundInput password = PasswordInput();
            RoundInput repeat = PasswordInput();
            Row("Nueva contraseña", password);
            Row("Repetir", repeat);

            RoundButton save = new RoundButton { Text = "Guardar contraseña", Font = Theme.UiFont(10, FontStyle.Bold), Bounds = new Rectangle(FieldLeft, rowY, 190, 42) };
            passwordStatus.ForeColor = Theme.Muted;
            passwordStatus.BackColor = Color.Transparent;
            passwordStatus.TextAlign = ContentAlignment.MiddleLeft;
            passwordStatus.Bounds = new Rectangle(FieldLeft + 204, rowY, FieldWidth - 204, 42);
            passwordStatus.Text = settings.HasPassword ? "Contraseña definida." : "Sin contraseña.";
            save.Click += delegate
            {
                string first = password.Box.Text;
                if (first.Length == 0 || first != repeat.Box.Text)
                {
                    passwordStatus.ForeColor = Theme.Danger;
                    passwordStatus.Text = first.Length == 0 ? "Escribe una contraseña." : "No coinciden.";
                    return;
                }
                settings.SetPassword(first);
                password.Box.Clear();
                repeat.Box.Clear();
                passwordStatus.ForeColor = Theme.Muted;
                passwordStatus.Text = "Contraseña guardada.";
                Changed();
            };
            page.Controls.Add(save);
            page.Controls.Add(passwordStatus);

            rowY += RowHeight + 10;
            RoundInput pin = PasswordInput();
            pin.Box.MaxLength = 12;
            Row("PIN (opcional)", pin);

            RoundButton savePin = new RoundButton { Text = "Guardar PIN", Font = Theme.UiFont(10, FontStyle.Bold), Bounds = new Rectangle(FieldLeft, rowY, 190, 42) };
            Label pinStatus = new Label
            {
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(FieldLeft + 204, rowY, FieldWidth - 204, 42),
                Text = settings.HasPin ? "PIN definido." : "Sin PIN (mín. 4 dígitos)."
            };
            savePin.Click += delegate
            {
                string code = pin.Box.Text.Trim();
                if (code.Length == 0)
                {
                    settings.ClearPin();
                    pinStatus.ForeColor = Theme.Muted;
                    pinStatus.Text = "PIN quitado.";
                    Changed();
                    return;
                }
                if (!code.All(char.IsDigit) || code.Length < 4)
                {
                    pinStatus.ForeColor = Theme.Danger;
                    pinStatus.Text = "Solo números, mínimo 4 dígitos.";
                    return;
                }
                settings.SetPin(code);
                pin.Box.Clear();
                pinStatus.ForeColor = Theme.Muted;
                pinStatus.Text = "PIN guardado.";
                Changed();
            };
            page.Controls.Add(savePin);
            page.Controls.Add(pinStatus);
        }

        // ---------- Bloqueo (compartido) ----------

        private void BuildBlock()
        {
            Label blockStatus = new Label { AutoSize = true, ForeColor = Theme.Danger, BackColor = Color.Transparent, Location = new Point(Inner, 0), Visible = false };

            ToggleSwitch lockBox = new ToggleSwitch("Bloquear la pantalla al terminar la cuenta") { Checked = settings.LockEnabled && settings.HasCredential };
            lockBox.CheckedChanged += delegate
            {
                if (lockBox.Checked && !settings.HasCredential)
                {
                    lockBox.Checked = false;
                    blockStatus.Text = "Primero guarda una contraseña o un PIN.";
                    blockStatus.Visible = true;
                    return;
                }
                blockStatus.Visible = false;
                settings.LockEnabled = lockBox.Checked;
                Changed();
            };
            FullRow(lockBox);

            blockStatus.Location = new Point(Inner, rowY - 14);
            page.Controls.Add(blockStatus);

            FullRow(Toggle("Mostrar la X para cerrar sin contraseña (bloqueo de apps)", settings.AllowClose, v => { settings.AllowClose = v; Changed(); }));

            rowY += 8;
            Title("Imagen de bloqueo", rowY);
            RoundButton imageButton = new RoundButton { Text = "Elegir imagen", Fill = Theme.Field, Font = Font, Bounds = new Rectangle(FieldLeft, rowY, 150, 38) };
            Label imageName = new Label
            {
                Text = ImageCaption(),
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(FieldLeft + 164, rowY, FieldWidth - 164, 38)
            };
            imageButton.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return;
                    settings.LockImage = dialog.FileName;
                    imageName.Text = ImageCaption();
                    Changed();
                }
            };
            page.Controls.Add(imageButton);
            page.Controls.Add(imageName);
            rowY += RowHeight;

            RoundButton clear = new RoundButton { Text = "Quitar imagen (fondo oscuro)", Fill = Theme.Field, Font = Font, Bounds = new Rectangle(FieldLeft, rowY, 260, 38) };
            clear.Click += delegate
            {
                settings.LockImage = "";
                imageName.Text = ImageCaption();
                Changed();
            };
            page.Controls.Add(clear);
            rowY += RowHeight;

            page.Controls.Add(new Label
            {
                Text = "La imagen y la contraseña se usan tanto para el bloqueo de la cuenta como para el de aplicaciones.",
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                MaximumSize = new Size(ContentWidth - Inner * 2, 0),
                AutoSize = true,
                Location = new Point(Inner, rowY)
            });
        }

        private string ImageCaption()
        {
            return settings.LockImage.Length > 0 ? Path.GetFileName(settings.LockImage) : "Sin imagen (fondo oscuro)";
        }

        // ---------- Sistema ----------

        private void BuildSystem()
        {
            ToggleSwitch enabled = Toggle("Atajo para mostrar u ocultar el reloj", settings.HotkeyEnabled, v => { settings.HotkeyEnabled = v; Changed(); });
            FullRow(enabled);

            hotkeyBox.Font = Font;
            hotkeyBox.Box.ReadOnly = true;
            hotkeyBox.Box.TextAlign = HorizontalAlignment.Center;
            hotkeyBox.Box.Text = HotkeyText(settings.HotkeyModifiers, settings.HotkeyKey);
            hotkeyBox.Box.KeyDown += OnHotkeyKeyDown;
            Row("Combinación", hotkeyBox);

            hotkeyStatus.AutoSize = true;
            hotkeyStatus.ForeColor = Theme.Danger;
            hotkeyStatus.BackColor = Color.Transparent;
            hotkeyStatus.Location = new Point(FieldLeft, rowY - 12);
            page.Controls.Add(hotkeyStatus);
            rowY += 22;

            visibleBox.Checked = settings.ClockVisible;
            visibleBox.CheckedChanged += delegate
            {
                if (settings.ClockVisible == visibleBox.Checked)
                    return;
                settings.ClockVisible = visibleBox.Checked;
                Changed();
            };
            FullRow(visibleBox);

            FullRow(Toggle("Dejar pasar los clics a través del reloj", settings.ClickThrough, v => { settings.ClickThrough = v; Changed(); }));

            ToggleSwitch startup = Toggle("Iniciar con Windows", Startup.IsEnabled(), Startup.Set);
            FullRow(startup);
        }

        private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
                return;

            uint modifiers = 0;
            if (e.Control) modifiers |= Native.MOD_CONTROL;
            if (e.Shift) modifiers |= Native.MOD_SHIFT;
            if (e.Alt) modifiers |= Native.MOD_ALT;
            bool functionKey = key >= Keys.F1 && key <= Keys.F24;
            if (modifiers == 0 && !functionKey)
            {
                hotkeyStatus.Text = "Usa Ctrl, Alt o Mayús, o una tecla F.";
                return;
            }

            settings.HotkeyModifiers = modifiers;
            settings.HotkeyKey = (uint)key;
            hotkeyBox.Box.Text = HotkeyText(modifiers, settings.HotkeyKey);
            Changed();
        }

        private static string HotkeyText(uint modifiers, uint key)
        {
            string text = "";
            if ((modifiers & Native.MOD_CONTROL) != 0) text += "Ctrl + ";
            if ((modifiers & Native.MOD_ALT) != 0) text += "Alt + ";
            if ((modifiers & Native.MOD_SHIFT) != 0) text += "Mayús + ";
            Keys k = (Keys)key;
            string name = k >= Keys.D0 && k <= Keys.D9 ? ((int)(k - Keys.D0)).ToString() : k.ToString();
            return text + name;
        }

        // ---------- Helpers ----------

        private void Title(string title, int y, int x = Inner)
        {
            page.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Location = new Point(x, y + 9)
            });
        }

        private void Row(string title, Control control)
        {
            Title(title, rowY);
            control.Bounds = new Rectangle(FieldLeft, rowY, FieldWidth, 38);
            page.Controls.Add(control);
            rowY += RowHeight;
        }

        private void FullRow(Control control)
        {
            control.Bounds = new Rectangle(Inner, rowY, ContentWidth - Inner * 2, 38);
            page.Controls.Add(control);
            rowY += RowHeight - 6;
        }

        private void Slider(string title, int min, int max, int value, Func<int, string> format, Action<int> apply)
        {
            ModernSlider slider = new ModernSlider { Minimum = min, Maximum = max };
            slider.Value = value;
            Label label = new Label
            {
                Text = format(slider.Value),
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Bounds = new Rectangle(FieldLeft + FieldWidth - 66, rowY, 66, 38)
            };
            slider.ValueChanged += delegate
            {
                label.Text = format(slider.Value);
                apply(slider.Value);
                Changed();
            };
            Title(title, rowY);
            slider.Bounds = new Rectangle(FieldLeft, rowY, FieldWidth - 76, 38);
            page.Controls.Add(slider);
            page.Controls.Add(label);
            rowY += RowHeight;
        }

        private RoundButton ColorSwatch(int value, Action<int> apply, int x)
        {
            RoundButton button = new RoundButton
            {
                Fill = Color.FromArgb(255, Color.FromArgb(value)),
                Bounds = new Rectangle(x, rowY, 80, 38)
            };
            button.Click += delegate
            {
                colorDialog.Color = button.Fill;
                if (colorDialog.ShowDialog(this) != DialogResult.OK)
                    return;
                button.Fill = colorDialog.Color;
                button.Invalidate();
                apply(colorDialog.Color.ToArgb());
                Changed();
            };
            return button;
        }

        private RoundSpinner Spinner(int max, int value)
        {
            RoundSpinner spinner = new RoundSpinner { Maximum = max, Font = Theme.UiFont(11) };
            spinner.Value = value;
            return spinner;
        }

        private RoundInput PasswordInput()
        {
            RoundInput input = new RoundInput { Font = Font };
            input.Box.PasswordChar = '*';
            return input;
        }

        private static ToggleSwitch Toggle(string title, bool value, Action<bool> apply)
        {
            ToggleSwitch box = new ToggleSwitch(title) { Checked = value };
            box.CheckedChanged += delegate { apply(box.Checked); };
            return box;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                colorDialog.Dispose();
                bannerTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
