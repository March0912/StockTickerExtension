using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Threading.Tasks;
using System.Timers;

namespace StockTickerExtension
{
    public class StockStatusBarUpdater
    {
        private readonly IVsStatusbar _statusBar;
        private readonly Timer _timer;
        private string _text;
        private uint _color;
        public StockStatusBarUpdater(IServiceProvider serviceProvider)
        {
            _statusBar = serviceProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
            _timer = new Timer(1000); // 每秒刷新一次
            _timer.Elapsed += OnTimerElapsed;
        }

        public void Start()
        {
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public void UpdateStatusInfo(double price, double changePercent, double positionProfit, double todayProfit)
        {
            string arrow;
            if (changePercent < 0)
            {
                arrow = "↘️";
                _color = 0x0000FF00;
            }
            else if (changePercent > 0)
            {
                arrow = "↗️";
                _color = 0x000000FF;
            }
            else
            {
                arrow = "";
                _color = 0x00FFFFFF;
            }

            _text = $"StockMonitoring: Price:{price:F2} | Change:{changePercent:F2}%{arrow} | Profit:{positionProfit:F2}| Today:{todayProfit:F2}";
        }

        public void UpdateStatusInfo(string text)
        {
            _text = $"StockMonitoring: {text}";
        }

        public void ClearStatusInfo()
        {
            _text = "";
            _statusBar.SetText("");
            _statusBar.FreezeOutput(0);
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!string.IsNullOrEmpty(_text))
                {
                    _statusBar.SetColorText(_text, _color, 0);
                }
                else
                {
                    _statusBar.Clear();
                }
            });
        }
    }
}