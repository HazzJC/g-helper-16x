using System.Drawing;

namespace GHelper.USB
{
    /// <summary>
    /// Per-key lighting for the ZenBook Pro 16X OLED (UX7602), report 0x5C on MI_01/COL04.
    ///
    /// Layout (confirmed live on a UX7602BZ by lighting slot ranges and reading the result off
    /// the physical keyboard): the LED index space is a row-major matrix with a stride of 21.
    /// Slot = row * 21 + column.
    ///
    ///   row 0   slot 0 = the lid logo (full RGB); slots 1-20 dead
    ///   row 1   slots  21- 41   Esc, F1-F12, PrtSc, Insert, Delete      (cols 0-15)
    ///   row 2   slots  42- 62   `, 1-0, -, =, Backspace x3, Home        (cols 0-16)
    ///   row 3   slots  63- 83   Tab, Q-P, [, ], #, (2 dead), PgUp       (cols 0-13, 16)
    ///   row 4   slots  84-104   Caps, A-L, ;, ', Enter x3, PgDn         (cols 0-16)
    ///   row 5   slots 105-125   LShift, \, Z-/, RShift x3, End          (cols 0-16)
    ///   row 6   slots 126-146   Ctrl, Fn, Win, Alt, Space, arrows       (cols 0-14)
    ///   row 7   slots 147-167   left/right lightbars + the small arrow keys
    ///   row 8+  slots 168-175   dead
    ///
    /// Wide keys (Backspace, Enter, RShift) occupy three consecutive slots; the up/down arrows
    /// share slots between rows 6 and 7. Columns 17-20 of every row are numpad positions this
    /// chassis does not have, and are dead.
    /// </summary>
    public static class Zenbook16X
    {
        public const int SLOTS = 176;
        public const int STRIDE = 21;
        const int CHUNKS = 11;
        const int PER_CHUNK = 16;

        /// First and last row bands that carry physical keys.
        public const int ROW_TOP = 1;
        public const int ROW_BOTTOM = 6;
        /// Columns 0-16 are reachable; 17-20 are absent numpad positions.
        public const int COLS = 17;

        /// <summary>The lid logo - full RGB, slot 0 of this same buffer (confirmed live).</summary>
        public const int SLOT_LOGO = 0;

        public const int SLOT_LIGHTBAR_LEFT = 147;
        public const int SLOT_LIGHTBAR_RIGHT = 163;

        public static int Slot(int row, int col) => row * STRIDE + col;

        /// <summary>
        /// [5C A2 00 00] - puts the controller into host-streamed per-key mode and resets the
        /// buffer. Must be sent BEFORE the chunk stream.
        ///
        /// This packet appears verbatim in ASUS's own AsusExclusiveAgent.exe (built by
        /// `mov dword [rsp+40h],0A25Ch` into a zeroed 64-byte feature report, call sites
        /// 0x1400239F7 and 0x140027CC9). An earlier pass on this fork mistook it for a trailing
        /// "commit" and sent it after the chunks, which blanked the keyboard; the pass after that
        /// removed it entirely, which left host streaming unable to pre-empt a running firmware
        /// effect. Sent first, it does both jobs: confirmed live by setting hardware Rainbow
        /// (mode 3) and then streaming solid red over the top of it.
        /// </summary>
        public static void DirectEnable()
        {
            AsusHid.SetFeatureAura(new byte[] { AsusHid.ZENBOOK_16X_AURA_ID, 0xA2, 0x00, 0x00 });
            directActive = true;
        }

        static bool directActive;

        /// <summary>
        /// Sends the enable packet only if the controller isn't already in host-streamed mode.
        ///
        /// The enable also *resets* the frame buffer, so sending it ahead of every frame would
        /// blank the keyboard between frames - harmless for a one-shot write, but a visible
        /// flicker for the modes that stream continuously (Audio re-renders every 50ms). Callers
        /// that stream repeatedly should use this; <see cref="InvalidateDirect"/> is called
        /// whenever a hardware effect takes the controller back out of host-streamed mode.
        /// </summary>
        public static void EnsureDirect()
        {
            if (!directActive) DirectEnable();
        }

        /// <summary>Records that the controller is no longer in host-streamed mode.</summary>
        public static void InvalidateDirect() => directActive = false;

        /// <summary>
        /// Streams one frame as 11 chunks of up to 16 LEDs. No trailing packet - the chunks are
        /// the whole transaction.
        /// </summary>
        public static void Stream(Color[] frame)
        {
            for (int chunk = 0; chunk < CHUNKS; chunk++)
            {
                int start = chunk * PER_CHUNK;
                int count = Math.Min(PER_CHUNK, SLOTS - start);

                byte[] pkt = new byte[64];
                pkt[0] = AsusHid.ZENBOOK_16X_AURA_ID;
                pkt[1] = 0xA2;
                pkt[2] = 0x00;
                pkt[3] = 0x01;
                pkt[4] = 0x01;
                pkt[5] = 0x00;
                pkt[6] = (byte)start;
                pkt[7] = (byte)count;
                pkt[8] = 0x00;

                for (int k = 0; k < count; k++)
                {
                    int slot = start + k;
                    Color c = slot < frame.Length ? frame[slot] : Color.Black;
                    pkt[9 + k * 3] = c.R;
                    pkt[9 + k * 3 + 1] = c.G;
                    pkt[9 + k * 3 + 2] = c.B;
                }

                AsusHid.SetFeatureAura(pkt);
            }
        }

        public static Color[] NewFrame() => new Color[SLOTS];

        /// <summary>Additive blend, so overlapping effects brighten rather than overwrite.</summary>
        public static void Blend(Color[] frame, int slot, Color c, double scale = 1.0)
        {
            if (slot < 0 || slot >= frame.Length || scale <= 0) return;
            Color cur = frame[slot];
            frame[slot] = Color.FromArgb(
                Math.Min(255, cur.R + (int)(c.R * scale)),
                Math.Min(255, cur.G + (int)(c.G * scale)),
                Math.Min(255, cur.B + (int)(c.B * scale)));
        }

        public static Color FromHue(double h)
        {
            h = (h % 1.0 + 1.0) % 1.0;
            double r = Math.Abs(h * 6 - 3) - 1;
            double g = 2 - Math.Abs(h * 6 - 2);
            double b = 2 - Math.Abs(h * 6 - 4);
            return Color.FromArgb(
                (int)(Math.Clamp(r, 0, 1) * 255),
                (int)(Math.Clamp(g, 0, 1) * 255),
                (int)(Math.Clamp(b, 0, 1) * 255));
        }
    }

    /// <summary>A software-rendered per-key animation. Render is called once per frame.</summary>
    public abstract class PerKeyEffect
    {
        /// <param name="frame">Zeroed frame buffer to draw into, indexed by LED slot.</param>
        /// <param name="time">Seconds since the effect started.</param>
        /// <param name="dt">Seconds since the previous frame.</param>
        public abstract void Render(Color[] frame, double time, double dt);

        public virtual void Reset() { }
    }

    /// <summary>
    /// Drives a <see cref="PerKeyEffect"/> at a fixed frame rate, streaming each frame to the
    /// keyboard. One shared instance; starting a new effect replaces whatever was running.
    /// </summary>
    public static class PerKeyEngine
    {
        static System.Timers.Timer? timer;
        static PerKeyEffect? effect;
        static DateTime started;
        static DateTime lastFrame;
        static readonly object gate = new();

        public static bool Running => timer != null;

        public static void Start(PerKeyEffect newEffect, int fps = 30)
        {
            lock (gate)
            {
                StopInternal();

                effect = newEffect;
                effect.Reset();
                started = lastFrame = DateTime.UtcNow;

                // Unconditional: whatever the controller was doing, this effect owns it now.
                Zenbook16X.DirectEnable();

                timer = new System.Timers.Timer(1000.0 / fps) { AutoReset = true };
                timer.Elapsed += OnFrame;
                timer.Start();

                Logger.WriteLine($"PerKeyEngine: started {newEffect.GetType().Name} at {fps}fps");
            }
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (timer == null) return;
                StopInternal();
                Logger.WriteLine("PerKeyEngine: stopped");
            }
        }

        static void StopInternal()
        {
            if (timer != null)
            {
                timer.Stop();
                timer.Elapsed -= OnFrame;
                timer.Dispose();
                timer = null;
            }
            effect = null;
        }

        static void OnFrame(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Frames are dropped rather than queued if a previous one is still streaming.
            if (!System.Threading.Monitor.TryEnter(gate)) return;
            try
            {
                if (effect == null) return;

                var now = DateTime.UtcNow;
                double dt = (now - lastFrame).TotalSeconds;
                lastFrame = now;
                if (dt <= 0 || dt > 0.5) dt = 1.0 / 30;

                var frame = Zenbook16X.NewFrame();
                effect.Render(frame, (now - started).TotalSeconds, dt);
                Zenbook16X.Stream(frame);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"PerKeyEngine frame error: {ex.Message}");
            }
            finally
            {
                System.Threading.Monitor.Exit(gate);
            }
        }
    }

    /// <summary>How a hand-painted per-key frame is animated.</summary>
    public enum CustomFrameMode
    {
        Static = 0,
        Breathe = 1,
        Strobe = 2,
        Sweep = 3,
    }

    /// <summary>
    /// Animates a hand-painted frame without changing its colours - only their brightness - so a
    /// custom layout stays recognisable while it moves. The single-colour hardware effects can't
    /// do this: they replace the whole keyboard with one colour.
    /// </summary>
    public class CustomFrameEffect : PerKeyEffect
    {
        readonly Color[] baseFrame;
        readonly CustomFrameMode mode;
        readonly double speed;

        public CustomFrameEffect(Color[] baseFrame, CustomFrameMode mode, double speed)
        {
            this.baseFrame = baseFrame;
            this.mode = mode;
            this.speed = speed;
        }

        public override void Render(Color[] frame, double time, double dt)
        {
            for (int slot = 0; slot < frame.Length && slot < baseFrame.Length; slot++)
            {
                Color c = baseFrame[slot];
                if (c.R + c.G + c.B == 0) continue;

                double level = mode switch
                {
                    // 0.08..1.0 so the dimmest point still shows the design faintly.
                    CustomFrameMode.Breathe => 0.08 + 0.92 * (0.5 + 0.5 * Math.Sin(time * speed * Math.PI)),
                    CustomFrameMode.Strobe => Math.Sin(time * speed * Math.PI * 2) > 0 ? 1.0 : 0.0,
                    CustomFrameMode.Sweep => SweepLevel(slot, time),
                    _ => 1.0,
                };

                frame[slot] = Color.FromArgb(
                    (int)(c.R * level), (int)(c.G * level), (int)(c.B * level));
            }
        }

        /// <summary>A bright band travelling left to right across the key columns.</summary>
        double SweepLevel(int slot, double time)
        {
            int col = slot % Zenbook16X.STRIDE;
            double head = (time * speed * 4) % (Zenbook16X.COLS + 6) - 3;
            double distance = Math.Abs(col - head);
            return 0.15 + 0.85 * Math.Max(0, 1 - distance / 3.5);
        }
    }

    /// <summary>
    /// Multi-coloured rain: drops spawn at the top of random columns, fall with a fading tail,
    /// and splash the nearer lightbar as they run off the bottom.
    /// </summary>
    public class RainEffect : PerKeyEffect
    {
        struct Drop
        {
            public int Col;
            public double Y;
            public double Speed;
            public Color Color;
            public int Length;
            public bool Splashed;
        }

        readonly List<Drop> drops = new();
        readonly Random rng = new();
        readonly Color[] palette;
        readonly double rate;
        readonly double minSpeed, maxSpeed;

        double spawnDebt;

        /// <param name="palette">Colours drops are drawn from.</param>
        /// <param name="rate">Average new drops per second.</param>
        /// <param name="minSpeed">Slowest fall speed, in rows per second.</param>
        /// <param name="maxSpeed">Fastest fall speed, in rows per second.</param>
        public RainEffect(Color[] palette, double rate, double minSpeed, double maxSpeed)
        {
            this.palette = palette.Length > 0 ? palette : new[] { Color.White };
            this.rate = rate;
            this.minSpeed = minSpeed;
            this.maxSpeed = maxSpeed;
        }

        public override void Reset()
        {
            drops.Clear();
            spawnDebt = 0;
        }

        public override void Render(Color[] frame, double time, double dt)
        {
            spawnDebt += rate * dt;
            while (spawnDebt >= 1)
            {
                spawnDebt -= 1;
                drops.Add(new Drop
                {
                    Col = rng.Next(Zenbook16X.COLS),
                    Y = Zenbook16X.ROW_TOP - 1,
                    Speed = minSpeed + rng.NextDouble() * (maxSpeed - minSpeed),
                    Color = palette[rng.Next(palette.Length)],
                    Length = 2 + rng.Next(3),
                });
            }

            for (int i = drops.Count - 1; i >= 0; i--)
            {
                Drop d = drops[i];
                d.Y += d.Speed * dt;

                if (d.Y - d.Length > Zenbook16X.ROW_BOTTOM)
                {
                    drops.RemoveAt(i);
                    continue;
                }

                int head = (int)Math.Round(d.Y);
                for (int t = 0; t <= d.Length; t++)
                {
                    int row = head - t;
                    if (row < Zenbook16X.ROW_TOP || row > Zenbook16X.ROW_BOTTOM) continue;

                    double fade = 1.0 - (double)t / (d.Length + 1);
                    fade *= fade; // steeper falloff reads better over only six rows
                    Zenbook16X.Blend(frame, Zenbook16X.Slot(row, d.Col), d.Color, fade);
                }

                if (d.Y >= Zenbook16X.ROW_BOTTOM)
                {
                    int bar = d.Col < Zenbook16X.COLS / 2
                        ? Zenbook16X.SLOT_LIGHTBAR_LEFT
                        : Zenbook16X.SLOT_LIGHTBAR_RIGHT;
                    Zenbook16X.Blend(frame, bar, d.Color, 0.8);
                    d.Splashed = true;
                }

                drops[i] = d;
            }
        }

        /// <summary>
        /// Builds the drop palette from the user's two Aura colours: five hue stops spanning
        /// Color1 -> Color2. If either colour is too dark or too grey for a hue to be meaningful,
        /// falls back to the full spectrum so the mode still lives up to "multi-coloured".
        /// </summary>
        public static Color[] PaletteFromColors(Color c1, Color c2)
        {
            const int STOPS = 5;

            if (!HasUsableHue(c1) || !HasUsableHue(c2) || Math.Abs(c1.GetHue() - c2.GetHue()) < 1f)
            {
                var spectrum = new Color[STOPS];
                for (int i = 0; i < STOPS; i++) spectrum[i] = Zenbook16X.FromHue((double)i / STOPS);
                return spectrum;
            }

            double h1 = c1.GetHue() / 360.0;
            double h2 = c2.GetHue() / 360.0;

            // Travel the short way around the wheel.
            if (Math.Abs(h2 - h1) > 0.5) h2 += h1 > h2 ? 1.0 : -1.0;

            var palette = new Color[STOPS];
            for (int i = 0; i < STOPS; i++)
                palette[i] = Zenbook16X.FromHue(h1 + (h2 - h1) * i / (STOPS - 1));
            return palette;
        }

        static bool HasUsableHue(Color c) => c.GetSaturation() > 0.15f && c.GetBrightness() > 0.1f;
    }
}
