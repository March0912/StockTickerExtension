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
        public double[] Prices { get; set; }
        public double[] AvgPrices { get; set; }
        public double[] HighPrices { get; set; }
        public double[] LowPrices { get; set; }
        public double[] Volumes { get; set; }
        public double[] BuyVolumes { get; set; }
        public double[] SellVolumes { get; set; }
        public double? ChangePercent { get; set; }
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
        private CancellationTokenSource _cts;
        private readonly HttpClient _http = new HttpClient();
        private readonly ConcurrentQueue<StockSnapshot> _queue = new ConcurrentQueue<StockSnapshot>();
        private DispatcherTimer _uiTimer;

        private List<string> _tradingMinutes;
        private int _totalMinutes;
        private int _fetchIntervalSeconds = 5;
        private bool _monitoring = false;
        private DateTime _currentDate;
        private CancellationTokenSource _kdjCts;

        private int PriceChatMinHeight = 240;
        private int PriceChatMaxHeight = 340;

        public StockToolWindowControl()
        {
            InitializeComponent();

            _currentDate = GetCurrentDate();
            _tradingMinutes = BuildTradingMinutes(_currentDate);
            _totalMinutes = _tradingMinutes.Count;

            Init();
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

        private void StockToolWindowControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AutoStopCheckBox.IsChecked == true)
            {
                StopMonitoring();
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
                    WpfPlotPrice.Height = PriceChatMinHeight;
                    WpfPlotVolume.Visibility = Visibility.Hidden;
                }
                else
                {
                    DatePickerControl.IsEnabled = true;
                    WpfPlotPrice.Height = PriceChatMaxHeight;
                    WpfPlotVolume.Visibility = Visibility.Visible;
                }
            }
            if (_monitoring)
            {
                StopMonitoring(); // 切换周期时先停止监控
                StartMonitoring();
            }
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
            StopBtn.Click += StopBtn_Click;
            StopBtn.IsEnabled = false;

            CodeTextBox.PreviewTextInput += CodeTextBox_PreviewTextInput;
            DataObject.AddPastingHandler(CodeTextBox, CodeTextBox_Pasting);
            // 当 UserControl 卸载（窗口关闭）时停止监控
            this.Unloaded += StockToolWindowControl_Unloaded;
            DatePickerControl.SelectedDateChanged += Date_SelecteionChanged;

            InitPeriodComboBox();
            InitPriceChat();

            _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, UiTimer_Tick, Dispatcher.CurrentDispatcher);
        }

        private void InitPeriodComboBox()
        {
            PeriodComboBox.Items.Add(new ComboBoxItem { Content = "Intraday" });
            if (false)
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

        bool CheckTradingTime(string period)
        {
            var code = CodeTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                UpdateStatus("Error: Please enter a stock code", Brushes.Red);
                return false;
            }

            // ------------------ 检查交易时间 ------------------
            if (period == "Intraday" && !IsTradingTime(DateTime.Now))
            {
                // 收盘后（15:00之后）允许启动，并显示当日完整分时数据
                var now = DateTime.Now.TimeOfDay;
                var closeTime = new TimeSpan(15, 0, 0);
                if (now > closeTime)
                {
                    return true;
                }

                UpdateStatus("Currently outside trading hours, monitoring cannot start", Brushes.Red);
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

            if (!CheckTradingTime(period))
                return;

            StopMonitoring();
            _monitoring = true;

            var code = CodeTextBox.Text?.Trim();
            _cts = new CancellationTokenSource();
            _ =Task.Run(() => FetchLoopAsync(code, period, _cts.Token));

            // ✅ 如果是分时图，则同时启动金叉监控线程
            if (period == "Intraday")
            {
                _kdjCts = new CancellationTokenSource();
                _ = Task.Run(() => MonitorKDJAsync(code, _kdjCts.Token));
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

        private async Task FetchLoopAsync(string code, string period, CancellationToken token)
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
                begStr = _currentDate.AddDays(-200).ToString("yyyyMMdd");
            else if (period == "Weekly K")
                begStr = _currentDate.AddDays(-365*3).ToString("yyyyMMdd");
            else if (period == "Monthly K")
                begStr = _currentDate.AddMonths(-36).ToString("yyyyMMdd");
            else if (period == "Quarterly K")
                begStr = _currentDate.AddMonths(-20).ToString("yyyyMMdd");
            else if (period == "Yearly K")
                begStr = _currentDate.AddYears(-10).ToString("yyyyMMdd");
            else
                begStr = _currentDate.AddDays(-200).ToString("yyyyMMdd");

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

            for (int i = 0; i < count; i++)
            {
                var parts = klines[i].Split(',');
                double open = double.Parse(parts[1]);
                double close = double.Parse(parts[2]);
                double low = double.Parse(parts[3]);
                double high = double.Parse(parts[4]);
                double vol = double.Parse(parts[5]);

                prices[i] = close;
                highs[i] = high;
                lows[i] = low;
//                 avgPrices[i] = (open + close + high + low) / 4.0;
                vols[i] = vol;
            }

            double lastPrice = prices.Last();
            double changePercent = (count >= 2) ? (lastPrice - prices[count - 2]) / prices[count - 2] * 100 : 0;

            // 假设你有收盘价、最高价、最低价列表
            bool goldenCross = HasKDJGoldenCross(prices, highs, lows);
            bool deadCross = HasKDJDeadCross(prices, highs, lows);

            if (goldenCross)
            {
                var t = DateTime.Now.ToString("hh:mm:ss");
                UpdateStatus($"*************** {t} KDJ 出现金叉信号！***************", Brushes.Green);
            }
            else if (deadCross)
            {
                var t = DateTime.Now.ToString("hh:mm:ss");
                UpdateStatus($"*************** {t} KDJ 出现死叉信号！***************", Brushes.Red);
            }

            return new StockSnapshot
            {
                Code = codeWithSuffix,
                Name = name,
                Prices = prices,
                HighPrices = highs,
                LowPrices = lows,
                AvgPrices = avgPrices,
                Volumes = vols,
                BuyVolumes = vols.Select(v => v * 0.5).ToArray(),
                SellVolumes = vols.Select(v => v * 0.5).ToArray(),
                CurrentPrice = lastPrice,
                ChangePercent = changePercent
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
            }
        }

        private void UpdatePriceChart(StockSnapshot snap)
        {
            if (!_monitoring)
                return;

            if(GetChatType() == ChartType.Intraday)
            {
                DrawIntradayPriceChart(snap);
            }
            else
            {
                DrawKlinePriceChart(snap);
            }
        }

        private void DrawIntradayPriceChart(StockSnapshot snap)
        {
            if (!_monitoring)
                return;

            List<double> safePrices = new List<double>();
            List<double> safeAvgPrices = new List<double>();

            for (int i = 0; i < snap.Prices.Length; i++)
            {
                if (!double.IsNaN(snap.Prices[i])) safePrices.Add(snap.Prices[i]);
                if (!double.IsNaN(snap.AvgPrices[i])) safeAvgPrices.Add(snap.AvgPrices[i]);
            }
            if (safePrices.Count == 0)
                return;

            WpfPlotPrice.Plot.Clear();

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
                barSell.FillColor = System.Drawing.Color.FromArgb(100, 0, 255, 0);
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

        private void DrawKlinePriceChart(StockSnapshot snap)
        {
            List<double> safePrices = new List<double>();
            List<double> safeAvgPrices = new List<double>();

            for (int i = 0; i < snap.Prices.Length; i++)
            {
                if (!double.IsNaN(snap.Prices[i])) safePrices.Add(snap.Prices[i]);
                if (!double.IsNaN(snap.AvgPrices[i])) safeAvgPrices.Add(snap.AvgPrices[i]);
            }
            if (safePrices.Count == 0)
                return;

            WpfPlotPrice.Plot.Clear();

            WpfPlotPrice.Plot.SetAxisLimits(xMin: 0, xMax: safePrices.Count);

            var xs = Enumerable.Range(0, safePrices.Count).Select(i => (double)i).ToArray();

            // 价格曲线
            WpfPlotPrice.Plot.AddScatter(xs, safePrices.ToArray(), color: System.Drawing.Color.FromArgb(31, 119, 180), lineWidth: 2.0f, markerSize: 2.2f);

            // 成交量（右Y轴）
            if (snap.BuyVolumes != null && snap.SellVolumes != null)
            {
                var safeBuy = snap.BuyVolumes.Take(safePrices.Count).Select(v => double.IsNaN(v) ? 0 : v).ToArray();
                var safeSell = snap.SellVolumes.Take(safePrices.Count).Select(v => double.IsNaN(v) ? 0 : v).ToArray();

                var barBuy = WpfPlotPrice.Plot.AddBar(safeBuy, xs);
                barBuy.FillColor = System.Drawing.Color.FromArgb(100, 255, 0, 0);
                barBuy.YAxisIndex = 1; // 使用右Y轴

                var barSell = WpfPlotPrice.Plot.AddBar(safeSell, xs);
                barSell.FillColor = System.Drawing.Color.FromArgb(100, 0, 255, 0);
                barSell.YAxisIndex = 1;
            }

            // 坐标轴名称
            WpfPlotPrice.Plot.YLabel("Price(RMB)");
            WpfPlotPrice.Plot.YAxis2.Label("Volume (Lots)");

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
            double maxVolume = Math.Max(snap.BuyVolumes?.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() ?? 0,
                                        snap.SellVolumes?.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() ?? 0);
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

            PositionProfitText.Text = $"nrealized P/L: {positionProfit:F2}";
            PositionProfitText.Foreground = positionProfit > 0 ? Brushes.Red : Brushes.Green;

            TodayProfitText.Text = $"Today's P/L: {todayProfit:F2}";
            TodayProfitText.Foreground = todayProfit > 0 ? Brushes.Red : Brushes.Green;
        }

        private void UpdateStatus(string text, Brush color = null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = text;
                StatusText.Foreground = color ?? Brushes.Gray;
            }));
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
//                     if (kSnap == null || kSnap.Prices == null || kSnap.Prices.Length < 10)
//                         continue;

//                     bool isGolden = HasKDJGoldenCross(kSnap.Prices, kSnap.HighPrices, kSnap.LowPrices);
//                     bool isDeath = HasKDJDeadCross(kSnap.Prices, kSnap.HighPrices, kSnap.LowPrices);
//                     var t = DateTime.Now.ToString("hh:mm:ss");
// 
//                     if (isGolden)
//                         UpdateStatus($"*************** {t} KDJ 出现金叉信号！***************", Brushes.Green);
//                     else if (isDeath)
//                         UpdateStatus($"*************** {t} KDJ 出现金叉信号！***************", Brushes.Red);
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
