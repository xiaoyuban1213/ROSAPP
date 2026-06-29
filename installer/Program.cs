using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ROSAppInstaller;

internal static class Program
{
    private const string AppName = "ROS-APP";
    private const string Publisher = "Yuban-Network";
    private static readonly string InstallerVersion = ResolveInstallerVersion();
    private static readonly object LogLock = new();
    private static string LogPath => Path.Combine(Path.GetTempPath(), "ROS-APP-Setup", "setup.log");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const int IdYes = 6;
    private const uint MbYesNo = 0x04;
    private const uint MbIconInfo = 0x40;
    private const uint MbIconError = 0x10;

    private static string ResolveInstallerVersion()
    {
        var infoVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            var idx = infoVersion.IndexOf('+');
            return idx > 0 ? infoVersion[..idx] : infoVersion;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }

        return "未知";
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            Exception? failure = null;
            string? installedExe = null;
            string? installDir = null;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var form = new InstallForm();
            form.OnStartInstall += async (_, __) =>
            {
                try
                {
                    installDir = form.InstallDirectory;
                    installedExe = await Task.Run(() => RunInstall(form.SetStatus, installDir));
                    form.SetStatus("安装完成，正在收尾...");
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    form.Close();
                }
            };

            Application.Run(form);

            if (failure is not null)
            {
                throw failure;
            }

            var msg = "安装完成，已创建快捷方式。是否立即启动？";
            var result = MessageBoxW(IntPtr.Zero, msg, $"{AppName} 安装完成", MbYesNo | MbIconInfo);
            if (result == IdYes && !string.IsNullOrWhiteSpace(installedExe) && File.Exists(installedExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExe,
                    WorkingDirectory = Path.GetDirectoryName(installedExe)!,
                    UseShellExecute = true
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            var msg = $"安装失败：{ex.Message}\r\n请查看日志：{LogPath}";
            WriteLog(ex.ToString());
            MessageBoxW(IntPtr.Zero, msg, $"{AppName} 安装失败", MbIconError);
            return 1;
        }
    }

    private static string RunInstall(Action<string>? updateStatus, string installDir)
    {
        WriteLog("启动安装程序。");
        updateStatus?.Invoke("正在准备安装...");

        var tempRoot = Path.Combine(Path.GetTempPath(), "ROS-APP-Setup");
        Directory.CreateDirectory(tempRoot);
        WriteLog($"临时目录：{tempRoot}");

        var payloadPath = Path.Combine(tempRoot, "payload.zip");
        updateStatus?.Invoke("正在解压安装包...");
        ExtractPayload(payloadPath);
        WriteLog("安装包解压完成。");
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new InvalidOperationException("安装目录无效。");
        }

        updateStatus?.Invoke("正在写入文件...");
        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, true);
            WriteLog($"清理旧版本：{installDir}");
        }
        Directory.CreateDirectory(installDir);

        ZipFile.ExtractToDirectory(payloadPath, installDir, true);
        WriteLog($"文件已写入：{installDir}");

        var iconPath = Path.Combine(installDir, "app.ico");
        var exePath = Path.Combine(installDir, $"{AppName}.exe");
        updateStatus?.Invoke("正在创建快捷方式...");
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            $"{AppName}.lnk"), exePath, installDir, iconPath);
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk"), exePath, installDir, iconPath);
        WriteLog("快捷方式创建完成。");

        updateStatus?.Invoke("正在写入卸载信息...");
        WriteUninstallScript(installDir);
        WriteUninstallRegistry(installDir, exePath);
        WriteLog("卸载信息写入完成。");

        return exePath;
    }

    private static void ExtractPayload(string payloadPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
        if (stream is null)
        {
            throw new InvalidOperationException("找不到安装包资源 payload.zip。");
        }

        using var file = File.Create(payloadPath);
        stream.CopyTo(file);
    }

    private static void CreateShortcut(string linkPath, string targetPath, string workingDir, string iconPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new InvalidOperationException("无法创建快捷方式（WScript.Shell 不可用）。");
        }
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDir;
        shortcut.IconLocation = iconPath;
        shortcut.Save();
    }

    private static void WriteUninstallScript(string installDir)
    {
        var uninstallPath = Path.Combine(installDir, "uninstall.ps1");
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lines = new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$target = \"{installDir}\"",
            $"$startMenu = \"{startMenu}\"",
            $"$desktop = \"{desktop}\"",
            $"Remove-Item -Force (Join-Path $startMenu \"{AppName}.lnk\") -ErrorAction SilentlyContinue",
            $"Remove-Item -Force (Join-Path $desktop \"{AppName}.lnk\") -ErrorAction SilentlyContinue",
            "Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue",
            $"Remove-Item -Recurse -Force \"HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{AppName}\" -ErrorAction SilentlyContinue"
        };
        File.WriteAllLines(uninstallPath, lines);
    }

    private static void WriteUninstallRegistry(string installDir, string exePath)
    {
        var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "1.0.0.0";
        using var key = Registry.CurrentUser.CreateSubKey(
            $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{AppName}"
        );
        if (key is null)
        {
            throw new InvalidOperationException("无法写入卸载注册表项。");
        }
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"powershell -ExecutionPolicy Bypass -File \"{Path.Combine(installDir, "uninstall.ps1")}\"");
    }

    private static void WriteLog(string message)
    {
        try
        {
            var logDir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            lock (LogLock)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n");
            }
        }
        catch
        {
            // ignore logging failures
        }
    }

    private sealed class InstallForm : Form
    {
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly TextBox _installPath;
        private readonly Button _browseButton;
        private readonly Button _installButton;
        private readonly Button _cancelButton;

        public event EventHandler? OnStartInstall;

        public string InstallDirectory => _installPath.Text.Trim();

        public InstallForm()
        {
            Text = $"{AppName} 安装程序 v{InstallerVersion}";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Width = 520;
            Height = 290;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var versionLabel = new Label
            {
                Text = $"版本: {InstallerVersion}    发布方: {Publisher}",
                AutoSize = true,
                Left = 24,
                Top = 12,
                ForeColor = Color.DimGray
            };

            var pathLabel = new Label
            {
                Text = "安装位置",
                AutoSize = true,
                Left = 24,
                Top = 40
            };

            _installPath = new TextBox
            {
                Left = 24,
                Top = 65,
                Width = 360,
                Text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    AppName
                )
            };

            _browseButton = new Button
            {
                Text = "浏览...",
                Left = 394,
                Top = 63,
                Width = 80
            };
            _browseButton.Click += (_, __) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "选择安装目录",
                    SelectedPath = _installPath.Text
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _installPath.Text = dialog.SelectedPath;
                }
            };

            _status = new Label
            {
                Text = "准备安装（请确认安装位置）",
                AutoSize = false,
                Width = 450,
                Height = 30,
                Left = 24,
                Top = 100
            };

            _progress = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Width = 450,
                Height = 18,
                Left = 24,
                Top = 135
            };

            _installButton = new Button
            {
                Text = "开始安装",
                Left = 274,
                Top = 180,
                Width = 90
            };
            _installButton.Click += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(_installPath.Text))
                {
                    MessageBoxW(Handle, "安装路径不能为空。", $"{AppName} 安装程序", MbIconError);
                    return;
                }

                ToggleInputs(false);
                OnStartInstall?.Invoke(this, EventArgs.Empty);
            };

            _cancelButton = new Button
            {
                Text = "取消",
                Left = 374,
                Top = 180,
                Width = 90
            };
            _cancelButton.Click += (_, __) => Close();

            Controls.Add(versionLabel);
            Controls.Add(pathLabel);
            Controls.Add(_installPath);
            Controls.Add(_browseButton);
            Controls.Add(_status);
            Controls.Add(_progress);
            Controls.Add(_installButton);
            Controls.Add(_cancelButton);
        }

        public void SetStatus(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => _status.Text = text));
                return;
            }

            _status.Text = text;
        }

        private void ToggleInputs(bool enabled)
        {
            _installPath.Enabled = enabled;
            _browseButton.Enabled = enabled;
            _installButton.Enabled = enabled;
            _cancelButton.Enabled = enabled;
        }
    }
}
