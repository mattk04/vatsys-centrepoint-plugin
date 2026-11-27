using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace EditViewCentrepoint
{
    /// <summary>
    /// Adds "Edit View Centrepoint" to the Tools menu of the main air display, secondary air displays and ground windows.
    /// </summary>
    [Export(typeof(IPlugin))]
    public class Plugin : IPlugin
    {
        const string MenuTitle = "Edit View Centrepoint";

        public string Name { get { return "Edit View Centrepoint"; } }

        public Plugin()
        {
            try { RegisterMenus(); }
            catch (Exception ex) { Log.Write("Plugin startup failed: " + ex); }
        }

        public void OnFDRUpdate(FDP2.FDR updated) { }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

        // ---------------------------------------------------------------------------------------------
        static void RegisterMenus()
        {
            Log.Write("Plugin loaded.");
            Log.Write("CustomToolStripMenuItemWindowType values: " + string.Join(", ", Enum.GetNames(typeof(CustomToolStripMenuItemWindowType))));
            Log.Write("CustomToolStripMenuItemCategory values: " + string.Join(", ", Enum.GetNames(typeof(CustomToolStripMenuItemCategory))));

            var category = Resolve<CustomToolStripMenuItemCategory>("Tools", new[] { "Tools" }, new[] { "tool" }, null);
            if (!category.HasValue) return;

            // Resolve each window type by name. The matching is deliberately forgiving; the values it picked are
            // logged so they can be checked against the enum listing above.
            var taken = new List<string>();
            AddRole("main air display", category.Value, new[] { "Main", "MainASD" }, new[] { "main" }, taken);
            AddRole("ground window", category.Value, new[] { "Ground", "GroundDisplay", "ASMGCS", "SMR" }, new[] { "ground", "asmgcs", "smr" }, taken);
            AddRole("secondary air display", category.Value,
                new[] { "Secondary", "SecondaryASD", "SecondaryAirDisplay", "SecondaryAir", "ASD", "AirDisplay" },
                new[] { "secondary", "asd", "air" }, taken);
        }

        static void AddRole(string role, CustomToolStripMenuItemCategory category, string[] exact, string[] contains, List<string> taken)
        {
            try
            {
                var type = Resolve<CustomToolStripMenuItemWindowType>(role, exact, contains, taken);
                if (!type.HasValue) return;
                taken.Add(type.Value.ToString());

                var menuItem = new ToolStripMenuItem(MenuTitle);
                menuItem.Click += OnMenuClick;
                MMI.AddCustomMenuItem(new CustomToolStripMenuItem(type.Value, category, menuItem));
                Log.Write("Added '" + MenuTitle + "' for " + role + " -> " + type.Value + " / " + category);
            }
            catch (Exception ex) { Log.Write("Couldn't add menu item for " + role + ": " + ex); }
        }

        static T? Resolve<T>(string what, string[] exact, string[] contains, List<string> excluding) where T : struct
        {
            string[] names = Enum.GetNames(typeof(T)).Where(n => excluding == null || !excluding.Contains(n)).ToArray();

            string hit = exact.Select(e => names.FirstOrDefault(n => string.Equals(n, e, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n != null)
                      ?? contains.Select(c => names.FirstOrDefault(n => n.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)).FirstOrDefault(n => n != null);

            if (hit == null)
            {
                Log.Write("No " + typeof(T).Name + " matches '" + what + "'; skipped. Available: " + string.Join(", ", names));
                return null;
            }
            return (T)Enum.Parse(typeof(T), hit);
        }

        // ---------------------------------------------------------------------------------------------
        static void OnMenuClick(object sender, EventArgs e)
        {
            try
            {
                Form form = FindOwningForm(sender as ToolStripItem) ?? Form.ActiveForm;
                if (form == null)
                {
                    Log.Write("Menu clicked but no owning window could be determined.");
                    return;
                }
                EditSession.Begin(form);
            }
            catch (Exception ex) { Log.Write("Menu click failed: " + ex); }
        }

        /// <summary>Walks up from a menu item (through nested drop-downs) to the window whose menu bar contains it.</summary>
        static Form FindOwningForm(ToolStripItem item)
        {
            for (int guard = 0; item != null && guard < 16; guard++)
            {
                var dropDown = item.Owner as ToolStripDropDown;
                if (dropDown != null && dropDown.OwnerItem != null) { item = dropDown.OwnerItem; continue; }
                return item.Owner != null ? item.Owner.FindForm() : null;
            }
            return null;
        }
    }
}
