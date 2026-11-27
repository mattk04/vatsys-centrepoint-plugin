using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EditViewCentrepoint
{
    /// <summary>
    /// One "Edit View Centrepoint" interaction on one window.
    ///   mouse move  -> pans the view (cursor is an X)
    ///   Enter / left click on the display -> keep the view where it is and make it the new centrepoint
    ///   Esc / middle click -> cancel and restore the viewport as it was when the mode started
    /// Input is taken with an application-wide message filter so it works regardless of which control has focus,
    /// and the keys / middle button are swallowed so vatSys doesn't also act on them.
    /// </summary>
    internal sealed class EditSession : IMessageFilter
    {
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEMOVE = 0x0200,
                  WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
                  WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209;

        static EditSession _current;
        static Cursor _xCursor;

        readonly ViewportAdapter _vp;
        double _origLat, _origLon;          // the viewport as it was when the mode began
        bool _haveOrig;
        Cursor _prevCursor;
        bool _filterOn, _ended;
        MouseButtons _pendingButton = MouseButtons.None;   // button whose release still has to be swallowed
        Keys _pendingKey = Keys.None;

        int _moveLogs, _panLogs;
        bool _anchored;
        Point _anchorScreen;
        double _anchorLat, _anchorLon;
        PixelScale _scale;

        EditSession(ViewportAdapter vp) { _vp = vp; }

        // ---------------------------------------------------------------------------------------------
        public static void Begin(Form form)
        {
            if (_current != null) _current.Finish(false, "replaced by a new session", Keys.None, MouseButtons.None);

            string error;
            ViewportAdapter vp = ViewportAdapter.Create(form, out error);
            if (vp == null) { Fail(form, error); return; }

            var s = new EditSession(vp);
            try
            {
                s.Start();
                _current = s;
            }
            catch (Exception ex)
            {
                s.Finish(false, "start failed", Keys.None, MouseButtons.None);
                Fail(form, "Couldn't start edit mode: " + ex.GetBaseException().Message);
            }
        }

        static void Fail(Form form, string message)
        {
            Log.Write("FAILED: " + message);
            MessageBox.Show(form, message + (Log.Enabled ? "\n\nDetails were written to:\n" + Log.Path
                                                         : "\n\nAdd the line  Logging=true  to EditViewCentrepoint.cfg (in Documents\\vatSys Files) for details."),
                "Edit View Centrepoint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void Start()
        {
            _vp.GetCentre(out _origLat, out _origLon);
            _haveOrig = true;

            Application.AddMessageFilter(this);
            _filterOn = true;
            _vp.Form.FormClosed += OnFormClosed;

            _prevCursor = _vp.Control.Cursor;
            _vp.Control.Cursor = GetXCursor();
            Cursor.Current = GetXCursor();
            Log.Write("Edit mode started on '" + _vp.Form.Text + "' at " + _origLat.ToString("F5") + ", " + _origLon.ToString("F5"));
        }

        void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            Finish(false, "window closed", Keys.None, MouseButtons.None);
        }

        // ---------------------------------------------------------------------------------------------
        public bool PreFilterMessage(ref Message m)
        {
            try
            {
                if (_ended)
                {
                    // Session is over; stay installed only long enough to swallow the matching key-up / button-up.
                    if (_pendingButton != MouseButtons.None)
                    {
                        bool left = _pendingButton == MouseButtons.Left;
                        if (m.Msg == (left ? WM_LBUTTONUP : WM_MBUTTONUP)) { _pendingButton = MouseButtons.None; RemoveFilterIfDone(); return true; }
                        if (m.Msg == (left ? WM_LBUTTONDBLCLK : WM_MBUTTONDBLCLK)) return true;
                    }
                    if (_pendingKey != Keys.None && (m.Msg == WM_KEYUP || m.Msg == WM_KEYDOWN) && (Keys)(int)m.WParam == _pendingKey)
                    {
                        if (m.Msg == WM_KEYUP) { _pendingKey = Keys.None; RemoveFilterIfDone(); }
                        return true;
                    }
                    return false;
                }

                switch (m.Msg)
                {
                    case WM_MOUSEMOVE:
                        OnMouseMove(m.HWnd);
                        return false;

                    case WM_KEYDOWN:
                        Keys key = (Keys)(int)m.WParam;
                        if (key == Keys.Return) { Finish(true, "Enter", Keys.Return, MouseButtons.None); return true; }
                        if (key == Keys.Escape) { Finish(false, "Escape", Keys.Escape, MouseButtons.None); return true; }
                        return false;

                    case WM_LBUTTONDOWN:
                        // A left click on the display accepts the view, same as Enter. The click is swallowed so
                        // vatSys doesn't also treat it as a click on a track/label underneath.
                        if (IsOverDisplay(m.HWnd))
                        {
                            Finish(true, "left click", Keys.None, MouseButtons.Left);
                            return true;
                        }
                        return false;

                    case WM_MBUTTONDOWN:
                        Finish(false, "middle click", Keys.None, MouseButtons.Middle);
                        return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Error in edit mode: " + ex);
                try { Finish(false, "error", Keys.None, MouseButtons.None); } catch { }
                Fail(_vp.Form, "Edit mode stopped because of an error: " + ex.GetBaseException().Message);
            }
            return false;
        }

        /// <summary>True if the message target, or whatever is actually under the cursor, is the display control.</summary>
        bool IsOverDisplay(IntPtr hwnd)
        {
            IntPtr c = _vp.Control.Handle;
            IntPtr under = NativeMethods.WindowFromPoint(Cursor.Position);
            return hwnd == c || NativeMethods.IsChild(c, hwnd) || under == c || NativeMethods.IsChild(c, under);
        }

        void OnMouseMove(IntPtr hwnd)
        {
            Point p = Cursor.Position;

            if (_moveLogs < 6)
            {
                _moveLogs++;
                Log.Write("Mouse move: msgHwnd=" + hwnd + " control=" + _vp.Control.Handle +
                          " over=" + IsOverDisplay(hwnd) + " anchored=" + _anchored);
            }

            if (!IsOverDisplay(hwnd)) { _anchored = false; return; }

            NativeMethods.SetCursor(GetXCursor().Handle);   // keep the X showing even if vatSys resets the cursor

            if (!_anchored)
            {
                // Anchor at the first mouse position over the display, so entering the view doesn't cause a jump.
                double lat, lon;
                _vp.GetCentre(out lat, out lon);
                string how;
                if (!_vp.MeasureScale(lat, out _scale, out how))
                    throw new InvalidOperationException("Couldn't work out the view scale (no screen-to-coordinate method or range found).");
                if (_scale.DLatDx == 0 && _scale.DLonDx == 0 && _scale.DLatDy == 0 && _scale.DLonDy == 0)
                    throw new InvalidOperationException("View scale was zero.");

                Log.Write("Pan anchored; scale from " + how);
                _anchorLat = lat; _anchorLon = lon; _anchorScreen = p; _anchored = true;
                return;
            }

            int dx = p.X - _anchorScreen.X, dy = p.Y - _anchorScreen.Y;
            if (dx == 0 && dy == 0) return;

            // Dragging the map by (dx,dy) means the centre becomes whatever was at (centre - delta).
            double newLat = _anchorLat - (_scale.DLatDx * dx + _scale.DLatDy * dy);
            double newLon = _anchorLon - (_scale.DLonDx * dx + _scale.DLonDy * dy);
            newLat = Math.Max(-85.0, Math.Min(85.0, newLat));
            if (_panLogs < 5)
            {
                _panLogs++;
                Log.Write("Pan: delta px (" + dx + ", " + dy + ") -> centre " + newLat.ToString("F5") + ", " + newLon.ToString("F5"));
            }
            _vp.SetCentre(newLat, newLon);
        }

        // ---------------------------------------------------------------------------------------------
        void Finish(bool commit, string reason, Keys pendingKey, MouseButtons pendingButton)
        {
            if (_ended) return;
            _ended = true;

            try
            {
                if (commit) _vp.Commit();
                else if (_haveOrig) _vp.SetCentre(_origLat, _origLon);   // restore the viewport exactly as it was
            }
            catch (Exception ex) { Log.Write("Error applying result: " + ex.GetBaseException().Message); }

            try
            {
                _vp.Form.FormClosed -= OnFormClosed;
                _vp.Control.Cursor = _prevCursor ?? Cursors.Default;
                Cursor.Current = Cursors.Default;
            }
            catch { }

            Log.Write("Edit mode " + (commit ? "committed" : "cancelled") + " (" + reason + ")");

            if (_current == this) _current = null;
            _pendingKey = pendingKey;
            _pendingButton = pendingButton;
            RemoveFilterIfDone();
        }

        void RemoveFilterIfDone()
        {
            if (_filterOn && _pendingButton == MouseButtons.None && _pendingKey == Keys.None)
            {
                Application.RemoveMessageFilter(this);
                _filterOn = false;
            }
        }

        // ---------------------------------------------------------------------------------------------
        /// <summary>A 32x32 black-and-white-outlined X, hotspot in the centre.</summary>
        static Cursor GetXCursor()
        {
            if (_xCursor != null) return _xCursor;

            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var outline = new Pen(Color.White, 5f))
                    using (var core = new Pen(Color.Black, 2f))
                    {
                        foreach (Pen pen in new[] { outline, core })
                        {
                            g.DrawLine(pen, 7, 7, 25, 25);
                            g.DrawLine(pen, 25, 7, 7, 25);
                        }
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                NativeMethods.ICONINFO info;
                NativeMethods.GetIconInfo(hIcon, out info);
                info.fIcon = false;
                info.xHotspot = 16;
                info.yHotspot = 16;
                IntPtr hCursor = NativeMethods.CreateIconIndirect(ref info);

                if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
                NativeMethods.DestroyIcon(hIcon);

                _xCursor = hCursor != IntPtr.Zero ? new Cursor(hCursor) : Cursors.Cross;
            }
            return _xCursor;
        }

        static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct ICONINFO
            {
                [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
                public int xHotspot;
                public int yHotspot;
                public IntPtr hbmMask;
                public IntPtr hbmColor;
            }

            [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
            [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
            [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO icon);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
            [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr hObject);
        }
    }
}
