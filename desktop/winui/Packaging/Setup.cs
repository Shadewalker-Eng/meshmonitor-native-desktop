using System;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Setup
{
    [STAThread]
    private static void Main()
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        Application.EnableVisualStyles();
        using (var mutex = new Mutex(false, @"Local\MeshMonitorNativeSetup"))
        {
            try { if (!mutex.WaitOne(0)) return; } catch (AbandonedMutexException) { }
            using (var window = new Form { Text = "MeshMonitor Setup", Width = 480, Height = 165, StartPosition = FormStartPosition.CenterScreen })
            {
                var label = new Label { Left = 16, Top = 15, Width = 435, Height = 46, Text = "Install MeshMonitor Native for this Windows account. The Start menu shortcut will open this version using your existing data." };
                var button = new Button { Left = 340, Top = 70, Width = 95, Text = "Install" };
                bool installing = false;
                window.FormClosing += (s, e) => { if (installing) e.Cancel = true; };
                button.Click += async (s, e) =>
                {
                    if (button.Text == "Close") { window.Close(); return; }
                    installing = true;
                    button.Enabled = false;
                    label.Text = "Installing application files…";
                    try
                    {
                        await Task.Run((Action)Install);
                        label.Text = "Installed. Open MeshMonitor Native from the Start menu. Quit any earlier instance from its tray menu before launching.";
                        button.Text = "Close";
                    }
                    catch (Exception ex) { label.Text = ex.Message; button.Text = "Retry"; }
                    finally { installing = false; button.Enabled = true; }
                };
                window.Controls.Add(label);
                window.Controls.Add(button);
                Application.Run(window);
            }
        }
    }

    private static void Install()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MeshMonitor Native");
        Directory.CreateDirectory(root);
        string version = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N");
        string directory = Path.Combine(root, version);
        string exe = Path.Combine(directory, "MeshMonitor.Native.exe");
        if (!Directory.Exists(directory))
        {
            string stage = Path.Combine(root, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("MeshMonitor.zip"))
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    archive.ExtractToDirectory(stage);
                }
                if (!File.Exists(Path.Combine(stage, "MeshMonitor.Native.exe")) ||
                    !File.Exists(Path.Combine(stage, "resources", "dist", "server", "server.js")))
                    throw new InvalidDataException("Incomplete application package.");
                Directory.Move(stage, directory);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programs);
        string shortcut = Path.Combine(programs, "MeshMonitor Native.lnk");
        string temporary = Path.Combine(programs, "MeshMonitor-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(temporary);
            try { link.TargetPath = exe; link.WorkingDirectory = directory; link.IconLocation = exe + ",0"; link.Save(); }
            finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
            if (File.Exists(shortcut)) File.Replace(temporary, shortcut, null);
            else File.Move(temporary, shortcut);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
