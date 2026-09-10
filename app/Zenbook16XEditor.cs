using GHelper.UI;
using GHelper.USB;
using System.Drawing.Drawing2D;

namespace GHelper
{
    /// <summary>
    /// Per-key editor for the ZenBook Pro 16X OLED. Paint individual keys, the two side lightbars
    /// and the lid logo, pick an animation and brightness, and keep as many named profiles as you
    /// like. Built in code rather than with a designer file so the whole layout stays in one place.
    /// </summary>
    public class Zenbook16XEditor : RForm
    {
        /// <summary>Scale for the fixed pixel sizes below; buttons auto-size instead.</summary>
        float DpiScale => Math.Max(1f, DeviceDpi / 96f);

        static readonly Color[] Palette =
        {
            Color.Black, Color.White,
            Color.FromArgb(255, 0, 0), Color.FromArgb(255, 128, 0), Color.FromArgb(255, 255, 0),
            Color.FromArgb(128, 255, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 255, 128),
            Color.FromArgb(0, 255, 255), Color.FromArgb(0, 128, 255), Color.FromArgb(0, 0, 255),
            Color.FromArgb(128, 0, 255), Color.FromArgb(255, 0, 255), Color.FromArgb(255, 0, 128),
        };

        readonly KeyboardCanvas canvas = new();
        readonly RComboBox profileCombo = new();
        readonly RComboBox effectCombo = new();
        readonly TrackBar speedBar = new();
        readonly TrackBar brightnessBar = new();
        readonly CheckBox liveApply = new();
        readonly Label status = new();
        readonly List<Swatch> swatches = new();
        Swatch? customSwatch;

        Zenbook16XProfile profile = new();
        bool loading;

        // Streaming a frame is 11 USB feature reports; doing that inline on every mouse-move
        // during a drag stalls the UI thread. Changes are snapshotted and a background timer
        // coalesces them into at most one stream per tick.
        readonly System.Timers.Timer applyTimer = new(60) { AutoReset = true };
        readonly object frameLock = new();
        Color[]? pendingFrame;
        CustomFrameMode pendingEffect;
        double pendingSpeed;

        public Zenbook16XEditor()
        {
            Text = "Per-Key Lighting — ZenBook Pro 16X";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 520);

            BuildLayout();
            InitTheme(true);

            // Size after InitTheme: it runs ControlHelper.Resize, which scales the form for DPI,
            // so anything set beforehand gets multiplied and can end up wider than the screen.
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Min((int)(work.Width * 0.86), 1440);
            int height = Math.Min((int)(width / 2.35) + 210, (int)(work.Height * 0.92));
            ClientSize = new Size(width, height);
            Location = new Point(work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height) / 2);

            // The Aura layer must have detected the backlight before ApplyBrightness or ApplyAura
            // will do anything, and the editor can be opened before that has happened.
            if (!Aura.IsBacklightDetected)
            {
                try { Aura.Init(); } catch (Exception ex) { Logger.WriteLine($"PerKeyEditor init: {ex.Message}"); }
            }

            RefreshProfileList();
            LoadProfile(Zenbook16XProfiles.ActiveName);

            brightnessBar.Value = Math.Clamp(AppConfig.Get(BrightnessKey, 3), 0, 3);
            SelectPaintColor(Palette[8]);

            canvas.CellsChanged += (_, _) => { Store("Saved"); QueueApply(); };
            effectCombo.SelectedIndexChanged += (_, _) => { if (!loading) { Store("Saved"); QueueApply(); } };
            speedBar.ValueChanged += (_, _) => { if (!loading) { Store("Saved"); QueueApply(); } };
            brightnessBar.ValueChanged += BrightnessBar_ValueChanged;
            profileCombo.SelectedIndexChanged += ProfileCombo_SelectedIndexChanged;

            applyTimer.Elapsed += ApplyTimer_Elapsed;
            applyTimer.Start();

            FormClosed += (_, _) =>
            {
                applyTimer.Stop();
                applyTimer.Dispose();
                StoreProfile();
            };
        }

        // ------------------------------------------------------------ layout

        void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16, 12, 16, 14),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildProfileBar(), 0, 0);

            canvas.Dock = DockStyle.Fill;
            canvas.Margin = new Padding(0, 10, 0, 10);
            root.Controls.Add(canvas, 0, 1);

            root.Controls.Add(BuildPaletteBar(), 0, 2);
            root.Controls.Add(BuildControlBar(), 0, 3);

            Controls.Add(root);
        }

        Control BuildProfileBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };

            bar.Controls.Add(Caption("Profile", 7));

            profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            profileCombo.Width = (int)(190 * DpiScale);
            profileCombo.Margin = new Padding(0, 3, 10, 0);
            bar.Controls.Add(profileCombo);

            bar.Controls.Add(Action("New…", (_, _) => NewProfile(), 84));
            bar.Controls.Add(Action("Rename…", (_, _) => RenameProfile(), 96));
            bar.Controls.Add(Action("Duplicate", (_, _) => DuplicateProfile(), 96));
            bar.Controls.Add(Action("Delete", (_, _) => DeleteProfile(), 84));

            status.AutoSize = true;
            status.Margin = new Padding(16, 10, 0, 0);
            status.ForeColor = Color.FromArgb(150, 150, 150);
            bar.Controls.Add(status);

            return bar;
        }

        Control BuildPaletteBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };

            foreach (var color in Palette)
            {
                var swatch = new Swatch(color) { Size = new Size((int)(28 * DpiScale), (int)(28 * DpiScale)) };
                swatch.Click += (_, _) => SelectPaintColor(swatch.Color);
                swatches.Add(swatch);
                bar.Controls.Add(swatch);
            }

            customSwatch = new Swatch(Palette[8]) { Margin = new Padding(14, 4, 6, 4), Size = new Size((int)(28 * DpiScale), (int)(28 * DpiScale)) };
            customSwatch.Click += (_, _) =>
            {
                var picker = new RColorPicker(canvas.PaintColor);
                picker.ColorChanged += SelectPaintColor;
                picker.ShowDialog(this);
            };
            swatches.Add(customSwatch);
            bar.Controls.Add(customSwatch);

            bar.Controls.Add(new Label
            {
                Text = "pick…",
                AutoSize = true,
                Margin = new Padding(0, 11, 20, 0),
                ForeColor = Color.FromArgb(150, 150, 150),
            });

            bar.Controls.Add(Action("Fill all", (_, _) => canvas.FillAll(canvas.PaintColor), 84));
            bar.Controls.Add(Action("Clear", (_, _) => canvas.FillAll(Color.Black), 76));

            return bar;
        }

        Control BuildControlBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
            };

            bar.Controls.Add(Caption("Effect"));
            effectCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            effectCombo.Width = (int)(120 * DpiScale);
            effectCombo.Margin = new Padding(0, 3, 18, 0);
            effectCombo.Items.AddRange(new object[] { "Static", "Breathe", "Strobe", "Sweep" });
            bar.Controls.Add(effectCombo);

            bar.Controls.Add(Caption("Speed"));
            SetupSlider(speedBar, 1, 5);
            bar.Controls.Add(speedBar);

            bar.Controls.Add(Caption("Brightness"));
            SetupSlider(brightnessBar, 0, 3);
            bar.Controls.Add(brightnessBar);

            liveApply.Text = "Live preview";
            liveApply.Checked = true;
            liveApply.AutoSize = true;
            liveApply.Margin = new Padding(4, 9, 18, 0);
            bar.Controls.Add(liveApply);

            bar.Controls.Add(Action("Apply now", (_, _) => QueueApply(true), 104));
            bar.Controls.Add(Action("Set as active mode", (_, _) => SetAsActiveMode(), 164));
            bar.Controls.Add(Action("Save and close", (_, _) => SaveAndClose(), 136));

            return bar;
        }

        static Label Caption(string text, int topMargin = 9) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, topMargin, 8, 0),
        };

        void SetupSlider(TrackBar bar, int min, int max)
        {
            bar.Minimum = min;
            bar.Maximum = max;
            bar.TickStyle = TickStyle.None;
            bar.Width = (int)(100 * DpiScale);
            bar.Height = 28;
            bar.Margin = new Padding(0, 2, 18, 0);
        }

        /// <summary>
        /// Buttons size to their own text. Fixed pixel widths clip once the form is DPI-scaled -
        /// the label grows with the font but the width doesn't.
        /// </summary>
        Button Action(string text, EventHandler onClick, int _ = 0)
        {
            var button = new RButton
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 5, 12, 5),
                Margin = new Padding(0, 2, 8, 0),
            };
            button.Click += onClick;
            return button;
        }

        void SelectPaintColor(Color color)
        {
            canvas.PaintColor = color;
            if (customSwatch is not null && !Palette.Contains(color)) customSwatch.Color = color;
            foreach (var s in swatches) s.Selected = s.Color == color;
        }

        // ------------------------------------------------------------ profiles

        void RefreshProfileList()
        {
            loading = true;
            profileCombo.Items.Clear();
            foreach (var name in Zenbook16XProfiles.Names()) profileCombo.Items.Add(name);
            loading = false;
        }

        void LoadProfile(string name)
        {
            loading = true;

            profile = Zenbook16XProfiles.Load(name);
            canvas.LoadColors(profile.Frame);
            effectCombo.SelectedIndex = (int)profile.Effect;
            speedBar.Value = Math.Clamp(profile.Speed, 1, 5);

            int index = profileCombo.Items.IndexOf(name);
            if (index >= 0) profileCombo.SelectedIndex = index;

            loading = false;
            SetStatus($"Loaded “{name}”");
            QueueApply();
        }

        /// <summary>Copies the current UI state into the profile object and persists it.</summary>
        void StoreProfile()
        {
            Array.Copy(canvas.Colors, profile.Frame, Math.Min(canvas.Colors.Length, profile.Frame.Length));
            profile.Effect = (CustomFrameMode)Math.Clamp(effectCombo.SelectedIndex, 0, 3);
            profile.Speed = speedBar.Value;
            Zenbook16XProfiles.Save(profile);
            Zenbook16XProfiles.ActiveName = profile.Name;
        }

        void Store(string message)
        {
            StoreProfile();
            SetStatus($"{message} to “{profile.Name}”");
        }

        void ProfileCombo_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (loading || profileCombo.SelectedItem is not string name || name == profile.Name) return;
            StoreProfile();
            LoadProfile(name);
        }

        void NewProfile()
        {
            string? name = Prompt("New profile", "Name:", "Profile " + (Zenbook16XProfiles.Names().Count + 1));
            if (name is null) return;

            StoreProfile();
            profile = new Zenbook16XProfile { Name = Unique(Zenbook16XProfiles.Sanitize(name)) };
            Zenbook16XProfiles.Save(profile);
            RefreshProfileList();
            LoadProfile(profile.Name);
        }

        void DuplicateProfile()
        {
            string? name = Prompt("Duplicate profile", "Name:", profile.Name + " copy");
            if (name is null) return;

            StoreProfile();
            var copy = profile.Clone();
            copy.Name = Unique(Zenbook16XProfiles.Sanitize(name));
            Zenbook16XProfiles.Save(copy);
            RefreshProfileList();
            LoadProfile(copy.Name);
        }

        void RenameProfile()
        {
            string? name = Prompt("Rename profile", "Name:", profile.Name);
            if (name is null) return;

            string clean = Zenbook16XProfiles.Sanitize(name);
            if (clean == profile.Name) return;
            clean = Unique(clean);

            string old = profile.Name;
            StoreProfile();
            profile.Name = clean;
            Zenbook16XProfiles.Save(profile);
            Zenbook16XProfiles.Delete(old);
            RefreshProfileList();
            LoadProfile(clean);
        }

        void DeleteProfile()
        {
            if (Zenbook16XProfiles.Names().Count <= 1)
            {
                SetStatus("Can't delete the only profile");
                return;
            }

            if (MessageBox.Show($"Delete profile “{profile.Name}”?", "Delete profile",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            Zenbook16XProfiles.Delete(profile.Name);
            RefreshProfileList();
            LoadProfile(Zenbook16XProfiles.Names()[0]);
        }

        static string Unique(string name)
        {
            var names = Zenbook16XProfiles.Names();
            if (!names.Contains(name)) return name;

            for (int i = 2; i < 100; i++)
                if (!names.Contains($"{name} {i}")) return $"{name} {i}";
            return name + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>Small inline text prompt - WinForms has no built-in one.</summary>
        string? Prompt(string title, string label, string initial)
        {
            using var dialog = new RForm
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(360, 130),
            };

            var prompt = new Label { Text = label, AutoSize = true, Location = new Point(16, 18) };
            var input = new RTextBox { Text = initial, Location = new Point(16, 44), Width = 328 };
            var ok = new RButton { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(168, 84), Width = 84, Height = 30 };
            var cancel = new RButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(260, 84), Width = 84, Height = 30 };

            dialog.Controls.AddRange(new Control[] { prompt, input, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            dialog.InitTheme();

            input.SelectAll();
            return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
        }

        void SetStatus(string text)
        {
            if (status.InvokeRequired) status.BeginInvoke(() => status.Text = text);
            else status.Text = text;
        }

        // ------------------------------------------------------------ applying

        static string BrightnessKey => SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online
            ? "keyboard_brightness_ac"
            : "keyboard_brightness";

        void BrightnessBar_ValueChanged(object? sender, EventArgs e)
        {
            int level = brightnessBar.Value;
            AppConfig.Set(BrightnessKey, level);

            Task.Run(() =>
            {
                try { Aura.ApplyBrightness(level, "PerKeyEditor"); } catch { }
            });

            // ApplyBrightness stops the effect engine when it hits zero, so repaint after it.
            QueueApply(true);
        }

        /// <param name="force">
        /// True for an explicit "Apply now"; false for the automatic calls, which only stream
        /// when live preview is on.
        /// </param>
        void QueueApply(bool force = false)
        {
            if (!force && !liveApply.Checked) return;

            // Snapshot everything the timer needs on the UI thread, so the timer thread never has
            // to marshal back and can't deadlock against a UI thread waiting on frameLock.
            var snapshot = new Color[Zenbook16X.SLOTS];
            Array.Copy(canvas.Colors, snapshot, Math.Min(canvas.Colors.Length, snapshot.Length));

            lock (frameLock)
            {
                pendingFrame = snapshot;
                pendingEffect = (CustomFrameMode)Math.Clamp(effectCombo.SelectedIndex, 0, 3);
                pendingSpeed = speedBar.Value * 0.4;
            }
        }

        void ApplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Color[] frame;
            CustomFrameMode mode;
            double speed;

            lock (frameLock)
            {
                if (pendingFrame is null) return;
                frame = pendingFrame;
                mode = pendingEffect;
                speed = pendingSpeed;
                pendingFrame = null;
            }

            try
            {
                if (mode == CustomFrameMode.Static)
                {
                    PerKeyEngine.Stop();
                    Zenbook16X.DirectEnable();
                    Zenbook16X.Stream(frame);
                }
                else
                {
                    PerKeyEngine.Start(new CustomFrameEffect(frame, mode, speed));
                }
            }
            catch (Exception ex) { Logger.WriteLine($"PerKeyEditor apply failed: {ex.Message}"); }
        }

        /// <summary>
        /// Makes this profile the keyboard's active mode, so it survives closing the editor.
        /// Streams directly rather than relying on ApplyAura, which is a no-op while the backlight
        /// is off and would otherwise silently do nothing.
        /// </summary>
        void SetAsActiveMode()
        {
            StoreProfile();
            AppConfig.Set("aura_mode", (int)AuraMode.CUSTOM_PERKEY);

            try { Aura.Mode = AuraMode.CUSTOM_PERKEY; } catch { }
            QueueApply(true);

            SetStatus($"“{profile.Name}” is now the active keyboard mode");
        }

        /// <summary>
        /// Saves the profile, makes it the active keyboard mode so it survives the editor closing,
        /// and shuts the window.
        /// </summary>
        void SaveAndClose()
        {
            SetAsActiveMode();
            Close();
        }
    }

    /// <summary>A round colour swatch with a selection ring.</summary>
    class Swatch : Control
    {
        Color color;
        bool selected;

        public Color Color
        {
            get => color;
            set { color = value; Invalidate(); }
        }

        public bool Selected
        {
            get => selected;
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        public Swatch(Color color)
        {
            this.color = color;
            Size = new Size(28, 28);
            Margin = new Padding(0, 4, 6, 4);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? RForm.formBack);

            var box = new Rectangle(3, 3, Width - 7, Height - 7);

            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, box);
            using (var edge = new Pen(Color.FromArgb(110, 110, 116))) g.DrawEllipse(edge, box);

            if (selected)
            {
                using var ring = new Pen(RForm.foreMain, 2f);
                g.DrawEllipse(ring, 1, 1, Width - 3, Height - 3);
            }
        }
    }
}
