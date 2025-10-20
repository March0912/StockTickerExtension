using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using ScottPlot;
using ScottPlot.Drawing.Colormaps;
using ScottPlot.Plottable;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TextBox;

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

    public enum PeriodType
    {
        Intraday = 0,
        DailyK,
        WeeklyK,
        MonthlyK,
        QuarterlyK,
        YearlyK,
    };

    public enum StockType : int
    {
        StockA,
        StockHK,
        StockUS
    };

    public partial class StockToolWindowControl : UserControl
    {
        private readonly StockToolWindow _ownerPane;
        private readonly ConfigManager _configManager = new ConfigManager();
        private readonly HttpClient _http = new HttpClient();
        private readonly ConcurrentQueue<StockSnapshot> _queue = new ConcurrentQueue<StockSnapshot>();
        
        private CancellationTokenSource _cts;
        private DispatcherTimer _uiTimer;
        private List<string> _tradingMinutes;
        private int _fetchIntervalSeconds = 5;
        private bool _monitoring = false;
        private bool _monitorOnce = false;
        private DateTime _currentDate;
        private CancellationTokenSource _kdjCts;
        private StockType _stockType = StockType.StockA;
        StockSnapshot _currentSnapshot;
        private Crosshair _crosshair;


        // K线图缩放和拖拽相关字段
        private bool _isDragging = false;
        private System.Windows.Point _lastMousePosition;
        private double _dragStartX = 0;
        private int _dragStartIndex = 0;

        const string s_trendsURL = "https://push2his.eastmoney.com/api/qt/stock/trends2/get";
        const string s_klineURL = "https://push2his.eastmoney.com/api/qt/stock/kline/get";

        public StockToolWindowControl(ToolWindowPane owner)
        {
            InitializeComponent();
            _ownerPane = owner as StockToolWindow;

            Init();
            Logger.Info("StockWather initialized successed!");
        }

        public bool IsAutoStopWhenClosed()
        {
            return AutoStopCheckBox.IsChecked == true;
        }

        private void StartBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StartMonitoring();

        private void StopBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StopMonitoring();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (AutoStopCheckBox.IsChecked == true)
            {
                StopMonitoring();
                _ownerPane.ClearStatusInfo();
            }
        }

        private void PeriodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PeriodComboBox.SelectedIndex == (int)PeriodType.Intraday)
            {
                DatePickerControl.IsEnabled = false;
                DatePickerControl.SelectedDate = DateTime.Today;

                MA5.IsEnabled = false;
                MA10.IsEnabled = false;
                MA20.IsEnabled = false;
                MA30.IsEnabled = false;
                MA60.IsEnabled = false;

                WpfPlotPrice.Plot.Clear();
                WpfPlotPrice.Configuration.ScrollWheelZoom = false;
                WpfPlotPrice.Configuration.LeftClickDragPan = false;
                WpfPlotPrice.Render();

                WpfPlotVolume.Visibility = Visibility.Hidden;
                WpfPlotVolume.Configuration.ScrollWheelZoom = false;
                WpfPlotVolume.Configuration.LeftClickDragPan = false;
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

                MA5.IsChecked = _configManager.Config.MA5Checked;
                MA10.IsChecked = _configManager.Config.MA10Checked;
                MA20.IsChecked = _configManager.Config.MA20Checked;
                MA30.IsChecked = _configManager.Config.MA30Checked;
                MA60.IsChecked = _configManager.Config.MA60Checked;

                WpfPlotPrice.Configuration.ScrollWheelZoom = false;
                WpfPlotPrice.Configuration.LeftClickDragPan = false;
                WpfPlotPrice.Render();

                WpfPlotVolume.Configuration.ScrollWheelZoom = false;
                WpfPlotVolume.Configuration.LeftClickDragPan = false;
                WpfPlotVolume.Visibility = Visibility.Visible;
                WpfPlotVolume.Render();
            }

            StartBtn_Click(null, null);
        }

        private void CodeTextBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             StopBtn_Click(null, null);
             StartBtn_Click(null, null);
        }

        private void StockTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _stockType = (StockType)StockTypeComboBox.SelectedIndex;
            _tradingMinutes = BuildTradingMinutes(_currentDate);
        }

        private void Date_SelecteionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentDate = GetCurrentDate();
            _tradingMinutes = BuildTradingMinutes(_currentDate);
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = false;
        }

        private void CodeTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true; // 防止系统默认行为（例如“叮”声）

                if (sender is ComboBox comboBox)
                {
                    var text = comboBox.Text?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (!_monitoring)
                            StartBtn_Click(null, null);
                    }
                    else
                    {
                        UpdateStatus("Please enter a stock code.", System.Windows.Media.Brushes.Red);
                    }
                }
            }
        }

        private void AddBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = CodeTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                // 自动把输入的代码加入历史记录
                if (!CodeTextBox.Items.Contains(text))
                {
                    CodeTextBox.Items.Add(text);
                }
            }
        }

        private void RemoveBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = CodeTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                CodeTextBox.Items.Remove(text);
            }
        }

        private void Init()
        {
            _configManager.Load();

            SharesBox.Text = _configManager.Config.CurrentShares.ToString();
            CostBox.Text = _configManager.Config.CurrentCostPrices.ToString();
            AutoStopCheckBox.IsChecked = _configManager.Config.AutoStopOnClose;
            MA5.IsChecked = _configManager.Config.MA5Checked;
            MA10.IsChecked = _configManager.Config.MA10Checked;
            MA20.IsChecked = _configManager.Config.MA20Checked;
            MA30.IsChecked = _configManager.Config.MA30Checked;
            MA60.IsChecked = _configManager.Config.MA60Checked;

            AddBtn.Click += AddBtn_Click;
            RemoveBtn.Click += RemoveBtn_Click;
            StartBtn.Click += StartBtn_Click;
            StartBtn.Content = !IsTradingTime(DateTime.Now) ? "Get" : "Start";
            StopBtn.Click += StopBtn_Click;
            StopBtn.IsEnabled = false;

            MA5.IsEnabled = false;
            MA10.IsEnabled = false;
            MA20.IsEnabled = false;
            MA30.IsEnabled = false;
            MA60.IsEnabled = false;

            // 当 UserControl 卸载（窗口关闭）时停止监控
            this.Unloaded += OnUnloaded;
            DatePickerControl.SelectedDateChanged += Date_SelecteionChanged;

            CurrentPriceText.FontWeight = FontWeights.Bold;
            CurrentPriceText.Foreground = System.Windows.Media.Brushes.Green;

            InitCodeTextBox();
            InitStockTypeComboBox();
            InitPeriodComboBox();

            _currentDate = GetCurrentDate();
            _tradingMinutes = BuildTradingMinutes(_currentDate);

            InitPriceChat();
            InitUIColor();

            if(CodeTextBox.Items.Count == 0)
            {
                UpdateStatus("Please enter a stock code.", System.Windows.Media.Brushes.Red);
            }

            _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, UiTimer_Tick, Dispatcher.CurrentDispatcher);
        }

        private void InitCodeTextBox()
        {
            CodeTextBox.KeyUp += CodeTextBox_KeyUp;

            foreach (var code in _configManager.Config.WatchStockList)
            {
                CodeTextBox.Items.Add(code);
            }

            CodeTextBox.Text = _configManager.Config.CurrentStock;

            //有问题，先不启用
//             CodeTextBox.SelectionChanged += CodeTextBox_SelectionChanged;    
        }

        private void InitStockTypeComboBox()
        {
            StockTypeComboBox.Items.Add("A-shares");
            StockTypeComboBox.Items.Add("HK stocks");
            StockTypeComboBox.Items.Add("US stocks");

            StockTypeComboBox.SelectedIndex = (int)_configManager.Config.CurrentStockType;
            _stockType = _configManager.Config.CurrentStockType;

            StockTypeComboBox.SelectionChanged += StockTypeComboBox_SelectionChanged;
        }

        private void InitPeriodComboBox()
        {
            PeriodComboBox.Items.Add("Intraday");
            PeriodComboBox.Items.Add("Daily K");
            PeriodComboBox.Items.Add("Weekly K");
            PeriodComboBox.Items.Add("Monthly K");
            PeriodComboBox.Items.Add("Quarterly K");
            PeriodComboBox.Items.Add("Yearly K");

            PeriodComboBox.SelectedIndex = 0;
            PeriodComboBox.SelectionChanged += PeriodComboBox_SelectionChanged;
        }

        private void InitPriceChat()
        {
            WpfPlotPrice.Configuration.ScrollWheelZoom = false;
            WpfPlotPrice.Configuration.LeftClickDragPan = false;
            WpfPlotPrice.Plot.SetAxisLimits(xMin: 0, xMax: _tradingMinutes.Count - 1);

            // 为价格图添加鼠标事件
//             WpfPlotPrice.MouseWheel += OnKLineMouseWheel;
//             WpfPlotPrice.MouseLeftButtonDown += OnKLineMouseLeftButtonDown;
//             WpfPlotPrice.MouseLeftButtonUp += OnKLineMouseLeftButtonUp;
            WpfPlotPrice.MouseMove += OnKLineMouseMove;
            WpfPlotPrice.MouseLeave += OnWpf_MouseLeave;

            // 初始化十字线（只创建一次）
            if (_crosshair == null)
            {
                _crosshair = WpfPlotPrice.Plot.AddCrosshair(50, 0);
                _crosshair.IsVisible = false;
                _crosshair.LineColor = System.Drawing.Color.Red;
                _crosshair.LineWidth = 1;
                _crosshair.Color = System.Drawing.Color.Red;
            }

            WpfPlotVolume.Configuration.ScrollWheelZoom = false;
            WpfPlotVolume.Configuration.LeftClickDragPan = false;

            // 为成交量图添加相同的鼠标事件，实现联动
//             WpfPlotVolume.MouseWheel += OnKLineMouseWheel;
//             WpfPlotVolume.MouseLeftButtonDown += OnKLineMouseLeftButtonDown;
//             WpfPlotVolume.MouseLeftButtonUp += OnKLineMouseLeftButtonUp;
//             WpfPlotVolume.MouseMove += OnKLineMouseMove;

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

        private void InitUIColor()
        {
            this.Loaded += (s, e) =>
            {
                ApplyThemeToAllControls(this);
                VSColorTheme.ThemeChanged += _ => Dispatcher.Invoke(() => ApplyThemeToAllControls(this));
            };
        }

        private void ApplyThemeToAllControls(DependencyObject obj)
        {
            var fgColor0 = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            var fgColor = System.Windows.Media.Color.FromArgb(fgColor0.A, fgColor0.R, fgColor0.G, fgColor0.B);

            var bgColor0 = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var bgColor = System.Windows.Media.Color.FromArgb(bgColor0.A, bgColor0.R, bgColor0.G, bgColor0.B);

            var bdColor0 = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey);
            var bdColor = System.Windows.Media.Color.FromArgb(bgColor0.A, bdColor0.R, bdColor0.G, bdColor0.B);

            var fgBrush = new SolidColorBrush(fgColor);
            var bgBrush = new SolidColorBrush(bgColor);
            var bdBrush = new SolidColorBrush(bdColor);
            
            // 设置当前控件
            if (obj is Control ctrl)
            {
//                 ctrl.Background = bgBrush;
                if (ctrl.Name != "StartBtn" && ctrl.Name != "StopBtn" && bgColor0.Name.ToLower() == "ff1f1f1f")
                    ctrl.Foreground = fgBrush;

                if (ctrl is ComboBox combo)
                {
                    combo.Background = bgBrush;
                    combo.BorderBrush = bdBrush;
                    // 下拉项的背景色需要通过 ItemContainerStyle 改
                    combo.ItemContainerStyle = new System.Windows.Style(typeof(ComboBoxItem))
                    {
                        Setters =
                        {
                            new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(bgColor)),
                            new Setter(ComboBoxItem.ForegroundProperty, new SolidColorBrush(fgColor)),
                            new Setter(ComboBoxItem.BorderBrushProperty, new SolidColorBrush(bgColor))
                        }
                    };
                    combo.UpdateLayout();
                    ApplyThemeToComboBoxChildren(combo, fgBrush, bgBrush, bdBrush);
                }
                else if (ctrl is CheckBox cb)
                {
                    cb.Foreground = fgBrush;
                    if (bgColor0.Name.ToLower() == "ff1f1f1f")  //暗黑主题才调整
                    {
                        cb.ApplyTemplate();
                        ApplyThemeToCheckBoxChildren(cb);
                    }
                }
                else if (ctrl is DatePicker dp)
                {
                    dp.ApplyTemplate();
                    ApplyThemeToDatePickerChildren(dp, fgBrush, bgBrush);
                }
                else if (ctrl is ScottPlot.WpfPlot wpfPlot)
                {
                    var bdColor1 = System.Drawing.Color.FromArgb(80, bdColor0.R, bdColor0.G, bdColor0.B);
                    var fgColor1 = System.Drawing.Color.FromArgb(150, fgColor0.R, fgColor0.G, fgColor0.B);
                    wpfPlot.Plot.Style(figureBackground: bgColor0,
                                        dataBackground: bgColor0,
                                        grid: bdColor1,
                                        tick: fgColor1,                                        
                                        axisLabel: fgColor0,
                                        titleLabel: fgColor0);
                    wpfPlot.Refresh();
                }
            }

            // 递归对子控件应用
            int count = VisualTreeHelper.GetChildrenCount(obj);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                ApplyThemeToAllControls(child);
            }
        }

        private void ApplyThemeToComboBoxChildren(DependencyObject obj, SolidColorBrush fgBrush, SolidColorBrush bgBrush, SolidColorBrush bdBrush)
        {
            if (obj is Border bd)
            {
                bd.Background = bgBrush;
                bd.BorderBrush = bdBrush;
            }
            else if (obj is TextBlock tb)
            {
                tb.Foreground = fgBrush;
            }
            else if (obj is TextBox tbox)
            {
                tbox.Foreground = new SolidColorBrush(Colors.Red);
            }
            else
            {
                int count = VisualTreeHelper.GetChildrenCount(obj);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                    ApplyThemeToComboBoxChildren(child, fgBrush, bgBrush, bdBrush);
                }
            }
        }
        private void ApplyThemeToCheckBoxChildren(DependencyObject obj)
        {
            if (obj is Path ph)
            {
                ph.Fill = new SolidColorBrush(Colors.LightBlue);
            }
            else
            {
                int count = VisualTreeHelper.GetChildrenCount(obj);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                    ApplyThemeToCheckBoxChildren(child);
                }
            }
        }

        private void ApplyThemeToDatePickerChildren(DependencyObject obj, SolidColorBrush fgBrush, SolidColorBrush bgBrush)
        {
            if (obj is DatePickerTextBox dptb)
            {
                 dptb.Foreground = fgBrush;
                 dptb.Background = bgBrush;
            }
//             else if (obj is Shape sp)
//             {
//                 sp.Fill = bgBrush;
//             }
            else
            {
                int count = VisualTreeHelper.GetChildrenCount(obj);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                    ApplyThemeToDatePickerChildren(child, fgBrush, bgBrush);
                }
            }
        }


        private List<string> BuildTradingMinutes(DateTime date)
        {
            var list = new List<string>();

            if (_stockType == StockType.StockA)
            {
                var t = date.AddHours(9).AddMinutes(30);
                var end = date.AddHours(11).AddMinutes(30);
                while (t <= end) 
                { 
                    list.Add(t.ToString("yyyy-MM-dd HH:mm"));
                    t = t.AddMinutes(1); 
                }

                t = date.AddHours(13);
                end = date.AddHours(15);
                while (t <= end) 
                {
                    list.Add(t.ToString("yyyy-MM-dd HH:mm"));
                    t = t.AddMinutes(1);
                }
            }
            else if( _stockType == StockType.StockHK)
            {
                var t = date.AddHours(9).AddMinutes(30);
                var end = date.AddHours(12).AddMinutes(00);
                while (t <= end)
                {
                    list.Add(t.ToString("yyyy-MM-dd HH:mm"));
                    t = t.AddMinutes(1);
                }

                t = date.AddHours(13);
                end = date.AddHours(16);
                while (t <= end)
                {
                    list.Add(t.ToString("yyyy-MM-dd HH:mm"));
                    t = t.AddMinutes(1);
                }
            }
            else// if (_stockType == StockType.StockUS)
            {
                // 判断是否夏令时（美东时间）
                var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                bool isDst = easternZone.IsDaylightSavingTime(DateTime.UtcNow);

                // 夏令时：21:30 - 次日04:00
                // 冬令时：22:30 - 次日05:00
                DateTime start = isDst ? date.AddHours(21).AddMinutes(30) : date.AddHours(22).AddMinutes(30);
                start = start.AddDays(-1);
                DateTime end = isDst ? date.AddDays(1).AddHours(4) : date.AddDays(1).AddHours(5);
                end = end.AddDays(-1);

                var t = start;
                while (t <= end)
                {
                    list.Add(t.ToString("yyyy-MM-dd HH:mm"));
                    t = t.AddMinutes(1);
                }
            }
            return list;
        }

        private void UpdateVSStatus(string code, double price, double changePercent, double positionProfit, double todayProfit)
        {
            // Dispatcher 保证在 UI 线程安全执行
            Dispatcher.Invoke(() =>
            {
                // 获取当前的父窗口（ToolWindowPane）
                if (_ownerPane != null)
                {
                    _ownerPane.UpdateStatusInfo(code, price, changePercent, positionProfit, todayProfit);
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

            if (_stockType == StockType.StockA)
            {
                TimeSpan morningStart = new TimeSpan(9, 30, 0);
                TimeSpan morningEnd = new TimeSpan(11, 30, 0);
                TimeSpan afternoonStart = new TimeSpan(13, 0, 0);
                TimeSpan afternoonEnd = new TimeSpan(15, 0, 0);

                TimeSpan nowTime = dt.TimeOfDay;
                return (nowTime >= morningStart && nowTime <= morningEnd) ||
                       (nowTime >= afternoonStart && nowTime <= afternoonEnd);
            }
            else if (_stockType == StockType.StockHK)
            {
                TimeSpan morningStart = new TimeSpan(9, 30, 0);
                TimeSpan morningEnd = new TimeSpan(12, 00, 0);
                TimeSpan afternoonStart = new TimeSpan(13, 0, 0);
                TimeSpan afternoonEnd = new TimeSpan(15, 0, 0);

                TimeSpan nowTime = dt.TimeOfDay;
                return (nowTime >= morningStart && nowTime <= morningEnd) ||
                       (nowTime >= afternoonStart && nowTime <= afternoonEnd);
            }
            else// if (_stockType == StockType.StockUS)
            {
                // 判断是否夏令时（美东时间）
                var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                bool isDst = easternZone.IsDaylightSavingTime(DateTime.UtcNow);

                DateTime today = DateTime.Today;
                // 夏令时：21:30 - 次日04:00
                // 冬令时：22:30 - 次日05:00
                DateTime start = isDst ? today.AddHours(21).AddMinutes(30) : today.AddHours(22).AddMinutes(30);
                start = start.AddDays(-1);
                DateTime end = isDst ? today.AddDays(1).AddHours(4) : today.AddDays(1).AddHours(5);
                end = end.AddDays(-1);

                TimeSpan nowTime = dt.TimeOfDay;
                return (nowTime >= start.TimeOfDay && nowTime <= end.TimeOfDay);
            }
        }

        bool CheckTradingTime()
        {
            var codeName = CodeTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(codeName))
            {
                UpdateStatus("Error: Please enter a stock code", System.Windows.Media.Brushes.Red);
                return false;
            }

            // ------------------ 检查交易时间 ------------------
            if (!IsTradingTime(DateTime.Now))
            {
                // 收盘后（15:00之后）允许启动，并显示当日完整分时数据
                UpdateStatus("Currently outside trading hours", System.Windows.Media.Brushes.Red); 
                return false;
            }
            return true;
        }

        private void StartMonitoring()
        {
            PeriodType period = (PeriodType)PeriodComboBox.SelectedIndex;
            if (!CheckTradingTime())
            {
                if(period == PeriodType.Intraday && DateTime.Now.TimeOfDay < new TimeSpan(9, 30, 0))
                {
                    return;
                }
                // 如果不在交易时间，则不启动监控，只获取一次数据
                _monitorOnce = true;
            }

            StopMonitoring();
            _monitoring = true;

            var codeName = CodeTextBox.Text?.Trim();
            if(codeName == null)
            {
                return; 
            }

            var code = codeName.Split(' ')[0];
            _cts = new CancellationTokenSource();
            _ =Task.Run(() => MonitorLoopAsync(code, period, _cts.Token));

            // ✅ 如果是分时图，则同时启动金叉监控线程
            if (period == PeriodType.Intraday)
            {
                _kdjCts = new CancellationTokenSource();
                if (!_monitorOnce)
                {
                    _ = Task.Run(() => MonitorKDJAsync(code, _kdjCts.Token));
                }
            }

            if (!_uiTimer.IsEnabled) _uiTimer.Start();
            UpdateStatus("", System.Windows.Media.Brushes.LightBlue);
           
            StartBtn.IsEnabled = false;
            StartBtn.FontWeight = FontWeights.Normal;

            StopBtn.IsEnabled = true;
            StopBtn.FontWeight = FontWeights.Bold;

            Logger.Info("Start monitoring stock: " + codeName);
        }

        private void StopMonitoring()
        {
            if (!_monitoring)
            {
                return;
            }

            StopBtn.IsEnabled = false;
            StopBtn.FontWeight = FontWeights.Normal;

            StartBtn.IsEnabled = true;
            StartBtn.FontWeight = FontWeights.Bold;

            _monitoring = false;

            _cts?.Cancel();
            _cts = null;

            _kdjCts?.Cancel();
            _kdjCts = null;

            UpdateStatus("Conitoring stopped", System.Windows.Media.Brushes.Green);
            if (_uiTimer.IsEnabled) 
                _uiTimer.Stop();

            Logger.Info("Monitoring stoped!");
        }

        private string PeriodToKType(PeriodType period)
        {
            string kType;
            switch (period)
            {
                case PeriodType.DailyK:
                    kType = "101";
                    break;
                case PeriodType.WeeklyK:
                    kType = "102";
                    break;
                case PeriodType.MonthlyK:
                    kType = "103";
                    break;
                case PeriodType.QuarterlyK:
                    kType = "104";
                    break;
                case PeriodType.YearlyK:
                    kType = "105";
                    break;
                default:
                    kType = "101";
                    break;
            }
            return kType;
        }

        private async Task MonitorLoopAsync(string code, PeriodType period, CancellationToken token)
        {
            if (!_monitoring)
                return;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snap = await FetchKSnapshot_Async(code, period);
                    if (snap != null)
                    {
                        snap.Code = code;
                        while (_queue.Count > 0) _queue.TryDequeue(out _);
                        _queue.Enqueue(snap);
                    }
                    else
                    {
                        await Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StopBtn_Click(null, null);
                        }));
                        UpdateStatus("Error: Failed to fetch data!", System.Windows.Media.Brushes.Red);
                    }
                }
                catch (Exception ex)
                {
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopBtn_Click(null, null);
                    }));
                    UpdateStatus("Error:" + ex.Message);
                    Logger.Error(ex.Message);
                }

                for (int i = 0; i < _fetchIntervalSeconds * 10; i++)
                {
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(100, token);
                }
            }
        }

		private async Task<StockSnapshot> FetchKSnapshot_Async(string code, PeriodType period)
        {
            if(period == PeriodType.Intraday)
               return await FetchSnapshot_Async(code);

            var secid = GetSecId(code);
            if (secid == null) return null;

            var kType = PeriodToKType(period);

			string begStr;
			if(period == PeriodType.DailyK)
				begStr = _currentDate.AddDays(-240).ToString("yyyyMMdd"); // 多取 40 天以支持 MA 引导
            else if (period == PeriodType.WeeklyK)
                begStr = _currentDate.AddDays(-240*7).ToString("yyyyMMdd");
            else if (period == PeriodType.MonthlyK)
                begStr = _currentDate.AddMonths(-240).ToString("yyyyMMdd");
            else if (period == PeriodType.QuarterlyK)
                begStr = _currentDate.AddMonths(-240*4).ToString("yyyyMMdd");
            else if (period == PeriodType.YearlyK)
                begStr = _currentDate.AddYears(-10).ToString("yyyyMMdd");
            else
                begStr = _currentDate.AddDays(-240).ToString("yyyyMMdd");

            string dateStr = _currentDate.ToString("yyyyMMdd");
            var parameters = $"?secid={secid}&klt={kType}&fqt=1&beg={begStr}&end={dateStr}&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55,f56,f57,f58";
            var requestUrl = s_klineURL + parameters;

            string text;
            using (var resp = await _http.GetAsync(requestUrl))
            {
                if (!resp.IsSuccessStatusCode) return null;

                text = await resp.Content.ReadAsStringAsync();
            }
            var jobj = JObject.Parse(text);
            if (jobj["data"] == null)
                return null;

            var dataObj = jobj["data"];
            if (dataObj.Type == JTokenType.Null)
                return null;

            var klines = dataObj["klines"].ToObject<string[]>();
            if (klines.Length == 0) return null;

			var name = dataObj["name"]?.ToString();
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
			double[] ma5full = Tool.ComputeExactWindowSMA(prices, 5);
			double[] ma10full = Tool.ComputeExactWindowSMA(prices, 10);
			double[] ma20full = Tool.ComputeExactWindowSMA(prices, 20);
			double[] ma30full = Tool.ComputeExactWindowSMA(prices, 30);
			double[] ma60full = Tool.ComputeExactWindowSMA(prices, 60);

            return new StockSnapshot
            {
                Code = code,
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

        private async Task<StockSnapshot> FetchSnapshot_Async(string code)
        {
            var secid = GetSecId(code);
            if (secid == null) return null;

            var dateStr = _currentDate.ToString("yyyyMMdd");
            var parameters = $"?fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays=1&secid={secid}&ut=fa5fd1943c7b386f172d6893dbfba10b&trends={dateStr}";
            var requestUrl = s_trendsURL + parameters;

            using (var req = new HttpRequestMessage(HttpMethod.Get, requestUrl))
            {
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT; .NET)");
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var text = await resp.Content.ReadAsStringAsync();
                var jobj = JObject.Parse(text);
                if (jobj["data"] == null)
                    return null;

                var dataObj = jobj["data"];
                if(dataObj.Type == JTokenType.Null)
                    return null;

                var name = dataObj["name"].ToObject<string>();
                var trends = dataObj["trends"].ToObject<string[]>();
                if (trends == null || trends.Length == 0) return null;

                var prices = Enumerable.Repeat(double.NaN, _tradingMinutes.Count).ToArray();
                var avgPrices = Enumerable.Repeat(double.NaN, _tradingMinutes.Count).ToArray();
                var vols = new double[_tradingMinutes.Count];
                var buy = new double[_tradingMinutes.Count];
                var sell = new double[_tradingMinutes.Count];

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
                    if (idx < 0 || idx >= _tradingMinutes.Count)
                        continue;
                    prices[idx] = r.price;
                    avgPrices[idx] = r.avg;
                    vols[idx] = r.vol;
                }

                for (int i = 0; i < parsedRows.Count; i++)
                {
                    int idx = _tradingMinutes.IndexOf(parsedRows[i].time);
                    if (idx < 0 || idx >= _tradingMinutes.Count)
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
                if (double.TryParse(dataObj["preClose"]?.ToString(), out double preClose))
                {
                    changePercent = (lastPrice - preClose) / preClose * 100;
                }

                return new StockSnapshot
                {
                    Code = code,
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

        private string GetSecId(string code)
        {
            string secId = code;
            switch (_stockType)
            {
                case StockType.StockA:
                    {
                        if (code.StartsWith("3"))
                        {
                            secId = "0." + code;
                        }
                        else if (code.StartsWith("6") || code.StartsWith("0"))
                        {
                            secId = "1." + code;
                        }
                        break;
                    }
                case StockType.StockHK:
                    {
                        secId = "116." + code;
                        break;
                    }
                case StockType.StockUS:
                    {
                        secId = "105." + code;
                        break;
                    }
                default:
                    secId = "0." + code;
                    break;
            }
            return secId;
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (!_monitoring)
                return;

            if (_queue.TryDequeue(out var snap))
            {
                _currentSnapshot = snap;

                if (!CodeTextBox.Text.Contains(snap.Name))
                {
                    CodeTextBox.Text = snap.Code + " " + snap.Name;
                }

                if (string.IsNullOrEmpty(StatusText.Text))
                {
                    UpdateStatus($"Monitoring {snap.Code} {snap.Name}", System.Windows.Media.Brushes.Green);
                }

                UpdatePriceChart(snap);
                UpdateMAText(snap);

                var val = snap.ChangePercent.Value;
                ChangePercentText.Text = snap.ChangePercent.HasValue ? $"{val:F2}%" : "--%";
                ChangePercentText.Foreground = val > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
                CurrentPriceText.Text = snap.CurrentPrice.ToString();

                UpdateProfitDisplay();

                if (GetCurrentPeriod() != PeriodType.Intraday)
                {
                    CheckKdjGoldenCross(snap);
                }
                if(_monitorOnce)
                {
                    StopBtn_Click(null, null);
                    _monitorOnce = false;
                }
                else
                {
                    if(!IsTradingTime(DateTime.Now))
                    {
                        StopBtn_Click(null, null);
                    }
                }
            }
        }

        private void UpdatePriceChart(StockSnapshot snap)
        {
            if (!_monitoring)
                return;

            if(GetCurrentPeriod() == PeriodType.Intraday)
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

            var crosshair = _crosshair; // 缓存旧的十字线
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
            WpfPlotPrice.Plot.SetAxisLimits(xMin: 0, xMax: _tradingMinutes.Count - 1);

            // 使用完整的交易时间索引，而不是只使用有效数据点的索引
            var xs = Enumerable.Range(0, _tradingMinutes.Count).Select(i => (double)i).ToArray();

            // 创建完整的价格数组，包含NaN值用于没有数据的时间点
            var fullPrices = new double[_tradingMinutes.Count];
            var fullAvgPrices = new double[_tradingMinutes.Count];
            
            // 将有效价格数据填充到对应的时间索引位置
            for (int i = 0; i < snap.Prices.Length && i < _tradingMinutes.Count; i++)
            {
                fullPrices[i] = snap.Prices[i];
                fullAvgPrices[i] = snap.AvgPrices[i];
            }

            // 价格曲线 - 只绘制有效的数据点
            var validPriceIndices = new List<double>();
            var validPrices = new List<double>();
            var validAvgPrices = new List<double>();
            
            for (int i = 0; i < _tradingMinutes.Count; i++)
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
            var fullBuyVolumes = new double[_tradingMinutes.Count];
            var fullSellVolumes = new double[_tradingMinutes.Count];

            // 成交量（右Y轴）
            if (snap.BuyVolumes != null && snap.SellVolumes != null)
            {
                // 将有效成交量数据填充到对应的时间索引位置
                for (int i = 0; i < snap.BuyVolumes.Length && i < _tradingMinutes.Count; i++)
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
                barSell.FillColor = System.Drawing.Color.FromArgb(200, 0, 255, 0);
                barSell.YAxisIndex = 1;
                barSell.BarWidth = 0.5; // 设置固定柱状图宽度
                barSell.BorderLineWidth = 0; // 去掉边框
            }

            // 设置坐标轴
            var dateStr = _currentDate.ToString("yyyy-MM-dd ");
            string[] labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
            if (_stockType == StockType.StockA)
                labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
            else if(_stockType == StockType.StockHK)
                labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00", dateStr + "15:30", dateStr + "16:00" };
            else
            {
                var dateStr0 = _currentDate.AddDays(-1).ToString("yyyy-MM-dd ");
                labelTimes = new[] { dateStr0 + "21:30", dateStr0 + "22:00", dateStr0 + "22:30", dateStr0 + "23:00", dateStr0 + "23:30", dateStr + "00:00", dateStr + "00:30", dateStr + "01:00", dateStr + "01:30", dateStr + "02:00", dateStr + "02:30", dateStr + "03:00", dateStr + "03:30", dateStr + "04:00" };
            }

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
            WpfPlotPrice.Plot.YLabel("Price");
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

            if (crosshair != null)
            {
                _crosshair = WpfPlotPrice.Plot.AddCrosshair(crosshair.X, crosshair.Y);
                _crosshair.LineColor = crosshair.LineColor;
                _crosshair.LineWidth = crosshair.LineWidth;
                _crosshair.IsVisible = crosshair.IsVisible;
            }

            WpfPlotPrice.Refresh();
        }

        private void DrawKLineChart(StockSnapshot snap)
        {
            if (!_monitoring || snap.Prices == null || snap.Prices.Length == 0)
                return;

            var crosshair = _crosshair; // 缓存旧的十字线
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

            // 设置 X 轴刻度 - 使用时间轴标签
            var (ticks, labels) = GenerateTimeAxisLabels(GetCurrentPeriod(), count);
            if (ticks.Count > 0)
                WpfPlotPrice.Plot.XTicks(ticks.ToArray(), labels.ToArray());

			WpfPlotPrice.Plot.YLabel("Price");

            // --- 1.1) 计算并叠加 MA5 / MA10 / MA20 ---
            // 优先使用预计算的严格窗口均线；若为空则退回本地计算

            var ma5 = snap.MA5 ?? Tool.ComputeSimpleMovingAverage(closes, 5);
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
					if (!double.IsNaN(ma5[i])) 
                    { 
                        firstIdx = i; 
                        firstVal = ma5[i]; 
                        break; 
                    }
				}
				if (firstIdx >= 0)
				{
					for (int i = 0; i < firstIdx; i++) 
                    { 
                        xList.Add(xs[i]); 
                        yList.Add(firstVal);
                    }
					for (int i = firstIdx; i < n5; i++)
                    { 
                        if (!double.IsNaN(ma5[i])) 
                        { 
                            xList.Add(xs[i]);
                            yList.Add(ma5[i]);
                        } 
                    }
				}
				var xv = xList.ToArray(); 
                var yv = yList.ToArray();
				if (yv.Length > 1) 
                    WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Black, lineWidth: 1.0f, markerSize: 0f, label: "MA5");
			}

            var ma10 = snap.MA10 ?? Tool.ComputeSimpleMovingAverage(closes, 10);
            if (MA10.IsChecked == true)
            {
                var xList = new List<double>();
				var yList = new List<double>();
				int n10 = Math.Min(xs.Length, ma10.Length);
				int firstIdx = -1;
				double firstVal = double.NaN;
				for (int i = 0; i < n10; i++)
				{
					if (!double.IsNaN(ma10[i]))
                    { 
                        firstIdx = i; 
                        firstVal = ma10[i];
                        break; 
                    }
				}
				if (firstIdx >= 0)
				{
					for (int i = 0; i < firstIdx; i++) 
                    {
                        xList.Add(xs[i]); 
                        yList.Add(firstVal); 
                    }
					for (int i = firstIdx; i < n10; i++) 
                    { 
                        if (!double.IsNaN(ma10[i])) 
                        {
                            xList.Add(xs[i]); 
                            yList.Add(ma10[i]);
                        }
                    }
				}
				var xv = xList.ToArray(); 
                var yv = yList.ToArray();
				if (yv.Length > 1)
                    WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Orange, lineWidth: 1.0f, markerSize: 0f, label: "MA10");
			}

            var ma20 = snap.MA20 ?? Tool.ComputeSimpleMovingAverage(closes, 20);
            if(MA20.IsChecked == true)
            {
                var xList = new List<double>();
				var yList = new List<double>();
				int n20 = Math.Min(xs.Length, ma20.Length);
				int firstIdx = -1;
				double firstVal = double.NaN;
				for (int i = 0; i < n20; i++)
				{
					if (!double.IsNaN(ma20[i])) 
                    { 
                        firstIdx = i;
                        firstVal = ma20[i];
                        break;
                    }
				}
				if (firstIdx >= 0)
				{
					for (int i = 0; i < firstIdx; i++) 
                    { 
                        xList.Add(xs[i]);
                        yList.Add(firstVal);
                    }
					for (int i = firstIdx; i < n20; i++)
                    { 
                        if (!double.IsNaN(ma20[i]))
                        { 
                            xList.Add(xs[i]); 
                            yList.Add(ma20[i]);
                        }
                    }
				}
				var xv = xList.ToArray(); 
                var yv = yList.ToArray();
				if (yv.Length > 1) 
                    WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.MediumVioletRed, lineWidth: 1.0f, markerSize: 0f, label: "MA20");
			}

            var ma30 = snap.MA30 ?? Tool.ComputeSimpleMovingAverage(closes, 30);
            if (MA30.IsChecked == true)
            {
                var xList = new List<double>();
                var yList = new List<double>();
                int n30 = Math.Min(xs.Length, ma30.Length);
                int firstIdx = -1;
                double firstVal = double.NaN;
                for (int i = 0; i < n30; i++)
                {
                    if (!double.IsNaN(ma30[i])) 
                    {
                        firstIdx = i; 
                        firstVal = ma30[i]; 
                        break;
                    }
                }
                if (firstIdx >= 0)
                {
                    for (int i = 0; i < firstIdx; i++) 
                    { 
                        xList.Add(xs[i]); 
                        yList.Add(firstVal);
                    }
                    for (int i = firstIdx; i < n30; i++) 
                    { 
                        if (!double.IsNaN(ma30[i])) 
                        { 
                            xList.Add(xs[i]); 
                            yList.Add(ma30[i]);
                        }
                    }
                }
                var xv = xList.ToArray(); 
                var yv = yList.ToArray();
                if (yv.Length > 1)
                    WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Green, lineWidth: 1.0f, markerSize: 0f, label: "MA30");
            }

            var ma60 = snap.MA60 ?? Tool.ComputeSimpleMovingAverage(closes, 60);
            if (MA60.IsChecked == true)
            {
                var xList = new List<double>();
                var yList = new List<double>();
                int n60 = Math.Min(xs.Length, ma60.Length);
                int firstIdx = -1;
                double firstVal = double.NaN;
                for (int i = 0; i < n60; i++)
                {
                    if (!double.IsNaN(ma60[i])) 
                    { 
                        firstIdx = i;
                        firstVal = ma60[i]; 
                        break;
                    }
                }
                if (firstIdx >= 0)
                {
                    for (int i = 0; i < firstIdx; i++) 
                    { 
                        xList.Add(xs[i]); 
                        yList.Add(firstVal);
                    }
                    for (int i = firstIdx; i < n60; i++) 
                    { 
                        if (!double.IsNaN(ma60[i])) 
                        { 
                            xList.Add(xs[i]); 
                            yList.Add(ma60[i]);
                        }
                    }
                }
                var xv = xList.ToArray(); 
                var yv = yList.ToArray();
                if (yv.Length > 1)
                    WpfPlotPrice.Plot.AddScatter(xv, yv, color: System.Drawing.Color.Gray, lineWidth: 1.0f, markerSize: 0f, label: "MA60");
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
                for (int i = 0; i < count && i < volsScaled.Length; i++)
                {
                    tmp[i] = volsScaled[i];
                }
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

            // 为成交量图设置相同的时间轴标签
            if (ticks.Count > 0)
                WpfPlotVolume.Plot.XTicks(ticks.ToArray(), labels.ToArray());

            // 给成交量 Y 轴加一点上边距
            double maxVol = volsScaled.DefaultIfEmpty(0).Max();
            WpfPlotVolume.Plot.SetAxisLimitsY(0, Math.Max(1e-6, maxVol * 1.2)); // 提高 20%

            // 让两个图的 X 轴范围一致（关键）
            WpfPlotPrice.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);
            WpfPlotVolume.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);


            if (crosshair != null)
            {
                _crosshair = WpfPlotPrice.Plot.AddCrosshair(crosshair.X, crosshair.Y);
                _crosshair.LineColor = crosshair.LineColor;
                _crosshair.LineWidth = crosshair.LineWidth;
                _crosshair.IsVisible = crosshair.IsVisible;
            }

            // 最后渲染
            WpfPlotPrice.Render();
            WpfPlotVolume.Render();
        }

        private void UpdateProfitDisplay()
        {
            if (!double.TryParse(SharesBox.Text, out double shares)) return;
            if (!double.TryParse(CostBox.Text, out double cost)) return;
            if (!double.TryParse(ChangePercentText.Text.TrimEnd('%'), out double change)) return;
                        
            double currentPrice = double.Parse(CurrentPriceText.Text);
            double positionProfit = (currentPrice - cost) * shares;
            double todayProfit = currentPrice * change * shares / 100;

            PositionProfitText.Text = $"Total: {positionProfit:F2}";
            PositionProfitText.Foreground = positionProfit > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;

            TodayProfitText.Text = $"Today: {todayProfit:F2}";
            TodayProfitText.Foreground = todayProfit > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;

            UpdateVSStatus(CodeTextBox.Text, currentPrice, change, positionProfit, todayProfit);
        }

        private void UpdateMAText(StockSnapshot snap)
        {
            if (snap.MA5 != null)
            {
                double lastMA5Val = double.NaN;
                foreach (var ma in snap.MA5)
                {
                    if (!double.IsNaN(ma))
                    {
                        lastMA5Val = ma;
                    }
                }
                if (!double.IsNaN(lastMA5Val))
                {
                    MA5.Content = $"MA5: {lastMA5Val:F2}";
                }
            }
            if (snap.MA10 != null)
            {
                double lastMA10Val = double.NaN;
                foreach (var ma in snap.MA10)
                {
                    if (!double.IsNaN(ma))
                    {
                        lastMA10Val = ma;
                    }
                }
                if (!double.IsNaN(lastMA10Val))
                {
                    MA10.Content = $"MA10: {lastMA10Val:F2}";
                }
            }
            if (snap.MA20 != null)
            {
                double lastMA20Val = double.NaN;
                foreach (var ma in snap.MA20)
                {
                    if (!double.IsNaN(ma))
                    {
                        lastMA20Val = ma;
                    }
                }
                if (!double.IsNaN(lastMA20Val))
                {
                    MA20.Content = $"MA20: {lastMA20Val:F2}";
                }
            }
            if (snap.MA30 != null)
            {
                double lastMA30Val = double.NaN;
                foreach (var ma in snap.MA30)
                {
                    if (!double.IsNaN(ma))
                    {
                        lastMA30Val = ma;
                    }
                }
                if (!double.IsNaN(lastMA30Val))
                {
                    MA30.Content = $"MA30: {lastMA30Val:F2}";
                }
            }
            if (snap.MA60 != null)
            {
                double lastMA60Val = double.NaN;
                foreach (var ma in snap.MA60)
                {
                    if (!double.IsNaN(ma))
                    {
                        lastMA60Val = ma;
                    }
                }
                if (!double.IsNaN(lastMA60Val))
                {
                    MA60.Content = $"MA60: {lastMA60Val:F2}";
                }
            }
        }

        private void UpdateStatus(string text, System.Windows.Media.Brush color = null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = text;
                StatusText.Foreground = color ?? System.Windows.Media.Brushes.Gray;
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

        private PeriodType GetCurrentPeriod()
        {
            return (PeriodType)PeriodComboBox.SelectedIndex;
        }

        private (List<double> ticks, List<string> labels) GenerateTimeAxisLabels(PeriodType period, int dataCount)
        {
            var ticks = new List<double>();
            var labels = new List<string>();
            
            // 根据数据点数量确定标签密度
            int labelInterval = Math.Max(1, dataCount / 8); // 最多显示8个标签
            
            // 根据不同的K线周期生成时间标签
            switch (period)
            {
                case PeriodType.DailyK:
                    // 日K线：显示日期，格式为 MM/dd
                    for (int i = 0; i < dataCount; i += labelInterval)
                    {
                        // 从当前日期往前推算
                        DateTime date = _currentDate.AddDays(-(dataCount - 1 - i));
                        ticks.Add(i);
                        labels.Add(date.ToString("MM/dd"));
                    }
                    break;
                    
                case PeriodType.WeeklyK:
                    // 周K线：显示周，格式为 MM/dd
                    for (int i = 0; i < dataCount; i += labelInterval)
                    {
                        DateTime date = _currentDate.AddDays(-(dataCount - 1 - i) * 7);
                        ticks.Add(i);
                        labels.Add(date.ToString("MM/dd"));
                    }
                    break;
                    
                case PeriodType.MonthlyK:
                    // 月K线：显示月份，格式为 yyyy/MM
                    for (int i = 0; i < dataCount; i += labelInterval)
                    {
                        DateTime date = _currentDate.AddMonths(-(dataCount - 1 - i));
                        ticks.Add(i);
                        labels.Add(date.ToString("yyyy/MM"));
                    }
                    break;
                    
                case PeriodType.QuarterlyK:
                    // 季K线：显示季度，格式为 yyyy/Q
                    for (int i = 0; i < dataCount; i += labelInterval)
                    {
                        DateTime date = _currentDate.AddMonths(-(dataCount - 1 - i) * 3);
                        ticks.Add(i);
                        labels.Add($"{date.Year}/Q{((date.Month - 1) / 3) + 1}");
                    }
                    break;
                    
                case PeriodType.YearlyK:
                    // 年K线：显示年份，格式为 yyyy
                    for (int i = 0; i < dataCount; i += labelInterval)
                    {
                        DateTime date = _currentDate.AddYears(-(dataCount - 1 - i));
                        ticks.Add(i);
                        labels.Add(date.ToString("yyyy"));
                    }
                    break;                    
                default:
                    // 默认显示索引
                    for (int i = 0; i < dataCount; i += labelInterval)
                    {
                        ticks.Add(i);
                        labels.Add(i.ToString());
                    }
                    break;
            }
            
            return (ticks, labels);
        }

        private void OnKLineMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (GetCurrentPeriod() == PeriodType.Intraday) return; // 分时图不处理缩放
            
            // 获取事件来源的控件
            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl == null) return;
            
            // 获取当前X轴范围
            var xLimits = WpfPlotPrice.Plot.GetAxisLimits();
            double currentRange = xLimits.XMax - xLimits.XMin;

            // 计算鼠标位置在X轴上的比例
            System.Windows.Point mousePos = e.GetPosition(sourceControl);
            double xRatio = mousePos.X / sourceControl.ActualWidth;
            double mouseX = xLimits.XMin + xRatio * currentRange;
            
            // 缩放因子
            double zoomFactor = e.Delta > 0 ? 0.8 : 1.25;
            double newRange = currentRange * zoomFactor;
            
            // 限制缩放范围
            newRange = Math.Max(5, Math.Min(newRange, 1000));
            
            // 计算新的X轴范围，以鼠标位置为中心
            double newXMin = mouseX - (mouseX - xLimits.XMin) * (newRange / currentRange);
            double newXMax = mouseX + (xLimits.XMax - mouseX) * (newRange / currentRange);
            
            // 应用新的X轴范围到两个图表
            WpfPlotPrice.Plot.SetAxisLimits(xMin: newXMin, xMax: newXMax);
            WpfPlotVolume.Plot.SetAxisLimits(xMin: newXMin, xMax: newXMax);
            
            // 重新渲染
            WpfPlotPrice.Render();
            WpfPlotVolume.Render();
        }

        private void OnKLineMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (GetCurrentPeriod() == PeriodType.Intraday) return; // 分时图不处理拖拽
            
            // 获取事件来源的控件
            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl == null) return;
            
            _isDragging = true;
            _lastMousePosition = e.GetPosition(sourceControl);
            _dragStartX = _lastMousePosition.X;
            
            // 获取当前X轴范围
            var xLimits = WpfPlotPrice.Plot.GetAxisLimits();
            _dragStartIndex = (int)xLimits.XMin;
            
            sourceControl.CaptureMouse();
        }

        private void OnKLineMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (GetCurrentPeriod() == PeriodType.Intraday) return;
            
            // 获取事件来源的控件
            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl == null) return;
            
            _isDragging = false;
            sourceControl.ReleaseMouseCapture();
        }

        private void OnKLineMouseMove(object sender, MouseEventArgs e)
        {
            // if (!_isDragging || GetChatType() == ChartType.Intraday) return;
            if (_crosshair == null)
                return;

            // 获取事件来源的控件
            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl == null) return;

            {
                (double mouseX, double mouseY) = sourceControl.GetMouseCoordinates();

                // 限制在范围内
                if (mouseX < 0 || mouseX >= _tradingMinutes.Count)
                    return;

                // 找到最接近的点索引（四舍五入）
                int index = (int)Math.Round(mouseX);
                if (index < 0 || index >= _tradingMinutes.Count)
                    return;

                // 获取对应的价格
                double[] prices = null;
                if (index < _tradingMinutes.Count && index < _currentSnapshot?.Prices?.Length)
                    prices = _currentSnapshot.Prices;

                if (prices == null || double.IsNaN(prices[index]))
                    return;

                sourceControl.Cursor = Cursors.Cross;

                string time = _tradingMinutes[index].Split(' ')[1]; // 显示HH:mm
                var y = mouseY;// prices[index];

                // 更新十字线位置与标签
                _crosshair.IsVisible = true;
                _crosshair.X = index;
                _crosshair.Y = y;
                _crosshair.Label = $"{time} {y:F2}";
                _crosshair.HorizontalLine.PositionFormatter = v => $"{y:F2}";
                _crosshair.VerticalLine.PositionFormatter = v => $"{_tradingMinutes[index].Split(' ')[1]}";
                _crosshair.HorizontalLine.Color = System.Drawing.Color.Red;
                _crosshair.VerticalLine.Color = System.Drawing.Color.Red;

                WpfPlotPrice.Render();
            }

            System.Windows.Point currentPos = e.GetPosition(sourceControl);
            double deltaX = currentPos.X - _lastMousePosition.X;
            if (_isDragging && Math.Abs(deltaX) > 1) // 避免微小移动
            {
                // 获取当前X轴范围
                var xLimits = WpfPlotPrice.Plot.GetAxisLimits();
                double currentRange = xLimits.XMax - xLimits.XMin;
                
                // 计算移动距离对应的X轴偏移
                double xOffset = -(deltaX / sourceControl.ActualWidth) * currentRange;
                
                // 计算新的X轴范围
                double newXMin = xLimits.XMin + xOffset;
                double newXMax = xLimits.XMax + xOffset;
                
                // 应用新的X轴范围到两个图表
                WpfPlotPrice.Plot.SetAxisLimits(xMin: newXMin, xMax: newXMax);
                WpfPlotVolume.Plot.SetAxisLimits(xMin: newXMin, xMax: newXMax);
                
                // 重新渲染
                WpfPlotPrice.Render();
                WpfPlotVolume.Render();
                
                _lastMousePosition = currentPos;
            }


//             var plt = WpfPlotPrice.Plot;
//             plt.Clear();
//             plt.AddSignal(ScottPlot.DataGen.Sin(100));
//             var ch = plt.AddCrosshair(50, 0);
//             ch.IsVisible = true;
//             WpfPlotPrice.Refresh();
        }

        private void OnWpf_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_crosshair != null)
            {
                _crosshair.IsVisible = false;
                WpfPlotPrice.Refresh();
            }

            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl != null)
                sourceControl.Cursor = Cursors.Arrow;
        }

        private async Task MonitorKDJAsync(string code, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 每分钟检测一次
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    var kSnap = await FetchKSnapshot_Async(code, PeriodType.DailyK);
                    CheckKdjGoldenCross(kSnap);
                }
                catch (TaskCanceledException)
                {
                    // 正常结束
                }
                catch (Exception ex)
                {
                    UpdateStatus("KDJ check error: " + ex.Message, System.Windows.Media.Brushes.Red);
                    Logger.Error("KDJ check error: " + ex.Message);
                }
            }
        }

        private void CheckKdjGoldenCross(StockSnapshot snap)
        {
            if (snap != null && snap.Prices != null && snap.Prices.Length >= 10)
            {
                bool isGolden = Tool.HasKDJGoldenCross(snap.Prices, snap.HighPrices, snap.LowPrices);
                bool isDeath = Tool.HasKDJDeadCross(snap.Prices, snap.HighPrices, snap.LowPrices);
                var t = DateTime.Now.ToString("hh:mm:ss");

                if (isGolden)
                {
                    string str = $"*************** {t} KDJ Golden Cross signal！***************";
                    UpdateStatus(str, System.Windows.Media.Brushes.Green);
                    UpdateVSStatus(str);
                }
                else if (isDeath)
                {
                    string str = $"*************** {t} KDJ Death Cross signal！***************";
                    UpdateStatus(str, System.Windows.Media.Brushes.Red);
                    UpdateVSStatus(str);
                }
            }
        }

        public void SaveConfig()
        {
            _configManager.Config.CurrentStockType = (StockType)StockTypeComboBox.SelectedIndex;
            _configManager.Config.CurrentStock = CodeTextBox.Text.Trim();
            _configManager.Config.AutoStopOnClose = AutoStopCheckBox.IsChecked == true;
            
            int shares = 0;
            int.TryParse(SharesBox.Text, out shares);
            _configManager.Config.CurrentShares = shares;

            float cost = 0.0f;
            float.TryParse(CostBox.Text, out cost);
            _configManager.Config.CurrentCostPrices = cost;

            _configManager.Config.MA5Checked = MA5.IsChecked == true;
            _configManager.Config.MA10Checked = MA10.IsChecked == true;
            _configManager.Config.MA20Checked = MA20.IsChecked == true;
            _configManager.Config.MA30Checked = MA30.IsChecked == true;
            _configManager.Config.MA60Checked = MA60.IsChecked == true;

            _configManager.Config.WatchStockList.Clear();
            foreach (var item in CodeTextBox.Items)
            {
                _configManager.Config.WatchStockList.Add(item.ToString());
            }
            _configManager.Save();
        }

        private void TryLoadVsBrush(string vsBrushFieldName, string localResourceKey, System.Windows.Media.Color fallbackColor)
        {
            try
            {
                // 尝试在常见的两个程序集名字下寻找类型（兼容不同 VS/SDK 版本）
                Type vsBrushesType = Type.GetType("Microsoft.VisualStudio.PlatformUI.VsBrushes, Microsoft.VisualStudio.Shell.15.0")
                                    ?? Type.GetType("Microsoft.VisualStudio.PlatformUI.VsBrushes, Microsoft.VisualStudio.Shell");

                if (vsBrushesType != null)
                {
                    // 字段名例如 "ToolWindowBackgroundKey", "ToolWindowTextKey", "CommandBarGradientBeginKey" ...
                    FieldInfo fi = vsBrushesType.GetField(vsBrushFieldName, BindingFlags.Public | BindingFlags.Static);
                    if (fi != null)
                    {
                        object keyObj = fi.GetValue(null);
                        // vsBrushes 的字段通常是 ComponentResourceKey
                        if (keyObj is ComponentResourceKey compKey)
                        {
                            // TryFindResource 在不同上下文可用（Window/UserControl）。我们用 this.TryFindResource
                            var res = this.TryFindResource(compKey);
                            if (res is SolidColorBrush scb)
                            {
                                // 覆盖/设置到本控件资源字典中
                                this.Resources[localResourceKey] = scb;
                                return;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略所有异常，走 fallback
            }

            // fallback：如果读取失败，用一个简单的 SolidColorBrush 填充 XAML 中的本地 key
            this.Resources[localResourceKey] = new SolidColorBrush(fallbackColor);
        }

        private System.Windows.Media.Color ColorFromHex(string hex, double opacity = 1.0)
        {
            // hex like "#RRGGBB" or "#AARRGGBB"
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                c.A = (byte)(opacity * 255);
                return c;
            }
            catch
            {
                return Colors.Transparent;
            }
        }
    }
}
