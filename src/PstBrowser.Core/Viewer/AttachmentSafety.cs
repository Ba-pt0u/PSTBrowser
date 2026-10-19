using System;
using System.Collections.Generic;
using System.IO;

namespace PstBrowser.Core.Viewer
{
    /// <summary>Attachment types that run code when opened (Windows, macOS, Linux): the viewer never opens them directly.</summary>
    public static class AttachmentSafety
    {
        private static readonly HashSet<string> DangerousExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Windows
            ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
            ".msi", ".msp", ".lnk", ".reg", ".jar", ".cpl", ".dll", ".sys", ".inf", ".scf", ".url", ".appx", ".msix", ".application",
            ".gadget", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".docm", ".xlsm", ".pptm", ".xlam", ".settingcontent-ms",
            // macOS
            ".app", ".command", ".tool", ".terminal", ".pkg", ".mpkg", ".dmg", ".scpt", ".scptd", ".applescript", ".workflow",
            ".webloc", ".inetloc", ".fileloc", ".dylib", ".kext", ".prefpane", ".plugin", ".mobileconfig",
            // Linux and scripts in general
            ".sh", ".bash", ".zsh", ".csh", ".ksh", ".run", ".desktop", ".appimage", ".deb", ".rpm", ".so", ".py", ".pl", ".rb",
        };

        public static bool IsDangerous(string fileName) => DangerousExtensions.Contains(Path.GetExtension(fileName ?? ""));
    }
}
