using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace EditViewCentrepoint
{
    /// <summary>
    /// Tuning knobs, optionally overridden by Documents\vatSys Files\EditViewCentrepoint.cfg (next to the log), e.g.
    ///     PanScale=1.0
    ///     SetCenterFlags=false,false
    ///     Redraw=RedrawBackground
    /// "Redraw" is a comma-separated list of parameterless methods on the display control to call after each move
    /// (leave it empty to call none).
    /// </summary>
    internal static class Tuning
    {
        /// <summary>Multiplier on pan speed. 1.0 = the view tracks the cursor 1:1.</summary>
        public static double PanScale = 1.0;

        /// <summary>Starting values for SetDisplayCenter's two bool parameters (null = automatic).</summary>
        public static bool[] SetCenterFlags;

        public static string[] RedrawMethods = { "RedrawBackground" };

        static Tuning()
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.Path), "EditViewCentrepoint.cfg");
                if (!System.IO.File.Exists(path)) return;

                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq < 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    if (key == "panscale") PanScale = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture);
                    else if (key == "setcenterflags")
                    {
                        string[] parts = val.Split(',');
                        if (parts.Length == 2) SetCenterFlags = new[] { bool.Parse(parts[0].Trim()), bool.Parse(parts[1].Trim()) };
                    }
                    else if (key == "redraw")
                        RedrawMethods = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(x => x.Trim()).ToArray();
                }
                Log.Write("Config loaded from " + path + ": PanScale=" + PanScale +
                          ", SetCenterFlags=" + (SetCenterFlags == null ? "auto" : SetCenterFlags[0] + "," + SetCenterFlags[1]) +
                          ", Redraw=" + string.Join(",", RedrawMethods));
            }
            catch (Exception ex) { Log.Write("Couldn't read config: " + ex.GetBaseException().Message); }
        }
    }

    /// <summary>Degrees of lat/lon the *content under the cursor* shifts per client pixel moved right (x) or down (y).</summary>
    internal struct PixelScale
    {
        public double DLatDx, DLatDy, DLonDx, DLonDy;

        public bool IsValid
        {
            get
            {
                double[] v = { DLatDx, DLatDy, DLonDx, DLonDy };
                foreach (double d in v) if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                return Math.Abs(DLatDx) + Math.Abs(DLatDy) > 1e-12 && Math.Abs(DLonDx) + Math.Abs(DLonDy) > 1e-12;
            }
        }
    }

    /// <summary>
    /// Wraps a vatsys.ASDControlDX (used by the main ASD, secondary ASDs and ground/ASMGCS windows).
    /// Uses the control's own members, found by reflection because the SDK doesn't expose them:
    ///   renderParams / GetRenderParams(bool)      - the current projection
    ///   ConvertScreenToLL(Vector2, RenderParams)  - screen -> lat/lon, so the view centre can be read and the pan scale measured
    ///   SetDisplayCenter(Coordinate, bool, bool)  - moves the view
    /// </summary>
    internal sealed class ViewportAdapter
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // SetDisplayCenter's two bool parameters are undocumented. We start with (false,false) and, if the view
        // doesn't actually move when asked, step through the other combinations automatically (and log it).
        static readonly bool[][] FlagCombos =
        {
            new[] { false, false }, new[] { true, false }, new[] { false, true }, new[] { true, true }
        };

        public Form Form { get; private set; }
        public Control Control { get; private set; }
        public bool IsMain { get; private set; }

        Type _coordType, _vecType;
        MethodInfo _setCenter, _screenToLL, _dxToClient, _getRenderParams;
        FieldInfo _renderParamsField, _renderSizeField;
        PropertyInfo _mainCentreProp;
        readonly List<MethodInfo> _redraw = new List<MethodInfo>();

        int _combo, _mismatches, _verifyLogs;
        bool _verifyDisabled, _haveLast;
        double _lastReqLat, _lastReqLon, _lastReadLat, _lastReadLon;

        // ---------------------------------------------------------------------------------------------
        public static ViewportAdapter Create(Form form, out string error)
        {
            error = null;
            Control control = FindDisplayControl(form);
            if (control == null)
            {
                error = "Couldn't find a display control inside the window '" + form.Text + "' (" + form.GetType().FullName + ").";
                return null;
            }

            var vp = new ViewportAdapter { Form = form, Control = control, IsMain = form.GetType().Name == "MainForm" };
            Log.DumpOnce(form, control);
            try { return vp.Discover(out error) ? vp : null; }
            catch (Exception ex)
            {
                error = "Couldn't read the view from " + control.GetType().Name + ": " + ex.GetBaseException().Message;
                Log.Write("Discover failed: " + ex);
                return null;
            }
        }

        static Control FindDisplayControl(Form form)
        {
            var all = new List<Control>();
            Collect(form, all);

            Control best = null;
            long bestScore = -1;
            foreach (Control c in all)
            {
                if (!c.Visible || c.Width < 50 || c.Height < 50) continue;

                Type t = c.GetType();
                bool standard = (t.Namespace ?? "").StartsWith("System.Windows.Forms", StringComparison.Ordinal);
                bool named = t.Name.IndexOf("ASD", StringComparison.OrdinalIgnoreCase) >= 0
                          || t.Name.IndexOf("ASMGCS", StringComparison.OrdinalIgnoreCase) >= 0;
                if (standard && !named) continue;

                long score = (long)c.Width * c.Height + (named ? 1000000000L : 0L);
                if (score > bestScore) { best = c; bestScore = score; }
            }

            if (best != null) Log.Write("Display control for '" + form.Text + "': " + best.GetType().FullName);
            return best;
        }

        static void Collect(Control parent, List<Control> into)
        {
            foreach (Control c in parent.Controls) { into.Add(c); Collect(c, into); }
        }

        static MethodInfo FindMethod(Type t, string name, int paramCount)
        {
            for (Type c = t; c != null && c != typeof(Control); c = c.BaseType)
                foreach (MethodInfo m in c.GetMethods(Declared))
                    if (m.Name == name && m.GetParameters().Length == paramCount) return m;
            return null;
        }

        static FieldInfo FindField(Type t, string name)
        {
            for (Type c = t; c != null && c != typeof(Control); c = c.BaseType)
            {
                FieldInfo f = c.GetField(name, Declared);
                if (f != null) return f;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------
        bool Discover(out string error)
        {
            error = null;
            Type ct = Control.GetType();

            _setCenter = FindMethod(ct, "SetDisplayCenter", 3);
            _screenToLL = FindMethod(ct, "ConvertScreenToLL", 2);
            _dxToClient = FindMethod(ct, "DXVector2ToClient", 1);
            _getRenderParams = FindMethod(ct, "GetRenderParams", 1);
            _renderParamsField = FindField(ct, "renderParams");
            _renderSizeField = FindField(ct, "renderSize");

            if (_setCenter == null || _screenToLL == null)
            {
                error = "This vatSys version doesn't have the expected SetDisplayCenter / ConvertScreenToLL members on " +
                        ct.FullName + ". See the log for the member dump.";
                return false;
            }

            _coordType = _screenToLL.ReturnType;
            _vecType = _screenToLL.GetParameters()[0].ParameterType;

            ParameterInfo[] sp = _setCenter.GetParameters();
            if (sp[0].ParameterType != _coordType || sp[1].ParameterType != typeof(bool) || sp[2].ParameterType != typeof(bool))
            {
                error = "SetDisplayCenter has an unexpected signature: " + _setCenter;
                return false;
            }

            if (IsMain)
            {
                PropertyInfo p = typeof(vatsys.MMI).GetProperty("MainASDCentre", AnyMember);
                if (p != null && p.PropertyType == _coordType && p.CanWrite) _mainCentreProp = p;
            }

            foreach (string name in Tuning.RedrawMethods)
            {
                MethodInfo m = FindMethod(ct, name, 0);
                if (m != null) _redraw.Add(m); else Log.Write("Redraw method '" + name + "' not found on " + ct.Name + ".");
            }

            if (Tuning.SetCenterFlags != null)
                for (int i = 0; i < FlagCombos.Length; i++)
                    if (FlagCombos[i][0] == Tuning.SetCenterFlags[0] && FlagCombos[i][1] == Tuning.SetCenterFlags[1]) _combo = i;

            if (RenderParams() == null)
            {
                error = "The display hasn't finished initialising yet (no render parameters). Try again in a moment.";
                return false;
            }

            LogState();

            double lat, lon;
            GetCentre(out lat, out lon);
            _lastReqLat = _lastReadLat = lat;
            _lastReqLon = _lastReadLon = lon;
            _haveLast = true;

            Log.Write("Discovered: SetDisplayCenter=" + _setCenter + ", ConvertScreenToLL=" + _screenToLL +
                      ", DXVector2ToClient=" + (_dxToClient != null ? "yes" : "no") +
                      ", redraw=[" + string.Join(",", _redraw.Select(m => m.Name)) + "]" +
                      ", MMI.MainASDCentre writable=" + (_mainCentreProp != null) +
                      ". Current centre " + lat.ToString("F5") + ", " + lon.ToString("F5"));
            return true;
        }

        /// <summary>Records the state of the fields that look relevant, so the semantics can be checked from the log.</summary>
        void LogState()
        {
            if (!Log.Enabled) return;
            try
            {
                string[] names = { "StoredCentreLL", "settingVisCenter", "startPanCentre", "startPanXY", "startCentreXYOffset",
                                   "QuickPan", "range", "StoredRange", "DefRange", "renderSize", "initialZoomSet", "asdType" };
                var lines = new List<string>();
                foreach (string n in names)
                {
                    FieldInfo f = FindField(Control.GetType(), n);
                    lines.Add(n + " = " + (f == null ? "(missing)" : Log.Describe(f.GetValue(Control))));
                }
                Log.Write("Control state: " + string.Join("; ", lines));
                Log.DumpObject("renderParams", RenderParams());
            }
            catch (Exception ex) { Log.Write("LogState failed: " + ex.GetBaseException().Message); }
        }

        // ---------------------------------------------------------------------------------------------
        static double GetNum(object o, string name)
        {
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, AnyMember);
            object v = p != null ? p.GetValue(o, null) : t.GetField(name, AnyMember).GetValue(o);
            return Convert.ToDouble(v);
        }

        static void SetNum(object o, string name, double value)
        {
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, AnyMember);
            if (p != null) { p.SetValue(o, Convert.ChangeType(value, p.PropertyType), null); return; }
            FieldInfo f = t.GetField(name, AnyMember);
            f.SetValue(o, Convert.ChangeType(value, f.FieldType));
        }

        object RenderParams()
        {
            object rp = _renderParamsField != null ? _renderParamsField.GetValue(Control) : null;
            if (rp == null && _getRenderParams != null && _getRenderParams.GetParameters()[0].ParameterType == typeof(bool))
                rp = _getRenderParams.Invoke(Control, new object[] { false });
            return rp;
        }

        object Vec(double x, double y)
        {
            return Activator.CreateInstance(_vecType, (float)x, (float)y);
        }

        object MakeCoord(double lat, double lon)
        {
            try { return Activator.CreateInstance(_coordType, lat, lon); }
            catch (MissingMethodException)
            {
                object o = Activator.CreateInstance(_coordType);
                SetNum(o, "Latitude", lat);
                SetNum(o, "Longitude", lon);
                return o;
            }
        }

        /// <summary>The control's size in the units that ConvertScreenToLL works in.</summary>
        void RenderSize(out double w, out double h)
        {
            if (_renderSizeField != null)
            {
                object s = _renderSizeField.GetValue(Control);
                if (s != null)
                {
                    w = GetNum(s, "Width"); h = GetNum(s, "Height");
                    if (w > 0 && h > 0) return;
                }
            }
            double r = DxPerClientPx();
            w = Control.ClientSize.Width * r;
            h = Control.ClientSize.Height * r;
        }

        /// <summary>How many ConvertScreenToLL units correspond to one client pixel (differs from 1 under display scaling).</summary>
        double DxPerClientPx()
        {
            if (_dxToClient != null)
            {
                try
                {
                    double x0 = GetNum(_dxToClient.Invoke(Control, new[] { Vec(0, 0) }), "X");
                    double x1 = GetNum(_dxToClient.Invoke(Control, new[] { Vec(100, 0) }), "X");
                    if (x1 - x0 > 0.001) return 100.0 / (x1 - x0);
                }
                catch (Exception ex) { Log.Write("DXVector2ToClient failed: " + ex.GetBaseException().Message); }
            }
            if (_renderSizeField != null)
            {
                object s = _renderSizeField.GetValue(Control);
                if (s != null && Control.ClientSize.Width > 0)
                {
                    double w = GetNum(s, "Width");
                    if (w > 0) return w / Control.ClientSize.Width;
                }
            }
            return 1.0;
        }

        void ScreenToLL(double x, double y, out double lat, out double lon)
        {
            object rp = RenderParams();
            if (rp == null) throw new InvalidOperationException("Render parameters aren't available.");
            object ll = _screenToLL.Invoke(Control, new object[] { Vec(x, y), rp });
            lat = GetNum(ll, "Latitude");
            lon = GetNum(ll, "Longitude");
        }

        // ---------------------------------------------------------------------------------------------
        public void GetCentre(out double lat, out double lon)
        {
            double w, h;
            RenderSize(out w, out h);
            ScreenToLL(w / 2.0, h / 2.0, out lat, out lon);
        }

        /// <summary>Moves the view so that lat/lon is at its centre.</summary>
        public void SetCentre(double lat, double lon)
        {
            bool[] f = FlagCombos[_combo];
            _setCenter.Invoke(Control, new object[] { MakeCoord(lat, lon), f[0], f[1] });

            // The control caches its map layer; ask it to redraw, otherwise the new centre isn't shown.
            foreach (MethodInfo m in _redraw)
            {
                try { m.Invoke(Control, null); }
                catch (Exception ex) { Log.Write("Redraw method " + m.Name + " failed: " + ex.GetBaseException().Message); }
            }
            Control.Invalidate();
            Verify(lat, lon);
        }

        /// <summary>Makes the current view position the centrepoint.</summary>
        public void Commit()
        {
            double lat, lon;
            GetCentre(out lat, out lon);
            SetCentre(lat, lon);

            if (_mainCentreProp != null)
            {
                try { _mainCentreProp.SetValue(null, MakeCoord(lat, lon), null); Log.Write("MMI.MainASDCentre updated."); }
                catch (Exception ex) { Log.Write("Couldn't update MMI.MainASDCentre: " + ex.GetBaseException().Message); }
            }
        }

        /// <summary>Checks the view really moved by what was asked; if not, tries the next SetDisplayCenter flag combination.</summary>
        void Verify(double lat, double lon)
        {
            if (_verifyDisabled) return;
            try
            {
                double rl, ro;
                GetCentre(out rl, out ro);

                if (_haveLast)
                {
                    double ex = lat - _lastReqLat, ey = lon - _lastReqLon;
                    double ax = rl - _lastReadLat, ay = ro - _lastReadLon;
                    double mag = Math.Sqrt(ex * ex + ey * ey);
                    if (mag > 1e-6)
                    {
                        double err = Math.Sqrt((ax - ex) * (ax - ex) + (ay - ey) * (ay - ey));
                        bool bad = err > 0.5 * mag;
                        if (_verifyLogs++ < 6)
                            Log.Write("Set centre: asked to move (" + ex.ToString("E2") + ", " + ey.ToString("E2") + "), view moved (" +
                                      ax.ToString("E2") + ", " + ay.ToString("E2") + ") flags=" + FlagCombos[_combo][0] + "," + FlagCombos[_combo][1] + (bad ? "  MISMATCH" : "  ok"));

                        if (!bad) _mismatches = 0;
                        else if (++_mismatches >= 3)
                        {
                            _mismatches = 0;
                            if (_combo + 1 < FlagCombos.Length)
                            {
                                _combo++;
                                Log.Write("View isn't following SetDisplayCenter; trying flags " + FlagCombos[_combo][0] + "," + FlagCombos[_combo][1]);
                            }
                            else
                            {
                                _combo = 0;
                                _verifyDisabled = true;
                                Log.Write("No SetDisplayCenter flag combination moved the view as expected; giving up on verifying.");
                            }
                        }
                    }
                }

                _lastReqLat = lat; _lastReqLon = lon; _lastReadLat = rl; _lastReadLon = ro; _haveLast = true;
            }
            catch (Exception ex) { _verifyDisabled = true; Log.Write("Verify failed: " + ex.GetBaseException().Message); }
        }

        // ---------------------------------------------------------------------------------------------
        public bool MeasureScale(double lat, out PixelScale scale, out string how)
        {
            double r = DxPerClientPx(), w, h;
            RenderSize(out w, out h);
            double cx = w / 2.0, cy = h / 2.0;

            double la0, lo0, la1, lo1, la2, lo2;
            ScreenToLL(cx, cy, out la0, out lo0);
            ScreenToLL(cx + 100 * r, cy, out la1, out lo1);
            ScreenToLL(cx, cy + 100 * r, out la2, out lo2);

            scale = new PixelScale
            {
                DLatDx = (la1 - la0) / 100.0 * Tuning.PanScale, DLonDx = (lo1 - lo0) / 100.0 * Tuning.PanScale,
                DLatDy = (la2 - la0) / 100.0 * Tuning.PanScale, DLonDy = (lo2 - lo0) / 100.0 * Tuning.PanScale
            };
            how = "ConvertScreenToLL; " + r.ToString("F3") + " units/px; ~" + (Math.Abs(scale.DLatDy) * 60.0).ToString("F4") + " NM per pixel vertically";
            return scale.IsValid;
        }
    }
}
