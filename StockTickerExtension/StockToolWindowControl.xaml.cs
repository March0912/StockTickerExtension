using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace StockTickerExtension
{
    public partial class StockSnapshot
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public double CurrentPrice { get; set; }
        /// <summary>
        /// 开盘价
        /// </summary>
        public double[] OpenPrice { get; set; }
        /// <summary>
        /// 收盘价/实时价
        /// </summary>
        public double[] Prices { get; set; }
        /// <summary>
        /// 均线价
        /// </summary>
        public double[] AvgPrices { get; set; }
        /// <summary>
        /// 最高价
        /// </summary>
        public double[] HighPrices { get; set; }
        /// <summary>
        /// 最低价
        /// </summary>
        public double[] LowPrices { get; set; }
        /// <summary>
        /// 总成交量
        /// </summary>
        public double[] Volumes { get; set; }
        /// <summary>
        /// 买入成交量
        /// </summary>
        public double[] BuyVolumes { get; set; }
        /// <summary>
        /// 卖出成交量
        /// </summary>
        public double[] SellVolumes { get; set; }
        /// <summary>
        /// 涨跌幅
        /// </summary>
        public double? ChangePercent { get; set; }

		// 预计算的均线（若可用，则用于绘图，确保从 x=0 开始）
		public double[] MA5 { get; set; }
		public double[] MA10 { get; set; }
		public double[] MA20 { get; set; }
		public double[] MA30 { get; set; }
		public double[] MA60 { get; set; }
    };

    public enum ChartType
    {
        Intraday = 0,
        DailyK,
        WeeklyK,
        MonthlyK,
        QuarterlyK,
        YearlyK,
    };

    public partial class StockToolWindowControl : UserControl
    {
        private readonly StockToolWindow _ownerPane;
        private CancellationTokenSource _cts;
        private readonly HttpClient _http = new HttpClient();
        private readonly ConcurrentQueue<StockSnapshot> _queue = new ConcurrentQueue<StockSnapshot>();
        private DispatcherTimer _uiTimer;

        private List<string> _tradingMinutes;
        private int _totalMinutes;
        private int _fetchIntervalSeconds = 5;
        private bool _monitoring = false;
        private bool _monitorOnce = false;
        private DateTime _currentDate;
        private CancellationTokenSource _kdjCts;

        public StockToolWindowControl(ToolWindowPane owner)
        {
            InitializeComponent();

            _ownerPane = owner as StockToolWindow;
            _currentDate = GetCurrentDate();
            _tradingMinutes = BuildTradingMinutes(_currentDate);
            _totalMinutes = _tradingMinutes.Count;

            Init();
        }

        public bool IsAutoStopWhenClosed()
        {
            return AutoStopCheckBox.IsChecked == true;
        }

        private void StartBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StartMonitoring();

        private void StopBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StopMonitoring();

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

        private void On_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AutoStopCheckBox.IsChecked == true)
            {
                StopMonitoring();
                _ownerPane.ClearStatusInfo();
            }
        }

        private void PeriodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PeriodComboBox.SelectedItem is ComboBoxItem item)
            {
                if (item.Content.ToString() == "Intraday")
                {
                    DatePickerControl.IsEnabled = false;
                    PeriodComboBox.SelectedItem = DateTime.Today;
                    MA5.IsEnabled = false;
                    MA10.IsEnabled = false;
                    MA20.IsEnabled = false;
                    MA30.IsEnabled = false;
                    MA60.IsEnabled = false;

                    WpfPlotPrice.Plot.Clear();
                    WpfPlotPrice.Configuration.ScrollWheelZoom = false;
                    WpfPlotPrice.Configuration.LeftClickDragPan = false;
                    WpfPlotVolume.Configuration.ScrollWheelZoom = false;
                    WpfPlotVolume.Configuration.LeftClickDragPan = false;
                    WpfPlotPrice.Render();

                    WpfPlotVolume.Visibility = Visibility.Hidden;
                    WpfPlotVolume.Render();
                }
                else
                {
                    DatePickerControl.IsEnabled = true;
                    MA5.IsEnabled = true;
                    MA10.IsEnabled = true;
                    MA20.IsEnabled = true;
                    MA30.IsEnabled = true;
                    MA60.IsEnabled = true;

                    WpfPlotPrice.Configuration.ScrollWheelZoom = true;
                    WpfPlotPrice.Configuration.LeftClickDragPan = true;
                    WpfPlotPrice.Render();

                    WpfPlotVolume.Configuration.ScrollWheelZoom = true;
                    WpfPlotVolume.Configuration.LeftClickDragPan = true;
                    WpfPlotVolume.Visibility = Visibility.Visible;
                    WpfPlotVolume.Render();
                }
            }

            StartBtn_Click(null, null);
        }

        private void Date_SelecteionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentDate = GetCurrentDate();
            _tradingMinutes = BuildTradingMinutes(_currentDate);
            _totalMinutes = _tradingMinutes.Count;
        }

        private void Init()
        {
            StartBtn.Click += StartBtn_Click;
            StartBtn.Content = !IsTradingTime(DateTime.Now) ? "Get" : "Start";
            StopBtn.Click += StopBtn_Click;
            StopBtn.IsEnabled = false;

            MA5.IsEnabled = false;
            MA10.IsEnabled = false;
            MA20.IsEnabled = false;
            MA30.IsEnabled = false;
            MA60.IsEnabled = false;

            CodeTextBox.PreviewTextInput += CodeTextBox_PreviewTextInput;
            DataObject.AddPastingHandler(CodeTextBox, CodeTextBox_Pasting);
            // 当 UserControl 卸载（窗口关闭）时停止监控
            this.Unloaded += On_Unloaded;
            DatePickerControl.SelectedDateChanged += Date_SelecteionChanged;

            CurrentPrice.FontWeight = FontWeights.Bold;
            CurrentPrice.Foreground = Brushes.Blue;

            InitPeriodComboBox();
            InitPriceChat();

            _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, UiTimer_Tick, Dispatcher.CurrentDispatcher);
        }

        private void InitPeriodComboBox()
        {
            PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Intraday" });
            if (true)
            {
                PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Daily K" });
                PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Weekly K" });
                PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Monthly K" });
                PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Quarterly K" });
                PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Yearly K" });
            }
            PeriodComboBox.SelectionChanged += PeriodComboBox_SelectionChanged;
        }

        private void InitPriceChat()
        {
            WpfPlotPrice.Configuration.ScrollWheelZoom = false;
            WpfPlotPrice.Configuration.LeftClickDragPan = false;
            WpfPlotPrice.Plot.SetAxisLimits(xMin: 0, xMax: _totalMinutes - 1);

            WpfPlotVolume.Configuration.ScrollWheelZoom = false;
            WpfPlotVolume.Configuration.LeftClickDragPan = false;

            // 关键时间点
            var dateStr = _currentDate.ToString("yyyy-MM-dd ");

            var labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
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
            // 设置 X 轴刻度
            if (ticks.Count > 0)
                WpfPlotPrice.Plot.XTicks(ticks.ToArray(), labels.ToArray());

            WpfPlotVolume.Visibility = Visibility.Hidden;
        }

        private List<string> BuildTradingMinutes(DateTime date)
        {
            var list = new List<string>();

            var t = date.AddHours(9).AddMinutes(30);
            var end = date.AddHours(11).AddMinutes(30);
            while (t <= end) { list.Add(t.ToString("yyyy-MM-dd HH:mm")); t = t.AddMinutes(1); }

            t = date.AddHours(13);
            end = date.AddHours(15);
            while (t <= end) { list.Add(t.ToString("yyyy-MM-dd HH:mm")); t = t.AddMinutes(1); }

            return list;
        }

        private void UpdateVSStatus(double price, double changePercent, double positionProfit, double todayProfit)
        {
            // Dispatcher 保证在 UI 线程安全执行
            Dispatcher.Invoke(() =>
            {
                // 获取当前的父窗口（ToolWindowPane）
                if (_ownerPane != null)
                {
                    _ownerPane.UpdateStatusInfo(price, changePercent, positionProfit, todayProfit);
                }
            });
        }

        private void UpdateVSStatus(string text)
        {
            // Dispatcher 保证在 UI 线程安全执行
            Dispatcher.Invoke(() =>
            {
                // 获取当前的父窗口（ToolWindowPane）
                if (_ownerPane != null)
                {
                    _ownerPane.UpdateStatusInfo(text);
                }
            });
        }


        bool IsWeekend(DateTime dt) => dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday;

        bool IsTradingTime(DateTime dt)
        {
            if (IsWeekend(dt))
                return false;

            TimeSpan morningStart = new TimeSpan(9, 30, 0);
            TimeSpan morningEnd = new TimeSpan(11, 30, 0);
            TimeSpan afternoonStart = new TimeSpan(13, 0, 0);
            TimeSpan afternoonEnd = new TimeSpan(15, 0, 0);

            TimeSpan nowTime = dt.TimeOfDay;
            return (nowTime >= morningStart && nowTime <= morningEnd) ||
                   (nowTime >= afternoonStart && nowTime <= afternoonEnd);
        }

        bool CheckTradingTime()
        {
            var code = CodeTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                UpdateStatus("Error: Please enter a stock code", Brushes.Red);
                return false;
            }

            // ------------------ 检查交易时间 ------------------
            if (!IsTradingTime(DateTime.Now))
            {
                // 收盘后（15:00之后）允许启动，并显示当日完整分时数据
                UpdateStatus("Currently outside trading hours", Brushes.Red); 
                return false;
            }
            return true;
        }

        private void StartMonitoring()
        {
            string period;
            if (PeriodComboBox.SelectedItem is ComboBoxItem item)
            {
                period = item.Content.ToString();
            }
            else
                period = PeriodComboBox.Text;

            if (!CheckTradingTime())
            {
                if(period == "Intraday" && DateTime.Now.TimeOfDay < new TimeSpan(9, 30, 0))
                {
                    return;
                }
                // 如果不在交易时间，则不启动监控，只获取一次数据
                _monitorOnce = true;
            }

            StopMonitoring();
            _monitoring = true;

            var code = CodeTextBox.Text?.Trim();
            _cts = new CancellationTokenSource();
            _ =Task.Run(() => MonitorLoopAsync(code, period, _cts.Token));

            // ✅ 如果是分时图，则同时启动金叉监控线程
            if (period == "Intraday")
            {
                _kdjCts = new CancellationTokenSource();
                if (!_monitorOnce)
                {
                    _ = Task.Run(() => MonitorKDJAsync(code, _kdjCts.Token));
                }
            }

            if (!_uiTimer.IsEnabled) _uiTimer.Start();
            UpdateStatus("", Brushes.Blue);
           
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
        }

        private void StopMonitoring()
        {
            if (!_monitoring)
            {
                return;
            }

            StopBtn.IsEnabled = false;
            StartBtn.IsEnabled = true;
            _monitoring = false;

            _cts?.Cancel();
            _cts = null;

            _kdjCts?.Cancel();
            _kdjCts = null;

            UpdateStatus("Conitoring stopped", Brushes.Blue);
            if (_uiTimer.IsEnabled) _uiTimer.Stop();
        }

        private string PeriodToKType(string period)
        {
            string kType;
            switch (period)
            {
                case "Daily K":
                    kType = "101";
                    break;
                case "Weekly K":
                    kType = "102";
                    break;
                case "Monthly K":
                    kType = "103";
                    break;
                case "Quarterly K":
                    kType = "104";
                    break;
                case "Yearly K":
                    kType = "105";
                    break;
                default:
                    kType = "Intraday";
                    break;
            }
            return kType;
        }

        private async Task MonitorLoopAsync(string code, string period, CancellationToken token)
        {
            if (!_monitoring)
                return;

            var codeWithSuffix = NormalizeCode(code);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snap = await FetchKSnapshotAsync(codeWithSuffix, period);
                    if (snap != null)
                    {
                        snap.Code = code;
                        while (_queue.Count > 0) _queue.TryDequeue(out _);
                        _queue.Enqueue(snap);
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

		private async Task<StockSnapshot> FetchKSnapshotAsync(string codeWithSuffix, string period)
        {
            if(period == "Intraday")
               return await FetchSnapshotAsync(codeWithSuffix);

            var secid = GetSecId(codeWithSuffix);
            if (secid == null) return null;

            var kType = PeriodToKType(period);

			string begStr;
			if(period == "Daily K")
				begStr = _currentDate.AddDays(-240).ToString("yyyyMMdd"); // 多取 40 天以支持 MA 引导
            else if (period == "Weekly K")
                begStr = _currentDate.AddDays(-240*7).ToString("yyyyMMdd");
            else if (period == "Monthly K")
                begStr = _currentDate.AddMonths(-240).ToString("yyyyMMdd");
            else if (period == "Quarterly K")
                begStr = _currentDate.AddMonths(-240*4).ToString("yyyyMMdd");
            else if (period == "Yearly K")
                begStr = _currentDate.AddYears(-10).ToString("yyyyMMdd");
            else
                begStr = _currentDate.AddDays(-240).ToString("yyyyMMdd");

            string dateStr = _currentDate.ToString("yyyyMMdd");
            string url = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&klt={kType}&fqt=1&beg={begStr}&end={dateStr}&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55,f56,f57,f58";

            string text;
            using (var resp = await _http.GetAsync(url))
            {
                if (!resp.IsSuccessStatusCode) return null;

                text = await resp.Content.ReadAsStringAsync();
            }
            var jobj = JObject.Parse(text);
            if (jobj["data"] == null || jobj["data"]["klines"] == null) return null;

            var klines = jobj["data"]["klines"].ToObject<string[]>();
            if (klines.Length == 0) return null;

			var name = jobj["data"]["name"]?.ToString();
			int count = klines.Length;
			var prices = new double[count];
            var avgPrices = new double[count];
            var vols = new double[count];
            var highs = new double[count];
            var lows = new double[count];
            var openPrice = new double[count];

			for (int i = 0; i < count; i++)
            {
                var parts = klines[i].Split(',');
                double open = double.Parse(parts[1]);
                double close = double.Parse(parts[2]);
                double high = double.Parse(parts[3]);
                double low = double.Parse(parts[4]);
                double vol = double.Parse(parts[5]);

                openPrice[i] = open;
                prices[i] = close;
                highs[i] = high;
                lows[i] = low;
                avgPrices[i] = (open + close + high + low) / 4.0;
                vols[i] = vol;
            }

			double lastPrice = prices.Last();
			double changePercent = (count >= 2) ? (lastPrice - prices[count - 2]) / prices[count - 2] * 100 : 0;

			// 计算严格窗口的全序列均线
			double[] ma5full = ComputeExactWindowSMA(prices, 5);
			double[] ma10full = ComputeExactWindowSMA(prices, 10);
			double[] ma20full = ComputeExactWindowSMA(prices, 20);
			double[] ma30full = ComputeExactWindowSMA(prices, 30);
			double[] ma60full = ComputeExactWindowSMA(prices, 60);

            return new StockSnapshot
            {
                Code = codeWithSuffix,
                Name = name,
                OpenPrice = openPrice,
                Prices = prices,
                HighPrices = highs,
                LowPrices = lows,
                AvgPrices = avgPrices,
                Volumes = vols,
                BuyVolumes = vols.Select(v => v * 0.5).ToArray(),
                SellVolumes = vols.Select(v => v * 0.5).ToArray(),
                CurrentPrice = lastPrice,
				ChangePercent = changePercent,
				MA5 = ma5full,
				MA10 = ma10full,
				MA20 = ma20full,
				MA30 = ma30full,
				MA60 = ma60full
            };
        }

        private async Task<StockSnapshot> FetchSnapshotAsync(string codeWithSuffix)
        {
            var secid = GetSecId(codeWithSuffix);
            if (secid == null) return null;

            var url = "https://push2his.eastmoney.com/api/qt/stock/trends2/get";
            var dateStr = _currentDate.ToString("yyyyMMdd");
            var parameters = $"?fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays=1&secid={secid}&ut=fa5fd1943c7b386f172d6893dbfba10b&trends={dateStr}";
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

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (!_monitoring)
                return;

            if (_queue.TryDequeue(out var snap))
            {
                if (StockName.Text != snap.Name)
                {
                    StockName.Text = snap.Name;
                }

                if (string.IsNullOrEmpty(StatusText.Text))
                {
                    UpdateStatus($"Monitoring {snap.Code} {snap.Name}", Brushes.Blue);
                }

                UpdatePriceChart(snap);

                var val = snap.ChangePercent.Value;
                ChangePercentText.Text = snap.ChangePercent.HasValue ? $"{val:F2}%" : "--%";
                ChangePercentText.Foreground = val > 0 ? Brushes.Red : Brushes.Green;
                CurrentPrice.Text = snap.CurrentPrice.ToString();

                UpdateProfitDisplay();

                if (GetChatType() != ChartType.Intraday)
                {
                    CheckKdjGoldenCross(snap);
                }
                if(_monitorOnce)
                {
                    StopBtn_Click(null, null);
                    _monitorOnce = false;
                }
            }
        }

        private void UpdatePriceChart(StockSnapshot snap)
        {
            if (!_monitoring)
                return;

            if(GetChatType() == ChartType.Intraday)
            {
                DrawIntradayChart(snap);
            }
            else
            {
                DrawKLineChart(snap);
            }
        }

        private void DrawIntradayChart(StockSnapshot snap)
        {
            if (!_monitoring || snap.Prices == null || snap.Prices.Length == 0)
                return;
            WpfPlotPrice.Plot.Clear();

            List<double> safePrices = new List<double>();
            List<double> safeAvgPrices = new List<double>();

            for (int i = 0; i < snap.Prices.Length; i++)
            {
                if (!double.IsNaN(snap.Prices[i])) safePrices.Add(snap.Prices[i]);
                if (!double.IsNaN(snap.AvgPrices[i])) safeAvgPrices.Add(snap.AvgPrices[i]);
            }
            if (safePrices.Count == 0)
                return;

            // 固定x轴范围为完整的交易时间范围，而不是根据数据点数量动态调整
            WpfPlotPrice.Plot.SetAxisLimits(xMin: 0, xMax: _totalMinutes - 1);

            // 使用完整的交易时间索引，而不是只使用有效数据点的索引
            var xs = Enumerable.Range(0, _totalMinutes).Select(i => (double)i).ToArray();

            // 创建完整的价格数组，包含NaN值用于没有数据的时间点
            var fullPrices = new double[_totalMinutes];
            var fullAvgPrices = new double[_totalMinutes];
            
            // 将有效价格数据填充到对应的时间索引位置
            for (int i = 0; i < snap.Prices.Length && i < _totalMinutes; i++)
            {
                fullPrices[i] = snap.Prices[i];
                fullAvgPrices[i] = snap.AvgPrices[i];
            }

            // 价格曲线 - 只绘制有效的数据点
            var validPriceIndices = new List<double>();
            var validPrices = new List<double>();
            var validAvgPrices = new List<double>();
            
            for (int i = 0; i < _totalMinutes; i++)
            {
                if (!double.IsNaN(fullPrices[i]))
                {
                    validPriceIndices.Add(i);
                    validPrices.Add(fullPrices[i]);
                    validAvgPrices.Add(double.IsNaN(fullAvgPrices[i]) ? fullPrices[i] : fullAvgPrices[i]);
                }
            }
            
            if (validPrices.Count > 0)
            {
                WpfPlotPrice.Plot.AddScatter(validPriceIndices.ToArray(), validPrices.ToArray(), color: System.Drawing.Color.FromArgb(31, 119, 180), lineWidth: 2.0f, markerSize: 2.2f);
                WpfPlotPrice.Plot.AddScatter(validPriceIndices.ToArray(), validAvgPrices.ToArray(), color: System.Drawing.Color.FromArgb(255, 127, 14), lineWidth: 2.0f, markerSize: 2.2f);
            }

            // 创建完整的成交量数组，初始化为0
            var fullBuyVolumes = new double[_totalMinutes];
            var fullSellVolumes = new double[_totalMinutes];

            // 成交量（右Y轴）
            if (snap.BuyVolumes != null && snap.SellVolumes != null)
            {
                // 将有效成交量数据填充到对应的时间索引位置
                for (int i = 0; i < snap.BuyVolumes.Length && i < _totalMinutes; i++)
                {
                    // 确保不包含NaN值，将NaN替换为0
                    fullBuyVolumes[i] = double.IsNaN(snap.BuyVolumes[i]) ? 0 : snap.BuyVolumes[i];
                    fullSellVolumes[i] = double.IsNaN(snap.SellVolumes[i]) ? 0 : snap.SellVolumes[i];
                }

                var barBuy = WpfPlotPrice.Plot.AddBar(fullBuyVolumes, xs);
                barBuy.FillColor = System.Drawing.Color.FromArgb(100, 255, 0, 0);
                barBuy.YAxisIndex = 1; // 使用右Y轴
                barBuy.BarWidth = 0.5; // 设置固定柱状图宽度
                barBuy.BorderLineWidth = 0; // 去掉边框

                var barSell = WpfPlotPrice.Plot.AddBar(fullSellVolumes, xs);
                barSell.FillColor = System.Drawing.Color.FromArgb(150, 0, 255, 0);
                barSell.YAxisIndex = 1;
                barSell.BarWidth = 0.5; // 设置固定柱状图宽度
                barSell.BorderLineWidth = 0; // 去掉边框
            }

            // 设置坐标轴
            var dateStr = _currentDate.ToString("yyyy-MM-dd ");
            var labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
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
            WpfPlotPrice.Plot.YLabel("Price(RMB)");
            WpfPlotPrice.Plot.YAxis2.Label("Volume (Lots)");

            // 设置右轴显示
            WpfPlotPrice.Plot.YAxis2.Ticks(true);
            WpfPlotPrice.Plot.YAxis2.Color(System.Drawing.Color.Gray);

            // 自动缩放
            WpfPlotPrice.Plot.AxisAuto(horizontalMargin: 0, verticalMargin: 0);

            // ------------------ 价格轴（左Y轴）留20%空间 ------------------
            double maxPrice = 0, minPrice = 0;
            if (validPrices.Count > 0)
            {
                maxPrice = Math.Max(validPrices.Max(), validAvgPrices.Max());
                minPrice = Math.Min(validPrices.Min(), validAvgPrices.Min());
            }

            // 上下各留出10%的空间（总共扩大20%）
            double priceRange = maxPrice - minPrice;
            WpfPlotPrice.Plot.SetAxisLimitsY(minPrice - priceRange * 0.1, maxPrice + priceRange * 0.1, yAxisIndex: 0);

            // 调整右侧成交量轴范围
            double maxVolume = Math.Max(fullBuyVolumes.DefaultIfEmpty(0).Max(),
                                        fullSellVolumes.DefaultIfEmpty(0).Max());
            WpfPlotPrice.Plot.SetAxisLimitsY(0, maxVolume * 1.3, yAxisIndex: 1); // 上限提高20%

            WpfPlotPrice.Render();
        }

        private void DrawKLineChart(StockSnapshot snap)
        {
            if (!_monitoring || snap.Prices == null || snap.Prices.Length == 0)
                return;

            // 清理两个图
            WpfPlotPrice.Plot.Clear();
            WpfPlotPrice.Plot.YAxis2.Ticks(false);
            WpfPlotPrice.Plot.YAxis2.Label("");
            WpfPlotVolume.Plot.Clear();

            // 确保成交量区可见
            WpfPlotVolume.Visibility = Visibility.Visible;

            int count = snap.Prices.Length;
            var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();

			// --- 1) 绘制 K 线（使用 ScottPlot 的 Candlesticks） ---
            var opens = snap.OpenPrice ?? Enumerable.Repeat(double.NaN, count).ToArray();
            var closes = snap.Prices;
            var highs = snap.HighPrices ?? Enumerable.Repeat(double.NaN, count).ToArray();
            var lows = snap.LowPrices ?? Enumerable.Repeat(double.NaN, count).ToArray();

            // 添加蜡烛图（若你的 ScottPlot 版本支持）
            try
            {
                var ohlcs = new List<ScottPlot.OHLC>();
                for (int i = 0; i < count; i++)
                {
                    if (!double.IsNaN(opens[i]) && !double.IsNaN(highs[i]) &&
                        !double.IsNaN(lows[i]) && !double.IsNaN(closes[i]))
                    {
                        ohlcs.Add(new ScottPlot.OHLC(opens[i], highs[i], lows[i], closes[i], xs[i], 1));
                    }
                }

                // AddCandlesticks(opens, highs, lows, closes, xs)
                var candles = WpfPlotPrice.Plot.AddCandlesticks(ohlcs.ToArray());
                // 美化：涨红 跌绿，和宽度
                candles.ColorUp = System.Drawing.Color.Red;
                candles.ColorDown = System.Drawing.Color.Green;
//              candles.CandleWidth = 0.6f; // 0..1 相对宽度
            }
            catch
            {
                // 退回到手动绘制（以防 AddCandlesticks 不可用）
                // 用矩形/线绘制每根蜡烛（保证实体从 open 到 close，而不是从 0 开始）
                for (int i = 0; i < count; i++)
                {
                    double x = xs[i];
                    double open = opens[i];
                    double close = closes[i];
                    double high = highs[i];
                    double low = lows[i];

                    if (double.IsNaN(open) || double.IsNaN(close) || double.IsNaN(high) || double.IsNaN(low))
                        continue;

                    var color = close >= open ? System.Drawing.Color.Red : System.Drawing.Color.Green;

                    // 影线（上下）
                    WpfPlotPrice.Plot.AddLine(x, x, close > open ? close : open, low, color, lineWidth: 0.6f);
                    WpfPlotPrice.Plot.AddLine(x, x, close > open ? close : open, high, color, lineWidth: 0.6f);

                    // 实体：用 AddRectangle（x - w/2, min(open,close), width, height）
                    double w = 0.6; // candle width in x-units
                    double left = x - w / 2.0;
                    double bottom = Math.Min(open, close);
                    double height = Math.Abs(close - open);
                    // Use AddRectangle - ScottPlot has AddRectangle in recent versions
                    try
                    {
                        var rect = WpfPlotPrice.Plot.AddRectangle(left, bottom, w, height);
                        rect.Color = color;
                        rect.BorderColor = color;
                    }
                    catch
                    {
                        // 如果没有 AddRectangle 可用，退回不绘制实体（影线至少能看）
                    }
                }
            }

            // X 轴对齐：使每个 candle 在整数位置（0..count-1）居中
            double xMin = -0.5;
            double xMax = count - 0.5;
            WpfPlotPrice.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);

            // 设置 X 轴刻度（按需要，只画部分刻度，避免拥挤）
            var ticks = new List<double>();
            var labels = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (i % Math.Max(1, count / 8) == 0) // 取约 8 个刻度
                {
                    ticks.Add(i);
                    labels.Add(i.ToString());
                }
            }
            if (ticks.Count > 0)
                WpfPlotPrice.Plot.XTicks(ticks.ToArray(), labels.ToArray());

			WpfPlotPrice.Plot.YLabel("Price(RMB)");

			// --- 1.1) 计算并叠加 MA5 / MA10 / MA20 ---
			// 优先使用预计算的严格窗口均线；若为空则退回本地计算
			var ma5 = snap.MA5 ?? ComputeSimpleMovingAverage(closes, 5);
            // 过滤 NaN，仅绘制有效点，避免 ScottPlot 因 NaN 抛异常
            if (MA5.IsChecked == true)
            {
				var xList = new List<double>();
				var yList = new List<double>();
				int n5 = Math.Min(xs.Length, ma5.Length);
				int firstIdx = -1;
				double firstVal = double.NaN;
				for (int i = 0; i < n5; i++)
				{
					if (!double.IsNaN(ma5[i])) { firstIdx = i; firstVal = ma5[i]; break; }
				}
				if (firstIdx >= 0)
				{
					for (int i = 0; i < firstIdx; i++) { xList.Add(xs[i]); yList.Add(firstVal); }
					for (int i = firstIdx; i < n5; i++) { if (!double.IsNaN(ma5[i])) { xList.Add(xs[i]); yList.Add(ma5[i]); } }
				}
				var xv = xList.ToArray(); var yv = yList.ToArray();
				if (yv.Length > 1) WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Black, lineWidth: 1.0f, markerSize: 0f, label: "MA5");
			}

            var ma10 = snap.MA10 ?? ComputeSimpleMovingAverage(closes, 10);
            if (MA10.IsChecked == true)
            {
                var xList = new List<double>();
				var yList = new List<double>();
				int n10 = Math.Min(xs.Length, ma10.Length);
				int firstIdx = -1;
				double firstVal = double.NaN;
				for (int i = 0; i < n10; i++)
				{
					if (!double.IsNaN(ma10[i])) { firstIdx = i; firstVal = ma10[i]; break; }
				}
				if (firstIdx >= 0)
				{
					for (int i = 0; i < firstIdx; i++) { xList.Add(xs[i]); yList.Add(firstVal); }
					for (int i = firstIdx; i < n10; i++) { if (!double.IsNaN(ma10[i])) { xList.Add(xs[i]); yList.Add(ma10[i]); } }
				}
				var xv = xList.ToArray(); var yv = yList.ToArray();
				if (yv.Length > 1) WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Orange, lineWidth: 1.0f, markerSize: 0f, label: "MA10");
			}

            var ma20 = snap.MA20 ?? ComputeSimpleMovingAverage(closes, 20);
            if(MA20.IsChecked == true)
            {
                var xList = new List<double>();
				var yList = new List<double>();
				int n20 = Math.Min(xs.Length, ma20.Length);
				int firstIdx = -1;
				double firstVal = double.NaN;
				for (int i = 0; i < n20; i++)
				{
					if (!double.IsNaN(ma20[i])) { firstIdx = i; firstVal = ma20[i]; break; }
				}
				if (firstIdx >= 0)
				{
					for (int i = 0; i < firstIdx; i++) { xList.Add(xs[i]); yList.Add(firstVal); }
					for (int i = firstIdx; i < n20; i++) { if (!double.IsNaN(ma20[i])) { xList.Add(xs[i]); yList.Add(ma20[i]); } }
				}
				var xv = xList.ToArray(); var yv = yList.ToArray();
				if (yv.Length > 1) WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.MediumVioletRed, lineWidth: 1.0f, markerSize: 0f, label: "MA20");
			}

            var ma30 = snap.MA30 ?? ComputeSimpleMovingAverage(closes, 30);
            if (MA30.IsChecked == true)
            {
                var xList = new List<double>();
                var yList = new List<double>();
                int n30 = Math.Min(xs.Length, ma30.Length);
                int firstIdx = -1;
                double firstVal = double.NaN;
                for (int i = 0; i < n30; i++)
                {
                    if (!double.IsNaN(ma30[i])) { firstIdx = i; firstVal = ma30[i]; break; }
                }
                if (firstIdx >= 0)
                {
                    for (int i = 0; i < firstIdx; i++) { xList.Add(xs[i]); yList.Add(firstVal); }
                    for (int i = firstIdx; i < n30; i++) { if (!double.IsNaN(ma30[i])) { xList.Add(xs[i]); yList.Add(ma30[i]); } }
                }
                var xv = xList.ToArray(); var yv = yList.ToArray();
                if (yv.Length > 1) WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Green, lineWidth: 1.0f, markerSize: 0f, label: "MA30");
            }

            var ma60 = snap.MA60 ?? ComputeSimpleMovingAverage(closes, 60);
            if (MA60.IsChecked == true)
            {
                var xList = new List<double>();
                var yList = new List<double>();
                int n60 = Math.Min(xs.Length, ma60.Length);
                int firstIdx = -1;
                double firstVal = double.NaN;
                for (int i = 0; i < n60; i++)
                {
                    if (!double.IsNaN(ma60[i])) { firstIdx = i; firstVal = ma60[i]; break; }
                }
                if (firstIdx >= 0)
                {
                    for (int i = 0; i < firstIdx; i++) { xList.Add(xs[i]); yList.Add(firstVal); }
                    for (int i = firstIdx; i < n60; i++) { if (!double.IsNaN(ma60[i])) { xList.Add(xs[i]); yList.Add(ma60[i]); } }
                }
                var xv = xList.ToArray(); var yv = yList.ToArray();
                if (yv.Length > 1) WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Gray, lineWidth: 1.0f, markerSize: 0f, label: "MA60");
            }

            // Y 轴：给上下增加小边距，避免实体触到边；同时包含 MA 值
            double yHigh = new[]
			{
				highs.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max(),
				ma5.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max(),
				ma10.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max(),
				ma20.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max(),
				ma30.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max(),
				ma60.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max()
			}.Max();
			double yLow = new[]
			{
				lows.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Min(),
				ma5.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.PositiveInfinity).Min(),
				ma10.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.PositiveInfinity).Min(),
				ma20.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.PositiveInfinity).Min(),
				ma30.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.PositiveInfinity).Min(),
				ma60.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.PositiveInfinity).Min()
			}.Min();
            if (yHigh > yLow)
            {
                double margin = (yHigh - yLow) * 0.06; // 6% margin
                WpfPlotPrice.Plot.SetAxisLimitsY(yLow - margin, yHigh + margin);
            }
            else
            {
                // fallback
                WpfPlotPrice.Plot.AxisAuto();
            }

            // --- 2) 绘制成交量到下方 WpfPlotVolume 并对齐 X 轴 ---
            WpfPlotVolume.Plot.Clear();
            WpfPlotVolume.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);

            // 将成交量转换为“手”（如果你的数据是股数），这里除以100；若已经是手则把 /100 去掉
            double[] volsScaled = snap.Volumes?.Select(v => v).ToArray() ?? new double[count];

            // 保证 volsScaled 长度等于 count
            if (volsScaled.Length != count)
            {
                var tmp = new double[count];
                for (int i = 0; i < count && i < volsScaled.Length; i++) tmp[i] = volsScaled[i];
                volsScaled = tmp;
            }

            // 为成交量设置颜色：用买/卖分开绘制（若有），否则按涨跌绘色
            if (snap.BuyVolumes != null && snap.SellVolumes != null && snap.BuyVolumes.Length == count && snap.SellVolumes.Length == count)
            {
                // 使用 buy/sell 绘制两组柱
                var buyScaled = snap.BuyVolumes.Select(v => v).ToArray();
                var sellScaled = snap.SellVolumes.Select(v =>v).ToArray();

                var barBuy = WpfPlotVolume.Plot.AddBar(buyScaled, xs);
                barBuy.FillColor = System.Drawing.Color.Red;
                barBuy.BarWidth = 0.5;
                barBuy.BorderLineWidth = 0;

                var barSell = WpfPlotVolume.Plot.AddBar(sellScaled, xs);
                barSell.FillColor = System.Drawing.Color.Green;
                barSell.BarWidth = 0.5;
                barSell.BorderLineWidth = 0;
            }
            else
            {
                // 单组成交量，颜色依据当天涨跌（或全部灰色）
                var bars = WpfPlotVolume.Plot.AddBar(volsScaled, xs);
                bars.FillColor = System.Drawing.Color.Gray;
                bars.BarWidth = 0.5;
                bars.BorderLineWidth = 0;
            }

            WpfPlotVolume.Plot.YLabel("Volume (Lots)");

            // 给成交量 Y 轴加一点上边距
            double maxVol = volsScaled.DefaultIfEmpty(0).Max();
            WpfPlotVolume.Plot.SetAxisLimitsY(0, Math.Max(1e-6, maxVol * 1.2)); // 提高 20%

            // 让两个图的 X 轴范围一致（关键）
            WpfPlotPrice.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);
            WpfPlotVolume.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);

            // 最后渲染
            WpfPlotPrice.Render();
            WpfPlotVolume.Render();
        }

        private void UpdateProfitDisplay()
        {
            if (!double.TryParse(SharesBox.Text, out double shares)) return;
            if (!double.TryParse(CostBox.Text, out double cost)) return;
            if (!double.TryParse(ChangePercentText.Text.TrimEnd('%'), out double change)) return;

            double currentPrice = double.Parse(CurrentPrice.Text);
            double positionProfit = (currentPrice - cost) * shares;
            double todayProfit = currentPrice * change * shares / 100;

            PositionProfitText.Text = $"nrealized P/L: {positionProfit:F2}";
            PositionProfitText.Foreground = positionProfit > 0 ? Brushes.Red : Brushes.Green;

            TodayProfitText.Text = $"Today's P/L: {todayProfit:F2}";
            TodayProfitText.Foreground = todayProfit > 0 ? Brushes.Red : Brushes.Green;

            UpdateVSStatus(currentPrice, change, positionProfit, todayProfit);
        }

        private void UpdateStatus(string text, Brush color = null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = text;
                StatusText.Foreground = color ?? Brushes.Gray;
            }));
        }

		private static double[] ComputeSimpleMovingAverage(double[] source, int period)
		{
			if (source == null || source.Length == 0 || period <= 1)
				return source?.ToArray() ?? Array.Empty<double>();

			int n = source.Length;
			double[] result = new double[n];
			double windowSum = 0.0;
			var window = new System.Collections.Generic.Queue<double>(period);

			for (int i = 0; i < n; i++)
			{
				double v = source[i];
				if (double.IsNaN(v))
				{
					result[i] = (i > 0) ? result[i - 1] : double.NaN;
					continue;
				}

				window.Enqueue(v);
				windowSum += v;
				if (window.Count > period)
					windowSum -= window.Dequeue();

				int denom = Math.Min(i + 1, period);
				result[i] = windowSum / denom;
			}

			return result;
		}

		private static double[] ComputeExactWindowSMA(double[] source, int period)
		{
			if (source == null || source.Length == 0 || period <= 0)
				return source?.ToArray() ?? Array.Empty<double>();

			int n = source.Length;
			double[] result = new double[n];
			for (int i = 0; i < n; i++) result[i] = double.NaN;

			double windowSum = 0.0;
			int start = 0;
			for (int i = 0; i < n; i++)
			{
				double v = source[i];
				if (double.IsNaN(v)) { start = i + 1; windowSum = 0; continue; }
				windowSum += v;
				if (i - start + 1 > period)
				{
					windowSum -= source[start];
					start++;
				}
				if (i - start + 1 == period)
					result[i] = windowSum / period;
			}

			return result;
		}

        private DateTime GetCurrentDate()
        {
            string s = DatePickerControl.Text;
            if (DateTime.TryParse(s, out DateTime date))
            {
                return date;
            }
            return DateTime.Today;
        }

        private ChartType GetChatType()
        {
            return (ChartType)PeriodComboBox.SelectedIndex;
        }

        private async Task MonitorKDJAsync(string code, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 每分钟检测一次
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    var kSnap = await FetchKSnapshotAsync(NormalizeCode(code), "Daily K");
                    CheckKdjGoldenCross(kSnap);
                }
                catch (TaskCanceledException)
                {
                    // 正常结束
                }
                catch (Exception ex)
                {
                    UpdateStatus("KDJ check error: " + ex.Message, Brushes.Red);
                }
            }
        }

        private void CheckKdjGoldenCross(StockSnapshot snap)
        {
            if (snap != null && snap.Prices != null && snap.Prices.Length >= 10)
            {
                bool isGolden = HasKDJGoldenCross(snap.Prices, snap.HighPrices, snap.LowPrices);
                bool isDeath = HasKDJDeadCross(snap.Prices, snap.HighPrices, snap.LowPrices);
                var t = DateTime.Now.ToString("hh:mm:ss");

                if (isGolden)
                {
                    string str = $"*************** {t} KDJ 出现金叉信号！***************";
                    UpdateStatus(str, Brushes.Green);
                    UpdateVSStatus(str);
                }
                else if (isDeath)
                {
                    string str = $"*************** {t} KDJ 出现死叉信号！***************";
                    UpdateStatus(str, Brushes.Red);
                    UpdateVSStatus(str);
                }
            }
        }

        /// <summary>
        /// 检测KDJ金叉出现（上一根K<D，本根K≥D）
        /// </summary>
        private bool HasKDJGoldenCross(double[] closes, double[] highs, double[] lows)
        {
            if (closes == null || highs == null || lows == null || closes.Length < 10)
                return false;

            List<double> KList = new List<double>();
            List<double> DList = new List<double>();
            double K = 50, D = 50;

            for (int i = 0; i < closes.Length; i++)
            {
                if (i < 8)
                {
                    KList.Add(K);
                    DList.Add(D);
                    continue;
                }

                double H9 = highs.Skip(Math.Max(0, i - 8)).Take(9).Max();
                double L9 = lows.Skip(Math.Max(0, i - 8)).Take(9).Min();
                double RSV = (H9 == L9) ? 50 : (closes[i] - L9) / (H9 - L9) * 100;

                K = 2.0 / 3.0 * K + 1.0 / 3.0 * RSV;
                D = 2.0 / 3.0 * D + 1.0 / 3.0 * K;

                KList.Add(K);
                DList.Add(D);
            }

            if (KList.Count >= 2)
            {
                double prevK = KList[KList.Count - 2];
                double prevD = DList[KList.Count - 2];
                double currK = KList[KList.Count - 1];
                double currD = DList[KList.Count - 1];

                if (prevK < prevD && currK >= currD)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 检测KDJ死叉出现（上一根K>D，本根K≤D）
        /// </summary>
        private bool HasKDJDeadCross(double[] closes, double[] highs, double[] lows)
        {
            if (closes == null || highs == null || lows == null || closes.Length < 10)
                return false;

            List<double> KList = new List<double>();
            List<double> DList = new List<double>();
            double K = 50, D = 50;

            for (int i = 0; i < closes.Length; i++)
            {
                if (i < 8)
                {
                    KList.Add(K);
                    DList.Add(D);
                    continue;
                }

                double H9 = highs.Skip(Math.Max(0, i - 8)).Take(9).Max();
                double L9 = lows.Skip(Math.Max(0, i - 8)).Take(9).Min();
                double RSV = (H9 == L9) ? 50 : (closes[i] - L9) / (H9 - L9) * 100;

                K = 2.0 / 3.0 * K + 1.0 / 3.0 * RSV;
                D = 2.0 / 3.0 * D + 1.0 / 3.0 * K;

                KList.Add(K);
                DList.Add(D);
            }

            if (KList.Count >= 2)
            {
                double prevK = KList[KList.Count - 2];
                double prevD = DList[KList.Count - 2];
                double currK = KList[KList.Count - 1];
                double currD = DList[KList.Count - 1];

                if (prevK > prevD && currK <= currD)
                    return true;
            }
            return false;
        }
    }
}
