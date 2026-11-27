using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace EditViewCentrepoint
{
    /// <summary>
    /// Logging is OFF by default: every call below is a no-op unless EditViewCentrepoint.cfg (next to where the log
    /// would go, Documents\vatSys Files) contains the line  Logging=true.
    /// When enabled, writes a log to Documents\vatSys Files\EditViewCentrepoint.log, including a one-off dump of the
    /// members of each vatSys display control the plugin touches (the vatSys SDK doesn't expose these,
    /// so the plugin finds them by reflection and the dump shows exactly what it had to work with).
    /// </summary>
    internal static class Log
    {
        const BindingFlags Decl = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static readonly object Gate = new object();
        static readonly HashSet<Type> Dumped = new HashSet<Type>();
        static string _path;
        static bool? _enabled;

        public static bool Enabled
        {
            get
            {
                if (_enabled == null) _enabled = ReadEnabledFlag();
                return _enabled.Value;
            }
        }

        static bool ReadEnabledFlag()
        {
            try
            {
                string cfg = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), "EditViewCentrepoint.cfg");
                if (!File.Exists(cfg)) return false;
                foreach (string raw in File.ReadAllLines(cfg))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                    if (line.Substring(0, eq).Trim().Equals("logging", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(eq + 1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        public static string Path
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSys Files");
                    if (!Directory.Exists(dir)) dir = System.IO.Path.GetTempPath();
                    _path = System.IO.Path.Combine(dir, "EditViewCentrepoint.log");
                }
                catch { _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EditViewCentrepoint.log"); }
                return _path;
            }
        }

        public static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                    File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { /* logging must never break vatSys */ }
        }

        public static void DumpOnce(Form form, Control control)
        {
            if (!Enabled) return;
            try
            {
                Type t = control.GetType();
                lock (Gate) { if (!Dumped.Add(t)) return; }

                var sb = new StringBuilder();
                sb.AppendLine("=== Member dump for display control " + t.FullName + " (window " + form.GetType().FullName + " '" + form.Text + "') ===");

                sb.AppendLine("-- Controls in window --");
                DumpTree(form, sb, 0);

                for (Type c = t; c != null && c != typeof(Control); c = c.BaseType)
                    DumpType(c, sb);

                Type mmi = typeof(vatsys.MMI);
                sb.AppendLine("-- static members of " + mmi.FullName + " that look view related --");
                foreach (MemberInfo m in mmi.GetMembers(Decl))
                {
                    string n = m.Name.ToLowerInvariant();
                    if (n.Contains("cent") || n.Contains("range") || n.Contains("pan") || n.Contains("asd") || n.Contains("viewport"))
                        sb.AppendLine("   " + m.MemberType + " " + m);
                }
                Write(sb.ToString());
            }
            catch (Exception ex) { Write("Dump failed: " + ex); }
        }


        /// <summary>Short description of a value for the log (coordinates as lat,lon).</summary>
        public static string Describe(object o)
        {
            if (o == null) return "null";
            try
            {
                Type t = o.GetType();
                var lat = t.GetProperty("Latitude", Decl | BindingFlags.Instance) ?? (MemberInfo)t.GetField("Latitude", Decl | BindingFlags.Instance);
                var lon = t.GetProperty("Longitude", Decl | BindingFlags.Instance) ?? (MemberInfo)t.GetField("Longitude", Decl | BindingFlags.Instance);
                if (lat != null && lon != null)
                    return "(" + Read(lat, o) + ", " + Read(lon, o) + ")";
                string s = o.ToString();
                return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
            }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        static object Read(MemberInfo m, object o)
        {
            var f = m as FieldInfo;
            return f != null ? f.GetValue(o) : ((PropertyInfo)m).GetValue(o, null);
        }

        /// <summary>Logs every instance field/property of an object with its value.</summary>
        public static void DumpObject(string label, object o)
        {
            if (!Enabled) return;
            if (o == null) { Write(label + " = null"); return; }
            var sb = new StringBuilder();
            sb.AppendLine("-- " + label + " (" + o.GetType().FullName + ") --");
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(inst | BindingFlags.DeclaredOnly))
                {
                    string v; try { v = Describe(f.GetValue(o)); } catch (Exception ex) { v = "<" + ex.GetType().Name + ">"; }
                    sb.AppendLine("   field    " + f.FieldType.Name + " " + f.Name + " = " + v);
                }
                foreach (PropertyInfo p in t.GetProperties(inst | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                    string v; try { v = Describe(p.GetValue(o, null)); } catch (Exception ex) { v = "<" + ex.GetType().Name + ">"; }
                    sb.AppendLine("   property " + p.PropertyType.Name + " " + p.Name + " = " + v);
                }
            }
            Write(sb.ToString());
        }

        static void DumpTree(Control parent, StringBuilder sb, int depth)
        {
            foreach (Control c in parent.Controls)
            {
                sb.AppendLine(new string(' ', 3 + depth * 2) + c.GetType().FullName + "  " + c.Width + "x" + c.Height + (c.Visible ? "" : " (hidden)"));
                DumpTree(c, sb, depth + 1);
            }
        }

        static void DumpType(Type t, StringBuilder sb)
        {
            sb.AppendLine("-- " + t.FullName + " --");
            foreach (FieldInfo f in t.GetFields(Decl)) sb.AppendLine("   field    " + f.FieldType.Name + " " + f.Name + (f.IsStatic ? " (static)" : ""));
            foreach (PropertyInfo p in t.GetProperties(Decl)) sb.AppendLine("   property " + p.PropertyType.Name + " " + p.Name);
            foreach (MethodInfo m in t.GetMethods(Decl))
            {
                if (m.IsSpecialName) continue;
                sb.AppendLine("   method   " + m);
            }
        }
    }
}
