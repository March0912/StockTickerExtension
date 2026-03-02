using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace StockTickerExtension
{
    public class CostData
    {
        public string Stock { get; set; }
        public int Shares { get; set; } = 0;
        public float CostPrice { get; set; } = 0.0f;
    };

    public class UserConfig
    {
        
        public string CurrentStock { get; set; }
        public bool MA5Checked { get; set; } = true;
        public bool MA10Checked { get; set; } = true;
        public bool MA20Checked { get; set; } = true;
        public bool MA30Checked { get; set; } = true;
        public bool MA60Checked { get; set; } = true;
        public List<string> WatchStockList { get; set; } = new List<string>();
        public List<CostData> CostList { get; set; } = new List<CostData>();
    }

    public class ConfigManager
    {
        private readonly string _configPath;
        private UserConfig _config;

        public ConfigManager()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"StockWatcher");

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _configPath = Path.Combine(dir, "config.json");
        }

        public bool Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    _config = JsonConvert.DeserializeObject<UserConfig>(json) ?? new UserConfig();
                    return true;
                }
                else
                {
                    _config = new UserConfig();
                    return true;
                }
            }
            catch (Exception ex)
            {
                /* 可加日志 */
                return false;
            }
            return false;
        }

        public void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            { 
                /* 可加日志 */
                var s = ex.Message;
                Logger.Error(s);
            }
        }

        public UserConfig Config => _config;
    }
}
