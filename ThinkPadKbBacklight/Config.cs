using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ThinkPadKbBacklight
{
    internal sealed class Config
    {
        public int TimeoutSeconds { get; set; } = 30;
        public int OnLevel { get; set; } = 2;
        public int OffLevel { get; set; } = 0;
        public bool Paused { get; set; } = false;
        public bool RestorePreviousLevel { get; set; } = true;

        public static string DefaultPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThinkPadKbBacklight");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
        }

        public static Config LoadOrDefault(string path)
        {
            try
            {
                if (!File.Exists(path)) return new Config();
                string json = File.ReadAllText(path, Encoding.UTF8);
                var c = new JavaScriptSerializer().Deserialize<Config>(json);
                if (c == null) return new Config();
                if (c.TimeoutSeconds < 3) c.TimeoutSeconds = 3;
                if (c.OnLevel < 0) c.OnLevel = 0;
                if (c.OnLevel > 2) c.OnLevel = 2;
                if (c.OffLevel < 0) c.OffLevel = 0;
                if (c.OffLevel > 2) c.OffLevel = 2;
                return c;
            }
            catch
            {
                return new Config();
            }
        }

        public void Save(string path)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                string json = ser.Serialize(this);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
