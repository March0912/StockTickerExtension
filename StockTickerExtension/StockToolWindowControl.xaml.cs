using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using ScottPlot;

namespace StockTickerExtension
{
    public partial class StockToolWindowControl : UserControl
    {
        private CancellationTokenSource _cts;
        private readonly HttpClient _http = new HttpClient();
        private readonly ConcurrentQueue<StockSnapshot> _queue = new ConcurrentQueue<StockSnapshot>();
        private DispatcherTimer _uiTimer;

        private readonly List<string> _tradingMinutes;
        private readonly int _totalMinutes;
        private int _fetchIntervalSeconds = 5;

        public StockToolWindowControl()
        {
            InitializeComponent();

            _tradingMinutes = BuildTradingMinutes();
            _totalMinutes = _tradingMinutes.Count;

            StartBtn.Click += StartBtn_Click;
            StopBtn.Click += StopBtn_Click;
            UpdateProfitBtn.Click += UpdateProfitBtn_Click;

            // 初始化图表标题与标签
            WpfPlotPrice.plt.Title("分时价格");
            WpfPlotPrice.plt.YLabel("价格(元)");
            WpfPlotPrice.plt.XLabel("时间");

            WpfPlotVolume.plt.Title("成交量");
            WpfPlotVolume.plt.YLabel("成交量(手)");

            _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, UiTimer_Tick, Dispatcher.CurrentDispatcher);
        }

        private void StartBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StartMonitoring();
        private void StopBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StopMonitoring();
        private void UpdateProfitBtn_Click(object sender, System.Windows.RoutedEventArgs e) => UpdateProfitDisplay();

        private List<string> BuildTradingMinutes()
        {
            var list = new List<string>();
            var t = DateTime.Today.AddHours(9).AddMinutes(30);
            var end = DateTime.Today.AddHours(11).AddMinutes(30);
            while (t <= end) { list.Add(t.ToString("yyyy-MM-dd HH:mm")); t = t.AddMinutes(1); }

            t = DateTime.Today.AddHours(13);
            end = DateTime.Today.AddHours(15);
            while (t <= end) { list.Add(t.ToString("yyyy-MM-dd HH:mm")); t = t.AddMinutes(1); }

            return list;
        }

        private void StartMonitoring()
        {
            var code = CodeTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                StatusText.Text = "错误：请输入股票代码";
                return;
            }

            StopMonitoring();
            _cts = new CancellationTokenSource();
            Task.Run(() => FetchLoop(code, _cts.Token));

            if (!_uiTimer.IsEnabled) _uiTimer.Start();
            StatusText.Text = $"正在监控 {code} ...";
        }

        private void StopMonitoring()
        {
            _cts?.Cancel();
            _cts = null;
            StatusText.Text = "监控已停止";
            if (_uiTimer.IsEnabled) _uiTimer.Stop();
        }

        private async Task FetchLoop(string code, CancellationToken token)
        {
            var codeWithSuffix = NormalizeCode(code);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await FetchSnapshotAsync(codeWithSuffix);
                    if (snapshot != null)
                    {
                        while (_queue.Count > 0) _queue.TryDequeue(out _);
                        _queue.Enqueue(snapshot);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }

                for (int i = 0; i < _fetchIntervalSeconds * 10; i++)
                {
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(100, token);
                }
            }
        }

        private string NormalizeCode(string code)
        {
            code = code.Trim();
            if (code.EndsWith(".SZ", StringComparison.OrdinalIgnoreCase) || code.EndsWith(".SH", StringComparison.OrdinalIgnoreCase))
                return code.ToUpper();
            return code.StartsWith("6") ? code + ".SH" : code + ".SZ";
        }

        private string GetSecId(string codeWithSuffix)
        {
            if (codeWithSuffix.EndsWith(".SZ")) return "0." + codeWithSuffix.Replace(".SZ", "");
            if (codeWithSuffix.EndsWith(".SH")) return "1." + codeWithSuffix.Replace(".SH", "");
            return null;
        }

        private async Task<StockSnapshot> FetchSnapshotAsync(string codeWithSuffix)
        {
            var secid = GetSecId(codeWithSuffix);
            if (secid == null) return null;

            var url = "https://push2his.eastmoney.com/api/qt/stock/trends2/get";
            var currentDate = DateTime.Now.ToString("yyyyMMdd");
            var parameters = $"?fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays=1&secid={secid}&ut=fa5fd1943c7b386f172d6893dbfba10b&trends={currentDate}";
            var requestUrl = url + parameters;

            using (var req = new HttpRequestMessage(HttpMethod.Get, requestUrl))
            {
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT; .NET)");
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var text = await resp.Content.ReadAsStringAsync();
                var jobj = JObject.Parse(text);
                if (jobj["data"] == null || jobj["data"]["trends"] == null) return null;

                var trends = jobj["data"]["trends"].ToObject<string[]>();
                if (trends == null || trends.Length == 0) return null;

                var prices = Enumerable.Repeat(double.NaN, _totalMinutes).ToArray();
                var avgPrices = Enumerable.Repeat(double.NaN, _totalMinutes).ToArray();
                var vols = new double[_totalMinutes];
                var buy = new double[_totalMinutes];
                var sell = new double[_totalMinutes];

                var parsedRows = new List<(string time, double price, double vol, double avg)>();
                foreach (var line in trends)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 8) continue;
                    var time = parts[0];
                    if (!double.TryParse(parts[2], out double price)) price = double.NaN;
                    if (!double.TryParse(parts[5], out double vol)) vol = 0;
                    if (!double.TryParse(parts[7], out double avg)) avg = double.NaN;
                    parsedRows.Add((time, price, vol, avg));
                }

                for (int i = 0; i < parsedRows.Count; i++)
                {
                    var r = parsedRows[i];
                    int idx = _tradingMinutes.IndexOf(r.time);
                    if (idx < 0 || idx >= _totalMinutes) 
                        continue;
                    prices[idx] = r.price;
                    avgPrices[idx] = r.avg;
                    vols[idx] = r.vol;
                }

                for (int i = 0; i < parsedRows.Count; i++)
                {
                    int idx = _tradingMinutes.IndexOf(parsedRows[i].time);
                    if (idx < 0 || idx >= _totalMinutes) 
                        continue;
                    if (i == 0) 
                    { 
                        buy[idx] = vols[idx] * 0.5; 
                        sell[idx] = vols[idx] * 0.5;
                    }
                    else
                    {
                        double cur = parsedRows[i].price;
                        double prev = parsedRows[i - 1].price;
                        if (double.IsNaN(cur) || double.IsNaN(prev))
                        { 
                            buy[idx] = vols[idx] * 0.5; 
                            sell[idx] = vols[idx] * 0.5; 
                        }
                        else if (cur > prev) 
                        { 
                            buy[idx] = vols[idx]; 
                            sell[idx] = 0;
                        }
                        else if (cur < prev)
                        { 
                            buy[idx] = 0; 
                            sell[idx] = vols[idx]; 
                        }
                        else 
                        { 
                            buy[idx] = vols[idx] * 0.5;
                            sell[idx] = vols[idx] * 0.5;
                        }
                    }
                }

                double lastPrice = parsedRows.LastOrDefault().price;
                double changePercent = double.NaN;
                if (double.TryParse(jobj["data"]["prePrice"]?.ToString(), out double prePrice))
                {
                    changePercent = (lastPrice - prePrice) / prePrice * 100;
                }

                return new StockSnapshot
                {
                    Prices = prices,
                    AvgPrices = avgPrices,
                    Volumes = vols,
                    BuyVolumes = buy,
                    SellVolumes = sell,
                    ChangePercent = changePercent
                };
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (_queue.TryDequeue(out var snap))
            {
                UpdatePriceChart(snap.Prices, snap.AvgPrices);
                UpdateVolumeChart(snap.BuyVolumes, snap.SellVolumes);
                ChangePercentText.Text = snap.ChangePercent.HasValue ? $"{snap.ChangePercent.Value:F2}%" : "--%";
            }
        }

        private void UpdatePriceChart(double[] prices, double[] avgPrices)
        {
            // 用上一个非NaN值填充 NaN，保证绘图不崩溃
            double lastPrice = 0;
            double lastAvg = 0;

            var safePrices = new double[prices.Length];
            var safeAvgPrices = new double[avgPrices.Length];

            for (int i = 0; i < prices.Length; i++)
            {
                safePrices[i] = double.IsNaN(prices[i]) ? lastPrice : prices[i];
                if (!double.IsNaN(safePrices[i])) lastPrice = safePrices[i];

                safeAvgPrices[i] = double.IsNaN(avgPrices[i]) ? lastAvg : avgPrices[i];
                if (!double.IsNaN(safeAvgPrices[i])) lastAvg = safeAvgPrices[i];
            }

            WpfPlotPrice.Plot.Clear();

            var xs = Enumerable.Range(0, safePrices.Length).Select(i => (double)i).ToArray();
            WpfPlotPrice.Plot.AddScatter(xs, safePrices, color: System.Drawing.Color.FromArgb(31, 119, 180), lineWidth: 1.5f);
            WpfPlotPrice.Plot.AddScatter(xs, safeAvgPrices, color: System.Drawing.Color.FromArgb(255, 127, 14), lineWidth: 1.5f);
            WpfPlotPrice.Render();
        }

        private void UpdateVolumeChart(double[] buy, double[] sell)
        {
            if (buy is null || sell is null) return;

            WpfPlotVolume.Plot.Clear();
            var xs = Enumerable.Range(0, buy.Length).Select(i => (double)i).ToArray();
            WpfPlotVolume.Plot.AddBar(xs, buy, color: System.Drawing.Color.FromArgb(228, 82, 82));
            WpfPlotVolume.Plot.AddBar(xs, sell, color: System.Drawing.Color.FromArgb(57, 176, 87));
            WpfPlotVolume.Render();
        }

        private void UpdateProfitDisplay()
        {
            if (!double.TryParse(SharesBox.Text, out double shares)) return;
            if (!double.TryParse(CostBox.Text, out double cost)) return;
            if (!double.TryParse(ChangePercentText.Text.TrimEnd('%'), out double change)) return;

            double currentPrice = cost * (1 + change / 100);
            double positionProfit = (currentPrice - cost) * shares;
            double todayProfit = positionProfit * 0.5; // 简单示例

            PositionProfitText.Text = $"持仓盈亏: {positionProfit:F2}";
            TodayProfitText.Text = $"今日盈亏: {todayProfit:F2}";
        }

        private class StockSnapshot
        {
            public double[] Prices { get; set; }
            public double[] AvgPrices { get; set; }
            public double[] Volumes { get; set; }
            public double[] BuyVolumes { get; set; }
            public double[] SellVolumes { get; set; }
            public double? ChangePercent { get; set; }
        }
    }
}
