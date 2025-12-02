using Microsoft.VisualStudio.OLE.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media;

namespace StockTickerExtension
{
    public class Tool
    {
        static public bool HasKDJGoldenCross(double[] closes, double[] highs, double[] lows)
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
        static public bool HasKDJDeadCross(double[] closes, double[] highs, double[] lows)
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
        static public double[] ComputeSimpleMovingAverage(double[] source, int period)
        {
            if (source == null || source.Length == 0 || period <= 1)
                return source?.ToArray() ?? Array.Empty<double>();

            int n = source.Length;
            double[] result = new double[n];
            double windowSum = 0.0;
            var window = new Queue<double>(period);

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
        static public double[] ComputeExactWindowSMA(double[] source, int period)
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
        static public System.Windows.Media.Color ColorFromHex(string hex, double opacity = 1.0)
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
        static public StockMarket ToStockMarket(string code)
        {
            StockMarket sm = StockMarket.StockA;
            switch (code.ToLower())
            {
                case "neeq":       //科创板、北交所、新三板等
                case "23":  //科创板
                case "astock":
                    sm = StockMarket.StockA;
                    break;
                case "hk":
                    sm = StockMarket.StockHK;
                    break;
                case "usstock":
                    sm = StockMarket.StockUS;
                    break;
                default:
                    sm = StockMarket.StockA;
                    break;
            }
            return sm;
        }
        static public string GetSecId(StockMarket stockType, string code)
        {
            string secId = code;
            switch (stockType)
            {
                case StockMarket.StockA:
                    {
                        char first = code[0];
                        if (first == '0' || first == '2' || first == '3')
                        {
                            secId = "0." + code;
                        }
                        else if (first == '6' || first == '9')
                        {
                            secId = "1." + code;
                        }
                        break;
                    }
                case StockMarket.StockHK:
                    {
                        secId = "116." + code;
                        break;
                    }
                case StockMarket.StockUS:
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
        static public bool IsWeekend(DateTime dt) => dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday;
        static public bool IsTradingTime(StockMarket stockType, DateTime dt)
        {
            if (Tool.IsWeekend(dt))
                return false;

            if (stockType == StockMarket.StockA)
            {
                TimeSpan morningStart = new TimeSpan(9, 30, 0);
                TimeSpan morningEnd = new TimeSpan(11, 30, 0);
                TimeSpan afternoonStart = new TimeSpan(13, 0, 0);
                TimeSpan afternoonEnd = new TimeSpan(15, 0, 0);

                TimeSpan nowTime = dt.TimeOfDay;
                return (nowTime >= morningStart && nowTime <= morningEnd) ||
                       (nowTime >= afternoonStart && nowTime <= afternoonEnd);
            }
            else if (stockType == StockMarket.StockHK)
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
        static public string PeriodToKType(PeriodType period)
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
        static public List<string> BuildTradingMinutes(StockMarket stockType, DateTime date)
        {
            var list = new List<string>();

            if (stockType == StockMarket.StockA)
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
            else if (stockType == StockMarket.StockHK)
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
        static public (List<double> ticks, List<string> labels) GenerateTimeAxisLabels(PeriodType period, DateTime[] dates, DateTime currentDate)
        {
            if(dates == null || dates.Length == 0)
                return (default, default);

            var dateCount = dates.Length;
            var ticks = new List<double>();
            var labels = new List<string>();

            // 根据数据点数量确定标签密度
            int labelInterval = Math.Max(dateCount, 10);
            if (dateCount <= 10)
            {
                labelInterval = 1;
            }
            else// if (dateCount > 10)
            {
                labelInterval = Math.Max(1, dateCount / 10); // 最多显示10个标签
            }

            // 根据不同的K线周期生成时间标签
            switch (period)
            {
                case PeriodType.DailyK:
                    for (int i = dateCount - 1; i >= 0; i -= labelInterval)
                    {
                        // 从当前日期往前推算
                        DateTime date = new DateTime();
                        if (dates != null && dates.Length > 0)
                        {
                            date = dates[i];
                        }
                        else
                        {
                            date = currentDate.AddDays(-(dateCount - 1 - i));
                        }
                        ticks.Add(i);
                        labels.Add(date.ToString("MM/dd"));
                    }
                    break;
                case PeriodType.WeeklyK:
                    for (int i = dateCount - 1; i >= 0; i -= labelInterval)
                    {
                        DateTime date = new DateTime();
                        if (dates != null && dates.Length > 0)
                        {
                            date = dates[i];
                        }
                        else
                        {
                            date = currentDate.AddDays(-(dateCount - 1 - i) * 7);
                        }
                        ticks.Add(i);
                        labels.Add(date.ToString("MM/dd"));
                    }
                    break;
                case PeriodType.MonthlyK:
                    for (int i = dateCount - 1; i >= 0; i -= labelInterval)
                    {
                        DateTime date = new DateTime();
                        if (dates != null && dates.Length > 0)
                        {
                            date = dates[i];
                        }
                        else
                        {
                            date = currentDate.AddMonths(-(dateCount - 1 - i));
                        }
                        ticks.Add(i);
                        labels.Add(date.ToString("yyyy/MM"));
                    }
                    break;
                case PeriodType.QuarterlyK:
                    for (int i = dateCount - 1; i >= 0; i -= labelInterval)
                    {
                        DateTime date = new DateTime();
                        if (dates != null && dates.Length > 0)
                        {
                            date = dates[i];
                        }
                        else
                        {
                            date = currentDate.AddMonths(-(dateCount - 1 - i) * 3);
                        }
                        ticks.Add(i);
                        labels.Add($"{date.Year}/Q{((date.Month - 1) / 3) + 1}");
                    }
                    break;
                case PeriodType.YearlyK:
                    for (int i = dateCount - 1; i >= 0; i -= labelInterval)
                    {
                        DateTime date = new DateTime();
                        if (dates != null && dates.Length > 0)
                        {
                            date = dates[i];
                        }
                        else
                        {
                            date = currentDate.AddYears(-(dateCount - 1 - i));
                        }
                        var dStr = date.ToString("yyyy/MM");
                        if (!labels.Contains(dStr))
                        {
                            ticks.Add(i);
                            labels.Add(dStr);
                        }
                    }
                    break;
                default:
                    // 默认显示索引
                    for (int i = 0; i < dateCount; i += labelInterval)
                    {
                        ticks.Add(i);
                        labels.Add(i.ToString());
                    }
                    break;
            }

            ticks.Reverse();
            labels.Reverse();
            return (ticks, labels);
        }
        static public bool isDarkTheme(int r, int g, int b)
        {
            double luminance = 0.2126 * (double)r + 0.7152 * (double)g + 0.0722 * (double)b;
            return luminance < 140;  // 阈值可按实际视觉调整
        }

        static public List<MACDItem> CalcMacd(List<double> closes, int shortPeriod = 12, int longPeriod = 26, int signalPeriod = 9)
        {
            List<MACDItem> result = new List<MACDItem>();
            if (closes == null || closes.Count == 0)
                return result;

            double emaShort = closes[0]; // 初始化
            double emaLong = closes[0];
            double dea = 0;

            double kShort = 2.0 / (shortPeriod + 1);
            double kLong = 2.0 / (longPeriod + 1);
            double kDea = 2.0 / (signalPeriod + 1);

            for (int i = 0; i < closes.Count; i++)
            {
                double close = closes[i];
                if(double.IsNaN(close))
                {
                    continue;
                }

                emaShort = emaShort * (1 - kShort) + close * kShort;
                emaLong = emaLong * (1 - kLong) + close * kLong;

                double dif = emaShort - emaLong;
                dea = dea * (1 - kDea) + dif * kDea;

                double macd = (dif - dea) * 2;

                result.Add(new MACDItem
                {
                    Dif = dif,
                    Dea = dea,
                    Macd = macd
                });
            }
            return result;
        }

    }

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
        /// K线日期
        /// </summary>
        public DateTime[] KLineDates { get; set; }

        /// <summary>
        /// 涨跌幅
        /// </summary>
        public double[] ChangePercents { get; set; }

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

	public enum StockMarket : int
	{
		StockA,
		StockHK,
		StockUS
	};

	public class StockInfo
	{
		public string Code { get; set; }
		public string Name { get; set; }
		public StockMarket StockType { get; set; }
	}

    public class MACDItem
    {
        public double Dif { get; set; }
        public double Dea { get; set; }
        public double Macd { get; set; }   // 通常 = (DIF - DEA) * 2
    }

    public class StockTokenSource : CancellationTokenSource
    {
        public StockTokenSource(string code, PeriodType period) : base()
        {
            _code = code;
            _period = period;
        }
        public string _code { get; set; }
        public PeriodType _period { get; set; }
        public int _fetchIntervalSeconds = 2;
    }

    public class BackGroundTockenSource : CancellationTokenSource
    {
        public List<string> _stockList;
        public int _curIndex = -1;
    }

}
