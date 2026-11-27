# vatSys Centrepoint Plugin
A vatSys plugin which allows fine control over the movement of the air & ground window viewports.

Adds **Tools → Edit View Centrepoint** to the main air display, secondary air displays and ground windows.

| Action | Result |
| --- | --- |
| Click the menu item | Enters edit mode. Cursor becomes an **X**; moving the mouse pans that window's view. |
| **Enter** or **left click** on the display | Keeps the view where it is and makes it the new centrepoint. |
| **Esc** or **middle click** | Leaves edit mode and restores the viewport exactly as it was when you started. |

## Build

1. Install the .NET SDK (or Visual Studio) able to target **.NET Framework 4.8**.
2. Run `dotnet build -c Release`.
If `vatSys.exe` isn't at `C:\Program Files (x86)\vatSys\bin\vatSys.exe`, pass your path:
   `dotnet build -c Release -p:VatSysDir="C:\path\to\vatSys\bin"`
3. Copy output from `bin\Release\net48\EditViewCentrepoint.dll` to the vatSys plugins directory

## Troubleshooting

Logging is off by default. To turn it on, add `Logging=true` to `Documents\vatSys Files\EditViewCentrepoint.cfg`; the plugin then writes `EditViewCentrepoint.log` in the same folder. It records:
* the `CustomToolStripMenuItemWindowType` / `Category` values and which ones it picked for each window kind,
* what it found on each display control (`SetDisplayCenter`, `ConvertScreenToLL`, render parameters), plus the current values of the view-related fields,
* a full member dump of each display control type the first time it is used.

vatSys' SDK doesn't expose its display controls, so the pan/centre logic is found by reflection. If a window
doesn't pan, or pans at the wrong speed, send that log and the member names can be pinned down exactly.
Optional settings file `Documents\vatSys Files\EditViewCentrepoint.cfg` (no rebuild needed):

```
Logging=false                # true = write EditViewCentrepoint.log
PanScale=1.0                 # pan speed multiplier
SetCenterFlags=false,false   # starting values for SetDisplayCenter's two bool parameters
Redraw=RedrawBackground      # methods called after each move to repaint (empty = none)
```
