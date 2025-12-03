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
using System.Xml.Linq;
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
        private Button btnTestReadData;
        private Button btnViewDatabase;
        private Button btnStartRecording;
        private Button btnStopRecording;
        private Button btnRunWasdRoute;
        private RichTextBox logBox;
        private Label lblStatus;
        private TextBox txtIterations;
        private Label lblIterations;
        private ComboBox cmbCharacter;
        private Label lblCharacter;

        // === ПЕРЕМЕННЫЕ ЛОГИКИ ===
        private CancellationTokenSource _cancellationTokenSource;
        private IntPtr gameWindow = IntPtr.Zero;
        private const string WINDOW_NAME = "World";
        
        // === ПЕРЕМЕННЫЕ ДЛЯ ОТСЛЕЖИВАНИЯ ===
        private long initialGold = 0;
        private DateTime sessionStartTime;
        private int totalIterations = 0;
        
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
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

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
        const int VK_Z = 0x5A;
        const int VK_R = 0x52;
        const int VK_5 = 0x35;
        const int VK_ESC = 0x1B;
        const int VK_F2 = 0x71;
        const int VK_F3 = 0x72;
        const int VK_F7 = 0x76;
        const int VK_F8 = 0x77;
        const int VK_3 = 0x33;
        const int VK_NUM5 = 0x65, VK_NUM2 = 0x62, VK_NUM7 = 0x67, VK_NUM8 = 0x68;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        static readonly Rectangle SpeedZone = new Rectangle(28, 591, 227, 76);

        private HashSet<int> currentlyHeldKeys = new HashSet<int>();
        private bool waitingForFullSpeed = false;
        
        // === ПЕРЕМЕННАЯ ДЛЯ ВЫБОРА ПЕРСОНАЖА ===
        private string selectedCharacter = "Друид"; // По умолчанию друид
        
        // === ПЕРЕМЕННЫЕ ДЛЯ ЗАПИСИ МАРШРУТА ===
        private bool isRecording = false;
        private List<WasdEvent> recordedRoute = new List<WasdEvent>();
        private System.Windows.Forms.Timer recordingTimer;
        private long recordingStartTime = 0;
        private HashSet<int> lastRecordedKeys = new HashSet<int>();

        public Form1()
        {
            // Определяем пути к файлам роутов в папке Debug
            string debugPath = Path.GetDirectoryName(Application.ExecutablePath);
            clickRoutePath = Path.Combine(debugPath, "route.txt");
            wasdRoutePath = Path.Combine(debugPath, "wasd_route.txt");
            enterRoutePath = Path.Combine(debugPath, "enter_route.txt");
            
            InitializeCustomUI();
            this.Text = "IO";
            this.Size = new Size(500, 450);
            this.KeyPreview = true; // Включаем обработку клавиш на уровне формы
            this.KeyDown += Form1_KeyDown;
            
            // Инициализируем БД
            InitializeDatabase();
        }
        
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F8)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                BtnTestReadData_Click(sender, e);
            }
        }
        
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F8)
            {
                BtnTestReadData_Click(this, EventArgs.Empty);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
            
            // Выбор персонажа
            lblCharacter = new Label() { Text = "Персонаж:", Location = new Point(220, startY), AutoSize = true };
            cmbCharacter = new ComboBox() { Location = new Point(290, startY - 2), Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbCharacter.Items.Add("Друид");
            cmbCharacter.Items.Add("Маг");
            cmbCharacter.SelectedIndex = 0; // По умолчанию друид
            cmbCharacter.SelectedIndexChanged += CmbCharacter_SelectedIndexChanged;

            int buttonY = startY + 30;
            btnStart = new Button() { Text = "СТАРТ ВСЕГО", Location = new Point(10, buttonY), Width = 150, Height = 40, BackColor = Color.LightGreen };
            btnStop = new Button() { Text = "СТОП", Location = new Point(170, buttonY), Width = 100, Height = 40, BackColor = Color.LightPink, Enabled = false };
            btnTestReadData = new Button() { Text = "Тест чтения данных", Location = new Point(280, buttonY), Width = 180, Height = 40, BackColor = Color.LightYellow };
            btnViewDatabase = new Button() { Text = "История сессий", Location = new Point(10, buttonY + 50), Width = 150, Height = 30, BackColor = Color.LightCyan };
            btnStartRecording = new Button() { Text = "Начать запись маршрута", Location = new Point(170, buttonY + 50), Width = 180, Height = 30, BackColor = Color.LightGreen };
            btnStopRecording = new Button() { Text = "Остановить запись", Location = new Point(360, buttonY + 50), Width = 110, Height = 30, BackColor = Color.LightPink, Enabled = false };
            btnRunWasdRoute = new Button() { Text = "Воспроизвести WASD", Location = new Point(10, buttonY + 85), Width = 150, Height = 30, BackColor = Color.LightBlue };

            lblStatus = new Label() { Text = "Ожидание...", Location = new Point(170, buttonY + 90), AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

            logBox = new RichTextBox() { Location = new Point(10, buttonY + 120), Width = 460, Height = 210, ReadOnly = true, BackColor = Color.Black, ForeColor = Color.Lime };

            this.Controls.Add(lblIterations);
            this.Controls.Add(txtIterations);
            this.Controls.Add(lblCharacter);
            this.Controls.Add(cmbCharacter);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);
            this.Controls.Add(btnTestReadData);
            this.Controls.Add(btnViewDatabase);
            this.Controls.Add(btnStartRecording);
            this.Controls.Add(btnStopRecording);
            this.Controls.Add(btnRunWasdRoute);
            this.Controls.Add(lblStatus);
            this.Controls.Add(logBox);

            btnStart.Click += BtnStart_Click;
            btnStop.Click += BtnStop_Click;
            btnTestReadData.Click += BtnTestReadData_Click;
            btnViewDatabase.Click += BtnViewDatabase_Click;
            btnStartRecording.Click += BtnStartRecording_Click;
            btnStopRecording.Click += BtnStopRecording_Click;
            btnRunWasdRoute.Click += BtnRunWasdRoute_Click;
            
            // Инициализация таймера для записи
            recordingTimer = new System.Windows.Forms.Timer();
            recordingTimer.Interval = 10; // Проверка каждые 10мс
            recordingTimer.Tick += RecordingTimer_Tick;
        }
        
        private void CmbCharacter_SelectedIndexChanged(object sender, EventArgs e)
        {
            selectedCharacter = cmbCharacter.SelectedItem.ToString();
            Log($"Выбран персонаж: {selectedCharacter}");
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
            
            // Запоминаем начальные данные
            sessionStartTime = DateTime.Now;
            totalIterations = iterations;
            Log("Чтение начального количества голды...");
            initialGold = ReadGoldAmount();
            Log($"Начальная голда: {initialGold}");

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

                    // 6. ФАЗА ПРОДАЖИ (если нужно)
                    bool isLastIteration = (iteration == iterations);
                    int inventorySlots = ReadInventorySlots();
                    Log($"Свободных слотов в инвентаре: {inventorySlots}");
                    
                    if (inventorySlots < 35 || isLastIteration)
                    {
                        Log("Запуск фазы продажи...");
                        lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Продажа)";
                        lblStatus.ForeColor = Color.Gold;
                        
                        try
                        {
                            await Task.Run(() => RunSellPhase(token), token);
                        }
                        catch (Exception ex)
                        {
                            Log($"Ошибка в фазе продажи: {ex.Message}");
                            if (token.IsCancellationRequested) return;
                        }
                        
                        if (token.IsCancellationRequested)
                        {
                            Log("Отмена после фазы продажи.");
                            return;
                        }
                    }
                    else
                    {
                        Log($"Пропуск фазы продажи (свободных слотов: {inventorySlots}, требуется < 35)");
                    }

                    // 7. ФАЗА СОЗДАНИЯ ГРУППЫ
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
                    
                    // 8. ФАЗА ЗАХОДА В РЕЙД
                    Log("Создание группы завершено. Переход к фазе захода в рейд...");
                    lblStatus.Text = $"В РАБОТЕ (Итерация {iteration}/{iterations} - Заход в рейд)";
                    lblStatus.ForeColor = Color.DarkGreen;
                    
                    try
                    {
                        await Task.Run(() => RunEnterRaidPhase(token), token);
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
                
                // Финальное чтение голды и сохранение в БД
                DateTime sessionEndTime = DateTime.Now;
                long finalGold = ReadGoldAmount();
                long profit = finalGold - initialGold;
                
                Log("========================================");
                Log($"СЕССИЯ ЗАВЕРШЕНА");
                Log($"Начальная голда: {initialGold}");
                Log($"Финальная голда: {finalGold}");
                Log($"Прибыль: {profit}");
                Log("========================================");
                
                // Сохраняем в БД
                SaveSessionToDatabase(sessionStartTime, sessionEndTime, totalIterations, profit);
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
        
        private void BtnViewDatabase_Click(object sender, EventArgs e)
        {
            DatabaseViewer viewer = new DatabaseViewer();
            viewer.ShowDialog();
        }
        
        private void BtnTestReadData_Click(object sender, EventArgs e)
        {
            try
            {
                Log("=== Чтение данных персонажа (F8) ===");
                
                // Получаем путь к папке проекта для сохранения скриншотов
                string projectPath = Path.GetDirectoryName(Application.ExecutablePath);
                string screenshotsPath = Path.Combine(projectPath, "screenshots");
                if (!Directory.Exists(screenshotsPath))
                {
                    Directory.CreateDirectory(screenshotsPath);
                }
                
                // Читаем голду (левый нижний угол 1524, 1021, размер 58x27)
                // Верхний левый угол: (1524, 1021 - 27) = (1524, 994)
                string gold = ReadTextFromScreen(1524, 994, 58, 27, Path.Combine(screenshotsPath, "gold.png"));
                Log($"Голда: {gold}");
                
                // Читаем инвентарь (левый нижний угол 1464, 1029, размер 61x41)
                // Верхний левый угол: (1464, 1029 - 41) = (1464, 988)
                string inventory = ReadTextFromScreen(1464, 988, 61, 41, Path.Combine(screenshotsPath, "inventory.png"));
                Log($"Инвентарь: {inventory}");
                
                // Читаем статус боя (левый нижний угол 64, 539, размер 151x48)
                // Верхний левый угол: (64, 539 - 48) = (64, 491)
                string combatStatus = ReadTextFromScreen(64, 491, 151, 48, Path.Combine(screenshotsPath, "combat_status.png"));
                Log($"Статус боя: {combatStatus}");
                
                Log($"=== Чтение завершено. Скриншоты сохранены в: {screenshotsPath} ===");
            }
            catch (Exception ex)
            {
                Log($"ОШИБКА при чтении данных: {ex.Message}");
            }
        }
        
        private string ReadTextFromScreen(int x, int y, int width, int height, string savePath = null)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(width, height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
                    }
                    
                    // Сохраняем оригинальный скриншот, если указан путь
                    if (!string.IsNullOrEmpty(savePath))
                    {
                        try
                        {
                            bmp.Save(savePath, System.Drawing.Imaging.ImageFormat.Png);
                            Log($"Скриншот сохранен: {savePath}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Не удалось сохранить скриншот {savePath}: {ex.Message}");
                        }
                    }
                    
                    // Обработка изображения для лучшего распознавания
                    using (Bitmap proc = new Bitmap(bmp.Width, bmp.Height))
                    {
                        // Определяем, нужно ли инвертировать (для всех показателей используем одинаковую обработку)
                        bool needsInversion = savePath != null && 
                            (savePath.Contains("gold") || savePath.Contains("inventory") || savePath.Contains("combat_status"));
                        
                        for (int px = 0; px < bmp.Width; px++)
                        {
                            for (int py = 0; py < bmp.Height; py++)
                            {
                                Color c = bmp.GetPixel(px, py);
                                // Преобразуем в черно-белое для лучшего OCR
                                int gray = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
                                
                                // Для всех показателей используем инвертирование и более низкий порог
                                if (needsInversion)
                                {
                                    // Инвертируем: темный текст на светлом фоне -> светлый текст на темном
                                    proc.SetPixel(px, py, gray > 100 ? Color.Black : Color.White);
                                }
                                else
                                {
                                    // Обычная обработка (для других случаев)
                                    proc.SetPixel(px, py, gray > 128 ? Color.White : Color.Black);
                                }
                            }
                        }
                        
                        // Сохраняем обработанное изображение для отладки
                        if (!string.IsNullOrEmpty(savePath))
                        {
                            string processedPath = savePath.Replace(".png", "_processed.png");
                            try
                            {
                                proc.Save(processedPath, System.Drawing.Imaging.ImageFormat.Png);
                            }
                            catch { }
                        }
                        
                        using (var engine = new TesseractEngine("./tessdata", "eng", EngineMode.Default))
                        {
                            // Разрешаем цифры, буквы и некоторые символы
                            engine.SetVariable("tessedit_char_whitelist", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz /");
                            engine.DefaultPageSegMode = PageSegMode.SingleBlock;
                            
                            using (var page = engine.Process(proc))
                            {
                                string text = page.GetText().Trim();
                                return string.IsNullOrWhiteSpace(text) ? "(не распознано)" : text;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"(ошибка: {ex.Message})";
            }
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

            int clickCount = 0;
            foreach (var cmd in commands)
            {
                if (token.IsCancellationRequested) return;

                switch (cmd.Command)
                {
                    case "CLICK":
                        clickCount++;
                        Log($"Клик: {cmd.X}, {cmd.Y}");
                        RightClickAt(cmd.X, cmd.Y);
                        // ГЛАВНОЕ ИСПРАВЛЕНИЕ: Ждем пока персонаж не остановится после клика
                        WaitUntilStopped(token);
                        
                        // После второго клика нажимаем F2
                        if (clickCount == 2)
                        {
                            Log("Нажатие F2 после второго клика");
                            PressKey(VK_F2);
                            Thread.Sleep(200);
                        }
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

                // Обработка застревания/боя (только для друида)
                if (selectedCharacter != "Маг" && speed > 0.1f && speed < 150f)
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
                    SendKeyDown(ev.VK);
                    if (!currentlyHeldKeys.Contains(ev.VK)) currentlyHeldKeys.Add(ev.VK);
                    if (ev.VK == VK_W) waitingForFullSpeed = true;
                }
                else
                {
                    SendKeyUp(ev.VK);
                    if (currentlyHeldKeys.Contains(ev.VK)) currentlyHeldKeys.Remove(ev.VK);
                    if (ev.VK == VK_W)
                    {
                        waitingForFullSpeed = false;
                        
                        // Для мага: после отпускания W ждем скорость 0, затем фаза боя и сбор
                        if (selectedCharacter == "Маг")
                        {
                            Log("Маг: после отпускания W ждем скорость 0...");
                            
                            // Ждем, пока скорость станет 0
                            bool moved = false;
                            long waitStart = GetTimestampMs();
                            while (GetTimestampMs() - waitStart < 10000) // Максимум 10 секунд ожидания
                            {
                                if (token.IsCancellationRequested) return;
                                
                                float speed = GetCurrentSpeed();
                                if (speed > 0.1f) moved = true;
                                
                                if (moved && speed <= 0.1f)
                                {
                                    Log("Маг: скорость стала 0. Начинаем фазу боя...");
                                    
                                    // Фаза боя: 5 нажатий S с интервалом 1 сек
                                    for (int s = 0; s < 5; s++)
                                    {
                                        if (token.IsCancellationRequested) return;
                                        Log($"Маг: нажатие S {s + 1}/5");
                                        PressKey(VK_S);
                                        Thread.Sleep(1000);
                                    }
                                    
                                    // Сбор: 2 клика
                                    if (token.IsCancellationRequested) return;
                                    Log("Маг: клик правой кнопкой (970, 625)");
                                    RightClickAt(970, 625);
                                    Thread.Sleep(200);
                                    
                                    if (token.IsCancellationRequested) return;
                                    Log("Маг: клик левой кнопкой (885, 312)");
                                    LeftClickAt(885, 312);
                                    Thread.Sleep(200);
                                    
                                    Log("Маг: фаза боя и сбор завершены. Продолжаем WASD маршрут...");
                                    break;
                                }
                                
                                Thread.Sleep(100);
                            }
                            
                            if (GetTimestampMs() - waitStart >= 10000)
                            {
                                Log("Маг: таймаут ожидания скорости 0. Продолжаем маршрут.");
                            }
                        }
                    }
                }
                Log($"WASD: {(ev.IsDown ? "DOWN" : "UP")} 0x{ev.VK:X} ({ev.TimeMs}ms)");
            }

            // Финальная очистка
            Log("Очистка нажатых клавиш после WASD-маршрута...");
            foreach (int vk in currentlyHeldKeys.ToArray()) SendKeyUp(vk);
            currentlyHeldKeys.Clear();
            waitingForFullSpeed = false;
            Log("WASD-маршрут завершен. Функция RunWasdRoute завершается.");
        }

        private List<WasdEvent> ParseWasdRoute(string filePath)
        {
            var events = new List<WasdEvent>();
            foreach (var line in File.ReadAllLines(filePath))
            {
                // Формат: [TimeMs] [IsDown/KeyUp] [VK_Code] (VK код в десятичном формате)
                var p = line.Trim().Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 3) continue;

                if (long.TryParse(p[0], out long t) &&
                    int.TryParse(p[1], out int down) && // 1=DOWN, 0=UP
                    int.TryParse(p[2], out int vk)) // VK code в десятичном формате
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
                        // Для мага не нажимаем X
                        if (selectedCharacter == "Маг")
                        {
                            Thread.Sleep(120);
                            continue;
                        }
                        
                        float speed = GetCurrentSpeed();
                        // Для друида нормальная скорость 155%
                        float targetSpeed = 155f;
                        float speedThreshold = 153f;
                        
                        if (currentlyHeldKeys.Contains(VK_W) && speed > 0.1f && speed < speedThreshold)
                        {
                            Log($"Замедление ({speed:F1}%, цель: {targetSpeed}%) -> Жму X");
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
            // Сначала перемещаем мышь (как в тестовой программе)
            MoveMouse(x, y);
            
            // Затем делаем клик
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero);
        }
        
        private void MoveMouse(int x, int y)
        {
            uint absX = (uint)(x * 65535 / Screen.PrimaryScreen.Bounds.Width);
            uint absY = (uint)(y * 65535 / Screen.PrimaryScreen.Bounds.Height);
            mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, absX, absY, 0, IntPtr.Zero);
        }

        private void LeftClickAt(int x, int y)
        {
            // Сначала перемещаем мышь (как в тестовой программе)
            MoveMouse(x, y);
            
            // Затем делаем клик
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }

        private void SendKeyDown(int vk) => PostMessage(gameWindow, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        private void SendKeyUp(int vk) => PostMessage(gameWindow, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        private void PressKey(int vk) { SendKeyDown(vk); Thread.Sleep(80); SendKeyUp(vk); }
        private void PressX() => PressKey(VK_X);
        
        private bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        
        private void WaitForF8Key(CancellationToken token)
        {
            // Ждем отпускания клавиши (если она уже нажата)
            while (IsKeyDown(VK_F8))
            {
                if (token.IsCancellationRequested) return;
                Thread.Sleep(50);
            }
            
            // Ждем нажатия F8
            while (!IsKeyDown(VK_F8))
            {
                if (token.IsCancellationRequested) return;
                Thread.Sleep(50);
            }
            
            // Ждем отпускания клавиши
            while (IsKeyDown(VK_F8))
            {
                if (token.IsCancellationRequested) return;
                Thread.Sleep(50);
            }
            
            Log("F8 нажата, продолжаем выполнение...");
        }

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
            Thread.Sleep(200);
            if (token.IsCancellationRequested) return;
            Log("Нажатие R");
            PressKey(VK_R);
            if (token.IsCancellationRequested) return;
            Thread.Sleep(300);
            
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
            
            // Ждем 1 минуту (55000 мс) с проверкой статуса боя каждую секунду
            int totalWaitMs = 50000;
            int checkIntervalMs = 1000; // Проверяем каждую секунду
            int elapsedMs = 0;
            
            while (elapsedMs < totalWaitMs)
            {
                if (token.IsCancellationRequested) return;
                
                // Проверяем статус боя каждую секунду
                string status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    // После боя продолжаем ожидание (не перезапускаем фазу)
                }
                
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

        // === ФАЗА ЗАХОДА В РЕЙД ===
        private void RunEnterRaidPhase(CancellationToken token)
        {
            while (true)
            {
                Log("Начало фазы захода в рейд...");
                
                if (token.IsCancellationRequested) return;
                
                // Проверяем статус боя перед началом
                string status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    // После боя перезапускаем фазу захода в рейд
                    continue;
                }
                
                Log("Нажатие NUM2");
                PressKey(VK_NUM2);
                Thread.Sleep(500);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Нажатие NUM2 (второй раз)");
                PressKey(VK_NUM2);
                Thread.Sleep(1500);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Нажатие F7");
                PressKey(VK_F7);
                Thread.Sleep(200);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Нажатие SPACE");
                PressKey(VK_SPACE);
                Thread.Sleep(500);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Клик: (1160, 117)");
                RightClickAt(1160, 117);
                
                // Во время ожидания проверяем статус боя каждую секунду
                Log("Ожидание 10 секунд...");
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(1000);
                    if (token.IsCancellationRequested) return;
                    
                    status = ReadCombatStatus();
                    if (status == "UP")
                    {
                        Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                        RunCombatPhaseWith5(token);
                        break; // Выходим из цикла ожидания
                    }
                }
                
                // Если был бой, перезапускаем фазу
                if (status == "UP")
                {
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Нажатие F7");
                PressKey(VK_F7);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Нажатие NUM8");
                PressKey(VK_NUM8);
                Thread.Sleep(2000);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                Log("Клик: (1383, 515)");
                RightClickAt(1325, 348);
                Thread.Sleep(500);
                PressX();
                
                Log("Фаза захода в рейд завершена.");
                break; // Выходим из цикла
            }
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
        
        // === ФУНКЦИИ ДЛЯ ЧТЕНИЯ ДАННЫХ ===
        private long ReadGoldAmount()
        {
            try
            {
                string goldText = ReadTextFromScreen(1524, 994, 58, 27, null);
                // Убираем все нецифровые символы
                string digitsOnly = new string(goldText.Where(char.IsDigit).ToArray());
                if (long.TryParse(digitsOnly, out long gold))
                {
                    return gold;
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка чтения голды: {ex.Message}");
            }
            return 0;
        }
        
        private int ReadInventorySlots()
        {
            try
            {
                string inventoryText = ReadTextFromScreen(1464, 988, 61, 41, null);
                // Убираем все нецифровые символы
                string digitsOnly = new string(inventoryText.Where(char.IsDigit).ToArray());
                if (int.TryParse(digitsOnly, out int slots))
                {
                    return slots;
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка чтения инвентаря: {ex.Message}");
            }
            return 0;
        }
        
        private string ReadCombatStatus()
        {
            try
            {
                string statusText = ReadTextFromScreen(64, 491, 151, 48, null);
                // Ищем UP или DOWN в тексте
                string upperText = statusText.ToUpper();
                if (upperText.Contains("UP"))
                {
                    return "UP";
                }
                else if (upperText.Contains("DOWN"))
                {
                    return "DOWN";
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка чтения статуса боя: {ex.Message}");
            }
            return "UNKNOWN";
        }
        
        // === ФАЗА БОЯ С 5 (вместо R) ===
        private void RunCombatPhaseWith5(CancellationToken token)
        {
            Log("Начало фазы боя (с 5)...");
            
            if (token.IsCancellationRequested) return;
            
            // Нажимаем F3
            Log("Нажатие F3");
            PressKey(VK_F3);
            Thread.Sleep(200);
            if (token.IsCancellationRequested) return;
            
            // Нажимаем 5 (вместо R)
            Log("Нажатие 5");
            PressKey(VK_5);
            Thread.Sleep(500);
            if (token.IsCancellationRequested) return;
            
            // Поочередно нажимаем S и 3 в течение 6 секунд
            Log("Начало цикла боя (S и 3) на 6 секунд...");
            bool pressS = true; // Начинаем с S
            long combatStartTime = GetTimestampMs();
            const int combatDurationMs = 6000; // 6 секунд
            
            while (true)
            {
                if (token.IsCancellationRequested) return;
                
                // Проверяем, прошло ли 6 секунд
                long elapsed = GetTimestampMs() - combatStartTime;
                if (elapsed >= combatDurationMs)
                {
                    Log($"Прошло 6 секунд. Завершение фазы боя.");
                    break;
                }
                
                if (pressS)
                {
                    Log("Нажатие S");
                    SendKeyDown(VK_S);
                    Thread.Sleep(500);
                    SendKeyUp(VK_S);
                }
                else
                {
                    Log("Нажатие 3");
                    PressKey(VK_3);
                }
                
                pressS = !pressS;
                Thread.Sleep(500);
            }
            
            // Нажимаем ESC
            Log("Нажатие ESC");
            PressKey(VK_ESC);
            Thread.Sleep(500);
            
            Log("Фаза боя завершена.");
        }
        
        // === ФАЗА ПРОДАЖИ ===
        private void RunSellPhase(CancellationToken token)
        {
            while (true)
            {
                Log("Начало фазы продажи...");
                
                if (token.IsCancellationRequested) return;
                
                // Проверяем статус боя перед началом
                string status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    // После боя перезапускаем фазу продажи
                    continue;
                }
                
                // 1. Нажатие клавиши Z
                Log("Нажатие Z");
                PressKey(VK_Z);
                Thread.Sleep(1000);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue; // Перезапускаем фазу
                }
                
                if (token.IsCancellationRequested) return;
                
                // 2. Нажатие NUM8
                Log("Нажатие NUM8");
                PressKey(VK_NUM8);
                Thread.Sleep(1000);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue; // Перезапускаем фазу
                }
                
                if (token.IsCancellationRequested) return;
                
                // 3. Клик правой кнопкой на 1067, 721
                Log("Клик правой кнопкой: (1067, 721)");
                RightClickAt(1067, 721);
                Thread.Sleep(500);
                
                // Проверяем статус боя
                status = ReadCombatStatus();
                if (status == "UP")
                {
                    Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                    RunCombatPhaseWith5(token);
                    continue; // Перезапускаем фазу
                }
                
                if (token.IsCancellationRequested) return;
                
                // 4. Клик левой кнопкой на 147, 177
                Log("Клик левой кнопкой: (147, 177)");
                LeftClickAt(147, 177);
                
                // Во время ожидания проверяем статус боя каждую секунду
                for (int i = 0; i < 15; i++)
                {
                    Thread.Sleep(1000);
                    if (token.IsCancellationRequested) return;
                    
                    status = ReadCombatStatus();
                    if (status == "UP")
                    {
                        Log("Обнаружен статус боя UP! Начинаем фазу боя...");
                        RunCombatPhaseWith5(token);
                        break; // Выходим из цикла ожидания
                    }
                }
                
                // Если был бой, перезапускаем фазу
                if (status == "UP")
                {
                    continue;
                }
                
                if (token.IsCancellationRequested) return;
                
                // 5. Клик левой кнопкой на 383, 141
                Log("Клик левой кнопкой: (383, 141)");
                LeftClickAt(383, 141);
                Thread.Sleep(500);
                
                Log("Фаза продажи завершена.");
                break; // Выходим из цикла
            }
        }
        
        // === БАЗА ДАННЫХ (XML) ===
        private void InitializeDatabase()
        {
            try
            {
                string xmlPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "sessions.xml");
                
                // Создаем XML файл, если его нет
                if (!File.Exists(xmlPath))
                {
                    XDocument doc = new XDocument(
                        new XElement("Sessions")
                    );
                    doc.Save(xmlPath);
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка инициализации БД: {ex.Message}");
            }
        }
        
        private void SaveSessionToDatabase(DateTime startTime, DateTime endTime, int iterations, long profit)
        {
            try
            {
                string xmlPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "sessions.xml");
                
                XDocument doc;
                if (File.Exists(xmlPath))
                {
                    doc = XDocument.Load(xmlPath);
                }
                else
                {
                    doc = new XDocument(new XElement("Sessions"));
                }
                
                // Находим максимальный ID
                int maxId = 0;
                if (doc.Root != null)
                {
                    var ids = doc.Root.Elements("Session")
                        .Select(s => int.TryParse(s.Element("Id")?.Value, out int id) ? id : 0);
                    maxId = ids.Any() ? ids.Max() : 0;
                }
                
                // Добавляем новую сессию
                XElement newSession = new XElement("Session",
                    new XElement("Id", maxId + 1),
                    new XElement("StartTime", startTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    new XElement("EndTime", endTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    new XElement("Iterations", iterations),
                    new XElement("Profit", profit)
                );
                
                if (doc.Root == null)
                {
                    doc = new XDocument(new XElement("Sessions", newSession));
                }
                else
                {
                    doc.Root.Add(newSession);
                }
                
                doc.Save(xmlPath);
                Log($"Данные сессии сохранены в БД: {xmlPath}");
            }
            catch (Exception ex)
            {
                Log($"Ошибка сохранения в БД: {ex.Message}");
            }
        }

        // === ОБРАБОТЧИКИ КНОПОК ЗАПИСИ И ВОСПРОИЗВЕДЕНИЯ WASD МАРШРУТА ===
        private void BtnStartRecording_Click(object sender, EventArgs e)
        {
            if (isRecording)
            {
                Log("Запись уже идет!");
                return;
            }

            recordedRoute.Clear();
            lastRecordedKeys.Clear();
            recordingStartTime = GetTimestampMs();
            isRecording = true;
            recordingTimer.Start();

            btnStartRecording.Enabled = false;
            btnStopRecording.Enabled = true;

            Log("=== НАЧАЛО ЗАПИСИ WASD МАРШРУТА ===");
            Log("Нажимайте клавиши для записи. Нажмите 'Остановить запись' для завершения.");
        }

        private void BtnStopRecording_Click(object sender, EventArgs e)
        {
            if (!isRecording)
            {
                Log("Запись не активна!");
                return;
            }

            isRecording = false;
            recordingTimer.Stop();

            // Отпускаем все зажатые клавиши
            foreach (int vk in lastRecordedKeys.ToArray())
            {
                long currentTime = GetTimestampMs();
                long elapsed = currentTime - recordingStartTime;
                recordedRoute.Add(new WasdEvent { TimeMs = elapsed, VK = vk, IsDown = false });
            }
            lastRecordedKeys.Clear();

            btnStartRecording.Enabled = true;
            btnStopRecording.Enabled = false;

            // Сохраняем маршрут
            SaveRecordedRoute();
            Log("=== ЗАПИСЬ ЗАВЕРШЕНА ===");
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            if (!isRecording) return;

            long currentTime = GetTimestampMs();
            long elapsed = currentTime - recordingStartTime;

            // Проверяем основные клавиши движения (W, A, S, D, SPACE и другие)
            int[] keysToCheck = { VK_W, VK_A, VK_S, VK_D, VK_SPACE, VK_X, VK_Z, VK_R, VK_5, VK_ESC, VK_F2, VK_F3, VK_3 };

            foreach (int vk in keysToCheck)
            {
                bool isPressed = (GetAsyncKeyState(vk) & 0x8000) != 0;
                bool wasPressed = lastRecordedKeys.Contains(vk);

                if (isPressed && !wasPressed)
                {
                    // Клавиша нажата
                    recordedRoute.Add(new WasdEvent { TimeMs = elapsed, VK = vk, IsDown = true });
                    lastRecordedKeys.Add(vk);
                    Log($"Записано: DOWN {vk} ({elapsed}ms)");
                }
                else if (!isPressed && wasPressed)
                {
                    // Клавиша отпущена
                    recordedRoute.Add(new WasdEvent { TimeMs = elapsed, VK = vk, IsDown = false });
                    lastRecordedKeys.Remove(vk);
                    Log($"Записано: UP {vk} ({elapsed}ms)");
                }
            }
        }

        private void SaveRecordedRoute()
        {
            if (recordedRoute.Count == 0)
            {
                Log("Нет записанных событий для сохранения!");
                return;
            }

            try
            {
                // Создаем резервную копию старого файла
                if (File.Exists(wasdRoutePath))
                {
                    string backupPath = wasdRoutePath + ".backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(wasdRoutePath, backupPath);
                    Log($"Создана резервная копия: {backupPath}");
                }

                // Сохраняем в формате: таймстамп\tkeydown/keyup\tvk_code
                using (StreamWriter writer = new StreamWriter(wasdRoutePath, false, Encoding.UTF8))
                {
                    foreach (var ev in recordedRoute)
                    {
                        writer.WriteLine($"{ev.TimeMs}\t{(ev.IsDown ? 1 : 0)}\t{ev.VK}");
                    }
                }

                Log($"Маршрут сохранен: {wasdRoutePath} ({recordedRoute.Count} событий)");
            }
            catch (Exception ex)
            {
                Log($"Ошибка сохранения маршрута: {ex.Message}");
            }
        }

        private void BtnRunWasdRoute_Click(object sender, EventArgs e)
        {
            if (!File.Exists(wasdRoutePath))
            {
                Log($"ОШИБКА: Файл WASD-маршрута не найден: {wasdRoutePath}");
                MessageBox.Show($"Файл wasd_route.txt не найден в папке:\n{Path.GetDirectoryName(wasdRoutePath)}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            btnRunWasdRoute.Enabled = false;
            btnStop.Enabled = true;

            Task.Run(() =>
            {
                try
                {
                    RunWasdRoute(wasdRoutePath, token);
                }
                catch (Exception ex)
                {
                    Log($"Ошибка при воспроизведении WASD маршрута: {ex.Message}");
                }
                finally
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        btnRunWasdRoute.Enabled = true;
                        btnStop.Enabled = false;
                        lblStatus.Text = "Ожидание...";
                    });
                }
            }, token);
        }
    }
}