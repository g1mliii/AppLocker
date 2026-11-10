using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AppLocker
{
    public class ConfigData
    {
        public string Salt { get; set; }
        public string PasswordHash { get; set; }
        public List<string> LockedApps { get; set; }
        public int UnlockDurationMinutes { get; set; } // 0 = until closed, >0 = minutes, -1 = session/reboot
        public bool ShowTrayIcon { get; set; }
        public bool HideWindowWhenLocked { get; set; }

        public ConfigData()
        {
            Salt = Guid.NewGuid().ToString("N");
            PasswordHash = "";
            LockedApps = new List<string>();
            UnlockDurationMinutes = 0; // Lock when closed by default
            ShowTrayIcon = true;
            HideWindowWhenLocked = true;
        }
    }

    internal static class NativeMethods
    {
        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
    }

    public static class SecurityHelper
    {
        public static string HashPassword(string password, string salt)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(salt + ":" + password);
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static bool VerifyPassword(string input, string salt, string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            string inputHash = HashPassword(input, salt);
            return string.Equals(inputHash, hash, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class AppLockState
    {
        public string AppName;
        public bool IsAuthorized;
        public DateTime AuthorizedUntil;
        public bool IsPromptShowing;

        public AppLockState(string name)
        {
            AppName = name;
            IsAuthorized = false;
            AuthorizedUntil = DateTime.MinValue;
            IsPromptShowing = false;
        }
    }

    public class AppLockerService : IDisposable
    {
        private ConfigData config;
        private string configPath;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem statusMenuItem;
        private System.Threading.Timer monitorTimer;
        private System.Threading.Timer maintenanceTimer;
        private volatile bool isMonitoring = false;
        private readonly object syncLock = new object();
        private SynchronizationContext uiContext;

        private Dictionary<string, AppLockState> appStates = new Dictionary<string, AppLockState>(StringComparer.OrdinalIgnoreCase);
        private HashSet<int> suspendedPids = new HashSet<int>();
        private Icon cachedPadlockIcon = null;

        public bool Initialize()
        {
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            configPath = Path.Combine(appDir, "config.json");

            LoadConfig();

            SystemEvents.SessionEnding += OnSessionEnding;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            if (string.IsNullOrEmpty(config.PasswordHash))
            {
                using (SettingsForm sf = new SettingsForm(config, true))
                {
                    if (sf.ShowDialog() == DialogResult.OK)
                    {
                        SaveConfig();
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            SetupTray();
            StartMonitoring();

            new System.Threading.Timer((s) => { TrimMemory(); }, null, 1500, Timeout.Infinite);
            maintenanceTimer = new System.Threading.Timer(OnMaintenance, null, 120000, 120000);
            return true;
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            ResumeAllSuspended();
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            ResumeAllSuspended();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    config = serializer.Deserialize<ConfigData>(json);
                }
            }
            catch { }

            if (config == null)
            {
                config = new ConfigData();
            }
        }

        public void SaveConfig()
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(config);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save config: " + ex.Message, "AppLocker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            UpdateTrayStatus();
        }

        private Icon GetPadlockIcon()
        {
            if (cachedPadlockIcon != null) return cachedPadlockIcon;

            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (Brush bodyBrush = new SolidBrush(Color.FromArgb(240, 178, 38)))
                    {
                        GraphicsPath path = new GraphicsPath();
                        Rectangle b = new Rectangle(5, 12, 22, 17);
                        int r = 3;
                        path.AddArc(b.X, b.Y, r * 2, r * 2, 180, 90);
                        path.AddArc(b.Right - r * 2, b.Y, r * 2, r * 2, 270, 90);
                        path.AddArc(b.Right - r * 2, b.Bottom - r * 2, r * 2, r * 2, 0, 90);
                        path.AddArc(b.X, b.Bottom - r * 2, r * 2, r * 2, 90, 90);
                        path.CloseFigure();
                        g.FillPath(bodyBrush, path);
                        using (Pen borderPen = new Pen(Color.FromArgb(170, 115, 15), 1.5f))
                        {
                            g.DrawPath(borderPen, path);
                        }
                    }

                    using (Pen shacklePen = new Pen(Color.FromArgb(90, 100, 115), 3f))
                    {
                        g.DrawArc(shacklePen, 9, 3, 14, 16, 180, 180);
                        g.DrawLine(shacklePen, 9, 11, 9, 13);
                        g.DrawLine(shacklePen, 23, 11, 23, 13);
                    }

                    using (Brush keyholeBrush = new SolidBrush(Color.FromArgb(50, 35, 10)))
                    {
                        g.FillEllipse(keyholeBrush, 14, 16, 4, 4);
                        g.FillPolygon(keyholeBrush, new Point[] {
                            new Point(14, 18),
                            new Point(18, 18),
                            new Point(17, 24),
                            new Point(15, 24)
                        });
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                cachedPadlockIcon = Icon.FromHandle(hIcon);
                return cachedPadlockIcon;
            }
        }

        private void SetupTray()
        {
            if (!config.ShowTrayIcon) return;

            trayMenu = new ContextMenuStrip();
            statusMenuItem = new ToolStripMenuItem("AppLocker: Active");
            statusMenuItem.Enabled = false;
            statusMenuItem.Font = new Font(trayMenu.Font, FontStyle.Bold);
            trayMenu.Items.Add(statusMenuItem);
            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem lockAllItem = new ToolStripMenuItem("Lock All Apps Now", null, (s, e) => {
                lock (syncLock)
                {
                    foreach (KeyValuePair<string, AppLockState> kv in appStates)
                    {
                        kv.Value.IsAuthorized = false;
                        kv.Value.AuthorizedUntil = DateTime.MinValue;
                    }
                }
                TrimMemory();
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(2000, "AppLocker", "All locked applications will require password again.", ToolTipIcon.Info);
                }
            });
            trayMenu.Items.Add(lockAllItem);

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings / Manage Locked Apps...", null, (s, e) => {
                if (PromptMasterPassword("Open Settings"))
                {
                    using (SettingsForm sf = new SettingsForm(config, false))
                    {
                        if (sf.ShowDialog() == DialogResult.OK)
                        {
                            SaveConfig();
                        }
                    }
                }
            });
            trayMenu.Items.Add(settingsItem);

            ToolStripMenuItem restartItem = new ToolStripMenuItem("Restart Locker", null, (s, e) => {
                RestartSelf();
            });
            trayMenu.Items.Add(restartItem);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit AppLocker", null, (s, e) => {
                if (PromptMasterPassword("Exit AppLocker"))
                {
                    Dispose();
                    Application.Exit();
                }
            });
            trayMenu.Items.Add(exitItem);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "AppLocker - Lightweight App Protection";
            trayIcon.Icon = GetPadlockIcon();
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;

            UpdateTrayStatus();
        }

        private void UpdateTrayStatus()
        {
            if (statusMenuItem != null && config != null)
            {
                int count = config.LockedApps != null ? config.LockedApps.Count : 0;
                statusMenuItem.Text = string.Format("AppLocker: Active ({0} locked)", count);
            }
        }

        private bool PromptMasterPassword(string actionName)
        {
            using (PasswordPromptForm pf = new PasswordPromptForm("Master Password Required", "Enter master password to " + actionName + ":", config))
            {
                return pf.ShowDialog() == DialogResult.OK;
            }
        }

        private void ResumeAllSuspended()
        {
            lock (syncLock)
            {
                foreach (int pid in suspendedPids)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            NativeMethods.NtResumeProcess(p.Handle);
                        }
                    }
                    catch { }
                }
                suspendedPids.Clear();
            }
        }

        private void RestartSelf()
        {
            ResumeAllSuspended();
            try
            {
                string exePath = Application.ExecutablePath;
                Process.Start(new ProcessStartInfo()
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            catch { }

            Dispose();
            Application.Exit();
        }

        private void StartMonitoring()
        {
            monitorTimer = new System.Threading.Timer(OnMonitorTick, null, 100, 350);
        }

        private void StopMonitoring()
        {
            if (monitorTimer != null)
            {
                monitorTimer.Dispose();
                monitorTimer = null;
            }
            if (maintenanceTimer != null)
            {
                maintenanceTimer.Dispose();
                maintenanceTimer = null;
            }
        }

        public void Dispose()
        {
            StopMonitoring();
            ResumeAllSuspended();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

        private AppLockState GetOrCreateState(string cleanName)
        {
            AppLockState state;
            if (!appStates.TryGetValue(cleanName, out state))
            {
                state = new AppLockState(cleanName);
                appStates[cleanName] = state;
            }
            return state;
        }

        private void OnMonitorTick(object stateObj)
        {
            if (isMonitoring) return;
            lock (syncLock)
            {
                if (isMonitoring) return;
                isMonitoring = true;
            }

            try
            {
                if (config.LockedApps == null || config.LockedApps.Count == 0) return;

                DateTime now = DateTime.UtcNow;

                for (int i = 0; i < config.LockedApps.Count; i++)
                {
                    string target = config.LockedApps[i];
                    if (string.IsNullOrEmpty(target)) continue;

                    string cleanName = target.Trim();
                    if (cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanName = cleanName.Substring(0, cleanName.Length - 4);
                    }

                    Process[] procs = Process.GetProcessesByName(cleanName);

                    // Case A: App is NOT running
                    if (procs.Length == 0)
                    {
                        lock (syncLock)
                        {
                            if (appStates.ContainsKey(cleanName))
                            {
                                AppLockState s = appStates[cleanName];
                                if (config.UnlockDurationMinutes <= 0 || now > s.AuthorizedUntil)
                                {
                                    s.IsAuthorized = false;
                                }
                                s.IsPromptShowing = false;
                            }
                        }
                        continue;
                    }

                    // Case B: App IS running
                    AppLockState appState;
                    lock (syncLock)
                    {
                        appState = GetOrCreateState(cleanName);

                        if (appState.IsAuthorized)
                        {
                            if (config.UnlockDurationMinutes > 0 && now > appState.AuthorizedUntil)
                            {
                                appState.IsAuthorized = false;
                            }
                            else
                            {
                                for (int j = 0; j < procs.Length; j++)
                                {
                                    int pid = procs[j].Id;
                                    if (suspendedPids.Contains(pid))
                                    {
                                        NativeMethods.NtResumeProcess(procs[j].Handle);
                                        suspendedPids.Remove(pid);
                                    }
                                    procs[j].Dispose();
                                }
                                continue;
                            }
                        }

                        if (appState.IsPromptShowing)
                        {
                            for (int j = 0; j < procs.Length; j++)
                            {
                                int pid = procs[j].Id;
                                if (!suspendedPids.Contains(pid))
                                {
                                    NativeMethods.NtSuspendProcess(procs[j].Handle);
                                    suspendedPids.Add(pid);
                                }
                                procs[j].Dispose();
                            }
                            continue;
                        }

                        appState.IsPromptShowing = true;

                        for (int j = 0; j < procs.Length; j++)
                        {
                            try
                            {
                                int pid = procs[j].Id;
                                if (!suspendedPids.Contains(pid))
                                {
                                    NativeMethods.NtSuspendProcess(procs[j].Handle);
                                    suspendedPids.Add(pid);
                                }
                                if (config.HideWindowWhenLocked && procs[j].MainWindowHandle != IntPtr.Zero)
                                {
                                    NativeMethods.ShowWindow(procs[j].MainWindowHandle, 6 /* SW_MINIMIZE */);
                                }
                            }
                            catch { }
                            finally
                            {
                                procs[j].Dispose();
                            }
                        }
                    }

                    string appToPrompt = cleanName;
                    uiContext.Post((o) => {
                        PromptAndUnlockApp(appToPrompt);
                    }, null);
                }
            }
            catch { }
            finally
            {
                lock (syncLock)
                {
                    isMonitoring = false;
                }
            }
        }

        private void PromptAndUnlockApp(string cleanName)
        {
            bool authorized = false;

            try
            {
                Process[] check = Process.GetProcessesByName(cleanName);
                if (check.Length == 0)
                {
                    lock (syncLock)
                    {
                        if (appStates.ContainsKey(cleanName))
                        {
                            appStates[cleanName].IsPromptShowing = false;
                        }
                    }
                    return;
                }
                for (int i = 0; i < check.Length; i++) check[i].Dispose();

                using (PasswordPromptForm pf = new PasswordPromptForm(cleanName, string.Format("'{0}' is locked. Enter password to unlock:", cleanName), config))
                {
                    DialogResult res = pf.ShowDialog();
                    if (res == DialogResult.OK)
                    {
                        authorized = true;
                    }
                }
            }
            catch { }

            lock (syncLock)
            {
                AppLockState state = GetOrCreateState(cleanName);
                state.IsPromptShowing = false;

                Process[] currentProcs = Process.GetProcessesByName(cleanName);

                if (authorized)
                {
                    state.IsAuthorized = true;
                    if (config.UnlockDurationMinutes > 0)
                    {
                        state.AuthorizedUntil = DateTime.UtcNow.AddMinutes(config.UnlockDurationMinutes);
                    }
                    else
                    {
                        state.AuthorizedUntil = DateTime.MaxValue;
                    }

                    for (int i = 0; i < currentProcs.Length; i++)
                    {
                        try
                        {
                            int pid = currentProcs[i].Id;
                            if (suspendedPids.Contains(pid))
                            {
                                NativeMethods.NtResumeProcess(currentProcs[i].Handle);
                                suspendedPids.Remove(pid);
                            }
                            if (config.HideWindowWhenLocked && currentProcs[i].MainWindowHandle != IntPtr.Zero)
                            {
                                NativeMethods.ShowWindow(currentProcs[i].MainWindowHandle, 9 /* SW_RESTORE */);
                                NativeMethods.SetForegroundWindow(currentProcs[i].MainWindowHandle);
                            }
                        }
                        catch { }
                        finally
                        {
                            currentProcs[i].Dispose();
                        }
                    }
                }
                else
                {
                    state.IsAuthorized = false;
                    for (int i = 0; i < currentProcs.Length; i++)
                    {
                        try
                        {
                            suspendedPids.Remove(currentProcs[i].Id);
                            currentProcs[i].Kill();
                        }
                        catch { }
                        finally
                        {
                            currentProcs[i].Dispose();
                        }
                    }
                }
            }

            TrimMemory();
        }

        private void OnMaintenance(object state)
        {
            lock (syncLock)
            {
                List<int> deadPids = new List<int>();
                foreach (int pid in suspendedPids)
                {
                    try
                    {
                        using (Process check = Process.GetProcessById(pid))
                        {
                            if (check.HasExited) deadPids.Add(pid);
                        }
                    }
                    catch
                    {
                        deadPids.Add(pid);
                    }
                }
                for (int i = 0; i < deadPids.Count; i++)
                {
                    suspendedPids.Remove(deadPids[i]);
                }
            }

            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    if (current.WorkingSet64 > 10 * 1024 * 1024)
                    {
                        TrimMemory();
                    }
                }
            }
            catch { }
        }

        private void TrimMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced);
                NativeMethods.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }

    public class PasswordPromptForm : Form
    {
        private TextBox txtPassword;
        private Label lblError;
        private ConfigData config;
        private int failedAttempts = 0;
        private const int MaxAttempts = 3;

        public PasswordPromptForm(string appName, string message, ConfigData cfg)
        {
            config = cfg;
            InitializeComponent(appName, message);
        }

        private void InitializeComponent(string appName, string message)
        {
            this.Text = "App Locked - " + appName;
            this.Size = new Size(390, 230);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.TopMost = true;
            this.BackColor = Color.FromArgb(248, 249, 251);
            this.Font = new Font("Segoe UI", 9f);
            this.ShowInTaskbar = true;

            Panel headerPanel = new Panel();
            headerPanel.Dock = DockStyle.Top;
            headerPanel.Height = 55;
            headerPanel.BackColor = Color.FromArgb(238, 240, 244);

            Label lblTitle = new Label();
            lblTitle.Text = "🔒  " + appName;
            lblTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(30, 35, 45);
            lblTitle.Location = new Point(18, 14);
            lblTitle.AutoSize = true;
            headerPanel.Controls.Add(lblTitle);

            Label lblMsg = new Label();
            lblMsg.Text = message;
            lblMsg.Location = new Point(20, 70);
            lblMsg.Size = new Size(340, 35);
            lblMsg.ForeColor = Color.FromArgb(50, 55, 65);

            txtPassword = new TextBox();
            txtPassword.Location = new Point(22, 110);
            txtPassword.Width = 330;
            txtPassword.Font = new Font("Segoe UI", 11f);
            txtPassword.UseSystemPasswordChar = true;

            lblError = new Label();
            lblError.Location = new Point(22, 142);
            lblError.Size = new Size(330, 20);
            lblError.ForeColor = Color.Crimson;
            lblError.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            lblError.Visible = false;

            Button btnUnlock = new Button();
            btnUnlock.Text = "Unlock";
            btnUnlock.Location = new Point(180, 165);
            btnUnlock.Size = new Size(82, 28);
            btnUnlock.BackColor = Color.FromArgb(0, 102, 204);
            btnUnlock.ForeColor = Color.White;
            btnUnlock.FlatStyle = FlatStyle.Flat;
            btnUnlock.FlatAppearance.BorderSize = 0;
            btnUnlock.Click += OnUnlockClick;

            Button btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(270, 165);
            btnCancel.Size = new Size(82, 28);
            btnCancel.FlatStyle = FlatStyle.Standard;
            btnCancel.Click += (s, e) => {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };

            this.Controls.Add(headerPanel);
            this.Controls.Add(lblMsg);
            this.Controls.Add(txtPassword);
            this.Controls.Add(lblError);
            this.Controls.Add(btnUnlock);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnUnlock;
            this.CancelButton = btnCancel;

            this.ActiveControl = txtPassword;
        }

        private void OnUnlockClick(object sender, EventArgs e)
        {
            string pass = txtPassword.Text;
            if (SecurityHelper.VerifyPassword(pass, config.Salt, config.PasswordHash))
            {
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                failedAttempts++;
                int remaining = MaxAttempts - failedAttempts;
                if (remaining <= 0)
                {
                    MessageBox.Show("Maximum attempts exceeded. Access denied.", "App Locked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.Cancel;
                    this.Close();
                }
                else
                {
                    lblError.Text = string.Format("Incorrect password! ({0} attempt{1} remaining)", remaining, remaining == 1 ? "" : "s");
                    lblError.Visible = true;
                    txtPassword.SelectAll();
                    txtPassword.Focus();
                }
            }
        }
    }

    public class SettingsForm : Form
    {
        private ConfigData config;
        private bool isFirstRun;

        private TextBox txtPassword;
        private TextBox txtConfirmPassword;
        private ListBox lstApps;
        private ComboBox cmbDuration;
        private CheckBox chkTrayIcon;
        private CheckBox chkHideWindow;

        public SettingsForm(ConfigData cfg, bool firstRun)
        {
            config = cfg;
            isFirstRun = firstRun;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = isFirstRun ? "AppLocker - Initial Setup" : "AppLocker - Settings & Configuration";
            this.Size = new Size(540, 580);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Segoe UI", 9f);
            this.BackColor = Color.FromArgb(248, 249, 251);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 60;
            header.BackColor = Color.FromArgb(235, 238, 244);

            Label lblTitle = new Label();
            lblTitle.Text = isFirstRun ? "🔒 Welcome to AppLocker Setup" : "🔒 AppLocker Settings";
            lblTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(30, 35, 45);
            lblTitle.Location = new Point(18, 10);
            lblTitle.AutoSize = true;
            header.Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.Text = isFirstRun ? "Set your master password and select apps to protect." : "Manage locked applications, password, and unlock rules.";
            lblSub.Font = new Font("Segoe UI", 8.5f);
            lblSub.ForeColor = Color.FromArgb(70, 75, 85);
            lblSub.Location = new Point(20, 34);
            lblSub.AutoSize = true;
            header.Controls.Add(lblSub);

            this.Controls.Add(header);

            int y = 75;

            GroupBox grpPassword = new GroupBox();
            grpPassword.Text = isFirstRun ? "1. Master Password" : "Master Password (Leave blank to keep current)";
            grpPassword.Location = new Point(20, y);
            grpPassword.Size = new Size(485, 95);

            Label lblPass = new Label();
            lblPass.Text = isFirstRun ? "Password:" : "New Password:";
            lblPass.Location = new Point(15, 25);
            lblPass.AutoSize = true;
            grpPassword.Controls.Add(lblPass);

            txtPassword = new TextBox();
            txtPassword.Location = new Point(130, 22);
            txtPassword.Width = 330;
            txtPassword.UseSystemPasswordChar = true;
            grpPassword.Controls.Add(txtPassword);

            Label lblConfirm = new Label();
            lblConfirm.Text = "Confirm Password:";
            lblConfirm.Location = new Point(15, 58);
            lblConfirm.AutoSize = true;
            grpPassword.Controls.Add(lblConfirm);

            txtConfirmPassword = new TextBox();
            txtConfirmPassword.Location = new Point(130, 55);
            txtConfirmPassword.Width = 330;
            txtConfirmPassword.UseSystemPasswordChar = true;
            grpPassword.Controls.Add(txtConfirmPassword);

            this.Controls.Add(grpPassword);

            y += 105;

            GroupBox grpApps = new GroupBox();
            grpApps.Text = isFirstRun ? "2. Locked Applications" : "Locked Applications";
            grpApps.Location = new Point(20, y);
            grpApps.Size = new Size(485, 205);

            lstApps = new ListBox();
            lstApps.Location = new Point(15, 25);
            lstApps.Size = new Size(330, 160);
            lstApps.IntegralHeight = false;
            if (config.LockedApps != null)
            {
                for (int i = 0; i < config.LockedApps.Count; i++)
                {
                    lstApps.Items.Add(config.LockedApps[i]);
                }
            }
            grpApps.Controls.Add(lstApps);

            Button btnBrowse = new Button();
            btnBrowse.Text = "➕ Browse .exe...";
            btnBrowse.Location = new Point(355, 25);
            btnBrowse.Size = new Size(115, 30);
            btnBrowse.Click += OnBrowseAppClick;
            grpApps.Controls.Add(btnBrowse);

            Button btnPickRunning = new Button();
            btnPickRunning.Text = "📋 Pick Running...";
            btnPickRunning.Location = new Point(355, 62);
            btnPickRunning.Size = new Size(115, 30);
            btnPickRunning.Click += OnPickRunningClick;
            grpApps.Controls.Add(btnPickRunning);

            Button btnAddName = new Button();
            btnAddName.Text = "✏️ Add Name...";
            btnAddName.Location = new Point(355, 99);
            btnAddName.Size = new Size(115, 30);
            btnAddName.Click += OnAddNameClick;
            grpApps.Controls.Add(btnAddName);

            Button btnRemove = new Button();
            btnRemove.Text = "🗑️ Remove";
            btnRemove.Location = new Point(355, 155);
            btnRemove.Size = new Size(115, 30);
            btnRemove.Click += (s, e) => {
                if (lstApps.SelectedIndex >= 0)
                {
                    lstApps.Items.RemoveAt(lstApps.SelectedIndex);
                }
            };
            grpApps.Controls.Add(btnRemove);

            this.Controls.Add(grpApps);

            y += 215;

            GroupBox grpOptions = new GroupBox();
            grpOptions.Text = isFirstRun ? "3. Unlock Rules & Options" : "Unlock Rules & Options";
            grpOptions.Location = new Point(20, y);
            grpOptions.Size = new Size(485, 90);

            Label lblDuration = new Label();
            lblDuration.Text = "Unlock Behavior:";
            lblDuration.Location = new Point(15, 23);
            lblDuration.AutoSize = true;
            grpOptions.Controls.Add(lblDuration);

            cmbDuration = new ComboBox();
            cmbDuration.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDuration.Location = new Point(130, 20);
            cmbDuration.Width = 330;
            cmbDuration.Items.Add("Stay unlocked until app is closed (Default)");
            cmbDuration.Items.Add("Stay unlocked for 15 minutes");
            cmbDuration.Items.Add("Stay unlocked for 30 minutes");
            cmbDuration.Items.Add("Stay unlocked for 1 hour");
            cmbDuration.Items.Add("Stay unlocked until locker restart / PC reboot");

            if (config.UnlockDurationMinutes == 15) cmbDuration.SelectedIndex = 1;
            else if (config.UnlockDurationMinutes == 30) cmbDuration.SelectedIndex = 2;
            else if (config.UnlockDurationMinutes == 60) cmbDuration.SelectedIndex = 3;
            else if (config.UnlockDurationMinutes == -1) cmbDuration.SelectedIndex = 4;
            else cmbDuration.SelectedIndex = 0;

            grpOptions.Controls.Add(cmbDuration);

            chkTrayIcon = new CheckBox();
            chkTrayIcon.Text = "Show System Tray Icon";
            chkTrayIcon.Location = new Point(130, 55);
            chkTrayIcon.AutoSize = true;
            chkTrayIcon.Checked = config.ShowTrayIcon;
            grpOptions.Controls.Add(chkTrayIcon);

            chkHideWindow = new CheckBox();
            chkHideWindow.Text = "Minimize Window While Locked";
            chkHideWindow.Location = new Point(290, 55);
            chkHideWindow.AutoSize = true;
            chkHideWindow.Checked = config.HideWindowWhenLocked;
            grpOptions.Controls.Add(chkHideWindow);

            this.Controls.Add(grpOptions);

            y += 100;

            Button btnSave = new Button();
            btnSave.Text = isFirstRun ? "Start AppLocker" : "Save & Apply";
            btnSave.Location = new Point(275, y);
            btnSave.Size = new Size(130, 32);
            btnSave.BackColor = Color.FromArgb(0, 102, 204);
            btnSave.ForeColor = Color.White;
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            btnSave.Click += OnSaveClick;
            this.Controls.Add(btnSave);

            Button btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(415, y);
            btnCancel.Size = new Size(90, 32);
            btnCancel.Click += (s, e) => {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnSave;
            this.CancelButton = btnCancel;
        }

        private void OnBrowseAppClick(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*";
                ofd.Title = "Select Application to Lock";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string filename = Path.GetFileName(ofd.FileName);
                    AddAppToList(filename);
                }
            }
        }

        private void OnPickRunningClick(object sender, EventArgs e)
        {
            using (Form picker = new Form())
            {
                picker.Text = "Select Running Application";
                picker.Size = new Size(420, 380);
                picker.StartPosition = FormStartPosition.CenterParent;
                picker.FormBorderStyle = FormBorderStyle.FixedDialog;
                picker.MaximizeBox = false;
                picker.MinimizeBox = false;

                ListBox lb = new ListBox();
                lb.Location = new Point(15, 15);
                lb.Size = new Size(375, 270);
                lb.IntegralHeight = false;

                HashSet<string> ignoreList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                    "svchost", "csrss", "smss", "winlogon", "lsass", "services", "explorer", "AppLocker", "dwm", "taskmgr", "conhost"
                };

                Process[] procs = Process.GetProcesses();
                SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                int mySession = Process.GetCurrentProcess().SessionId;

                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        string pName = procs[i].ProcessName;
                        if (!ignoreList.Contains(pName))
                        {
                            if (!string.IsNullOrEmpty(procs[i].MainWindowTitle) || procs[i].SessionId == mySession)
                            {
                                names.Add(pName + ".exe");
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        procs[i].Dispose();
                    }
                }

                foreach (string n in names)
                {
                    lb.Items.Add(n);
                }

                Button btnAdd = new Button();
                btnAdd.Text = "Add Selected";
                btnAdd.Location = new Point(190, 295);
                btnAdd.Size = new Size(100, 30);
                btnAdd.Click += (s, ev) => {
                    if (lb.SelectedItem != null)
                    {
                        AddAppToList(lb.SelectedItem.ToString());
                        picker.DialogResult = DialogResult.OK;
                        picker.Close();
                    }
                };

                Button btnClose = new Button();
                btnClose.Text = "Close";
                btnClose.Location = new Point(298, 295);
                btnClose.Size = new Size(90, 30);
                btnClose.Click += (s, ev) => picker.Close();

                picker.Controls.Add(lb);
                picker.Controls.Add(btnAdd);
                picker.Controls.Add(btnClose);
                picker.ShowDialog(this);
            }
        }

        private void OnAddNameClick(object sender, EventArgs e)
        {
            using (Form prompt = new Form())
            {
                prompt.Text = "Add App by Executable Name";
                prompt.Size = new Size(350, 160);
                prompt.StartPosition = FormStartPosition.CenterParent;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.MaximizeBox = false;
                prompt.MinimizeBox = false;

                Label lbl = new Label() { Text = "Enter process or executable name (e.g. notepad.exe):", Location = new Point(15, 15), Size = new Size(310, 30) };
                TextBox tb = new TextBox() { Location = new Point(18, 50), Width = 300 };
                Button ok = new Button() { Text = "Add", Location = new Point(140, 85), Width = 80, DialogResult = DialogResult.OK };
                Button cancel = new Button() { Text = "Cancel", Location = new Point(230, 85), Width = 80, DialogResult = DialogResult.Cancel };

                prompt.Controls.Add(lbl);
                prompt.Controls.Add(tb);
                prompt.Controls.Add(ok);
                prompt.Controls.Add(cancel);
                prompt.AcceptButton = ok;
                prompt.CancelButton = cancel;

                if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(tb.Text))
                {
                    string name = tb.Text.Trim();
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        name += ".exe";
                    }
                    AddAppToList(name);
                }
            }
        }

        private void AddAppToList(string appName)
        {
            for (int i = 0; i < lstApps.Items.Count; i++)
            {
                if (string.Equals(lstApps.Items[i].ToString(), appName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            lstApps.Items.Add(appName);
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            string pass = txtPassword.Text;
            string confirm = txtConfirmPassword.Text;

            if (isFirstRun && string.IsNullOrEmpty(pass))
            {
                MessageBox.Show("Please enter a master password.", "Password Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPassword.Focus();
                return;
            }

            if (!string.IsNullOrEmpty(pass))
            {
                if (pass != confirm)
                {
                    MessageBox.Show("Passwords do not match. Please re-enter.", "Password Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtConfirmPassword.SelectAll();
                    txtConfirmPassword.Focus();
                    return;
                }

                config.PasswordHash = SecurityHelper.HashPassword(pass, config.Salt);
            }

            List<string> apps = new List<string>();
            for (int i = 0; i < lstApps.Items.Count; i++)
            {
                apps.Add(lstApps.Items[i].ToString());
            }
            config.LockedApps = apps;

            switch (cmbDuration.SelectedIndex)
            {
                case 1: config.UnlockDurationMinutes = 15; break;
                case 2: config.UnlockDurationMinutes = 30; break;
                case 3: config.UnlockDurationMinutes = 60; break;
                case 4: config.UnlockDurationMinutes = -1; break;
                default: config.UnlockDurationMinutes = 0; break;
            }

            config.ShowTrayIcon = chkTrayIcon.Checked;
            config.HideWindowWhenLocked = chkHideWindow.Checked;

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
            File.AppendAllText(logFile, DateTime.Now.ToString("o") + " Main started. PID: " + Process.GetCurrentProcess().Id + "\n");

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                try {
                    File.AppendAllText(logFile, "Unhandled: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null") + "\n");
                } catch {}
            };
            Application.ThreadException += (s, e) => {
                try {
                    File.AppendAllText(logFile, "ThreadEx: " + (e.Exception != null ? e.Exception.ToString() : "null") + "\n");
                } catch {}
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool restartRequested = false;
            bool stopRequested = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--restart" || a == "-r") restartRequested = true;
                if (a == "--stop" || a == "-s") stopRequested = true;
            }

            File.AppendAllText(logFile, "Checking existing processes...\n");
            Process current = Process.GetCurrentProcess();
            Process[] existingProcs = Process.GetProcessesByName(current.ProcessName);
            bool anotherProcessFound = false;
            for (int i = 0; i < existingProcs.Length; i++)
            {
                if (existingProcs[i].Id != current.Id)
                {
                    anotherProcessFound = true;
                    File.AppendAllText(logFile, "Found other instance PID: " + existingProcs[i].Id + "\n");
                }
                existingProcs[i].Dispose();
            }

            if (anotherProcessFound && !restartRequested)
            {
                File.AppendAllText(logFile, "Exiting because another instance is already running.\n");
                MessageBox.Show(
                    "AppLocker is ALREADY RUNNING actively in the background!\n\n" +
                    "A second instance cannot be started. Look for the golden padlock icon in your Windows System Tray (bottom-right near the clock; click the '^' arrow if hidden).\n\n" +
                    "To unlock apps, simply launch them!",
                    "AppLocker Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            File.AppendAllText(logFile, "Initializing service...\n");
            using (AppLockerService service = new AppLockerService())
            {
                if (service.Initialize())
                {
                    File.AppendAllText(logFile, "Service initialized. Entering Application.Run()...\n");
                    Application.Run();
                    File.AppendAllText(logFile, "Application.Run() exited.\n");
                }
                else
                {
                    File.AppendAllText(logFile, "Service Initialize returned false.\n");
                }
            }
            File.AppendAllText(logFile, "Main exiting.\n");
        }

        private static void KillRunningInstances()
        {
            Process current = Process.GetCurrentProcess();
            Process[] procs = Process.GetProcessesByName(current.ProcessName);
            for (int i = 0; i < procs.Length; i++)
            {
                try
                {
                    if (procs[i].Id != current.Id)
                    {
                        procs[i].Kill();
                    }
                }
                catch { }
                finally
                {
                    procs[i].Dispose();
                }
            }
            current.Dispose();
        }
    }
}
