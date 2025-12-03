using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace io
{
    public partial class DatabaseViewer : Form
    {
        private DataGridView dataGridView;
        private Button btnRefresh;
        private Label lblTotalProfit;
        
        public DatabaseViewer()
        {
            InitializeComponent();
            LoadData();
        }
        
        private void InitializeComponent()
        {
            this.Text = "История сессий";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            
            // DataGridView для отображения данных
            dataGridView = new DataGridView
            {
                Location = new Point(10, 10),
                Size = new Size(760, 480),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            this.Controls.Add(dataGridView);
            
            // Кнопка обновления
            btnRefresh = new Button
            {
                Text = "Обновить",
                Location = new Point(10, 500),
                Size = new Size(100, 30)
            };
            btnRefresh.Click += BtnRefresh_Click;
            this.Controls.Add(btnRefresh);
            
            // Метка для общей прибыли
            lblTotalProfit = new Label
            {
                Text = "Общая прибыль: 0",
                Location = new Point(120, 505),
                AutoSize = true,
                Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold)
            };
            this.Controls.Add(lblTotalProfit);
        }
        
        private void LoadData()
        {
            try
            {
                string xmlPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "sessions.xml");
                
                if (!File.Exists(xmlPath))
                {
                    dataGridView.DataSource = null;
                    lblTotalProfit.Text = "База данных не найдена";
                    return;
                }
                
                // Создаем таблицу
                DataTable table = new DataTable();
                table.Columns.Add("ID", typeof(int));
                table.Columns.Add("Время начала", typeof(string));
                table.Columns.Add("Время окончания", typeof(string));
                table.Columns.Add("Итераций", typeof(int));
                table.Columns.Add("Прибыль", typeof(long));
                
                // Загружаем данные из XML
                XDocument doc = XDocument.Load(xmlPath);
                var sessions = doc.Root?.Elements("Session")
                    .OrderByDescending(s => int.Parse(s.Element("Id")?.Value ?? "0"))
                    .ToList();
                
                if (sessions != null)
                {
                    foreach (var session in sessions)
                    {
                        table.Rows.Add(
                            int.Parse(session.Element("Id")?.Value ?? "0"),
                            session.Element("StartTime")?.Value ?? "",
                            session.Element("EndTime")?.Value ?? "",
                            int.Parse(session.Element("Iterations")?.Value ?? "0"),
                            long.Parse(session.Element("Profit")?.Value ?? "0")
                        );
                    }
                }
                
                dataGridView.DataSource = table;
                
                // Вычисляем общую прибыль
                long totalProfit = 0;
                foreach (DataRow row in table.Rows)
                {
                    if (row["Прибыль"] != DBNull.Value)
                    {
                        totalProfit += Convert.ToInt64(row["Прибыль"]);
                    }
                }
                
                lblTotalProfit.Text = $"Общая прибыль: {totalProfit:N0}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки данных: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            LoadData();
        }
    }
}

