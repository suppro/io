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
        private Button btnStart;
        private Button btnStop;
        private RichTextBox logBox;
        private Label lblStatus;
        private TextBox txtIterations;
        private Label lblIterations;

        // === ПЕРЕМЕННЫЕ ЛОГИКИ ===
        private CancellationTokenSource _cancellationTokenSource;
        private IntPtr gameWindow = IntPtr.Zero;
        private const string WINDOW_NAME = "World";
        
        // === ПУТИ К ФАЙЛАМ РОУТОВ ===
        private readonly string clickRoutePath;
        private readonly string wasdRoutePath;
        private readonly string enterRoutePath;

        // === WinAPI ===
        [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;

        const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;
        const int VK_SPACE = 0x20;
        const int VK_X = 0x58;
        const int VK_R = 0x52;
        const int VK_F3 = 0x72;
        const int VK_3 = 0x33;
        const int VK_NUM5 = 0x65, VK_NUM2 = 0x62, VK_NUM7 = 0x67, VK_NUM8 = 0x68;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        static readonly Rectangle SpeedZone = new Rectangle(28, 591, 227, 76);

        private HashSet<int> currentlyHeldKeys = new HashSet<int>();
        private bool waitingForFullSpeed = false;

        public Form1()
        {
            // Определяем пути к файлам роутов в папке Debug
            string debugPath = Path.GetDirectoryName(Application.ExecutablePath);
            clickRoutePath = Path.Combine(debugPath, "route.txt");
            wasdRoutePath = Path.Combine(debugPath, "wasd_route.txt");
            enterRoutePath = Path.Combine(debugPath, "enter_route.txt");
            
            InitializeCustomUI();
            this.Text = "IO";
            this.Size = new Size(500, 420);
        }

        // === ФУНКЦИЯ СРАВНЕНИЯ ИЗОБРАЖЕНИЙ ===
        private bool CompareImageWithScreen(int x, int y, int width, int height, string imagePath)
        {
            try
            {
                if (!File.Exists(imagePath))
                {
                    Log($"ОШИБКА: Файл изображения не найден: {imagePath}");
                    return false;
                }

                // Загружаем эталонное изображение
                using (Bitmap template = new Bitmap(imagePath))
                {
                    // Делаем скриншот указанной области
                    using (Bitmap screen = new Bitmap(width, height))
                    {
                        using (Graphics g = Graphics.FromImage(screen))
                        {
                            g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
                        }

                        // Сохраняем скриншот для отладки
                        string debugPath = Path.GetDirectoryName(imagePath);
                        string debugScreenPath = Path.Combine(debugPath, "screen_debug.png");
                        try
                        {
                            screen.Save(debugScreenPath, System.Drawing.Imaging.ImageFormat.Png);
                            Log($"Скриншот для отладки сохранен: {debugScreenPath}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Не удалось сохранить скриншот для отладки: {ex.Message}");
                        }

                        // Проверяем размеры
                        if (screen.Width != template.Width || screen.Height != template.Height)
                        {
                            Log($"Размеры не совпадают: экран {screen.Width}x{screen.Height}, шаблон {template.Width}x{template.Height}");
                            return false;
                        }

                        // Сравниваем пиксели
                        int differentPixels = 0;
                        int totalPixels = width * height;
                        int tolerance = 5; // Допустимое отклонение цвета

                        for (int i = 0; i < width; i++)
                        {
                            for (int j = 0; j < height; j++)
                            {
                                Color screenPixel = screen.GetPixel(i, j);
                                Color templatePixel = template.GetPixel(i, j);

                                // Проверяем разницу в RGB
                                int diffR = Math.Abs(screenPixel.R - templatePixel.R);
                                int diffG = Math.Abs(screenPixel.G - templatePixel.G);
                                int diffB = Math.Abs(screenPixel.B - templatePixel.B);

                                if (diffR > tolerance || diffG > tolerance || diffB > tolerance)
                                {
                                    differentPixels++;
                                }
                            }
                        }

                        // Считаем совпадение, если различается менее 5% пикселей
                        double matchPercentage = 1.0 - ((double)differentPixels / totalPixels);
                        bool matches = matchPercentage >= 0.70;

                        Log($"Сравнение изображений: совпадение {matchPercentage:P2} (различается {differentPixels} из {totalPixels} пикселей)");
                        return matches;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка при сравнении изображений: {ex.Message}");
                return false;
            }
        }

        // === ГЛАВНАЯ ЗАМЕНА ДЛЯ .NET 4.8 ===
        private long GetTimestampMs()
        {
            return DateTime.Now.Ticks / 10000;
        }

        private void InitializeCustomUI()
        {
            int startY = 15;

            // Поле для ввода числа итераций
            lblIterations = new Label() { Text = "Число итераций:", Location = new Point(10, startY), AutoSize = true };
            txtIterations = new TextBox() { Location = new Point(120, startY - 2), Width = 80, Text = "1" };

            int buttonY = startY + 30;
            btnStart = new Button() { Text = "СТАРТ ВСЕГО", Location = new Point(10, buttonY), Width = 150, Height = 40, BackColor = Color.LightGreen };
            btnStop = new Button() { Text = "СТОП", Location = new Point(170, buttonY), Width = 100, Height = 40, BackColor = Color.LightPink, Enabled = false };

            lblStatus = new Label() { Text = "Ожидание...", Location = new Point(280, buttonY + 10), AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

            logBox = new RichTextBox() { Location = new Point(10, buttonY + 50), Width = 460, Height = 280, ReadOnly = true, BackColor = Color.Black, ForeColor = Color.Lime };

            this.Controls.Add(lblIterations);
            this.Controls.Add(txtIterations);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);
            this.Controls.Add(lblStatus);
            this.Controls.Add(logBox);

            btnStart.Click += BtnStart_Click;
            btnStop.Click += BtnStop_Click;
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (!File.Exists(clickRoutePath))
            {
                Log($"ОШИБКА: Файл Click-маршрута не найден: {clickRoutePath}");
                MessageBox.Show($"Файл route.txt не найден в папке:\n{Path.GetDirectoryName(clickRoutePath)}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!File.Exists(wasdRoutePath))
            {
                Log($"ОШИБКА: Файл WASD-маршрута не найден: {wasdRoutePath}");
                MessageBox.Show($"Файл wasd_route.txt не найден в папке:\n{Path.GetDirectoryName(wasdRoutePath)}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!File.Exists(enterRoutePath))
            {
                Log($"ОШИБКА: Файл маршрута захода в рейд не найден: {enterRoutePath}");
                MessageBox.Show($"Файл enter_route.txt не найден в папке:\n{Path.GetDirectoryName(enterRoutePath)}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            // Проверяем число итераций
            if (!int.TryParse(txtIterations.Text, out int iterations) || iterations < 1)
            {
                Log("ОШИБКА: Некорректное число итераций! Должно быть положительное число.");
                MessageBox.Show("Введите корректное число итераций (положительное число).", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            Log($"Загружены роуты:");
            Log($"  Click: {clickRoutePath}");
            Log($"  WASD: {wasdRoutePath}");
            Log($"  Enter: {enterRoutePath}");
            Log($"Число итераций: {iterations}");

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
            txtIterations.Enabled = false;
            btnStop.Enabled = true;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            try
            {
                // ЦИКЛ ПО ИТЕРАЦИЯМ
                for (int iteration = 1; iteration <= iterations; iteration++)
                {
                    if (token.IsCancellationRequested) break;
                    
                    Log($"========== ИТЕРАЦИЯ {iteration}/{iterations} ==========");
                    
                    // 1. ЗАПУСК CLICK-МАРШРУТА
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Click)";
                    lblStatus.ForeColor = Color.Orange;
                    await Task.Run(() => RunClickRoute(clickRoutePath, token), token);

                if (token.IsCancellationRequested) return;

                    // 2. ЗАПУСК WASD-МАРШРУТА
                    Log("Click-маршрут завершен. Переход к WASD...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - WASD)";
                    lblStatus.ForeColor = Color.Green;

                try
                {
                    await Task.Run(() => RunWasdRoute(wasdRoutePath, token));
                    Log("Task.Run для WASD-маршрута завершен успешно.");
                }
                catch (OperationCanceledException)
                {
                    Log("WASD-маршрут отменен.");
                    return;
                }
                catch (Exception ex)
                {
                    Log($"Ошибка в WASD-маршруте: {ex.Message}");
                    Log($"StackTrace: {ex.StackTrace}");
                    if (token.IsCancellationRequested) return;
                }

                if (token.IsCancellationRequested)
                {
                    Log("Отмена после WASD-маршрута.");
                    return;
                }

                    // 3. ФАЗА БОЯ
                    Log("=== ПЕРЕХОД К ФАЗЕ БОЯ ===");
                    Log("WASD-маршрут завершен. Переход к фазе боя...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Бой)";
                    lblStatus.ForeColor = Color.Red;
                
                try
                {
                    await Task.Run(() => RunBattlePhase(token), token);
                }
                catch (Exception ex)
                {
                    Log($"Ошибка в фазе боя: {ex.Message}");
                    if (token.IsCancellationRequested) return;
                }

                if (token.IsCancellationRequested)
                {
                    Log("Отмена после фазы боя.");
                    return;
                }

                    // 4. ФАЗА СБОРА
                    Log("Фаза боя завершена. Переход к фазе сбора...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Сбор)";
                    lblStatus.ForeColor = Color.Blue;
                
                try
                {
                    await Task.Run(() => RunCollectionPhase(token), token);
                }
                catch (Exception ex)
                {
                    Log($"Ошибка в фазе сбора: {ex.Message}");
                    if (token.IsCancellationRequested) return;
                }

                if (token.IsCancellationRequested)
                {
                    Log("Отмена после фазы сбора.");
                    return;
                }

                    // 5. ФАЗА ОЖИДАНИЯ ВЫХОДА ИЗ РЕЙДА
                    Log("Фаза сбора завершена. Переход к фазе ожидания выхода из рейда...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Ожидание)";
                    lblStatus.ForeColor = Color.Orange;
                
                try
                {
                    await Task.Run(() => RunWaitForRaidExitPhase(token), token);
                }
                catch (Exception ex)
                {
                    Log($"Ошибка в фазе ожидания: {ex.Message}");
                    if (token.IsCancellationRequested) return;
                }

                if (token.IsCancellationRequested)
                {
                    Log("Отмена после фазы ожидания.");
                    return;
                }

                    // 6. ФАЗА СОЗДАНИЯ ГРУППЫ
                    Log("Ожидание завершено. Переход к фазе создания группы...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Создание группы)";
                    lblStatus.ForeColor = Color.Purple;
                
                try
                {
                    await Task.Run(() => RunCreateGroupPhase(token), token);
                }
                catch (Exception ex)
                {
                    Log($"Ошибка в фазе создания группы: {ex.Message}");
                }
                
                    if (token.IsCancellationRequested) break;
                    
                    // 7. ФАЗА ЗАХОДА В РЕЙД
                    Log("Создание группы завершено. Переход к фазе захода в рейд...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Заход в рейд)";
                    lblStatus.ForeColor = Color.DarkGreen;
                    
                    try
                    {
                        await Task.Run(() => RunWasdRoute(enterRoutePath, token), token);
                        Log("Фаза захода в рейд завершена.");
                    }
                    catch (OperationCanceledException)
                    {
                        Log("Фаза захода в рейд отменена.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка в фазе захода в рейд: {ex.Message}");
                        if (token.IsCancellationRequested) break;
                    }
                    
                    if (token.IsCancellationRequested) break;
                    
                    Log($"Итерация {iteration}/{iterations} завершена.");
                    if (iteration < iterations)
                    {
                        Log("Переход к следующей итерации...");
                        PressX();
                        Thread.Sleep(10000); // Небольшая пауза между итерациями
                    }
                }
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
            txtIterations.Enabled = true;
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
            Log("Очистка нажатых клавиш после WASD-маршрута...");
            foreach (int vk in currentlyHeldKeys.ToArray()) KeyUp(vk);
            currentlyHeldKeys.Clear();
            waitingForFullSpeed = false;
            Log("WASD-маршрут завершен. Функция RunWasdRoute завершается.");
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

        private void LeftClickAt(int x, int y)
        {
            int absX = x * 65535 / Screen.PrimaryScreen.Bounds.Width;
            int absY = y * 65535 / Screen.PrimaryScreen.Bounds.Height;

            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE, (uint)absX, (uint)absY, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN, (uint)absX, (uint)absY, 0, IntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP, (uint)absX, (uint)absY, 0, IntPtr.Zero);
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

        // === ФАЗА БОЯ ===
        private void RunBattlePhase(CancellationToken token)
        {
            Log("Начало фазы боя...");
            
            // Нажимаем F3
            Log("Нажатие F3");
            PressKey(VK_F3);
            if (token.IsCancellationRequested) return;
            
            // Нажимаем R
            Log("Нажатие R");
            PressKey(VK_R);
            if (token.IsCancellationRequested) return;
            
            // Ждем 0.5 секунды
            Thread.Sleep(500);
            if (token.IsCancellationRequested) return;
            
            // Поочередно нажимаем S и 3 с задержкой 0.5 сек в течение 10 секунд
            Log("Начало цикла боя (S и 3) на 10 секунд...");
            long startTime = GetTimestampMs();
            long endTime = startTime + 15000; // 10 секунд = 10000 мс
            bool pressS = true; // Начинаем с S
            
            while (GetTimestampMs() < endTime)
            {
                if (token.IsCancellationRequested) return;
                
                if (pressS)
                {
                    Log("Нажатие S");
                    PressKey(VK_S);
                }
                else
                {
                    Log("Нажатие 3");
                    PressKey(VK_3);
                }
                
                pressS = !pressS; // Переключаем между S и 3
                
                // Задержка 0.5 секунды
                Thread.Sleep(100);
            }
            
            Log("Фаза боя завершена.");
        }

        // === ФАЗА ОЖИДАНИЯ ВЫХОДА ИЗ РЕЙДА ===
        private void RunWaitForRaidExitPhase(CancellationToken token)
        {
            Log("Начало фазы ожидания выхода из рейда (60 секунд)...");
            
            // Ждем 1 минуту (60000 мс) с проверкой отмены каждые 500 мс
            int totalWaitMs = 60000;
            int checkIntervalMs = 500;
            int elapsedMs = 0;
            
            while (elapsedMs < totalWaitMs)
            {
                if (token.IsCancellationRequested) return;
                
                Thread.Sleep(checkIntervalMs);
                elapsedMs += checkIntervalMs;
                
                // Показываем оставшееся время каждые 5 секунд
                if (elapsedMs % 5000 == 0)
                {
                    int remainingSeconds = (totalWaitMs - elapsedMs) / 1000;
                    Log($"Ожидание выхода из рейда... Осталось: {remainingSeconds} сек");
                }
            }
            
            Log("Фаза ожидания выхода из рейда завершена.");
        }

        // === ФАЗА СОЗДАНИЯ ГРУППЫ ===
        private void RunCreateGroupPhase(CancellationToken token)
        {
            Log("Начало фазы создания группы...");
            
            // Путь к изображению для проверки
            string debugPath = Path.GetDirectoryName(Application.ExecutablePath);
            string imagePath = Path.Combine(debugPath, "img.png");
            
            // Координаты для кликов
            int[][] coordinates = new int[][]
            {
                new int[] { 1775, 1058 },
                new int[] { 323, 299 },
                new int[] { 299, 593 },
                new int[] { 437, 228 },
                new int[] { 432, 559 },
                new int[] { 565, 594 }
            };
            
            // 1. Кликаем на первую координату (левой кнопкой мыши)
            if (token.IsCancellationRequested) return;
            Log($"Клик создания группы 1/?: ({coordinates[0][0]}, {coordinates[0][1]})");
            LeftClickAt(coordinates[0][0], coordinates[0][1]);
            
            // Ждем немного, чтобы интерфейс обновился
            Thread.Sleep(500);
            if (token.IsCancellationRequested) return;
            
            // 2. Проверяем изображение в прямоугольнике
            // (279, 382) - левый нижний угол, размер 130x173
            // Левый верхний угол: X=279, Y=382-173=209
            int rectX = 279;
            int rectY = 382 - 173; // 209 - левый верхний угол
            int rectWidth = 130;
            int rectHeight = 173;
            Log($"Проверка изображения после первого клика... Прямоугольник: ({rectX}, {rectY}) размер {rectWidth}x{rectHeight}");
            bool imageMatches = CompareImageWithScreen(rectX, rectY, rectWidth, rectHeight, imagePath);
            
            if (imageMatches)
            {
                Log("Изображение совпадает. Продолжаем выполнение остальных кликов...");
                
                // Выполняем остальные клики (начиная со второго)
                for (int i = 1; i < coordinates.Length; i++)
                {
                    if (token.IsCancellationRequested) return;
                    
                    int x = coordinates[i][0];
                    int y = coordinates[i][1];
                    Log($"Клик создания группы {i + 1}/{coordinates.Length}: ({x}, {y})");
                    LeftClickAt(x, y);
                    
                    // Задержка 0.3 сек между кликами (не после последнего)
                    if (i < coordinates.Length - 1)
                    {
                        Thread.Sleep(300);
                    }
                }
            }
            else
            {
                Log("Изображение не совпадает. Кликаем сразу на последнюю координату...");
                
                // Кликаем сразу на последнюю координату
                if (token.IsCancellationRequested) return;
                int lastX = coordinates[coordinates.Length - 1][0];
                int lastY = coordinates[coordinates.Length - 1][1];
                Log($"Клик создания группы (последний): ({lastX}, {lastY})");
                LeftClickAt(lastX, lastY);
            }
            
            Log("Фаза создания группы завершена.");
        }

        // === ФАЗА СБОРА ===
        private void RunCollectionPhase(CancellationToken token)
        {
            Log("Начало фазы сбора...");
            
            // Кликаем 8 раз с задержкой 0.5 сек по координатам 970, 625
            for (int i = 0; i < 8; i++)
            {
                if (token.IsCancellationRequested) return;
                
                Log($"Клик сбора {i + 1}/8: (970, 625)");
                RightClickAt(970, 625);
                Thread.Sleep(200);
                LeftClickAt(885, 312);

                // Не ждем после последнего клика
                if (i < 7)
                {
                    Thread.Sleep(500);
                }
            }
            
            Log("Фаза сбора завершена.");
        }
    }
}