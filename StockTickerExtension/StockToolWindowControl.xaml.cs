using EnvDTE;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using ScottPlot.Plottable;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static StockTickerExtension.UserConfig;
using static System.Net.Mime.MediaTypeNames;

namespace StockTickerExtension
{
    public partial class StockToolWindowControl : UserControl
    {
        const string s_trendsURL = "https://push2his.eastmoney.com/api/qt/stock/trends2/get";
        const string s_klineURL = "https://push2his.eastmoney.com/api/qt/stock/kline/get";

        private readonly StockToolWindow _ownerPane;
        private readonly ConfigManager _configManager = new ConfigManager();
        private readonly HttpClient _http = new HttpClient();
        private readonly ConcurrentQueue<StockSnapshot> _queue = new ConcurrentQueue<StockSnapshot>();

        private StockTokenSource _cts;
        private CancellationTokenSource _kdjCts;

        private DispatcherTimer _uiTimer;
        private List<string> _tradingMinutes;
        private BackGroundTockenSource _backgroundWatchListCts;

        private bool _monitoring = false;
        private bool _monitorOnce = false;
        private bool _isBlackTheme = false;
        private bool _isEditingCodeText = false;
        private bool _refreshNow = false;

        private DateTime _currentDate;
        private StockMarket _stockType = StockMarket.StockA;
        private StockSnapshot _currentSnapshot;
        private Crosshair _crosshair;
        private FuzzySearchDialog _fuzzySearchDialog;
        private ScottPlot.Plottable.Text _infoText;

        public StockToolWindowControl(ToolWindowPane owner)
        {
            InitializeComponent();
            _ownerPane = owner as StockToolWindow;

            Init();
            Logger.Info("StockWather initialized successed!");
        }

        public bool IsMonitoring()
        {
            return _monitoring;
        }

        private void StartBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StartMonitoring();

        private void StopBtn_Click(object sender, System.Windows.RoutedEventArgs e) => StopMonitoring();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            return;
            StopMonitoring();
            _ownerPane.ClearStatusInfo();
        }

        private void PeriodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PeriodComboBox.SelectedIndex == (int)PeriodType.Intraday)
            {
                DatePickerControl.IsEnabled = false;
                DatePickerControl.SelectedDate = DateTime.Today;

                MA5.IsEnabled = false;
                MA5.Content = "MA5: --";

                MA10.IsEnabled = false;
                MA10.Content = "MA10: --";

                MA20.IsEnabled = false;
                MA20.Content = "MA20: --";

                MA30.IsEnabled = false;
                MA30.Content = "MA30: --";

                MA60.IsEnabled = false;
                MA60.Content = "MA60: --";

                WpfPlotChart1.Plot.Clear();
                WpfPlotChart1.Height = 240;
                WpfPlotChart1.Configuration.ScrollWheelZoom = false;
                WpfPlotChart1.Configuration.LeftClickDragPan = false;

                WpfPlotChart2.Visibility = Visibility.Visible;
                WpfPlotChart2.Configuration.ScrollWheelZoom = false;
                WpfPlotChart2.Configuration.LeftClickDragPan = false;
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

                WpfPlotChart1.Plot.Clear();
                WpfPlotChart1.Height = 400;
                WpfPlotChart1.Configuration.ScrollWheelZoom = false;
                WpfPlotChart1.Configuration.LeftClickDragPan = false;

                WpfPlotChart2.Configuration.ScrollWheelZoom = false;
                WpfPlotChart2.Configuration.LeftClickDragPan = false;
                WpfPlotChart2.Visibility = Visibility.Visible;
            }
            StartMonitoring();
        }

        private void UpdateStockType(StockMarket type)
        {
            _stockType = type;
            _tradingMinutes = Tool.BuildTradingMinutes(_stockType, _currentDate);
        }

        private void Date_SelecteionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentDate = GetCurrentDate();
            _tradingMinutes = Tool.BuildTradingMinutes(_stockType, _currentDate);
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = false;
        }

        private async void CodeTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true; // 防止系统默认行为（例如“叮”声）

                    var comboBox = sender as ComboBox;
                    var text = comboBox.Text?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        UpdateStatus("Please enter a stock code.", System.Windows.Media.Brushes.Red);
                        return;
                    }
                    UpdateStatus($"Searching {text}...", _isBlackTheme ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black);

                    bool isOnlyDigit = text.All(char.IsDigit);
                    if (isOnlyDigit)
                    {
                        UpdateStockType(StockMarket.StockA);
                        StartMonitoring();
                    }
                    else
                    {
                        var idx = GetCodeTextBoxIndex(text);
                        if (idx >= 0)
                        {
                            CodeTextBox.SelectedIndex = idx;
                            StockMarket sm = StockMarket.StockA;
                            if (CodeTextBox.Text.EndsWith(StockMarket.StockHK.ToString()))
                            {
                                sm = StockMarket.StockHK;
                            }
                            else if (CodeTextBox.Text.EndsWith(StockMarket.StockUS.ToString()))
                            {
                                sm = StockMarket.StockUS;
                            }
                            else
                            {
                                sm = StockMarket.StockA;
                            }
                            UpdateStockType(sm);
                            StartMonitoring();
                            return;
                        }

                        List<StockInfo> results = await SearchStocks_Async(text);
                        if (results.Count > 0)
                        {
                            UpdateStatus($"Search result: total {results.Count} stocks!", _isBlackTheme ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black);
                            ShowFuzzyDialog(results);
                        }
                        else
                        {
                            UpdateStatus("Search result: No data!", System.Windows.Media.Brushes.Red);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CodeTextBox_KeyUp: {ex.Message}");
            }
        }

        private void CodeTextBox_DropDownClosed(object sender, EventArgs e)
        {
            var comboBox = sender as ComboBox;
            var text = comboBox.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                if (_currentSnapshot != null && text.StartsWith(_currentSnapshot.Code))
                {
                    return;
                }
                StockMarket sm = StockMarket.StockA;
                if (text.EndsWith(StockMarket.StockHK.ToString()))
                {
                    sm = StockMarket.StockHK;
                }
                else if (text.EndsWith(StockMarket.StockUS.ToString()))
                {
                    sm = StockMarket.StockUS;
                }
                else
                {
                    sm = StockMarket.StockA;
                }
                UpdateStockType(sm);
                StartMonitoring(text);
            }
        }

        private void AddBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = CodeTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                if (!CodeTextBox.Items.Contains(text))
                {
                    CodeTextBox.Items.Add(text);
                    if (_backgroundWatchListCts != null)
                    {
                        _backgroundWatchListCts._stockList.Add(text);
                    }
                }
            }
        }

        private void RemoveBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = CodeTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                CodeTextBox.Items.Remove(text);
                if (_backgroundWatchListCts != null)
                {
                    _backgroundWatchListCts._stockList.Remove(text);
                }
            }
        }
        private void MoveUpBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            int currentIndex = CodeTextBox.SelectedIndex;

            if (currentIndex <= 0) return;

            var item = CodeTextBox.Items[currentIndex];
            CodeTextBox.Items.RemoveAt(currentIndex);
            CodeTextBox.Items.Insert(currentIndex - 1, item);
            CodeTextBox.SelectedIndex = currentIndex - 1;

            if (_backgroundWatchListCts != null)
            {
                var text = CodeTextBox.Text?.Trim();
                currentIndex = _backgroundWatchListCts._stockList.IndexOf(text);
                _backgroundWatchListCts._stockList.Remove(text);
                _backgroundWatchListCts._stockList.Insert(currentIndex - 1, text);
            }
        }
        private void MoveDownBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            int currentIndex = CodeTextBox.SelectedIndex;
            if (currentIndex < 0 || currentIndex >= CodeTextBox.Items.Count - 1) return;

            var item = CodeTextBox.Items[currentIndex];
            CodeTextBox.Items.RemoveAt(currentIndex);
            CodeTextBox.Items.Insert(currentIndex + 1, item);
            CodeTextBox.SelectedIndex = currentIndex + 1;

            if (_backgroundWatchListCts != null)
            {
                var text = CodeTextBox.Text?.Trim();
                currentIndex = _backgroundWatchListCts._stockList.IndexOf(text);
                _backgroundWatchListCts._stockList.Remove(text);
                _backgroundWatchListCts._stockList.Insert(currentIndex + 1, text);
            }
        }

        private void SharesBox_PreviewInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }
        private void SharesBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_currentSnapshot != null)
            {
                if (string.IsNullOrEmpty(SharesBox.Text))
                {
                    return;
                }
                var code = _currentSnapshot.Code;

                int shares = int.Parse(SharesBox.Text);
                var item = _configManager.Config.CostList.Find(x => x.Stock == code);
                if (item != null)
                {
                    item.Shares = shares;
                }
                else
                {
                    var costItem = new CostData();
                    costItem.Stock = code;
                    costItem.Shares = shares;
                    _configManager.Config.CostList.Add(costItem);
                }                
            }
        }
        private void SharesBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _currentSnapshot != null)
            {
                if (string.IsNullOrEmpty(SharesBox.Text))
                {
                    return;
                }
                var code = _currentSnapshot.Code;
                int shares = int.Parse(SharesBox.Text);
                var item = _configManager.Config.CostList.Find(x => x.Stock == code);
                if (item != null)
                {
                    item.Shares = shares;
                }
                else
                {
                    var costItem = new CostData();
                    costItem.Stock = code;
                    costItem.Shares = shares;
                    _configManager.Config.CostList.Add(costItem);
                }
            }
        }
        
        private void SharesBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.DataObject.GetData(DataFormats.Text);
                if (!text.All(char.IsDigit) || !text.EndsWith("00"))
                {
                    e.CancelCommand();
                }
            }
        }

        private void CostBox_PreviewInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            string newText = textBox.Text.Insert(textBox.SelectionStart, e.Text);
            bool isNumeric = double.TryParse(newText, out _) || newText == "." || newText == "-";
            e.Handled = !isNumeric;
        }

        private void CostBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.DataObject.GetData(DataFormats.Text);
                Regex regex = new Regex("[^0-9.]+");
                if (regex.IsMatch(text))
                {
                    e.CancelCommand();
                }
                else if(text.Count(x => x == '.') > 1)
                {
                    e.CancelCommand();
                }
            }
        }

        private void CostBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _currentSnapshot != null)
            {
                var code = _currentSnapshot.Code;
                if (!string.IsNullOrEmpty(CostBox.Text))
                {
                    float cost = float.Parse(CostBox.Text);
                    var item = _configManager.Config.CostList.Find(x => x.Stock == code);
                    if (item != null)
                    {
                        item.CostPrice = cost;
                    }
                    else
                    {
                        var costItem = new CostData();
                        costItem.Stock = code;
                        costItem.CostPrice = cost;
                        _configManager.Config.CostList.Add(costItem);
                    }
                }
            }
        }
        private void CostBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_currentSnapshot != null)
            {
                var code = _currentSnapshot.Code;
                if (!string.IsNullOrEmpty(CostBox.Text))
                {
                    float cost = float.Parse(CostBox.Text);
                    var item = _configManager.Config.CostList.Find(x => x.Stock == code);
                    if (item != null)
                    {
                        item.CostPrice = cost;
                    }
                    else
                    {
                        var costItem = new CostData();
                        costItem.Stock = code;
                        costItem.CostPrice = cost;
                        _configManager.Config.CostList.Add(costItem);
                    }
                }
            }
        }

        private void Init()
        {
            _configManager.Load();

            string stock = _configManager.Config.CurrentStock;
            string code = stock.Split(' ')[0];
            UpdateCostShares(code);

            MA5.IsChecked = _configManager.Config.MA5Checked;
            MA10.IsChecked = _configManager.Config.MA10Checked;
            MA20.IsChecked = _configManager.Config.MA20Checked;
            MA30.IsChecked = _configManager.Config.MA30Checked;
            MA60.IsChecked = _configManager.Config.MA60Checked;

            AddBtn.Click += AddBtn_Click;
            RemoveBtn.Click += RemoveBtn_Click;
            MoveUpBtn.Click += MoveUpBtn_Click;
            MoveDownBtn.Click += MoveDownBtn_Click;
            StartBtn.Click += StartBtn_Click;
            StartBtn.Content = !Tool.IsTradingTime(_stockType, DateTime.Now) ? "Get" : "Start";
            StopBtn.Click += StopBtn_Click;
            StopBtn.IsEnabled = false;

            SharesBox.PreviewTextInput += SharesBox_PreviewInput;            
            SharesBox.KeyUp += SharesBox_KeyUp;
            SharesBox.LostFocus += SharesBox_LostFocus;
            DataObject.AddPastingHandler(SharesBox, SharesBox_Paste);

            CostBox.PreviewTextInput += CostBox_PreviewInput;
            DataObject.AddPastingHandler(CostBox, CostBox_Paste); 
            CostBox.LostFocus += CostBox_LostFocus;
            CostBox.KeyUp += CostBox_KeyUp;

            MA5.IsEnabled = false;
            MA10.IsEnabled = false;
            MA20.IsEnabled = false;
            MA30.IsEnabled = false;
            MA60.IsEnabled = false;

            this.Unloaded += OnUnloaded;
            DatePickerControl.SelectedDateChanged += Date_SelecteionChanged;

            CurrentPriceText.FontWeight = FontWeights.Bold;
            CurrentPriceText.Foreground = System.Windows.Media.Brushes.Green;

            _currentDate = GetCurrentDate();
            _tradingMinutes = Tool.BuildTradingMinutes(_stockType, _currentDate);

            InitCodeTextBox();
            InitPeriodComboBox();

            InitPriceChat();
            InitUIColor();

            if (CodeTextBox.Items.Count == 0)
            {
                UpdateStatus("Please enter or choose a stock code.", System.Windows.Media.Brushes.Red);
            }

            _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(0.1), DispatcherPriority.Normal, UiTimer_Tick, Dispatcher.CurrentDispatcher);
            _uiTimer.Stop();

            Logger.Info("StockToolWindowControl init finished");
        }

        private void InitCodeTextBox()
        {
            _currentDate = GetCurrentDate();

            CodeTextBox.KeyUp += CodeTextBox_KeyUp;
            CodeTextBox.DropDownClosed += CodeTextBox_DropDownClosed;
            CodeTextBox.GotFocus += (s, e) => { _isEditingCodeText = true; };
            CodeTextBox.LostFocus += (s, e) => { _isEditingCodeText = false; };

            foreach (var code in _configManager.Config.WatchStockList)
            {
                CodeTextBox.Items.Add(code);
            }
            CodeTextBox.Text = _configManager.Config.CurrentStock ?? string.Empty;
            StockMarket sm = StockMarket.StockA;
            if (CodeTextBox.Text.EndsWith(StockMarket.StockHK.ToString(), StringComparison.Ordinal))
            {
                sm = StockMarket.StockHK;
            }
            else if (CodeTextBox.Text.EndsWith(StockMarket.StockUS.ToString(), StringComparison.Ordinal))
            {
                sm = StockMarket.StockUS;
            }
            else
            {
                sm = StockMarket.StockA;
            }
            UpdateStockType(sm);
            Logger.Info($"InitCodeTextBox, current stock: {CodeTextBox.Text}, stock market: {sm}");
        }

        private void InitPeriodComboBox()
        {
            PeriodComboBox.Items.Add("Intraday");
            PeriodComboBox.Items.Add("Daily K");
            PeriodComboBox.Items.Add("Weekly K");
            PeriodComboBox.Items.Add("Monthly K");
            PeriodComboBox.Items.Add("Quarterly K");
            PeriodComboBox.Items.Add("Yearly K");
            PeriodComboBox.Items.Add("1 Minute");
            PeriodComboBox.Items.Add("5 Minutes");
            PeriodComboBox.Items.Add("15 Minutes");
            PeriodComboBox.Items.Add("30 Minutes");
            PeriodComboBox.Items.Add("60 Minutes");

            PeriodComboBox.SelectedIndex = 0;
            PeriodComboBox.SelectionChanged += PeriodComboBox_SelectionChanged;
        }

        private void InitPriceChat()
        {
            WpfPlotChart1.Configuration.ScrollWheelZoom = false;
            WpfPlotChart1.Configuration.LeftClickDragPan = false;
            WpfPlotChart1.Plot.SetAxisLimits(xMin: 0, xMax: _tradingMinutes.Count - 1);

            WpfPlotChart1.MouseMove += OnWpfMouseMove;
            WpfPlotChart1.MouseLeave += OnWpfMouseLeave;
            WpfPlotChart1.RightClicked -= WpfPlotChart1.DefaultRightClickEvent;

            // 初始化十字线（只创建一次）
            if (_crosshair == null)
            {
                _crosshair = WpfPlotChart1.Plot.AddCrosshair(50, 0);
                _crosshair.IsVisible = false;
                _crosshair.LineColor = System.Drawing.Color.Red;
                _crosshair.LineWidth = 1;
                _crosshair.Color = System.Drawing.Color.Red;
            }

            WpfPlotChart2.Configuration.ScrollWheelZoom = false;
            WpfPlotChart2.Configuration.LeftClickDragPan = false;
            WpfPlotChart2.RightClicked -= WpfPlotChart2.DefaultRightClickEvent;

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
                WpfPlotChart1.Plot.XTicks(ticks.ToArray(), labels.ToArray());

            WpfPlotChart2.Visibility = Visibility.Visible;
            Logger.Info("InitPriceChat finished");
        }

        private void InitUIColor()
        {
            RefreshTheme();
            this.Loaded += (s, e) =>
            {
                ApplyThemeToAllControls(this);
                VSColorTheme.ThemeChanged += _ => Dispatcher.Invoke(() =>
                {
                    RefreshTheme();
                    ApplyThemeToAllControls(this);

                    if (!IsMonitoring())
                    {
                        StartMonitoring();
                    }
                });
            };
        }

        private void RefreshTheme()
        {
            var bgColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            _isBlackTheme = Tool.isDarkTheme(bgColor.R, bgColor.G, bgColor.B);
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


            if (obj is Control ctrl)
            {
                if (ctrl.Name != "StartBtn" && ctrl.Name != "StopBtn")
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
                    if (cb.Name == "MA5")
                    {
                        cb.Foreground = new SolidColorBrush(_isBlackTheme ? Colors.White : Colors.Black);
                    }
                    else if (cb.Name == "MA10")
                    {
                        cb.Foreground = new SolidColorBrush(Colors.Orange);
                    }
                    else if (cb.Name == "MA20")
                    {
                        cb.Foreground = new SolidColorBrush(Colors.MediumVioletRed);
                    }
                    else if (cb.Name == "MA30")
                    {
                        cb.Foreground = new SolidColorBrush(Colors.Green);
                    }
                    else if (cb.Name == "MA60")
                    {
                        cb.Foreground = new SolidColorBrush(Colors.BlueViolet);
                    }
                    else
                    {
                        cb.Foreground = fgBrush;
                    }
                    if (_isBlackTheme)  //暗黑主题才调整
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
                tb.Background = bgBrush;
                tb.Foreground = fgBrush;
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
                ph.Fill = new SolidColorBrush(Colors.Gray);
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
            //             else
            {
                int count = VisualTreeHelper.GetChildrenCount(obj);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                    ApplyThemeToDatePickerChildren(child, fgBrush, bgBrush);
                }
            }
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

        private bool CheckTradingTime()
        {
            var codeName = CodeTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(codeName))
            {
                UpdateStatus("Error: Please enter or choose a stock code", System.Windows.Media.Brushes.Red);
                return false;
            }

            // ------------------ 检查交易时间 ------------------
            if (!Tool.IsTradingTime(_stockType, DateTime.Now))
            {
                // 收盘后（15:00之后）允许启动，并显示当日完整分时数据
                UpdateStatus("Currently outside trading hours", System.Windows.Media.Brushes.Red);
                return false;
            }
            return true;
        }

        private int GetCodeTextBoxIndex(string text)
        {
            foreach (var item in CodeTextBox.Items)
            {
                var list = item.ToString().Split(' ');
                foreach (var str in list)
                {
                    if (str == text)
                    {
                        return CodeTextBox.Items.IndexOf(item);
                    }
                }
                if (item.ToString() == text)
                {
                    return CodeTextBox.Items.IndexOf(item);
                }
            }
            return -1;
        }

        private void StartMonitoring(string text = "")
        {
            PeriodType period = (PeriodType)PeriodComboBox.SelectedIndex;
            if (!CheckTradingTime())
            {
                if (period == PeriodType.Intraday && DateTime.Now.TimeOfDay < new TimeSpan(9, 30, 0))
                {
                    return;
                }
                // 如果不在交易时间，则不启动监控，只获取一次数据
                _monitorOnce = true;
            }

            var codeName = string.IsNullOrEmpty(text) ? CodeTextBox.Text?.Trim() : text;
            if (codeName == null)
            {
                return;
            }

            _monitoring = true;
            _currentSnapshot = null;
            _refreshNow = true;

            var code = codeName.Split(' ')[0];

            if (_cts == null)
            {
                _cts = new StockTokenSource(code, period);
                _ = Task.Run(() => MonitorLoopAsync(_cts));
            }
            else
            {
                _cts._code = code;
                _cts._period = period;
            }

            if (!_uiTimer.IsEnabled) _uiTimer.Start();

            UpdateStatus($"{codeName} Conitoring started", _isBlackTheme ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black);

            StartBtn.IsEnabled = false;
            StartBtn.FontWeight = FontWeights.Normal;

            StopBtn.IsEnabled = true;
            StopBtn.FontWeight = FontWeights.Bold;

            StartMonitorKDJ(period, code);
            StartBackgroundWatchStockList();

            Logger.Info("Start monitoring stock: " + codeName);
        }

        private void StopMonitoring()
        {
            if (!_monitoring)
            {
                return;
            }

            Logger.Info("Stopping monitoring for stock: " + CodeTextBox.Text);

            StopBtn.IsEnabled = false;
            StopBtn.FontWeight = FontWeights.Normal;

            StartBtn.IsEnabled = true;
            StartBtn.FontWeight = FontWeights.Bold;

            _monitoring = false;

            _cts?.Cancel();
            _cts = null;

            _kdjCts?.Cancel();
            _kdjCts = null;

            _backgroundWatchListCts?.Cancel();
            _backgroundWatchListCts = null;
            OtherStocksInfo.Text = "";

            UpdateStatus($"{_currentSnapshot?.Code} {_currentSnapshot?.Name} Conitoring stopped", _isBlackTheme ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black);
            if (_uiTimer.IsEnabled)
                _uiTimer.Stop();

            Logger.Info("Monitoring stoped!");
        }

        private async Task MonitorLoopAsync(StockTokenSource cts)
        {
            if (!_monitoring)
                return;
            Logger.Info("Monitoring loop started!");

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var snap = await FetchKLinesSnapshot_Async(cts._code, cts._period);
                    if (snap != null)
                    {
                        snap.Code = cts._code;
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
                    UpdateStatus("Error:" + ex.Message, System.Windows.Media.Brushes.Red);
                    Logger.Error(ex.Message);
                }

                for (int i = 0; i < cts._fetchIntervalSeconds; i++)
                {
                    if (_refreshNow)
                    {
                        _refreshNow = false;
                        break;
                    }

                    if (cts.Token.IsCancellationRequested) break;
                    await Task.Delay(1000, cts.Token);
                }
            }
            Logger.Info("Monitoring loop stopped!");
        }

        private async Task<StockSnapshot> FetchKLinesSnapshot_Async(string code, PeriodType period)
        {
            if (period == PeriodType.Intraday)
                return await FetchTrendsSnapshot_Async(code);

            var secid = Tool.GetSecId(_stockType, code);
            if (secid == null) return null;

            var kType = Tool.PeriodToKType(period);

            var interval = Tool.GetRequestInterval(period, _currentDate);
            var parameters = $"?secid={secid}&klt={kType}&fqt=1&beg={interval.Item1}&end={interval.Item2}&fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55,f56,f57,f58";
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
            var buyVols = new double[count];
            var sellVols = new double[count];
            var highs = new double[count];
            var lows = new double[count];
            var openPrice = new double[count];
            var changePercents = new double[count];
            var kLineDates = new DateTime[count];

            for (int i = 0; i < count; i++)
            {
                var parts = klines[i].Split(',');
                var date = parts[0].ToString();
                double open = double.Parse(parts[1]);
                double close = double.Parse(parts[2]);
                double high = double.Parse(parts[3]);
                double low = double.Parse(parts[4]);
                double vol = double.Parse(parts[5]);

                kLineDates[i] = DateTime.Parse(date);
                openPrice[i] = open;
                prices[i] = close;
                highs[i] = high;
                lows[i] = low;
                avgPrices[i] = (open + close + high + low) / 4.0;
                vols[i] = vol;
            }

            for (int i = 0; i < count; i++)
            {
                if (i == 0)
                {
                    buyVols[i] = vols[i] * 0.5;
                    sellVols[i] = vols[i] * 0.5;
                }
                else
                {
                    double cur = prices[i];
                    double prev = prices[i - 1];
                    if (double.IsNaN(cur) || double.IsNaN(prev))
                    {
                        buyVols[i] = vols[i] * 0.5;
                        sellVols[i] = vols[i] * 0.5;
                    }
                    else if (cur > prev)
                    {
                        buyVols[i] = vols[i];
                        sellVols[i] = 0;
                    }
                    else if (cur < prev)
                    {
                        buyVols[i] = 0;
                        sellVols[i] = vols[i];
                    }
                    else
                    {
                        buyVols[i] = vols[i] * 0.5;
                        sellVols[i] = vols[i] * 0.5;
                    }
                }
            }

            double lastPrice = prices?.Last() ?? 0;
            changePercents[0] = 0;
            for (int i = 1; i < count; i++)
            {
                changePercents[i] = (prices[i] - prices[i - 1]) / prices[i - 1] * 100;
            }

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
                BuyVolumes = buyVols,
                SellVolumes = sellVols,
                KLineDates = kLineDates,
                CurrentPrice = lastPrice,
                ChangePercents = changePercents,
                MA5 = ma5full,
                MA10 = ma10full,
                MA20 = ma20full,
                MA30 = ma30full,
                MA60 = ma60full
            };
        }

        private async Task<StockSnapshot> FetchTrendsSnapshot_Async(string code)
        {
            var secid = Tool.GetSecId(_stockType, code);
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
                if (dataObj.Type == JTokenType.Null)
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
                    if (!double.TryParse(parts[5], out double vol)) vol = double.NaN;
                    if (!double.TryParse(parts[7], out double avg)) avg = double.NaN;
                    parsedRows.Add((time, price, vol, avg));
                }

                for (int i = 0; i < parsedRows.Count; i++)
                {
                    var r = parsedRows[i];
                    int idx = _tradingMinutes.IndexOf(r.time);
                    if (idx < 0 || idx >= _tradingMinutes.Count)
                        continue;
                    if (r.price == 0 || r.avg == 0 || r.vol == 0)
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
                var changePercents = new double[1];
                if (double.TryParse(dataObj["preClose"]?.ToString(), out double preClose))
                {
                    changePercents[0] = (lastPrice - preClose) / preClose * 100;
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
                    ChangePercents = changePercents
                };
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (!_monitoring)
                return;

            try
            {
                if (_queue.TryDequeue(out var snap))
                {
                    _currentSnapshot = snap;

                    if (!CodeTextBox.Text.StartsWith(snap.Code + " " + snap.Name))
                    {
                        if (!_isEditingCodeText || _monitorOnce)
                        {
                            CodeTextBox.Text = snap.Code + " " + snap.Name;
                        }
                    }

                    if (string.IsNullOrEmpty(StatusText.Text) || StatusText.Text.Contains("Conitoring started"))
                    {
                        UpdateStatus($"Monitoring {snap.Code} {snap.Name}", _isBlackTheme ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black);
                    }

                    UpdateChart(snap);
                    UpdateMAText(snap);
                    UpdatePricesText(snap);
                    UpdateProfitDisplay();
                    UpdateCostShares(snap.Code);

                    if (_monitorOnce)
                    {
                        StopBtn_Click(null, null);
                        _monitorOnce = false;
                    }
                    else
                    {
                        if (!Tool.IsTradingTime(_stockType, DateTime.Now))
                        {
                            StopBtn_Click(null, null);
                            //如果是下午收盘时间,则调用TakeSnapshot截图并保存
                            //if (DateTime.Now.Hour >= 14 && DateTime.Now.Minute >= 59)
                            //{
                            //    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StockWatcher", "Snapshot");
                            //    string fileName = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".png");
                            //    Tool.TakeSnapshot(this, fileName);
                            //}
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"UiTimer_Tick exception: {ex}");
            }
        }

        private async Task BackgroundWatchRun_Async(BackGroundTockenSource bgts)
        {
            Logger.Info("BackgroundWatchRun started");

            while (!bgts.Token.IsCancellationRequested && bgts._stockList != null && bgts._stockList.Count > 0)
            {
                int nCount = bgts._stockList.Count;
                bgts._curIndex = (bgts._curIndex + 1) % nCount;
                try
                {
                    var txt = bgts._stockList[bgts._curIndex].ToString();
                    var code = txt.Substring(0, txt.IndexOf(' '));
                    //if (code == _currentSnapshot?.Code)
                    //{
                    //    continue;
                    //}
                    var info = StockInfoFetcher.FetchStockInfoAsync(code, _stockType);
                    if (info != null && info.Result != null)
                    {
                        var sign = info.Result.Change >= 0 ? "↑" : "↓";
                        var color = info.Result.Change >= 0 ? Brushes.Red : Brushes.Green;

                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            OtherStocksInfo.Foreground = color;
                            OtherStocksInfo.Text = $"{info.Result.Name} {info.Result.Price:F2} " +
                                                   $"Open: {info.Result.Open:F2} High: {info.Result.High:F2} Low: {info.Result.Low:F2} {info.Result.Change:F2}% {sign}";
                            var costData = _configManager.Config.CostList.Find(x => x.Stock == code);
                            if( costData != null)
                            {
                                costData.CostTL = ((float)info.Result.Price - costData.CostPrice) * costData.Shares;
                            }
                        }));
                    }
                    for (int i = 0; i < 5 * 10; i++)
                    {
                        if (bgts.Token.IsCancellationRequested) break;
                        await Task.Delay(100, bgts.Token);
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.Error(ex.Message);
                }
            }
            Logger.Info("BackgroundWatchRun ended");
        }

        private void StartMonitorKDJ(PeriodType period, string code)
        {
            return;

            if (period == PeriodType.Intraday)
            {
                _kdjCts = new CancellationTokenSource();
                if (!_monitorOnce)
                {
                    _ = Task.Run(() => MonitorKDJAsync(code, _kdjCts.Token));
                }
            }
        }

        private void StartBackgroundWatchStockList()
        {
            if (_monitorOnce)
                return;

            if (_backgroundWatchListCts == null)
            {
                _backgroundWatchListCts = new BackGroundTockenSource();
                _backgroundWatchListCts._stockList = CodeTextBox.Items.Cast<string>().ToList();
                _ = Task.Run(() => BackgroundWatchRun_Async(_backgroundWatchListCts));
            }
            else
            {
                _backgroundWatchListCts._stockList = CodeTextBox.Items.Cast<string>().ToList();
            }
        }

        private void UpdateChart(StockSnapshot snap)
        {
            if (!_monitoring)
                return;
            try
            {
                DrawChart1(snap);
                DrawChart2(snap);
            }
            catch (Exception ex)
            {
                Logger.Error($"UpdateChart exception: {ex}");
            }
        }

        private void DrawChart1(StockSnapshot snap)
        {
            var period = GetCurrentPeriod();
            if (period == PeriodType.Intraday)
            {
                if (!_monitoring || snap.Prices == null || snap.Prices.Length == 0 || snap.Volumes == null || snap.Volumes.Length == 0)
                    return;

                var crosshair = _crosshair; // 缓存旧的十字线
                WpfPlotChart1.Plot.Clear();

                // 设置X轴
                var dateStr = _currentDate.ToString("yyyy-MM-dd ");
                string[] labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
                if (_stockType == StockMarket.StockA)
                    labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
                else if (_stockType == StockMarket.StockHK)
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
                WpfPlotChart1.Plot.XTicks(ticks.ToArray(), labels.ToArray());

                WpfPlotChart1.Plot.YLabel("Price");
                WpfPlotChart1.Plot.YAxis2.Label("Volume (Lots)");

                // 设置右轴显示
                WpfPlotChart1.Plot.YAxis2.Ticks(true);
                WpfPlotChart1.Plot.YAxis2.Color(System.Drawing.Color.Gray);

                // 自动缩放
                WpfPlotChart1.Plot.AxisAuto(horizontalMargin: 0, verticalMargin: 0);

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
                WpfPlotChart1.Plot.SetAxisLimits(xMin: 0, xMax: _tradingMinutes.Count - 1);

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
                    WpfPlotChart1.Plot.AddScatter(validPriceIndices.ToArray(), validPrices.ToArray(), color: System.Drawing.Color.FromArgb(31, 119, 180), lineWidth: 2.0f, markerSize: 2.2f);
                    WpfPlotChart1.Plot.AddScatter(validPriceIndices.ToArray(), validAvgPrices.ToArray(), color: System.Drawing.Color.FromArgb(255, 127, 14), lineWidth: 2.0f, markerSize: 2.2f);
                }

                // 使用完整的交易时间索引，而不是只使用有效数据点的索引
                var xs = Enumerable.Range(0, _tradingMinutes.Count).Select(i => (double)i).ToArray();

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

                    var barBuy = WpfPlotChart1.Plot.AddBar(fullBuyVolumes, xs);
                    barBuy.FillColor = System.Drawing.Color.Red;
                    barBuy.YAxisIndex = 1; // 使用右Y轴
                    barBuy.BarWidth = 0.5; // 设置固定柱状图宽度
                    barBuy.BorderLineWidth = 0; // 去掉边框

                    var barSell = WpfPlotChart1.Plot.AddBar(fullSellVolumes, xs);
                    barSell.FillColor = System.Drawing.Color.Green;
                    barSell.YAxisIndex = 1;
                    barSell.BarWidth = 0.5; // 设置固定柱状图宽度
                    barSell.BorderLineWidth = 0; // 去掉边框
                }

                // ------------------ 价格轴（左Y轴）留20%空间 ------------------
                double maxPrice = 0, minPrice = 0;
                if (validPrices.Count > 0)
                {
                    maxPrice = Math.Max(validPrices.Max(), validAvgPrices.Max());
                    minPrice = Math.Min(validPrices.Min(), validAvgPrices.Min());
                }

                // 上下各留出10%的空间（总共扩大20%）
                double priceRange = maxPrice - minPrice;
                WpfPlotChart1.Plot.SetAxisLimitsY(minPrice - priceRange * 0.1, maxPrice + priceRange * 0.1 + 0.01, yAxisIndex: 0);
                WpfPlotChart1.Plot.YAxis.TickLabelFormat("F2", false);

                // 调整右侧成交量轴范围
                double maxVolume = Math.Max(fullBuyVolumes.DefaultIfEmpty(0).Max(),
                                            fullSellVolumes.DefaultIfEmpty(0).Max());
                WpfPlotChart1.Plot.SetAxisLimitsY(0, maxVolume * 1.3 + 0.01, yAxisIndex: 1); // 上限提高20%

                DrawCostLine();

                if (crosshair != null)
                {
                    _crosshair = WpfPlotChart1.Plot.AddCrosshair(crosshair.X, crosshair.Y);
                    _crosshair.LineColor = crosshair.LineColor;
                    _crosshair.LineWidth = crosshair.LineWidth;
                    _crosshair.IsVisible = crosshair.IsVisible;
                }

                WpfPlotChart1.Render();
            }
            else
            {
                if (!_monitoring || snap == null || snap.Prices == null || snap.Prices.Length == 0 || snap.KLineDates == null || snap.KLineDates.Length == 0)
                    return;

                var crosshair = _crosshair;

                WpfPlotChart1.Plot.Clear();
                WpfPlotChart1.Plot.YAxis2.Ticks(false);
                WpfPlotChart1.Plot.YAxis2.Label("");

                int count = snap.Prices.Length;
                var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();

                // --- 1) 绘制 K 线（使用 ScottPlot 的 Candlesticks） ---
                var opens = snap.OpenPrice ?? Enumerable.Repeat(double.NaN, count).ToArray();
                var closes = snap.Prices;
                var highs = snap.HighPrices ?? Enumerable.Repeat(double.NaN, count).ToArray();
                var lows = snap.LowPrices ?? Enumerable.Repeat(double.NaN, count).ToArray();

                // 添加蜡烛图
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
                var candles = WpfPlotChart1.Plot.AddCandlesticks(ohlcs.ToArray());
                candles.ColorUp = System.Drawing.Color.Red;
                candles.ColorDown = System.Drawing.Color.Green;

                // X 轴对齐：使每个 candle 在整数位置（0..count-1）居中
                double xMin = -0.5;
                double xMax = count - 0.5;
                WpfPlotChart1.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);

                // 设置 X 轴刻度 - 使用时间轴标签
                var (ticks, labels) = Tool.GenerateTimeAxisLabels(GetCurrentPeriod(), snap.KLineDates, GetCurrentDate());
                WpfPlotChart1.Plot.XTicks(ticks.ToArray(), labels.ToArray());
                WpfPlotChart1.Plot.YLabel("Price");

                DrawAVGLines(snap);
                DrawCostLine();
                
                if (crosshair != null)
                {
                    _crosshair = WpfPlotChart1.Plot.AddCrosshair(crosshair.X, crosshair.Y);
                    _crosshair.LineColor = crosshair.LineColor;
                    _crosshair.LineWidth = crosshair.LineWidth;
                    _crosshair.IsVisible = crosshair.IsVisible;
                }

                // 最后渲染
                WpfPlotChart1.Render();
            }
        }

        private void DrawChart2(StockSnapshot snap)
        {
            var period = GetCurrentPeriod();
            if (period == PeriodType.Intraday)
            {
                if (!_monitoring || snap.Prices == null || snap.Prices.Length == 0 || snap.Volumes == null || snap.Volumes.Length == 0)
                    return;

                WpfPlotChart2.Plot.Clear();

                // 绘制MACD曲线
                WpfPlotChart2.Plot.SetAxisLimits(xMin: 0, xMax: _tradingMinutes.Count - 1);
                WpfPlotChart2.Plot.YLabel("MACD");
                WpfPlotChart2.Plot.AxisAuto(horizontalMargin: 0, verticalMargin: 0);

                // 设置X轴
                var dateStr = _currentDate.ToString("yyyy-MM-dd ");
                string[] labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
                if (_stockType == StockMarket.StockA)
                    labelTimes = new[] { dateStr + "09:30", dateStr + "10:00", dateStr + "10:30", dateStr + "11:00", dateStr + "11:30", dateStr + "13:30", dateStr + "14:00", dateStr + "14:30", dateStr + "15:00" };
                else if (_stockType == StockMarket.StockHK)
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
                WpfPlotChart2.Plot.XTicks(ticks.ToArray(), labels.ToArray());

                var macdItems = Tool.CalcMacd(snap.Prices.ToList());
                var difList = macdItems.Select(item => item.Dif).ToList();
                var deaList = macdItems.Select(item => item.Dea).ToList();
                var macdList = macdItems.Select(item => item.Macd).ToList();

                // 价格曲线 - 只绘制有效的数据点
                var validPriceIndices = new List<double>();
                for (int i = 0; i < _tradingMinutes.Count; i++)
                {
                    if (!double.IsNaN(snap.Prices[i]))
                    {
                        validPriceIndices.Add(i);
                    }
                }
                if (validPriceIndices.Count == 0)
                {
                    return;
                }

                WpfPlotChart2.Plot.AddScatter(validPriceIndices.ToArray(), difList.ToArray(), color: _isBlackTheme ? System.Drawing.Color.White : System.Drawing.Color.Black, lineWidth: 1.8f, markerSize: 0.0f);
                WpfPlotChart2.Plot.AddScatter(validPriceIndices.ToArray(), deaList.ToArray(), color: System.Drawing.Color.FromArgb(255, 127, 14), lineWidth: 1.8f, markerSize: 0.0f);

                double maxMacd = difList.Max();
                double minMacd = difList.Min();
                double macdRange = maxMacd - minMacd;
                WpfPlotChart2.Plot.SetAxisLimitsY(minMacd - macdRange * 0.1, maxMacd + macdRange * 0.1 + 0.01, yAxisIndex: 0);
                WpfPlotChart2.Plot.YAxis.TickLabelFormat("F2", false);
                WpfPlotChart2.Plot.YAxis2.Label("   ");
                WpfPlotChart2.Plot.YAxis2.TickLabelFormat("F2", false);
                WpfPlotChart2.Plot.SetAxisLimitsY(minMacd - macdRange * 0.1, maxMacd + macdRange * 0.1 + 0.01, yAxisIndex: 1);

                // 设置右轴显示，仅仅是为了使上下对齐一点点
                WpfPlotChart2.Plot.YAxis2.Ticks(true);
                WpfPlotChart2.Plot.YAxis2.Color(System.Drawing.Color.Gray);

                var fullBuyMacds = new double[macdList.Count];
                var fullSellMacds = new double[macdList.Count];
                {
                    // 将有效成交量数据填充到对应的时间索引位置
                    for (int i = 0; i < macdList.Count; i++)
                    {
                        if (macdList[i] >= 0)
                        {
                            fullBuyMacds[i] = macdList[i];
                            fullSellMacds[i] = 0;
                        }
                        else
                        {
                            fullBuyMacds[i] = 0;
                            fullSellMacds[i] = macdList[i];
                        }
                    }

                    var xs = Enumerable.Range(0, macdList.Count).Select(i => (double)i).ToArray();

                    var buyMacdBar = WpfPlotChart2.Plot.AddBar(fullBuyMacds, xs);
                    buyMacdBar.FillColor = System.Drawing.Color.Red;
                    buyMacdBar.FillColorNegative = System.Drawing.Color.Red;
                    buyMacdBar.YAxisIndex = 0;
                    buyMacdBar.BarWidth = 0.5;
                    buyMacdBar.BorderLineWidth = 0;

                    var sellMacdBar = WpfPlotChart2.Plot.AddBar(fullSellMacds, xs);
                    sellMacdBar.FillColor = System.Drawing.Color.Green;
                    sellMacdBar.FillColorNegative = System.Drawing.Color.Green;
                    sellMacdBar.YAxisIndex = 0;
                    sellMacdBar.BarWidth = 0.5;
                    sellMacdBar.BorderLineWidth = 0;
                }
                WpfPlotChart2.Render();
            }
            else if (period >= PeriodType.Minute1)
            {
                if (!_monitoring || snap == null || snap.Prices == null || snap.Prices.Length == 0 || snap.KLineDates == null)
                    return;

                WpfPlotChart2.Plot.Clear();

                int count = snap.Prices.Length;

                // 绘制MACD曲线
                WpfPlotChart2.Plot.SetAxisLimits(xMin: 0, xMax: count - 1);
                WpfPlotChart2.Plot.YLabel("MACD");
                WpfPlotChart2.Plot.AxisAuto(horizontalMargin: 0, verticalMargin: 0);

                var (ticks, labels) = Tool.GenerateTimeAxisLabels(period, snap.KLineDates, GetCurrentDate());
                WpfPlotChart2.Plot.XTicks(ticks.ToArray(), labels.ToArray());

                var macdItems = Tool.CalcMacd(snap.Prices.ToList());
                var difList = macdItems.Select(item => item.Dif).ToList();
                var deaList = macdItems.Select(item => item.Dea).ToList();
                var macdList = macdItems.Select(item => item.Macd).ToList();

                // 价格曲线 - 只绘制有效的数据点
                var validPriceIndices = new List<double>();
                for (int i = 0; i < count; i++)
                {
                    if (!double.IsNaN(snap.Prices[i]))
                    {
                        validPriceIndices.Add(i);
                    }
                }
                WpfPlotChart2.Plot.AddScatter(validPriceIndices.ToArray(), difList.ToArray(), color: _isBlackTheme ? System.Drawing.Color.White : System.Drawing.Color.Black, lineWidth: 1.8f, markerSize: 0.0f);
                WpfPlotChart2.Plot.AddScatter(validPriceIndices.ToArray(), deaList.ToArray(), color: System.Drawing.Color.FromArgb(255, 127, 14), lineWidth: 1.8f, markerSize: 0.0f);

                double maxMacd = difList.Max();
                double minMacd = difList.Min();
                double macdRange = maxMacd - minMacd;
                WpfPlotChart2.Plot.SetAxisLimitsY(minMacd - macdRange * 0.1, maxMacd + macdRange * 0.1 + 0.01, yAxisIndex: 0);
                WpfPlotChart2.Plot.YAxis.TickLabelFormat("F2", false);
                WpfPlotChart2.Plot.YAxis2.Ticks(false);

                var fullBuyMacds = new double[macdList.Count];
                var fullSellMacds = new double[macdList.Count];
                // 将有效成交量数据填充到对应的时间索引位置
                for (int i = 0; i < macdList.Count; i++)
                {
                    if (macdList[i] >= 0)
                    {
                        fullBuyMacds[i] = macdList[i];
                        fullSellMacds[i] = 0;
                    }
                    else
                    {
                        fullBuyMacds[i] = 0;
                        fullSellMacds[i] = macdList[i];
                    }
                }

                var xs = Enumerable.Range(0, macdList.Count).Select(i => (double)i).ToArray();

                var buyMacdBar = WpfPlotChart2.Plot.AddBar(fullBuyMacds, xs);
                buyMacdBar.FillColor = System.Drawing.Color.Red;
                buyMacdBar.FillColorNegative = System.Drawing.Color.Red;
                buyMacdBar.YAxisIndex = 0;
                buyMacdBar.BarWidth = 0.5;
                buyMacdBar.BorderLineWidth = 0;

                var sellMacdBar = WpfPlotChart2.Plot.AddBar(fullSellMacds, xs);
                sellMacdBar.FillColor = System.Drawing.Color.Green;
                sellMacdBar.FillColorNegative = System.Drawing.Color.Green;
                sellMacdBar.YAxisIndex = 0;
                sellMacdBar.BarWidth = 0.5;
                sellMacdBar.BorderLineWidth = 0;

                WpfPlotChart2.Render();
            }
            else
            {
                if (!_monitoring || snap == null || snap.Prices == null || snap.Prices.Length == 0 || snap.KLineDates == null || snap.KLineDates.Length == 0)
                    return;

                WpfPlotChart2.Plot.AxisAuto(horizontalMargin: 0, verticalMargin: 0);

                var (ticks, labels) = Tool.GenerateTimeAxisLabels(period, snap.KLineDates, GetCurrentDate());
                WpfPlotChart2.Plot.XTicks(ticks.ToArray(), labels.ToArray());

                int count = snap.Prices.Length;

                double xMin = -0.5;
                double xMax = count - 0.5;

                // --- 2) 绘制成交量到下方 WpfPlotVolume 并对齐 X 轴 ---
                WpfPlotChart2.Plot.Clear();
                WpfPlotChart2.Plot.SetAxisLimits(xMin: xMin, xMax: xMax);
                WpfPlotChart2.Visibility = Visibility.Visible;
                WpfPlotChart2.Plot.YAxis.TickLabelFormat("", false);
                WpfPlotChart2.Plot.YAxis2.TickLabelFormat("", false);
                WpfPlotChart2.Plot.YAxis2.Ticks(false);
                WpfPlotChart2.Plot.YAxis2.Label("");

                double[] volsScaled = snap.Volumes?.Select(v => v / 100).ToArray() ?? new double[count];
                // 为成交量设置颜色：用买/卖分开绘制（若有），否则按涨跌绘色
                if (snap.BuyVolumes != null && snap.SellVolumes != null)
                {
                    var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();

                    // 使用 buy/sell 绘制两组柱
                    var buyScaled = snap.BuyVolumes.Select(v => v / 100).ToArray();
                    var buyBar = WpfPlotChart2.Plot.AddBar(buyScaled, xs);
                    buyBar.FillColor = System.Drawing.Color.Red;
                    buyBar.BarWidth = 0.5;
                    buyBar.BorderLineWidth = 0;

                    var sellScaled = snap.SellVolumes.Select(v => v / 100).ToArray();
                    var sellBar = WpfPlotChart2.Plot.AddBar(sellScaled, xs);
                    sellBar.FillColor = System.Drawing.Color.Green;
                    sellBar.BarWidth = 0.5;
                    sellBar.BorderLineWidth = 0;
                }

                // 给成交量 Y 轴加一点上边距
                double maxVol = volsScaled.DefaultIfEmpty(0).Max();
                WpfPlotChart2.Plot.SetAxisLimitsY(0, Math.Max(1e-6, maxVol * 1.2)); // 提高 20%                                                                                
                WpfPlotChart2.Plot.YLabel("Volume (Lots)");
                WpfPlotChart2.Render();
            }
        }

        private void DrawAVGLines(StockSnapshot snap)
        {
            int count = snap.Prices.Length;
            var highs = snap.HighPrices ?? Enumerable.Repeat(double.NaN, count).ToArray();
            var lows = snap.LowPrices ?? Enumerable.Repeat(double.NaN, count).ToArray();

            {
                var ma5 = DrawMA5Line(snap);
                var ma10 = DrawMA10Line(snap);
                var ma20 = DrawMA20Line(snap);
                var ma30 = DrawMA30Line(snap);
                var ma60 = DrawMA60Line(snap);

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
                    WpfPlotChart1.Plot.SetAxisLimitsY(yLow - margin, yHigh + margin);
                }
                else
                {
                    // fallback
                    WpfPlotChart1.Plot.AxisAuto();
                }
            }
        }

        private void DrawCostLine()
        {
            if (!string.IsNullOrEmpty(CostBox.Text))
            {
                float cost = float.Parse(CostBox.Text);
                var hLine = WpfPlotChart1.Plot.AddHorizontalLine(cost);
                // 设置样式
                hLine.LineStyle = ScottPlot.LineStyle.DashDotDot;
                hLine.Color = _isBlackTheme ? System.Drawing.Color.White : System.Drawing.Color.Black;
                hLine.LineWidth = 1;
                // 可选：添加标签
                //hLine.Label = "成本线";
            }
        }

        private double[] DrawMA5Line(StockSnapshot snap)
        {
            int count = snap.Prices.Length;
            var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            var closes = snap.Prices;

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
                {
                    var brush = MA5.Foreground as SolidColorBrush;
                    var ma5Color = System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                    WpfPlotChart1.Plot.AddScatter(xv, yv, color: ma5Color, lineWidth: 1.0f, markerSize: 0f, label: "MA5");
                }
            }
            return ma5;
        }

        private double[] DrawMA10Line(StockSnapshot snap)
        {
            int count = snap.Prices.Length;
            var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            var closes = snap.Prices;

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
                {
                    var brush = MA10.Foreground as SolidColorBrush;
                    var ma10Color = System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                    WpfPlotChart1.Plot.AddScatter(xv, yv, color: ma10Color, lineWidth: 1.0f, markerSize: 0f, label: "MA10");
                }
            }
            return ma10;
        }

        private double[] DrawMA20Line(StockSnapshot snap)
        {
            int count = snap.Prices.Length;
            var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            var closes = snap.Prices;

            var ma20 = snap.MA20 ?? Tool.ComputeSimpleMovingAverage(closes, 20);
            if (MA20.IsChecked == true)
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
                {
                    var brush = MA20.Foreground as SolidColorBrush;
                    var ma20Color = System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                    WpfPlotChart1.Plot.AddScatter(xv, yv, color: ma20Color, lineWidth: 1.0f, markerSize: 0f, label: "MA20");
                }
            }
            return ma20;
        }

        private double[] DrawMA30Line(StockSnapshot snap)
        {
            int count = snap.Prices.Length;
            var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            var closes = snap.Prices;

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
                {
                    var brush = MA30.Foreground as SolidColorBrush;
                    var ma30Color = System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                    WpfPlotChart1.Plot.AddScatter(xv, yv, ma30Color, lineWidth: 1.0f, markerSize: 0f, label: "MA30");
                }
            }
            return ma30;
        }

        private double[] DrawMA60Line(StockSnapshot snap)
        {
            int count = snap.Prices.Length;
            var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
            var closes = snap.Prices;

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
                {
                    var brush = MA60.Foreground as SolidColorBrush;
                    var ma60Color = System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                    WpfPlotChart1.Plot.AddScatter(xv, yv, color: ma60Color, lineWidth: 1.0f, markerSize: 0f, label: "MA60");
                }
            }
            return ma60;
        }

        private void UpdateProfitDisplay()
        {
            if (!float.TryParse(SharesBox.Text, out float shares)) return;
            if (!float.TryParse(CostBox.Text, out float cost)) return;
            if (!float.TryParse(ChangePercentText.Text.TrimEnd('%'), out float change)) return;

            float currentPrice = float.Parse(CurrentPriceText.Text);
            float positionProfit = (currentPrice - cost) * (float)shares;
            float todayProfit = (float)(currentPrice * change * shares) / (float)100.0;

            PositionProfitText.Text = $"Total: {positionProfit:F2}";
            PositionProfitText.Foreground = positionProfit > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
            TodayProfitText.Text = $"Today: {todayProfit:F2}";
            TodayProfitText.Foreground = todayProfit > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;

            float totalPL = 0;
            foreach(var stockCost in _configManager.Config.CostList)
            {
                totalPL += stockCost.CostTL;
            }
            TotalPLText.Text = $"Total P/L: {totalPL:F2}";
            TotalPLText.Foreground = totalPL > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;

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

        private void UpdatePricesText(StockSnapshot snap)
        {
            var val = snap.ChangePercents?.Last() ?? 0;
            var foreground = val > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;

            CurrentPriceText.Text = snap.CurrentPrice.ToString("F2");
            CurrentPriceText.Foreground = foreground;

            ChangePercentText.Text = val != 0 ? $"{val:F2}%" : "--%";
            ChangePercentText.Foreground = foreground;

            if (GetCurrentPeriod() == PeriodType.Intraday)
            {
                var prices = snap.Prices.Where(p => !double.IsNaN(p)).ToArray();
                if (prices != null && prices.Length > 0)
                {
                    OpenPriceText.Text = prices?.First().ToString("F2") ?? "";
                    HighestPriceText.Text = prices?.Max().ToString("F2") ?? "";
                    LowestPriceText.Text = prices?.Min().ToString("F2") ?? "";
                }
            }
            else
            {
                OpenPriceText.Text = snap.OpenPrice?.Last().ToString("F2") ?? "";
                HighestPriceText.Text = snap.HighPrices?.Last().ToString("F2") ?? "";
                LowestPriceText.Text = snap.LowPrices?.Last().ToString("F2") ?? "";
            }            
        }

        private void UpdateStatus(string text, System.Windows.Media.Brush color = null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
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

        private void OnKLineMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (GetCurrentPeriod() == PeriodType.Intraday) return; // 分时图不处理缩放

            // 获取事件来源的控件
            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl == null) return;

            // 获取当前X轴范围
            var xLimits = WpfPlotChart1.Plot.GetAxisLimits();
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
            WpfPlotChart1.Plot.SetAxisLimits(xMin: newXMin, xMax: newXMax);
            WpfPlotChart2.Plot.SetAxisLimits(xMin: newXMin, xMax: newXMax);

            // 重新渲染
            WpfPlotChart1.Render();
            WpfPlotChart2.Render();
        }

        private void OnWpfMouseMove(object sender, MouseEventArgs e)
        {
            // 获取事件来源的控件
            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl == null) return;

            if (_crosshair != null && _currentSnapshot != null)
            {
                (double mouseX, double mouseY) = sourceControl.GetMouseCoordinates();

                var xTicksLen = GetCurrentPeriod() == PeriodType.Intraday ? (_currentSnapshot.Prices?.Length ?? 0) : (_currentSnapshot.KLineDates?.Length ?? 0);

                // 限制在范围内
                if (mouseX < 0 || mouseX >= xTicksLen)
                    return;

                // 找到最接近的点索引（四舍五入）
                int index = (int)Math.Round(mouseX);
                if (index < 0 || index >= xTicksLen)
                    return;

                // 获取对应的价格
                double[] prices = null;
                if (index < xTicksLen && index < _currentSnapshot?.Prices?.Length)
                    prices = _currentSnapshot.Prices;

                if (prices == null || double.IsNaN(prices[index]))
                    return;

                sourceControl.Cursor = Cursors.Cross;

                string time = "";
                string labelText = "";
                if (GetCurrentPeriod() == PeriodType.Intraday)
                {
                    time = _tradingMinutes[index].Split(' ')[1]; // 显示HH:mm
                    labelText = $"Price:{_currentSnapshot.Prices[index]} \r\nAvgPrice:{_currentSnapshot.AvgPrices[index]}";
                }
                else
                {
                    if (GetCurrentPeriod() == PeriodType.DailyK || GetCurrentPeriod() == PeriodType.WeeklyK)
                        time = _currentSnapshot.KLineDates[index].ToString("yyyy-MM-dd");
                    else if ((int)GetCurrentPeriod() >= (int)PeriodType.Minute1 && (int)GetCurrentPeriod() <= (int)PeriodType.Minute60)
                    {
                        time = _currentSnapshot.KLineDates[index].ToString("MM/dd HH:mm");

                    }
                    else
                        time = _currentSnapshot.KLineDates[index].ToString("yyyy-MM");

                    var val = _currentSnapshot.ChangePercents != null ? _currentSnapshot.ChangePercents[index] : 0;
                    var open = _currentSnapshot.OpenPrice[index];
                    var close = _currentSnapshot.Prices[index];
                    var high = _currentSnapshot.HighPrices[index];
                    var low = _currentSnapshot.LowPrices[index];

                    labelText = $"Open: {open:F2} \r\n" +
                                $"Close: {close:F2} \r\n" +
                                $"High: {high:F2} \r\n" +
                                $"Low: {low:F2} \r\n" +
                                $"Change:{val:F2}%";

                    OpenPriceText.Text = open.ToString("F2");
                    CurrentPriceText.Text = close.ToString("F2");
                    HighestPriceText.Text = high.ToString("F2");
                    LowestPriceText.Text = low.ToString("F2");
                    ChangePercentText.Text = $"{val:F2}%";
                    var foreground = val > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
                    ChangePercentText.Foreground = foreground;
                }
                if (_infoText != null)
                {
                    WpfPlotChart1.Plot.Remove(_infoText);
                }

                var posX = index;
                if (posX > xTicksLen - 20)
                {
                    posX = posX - 20;
                }
                _infoText = WpfPlotChart1.Plot.AddText(labelText, posX + 1, _crosshair.Y, color: _isBlackTheme ? System.Drawing.Color.White : System.Drawing.Color.Blue);
                _infoText.Alignment = ScottPlot.Alignment.UpperLeft;
                _infoText.Font.Size = 14;
                _infoText.Font.Bold = true;
                _infoText.BackgroundColor = System.Drawing.Color.Gray;

                var y = mouseY;// prices[index];

                // 更新十字线位置与标签
                _crosshair.IsVisible = true;
                _crosshair.X = index;
                _crosshair.Y = y;
                _crosshair.Label = labelText;
                _crosshair.HorizontalLine.PositionFormatter = v => $"{y:F2}";
                _crosshair.VerticalLine.PositionFormatter = v => $"{time}";
                _crosshair.HorizontalLine.Color = System.Drawing.Color.Red;
                _crosshair.VerticalLine.Color = System.Drawing.Color.Red;

                WpfPlotChart1.Render();
            }
        }

        private void OnWpfMouseLeave(object sender, MouseEventArgs e)
        {
            if (_crosshair != null)
            {
                if (_infoText != null)
                {
                    WpfPlotChart1.Plot.Remove(_infoText);
                }
                _crosshair.IsVisible = false;
                WpfPlotChart1.Refresh();
            }

            var sourceControl = sender as ScottPlot.WpfPlot;
            if (sourceControl != null)
                sourceControl.Cursor = Cursors.Arrow;

            if (_currentSnapshot != null)
            {
                if (GetCurrentPeriod() == PeriodType.Intraday)
                {
                    var prices = _currentSnapshot.Prices.Where(p => !double.IsNaN(p)).ToArray();
                    if (prices != null && prices.Length > 0)
                    {
                        OpenPriceText.Text = prices?.First().ToString() ?? "";
                        HighestPriceText.Text = prices?.Max().ToString() ?? "";
                        LowestPriceText.Text = prices?.Min().ToString() ?? "";
                    }

                    var val = _currentSnapshot.ChangePercents?.Last() ?? 0;
                    ChangePercentText.Text = $"{val:F2}%"; //$"{val: F2}%";
                    var foreground = val > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
                    ChangePercentText.Foreground = foreground;
                }
                else
                {
                    double open = _currentSnapshot.OpenPrice?.LastOrDefault() ?? 0;
                    double high = _currentSnapshot.HighPrices?.LastOrDefault() ?? 0;
                    double low = _currentSnapshot.LowPrices?.LastOrDefault() ?? 0;
                    double val = _currentSnapshot.ChangePercents?.LastOrDefault() ?? 0;

                    OpenPriceText.Text = open.ToString();
                    HighestPriceText.Text = high.ToString();
                    LowestPriceText.Text = low.ToString();

                    ChangePercentText.Text = $"{val:F2}%";
                    var foreground = val > 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
                    ChangePercentText.Foreground = foreground;
                }
            }
        }

        private async Task MonitorKDJAsync(string code, CancellationToken token)
        {
            Logger.Info("Start KDJ monitor for " + code);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 每10分钟检测一次
                    await Task.Delay(TimeSpan.FromMinutes(10), token);
                    var kSnap = await FetchKLinesSnapshot_Async(code, PeriodType.DailyK);
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
            Logger.Info("Stop KDJ monitor for " + code);
        }

        private void CheckKdjGoldenCross(StockSnapshot snap)
        {
            if (snap != null && snap.Prices != null && snap.Prices.Length >= 10)
            {
                bool isGolden = Tool.HasKDJGoldenCross(snap.Prices, snap.HighPrices, snap.LowPrices);
                bool isDeath = Tool.HasKDJDeadCross(snap.Prices, snap.HighPrices, snap.LowPrices);
                var t = DateTime.Now.ToString("HH:mm:ss");

                if (isGolden)
                {
                    string str = $"*************** {snap.Code} {snap.Name} {t} KDJ Golden Cross signal！***************";
                    UpdateStatus(str, System.Windows.Media.Brushes.Red);
                    UpdateVSStatus(str);
                }
                else if (isDeath)
                {
                    string str = $"*************** {snap.Code} {snap.Name} {t} KDJ Death Cross signal！***************";
                    UpdateStatus(str, System.Windows.Media.Brushes.Green);
                    UpdateVSStatus(str);
                }
            }
        }

        public void SaveConfig()
        {
            _configManager.Config.CurrentStock = CodeTextBox.Text.Trim();

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

        public void UpdateCostShares(string stockCode)
        {
            var item = _configManager.Config.CostList.Find(x => x.Stock == stockCode);
            if (item != null)
            {
                SharesBox.Text = item.Shares.ToString();
                CostBox.Text = item.CostPrice.ToString();
            }
            else
            {
                SharesBox.Text = "0";
                CostBox.Text = "0";
            }
        }

        private async Task<List<StockInfo>> SearchStocks_Async(string keyword)
        {
            var list = new List<StockInfo>();

            // 东方财富搜索接口
            string url = $"https://searchapi.eastmoney.com/api/suggest/get?input={keyword}&type=14&count=100";

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            using (var resp = await client.GetAsync(url))
            {
                if (!resp.IsSuccessStatusCode)
                    return null;

                string text = await resp.Content.ReadAsStringAsync();
                var jObj = JObject.Parse(text);
                var results = jObj["QuotationCodeTable"]?["Data"];
                if (results != null)
                {
                    foreach (var item in results)
                    {
                        var classify = item["Classify"]?.ToString();
                        classify = classify.ToLower();
                        if (classify != "astock" && classify != "23" && classify != "hk" && classify != "usstock")
                        {
                            continue;
                        }
                        var type = Tool.ToStockMarket(classify);
                        list.Add(new StockInfo
                        {
                            Code = item["Code"]?.ToString(),
                            Name = item["Name"]?.ToString(),
                            StockType = type
                        });
                    }
                }
            }

            return list;
        }

        private void ShowFuzzyDialog(List<StockInfo> list)
        {
            _fuzzySearchDialog?.Close();
            _fuzzySearchDialog = new FuzzySearchDialog(list)
            {
                Owner = System.Windows.Window.GetWindow(this)
            };

            _fuzzySearchDialog.StockSelected += info =>
            {
                CodeTextBox.Text = info.Code + " " + info.Name;
                if (info.StockType == StockMarket.StockHK || info.StockType == StockMarket.StockUS)
                {
                    CodeTextBox.Text += " " + info.StockType.ToString();
                }
                UpdateStockType(info.StockType);
                StartMonitoring();
            };

            var transform = CodeTextBox.TransformToAncestor(this);
            var pos = transform.Transform(new System.Windows.Point(0, CodeTextBox.ActualHeight));
            var screenPos = CodeTextBox.PointToScreen(pos);
            _fuzzySearchDialog.Left = screenPos.X;
            _fuzzySearchDialog.Top = screenPos.Y;
            _fuzzySearchDialog.Show();
        }
    }
}
