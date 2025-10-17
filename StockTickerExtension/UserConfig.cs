using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace StockTickerExtension
{
    public class UserConfig
    {
        public StockType LastStockType { get; set; } = StockType.StockA;
        public string LastStockCode { get; set; } = "";   // 上次输入的股票代码
        public int LastShares { get; set; } = 0;         // 上次的持仓数量
        public float LastCostPrices { get; set; } = 0.0f;         // 上次的持仓成本
        public string PeriodType { get; set; } = "Intraday";        
        public bool AutoStopOnClose { get; set; } = false;       // 是否关闭时自动停止监控
        public bool MA5Checked { get; set; } = true;
        public bool MA10Checked { get; set; } = true;
        public bool MA20Checked { get; set; } = true;
        public bool MA30Checked { get; set; } = true;
        public bool MA60Checked { get; set; } = true;
        public List<string> WatchStockList { get; set; } = new List<string>();
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
