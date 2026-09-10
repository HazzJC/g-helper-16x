using GHelper.UI;
using GHelper.USB;

namespace GHelper
{
    /// <summary>
    /// Per-key editor for the ZenBook Pro 16X OLED. Paint individual keys, the two side lightbars
    /// and the lid logo, pick a brightness and an animation, then apply the result or save it as
    /// the Custom mode. Built in code rather than with a designer file so the whole layout stays
    /// in one place.
    /// </summary>
    public class Zenbook16XEditor : RForm
    {
        static readonly Color[] Palette =
        {
            Color.Black, Color.White,
            Color.FromArgb(255, 0, 0), Color.FromArgb(255, 128, 0), Color.FromArgb(255, 255, 0),
            Color.FromArgb(128, 255, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 255, 128),
            Color.FromArgb(0, 255, 255), Color.FromArgb(0, 128, 255), Color.FromArgb(0, 0, 255),
            Color.FromArgb(128, 0, 255), Color.FromArgb(255, 0, 255), Color.FromArgb(255, 0, 128),
        };

        readonly KeyboardCanvas canvas = new();
        readonly RColorButton customColor = new();
        readonly CheckBox liveApply = new();
        readonly ComboBox effectCombo = new();
        readonly TrackBar speedBar = new();
        readonly TrackBar brightnessBar = new();

        // Streaming a frame is 11 USB feature reports. Doing that inline on every mouse-move
        // during a drag stalls the UI thread, so changes set a flag and a background timer
        // coalesces them into at most one stream per tick.
        readonly System.Timers.Timer applyTimer = new(60) { AutoReset = true };
        readonly object frameLock = new();
        Color[]? pendingFrame;
        CustomFrameMode pendingEffect;
        double pendingSpeed;
        bool pendingLogo;

        public Zenbook16XEditor()
        {
            Text = "Per-Key Lighting — ZenBook Pro 16X";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(700, 460);

            BuildLayout();
            InitTheme(true);

            // Size after InitTheme: it runs ControlHelper.Resize, which scales the form for DPI,
            // so anything set beforehand gets multiplied and can end up wider than the screen.
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Min((int)(work.Width * 0.85), 1400);
            int height = Math.Min((int)(width / 2.4) + 190, (int)(work.Height * 0.9));
            ClientSize = new Size(width, height);
            Location = new Point(work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height) / 2);

            canvas.LoadColors(Zenbook16XCustom.Load());
            canvas.LogoOn = AppConfig.IsNotFalse("zenbook_custom_logo");
            canvas.PaintColor = Palette[8];
            customColor.SwatchColor = canvas.PaintColor;

            effectCombo.SelectedIndex = Math.Clamp(AppConfig.Get("zenbook_custom_effect", 0), 0, effectCombo.Items.Count - 1);
            speedBar.Value = Math.Clamp(AppConfig.Get("zenbook_custom_speed", 2), speedBar.Minimum, speedBar.Maximum);
            brightnessBar.Value = Math.Clamp(AppConfig.Get(BrightnessKey, 3), 0, 3);

            canvas.CellsChanged += (_, _) => QueueApply();
            effectCombo.SelectedIndexChanged += (_, _) => { Save(); QueueApply(); };
            speedBar.ValueChanged += (_, _) => { Save(); QueueApply(); };
            brightnessBar.ValueChanged += BrightnessBar_ValueChanged;

            applyTimer.Elapsed += ApplyTimer_Elapsed;
            applyTimer.Start();

            FormClosed += (_, _) =>
            {
                applyTimer.Stop();
                applyTimer.Dispose();
                Save();
            };
        }

        void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12),
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            canvas.Dock = DockStyle.Fill;
            root.Controls.Add(canvas, 0, 0);

            // ---- palette strip
            var palettePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 4),
            };

            foreach (var color in Palette)
            {
                var swatch = new Button
                {
                    Size = new Size(30, 30),
                    Margin = new Padding(0, 0, 5, 0),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = color,
                    Tag = color,
                };
                swatch.FlatAppearance.BorderSize = 1;
                swatch.Click += (s, _) =>
                {
                    if (s is Button b && b.Tag is Color c)
                    {
                        canvas.PaintColor = c;
                        customColor.SwatchColor = c;
                    }
                };
                palettePanel.Controls.Add(swatch);
            }

            customColor.Text = "Custom…";
            customColor.Width = 110;
            customColor.Height = 30;
            customColor.Margin = new Padding(12, 0, 0, 0);
            customColor.Click += (_, _) =>
            {
                var picker = new RColorPicker(canvas.PaintColor);
                picker.ColorChanged += c =>
                {
                    canvas.PaintColor = c;
                    customColor.SwatchColor = c;
                };
                picker.ShowDialog(this);
            };
            palettePanel.Controls.Add(customColor);

            root.Controls.Add(palettePanel, 0, 1);

            // ---- effect / speed / brightness
            var tuning = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
            };

            tuning.Controls.Add(Caption("Effect"));
            effectCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            effectCombo.Width = 130;
            effectCombo.Margin = new Padding(0, 4, 16, 0);
            effectCombo.Items.AddRange(new object[] { "Static", "Breathe", "Strobe", "Sweep" });
            tuning.Controls.Add(effectCombo);

            tuning.Controls.Add(Caption("Speed"));
            SetupSlider(speedBar, 1, 5);
            tuning.Controls.Add(speedBar);

            tuning.Controls.Add(Caption("Brightness"));
            SetupSlider(brightnessBar, 0, 3);
            tuning.Controls.Add(brightnessBar);

            root.Controls.Add(tuning, 0, 2);

            // ---- actions
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 0),
            };

            actions.Controls.Add(Action("Fill all", (_, _) => canvas.FillAll(canvas.PaintColor)));
            actions.Controls.Add(Action("Clear", (_, _) => canvas.FillAll(Color.Black)));
            actions.Controls.Add(Action("Apply now", (_, _) => QueueApply(true)));
            actions.Controls.Add(Action("Save as Custom mode", (_, _) => SaveAndActivate(), 190));

            liveApply.Text = "Live preview";
            liveApply.Checked = true;
            liveApply.AutoSize = true;
            liveApply.Margin = new Padding(16, 8, 0, 0);
            actions.Controls.Add(liveApply);

            var hint = new Label
            {
                AutoSize = true,
                Margin = new Padding(20, 9, 0, 0),
                Text = "Click or drag to paint · right-click to clear a key",
            };
            actions.Controls.Add(hint);

            root.Controls.Add(actions, 0, 3);
            Controls.Add(root);
        }

        static Label Caption(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 9, 6, 0),
        };

        static void SetupSlider(TrackBar bar, int min, int max)
        {
            bar.Minimum = min;
            bar.Maximum = max;
            bar.TickStyle = TickStyle.None;
            bar.Width = 110;
            bar.Height = 28;
            bar.Margin = new Padding(0, 2, 16, 0);
        }

        Button Action(string text, EventHandler onClick, int width = 130)
        {
            var button = new RButton { Text = text, Width = width, Height = 32, Margin = new Padding(0, 0, 8, 0) };
            button.Click += onClick;
            return button;
        }

        /// <summary>
        /// The app keeps separate stored levels for AC and battery (confusingly, "..._ac" is the
        /// battery one - see InputDispatcher.SetBacklight), so write whichever is in force.
        /// </summary>
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

        // ------------------------------------------------------------ applying

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
                pendingLogo = canvas.LogoOn;
            }
        }

        void ApplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Color[] frame;
            CustomFrameMode mode;
            double speed;
            bool logo;

            lock (frameLock)
            {
                if (pendingFrame is null) return;
                frame = pendingFrame;
                mode = pendingEffect;
                speed = pendingSpeed;
                logo = pendingLogo;
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

                Program.acpi.SetMonogramLogo(logo);
            }
            catch (Exception ex) { Logger.WriteLine($"PerKeyEditor apply failed: {ex.Message}"); }
        }

        // ------------------------------------------------------------ persistence

        void Save()
        {
            Zenbook16XCustom.Save(canvas.Colors);
            AppConfig.Set("zenbook_custom_logo", canvas.LogoOn ? 1 : 0);
            AppConfig.Set("zenbook_custom_effect", effectCombo.SelectedIndex);
            AppConfig.Set("zenbook_custom_speed", speedBar.Value);
        }

        void SaveAndActivate()
        {
            Save();
            AppConfig.Set("aura_mode", (int)AuraMode.CUSTOM_PERKEY);
            Task.Run(() => { try { Aura.ApplyAura(); } catch { } });
        }
    }

    /// <summary>Persistence for the hand-painted per-key frame.</summary>
    public static class Zenbook16XCustom
    {
        const string KEY = "zenbook_custom_frame";

        public static void Save(Color[] colors)
        {
            var hex = new System.Text.StringBuilder(Zenbook16X.SLOTS * 6);
            for (int i = 0; i < Zenbook16X.SLOTS; i++)
            {
                Color c = i < colors.Length ? colors[i] : Color.Black;
                hex.Append($"{c.R:X2}{c.G:X2}{c.B:X2}");
            }
            AppConfig.Set(KEY, hex.ToString());
        }

        public static Color[] Load()
        {
            var colors = new Color[Zenbook16X.SLOTS];
            Array.Fill(colors, Color.Black);

            string stored = AppConfig.GetString(KEY, "");
            if (stored.Length < Zenbook16X.SLOTS * 6) return colors;

            for (int i = 0; i < Zenbook16X.SLOTS; i++)
            {
                try
                {
                    colors[i] = Color.FromArgb(
                        Convert.ToByte(stored.Substring(i * 6, 2), 16),
                        Convert.ToByte(stored.Substring(i * 6 + 2, 2), 16),
                        Convert.ToByte(stored.Substring(i * 6 + 4, 2), 16));
                }
                catch { colors[i] = Color.Black; }
            }
            return colors;
        }

        public static CustomFrameMode Effect => (CustomFrameMode)Math.Clamp(AppConfig.Get("zenbook_custom_effect", 0), 0, 3);

        public static double Speed => Math.Clamp(AppConfig.Get("zenbook_custom_speed", 2), 1, 5) * 0.4;
    }
}
