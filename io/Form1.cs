using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tesseract;

namespace io
{
    // === Структуры для WASD маршрута (таймстампы) ===
    class WasdEvent
    {
        public long TimeMs { get; set; }
        public int VK { get; set; }
        public bool IsDown { get; set; }
    }

    // === Структуры для Click маршрута (команды) ===
    class ClickCommand
    {
        public string Command { get; set; } // NUM5, SLEEP, CLICK
        public int X { get; set; }
        public int Y { get; set; }
        public int DelayMs { get; set; } // Только для SLEEP
    }

    public partial class Form1 : Form
    {
        // === ЭЛЕМЕНТЫ ИНТЕРФЕЙСА ===
        private Button btnSelectClickRoute;
        private TextBox txtClickRoutePath;
        private Button btnSelectWasdRoute;
        private TextBox txtWasdRoutePath;

        private Button btnStart;
        private Button btnStop;
        private RichTextBox logBox;
        private Label lblStatus;

        // === ПЕРЕМЕННЫЕ ЛОГИКИ ===
        private CancellationTokenSource _cancellationTokenSource;
        private IntPtr gameWindow = IntPtr.Zero;
        private const string WINDOW_NAME = "World";

        // === WinAPI ===
        [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;

        const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;
        const int VK_SPACE = 0x20;
        const int VK_X = 0x58;
        const int VK_NUM5 = 0x65, VK_NUM2 = 0x62, VK_NUM7 = 0x67, VK_NUM8 = 0x68;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        static readonly Rectangle SpeedZone = new Rectangle(28, 591, 227, 76);

        private HashSet<int> currentlyHeldKeys = new HashSet<int>();
        private bool waitingForFullSpeed = false;

        public Form1()
        {
            InitializeCustomUI();
            this.Text = "Smart Raid Bot Player (Sequential)";
            this.Size = new Size(500, 480);
        }

        // === ГЛАВНАЯ ЗАМЕНА ДЛЯ .NET 4.8 ===
        private long GetTimestampMs()
        {
            return DateTime.Now.Ticks / 10000;
        }

        private void InitializeCustomUI()
        {
            // Поле для Click-маршрута
            Label l1 = new Label() { Text = "1. Click/Command Route:", Location = new Point(10, 15), AutoSize = true };
            txtClickRoutePath = new TextBox() { Location = new Point(10, 35), Width = 350, ReadOnly = true };
            btnSelectClickRoute = new Button() { Text = "Обзор Click...", Location = new Point(370, 33), Width = 100 };

            // Поле для WASD-маршрута
            Label l2 = new Label() { Text = "2. WASD/Time-based Route:", Location = new Point(10, 75), AutoSize = true };
            txtWasdRoutePath = new TextBox() { Location = new Point(10, 95), Width = 350, ReadOnly = true };
            btnSelectWasdRoute = new Button() { Text = "Обзор WASD...", Location = new Point(370, 93), Width = 100 };

            int startY = 135;

            btnStart = new Button() { Text = "СТАРТ ВСЕГО", Location = new Point(10, startY), Width = 150, Height = 40, BackColor = Color.LightGreen };
            btnStop = new Button() { Text = "СТОП", Location = new Point(170, startY), Width = 100, Height = 40, BackColor = Color.LightPink, Enabled = false };

            lblStatus = new Label() { Text = "Ожидание...", Location = new Point(280, startY + 10), AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

            logBox = new RichTextBox() { Location = new Point(10, startY + 50), Width = 460, Height = 250, ReadOnly = true, BackColor = Color.Black, ForeColor = Color.Lime };

            this.Controls.Add(l1);
            this.Controls.Add(txtClickRoutePath);
            this.Controls.Add(btnSelectClickRoute);
            this.Controls.Add(l2);
            this.Controls.Add(txtWasdRoutePath);
            this.Controls.Add(btnSelectWasdRoute);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);
            this.Controls.Add(lblStatus);
            this.Controls.Add(logBox);

            btnSelectClickRoute.Click += (s, e) => BtnSelectRoute_Click(txtClickRoutePath, "Click/Command Route");
            btnSelectWasdRoute.Click += (s, e) => BtnSelectRoute_Click(txtWasdRoutePath, "WASD Route");
            btnStart.Click += BtnStart_Click;
            btnStop.Click += BtnStop_Click;
        }

        private void BtnSelectRoute_Click(TextBox pathBox, string filterName)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                ofd.Title = $"Выберите файл: {filterName}";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    pathBox.Text = ofd.FileName;
                }
            }
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (!File.Exists(txtClickRoutePath.Text))
            {
                Log("ОШИБКА: Файл Click-маршрута не выбран или не существует!");
                return;
            }
            if (!File.Exists(txtWasdRoutePath.Text))
            {
                Log("ОШИБКА: Файл WASD-маршрута не выбран или не существует!");
                return;
            }

            Log("Поиск окна игры...");
            gameWindow = FindWindow(WINDOW_NAME);
            if (gameWindow == IntPtr.Zero)
            {
                Log("ОШИБКА: Окно 'World' не найдено!");
                MessageBox.Show("Запустите игру!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Log($"Окно найдено! ID: {gameWindow}");

            btnStart.Enabled = false;
            btnSelectClickRoute.Enabled = false;
            btnSelectWasdRoute.Enabled = false;
            btnStop.Enabled = true;
            lblStatus.Text = "В РАБОТЕ (Click)";
            lblStatus.ForeColor = Color.Orange;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            try
            {
                // 1. ЗАПУСК CLICK-МАРШРУТА
                await Task.Run(() => RunClickRoute(txtClickRoutePath.Text, token), token);

                if (token.IsCancellationRequested) return;

                // 2. ЗАПУСК WASD-МАРШРУТА
                Log("Click-маршрут завершен. Переход к WASD...");
                lblStatus.Text = "В РАБОТЕ (WASD)";
                lblStatus.ForeColor = Color.Green;

                await Task.Run(() => RunWasdRoute(txtWasdRoutePath.Text, token), token);
            }
            catch (OperationCanceledException)
            {
                Log("Маршрут остановлен пользователем.");
            }
            catch (Exception ex)
            {
                Log($"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}");
            }
            finally
            {
                ResetUI();
            }
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            _cancellationTokenSource?.Cancel();
        }

        private void ResetUI()
        {
            if (InvokeRequired) { Invoke(new Action(ResetUI)); return; }
            btnStart.Enabled = true;
            btnSelectClickRoute.Enabled = true;
            btnSelectWasdRoute.Enabled = true;
            btnStop.Enabled = false;
            lblStatus.Text = "ОСТАНОВЛЕН";
            lblStatus.ForeColor = Color.Red;
            foreach (var key in currentlyHeldKeys) PostMessage(gameWindow, WM_KEYUP, (IntPtr)key, IntPtr.Zero);
            currentlyHeldKeys.Clear();
        }

        private void Log(string msg)
        {
            if (logBox.InvokeRequired)
            {
                logBox.BeginInvoke(new Action(() => Log(msg)));
            }
            else
            {
                logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                logBox.ScrollToCaret();
            }
        }

        // === 1. CLICK ROUTE (Команды и клики без таймстампов) ===
        private void RunClickRoute(string filePath, CancellationToken token)
        {
            var commands = ParseClickRoute(filePath);
            if (commands.Count == 0) { Log("Click-файл пуст."); return; }

            Log($"Click-маршрут: {commands.Count} команд. Старт...");

            // Включаем монитор скорости для ожидания остановки
            Task speedMonitor = Task.Run(() => MonitorSpeed_Smart(token), token);

            foreach (var cmd in commands)
            {
                if (token.IsCancellationRequested) return;

                switch (cmd.Command)
                {
                    case "CLICK":
                        Log($"Клик: {cmd.X}, {cmd.Y}");
                        RightClickAt(cmd.X, cmd.Y);
                        // ГЛАВНОЕ ИСПРАВЛЕНИЕ: Ждем пока персонаж не остановится после клика
                        WaitUntilStopped(token);
                        break;
                    case "SLEEP":
                        Log($"Задержка: {cmd.DelayMs} мс");
                        Thread.Sleep(cmd.DelayMs);
                        break;
                    case "NUM5":
                    case "NUM2":
                    case "NUM7":
                    case "NUM8":
                        Log($"Нажатие: {cmd.Command}");
                        PressKey(GetKeyVkCode(cmd.Command));
                        // Короткая пауза после нажатия, чтобы избежать double-tap
                        Thread.Sleep(200);
                        break;
                }
            }
            // Отключаем монитор скорости, чтобы он не мешал WASD маршруту (он запустит свой)
            waitingForFullSpeed = false;
        }

        // НОВАЯ/ВОССТАНОВЛЕННАЯ ЛОГИКА ОЖИДАНИЯ
        private void WaitUntilStopped(CancellationToken token)
        {
            Log("Ожидание остановки...");
            long movementStart = GetTimestampMs();
            bool moved = false;

            // Максимальное время ожидания 15 секунд
            while (GetTimestampMs() - movementStart < 15000)
            {
                if (token.IsCancellationRequested) return;

                float speed = GetCurrentSpeed();

                // Если скорость была ненулевой (начали движение), но стала нулевой (остановились)
                if (speed > 0.1f) moved = true;

                if (moved && speed <= 0.1f)
                {
                    Log("Персонаж остановился.");
                    // Короткая пауза после остановки, чтобы убедиться
                    Thread.Sleep(50);
                    return;
                }

                // Обработка застревания/боя
                if (speed > 0.1f && speed < 150f)
                {
                    Log("Бой/Замедление -> Жму X");
                    PressX();
                    Thread.Sleep(600);
                    // Сбрасываем таймер движения после X, чтобы дать время на разгон
                    movementStart = GetTimestampMs();
                }
                Thread.Sleep(100);
            }
            Log("Таймаут ожидания остановки (15 сек). Переход к следующему шагу.");
        }


        private List<ClickCommand> ParseClickRoute(string filePath)
        {
            var commands = new List<ClickCommand>();
            foreach (var line in File.ReadAllLines(filePath))
            {
                var p = line.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;

                string cmd = p[0].ToUpper();
                var clickCmd = new ClickCommand { Command = cmd };

                if (cmd == "CLICK" && p.Length >= 3 && int.TryParse(p[1], out int x) && int.TryParse(p[2], out int y))
                {
                    clickCmd.X = x;
                    clickCmd.Y = y;
                }
                else if (cmd == "SLEEP" && p.Length >= 2 && int.TryParse(p[1], out int delay))
                {
                    clickCmd.DelayMs = delay;
                }
                else if (cmd == "NUM5" || cmd == "NUM2" || cmd == "NUM7" || cmd == "NUM8")
                {
                    // ОК, команда без параметров
                }
                else
                {
                    Log($"Пропущена неизвестная или некорректная команда в Click-маршруте: {line}");
                    continue;
                }
                commands.Add(clickCmd);
            }
            return commands;
        }

        // === 2. WASD ROUTE (Таймстампы и VK-коды) ===
        // Логика остается без изменений, так как она привязана к точному времени.
        private void RunWasdRoute(string filePath, CancellationToken token)
        {
            var events = ParseWasdRoute(filePath);
            if (events.Count == 0) { Log("WASD-файл пуст."); return; }

            Log($"WASD-маршрут: {events.Count} действий. Старт...");

            currentlyHeldKeys.Clear();
            waitingForFullSpeed = false;

            // Запуск монитора скорости в отдельном потоке
            Task speedMonitor = Task.Run(() => MonitorSpeed_Smart(token), token);

            long baseTime = GetTimestampMs();

            for (int i = 0; i < events.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var ev = events[i];
                while (true)
                {
                    if (token.IsCancellationRequested) return;

                    long now = GetTimestampMs();
                    long elapsedReal = now - baseTime;
                    long needWait = ev.TimeMs - elapsedReal;

                    if (needWait <= 0) break;
                    int sleepMs = (int)Math.Min(needWait, 10);
                    Thread.Sleep(sleepMs);
                }

                // Выполнение действия
                if (ev.IsDown)
                {
                    KeyDown(ev.VK);
                    if (!currentlyHeldKeys.Contains(ev.VK)) currentlyHeldKeys.Add(ev.VK);
                    if (ev.VK == VK_W) waitingForFullSpeed = true;
                }
                else
                {
                    KeyUp(ev.VK);
                    if (currentlyHeldKeys.Contains(ev.VK)) currentlyHeldKeys.Remove(ev.VK);
                    if (ev.VK == VK_W) waitingForFullSpeed = false;
                }
                Log($"WASD: {(ev.IsDown ? "DOWN" : "UP")} 0x{ev.VK:X} ({ev.TimeMs}ms)");
            }

            // Финальная очистка
            foreach (int vk in currentlyHeldKeys.ToArray()) KeyUp(vk);
            Log("WASD-маршрут завершен.");
        }

        private List<WasdEvent> ParseWasdRoute(string filePath)
        {
            var events = new List<WasdEvent>();
            foreach (var line in File.ReadAllLines(filePath))
            {
                // Формат: [TimeMs] [IsDown/KeyUp] [VK_Code]
                var p = line.Trim().Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 3) continue;

                if (long.TryParse(p[0], out long t) &&
                    int.TryParse(p[1], out int down) && // 1=DOWN, 0=UP
                    int.TryParse(p[2], NumberStyles.HexNumber, null, out int vk)) // VK code в Hex
                {
                    events.Add(new WasdEvent { TimeMs = t, VK = vk, IsDown = down == 1 });
                }
            }
            return events;
        }

        // === Вспомогательные функции (Монитор скорости, OCR, Ввод) ===
        private void MonitorSpeed_Smart(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Проверяем, активна ли логика ожидания W (актуально только для WASD)
                    if (waitingForFullSpeed)
                    {
                        float speed = GetCurrentSpeed();
                        if (currentlyHeldKeys.Contains(VK_W) && speed > 0.1f && speed < 153f)
                        {
                            Log($"Замедление ({speed:F1}%) -> Жму X");
                            PressX();
                            Thread.Sleep(650);
                        }
                    }
                    Thread.Sleep(120);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Ошибка монитора скорости: {ex.Message}"); }
        }

        private int GetKeyVkCode(string keyName)
        {
            switch (keyName.ToUpper())
            {
                case "W": return VK_W;
                case "A": return VK_A;
                case "S": return VK_S;
                case "D": return VK_D;
                case "SPACE": return VK_SPACE;
                case "X": return VK_X;
                case "NUM5": return VK_NUM5;
                case "NUM2": return VK_NUM2;
                case "NUM7": return VK_NUM7;
                case "NUM8": return VK_NUM8;
                default: return 0;
            }
        }

        private float GetCurrentSpeed()
        {
            // Логика OCR остается без изменений
            try
            {
                using (Bitmap bmp = new Bitmap(SpeedZone.Width, SpeedZone.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(SpeedZone.Left, SpeedZone.Top, 0, 0, SpeedZone.Size);
                    }

                    using (Bitmap proc = new Bitmap(bmp.Width, bmp.Height))
                    {
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            for (int y = 0; y < bmp.Height; y++)
                            {
                                Color c = bmp.GetPixel(x, y);
                                proc.SetPixel(x, y, (c.R > 180 && c.B > 180 && c.G < 100) ? Color.Black : Color.White);
                            }
                        }

                        using (var engine = new TesseractEngine("./tessdata", "eng", EngineMode.Default))
                        {
                            engine.SetVariable("tessedit_char_whitelist", "0123456789.%");
                            engine.DefaultPageSegMode = PageSegMode.SingleLine;
                            using (var page = engine.Process(proc))
                            {
                                string text = page.GetText().Trim().Replace(" ", "").Replace("\n", "");
                                if (float.TryParse(text.Replace("%", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out float val))
                                    return val;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return 0f;
        }

        private void RightClickAt(int x, int y)
        {
            int absX = x * 65535 / Screen.PrimaryScreen.Bounds.Width;
            int absY = y * 65535 / Screen.PrimaryScreen.Bounds.Height;

            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE, (uint)absX, (uint)absY, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_RIGHTDOWN, (uint)absX, (uint)absY, 0, IntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_RIGHTUP, (uint)absX, (uint)absY, 0, IntPtr.Zero);
        }

        private void KeyDown(int vk) => PostMessage(gameWindow, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        private void KeyUp(int vk) => PostMessage(gameWindow, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        private void PressKey(int vk) { KeyDown(vk); Thread.Sleep(80); KeyUp(vk); }
        private void PressX() => PressKey(VK_X);

        private IntPtr FindWindow(string contains)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}