using Newtonsoft.Json.Linq;
using ScottPlot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TrackBar;
using System.Text.RegularExpressions;

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

            Init();
        }

        private void StartBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StartMonitoring();
        private void StopBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StopMonitoring();
        private void UpdateProfitBtn_Click(object sender, System.Windows.RoutedEventArgs e) => UpdateProfitDisplay();

        private void CodeTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            var tb = sender as TextBox;
            e.Handled = !e.Text.All(char.IsDigit) || tb.Text.Length >= 6;
        }

        private void CodeTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string text = e.DataObject.GetData(DataFormats.Text) as string;
                if (!text.All(char.IsDigit))
                    e.CancelCommand();
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void StockToolWindowControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AutoStopCheckBox.IsChecked == true)
            {
                StopMonitoring();
            }
        }

        private void Init()
        {
            StartBtn.Click += StartBtn_Click;
            StopBtn.Click += StopBtn_Click;
            UpdateProfitBtn.Click += UpdateProfitBtn_Click;
            CodeTextBox.PreviewTextInput += CodeTextBox_PreviewTextInput;
            DataObject.AddPastingHandler(CodeTextBox, CodeTextBox_Pasting);
            // 当 UserControl 卸载（窗口关闭）时停止监控
            this.Unloaded += StockToolWindowControl_Unloaded;

            WpfPlotPrice.Configuration.ScrollWheelZoom = false;
            WpfPlotPrice.Configuration.LeftClickDragPan = false;

            WpfPlotPrice.Plot.SetAxisLimits(xMin: 0, xMax: _totalMinutes - 1);

            // 关键时间点
            var today = DateTime.Today.ToString("yyyy-MM-dd ");

            var labelTimes = new[] { today + "09:30", today + "10:00", today + "10:30", today + "11:00", today + "11:30", today + "13:30", today + "14:00", today + "14:30", today + "15:00" };
            var ticks = new List<double>();
            var labels = new List<string>();
            foreach (var t in labelTimes)
            {
                int idx = _tradingMinutes.IndexOf(t);  // _tradingMinutes 是交易分钟列表
                if (idx >= 0)
                {
                    ticks.Add(idx);
                    labels.Add(t.Split(' ')[1]);
                }
            }
            // 设置 X 轴刻度
            if (ticks.Count > 0)
                WpfPlotPrice.Plot.XTicks(ticks.ToArray(), labels.ToArray());

            _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, UiTimer_Tick, Dispatcher.CurrentDispatcher);
        }

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

            // ------------------ 检查交易时间 ------------------
            var now = DateTime.Now;
            bool inMorning = now.TimeOfDay >= TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(30)) &&
                             now.TimeOfDay <= TimeSpan.FromHours(11).Add(TimeSpan.FromMinutes(30));
            bool inAfternoon = now.TimeOfDay >= TimeSpan.FromHours(13).Add(TimeSpan.FromMinutes(30)) &&
                               now.TimeOfDay <= TimeSpan.FromHours(15);

//             if (!inMorning && !inAfternoon)
//             {
//                 StatusText.Text = "当前时间不在交易时间内，无法启动监控";
//                 return;
//             }

            StopMonitoring();
            _cts = new CancellationTokenSource();
            _=Task.Run(() => FetchLoopAsync(code, _cts.Token));

            if (!_uiTimer.IsEnabled) _uiTimer.Start();
            StatusText.Text = "";
        }

        private void StopMonitoring()
        {
            _cts?.Cancel();
            _cts = null;
            StatusText.Text = "监控已停止";
            if (_uiTimer.IsEnabled) _uiTimer.Stop();
        }

        private async Task FetchLoopAsync(string code, CancellationToken token)
        {
            var codeWithSuffix = NormalizeCode(code);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await FetchSnapshotAsync(codeWithSuffix);
                    if (snapshot != null)
                    {
                        snapshot.Code = code;
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

                var name = jobj["data"]["name"].ToObject<string>();
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
                if (double.TryParse(jobj["data"]["preClose"]?.ToString(), out double preClose))
                {
                    changePercent = (lastPrice - preClose) / preClose * 100;
                }

                return new StockSnapshot
                {
                    Name = name,
                    CurrentPrice = lastPrice,
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
                if (string.IsNullOrEmpty(StatusText.Text))
                {
                    StatusText.Text = $"正在监控 {snap.Code} {snap.Name}";
                }
                UpdatePriceChart(snap.Prices, snap.AvgPrices, snap.BuyVolumes, snap.SellVolumes);
                ChangePercentText.Text = snap.ChangePercent.HasValue ? $"{snap.ChangePercent.Value:F2}%" : "--%";
                CurrentPrice.Text = snap.CurrentPrice.ToString();
                UpdateProfitDisplay();
            }
        }

        private void UpdatePriceChart(double[] prices, double[] avgPrices, double[] buyVolumes, double[] sellVolumes)
        {
            List<double> safePrices = new List<double>();
            List<double> safeAvgPrices = new List<double>();

            for (int i = 0; i < prices.Length; i++)
            {
                if (!double.IsNaN(prices[i])) safePrices.Add(prices[i]);
                if (!double.IsNaN(avgPrices[i])) safeAvgPrices.Add(avgPrices[i]);
            }

            WpfPlotPrice.Plot.Clear();

            var xs = Enumerable.Range(0, safePrices.Count).Select(i => (double)i).ToArray();

            // 价格曲线
            WpfPlotPrice.Plot.AddScatter(xs, safePrices.ToArray(), color: System.Drawing.Color.FromArgb(31, 119, 180), lineWidth: 2.0f, markerSize: 2.2f);
            WpfPlotPrice.Plot.AddScatter(xs, safeAvgPrices.ToArray(), color: System.Drawing.Color.FromArgb(255, 127, 14), lineWidth: 2.0f, markerSize: 2.2f);

            // 成交量（右Y轴）
            if (buyVolumes != null && sellVolumes != null)
            {
                var safeBuy = buyVolumes.Take(safePrices.Count).ToArray();
                var safeSell = sellVolumes.Take(safePrices.Count).ToArray();

                var barBuy = WpfPlotPrice.Plot.AddBar(safeBuy, xs);
                barBuy.FillColor = System.Drawing.Color.FromArgb(100, 255, 0, 0);
                barBuy.YAxisIndex = 1; // 使用右Y轴

                var barSell = WpfPlotPrice.Plot.AddBar(safeSell, xs);
                barSell.FillColor = System.Drawing.Color.FromArgb(100, 0, 255, 0);
                barSell.YAxisIndex = 1;
            }

            // 设置坐标轴
            var today = DateTime.Today.ToString("yyyy-MM-dd ");
            var labelTimes = new[] { today + "09:30", today + "10:00", today + "10:30", today + "11:00", today + "11:30", today + "13:30", today + "14:00", today + "14:30", today + "15:00" };
            var ticks = new List<double>();
            var labels = new List<string>();
            foreach (var t in labelTimes)
            {
                int idx = _tradingMinutes.IndexOf(t);
                if (idx >= 0)
                {
                    ticks.Add(idx);
                    labels.Add(t.Split(' ')[1]);
                }
            }
            if (ticks.Count > 0)
                WpfPlotPrice.Plot.XTicks(ticks.ToArray(), labels.ToArray());

            // 坐标轴名称
            WpfPlotPrice.Plot.YLabel("价格(元)");
            WpfPlotPrice.Plot.YAxis2.Label("成交量(手)");

            // 设置右轴显示
            WpfPlotPrice.Plot.YAxis2.Ticks(true);
            WpfPlotPrice.Plot.YAxis2.Color(System.Drawing.Color.Gray);

            // 自动缩放
            WpfPlotPrice.Plot.AxisAuto(horizontalMargin: 0, verticalMargin: 0);

            // ------------------ 价格轴（左Y轴）留20%空间 ------------------
            double maxPrice = Math.Max(safePrices.DefaultIfEmpty(0).Max(), safeAvgPrices.DefaultIfEmpty(0).Max());
            double minPrice = Math.Min(safePrices.DefaultIfEmpty(0).Min(), safeAvgPrices.DefaultIfEmpty(0).Min());

            // 上下各留出10%的空间（总共扩大20%）
            double priceRange = maxPrice - minPrice;
            WpfPlotPrice.Plot.SetAxisLimitsY(minPrice - priceRange * 0.1, maxPrice + priceRange * 0.1, yAxisIndex: 0);

            // 调整右侧成交量轴范围
            double maxVolume = Math.Max(buyVolumes?.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() ?? 0,
                                        sellVolumes?.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() ?? 0);
            WpfPlotPrice.Plot.SetAxisLimitsY(0, maxVolume * 1.3, yAxisIndex: 1); // 上限提高20%
    
            WpfPlotPrice.Render();
        }

        private void UpdateProfitDisplay()
        {
            if (!double.TryParse(SharesBox.Text, out double shares)) return;
            if (!double.TryParse(CostBox.Text, out double cost)) return;
            if (!double.TryParse(ChangePercentText.Text.TrimEnd('%'), out double change)) return;

            double currentPrice = double.Parse(CurrentPrice.Text);
            double positionProfit = (currentPrice - cost) * shares;
            double todayProfit = currentPrice * change * shares / 100;

            PositionProfitText.Text = $"持仓盈亏: {positionProfit:F2}";
            TodayProfitText.Text = $"今日盈亏: {todayProfit:F2}";
        }

        private class StockSnapshot
        {
            public string Code { get; set; }
            public string Name { get; set; }
            public double CurrentPrice { get; set; }
            public double[] Prices { get; set; }
            public double[] AvgPrices { get; set; }
            public double[] Volumes { get; set; }
            public double[] BuyVolumes { get; set; }
            public double[] SellVolumes { get; set; }
            public double? ChangePercent { get; set; }
        }
    }
}
