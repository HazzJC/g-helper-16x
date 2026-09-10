using System.Drawing;
using System.Text;

namespace GHelper.USB
{
    /// <summary>A saved per-key layout: the frame plus how it animates.</summary>
    public class Zenbook16XProfile
    {
        public string Name = "Default";
        public Color[] Frame = NewFrame();
        public CustomFrameMode Effect = CustomFrameMode.Static;
        public int Speed = 2;

        public static Color[] NewFrame()
        {
            var frame = new Color[Zenbook16X.SLOTS];
            Array.Fill(frame, Color.Black);
            return frame;
        }

        public double SpeedFactor => Math.Clamp(Speed, 1, 5) * 0.4;

        public Zenbook16XProfile Clone() => new()
        {
            Name = Name,
            Frame = (Color[])Frame.Clone(),
            Effect = Effect,
            Speed = Speed,
        };
    }

    /// <summary>
    /// Named per-key profiles, stored in the app config. Each profile is one key holding the
    /// frame as hex triples plus its effect and speed; a separate index key keeps the order and
    /// which one is active, so the whole set survives a restart.
    /// </summary>
    public static class Zenbook16XProfiles
    {
        const string INDEX_KEY = "zenbook_profiles";
        const string ACTIVE_KEY = "zenbook_profile_active";
        const string PROFILE_PREFIX = "zenbook_profile_";

        // Older builds stored a single unnamed frame under these keys; migrated on first load.
        const string LEGACY_FRAME = "zenbook_custom_frame";
        const string LEGACY_EFFECT = "zenbook_custom_effect";
        const string LEGACY_SPEED = "zenbook_custom_speed";

        public static List<string> Names()
        {
            string index = AppConfig.GetString(INDEX_KEY, "") ?? "";
            var names = index.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (names.Count == 0) names.Add("Default");
            return names;
        }

        static void SaveNames(IEnumerable<string> names)
            => AppConfig.Set(INDEX_KEY, string.Join(',', names));

        public static string ActiveName
        {
            get
            {
                string active = AppConfig.GetString(ACTIVE_KEY, "") ?? "";
                var names = Names();
                return names.Contains(active) ? active : names[0];
            }
            set => AppConfig.Set(ACTIVE_KEY, value);
        }

        /// <summary>Names are used as config keys, so keep them to something safe and short.</summary>
        public static string Sanitize(string name)
        {
            var clean = new string((name ?? "").Trim()
                .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
                .ToArray());
            if (clean.Length > 32) clean = clean.Substring(0, 32);
            return string.IsNullOrWhiteSpace(clean) ? "Profile" : clean;
        }

        public static Zenbook16XProfile Load(string name)
        {
            var profile = new Zenbook16XProfile { Name = name };

            string stored = AppConfig.GetString(PROFILE_PREFIX + name, "") ?? "";
            if (string.IsNullOrEmpty(stored))
            {
                MigrateLegacy(profile);
                return profile;
            }

            // "<effect>|<speed>|<hex triples>"
            var parts = stored.Split('|');
            if (parts.Length == 3)
            {
                if (int.TryParse(parts[0], out int effect)) profile.Effect = (CustomFrameMode)Math.Clamp(effect, 0, 3);
                if (int.TryParse(parts[1], out int speed)) profile.Speed = Math.Clamp(speed, 1, 5);
                ParseFrame(parts[2], profile.Frame);
            }
            else ParseFrame(stored, profile.Frame);

            return profile;
        }

        static void MigrateLegacy(Zenbook16XProfile profile)
        {
            string legacy = AppConfig.GetString(LEGACY_FRAME, "") ?? "";
            if (string.IsNullOrEmpty(legacy)) return;

            ParseFrame(legacy, profile.Frame);
            profile.Effect = (CustomFrameMode)Math.Clamp(AppConfig.Get(LEGACY_EFFECT, 0), 0, 3);
            profile.Speed = Math.Clamp(AppConfig.Get(LEGACY_SPEED, 2), 1, 5);
            Save(profile);
        }

        public static void Save(Zenbook16XProfile profile)
        {
            var hex = new StringBuilder(Zenbook16X.SLOTS * 6);
            for (int i = 0; i < Zenbook16X.SLOTS; i++)
            {
                Color c = i < profile.Frame.Length ? profile.Frame[i] : Color.Black;
                hex.Append($"{c.R:X2}{c.G:X2}{c.B:X2}");
            }

            AppConfig.Set(PROFILE_PREFIX + profile.Name, $"{(int)profile.Effect}|{profile.Speed}|{hex}");

            var names = Names();
            if (!names.Contains(profile.Name))
            {
                names.Add(profile.Name);
                SaveNames(names);
            }
        }

        public static void Delete(string name)
        {
            var names = Names();
            if (names.Count <= 1) return; // never leave the list empty

            names.Remove(name);
            SaveNames(names);
            AppConfig.Remove(PROFILE_PREFIX + name);

            if (ActiveName == name) ActiveName = names[0];
        }

        /// <summary>The profile the Custom (Per-Key) aura mode plays back.</summary>
        public static Zenbook16XProfile Active() => Load(ActiveName);

        static void ParseFrame(string hex, Color[] into)
        {
            if (hex.Length < Zenbook16X.SLOTS * 6) return;
            for (int i = 0; i < Zenbook16X.SLOTS; i++)
            {
                try
                {
                    into[i] = Color.FromArgb(
                        Convert.ToByte(hex.Substring(i * 6, 2), 16),
                        Convert.ToByte(hex.Substring(i * 6 + 2, 2), 16),
                        Convert.ToByte(hex.Substring(i * 6 + 4, 2), 16));
                }
                catch { into[i] = Color.Black; }
            }
        }
    }
}
