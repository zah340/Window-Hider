using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace StreamHider
{
    public partial class MainWindow : Window
    {
        #region Windows API & COM Interfaces

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        // Hook API
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // Taskbar Interface
        [ComImportAttribute()]
        [GuidAttribute("56FDF342-FD6D-11d0-958A-006097C9A090")]
        [InterfaceTypeAttribute(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        [ComImportAttribute()]
        [GuidAttribute("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarList { }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        #endregion

        #region Constants

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const int MAX_SELECTIONS = 999;
        private const int MIN_REQUIRED_BUILD = 19041;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        #endregion

        #region Fields

        private ObservableCollection<ProcessItem> processItems = new ObservableCollection<ProcessItem>();
        private ObservableCollection<IntPtr> hiddenWindows = new ObservableCollection<IntPtr>();
        private bool isTransparencySupported = false;

        // Keybind variables
        private IntPtr _hookID = IntPtr.Zero;
        private LowLevelKeyboardProc _proc;
        private Key _triggerKey = Key.None;
        private bool _isBindingKey = false;
        private bool _isSelfHidden = false;

        // Taskbar object
        private ITaskbarList? _taskbarList;

        #endregion

        public MainWindow()
        {
            InitializeComponent();
            ProcessListView.ItemsSource = processItems;

            // Initialize Hook Delegate
            _proc = HookCallback;
            _hookID = SetHook(_proc);

            // Initialize Taskbar List
            try
            {
                _taskbarList = (ITaskbarList)new TaskbarList();
                _taskbarList.HrInit();
            }
            catch { /* Fail silently if COM fails */ }

            CheckWindowsVersion();
            RefreshProcessList();

            // Load saved keybind
            string savedKey = StreamHider.Properties.Settings.Default.SavedHotkey;
            if (!string.IsNullOrEmpty(savedKey) && savedKey != "None")
            {
                try
                {
                    // Try converting the text (e.g. "F12") back into a key.
                    Key key = (Key)Enum.Parse(typeof(Key), savedKey);
                    SetBindKey(key);
                }
                catch { }
            }

            // 1. Check First Run
            if (StreamHider.Properties.Settings.Default.IsFirstRun)
            {
                MessageBox.Show(
                    "Important Taskbar Information:\n\n" +
                    "Taskbar icons will only disappear if the application is NOT pinned to your Taskbar.\n" +
                    "If an app is pinned, the icon will remain visible even if the window is hidden.\n\n" +
                    "",
                    "Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // Set FirstRun to false so it doesn't show again
                StreamHider.Properties.Settings.Default.IsFirstRun = false;
                StreamHider.Properties.Settings.Default.Save();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            UnhookWindowsHookEx(_hookID);
            // Restore all taskbar buttons on exit just in case
            if (_taskbarList != null)
            {
                foreach (var hwnd in hiddenWindows)
                {
                    try { _taskbarList.AddTab(hwnd); } catch { }
                }
            }
        }

        #region Keyboard Hook Logic

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Key key = KeyInterop.KeyFromVirtualKey(vkCode);

                if (_isBindingKey)
                {
                    if (key != Key.Escape) SetBindKey(key);
                    else
                    {
                        _isBindingKey = false;
                        BindKeyButton.Content = $"Click to Bind Key: {_triggerKey}";
                        StatusText.Text = "Binding cancelled.";
                    }
                    return (IntPtr)1;
                }

                if (_triggerKey != Key.None && key == _triggerKey)
                {
                    ToggleSelfVisibility();
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void SetBindKey(Key key)
        {
            _triggerKey = key;
            _isBindingKey = false;

            StreamHider.Properties.Settings.Default.SavedHotkey = key.ToString();
            StreamHider.Properties.Settings.Default.Save();

            Application.Current.Dispatcher.Invoke(() =>
            {
                BindKeyButton.Content = $"Current Key: {key}";
                BindKeyButton.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5865F2"));
                StatusText.Text = $"Keybind set to: {key}. Press it to toggle visibility.";
            });
        }

        private void BindKeyButton_Click(object sender, RoutedEventArgs e)
        {
            _isBindingKey = true;
            BindKeyButton.Content = "Press any key...";
            BindKeyButton.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEE75C"));
            StatusText.Text = "Press any key to set as toggle...";
        }

        private void ToggleSelfVisibility()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IntPtr myHandle = new WindowInteropHelper(this).Handle;

                if (_isSelfHidden)
                {
                    SetWindowDisplayAffinity(myHandle, WDA_NONE);
                    if (_taskbarList != null) _taskbarList.AddTab(myHandle); // Restore to Taskbar
                    _isSelfHidden = false;
                    SelfHideStatusText.Text = "👁️ Visible";
                    SelfHideStatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ED4245"));
                }
                else
                {
                    SetWindowDisplayAffinity(myHandle, WDA_EXCLUDEFROMCAPTURE);
                    if (_taskbarList != null) _taskbarList.DeleteTab(myHandle); // Remove from Taskbar
                    _isSelfHidden = true;
                    SelfHideStatusText.Text = "👻 Hidden";
                    SelfHideStatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#57F287"));
                }
            });
        }

        #endregion

        #region Standard Logic

        private void CheckWindowsVersion()
        {
            try
            {
                using (RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var currentBuild = registryKey?.GetValue("CurrentBuild")?.ToString();
                    if (int.TryParse(currentBuild, out int buildNumber))
                    {
                        isTransparencySupported = buildNumber >= MIN_REQUIRED_BUILD;
                    }
                }
            }
            catch { }
        }

        private void RefreshProcessList()
        {
            processItems.Clear();
            var windowHandles = new System.Collections.Generic.List<WindowInfo>();

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    int length = GetWindowTextLength(hWnd);
                    if (length > 0)
                    {
                        StringBuilder sb = new StringBuilder(length + 1);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        string title = sb.ToString();

                        if (!string.IsNullOrWhiteSpace(title) && title.Length > 2 && !title.Equals("Program Manager"))
                        {
                            GetWindowThreadProcessId(hWnd, out uint processId);
                            try
                            {
                                var process = Process.GetProcessById((int)processId);
                                windowHandles.Add(new WindowInfo { Handle = hWnd, Title = title, ProcessName = process.ProcessName, ProcessId = processId });
                            }
                            catch { }
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            var grouped = windowHandles
                .Where(w => !w.ProcessName.Equals("StreamHider", StringComparison.OrdinalIgnoreCase))
                .GroupBy(w => w.ProcessId)
                .Select(g => new ProcessItem
                {
                    ProcessName = g.First().ProcessName,
                    WindowTitle = g.First().Title,
                    WindowHandle = g.First().Handle,
                    DisplayName = $"{g.First().ProcessName} - {TruncateString(g.First().Title, 60)}"
                })
                .OrderBy(p => p.ProcessName)
                .ToList();

            foreach (var item in grouped) processItems.Add(item);
            StatusText.Text = $"Found {processItems.Count} windows.";
        }

        private string TruncateString(string input, int maxLength)
        {
            if (string.IsNullOrEmpty(input) || input.Length <= maxLength) return input;
            return input.Substring(0, maxLength - 3) + "...";
        }

        #endregion

        #region Buttons & Injection Logic

        private void CheckBox_Checked(object sender, RoutedEventArgs e)
        {
          //
          /* 
            int selectedCount = processItems.Count(p => p.IsSelected);
            if (selectedCount > MAX_SELECTIONS)
            {
                ((System.Windows.Controls.CheckBox)sender).IsChecked = false;
                MessageBox.Show($"Max {MAX_SELECTIONS} selections allowed.", "Limit Reached", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
          */
        }

        private void CheckBox_Unchecked(object sender, RoutedEventArgs e) { }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = processItems.Where(p => p.IsSelected).ToList();
            if (selectedItems.Count == 0) return;

            int successCount = 0;
            int failCount = 0;

            foreach (var item in selectedItems)
            {
                GetWindowThreadProcessId(item.WindowHandle, out uint processId);

                // 1. Hide Window Content (Injection)
                bool result = Injector.HideWindowFromProcess(processId, item.WindowHandle);

                if (result)
                {
                    // 2. Hide from Taskbar (ITaskbarList)
                    if (_taskbarList != null)
                    {
                        try
                        {
                            _taskbarList.DeleteTab(item.WindowHandle);
                        }
                        catch { /* Some windows block this */ }
                    }

                    successCount++;
                    if (!hiddenWindows.Contains(item.WindowHandle)) hiddenWindows.Add(item.WindowHandle);
                }
                else failCount++;
            }

            StatusText.Text = $"Hidden: {successCount} | Failed: {failCount}";
            if (failCount > 0) MessageBox.Show("Some windows failed. Run as Admin.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void UnhideButton_Click(object sender, RoutedEventArgs e)
        {
            if (hiddenWindows.Count == 0)
            {
                MessageBox.Show("No windows are currently hidden.", "Info");
                return;
            }

            int successCount = 0;

            // Loop through all hidden windows
            foreach (var hwnd in hiddenWindows.ToList()) // ToList to create a copy for safe iteration
            {
                // 1. Restore Taskbar Button (if applicable)
                if (_taskbarList != null)
                {
                    try { _taskbarList.AddTab(hwnd); } catch { }
                }

                // 2. Restore Content Visibility (TRUE UNHIDE)
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (Injector.ShowWindowFromProcess(pid, hwnd))
                {
                    successCount++;
                }
            }

            // Clear the list and UI selection
            hiddenWindows.Clear();
            foreach (var item in processItems) item.IsSelected = false;

            StatusText.Text = $"Unhidden {successCount} windows.";
            MessageBox.Show(
                $"Restored visibility for {successCount} windows.\n\n" +
                $"• Taskbar buttons restored.\n" +
                $"• Window content visible in capture.",
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcessList();
        }

        #endregion

        #region Helper Classes
        private class WindowInfo
        {
            public IntPtr Handle { get; set; }
            public string Title { get; set; } = string.Empty;
            public string ProcessName { get; set; } = string.Empty;
            public uint ProcessId { get; set; }
        }
        #endregion
    }

    public class ProcessItem : INotifyPropertyChanged
    {
        private bool isSelected;
        public string ProcessName { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public IntPtr WindowHandle { get; set; }
        public string DisplayName { get; set; } = string.Empty;

        public bool IsSelected
        {
            get => isSelected;
            set { if (isSelected != value) { isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}