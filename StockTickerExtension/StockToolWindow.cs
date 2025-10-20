using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockTickerExtension
{
    /// <summary>
    /// This class implements the tool window exposed by this package and hosts a user control.
    /// </summary>
    /// <remarks>
    /// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
    /// usually implemented by the package implementer.
    /// <para>
    /// This class derives from the ToolWindowPane class provided from the MPF in order to use its
    /// implementation of the IVsUIElementPane interface.
    /// </para>
    /// </remarks>
    [Guid("556ab237-e377-42ee-893e-5e2311729da4")]
    public class StockToolWindow : ToolWindowPane, IVsWindowFrameNotify
    {
        private StockStatusBarUpdater _statusUpdater;
        /// <summary>
        /// Initializes a new instance of the <see cref="StockToolWindow"/> class.
        /// </summary>
        public StockToolWindow() : base(null)
        {
            this.Caption = "StockMonitoring";

            // This is the user control hosted by the tool window; Note that, even if this class implements IDisposable,
            // we are not calling Dispose on this object. This is because ToolWindowPane calls Dispose on
            // the object returned by the Content property.
            this.Content = new StockToolWindowControl(this);
        }

        protected override void Initialize()
        {
            base.Initialize();
            _statusUpdater = new StockStatusBarUpdater(this);
        }
        public void UpdateStatusInfo(string code, double price, double changePercent, double positionProfit, double todayProfit)
        {
            _statusUpdater.UpdateStatusInfo(code, price, changePercent, positionProfit, todayProfit);
        }

        public void UpdateStatusInfo(string text)
        {
            _statusUpdater.UpdateStatusInfo(text);
        }

        public void ClearStatusInfo()
        {
            _statusUpdater.ClearStatusInfo();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _statusUpdater?.Stop();

            base.Dispose(disposing);
        }
        protected override void OnClose()
        {
            _statusUpdater.ClearStatusInfo();
            base.OnClose();
        }

        public int OnShow(int fShow)
        {
            if (fShow == 0)
            {
                var tb = this.Content as StockToolWindowControl;
                if(tb == null)
                    return VSConstants.S_OK;

                if(tb.IsAutoStopWhenClosed())
                {
                    _statusUpdater.ClearStatusInfo();
                    _statusUpdater.Stop();
                }
                else
                {
                    _statusUpdater.Start();
                }
                tb.SaveConfig();
            }
            else
            {
                _statusUpdater.ClearStatusInfo();
                _statusUpdater.Stop();
            }
            return VSConstants.S_OK;
        }

        public int OnMove() => VSConstants.S_OK;      
        public int OnSize() => VSConstants.S_OK;
        public int OnDockableChange(int fDockable) => VSConstants.S_OK;
    }
}
