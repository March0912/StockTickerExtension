using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StockTickerExtension
{
    public class Tool
    {
        /// <summary>
        /// 检测KDJ金叉出现（上一根K<D，本根K≥D）
        /// </summary>
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

        /// <summary>
        /// 检测KDJ死叉出现（上一根K>D，本根K≤D）
        /// </summary>
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

		//         public string DisplayText => $"{Name} ({Code}) [{StockType}]";
	}

}
