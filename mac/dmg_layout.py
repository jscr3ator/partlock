"""Writes the .DS_Store that lays out the PartLock DMG window:
PartLock.app on the left, Applications on the right, the README link underneath."""
import sys
from ds_store import DSStore

staging, readme = sys.argv[1], sys.argv[2]
with DSStore.open(staging + "/.DS_Store", "w+") as d:
    d["."]["bwsp"] = {
        "ShowStatusBar": False, "ShowToolbar": False, "ShowPathbar": False, "ShowSidebar": False,
        "ContainerShowSidebar": False, "SidebarWidth": 0, "WindowBounds": "{{200, 120}, {600, 440}}",
    }
    d["."]["icvp"] = {
        "viewOptionsVersion": 1, "arrangeBy": "none", "iconSize": 112.0, "gridSpacing": 100.0,
        "textSize": 13.0, "labelOnBottom": True, "showItemInfo": False, "showIconPreview": True,
        "backgroundType": 0, "backgroundColorRed": 1.0, "backgroundColorGreen": 1.0, "backgroundColorBlue": 1.0,
        "gridOffsetX": 0.0, "gridOffsetY": 0.0, "scrollPositionX": 0.0, "scrollPositionY": 0.0,
    }
    d["."]["vSrn"] = ("long", 1)
    d["PartLock.app"]["Iloc"] = (150, 150)
    d["Applications"]["Iloc"] = (450, 150)
    d[readme]["Iloc"] = (300, 320)
print("DMG layout written")
