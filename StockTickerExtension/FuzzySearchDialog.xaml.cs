using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.PlatformUI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StockTickerExtension
{
    public partial class FuzzySearchDialog : Window
    {
        public string SelectedCode { get; private set; }
        public string SelectedName { get; private set; }

		private bool _isClosing = false;
		public event Action<StockInfo> StockSelected;

		public FuzzySearchDialog(List<StockInfo> list)
        {
            InitializeComponent();
            this.KeyUp += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    this.Close();
                }
            };
			this.Loaded += FuzzySearchDialog_Loaded;
			this.Closed += FuzzySearchDialog_Closed;

			// 备用：当 Window.Deactivated 触发时也关闭（有时可靠）
			this.Deactivated += (s, e) => SafeClose();

			// 也监听键盘 ESC 关闭
			this.PreviewKeyDown += (s, e) =>
			{
				if (e.Key == Key.Escape)
				{
					SafeClose();
				}
			};

			ResultList.Items.Clear();
			foreach (var stock in list)
			{
				var item = new ListViewItem
				{
					Content = $"{stock.Code} {stock.Name}",
				};				
				ResultList.Items.Add(item);
			}
            InitUIColor();
        }

        private async void ListBox_MouseDoubleClickEvent(object sender, MouseButtonEventArgs e)
        {
			if(ResultList.SelectedItem is ListViewItem item && item.IsSelected)
            {
                SelectedCode = item.Content.ToString().Split(' ')[0];
                SelectedName = item.Content.ToString().Split(' ')[1];

				StockInfo info = new StockInfo
				{
					Code = SelectedCode,
					Name = SelectedName
				};
				StockSelected?.Invoke(info);
				SafeClose();
			}
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

			ResultList.Background = bgBrush;
			ResultList.Foreground = fgBrush;
            ResultList.BorderBrush = bdBrush;
		}
		protected override void OnDeactivated(EventArgs e)
		{
			base.OnDeactivated(e);
			SafeClose();
		}
		
		private void FuzzySearchDialog_Loaded(object sender, RoutedEventArgs e)
		{
			// 1) 尝试捕获鼠标到本窗口子树（当点击窗外会触发 PreviewMouseDownOutsideCapturedElementEvent）
			try
			{
				// Capture 到 SubTree（允许捕获外部点击事件）
				Mouse.Capture(this, CaptureMode.SubTree);

				// 处理 WPF 专门的“鼠标在捕获外部位置按下”事件（当捕获成功时此事件会触发）
				this.AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, new MouseButtonEventHandler(OnMouseDownOutsideCapturedElement), true);
			}
			catch
			{
				// 捕获可能失败于某些环境，继续用 owner 监听做兜底
			}

			// 2) 订阅 Owner 的 PreviewMouseDown 作为兜底（因为在 VS 环境里有时 Owner 会收到点击）
			if (this.Owner != null)
			{
				this.Owner.PreviewMouseDown += Owner_PreviewMouseDown;
				this.Owner.LocationChanged += Owner_WindowMovedOrResized;
				this.Owner.SizeChanged += Owner_WindowMovedOrResized;
			}
			else
			{
				// 如果 Owner 为空，可尝试全局订阅 Application 的活动窗口鼠标事件（谨慎）
				Application.Current.Deactivated += Application_Deactivated;
			}
		}

		private void FuzzySearchDialog_Closed(object sender, EventArgs e)
		{
			// 清理订阅与鼠标捕获
			try { Mouse.Capture(null); } catch { }
			try { this.RemoveHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, new MouseButtonEventHandler(OnMouseDownOutsideCapturedElement)); } catch { }

			if (this.Owner != null)
			{
				this.Owner.PreviewMouseDown -= Owner_PreviewMouseDown;
				this.Owner.LocationChanged -= Owner_WindowMovedOrResized;
				this.Owner.SizeChanged -= Owner_WindowMovedOrResized;
			}
			Application.Current.Deactivated -= Application_Deactivated;
		}

		// 当捕获到“点击窗外”时触发（优先级高）
		private void OnMouseDownOutsideCapturedElement(object sender, MouseButtonEventArgs e)
		{
			SafeClose();
		}

		private void Owner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
		{
			// 判断点击位置是否位于本弹窗内部，如果不在则关闭
			var pt = e.GetPosition(this);
			bool inside = (pt.X >= 0 && pt.X <= this.ActualWidth && pt.Y >= 0 && pt.Y <= this.ActualHeight);
			if (!inside)
			{
				SafeClose();
			}
		}

		// Owner 窗口移动/缩放时，自动关闭或重定位（你可以选择关闭或更新 Left/Top）
		private void Owner_WindowMovedOrResized(object sender, EventArgs e)
		{
			// 更稳妥的做法是直接关闭弹窗（避免位置计算复杂）
			SafeClose();
		}

		private void Application_Deactivated(object sender, EventArgs e)
		{
			SafeClose();
		}

		private void SafeClose()
		{
			if (_isClosing) return;
			_isClosing = true;

			Dispatcher.BeginInvoke(new Action(() =>
			{
				try
				{
					if (this.IsLoaded)
						this.Close();
				}
				catch { }
			}), System.Windows.Threading.DispatcherPriority.Normal);
		}
	}
}
