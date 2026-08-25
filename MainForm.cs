using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public partial class MainForm : Form
    {
        // ---- целевой сервер (глобально, для всех вкладок) ----
        private CheckBox chkLocalServer;
        private readonly ToolTip _toolTip = new ToolTip();
        private ComboBox cmbTargetServer;

        // ---- общий блок вывода ----
        private RichTextBox txtOutput;
        private TabControl tabs; // нужен, чтобы программно переключать вкладку (напр. двойной клик по зоне -> вкладка Scopes)

        // ---- вкладка "Зоны" ----
        private ListBox lstZones;
        private TextBox txtNewZoneName;
        private ComboBox cmbZoneType;
        private TextBox txtZoneFilter;
        private ComboBox cmbZoneSort;
        private Button btnZoneSortDir;
        private Label lblZoneSource;
        private List<PSObject> _lastZones = new List<PSObject>();
        private bool _zoneSortAscending = true;

        // ---- вкладка "Scopes и записи" ----
        private ComboBox cmbScopeZoneName;   // имя зоны, для которой смотрим scopes - выпадающий список
        private TextBox txtNewScopeName;
        private TextBox txtRecordScopeName; // в какой scope добавляем/смотрим записи - синхронизируется с деревом
        private TextBox txtRecordName;
        private ComboBox cmbNewRecordType; // A / AAAA / CNAME / PTR / TXT / SRV
        private TextBox txtRecordValue;    // IP / целевое имя / текст - смысл зависит от типа записи
        private TextBox txtSrvPriority;
        private TextBox txtSrvWeight;
        private TextBox txtSrvPort;
        private ListBox lstRecords;
        private List<PSObject> _lastScopeRecords = new List<PSObject>(); // сырые данные с сервера, без сортировки/фильтра
        private List<PSObject> _displayedRecords = new List<PSObject>(); // 1:1 с текущими строками lstRecords - null для строк-папок
        private List<RecordTreeNode> _displayedFolders = new List<RecordTreeNode>(); // 1:1 с текущими строками lstRecords - null для строк-записей
        private TextBox txtRecordFilter;
        private ComboBox cmbRecordSort;
        private Button btnRecordSortDir;
        private bool _recordSortAscending = true;

        // ---- дерево записей (верхний уровень - scope'ы зоны, ниже - папки по точкам в имени,
        //      как в dnsmgmt.msc; scope подгружается лениво, при первом выборе/раскрытии) ----
        private TreeView treeRecordFolders;
        private RecordTreeNode _currentFolderNode; // какая "папка" сейчас показана в правом списке
        private Label lblCurrentFolderPath; // "Добавление в: ..." - видимая подсказка, куда попадёт новая запись
        private Dictionary<RecordTreeNode, TreeNode> _folderToTreeNode = new Dictionary<RecordTreeNode, TreeNode>();
        private Dictionary<RecordTreeNode, string> _folderRootToScopeName = new Dictionary<RecordTreeNode, string>(); // корень поддерева -> имя scope, которому он принадлежит
        private HashSet<TreeNode> _loadedScopeTreeNodes = new HashSet<TreeNode>(); // какие узлы scope уже реально подгружены (не просто заглушка)

        /// <summary>
        /// Узел дерева записей - группировка по составным именам (admin.pro32connect -> папка
        /// "pro32connect" содержит запись "admin"), как это делает стандартная оснастка dnsmgmt.msc.
        /// Строится из уже загруженного плоского списка записей scope - без обращения к серверу.
        /// </summary>
        private class RecordTreeNode
        {
            public string Label;
            public RecordTreeNode Parent;
            public Dictionary<string, RecordTreeNode> Children = new Dictionary<string, RecordTreeNode>(StringComparer.OrdinalIgnoreCase);
            public List<PSObject> RecordsHere = new List<PSObject>(); // записи, чьё полное имя заканчивается ИМЕННО на этом узле
        }

        // ---- вкладка "Подсети" ----
        private ListBox lstSubnets;
        private TextBox txtSubnetName;
        private TextBox txtSubnetCidr;

        // ---- вкладка "Политики" ----
        private ComboBox cmbPolicyZoneName; // выпадающий список зон, как на вкладке Scopes
        private ListBox lstPolicies;
        private RichTextBox rtbPolicyDetails; // подробности выбранной политики (подсети/scope), чтобы не уезжало за экран одной строкой
        private TextBox txtPolicyName;
        private TextBox txtPolicySubnetName;
        private TextBox txtPolicyScopeName;
        private List<PolicyInfo> _lastPolicies = new List<PolicyInfo>(); // 1:1 с элементами lstPolicies

        private class PolicyInfo
        {
            public string Name;
            public string SubnetDisplay; // "net_100 (10.0.100.0/24), Old_DNS_redirect13 (...)"
            public string Scope;
        }

        public MainForm()
        {
            InitializeComponent();
            Shown += async (s, e) =>
            {
                // Вкладка "Зоны" открыта по умолчанию при старте - SelectedIndexChanged для неё
                // не сработает (переключения не было), поэтому грузим её явно здесь же.
                // Если первый запрос не удался (например, эта машина вообще не DNS-сервер) -
                // второй такой же запрос обречён точно так же, не дублируем одну и ту же ошибку.
                var zonesLoaded = await RefreshZonesAsync();
                if (zonesLoaded) await RefreshAllZoneCombosAsync();
            };
            FormClosing += (s, e) => DnsHelper.DisposeActiveCimSession();
        }

        // ============================================================
        //  UI
        // ============================================================

        private void InitializeComponent()
        {
            Text = $"DNS Server Tool v{AppVersion.Current}";
            Width = 1050;
            Height = 810;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);

            // Иконка окна (заголовок, панель задач) - общий хелпер AppIcon (см. AppIcon.cs),
            // тот же используется и во всех дополнительных диалогах (DangerConfirmDialog и т.п.).
            if (AppIcon.Current != null) Icon = AppIcon.Current;

            tabs = new TabControl { Dock = DockStyle.Fill };

            tabs.TabPages.Add(BuildZonesTab());
            tabs.TabPages.Add(BuildScopesTab());
            tabs.TabPages.Add(BuildSubnetsTab());
            tabs.TabPages.Add(BuildPoliciesTab());

            // Автоподгрузка при переходе на вкладку - только если там ещё пусто (первый заход).
            // Если человек уже сам нажимал "Обновить"/"↻" - повторно не дёргаем сервер на каждый клик по вкладке.
            tabs.SelectedIndexChanged += async (s, e) =>
            {
                switch (tabs.SelectedIndex)
                {
                    case 0 when lstZones.Items.Count == 0:
                        await RefreshZonesAsync();
                        break;
                    case 1 when cmbScopeZoneName.Items.Count == 0:
                        await RefreshAllZoneCombosAsync();
                        break;
                    case 3 when cmbPolicyZoneName.Items.Count == 0:
                        await RefreshAllZoneCombosAsync();
                        break;
                }
            };

            var targetServerPanel = BuildTargetServerPanel();
            var brandHeaderPanel = BuildBrandHeaderPanel();

            var outputPanel = BuildOutputPanel();

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                BackColor = Color.WhiteSmoke
            };

            var footerRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 2, 6, 0)
            };

            // Маленькая версия значка приложения рядом с подписью - тот же приглушённый стиль
            // футера, минимальный визуальный след (не баннер, просто тихий бренд-штрих).
            // Кликабельно - открывает "О программе" с полным баннером (единственное место,
            // где он показывается целиком).
            if (AppIcon.Current != null)
            {
                var picLogo = new PictureBox
                {
                    Image = AppIcon.Current.ToBitmap(),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(16, 16),
                    Margin = new Padding(0, 1, 4, 0),
                    Cursor = Cursors.Hand
                };
                picLogo.Click += (s, e) => AboutDialog.Show();
                footerRow.Controls.Add(picLogo);
            }

            var lblFooterText = new Label
            {
                Text = "Создано by foosber, 2026",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic),
                Margin = new Padding(0, 3, 0, 0),
                Cursor = Cursors.Hand
            };
            lblFooterText.Click += (s, e) => AboutDialog.Show();
            footerRow.Controls.Add(lblFooterText);

            footer.Controls.Add(footerRow);

            // Порядок добавления важен: то, что снизу (Dock=Bottom), добавляем первым,
            // а Fill - последним, чтобы он занял оставшееся место.
            Controls.Add(tabs);
            Controls.Add(outputPanel);
            Controls.Add(footer);
            Controls.Add(targetServerPanel);
            Controls.Add(brandHeaderPanel);
        }

        /// <summary>
        /// Панель сверху окна: куда физически идут все команды. Пусто = локальный компьютер
        /// (как было раньше). Если указать имя сервера - ВСЕ операции на всех вкладках начинают
        /// выполняться на нём через -ComputerName (WinRM), без переезда приложения на другой сервер.
        /// </summary>
        /// <summary>
        /// Отдельный блок сверху окна - специально под баннер-логотип в читаемом размере.
        /// Раньше баннер пытались втиснуть в панель подключения (те же 32px по высоте, что и
        /// сама панель) - при таком сжатии текст на баннере ("DNS Server Tool" и тэглайн)
        /// превращался в нечитаемое пятно. Теперь у баннера своя полоса, панель подключения
        /// ниже осталась той же высоты, что и была.
        /// </summary>
        private Control BuildBrandHeaderPanel()
        {
            const int height = 56;
            var panel = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Color.FromArgb(245, 247, 249) };

            var bannerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "banner.png");
            if (File.Exists(bannerPath))
            {
                try
                {
                    using var original = Image.FromFile(bannerPath);
                    var displayHeight = height - 8; // небольшой отступ сверху/снизу, не впритык к краям
                    var displayWidth = (int)(original.Width * (displayHeight / (float)original.Height));
                    var bannerPic = new PictureBox
                    {
                        Image = new Bitmap(original, new Size(displayWidth, displayHeight)),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(displayWidth, displayHeight),
                        Location = new Point(12, (height - displayHeight) / 2),
                        Cursor = Cursors.Hand
                    };
                    bannerPic.Click += (s, e) => AboutDialog.Show();
                    _toolTip.SetToolTip(bannerPic, "О программе");
                    panel.Controls.Add(bannerPic);
                }
                catch { /* повреждённый файл баннера - не критично, просто пропускаем */ }
            }

            return panel;
        }

        private Control BuildTargetServerPanel()
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 4, 6, 4), BackColor = Color.FromArgb(245, 247, 249) };

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

            row.Controls.Add(new Label
            {
                Text = "Целевой DNS-сервер:",
                AutoSize = true,
                Margin = new Padding(0, 6, 4, 0),
                Font = new Font(Font, FontStyle.Bold)
            });

            chkLocalServer = new CheckBox { Text = "Локальный", Checked = true, AutoSize = true, Margin = new Padding(0, 5, 8, 0) };

            cmbTargetServer = new ComboBox { Width = 220, Margin = new Padding(0, 3, 8, 0), Enabled = false, DropDownStyle = ComboBoxStyle.DropDown };
            cmbTargetServer.Items.AddRange(AppSettings.GetList("RemoteServerHistory").Cast<object>().ToArray());

            // Взаимоисключающе и однозначно: галочка ИЛИ текст, совмещать нельзя, никакой
            // путаницы с текстом-подсказкой (в отличие от прежнего варианта с placeholder).
            chkLocalServer.CheckedChanged += (s, e) =>
            {
                cmbTargetServer.Enabled = !chkLocalServer.Checked;
                if (chkLocalServer.Checked)
                {
                    cmbTargetServer.Text = "";
                }
                else
                {
                    // Переключились на "не локальный" - обновляем список истории (вдруг с прошлого
                    // раза успешно добавился новый сервер) и сразу открываем выпадашку для удобства.
                    var current = cmbTargetServer.Text;
                    cmbTargetServer.Items.Clear();
                    cmbTargetServer.Items.AddRange(AppSettings.GetList("RemoteServerHistory").Cast<object>().ToArray());
                    cmbTargetServer.Text = current;
                    if (cmbTargetServer.Items.Count > 0) cmbTargetServer.DroppedDown = true;
                }
                UpdateTargetComputerName();
            };
            cmbTargetServer.TextChanged += (s, e) =>
            {
                if (cmbTargetServer.Text.Length > 0 && chkLocalServer.Checked)
                    chkLocalServer.Checked = false; // начал печатать - галочка "Локальный" снимается сама
                UpdateTargetComputerName();
            };

            var btnTestConnection = new Button { Text = "Проверить подключение", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
            btnTestConnection.Click += async (s, e) => await TestTargetServerConnectionAsync();

            var hint = HelpIcon.Create(_toolTip, "Нужен WinRM и права на управление DNS на удалённом сервере.");

            row.Controls.Add(chkLocalServer);
            row.Controls.Add(cmbTargetServer);
            row.Controls.Add(btnTestConnection);
            row.Controls.Add(hint);

            panel.Controls.Add(row);
            return panel;
        }

        private void UpdateTargetComputerName()
        {
            DnsHelper.ComputerName = chkLocalServer.Checked ? "" : cmbTargetServer.Text.Trim();
            DnsHelper.InvalidateCimSessionIfServerChanged(DnsHelper.ComputerName);
        }

        private async Task TestTargetServerConnectionAsync()
        {
            var target = chkLocalServer.Checked ? "" : cmbTargetServer.Text.Trim();
            AppendLog(chkLocalServer.Checked
                ? "Проверяю подключение к локальному DNS-серверу..."
                : $"Проверяю подключение к '{target}'...");

            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(log);

            if (WasSuccess(log))
            {
                AppendLog($"OK: подключение работает, зон видно: {results.Count}");
                return;
            }

            // Обычная проверка не удалась - для удалённого сервера предлагаем ввести другие
            // учётные данные (текущая Windows-учётка может просто не иметь прав на этом сервере).
            if (!chkLocalServer.Checked && !string.IsNullOrEmpty(target))
            {
                AppendLog("Подключение не удалось текущей учётной записью - предлагаю ввести другие данные...");
                var authOk = ServerAuthDialog.Show(target);
                if (!authOk)
                {
                    AppendLog("Аутентификация отменена или не удалась - работаем без доступа к этому серверу.");
                    return;
                }

                AppendLog("Повторно проверяю подключение с новыми учётными данными...");
                var (retryResults, retryLog) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
                AppendLog(retryLog);
                if (WasSuccess(retryLog))
                    AppendLog($"OK: подключение работает, зон видно: {retryResults.Count}");
            }
        }

        private Control BuildOutputPanel()
        {
            const int expandedHeight = 220;
            const int collapsedHeight = 34; // только строка с кнопками, без самого текста

            var panel = new Panel { Dock = DockStyle.Bottom, Height = expandedHeight, Padding = new Padding(6) };

            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            header.Controls.Add(new Label { Text = "Вывод:", AutoSize = true, Margin = new Padding(0, 6, 8, 0), Font = new Font(Font, FontStyle.Bold) });

            var btnToggle = new Button { Text = "▲ Свернуть", AutoSize = true };
            header.Controls.Add(btnToggle);

            var btnClear = new Button { Text = "Очистить", AutoSize = true };
            btnClear.Click += (s, e) => txtOutput.Clear();
            header.Controls.Add(btnClear);

            var btnOpenLog = new Button { Text = "Открыть файл лога изменений", AutoSize = true };
            btnOpenLog.Click += (s, e) => OpenChangeLog();
            header.Controls.Add(btnOpenLog);

            txtOutput = new RichTextBox
            {
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White
            };

            // Список вывода может разрастаться и заставлять много крутить колёсиком - даём
            // свернуть блок в одну строку с кнопками, не теряя сам текст (просто прячем).
            var collapsed = false;
            btnToggle.Click += (s, e) =>
            {
                collapsed = !collapsed;
                txtOutput.Visible = !collapsed;
                panel.Height = collapsed ? collapsedHeight : expandedHeight;
                btnToggle.Text = collapsed ? "▼ Показать" : "▲ Свернуть";
            };

            panel.Controls.Add(txtOutput);
            panel.Controls.Add(header);
            return panel;
        }

        // ---- маленькие хелперы разметки ----

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 2)
            };
            foreach (var c in controls)
            {
                c.Margin = new Padding(4, 6, 4, 2);
                p.Controls.Add(c);
            }
            return p;
        }

        private static FlowLayoutPanel Column(params Control[] rows)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Dock = DockStyle.Top
            };
            foreach (var r in rows)
            {
                r.Margin = new Padding(0);
                p.Controls.Add(r);
            }
            return p;
        }

        private static TabPage WrapTab(string title, Control controlsColumn, Control listControl)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            controlsColumn.Dock = DockStyle.Top;
            listControl.Dock = DockStyle.Fill;
            if (listControl is ListBox lb) { lb.Font = new Font("Consolas", 9F); lb.HorizontalScrollbar = true; }

            table.Controls.Add(controlsColumn, 0, 0);
            table.Controls.Add(listControl, 0, 1);

            var page = new TabPage(title) { Padding = new Padding(10) };
            page.Controls.Add(table);
            return page;
        }

        // .NET Framework 4.8 не знает TextBox.PlaceholderText (это фича WinForms из .NET 5+),
        // поэтому имитируем placeholder вручную: серый текст-подсказка, которая исчезает по фокусу.
        private static TextBox Tb(int width = 200, string placeholderText = null)
        {
            var t = new TextBox { Width = width };
            if (!string.IsNullOrEmpty(placeholderText))
            {
                t.Tag = placeholderText;
                t.Text = placeholderText;
                t.ForeColor = Color.Gray;

                t.Enter += (s, e) =>
                {
                    if (t.ForeColor == Color.Gray)
                    {
                        t.Text = "";
                        t.ForeColor = SystemColors.WindowText;
                    }
                };
                t.Leave += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(t.Text))
                    {
                        // Порядок важен: ForeColor выставляем ДО Text, потому что TextChanged
                        // срабатывает синхронно на присвоение Text, а слушатели (например у поля
                        // "Целевой сервер") должны увидеть серый цвет уже в момент события,
                        // иначе placeholder ошибочно прочитается как настоящее значение.
                        t.ForeColor = Color.Gray;
                        t.Text = (string)t.Tag;
                    }
                };
            }
            return t;
        }

        /// <summary>
        /// Читает реальное значение TextBox - если сейчас показан серый placeholder,
        /// возвращает "" вместо текста подсказки.
        /// </summary>
        private static string Val(TextBox t)
        {
            if (t.Tag is string placeholder && t.ForeColor == Color.Gray && t.Text == placeholder)
                return "";
            return t.Text.Trim();
        }

        private static string Val(ComboBox c) => (c.Text ?? "").Trim();

        /// <summary>Меняет текст-подсказку у уже созданного поля (используется при смене типа записи).</summary>
        private static void SetPlaceholder(TextBox t, string newPlaceholder)
        {
            var wasShowingPlaceholder = t.Tag is string oldPh && t.ForeColor == Color.Gray && t.Text == oldPh;
            t.Tag = newPlaceholder;
            if (wasShowingPlaceholder || string.IsNullOrEmpty(t.Text))
            {
                t.Text = newPlaceholder;
                t.ForeColor = Color.Gray;
            }
        }

        private static string PlaceholderForRecordType(string type) => (type ?? "").ToUpperInvariant() switch
        {
            "AAAA" => "IPv6, напр. fe80::1",
            "CNAME" => "целевое имя (FQDN), напр. www.example.com",
            "PTR" => "целевое имя (FQDN) для reverse-записи",
            "NS" => "имя сервера (FQDN), напр. ns1.example.com",
            "MX" => "почтовый сервер (FQDN), напр. mail.example.com — приоритет в поле Priority/Preference",
            "TXT" => "текст записи, напр. v=spf1 include:_spf.example.com ~all",
            "SRV" => "целевой хост (Target), напр. sipserver.example.com",
            _ => "IPv4, напр. 10.0.1.10"
        };

        // ============================================================
        //  Вкладка "Зоны"
        // ============================================================

        private TabPage BuildZonesTab()
        {
            lstZones = new ListBox();

            var btnRefresh = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список зон", _toolTip,
                async (s, e) => await RefreshZonesAsync());

            var btnReloadZone = IconFactory.CreateButton(IconFactory.Refresh(), "Перезагрузить выбранную зону (dnscmd /ZoneReload, только локально)", _toolTip,
                async (s, e) => await ReloadSelectedZoneAsync());

            var btnAdd = IconFactory.CreateButton(IconFactory.Add(), "Создать зону...", _toolTip, async (s, e) =>
            {
                var (name, type) = AddZoneDialog.Show();
                if (name == null) return; // отмена
                txtNewZoneName.Text = name;
                cmbZoneType.Text = type;
                await AddZoneAsync();
            });

            var btnRemove = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранную зону", _toolTip,
                async (s, e) => await RemoveZoneAsync());

            // Поля остаются - их заполняет диалог выше (AddZoneDialog), а не человек напрямую.
            // Не убираю сами TextBox/ComboBox, потому что RemoveZoneAsync/AddZoneAsync их читают -
            // так меньше правок в уже работающей логике, поля просто больше не показываются на экране.
            txtNewZoneName = Tb(220, "имя зоны, напр. corp.local");
            cmbZoneType = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbZoneType.Items.AddRange(new object[]
            {
                "AD-интегрированная (реплика: домен)",
                "AD-интегрированная (реплика: лес)",
                "Файловая (.dns на диске)"
            });
            cmbZoneType.SelectedIndex = 0;

            // Фильтр + сортировка + экспорт - применяются к тому, что уже загружено (без нового
            // обращения к серверу), поэтому реагируют мгновенно по мере ввода. Фильтр оставляем
            // видимым текстовым полем (не прячем за иконку) - живой поиск по мере набора важнее
            // компактности именно для него.
            txtZoneFilter = new TextBox { Width = 160, Margin = new Padding(2) };
            txtZoneFilter.TextChanged += (s, e) => RenderZonesList();

            cmbZoneSort = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2) };
            cmbZoneSort.Items.AddRange(new object[] { "Имя", "Тип" });
            cmbZoneSort.SelectedIndex = 0;
            cmbZoneSort.SelectedIndexChanged += (s, e) => RenderZonesList();

            btnZoneSortDir = new Button { Text = "▲", Width = 32, Margin = new Padding(2) };
            btnZoneSortDir.Click += (s, e) =>
            {
                _zoneSortAscending = !_zoneSortAscending;
                btnZoneSortDir.Text = _zoneSortAscending ? "▲" : "▼";
                RenderZonesList();
            };

            var btnExportZones = IconFactory.CreateButton(IconFactory.Export(), "Экспорт в файл...", _toolTip,
                (s, e) => ExportListToFile(lstZones.Items.Cast<string>(), $"zones_{DateTime.Now:yyyyMMdd_HHmmss}.txt"));

            var column = Column(
                Row(btnRefresh, btnAdd, btnRemove, btnReloadZone,
                    new Label { Text = "  Фильтр:", AutoSize = true, Margin = new Padding(4, 6, 0, 2) }, txtZoneFilter,
                    cmbZoneSort, btnZoneSortDir, btnExportZones)
            );

            // Оборачиваем список зон в панель с полоской "источник зоны" снизу - показывает,
            // AD/файл это или Secondary с её мастер-серверами, для выбранной строки списка.
            var zonesWrapper = new Panel { Dock = DockStyle.Fill };
            lstZones.Dock = DockStyle.Fill;
            lstZones.Font = new Font("Consolas", 9F);
            lstZones.HorizontalScrollbar = true;
            lstZones.SelectedIndexChanged += (s, e) => ShowZoneSource();

            // Двойной клик по зоне - сразу переходим на вкладку "Scopes и записи" с этой
            // зоной уже выбранной и её scope'ами загруженными, не нужно переключаться и
            // подставлять имя зоны вручную. Порядок важен: сначала грузим данные, и только
            // потом переключаем вкладку - иначе собственное автообновление вкладки при
            // переключении (см. tabs.SelectedIndexChanged) может гонкой затереть то, что
            // мы только что установили.
            lstZones.DoubleClick += async (s, e) =>
            {
                if (lstZones.SelectedItem == null) return;
                var zoneName = lstZones.SelectedItem.ToString().Split('[')[0].Trim();

                await RefreshAllZoneCombosAsync(); // гарантированно наполняем список зон, прежде чем выбирать в нём
                cmbScopeZoneName.Text = zoneName;
                await RefreshScopesAsync();
                tabs.SelectedIndex = 1; // "Scopes и записи" - переключаем последним, когда всё уже готово
            };

            lblZoneSource = new Label
            {
                Text = "Источник: -",
                Dock = DockStyle.Bottom,
                Height = 36,
                Padding = new Padding(6, 4, 6, 4),
                BackColor = Color.WhiteSmoke,
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8.5F),
                AutoEllipsis = true
            };

            zonesWrapper.Controls.Add(lstZones);
            zonesWrapper.Controls.Add(lblZoneSource);

            return WrapTab("Зоны", column, zonesWrapper);
        }

        /// <summary>Перестраивает lstZones из _lastZones с учётом текущего фильтра и сортировки - без обращения к серверу.</summary>
        private void RenderZonesList()
        {
            var filter = (txtZoneFilter.Text ?? "").Trim();

            var rows = _lastZones.Select(z =>
            {
                var name = z.Properties["ZoneName"]?.Value?.ToString() ?? "";
                var zoneType = z.Properties["ZoneType"]?.Value?.ToString() ?? "?";
                var isDsIntegrated = z.Properties["IsDsIntegrated"]?.Value;
                var tag = zoneType == "Primary"
                    ? (isDsIntegrated is bool b && b ? "Primary/AD" : "Primary/файл")
                    : zoneType;
                return (name, tag, display: $"{name,-35} [{tag}]");
            });

            if (!string.IsNullOrEmpty(filter))
                rows = rows.Where(r => r.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        r.tag.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

            rows = cmbZoneSort.SelectedIndex == 1
                ? (_zoneSortAscending ? rows.OrderBy(r => r.tag, StringComparer.OrdinalIgnoreCase) : rows.OrderByDescending(r => r.tag, StringComparer.OrdinalIgnoreCase))
                : (_zoneSortAscending ? rows.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase) : rows.OrderByDescending(r => r.name, StringComparer.OrdinalIgnoreCase));

            lstZones.Items.Clear();
            foreach (var r in rows) lstZones.Items.Add(r.display);
            ShowZoneSource();
        }

        /// <summary>Показывает источник выбранной зоны - AD/файл для Primary, мастер-серверы для Secondary/Stub.</summary>
        private void ShowZoneSource()
        {
            if (lblZoneSource == null) return;

            if (lstZones.SelectedItem == null)
            {
                lblZoneSource.Text = "Источник: - (выбери зону в списке)";
                return;
            }

            var name = lstZones.SelectedItem.ToString().Split('[')[0].Trim();
            var z = _lastZones.FirstOrDefault(o =>
                string.Equals(o.Properties["ZoneName"]?.Value?.ToString(), name, StringComparison.OrdinalIgnoreCase));

            if (z == null) { lblZoneSource.Text = "Источник: -"; return; }

            var zoneType = z.Properties["ZoneType"]?.Value?.ToString() ?? "?";
            if (zoneType == "Primary")
            {
                var isDsIntegrated = z.Properties["IsDsIntegrated"]?.Value;
                lblZoneSource.Text = (isDsIntegrated is bool b && b)
                    ? "Источник: Primary, хранится в Active Directory (реплицируется между DC домена)."
                    : $"Источник: Primary, файловая зона - {z.Properties["ZoneFile"]?.Value}";
            }
            else
            {
                var masters = DnsHelper.FlattenPropertyValue(z.Properties["MasterServers"]?.Value);
                lblZoneSource.Text = string.IsNullOrEmpty(masters)
                    ? $"Источник: {zoneType} (только чтение здесь), мастер-серверы не указаны."
                    : $"Источник: {zoneType} (только чтение здесь) - мастер-серверы: {masters}";
            }
        }

        private async Task<bool> RefreshZonesAsync()
        {
            AppendLog("Загружаю список зон...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(log);
            _lastZones = results;
            RenderZonesList();
            return WasSuccess(log);
        }

        private async Task AddZoneAsync()
        {
            var zoneName = Val(txtNewZoneName);
            if (string.IsNullOrEmpty(zoneName))
            {
                AppendLog("Укажи имя новой зоны.");
                return;
            }

            var parameters = new Dictionary<string, object> { ["Name"] = zoneName };
            string kindLabel;

            switch (cmbZoneType.SelectedIndex)
            {
                case 1: // AD, реплика на весь лес
                    parameters["ReplicationScope"] = "Forest";
                    kindLabel = "AD (Forest)";
                    break;
                case 2: // Файловая - AD ничего не знает про неё, всё хранится в .dns на диске
                    parameters["ZoneFile"] = zoneName + ".dns";
                    kindLabel = "файловая";
                    break;
                default: // AD, реплика на домен (значение по умолчанию, как раньше)
                    parameters["ReplicationScope"] = "Domain";
                    kindLabel = "AD (Domain)";
                    break;
            }

            AppendLog($"Создаю первичную зону '{zoneName}' ({kindLabel})...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Add-DnsServerPrimaryZone", parameters));
            AppendLog(log);
            FileLogger.LogChange("ZONE ADD", zoneName, $"Тип={kindLabel}", WasSuccess(log), log);
            await RefreshZonesAsync();
        }

        private async Task RemoveZoneAsync()
        {
            if (lstZones.SelectedItem == null)
            {
                AppendLog("Сначала выбери зону в списке.");
                return;
            }

            var zoneName = lstZones.SelectedItem.ToString().Split('[')[0].Trim();
            if (!DangerConfirmDialog.Show(
                    "Удаление зоны",
                    $"   Удалить зону \"{zoneName}\" целиком?",
                    "Будут безвозвратно удалены ВСЕ записи, scopes и настройки этой зоны. " +
                    "Это действие нельзя отменить."))
                return;

            var parameters = new Dictionary<string, object> { ["Name"] = zoneName, ["Force"] = true };
            AppendLog($"Удаляю зону '{zoneName}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerZone", parameters));
            AppendLog(log);
            FileLogger.LogChange("ZONE DELETE", zoneName, "-", WasSuccess(log), log);
            await RefreshZonesAsync();
        }

        /// <summary>
        /// Перезагружает выбранную зону с диска (dnscmd /ZoneReload) - тот же механизм, что уже
        /// используется в файловом режиме для Secondary-зон, но теперь доступен напрямую для
        /// любой зоны: полезно, если запись поправили в обход приложения (руками в файле) и
        /// нужно, чтобы DNS Server перечитал её без перезапуска всей службы.
        /// </summary>
        private async Task ReloadSelectedZoneAsync()
        {
            if (lstZones.SelectedItem == null)
            {
                AppendLog("Сначала выбери зону в списке.");
                return;
            }

            var zoneName = lstZones.SelectedItem.ToString().Split('[')[0].Trim();
            AppendLog($"Перезагружаю зону '{zoneName}' (dnscmd /ZoneReload)...");

            var result = await Task.Run(() => RunDnscmdZoneReload(zoneName));
            AppendLog(result);

            var success = result.StartsWith("OK");
            FileLogger.LogChange("ZONE RELOAD", zoneName, "dnscmd /ZoneReload", success, success ? null : result);

            await RefreshZonesAsync();
        }

        // ============================================================
        //  Вкладка "Scopes и записи"
        // ============================================================

        private TabPage BuildScopesTab()
        {
            lstRecords = new ListBox();

            // Дерево записей: верхний уровень - scope'ы зоны, внутри каждого - папки записей
            // (группировка по составным именам, как в dnsmgmt.msc). Scope подгружается ЛЕНИВО -
            // при первом выборе/раскрытии его узла, а не все разом (некоторые scope содержат
            // сотни записей - незачем тянуть их все, если человек смотрит только один).
            treeRecordFolders = new TreeView { HideSelection = false };
            treeRecordFolders.AfterSelect += async (s, e) =>
            {
                if (e.Node?.Tag is RecordTreeNode node)
                {
                    _currentFolderNode = node;
                    var root = node;
                    while (root.Parent != null) root = root.Parent;
                    if (_folderRootToScopeName.TryGetValue(root, out var ownerScope))
                        txtRecordScopeName.Text = ownerScope;
                    UpdateCurrentFolderPathLabel();
                    RenderRecordsList();
                }
                else if (e.Node?.Tag is string scopeName && !_loadedScopeTreeNodes.Contains(e.Node))
                {
                    await LoadScopeIntoTreeAsync(e.Node, scopeName);
                    e.Node.Expand();
                }
            };
            // Подстраховка: если раскрыть стрелкой узел scope, который ещё не выбирали кликом -
            // тот же ленивый догруз, чтобы не остаться с одной заглушкой "..." внутри.
            treeRecordFolders.BeforeExpand += async (s, e) =>
            {
                if (e.Node?.Tag is string scopeName && !_loadedScopeTreeNodes.Contains(e.Node))
                    await LoadScopeIntoTreeAsync(e.Node, scopeName);
            };

            // Правый клик по дереву - выделить узел под курсором, затем меню "Создать папку".
            treeRecordFolders.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var node = treeRecordFolders.GetNodeAt(e.Location);
                    if (node != null) treeRecordFolders.SelectedNode = node;
                }
            };
            var treeContextMenu = new ContextMenuStrip();
            var menuCreateFolder = new ToolStripMenuItem("Создать папку (поддомен, * + IP)...");
            menuCreateFolder.Click += async (s, e) => await CreateSubfolderAsync();
            treeContextMenu.Items.Add(menuCreateFolder);
            treeRecordFolders.ContextMenuStrip = treeContextMenu;

            // Двойной клик по записи - сразу открыть редактирование (самый частый сценарий).
            // Если это строка-папка - EditSelectedRecordAsync сам распознает это и зайдёт внутрь
            // вместо попытки редактирования (см. NavigateToFolder внутри неё).
            lstRecords.DoubleClick += async (s, e) => await EditSelectedRecordAsync();

            // Правый клик - меню "Проверить" / "Изменить" / "Удалить" - все действия над
            // записью в одном месте, отдельная кнопка "Удалить" на панели больше не нужна.
            var recordsContextMenu = new ContextMenuStrip();
            var menuCheck = new ToolStripMenuItem("Проверить запись (nslookup / ping)...");
            menuCheck.Click += (s, e) => CheckSelectedRecord();
            var menuEdit = new ToolStripMenuItem("Изменить запись...");
            menuEdit.Click += async (s, e) => await EditSelectedRecordAsync();
            var menuDelete = new ToolStripMenuItem("Удалить запись...");
            menuDelete.Click += async (s, e) => await RemoveRecordAsync();
            recordsContextMenu.Items.Add(menuCheck);
            recordsContextMenu.Items.Add(menuEdit);
            recordsContextMenu.Items.Add(new ToolStripSeparator()); // отделяем деструктивное действие от остальных
            recordsContextMenu.Items.Add(menuDelete);
            // Клик правой кнопкой должен сначала выделить строку под курсором - иначе меню
            // применится к тому, что было выделено раньше (или ни к чему).
            lstRecords.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var idx = lstRecords.IndexFromPoint(e.Location);
                    if (idx >= 0) lstRecords.SelectedIndex = idx;
                }
            };
            lstRecords.ContextMenuStrip = recordsContextMenu;

            cmbScopeZoneName = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            var btnLoadZoneNames = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список зон", _toolTip,
                async (s, e) => await RefreshAllZoneCombosAsync());
            var btnLoadScopes = IconFactory.CreateButton(IconFactory.Folder(), "Показать scopes зоны", _toolTip,
                async (s, e) => await RefreshScopesAsync());

            // Выбор зоны из выпадающего списка (клик по варианту, не просто набор текста) -
            // сразу подгружаем её scopes (а RefreshScopesAsync дальше сам выберет первый scope
            // и подгрузит его записи).
            cmbScopeZoneName.SelectedIndexChanged += async (s, e) => await RefreshScopesAsync();

            // Поля ниже больше не показываются на панели - их заполняют диалоги перед вызовом
            // уже существующей логики (AddScopeAsync/AddRecordToScopeAsync и т.п.), чтобы не
            // переписывать саму бизнес-логику ради смены интерфейса.
            txtNewScopeName = Tb(180, "имя нового scope");
            var btnAddScope = IconFactory.CreateButton(IconFactory.Add(), "Создать scope...", _toolTip, async (s, e) =>
            {
                var zoneHint = string.IsNullOrEmpty(Val(cmbScopeZoneName)) ? "(зона не выбрана)" : Val(cmbScopeZoneName);
                var name = AddScopeDialog.Show(zoneHint);
                if (name == null) return;
                txtNewScopeName.Text = name;
                await AddScopeAsync();
            });

            var btnRemoveScope = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранный scope", _toolTip,
                async (s, e) => await RemoveScopeAsync());

            txtRecordScopeName = Tb(140, "scope для записей");
            var btnLoadRecords = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить записи текущего scope (выбранного в дереве слева)", _toolTip,
                async (s, e) => await RefreshRecordsAsync());

            txtRecordName = Tb(140, "имя хоста (или @ для корня зоны)");

            cmbNewRecordType = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbNewRecordType.Items.AddRange(new object[] { "A", "AAAA", "CNAME", "PTR", "NS", "MX", "TXT", "SRV" });
            cmbNewRecordType.SelectedIndex = 0;

            txtRecordValue = Tb(220, "IPv4, напр. 10.0.1.10");
            cmbNewRecordType.SelectedIndexChanged += (s, e) => SetPlaceholder(txtRecordValue, PlaceholderForRecordType(cmbNewRecordType.Text));

            txtSrvPriority = Tb(50, "10");
            txtSrvWeight = Tb(50, "10");
            txtSrvPort = Tb(50, "443");

            var btnAddRecord = IconFactory.CreateButton(IconFactory.Add(), "Добавить запись в текущую папку...", _toolTip, async (s, e) =>
            {
                var result = RecordEditDialog.Show("A", "", "", "10", "10", "443", isNew: true);
                if (result == null) return; // отмена
                cmbNewRecordType.Text = result.Type;
                txtRecordName.Text = result.Name;
                txtRecordValue.Text = result.Value;
                txtSrvPriority.Text = result.Priority;
                txtSrvWeight.Text = result.Weight;
                txtSrvPort.Text = result.Port;
                await AddRecordToScopeAsync();
            });

            // Отдельный, явно подписанный "аварийный" путь для Secondary-зон, где обычный API
            // пишет отказ (WIN32 9611) - правит .dns-файл scope напрямую на ЭТОЙ машине и
            // перезагружает зону через dnscmd. См. AddRecordToScopeFileAsync().
            var btnAddRecordFile = IconFactory.CreateButton(IconFactory.Notepad(), "Добавить запись в файл (обходной путь для Secondary-зон)...", _toolTip, async (s, e) =>
            {
                var confirmInfo = MessageBox.Show(
                    "Это обходной путь для Secondary-зон: строка будет дописана НАПРЯМУЮ в .dns-файл " +
                    "scope на этой машине, в обход обычного API. Используй, только если обычная " +
                    "\"Добавить запись\" отказывает с ошибкой \"Недопустимый тип зоны DNS\" (WIN32 9611).\n\nПродолжить?",
                    "Файловый режим", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (confirmInfo != DialogResult.OK) return;

                var result = RecordEditDialog.Show("A", "", "", "10", "10", "443", isNew: true);
                if (result == null) return;
                cmbNewRecordType.Text = result.Type;
                txtRecordName.Text = result.Name;
                txtRecordValue.Text = result.Value;
                txtSrvPriority.Text = result.Priority;
                txtSrvWeight.Text = result.Weight;
                txtSrvPort.Text = result.Port;
                await AddRecordToScopeFileAsync();
            });

            // Фильтр + сортировка + экспорт для записей - применяются мгновенно, без нового
            // обращения к серверу, к уже загруженному списку. Фильтр оставляем видимым текстовым
            // полем (живой поиск по мере набора важнее компактности именно для него).
            txtRecordFilter = new TextBox { Width = 140, Margin = new Padding(2) };
            txtRecordFilter.TextChanged += (s, e) => RenderRecordsList();

            cmbRecordSort = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2) };
            cmbRecordSort.Items.AddRange(new object[] { "Имя", "Тип", "Значение" });
            cmbRecordSort.SelectedIndex = 0;
            cmbRecordSort.SelectedIndexChanged += (s, e) => RenderRecordsList();

            btnRecordSortDir = new Button { Text = "▲", Width = 32, Margin = new Padding(2) };
            btnRecordSortDir.Click += (s, e) =>
            {
                _recordSortAscending = !_recordSortAscending;
                btnRecordSortDir.Text = _recordSortAscending ? "▲" : "▼";
                RenderRecordsList();
            };

            var btnExportRecords = IconFactory.CreateButton(IconFactory.Export(), "Экспорт в файл...", _toolTip,
                (s, e) => ExportListToFile(lstRecords.Items.Cast<string>(), $"records_{Val(txtRecordScopeName)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"));

            var recordsFilterRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true
            };
            recordsFilterRow.Controls.Add(btnAddRecord);
            recordsFilterRow.Controls.Add(btnAddRecordFile);
            recordsFilterRow.Controls.Add(new Label { Text = "  Фильтр:", AutoSize = true, Margin = new Padding(2, 6, 0, 0) });
            recordsFilterRow.Controls.Add(txtRecordFilter);
            recordsFilterRow.Controls.Add(cmbRecordSort);
            recordsFilterRow.Controls.Add(btnRecordSortDir);
            recordsFilterRow.Controls.Add(btnExportRecords);

            lstRecords.Dock = DockStyle.Fill;
            lstRecords.Font = new Font("Consolas", 9F);
            lstRecords.HorizontalScrollbar = true;
            var recordsWrapper = new Panel { Dock = DockStyle.Fill };
            recordsWrapper.Controls.Add(lstRecords);
            recordsWrapper.Controls.Add(recordsFilterRow);

            var hint = HelpIcon.Create(_toolTip,
                "Запись добавляется в scope/папку, которая сейчас выбрана в дереве слева.\n" +
                "Для записи в корне зоны (SOA/NS/SPF и т.п.) укажи имя \"@\" в диалоге добавления.\n" +
                "Слева - дерево: scope'ы зоны сверху, внутри каждого - записи, сгруппированные по\n" +
                "составным именам (как в dnsmgmt.msc). Двойной клик по записи справа - изменить;\n" +
                "правая кнопка мыши - меню (проверить/изменить/удалить).");

            lblCurrentFolderPath = new Label
            {
                Text = "Добавление в: корень scope",
                AutoSize = true,
                ForeColor = Color.SteelBlue,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(4, 6, 4, 2)
            };

            var column = Column(
                Row(new Label { Text = "Зона:", AutoSize = true, Margin = new Padding(4, 8, 4, 2) }, cmbScopeZoneName, btnLoadZoneNames, btnLoadScopes,
                    new Label { Text = "  ", AutoSize = true }, btnAddScope, btnRemoveScope,
                    new Label { Text = "  ", AutoSize = true }, btnLoadRecords, hint),
                Row(lblCurrentFolderPath)
            );

            return WrapTabTwoLists("Scopes и записи", column,
                "Scopes зоны", treeRecordFolders,
                "Записи выбранной папки", recordsWrapper,
                "ScopesRecordsSplitter");
        }

        /// <summary>
        /// Как WrapTab, но с двумя панелями рядом (бок о бок), разделёнными перетаскиваемой
        /// границей - например слева список имён, справа список записей или подробности
        /// выбранного элемента. Позиция границы запоминается в settings.ini под ключом
        /// splitterSettingsKey и восстанавливается при следующем запуске.
        /// </summary>
        private static TabPage WrapTabTwoLists(string title, Control controlsColumn,
            string leftTitle, Control leftList, string rightTitle, Control rightList,
            string splitterSettingsKey)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            controlsColumn.Dock = DockStyle.Top;

            var split = new SplitContainer
            {
                // Явно задаём стартовый размер ДО min-size свойств: у только что созданного
                // SplitContainer размер по умолчанию маленький (~150px), и если сумма
                // Panel1MinSize+Panel2MinSize+SplitterWidth больше этого стартового размера,
                // WinForms бросает InvalidOperationException прямо здесь же, при инициализации -
                // до Dock=Fill и до любых try/catch в обработчиках событий.
                Size = new Size(800, 500),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical, // граница - вертикальная линия, панели слева/справа
                SplitterWidth = 6,
                Panel1MinSize = 100,
                Panel2MinSize = 120
            };

            leftList.Dock = DockStyle.Fill;
            rightList.Dock = DockStyle.Fill;
            if (leftList is ListBox leftLb) { leftLb.Font = new Font("Consolas", 9F); leftLb.HorizontalScrollbar = true; }
            if (rightList is ListBox rightLb) { rightLb.Font = new Font("Consolas", 9F); rightLb.HorizontalScrollbar = true; }

            var leftPanel = new Panel { Dock = DockStyle.Fill };
            leftPanel.Controls.Add(leftList);
            leftPanel.Controls.Add(new Label { Text = leftTitle, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Padding = new Padding(2, 4, 2, 2) });

            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(rightList);
            rightPanel.Controls.Add(new Label { Text = rightTitle, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Padding = new Padding(2, 4, 2, 2) });

            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(rightPanel);

            // Восстанавливаем сохранённую позицию границы; если сохранённое значение уже не
            // помещается (например окно стало у́же) - SplitContainer сам подберёт ближайшее
            // валидное, ошибка тут не критична и не должна ронять приложение.
            split.HandleCreated += (s, e) =>
            {
                try { split.SplitterDistance = AppSettings.GetInt(splitterSettingsKey, 380); }
                catch { /* сохранённое значение больше не подходит по размеру - оставляем как есть */ }
            };
            split.SplitterMoved += (s, e) => AppSettings.SetInt(splitterSettingsKey, split.SplitterDistance);

            table.Controls.Add(controlsColumn, 0, 0);
            table.Controls.Add(split, 0, 1);

            var page = new TabPage(title) { Padding = new Padding(10) };
            page.Controls.Add(table);
            return page;
        }

        /// <summary>Подгружает имена всех зон в выпадающий список на вкладке Scopes.</summary>
        /// <summary>Один запрос Get-DnsServerZone - заполняет сразу оба выпадающих списка зон (Scopes и Политики).</summary>
        private async Task RefreshAllZoneCombosAsync()
        {
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(log);

            var names = DnsHelper.GetStringProperty(results, "ZoneName");

            foreach (var combo in new[] { cmbScopeZoneName, cmbPolicyZoneName })
            {
                var current = combo.Text;
                combo.Items.Clear();
                foreach (var name in names) combo.Items.Add(name);
                combo.Text = current; // не затираем то, что человек уже успел ввести/выбрать вручную
            }
        }

        private async Task RefreshScopesAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            if (string.IsNullOrEmpty(zoneName)) { AppendLog("Укажи имя зоны."); return; }

            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName };
            AppendLog($"Загружаю scopes зоны '{zoneName}'...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZoneScope", parameters));
            AppendLog(log);

            var scopeNames = DnsHelper.GetStringProperty(results, "ZoneScope");
            if (scopeNames.Count == 0) scopeNames = DnsHelper.GetStringProperty(results, "Name");

            treeRecordFolders.Nodes.Clear();
            _folderToTreeNode.Clear();
            _folderRootToScopeName.Clear();
            _loadedScopeTreeNodes.Clear();
            _currentFolderNode = null;
            lstRecords.Items.Clear();
            _displayedRecords.Clear();
            _displayedFolders.Clear();

            // Scope не подгружается сразу целиком - только когда реально понадобится (клик/раскрытие).
            // Некоторые scope содержат сотни записей, незачем тянуть все разом, если смотрят один.
            foreach (var name in scopeNames)
            {
                var scopeNode = new TreeNode(name) { Tag = name };
                scopeNode.Nodes.Add(new TreeNode("...")); // заглушка - только чтобы была стрелка разворачивания
                treeRecordFolders.Nodes.Add(scopeNode);
            }

            // Сразу подгружаем и показываем первый scope - не нужно кликать вручную,
            // чтобы увидеть содержимое хотя бы одного scope сразу после выбора зоны.
            if (treeRecordFolders.Nodes.Count > 0)
            {
                var first = treeRecordFolders.Nodes[0];
                await LoadScopeIntoTreeAsync(first, (string)first.Tag);
                first.Expand();
            }
        }

        private async Task AddScopeAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtNewScopeName);
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Нужны и имя зоны, и имя scope.");
                return;
            }

            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["Name"] = scopeName };
            AppendLog($"Создаю scope '{scopeName}' в зоне '{zoneName}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Add-DnsServerZoneScope", parameters));
            AppendLog(log);
            FileLogger.LogChange("SCOPE ADD", zoneName, $"Scope={scopeName}", WasSuccess(log), log);
            await RefreshScopesAsync();
        }

        private async Task RemoveScopeAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName); // синхронизируется с текущим выбором в дереве
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Укажи зону и выбери scope в дереве слева.");
                return;
            }

            if (!DangerConfirmDialog.Show(
                    "Удаление scope",
                    $"   Удалить scope \"{scopeName}\" из зоны \"{zoneName}\"?",
                    "Будут безвозвратно удалены ВСЕ записи внутри этого scope. " +
                    "Все политики, ссылающиеся на этот scope, перестанут работать. " +
                    "Это действие нельзя отменить."))
                return;

            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["Name"] = scopeName, ["Force"] = true };
            AppendLog($"Удаляю scope '{scopeName}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerZoneScope", parameters));
            AppendLog(log);
            FileLogger.LogChange("SCOPE DELETE", zoneName, $"Scope={scopeName}", WasSuccess(log), log);
            await RefreshScopesAsync();
        }

        /// <summary>
        /// DNS-командлеты ждут ИМЯ ОТНОСИТЕЛЬНО ЗОНЫ (например "bla.bla" для записи внутри
        /// "bla.bla.corp.local" в зоне "corp.local") и сами дописывают зону при создании.
        /// Если пользователь ввёл имя целиком, с зоной на конце ("bla.bla.corp.local") -
        /// зона приклеится ВТОРОЙ раз ("bla.bla.corp.local.corp.local"). Срезаем суффикс
        /// зоны, если он есть, чтобы многоуровневые имена (bla.bla, а не просто bla)
        /// создавались как в обычной оснастке - вложенной записью, а не дублем зоны.
        /// </summary>
        private static string NormalizeRecordName(string name, string zoneName)
        {
            if (string.IsNullOrEmpty(name) || name == "@" || string.IsNullOrEmpty(zoneName))
                return name;

            var suffix = "." + zoneName;
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - suffix.Length);

            // Точное совпадение с именем зоны целиком - это и есть корень зоны
            if (string.Equals(name, zoneName, StringComparison.OrdinalIgnoreCase))
                return "@";

            return name;
        }

        /// <summary>
        /// Восстанавливает DNS-суффикс текущей папки в дереве - обход СНИЗУ ВВЕРХ (от узла к
        /// корню scope) уже даёт метки в правильном порядке для конкатенации (без разворота):
        /// если сейчас внутри "_msdcs > dc > _tcp", суффикс будет "_tcp.dc._msdcs". Корень scope
        /// (Label == "" и Parent == null) не включается.
        /// </summary>
        private static string GetFolderPathSuffix(RecordTreeNode node)
        {
            var parts = new List<string>();
            var current = node;
            while (current != null && current.Parent != null)
            {
                parts.Add(current.Label);
                current = current.Parent;
            }
            return string.Join(".", parts);
        }

        /// <summary>Обновляет видимую подсказку "Добавление в: ..." - вызывать при любой смене текущей папки.</summary>
        private void UpdateCurrentFolderPathLabel()
        {
            if (lblCurrentFolderPath == null) return;
            var suffix = GetFolderPathSuffix(_currentFolderNode);
            lblCurrentFolderPath.Text = string.IsNullOrEmpty(suffix)
                ? "Добавление в: корень scope"
                : $"Добавление в: {suffix}  (запись \"test\" станет \"test.{suffix}\")";
        }

        /// <summary>
        /// Приклеивает текущую папку к введённому имени записи, чтобы новая запись создавалась
        /// именно там, где сейчас находится пользователь в дереве - "test" внутри папки
        /// "pro32connect" станет "test.pro32connect", а не просто "test" в корне scope.
        /// "@" внутри папки означает "сама эта папка" (запись без доп. метки).
        /// </summary>
        private string ApplyFolderPrefix(string enteredName)
        {
            var folderSuffix = GetFolderPathSuffix(_currentFolderNode);
            if (string.IsNullOrEmpty(folderSuffix)) return enteredName; // мы в корне scope - ничего приклеивать не нужно

            if (enteredName == "@") return folderSuffix; // "@" в папке = сама папка, без доп. метки
            return $"{enteredName}.{folderSuffix}";
        }

        private async Task AddRecordToScopeAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            var recordName = ApplyFolderPrefix(NormalizeRecordName(Val(txtRecordName), zoneName));
            var value = Val(txtRecordValue);
            var type = cmbNewRecordType.SelectedItem?.ToString() ?? "A";

            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName) ||
                string.IsNullOrEmpty(recordName) || string.IsNullOrEmpty(value))
            {
                AppendLog("Заполни зону, scope, имя записи и значение.");
                return;
            }

            var (cmdlet, parameters) = BuildAddRecordCommand(zoneName, scopeName, type, recordName, value,
                Val(txtSrvPriority), Val(txtSrvWeight), Val(txtSrvPort));

            AppendLog($"Добавляю {type}-запись '{recordName}' -> {value} в scope '{scopeName}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke(cmdlet, parameters));
            AppendLog(log);
            FileLogger.LogChange("RECORD ADD", zoneName, $"Scope={scopeName} {type} {recordName} -> {value}", WasSuccess(log), log);
            await RefreshRecordsAsync();
        }

        /// <summary>
        /// Создаёт новую "папку" - поддомен внутри текущего выбранного узла дерева (scope или уже
        /// существующая папка). Технически папки в нашем дереве - чисто визуальная группировка по
        /// именам записей (см. BuildRecordTree), без хотя бы одной записи внутри папка не
        /// существует - поэтому "создание папки" реализуется через wildcard-запись "*" внутри
        /// нового поддомена: она и добавляет реальную DNS-запись (отвечающую на любое имя в этом
        /// поддомене), и заставляет поддомен появиться как папка в дереве.
        /// </summary>
        private async Task CreateSubfolderAsync()
        {
            var selectedTn = treeRecordFolders.SelectedNode;
            if (selectedTn == null)
            {
                AppendLog("Выбери scope или папку в дереве слева, внутри которой создать новую.");
                return;
            }

            // Если выбранный узел - ещё не подгруженный scope, сначала подгружаем его -
            // иначе непонятно, куда именно встраивать новую папку.
            if (selectedTn.Tag is string scopeNameNotLoaded && !_loadedScopeTreeNodes.Contains(selectedTn))
                await LoadScopeIntoTreeAsync(selectedTn, scopeNameNotLoaded);

            if (!(selectedTn.Tag is RecordTreeNode currentNode))
            {
                AppendLog("Не удалось определить текущий узел дерева.");
                return;
            }

            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Не удалось определить зону/scope для новой папки.");
                return;
            }

            var parentSuffix = GetFolderPathSuffix(currentNode);
            var parentHint = string.IsNullOrEmpty(parentSuffix) ? $"корень scope '{scopeName}'" : parentSuffix;

            var (folderName, ip) = CreateSubfolderDialog.Show(parentHint);
            if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(ip)) return; // отмена

            var wildcardName = string.IsNullOrEmpty(parentSuffix) ? $"*.{folderName}" : $"*.{folderName}.{parentSuffix}";

            var (cmdlet, parameters) = BuildAddRecordCommand(zoneName, scopeName, "A", wildcardName, ip, "", "", "");
            AppendLog($"Создаю папку '{folderName}' - добавляю wildcard-запись '{wildcardName}' -> {ip}...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke(cmdlet, parameters));
            AppendLog(log);
            FileLogger.LogChange("RECORD ADD", zoneName,
                $"Scope={scopeName} A {wildcardName} -> {ip} (создание папки '{folderName}')", WasSuccess(log), log);

            await RefreshRecordsAsync();
        }

        /// <summary>
        /// Собирает командлет и параметры для добавления записи любого из 6 типов.
        /// Общий код для обычного добавления (AddRecordToScopeAsync) и для редактирования
        /// (EditSelectedRecordAsync - там запись пересоздаётся с новыми значениями).
        /// Каждый тип записи - отдельный выделенный командлет (Add-DnsServerResourceRecord<Тип>),
        /// а не универсальный Add-DnsServerResourceRecord -A/-AAAA/... - именно выделенные командлеты
        /// надёжно работают со scope (см. историю переписки: универсальный вариант падал на файловых зонах).
        /// </summary>
        private static (string Cmdlet, Dictionary<string, object> Parameters) BuildAddRecordCommand(
            string zoneName, string scopeName, string type, string name, string value,
            string priorityText, string weightText, string portText)
        {
            var parameters = new Dictionary<string, object>
            {
                ["ZoneName"] = zoneName,
                ["ZoneScope"] = scopeName,
                ["Name"] = name
            };

            string cmdlet;
            switch (type)
            {
                case "AAAA":
                    cmdlet = "Add-DnsServerResourceRecordAAAA";
                    parameters["IPv6Address"] = value;
                    break;
                case "CNAME":
                    cmdlet = "Add-DnsServerResourceRecordCName";
                    parameters["HostNameAlias"] = value;
                    break;
                case "PTR":
                    cmdlet = "Add-DnsServerResourceRecordPtr";
                    parameters["PtrDomainName"] = value;
                    break;
                case "NS":
                    // Add-DnsServerResourceRecordNS не существует (проверено отдельно по документации
                    // Microsoft - в отличие от MX ниже, у NS выделенного командлета нет).
                    cmdlet = "Add-DnsServerResourceRecord";
                    parameters["NS"] = true;
                    parameters["NameServer"] = value;
                    break;
                case "MX":
                    // В отличие от NS/TXT/SRV, у MX ЕСТЬ выделенный командлет - проверено отдельно
                    // по документации Microsoft (легко было по инерции отправить его в общий с NS/TXT/SRV).
                    cmdlet = "Add-DnsServerResourceRecordMX";
                    parameters["MailExchange"] = value;
                    parameters["Preference"] = (ushort)ParseIntOrDefault(priorityText, 10);
                    break;
                case "TXT":
                    // Add-DnsServerResourceRecordTxt НЕ СУЩЕСТВУЕТ как отдельный командлет
                    // (проверено по официальной документации Microsoft) - только универсальный
                    // Add-DnsServerResourceRecord с ключом -Txt.
                    cmdlet = "Add-DnsServerResourceRecord";
                    parameters["Txt"] = true;
                    parameters["DescriptiveText"] = value;
                    break;
                case "SRV":
                    // Аналогично TXT - Add-DnsServerResourceRecordSrv тоже не существует.
                    cmdlet = "Add-DnsServerResourceRecord";
                    parameters["Srv"] = true;
                    parameters["DomainName"] = value;
                    parameters["Priority"] = (ushort)ParseIntOrDefault(priorityText, 10);
                    parameters["Weight"] = (ushort)ParseIntOrDefault(weightText, 10);
                    parameters["Port"] = (ushort)ParseIntOrDefault(portText, 443);
                    break;
                default: // "A"
                    cmdlet = "Add-DnsServerResourceRecordA";
                    parameters["IPv4Address"] = value;
                    break;
            }

            return (cmdlet, parameters);
        }

        private static int ParseIntOrDefault(string s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

        /// <summary>
        /// Обходной путь для Secondary-зон, у которых локально (не на мастере) существует
        /// scope как отдельный .dns-файл: обычный API (Add-DnsServerResourceRecord*) отказывает
        /// с WIN32 9611 "Недопустимый тип зоны DNS", потому что зона read-only. Reload зоны
        /// же просто перечитывает файл с диска и эту проверку не делает - поэтому правим файл
        /// напрямую и просим DNS Server перечитать зону.
        ///
        /// ВСЕГДА выполняется ЛОКАЛЬНО (на этой машине), вне зависимости от настройки "Целевой
        /// сервер" сверху - потому что вся суть в том, что физический .dns-файл scope лежит
        /// именно на этом сервере, а не там, куда сейчас может быть направлено удалённое
        /// управление через WinRM.
        /// </summary>
        private async Task AddRecordToScopeFileAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            var recordName = ApplyFolderPrefix(NormalizeRecordName(Val(txtRecordName), zoneName));
            var value = Val(txtRecordValue);
            var type = cmbNewRecordType.SelectedItem?.ToString() ?? "A";

            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName) ||
                string.IsNullOrEmpty(recordName) || string.IsNullOrEmpty(value))
            {
                AppendLog("Заполни зону, scope, имя записи и значение.");
                return;
            }

            var filePath = Path.Combine(@"C:\Windows\System32\dns", zoneName, scopeName + ".dns");

            var confirm = MessageBox.Show(
                "Это обходной путь для Secondary-зон: строка допишется НАПРЯМУЮ в файл" +
                $"{Environment.NewLine}{filePath}{Environment.NewLine}" +
                "на ЭТОЙ машине (локально, независимо от настройки \"Целевой сервер\" сверху), " +
                "после чего зона будет перезагружена командой dnscmd /ZoneReload." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "Это в обход обычных проверок DNS Server API - используй, только если точно " +
                "понимаешь, что делаешь (см. README, раздел про Secondary-зоны)." +
                $"{Environment.NewLine}{Environment.NewLine}Продолжить?",
                "Файловый режим добавления записи", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            if (!File.Exists(filePath))
            {
                AppendLog($"ОШИБКА: файл scope не найден: {filePath} - проверь имя зоны/scope (регистр важен для пути на диске).");
                return;
            }

            string line;
            switch (type)
            {
                case "AAAA":
                    line = $"{recordName}\tIN\tAAAA\t{value}";
                    break;
                case "CNAME":
                    line = $"{recordName}\tIN\tCNAME\t{EnsureTrailingDot(value)}";
                    break;
                case "PTR":
                    line = $"{recordName}\tIN\tPTR\t{EnsureTrailingDot(value)}";
                    break;
                case "NS":
                    line = $"{recordName}\tIN\tNS\t{EnsureTrailingDot(value)}";
                    break;
                case "MX":
                    var preference = ParseIntOrDefault(Val(txtSrvPriority), 10);
                    line = $"{recordName}\tIN\tMX\t{preference} {EnsureTrailingDot(value)}";
                    break;
                case "TXT":
                    line = $"{recordName}\tIN\tTXT\t\"{value}\"";
                    break;
                case "SRV":
                    var priority = ParseIntOrDefault(Val(txtSrvPriority), 10);
                    var weight = ParseIntOrDefault(Val(txtSrvWeight), 10);
                    var port = ParseIntOrDefault(Val(txtSrvPort), 443);
                    line = $"{recordName}\tIN\tSRV\t{priority} {weight} {port} {EnsureTrailingDot(value)}";
                    break;
                default: // "A"
                    line = $"{recordName}\tIN\tA\t{value}";
                    break;
            }

            string backupPath = null;
            try
            {
                // Бэкап файла перед правкой - если после reload что-то пойдёт не так, есть куда откатиться.
                backupPath = filePath + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(filePath, backupPath, overwrite: false);

                // Если файл не заканчивается переводом строки - новая запись прилипнет
                // к хвосту последней существующей строки. Проверяем и при необходимости
                // сначала добавляем перевод строки перед самой записью.
                var existingContent = File.ReadAllText(filePath);
                var needsLeadingNewline = existingContent.Length > 0 &&
                                           !existingContent.EndsWith("\n") && !existingContent.EndsWith("\r");
                var textToAppend = (needsLeadingNewline ? Environment.NewLine : "") + line + Environment.NewLine;

                File.AppendAllText(filePath, textToAppend, Encoding.UTF8);
                AppendLog($"OK: строка добавлена в файл {filePath}{Environment.NewLine}   (бэкап: {backupPath}){Environment.NewLine}   Строка: {line}");

                AppendLog($"Перезагружаю зону '{zoneName}' (dnscmd /ZoneReload)...");
                var reloadResult = RunDnscmdZoneReload(zoneName);
                AppendLog(reloadResult);

                var success = reloadResult.StartsWith("OK");
                FileLogger.LogChange("RECORD ADD (файл)", zoneName,
                    $"Scope={scopeName} {type} {recordName} -> {value} | файл={filePath}", success, success ? null : reloadResult);
            }
            catch (Exception ex)
            {
                AppendLog($"ОШИБКА при правке файла/перезагрузке зоны: {ex.Message}");
                FileLogger.LogChange("RECORD ADD (файл)", zoneName,
                    $"Scope={scopeName} {type} {recordName} -> {value} | файл={filePath}", false, ex.Message);
                return;
            }

            await RefreshRecordsAsync();
        }

        private static string EnsureTrailingDot(string fqdn) =>
            string.IsNullOrEmpty(fqdn) || fqdn.EndsWith(".") ? fqdn : fqdn + ".";

        /// <summary>
        /// dnscmd.exe входит в саму роль DNS Server (не отдельный пакет) - должен быть на любом
        /// сервере, где эта роль установлена. /ZoneReload перечитывает конкретную зону с диска,
        /// не трогая остальные зоны и не требуя перезапуска всей службы DNS.
        /// </summary>
        private static string RunDnscmdZoneReload(string zoneName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dnscmd.exe",
                    Arguments = $"/ZoneReload {zoneName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // dnscmd.exe на русской Windows тоже пишет в CP866 - без этого кракозябры.
                    StandardOutputEncoding = Encoding.GetEncoding(866),
                    StandardErrorEncoding = Encoding.GetEncoding(866)
                };

                using var proc = Process.Start(psi);
                var output = proc.StandardOutput.ReadToEnd().Trim();
                var error = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit(15000);

                if (!string.IsNullOrEmpty(error)) return $"ОШИБКА dnscmd: {error}";
                return $"OK: {(string.IsNullOrEmpty(output) ? "зона перезагружена" : output)}";
            }
            catch (Exception ex)
            {
                return "ОШИБКА: не удалось запустить dnscmd.exe - " + ex.Message +
                       ". Убедись, что команда доступна (входит в роль DNS Server), либо перезагрузи зону вручную через оснастку.";
            }
        }

        private async Task RefreshRecordsAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Укажи зону (поле 'Зона' сверху) и scope (поле 'Записи scope').");
                return;
            }

            // Ищем узел этого scope среди уже показанных в дереве - обновляем именно его ветку,
            // остальные scope'ы (если уже подгружены) не трогаем.
            TreeNode scopeNode = null;
            foreach (TreeNode n in treeRecordFolders.Nodes)
            {
                if (string.Equals(n.Text, scopeName, StringComparison.OrdinalIgnoreCase)) { scopeNode = n; break; }
            }

            if (scopeNode == null)
            {
                // Дерево ещё не строилось для текущей зоны (например, поле scope заполнили
                // руками, не через дерево) - обновляем список scope'ов целиком.
                await RefreshScopesAsync();
                return;
            }

            await LoadScopeIntoTreeAsync(scopeNode, scopeName);
            scopeNode.Expand();
        }

        /// <summary>
        /// Группирует плоский список записей в дерево по составным именам - так же, как это
        /// делает dnsmgmt.msc: "admin.pro32connect" -> папка "pro32connect" содержит запись "admin".
        /// Имя разбивается по точкам и ЧИТАЕТСЯ СПРАВА НАЛЕВО (правый сегмент - самый внешний
        /// уровень, ближе к корню зоны) - "_ldap._tcp.dc._msdcs" даёт путь _msdcs > dc > _tcp > _ldap.
        /// Работает на уже загруженных данных, без обращения к серверу.
        /// </summary>
        private static RecordTreeNode BuildRecordTree(List<PSObject> records)
        {
            var root = new RecordTreeNode { Label = "" };
            foreach (var rec in records)
            {
                var name = rec.Properties["HostName"]?.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(name) || name == "@")
                {
                    root.RecordsHere.Add(rec);
                    continue;
                }

                var segments = name.Split('.');
                Array.Reverse(segments);

                var node = root;
                foreach (var seg in segments)
                {
                    if (!node.Children.TryGetValue(seg, out var child))
                    {
                        child = new RecordTreeNode { Label = seg, Parent = node };
                        node.Children[seg] = child;
                    }
                    node = child;
                }
                node.RecordsHere.Add(rec);
            }
            return root;
        }

        /// <summary>Считает записи во всём поддереве узла (для метки "N записей" у папки в списке).</summary>
        private static int CountRecordsRecursive(RecordTreeNode node)
        {
            var count = node.RecordsHere.Count;
            foreach (var child in node.Children.Values)
                count += CountRecordsRecursive(child);
            return count;
        }

        /// <summary>
        /// Загружает записи ОДНОГО scope и встраивает их деревом папок прямо под его узлом
        /// в общем TreeView (scope'ы зоны - верхний уровень, эта функция строит то, что внутри).
        /// Вызывается лениво - только когда scope реально выбирают/раскрывают, а не для всех разом.
        /// </summary>
        private async Task LoadScopeIntoTreeAsync(TreeNode scopeNode, string scopeName)
        {
            var zoneName = Val(cmbScopeZoneName);
            if (string.IsNullOrEmpty(zoneName)) return;

            txtRecordScopeName.Text = scopeName; // держим в синхроне для Add/Remove записи и удаления scope

            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["ZoneScope"] = scopeName };
            AppendLog($"Загружаю записи scope '{scopeName}'...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerResourceRecord", parameters));
            AppendLog(log);

            _lastScopeRecords = results;
            var root = BuildRecordTree(results);

            scopeNode.Nodes.Clear(); // убираем заглушку "..." (или старое содержимое при повторной загрузке)
            scopeNode.Tag = root;    // теперь сам узел scope играет роль корневой "папки"
            _folderToTreeNode[root] = scopeNode;
            _folderRootToScopeName[root] = scopeName;
            AddChildTreeNodes(scopeNode, root);
            _loadedScopeTreeNodes.Add(scopeNode);

            _currentFolderNode = root;
            treeRecordFolders.SelectedNode = scopeNode;
            UpdateCurrentFolderPathLabel();
            RenderRecordsList();
        }

        private void AddChildTreeNodes(TreeNode parentTn, RecordTreeNode node)
        {
            // Настоящая "папка" - это узел, у которого ЕСТЬ СВОИ дочерние узлы (что-то вложено
            // ещё глубже). Узел без дочерних узлов - это просто обычная запись вроде
            // "admin.pro32connect" (BuildRecordTree создаёт узел для каждого сегмента имени,
            // включая последний), её не нужно показывать в дереве как отдельную "папку" -
            // она и так видна в правом списке обычной строкой записи.
            foreach (var child in node.Children.Values.Where(c => c.Children.Count > 0)
                                                       .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase))
            {
                var childTn = new TreeNode(child.Label) { Tag = child };
                parentTn.Nodes.Add(childTn);
                _folderToTreeNode[child] = childTn;
                AddChildTreeNodes(childTn, child);
            }
        }

        /// <summary>Переключает текущую "папку" (используется и кликом по дереву, и двойным кликом по строке-папке справа).</summary>
        private void NavigateToFolder(RecordTreeNode node)
        {
            if (node == null) return;
            _currentFolderNode = node;
            if (_folderToTreeNode.TryGetValue(node, out var tn))
                treeRecordFolders.SelectedNode = tn; // синхронизируем дерево, если навигация пришла не из него
            UpdateCurrentFolderPathLabel();
            RenderRecordsList();
        }

        /// <summary>
        /// Перестраивает lstRecords из ТЕКУЩЕЙ ПАПКИ (_currentFolderNode) - и её подпапки, и её
        /// собственные записи - с учётом фильтра/сортировки, без обращения к серверу. Параллельно
        /// обновляет _displayedRecords/_displayedFolders - по ним (не по _lastScopeRecords!) идёт
        /// удаление/редактирование по индексу и различение "это запись" / "это папка".
        /// </summary>
        private void RenderRecordsList()
        {
            lstRecords.Items.Clear();
            _displayedRecords.Clear();
            _displayedFolders.Clear();

            if (_currentFolderNode == null) return;

            var filter = (txtRecordFilter.Text ?? "").Trim();

            var rows = new List<(string display, string name, string type, string value, bool isFolder, RecordTreeNode folder, PSObject record)>();

            foreach (var child in _currentFolderNode.Children.Values)
            {
                if (child.Children.Count > 0)
                {
                    // Настоящая папка - есть что-то вложено ещё глубже.
                    var count = CountRecordsRecursive(child);
                    var display = $"📁 {child.Label,-26} ПАПКА  {count} запис.";
                    rows.Add((display, child.Label, "ПАПКА", count.ToString(), true, child, null));
                }
                else
                {
                    // "Лист" без вложенности - это обычная запись (или несколько записей с
                    // одним именем, например round-robin A), а не папка - показываем как есть.
                    foreach (var rec in child.RecordsHere)
                    {
                        var name = rec.Properties["HostName"]?.Value?.ToString() ?? "";
                        var type = rec.Properties["RecordType"]?.Value?.ToString() ?? "";
                        var data = DnsHelper.DescribeRecordData(rec.Properties["RecordData"]?.Value, type);
                        var display = $"{name,-28} {type,-6} {data}";
                        rows.Add((display, name, type, data, false, null, rec));
                    }
                }
            }

            foreach (var rec in _currentFolderNode.RecordsHere)
            {
                var name = rec.Properties["HostName"]?.Value?.ToString() ?? "";
                var type = rec.Properties["RecordType"]?.Value?.ToString() ?? "";
                var data = DnsHelper.DescribeRecordData(rec.Properties["RecordData"]?.Value, type);
                var display = $"{name,-28} {type,-6} {data}";
                rows.Add((display, name, type, data, false, null, rec));
            }

            IEnumerable<(string display, string name, string type, string value, bool isFolder, RecordTreeNode folder, PSObject record)> filtered = rows;
            if (!string.IsNullOrEmpty(filter))
                filtered = rows.Where(r => r.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            r.type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            r.value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

            Func<(string display, string name, string type, string value, bool isFolder, RecordTreeNode folder, PSObject record), string> keySelector = cmbRecordSort.SelectedIndex switch
            {
                1 => r => r.type,
                2 => r => r.value,
                _ => r => r.name
            };

            // Папки всегда сверху, как в проводнике - направление сортировки (▲/▼) влияет
            // только на порядок ВНУТРИ каждой из двух групп, не на то, какая группа выше.
            var grouped = filtered.OrderBy(r => r.isFolder ? 0 : 1);
            var ordered = _recordSortAscending
                ? grouped.ThenBy(keySelector, StringComparer.OrdinalIgnoreCase)
                : grouped.ThenByDescending(keySelector, StringComparer.OrdinalIgnoreCase);

            foreach (var r in ordered.ToList())
            {
                lstRecords.Items.Add(r.display);
                _displayedRecords.Add(r.record);
                _displayedFolders.Add(r.folder);
            }
        }

        private async Task RemoveRecordAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            var index = lstRecords.SelectedIndex;

            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Укажи зону и scope.");
                return;
            }
            if (index < 0 || index >= _displayedRecords.Count)
            {
                AppendLog("Выбери запись в правом списке (и сначала нажми 'Показать записи в scope', если список пуст).");
                return;
            }
            if (_displayedRecords[index] == null)
            {
                AppendLog("Это папка (группировка по имени), а не запись - удалить её напрямую нельзя, зайди внутрь и работай с записями.");
                return;
            }

            // Берём "сырой" объект записи целиком из последнего Get-DnsServerResourceRecord
            // и передаём его в -InputObject - это официальный паттерн удаления конкретной
            // записи (эквивалент "Get-DnsServerResourceRecord ... | Remove-DnsServerResourceRecord").
            // Передавать Name/RRType/RecordData по отдельности ненадёжно: RecordData в объекте
            // записи - это вложенная структура, а не то, что ожидает параметр -RecordData.
            var record = _displayedRecords[index];
            var hostName = record.Properties["HostName"]?.Value?.ToString();
            var recordType = record.Properties["RecordType"]?.Value?.ToString();

            if (MessageBox.Show($"Удалить запись '{hostName}' ({recordType}) из scope '{scopeName}'?", "Подтверждение",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var parameters = new Dictionary<string, object>
            {
                ["ZoneName"] = zoneName,
                ["ZoneScope"] = scopeName,
                ["InputObject"] = record,
                ["Force"] = true
            };

            AppendLog($"Удаляю запись '{hostName}' ({recordType}) из scope '{scopeName}'...");
            var (_, delLog) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerResourceRecord", parameters));
            AppendLog(delLog);
            FileLogger.LogChange("RECORD DELETE", zoneName, $"Scope={scopeName} {recordType} {hostName}", WasSuccess(delLog), delLog);
            await RefreshRecordsAsync();
        }

        /// <summary>
        /// Открывает окно редактирования выбранной записи (двойной клик или пункт контекстного
        /// меню). Модуль DnsServer не даёт надёжной команды "переименовать/изменить запись на
        /// месте" сразу для всех типов, поэтому запись пересоздаётся: сначала добавляется новая
        /// с изменёнными значениями, и только при успехе удаляется старая - если добавление
        /// не удастся, старая запись остаётся на месте и ничего не теряется.
        /// </summary>
        private async Task EditSelectedRecordAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            var index = lstRecords.SelectedIndex;

            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Укажи зону и scope.");
                return;
            }
            if (index < 0 || index >= _displayedRecords.Count)
            {
                AppendLog("Выбери запись в списке для редактирования.");
                return;
            }
            if (_displayedRecords[index] == null)
            {
                NavigateToFolder(_displayedFolders[index]); // это папка - заходим внутрь вместо редактирования
                return;
            }

            var oldRecord = _displayedRecords[index];
            var oldName = oldRecord.Properties["HostName"]?.Value?.ToString() ?? "";
            var oldType = oldRecord.Properties["RecordType"]?.Value?.ToString() ?? "A";
            var oldValue = DnsHelper.DescribeRecordData(oldRecord.Properties["RecordData"]?.Value, oldType);

            string oldPriority = "", oldWeight = "", oldPort = "";
            if (oldType == "SRV")
            {
                var rd = oldRecord.Properties["RecordData"]?.Value;
                if (rd != null)
                {
                    var psObj = PSObject.AsPSObject(rd);
                    oldPriority = psObj.Properties["Priority"]?.Value?.ToString() ?? "";
                    oldWeight = psObj.Properties["Weight"]?.Value?.ToString() ?? "";
                    oldPort = psObj.Properties["Port"]?.Value?.ToString() ?? "";
                }
            }

            var edited = RecordEditDialog.Show(oldType, oldName, oldValue, oldPriority, oldWeight, oldPort);
            if (edited == null) return; // нажали "Отмена"

            edited.Name = NormalizeRecordName(edited.Name, zoneName);

            if (string.IsNullOrEmpty(edited.Name) || string.IsNullOrEmpty(edited.Value))
            {
                AppendLog("Имя и значение не могут быть пустыми - изменения не сохранены.");
                return;
            }

            // Ничего реально не поменялось - не гоняем сервер зря
            if (edited.Type == oldType && edited.Name == oldName && edited.Value == oldValue &&
                edited.Priority == oldPriority && edited.Weight == oldWeight && edited.Port == oldPort)
            {
                AppendLog("Изменений нет - ничего не сохраняю.");
                return;
            }

            var (addCmdlet, addParameters) = BuildAddRecordCommand(zoneName, scopeName, edited.Type, edited.Name, edited.Value,
                edited.Priority, edited.Weight, edited.Port);

            // DNS не даёт CNAME сосуществовать с ЛЮБОЙ другой записью под тем же именем -
            // ни секунды. Если имя не меняется и хотя бы одна из сторон (старая или новая) CNAME,
            // обычный порядок "сначала добавить новую, потом удалить старую" физически не сработает:
            // сервер откажет добавлять новую запись, пока старая ещё существует под этим именем
            // (WIN32 9708 "Узел является записью CNAME DNS" - именно это сейчас и произошло).
            // Меняем порядок на обратный - с автоматическим откатом, если добавление не удастся.
            var nameUnchanged = string.Equals(edited.Name, oldName, StringComparison.OrdinalIgnoreCase);
            var cnameInvolved = oldType == "CNAME" || edited.Type == "CNAME";

            if (nameUnchanged && cnameInvolved)
            {
                await EditRecordRemoveFirstAsync(zoneName, scopeName, oldRecord, oldType, oldName, oldValue,
                    oldPriority, oldWeight, oldPort, edited, addCmdlet, addParameters);
                await RefreshRecordsAsync();
                return;
            }

            AppendLog($"Сохраняю изменения записи '{oldName}' -> '{edited.Name}' ({edited.Type}, {edited.Value})...");
            var (_, addLog) = await Task.Run(() => DnsHelper.Invoke(addCmdlet, addParameters));
            AppendLog(addLog);

            if (!WasSuccess(addLog))
            {
                AppendLog("Не удалось создать новую запись - старая запись оставлена без изменений.");
                FileLogger.LogChange("RECORD EDIT", zoneName,
                    $"Scope={scopeName} {oldType} {oldName} -> {edited.Type} {edited.Name}={edited.Value} (ОТМЕНЕНО: ошибка добавления)", false, addLog);
                return;
            }

            // Новая запись успешно создана - теперь можно безопасно убрать старую
            var removeParameters = new Dictionary<string, object>
            {
                ["ZoneName"] = zoneName,
                ["ZoneScope"] = scopeName,
                ["InputObject"] = oldRecord,
                ["Force"] = true
            };
            var (_, removeLog) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerResourceRecord", removeParameters));
            AppendLog(removeLog);

            var overallSuccess = WasSuccess(removeLog);
            FileLogger.LogChange("RECORD EDIT", zoneName,
                $"Scope={scopeName} {oldType} {oldName}={oldValue} -> {edited.Type} {edited.Name}={edited.Value}", overallSuccess,
                overallSuccess ? null : removeLog);

            if (!overallSuccess)
                AppendLog("Новая запись создана, но старую удалить не удалось - возможен дубликат, проверь список вручную.");

            await RefreshRecordsAsync();
        }

        /// <summary>
        /// Особый порядок для случаев, когда обычный "сначала добавить" не сработает физически
        /// (переход в CNAME или из CNAME под тем же именем - DNS не даёт CNAME сосуществовать
        /// с чем-либо ещё). Сначала удаляем старую запись, потом добавляем новую; если добавление
        /// не удалось - пытаемся автоматически откатить (вернуть старую запись обратно), чтобы
        /// не остаться совсем без записи.
        /// </summary>
        private async Task EditRecordRemoveFirstAsync(string zoneName, string scopeName, PSObject oldRecord,
            string oldType, string oldName, string oldValue, string oldPriority, string oldWeight, string oldPort,
            RecordEditResult edited, string addCmdlet, Dictionary<string, object> addParameters)
        {
            AppendLog($"Тип/имя связаны с CNAME - удаляю старую запись первой (иначе DNS не даст создать новую под тем же именем)...");

            var removeParameters = new Dictionary<string, object>
            {
                ["ZoneName"] = zoneName,
                ["ZoneScope"] = scopeName,
                ["InputObject"] = oldRecord,
                ["Force"] = true
            };
            var (_, removeLog) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerResourceRecord", removeParameters));
            AppendLog(removeLog);

            if (!WasSuccess(removeLog))
            {
                AppendLog("Не удалось удалить старую запись - изменения не сохранены, старая запись осталась как была.");
                FileLogger.LogChange("RECORD EDIT", zoneName,
                    $"Scope={scopeName} {oldType} {oldName} -> {edited.Type} {edited.Name}={edited.Value} (ОТМЕНЕНО: ошибка удаления старой)", false, removeLog);
                return;
            }

            AppendLog($"Старая запись удалена, добавляю новую ({edited.Type}, {edited.Value})...");
            var (_, addLog) = await Task.Run(() => DnsHelper.Invoke(addCmdlet, addParameters));
            AppendLog(addLog);

            if (WasSuccess(addLog))
            {
                FileLogger.LogChange("RECORD EDIT", zoneName,
                    $"Scope={scopeName} {oldType} {oldName}={oldValue} -> {edited.Type} {edited.Name}={edited.Value}", true);
                return;
            }

            // Новая запись не создалась, а старая уже удалена - пробуем откатить (вернуть старую
            // запись назад), чтобы зона не осталась вообще без записи под этим именем.
            AppendLog("ОШИБКА: не удалось создать новую запись. Пробую откатить - вернуть старую запись обратно...");
            var (rollbackCmdlet, rollbackParameters) = BuildAddRecordCommand(zoneName, scopeName, oldType, oldName, oldValue,
                oldPriority, oldWeight, oldPort);
            var (_, rollbackLog) = await Task.Run(() => DnsHelper.Invoke(rollbackCmdlet, rollbackParameters));
            AppendLog(rollbackLog);

            var rolledBack = WasSuccess(rollbackLog);
            AppendLog(rolledBack
                ? "OK: откат успешен, старая запись восстановлена."
                : "ОШИБКА: откат тоже не удался - запись под этим именем сейчас ОТСУТСТВУЕТ, нужно добавить вручную.");

            FileLogger.LogChange("RECORD EDIT", zoneName,
                $"Scope={scopeName} {oldType} {oldName}={oldValue} -> {edited.Type} {edited.Name}={edited.Value} " +
                $"(ОШИБКА добавления, откат {(rolledBack ? "успешен" : "НЕ УДАЛСЯ - записи нет!")})", false, addLog);
        }

        /// <summary>Открывает окно проверки записи (nslookup / Resolve-DnsName) для выбранной строки.</summary>
        private void CheckSelectedRecord()
        {
            var index = lstRecords.SelectedIndex;
            if (index >= 0 && index < _displayedRecords.Count && _displayedRecords[index] == null)
            {
                AppendLog("Это папка (группировка по имени), а не запись - для проверки зайди внутрь и выбери саму запись.");
                return;
            }

            var hostName = (index >= 0 && index < _displayedRecords.Count)
                ? _displayedRecords[index].Properties["HostName"]?.Value?.ToString() ?? ""
                : "";

            // HostName у записи - это только короткое имя ("www", "@" для корня зоны), а не
            // полное доменное имя. Без имени зоны nslookup/Resolve-DnsName либо ошибётся,
            // либо уйдёт резолвить совсем не то через локальный DNS suffix. Достраиваем FQDN.
            var zoneName = Val(cmbScopeZoneName);
            string fqdn;
            if (string.IsNullOrEmpty(zoneName))
                fqdn = hostName; // зона не выбрана - подставить нечего, оставляем как есть
            else if (string.IsNullOrEmpty(hostName) || hostName == "@")
                fqdn = zoneName; // запись в корне зоны - это и есть сама зона
            else if (hostName.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase) || hostName.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                fqdn = hostName; // уже полное имя (бывает для некоторых типов записей) - не дублируем зону
            else
                fqdn = $"{hostName}.{zoneName}";

            RecordCheckDialog.Show(fqdn, DnsHelper.ComputerName);
        }

        // ============================================================
        //  Вкладка "Подсети" (Client Subnets)
        // ============================================================

        private TabPage BuildSubnetsTab()
        {
            lstSubnets = new ListBox();

            var btnRefresh = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список подсетей", _toolTip,
                async (s, e) => await RefreshSubnetsAsync());

            txtSubnetName = Tb(180, "имя подсети");
            txtSubnetCidr = Tb(160, "10.0.1.0/24");
            var btnAdd = IconFactory.CreateButton(IconFactory.Add(), "Добавить подсеть...", _toolTip, async (s, e) =>
            {
                var (name, cidr) = AddSubnetDialog.Show();
                if (name == null) return;
                txtSubnetName.Text = name;
                txtSubnetCidr.Text = cidr;
                await AddSubnetAsync();
            });

            var btnRemove = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранную подсеть", _toolTip,
                async (s, e) => await RemoveSubnetAsync());

            var btnExportSubnets = IconFactory.CreateButton(IconFactory.Export(), "Экспорт в файл...", _toolTip,
                (s, e) => ExportListToFile(lstSubnets.Items.Cast<string>(), $"subnets_{DateTime.Now:yyyyMMdd_HHmmss}.txt"));

            var column = Column(
                Row(btnRefresh, btnAdd, btnRemove, btnExportSubnets)
            );

            return WrapTab("Подсети", column, lstSubnets);
        }

        private async Task RefreshSubnetsAsync()
        {
            AppendLog("Загружаю клиентские подсети...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerClientSubnet"));
            AppendLog(log);
            lstSubnets.Items.Clear();
            foreach (var s in results)
            {
                var name = s.Properties["Name"]?.Value?.ToString() ?? "";
                var cidr = DnsHelper.FlattenPropertyValue(s.Properties["IPv4Subnet"]?.Value);
                if (string.IsNullOrEmpty(cidr)) cidr = DnsHelper.FlattenPropertyValue(s.Properties["IPv6Subnet"]?.Value);
                lstSubnets.Items.Add(string.IsNullOrEmpty(cidr) ? name : $"{name,-25} {cidr}");
            }
        }

        private async Task AddSubnetAsync()
        {
            var name = Val(txtSubnetName);
            var cidr = Val(txtSubnetCidr);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cidr))
            {
                AppendLog("Заполни имя подсети и CIDR (например 10.0.1.0/24).");
                return;
            }

            var parameters = new Dictionary<string, object> { ["Name"] = name, ["IPv4Subnet"] = cidr };
            AppendLog($"Создаю подсеть '{name}' ({cidr})...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Add-DnsServerClientSubnet", parameters));
            AppendLog(log);
            FileLogger.LogChange("SUBNET ADD", name, $"CIDR={cidr}", WasSuccess(log), log);
            await RefreshSubnetsAsync();
        }

        private async Task RemoveSubnetAsync()
        {
            if (lstSubnets.SelectedItem == null) { AppendLog("Выбери подсеть в списке."); return; }
            var name = lstSubnets.SelectedItem.ToString().Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            if (!DangerConfirmDialog.Show(
                    "Удаление подсети",
                    $"   Удалить подсеть \"{name}\"?",
                    "Все политики, ссылающиеся на эту подсеть по имени, перестанут корректно " +
                    "работать (перестанут находить клиентов). Это действие нельзя отменить."))
                return;

            var parameters = new Dictionary<string, object> { ["Name"] = name, ["Force"] = true };
            AppendLog($"Удаляю подсеть '{name}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerClientSubnet", parameters));
            AppendLog(log);
            FileLogger.LogChange("SUBNET DELETE", name, "-", WasSuccess(log), log);
            await RefreshSubnetsAsync();
        }

        // ============================================================
        //  Вкладка "Политики" (Query Resolution Policies)
        // ============================================================

        private TabPage BuildPoliciesTab()
        {
            lstPolicies = new ListBox();
            rtbPolicyDetails = new RichTextBox
            {
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White
            };

            cmbPolicyZoneName = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            var btnLoadPolicyZoneNames = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список зон", _toolTip,
                async (s, e) => await RefreshAllZoneCombosAsync());

            var btnRefresh = IconFactory.CreateButton(IconFactory.Folder(), "Показать политики зоны", _toolTip,
                async (s, e) => await RefreshPoliciesAsync());

            txtPolicyName = Tb(140, "имя политики");
            txtPolicySubnetName = Tb(200, "подсеть(и) через запятую");
            txtPolicyScopeName = Tb(140, "имя scope");
            var btnAdd = IconFactory.CreateButton(IconFactory.Add(), "Создать политику (привязать подсеть к scope)...", _toolTip, async (s, e) =>
            {
                var zoneHint = string.IsNullOrEmpty(Val(cmbPolicyZoneName)) ? "(зона не выбрана)" : Val(cmbPolicyZoneName);
                var (name, subnets, scope) = AddPolicyDialog.Show(zoneHint);
                if (name == null) return;
                txtPolicyName.Text = name;
                txtPolicySubnetName.Text = subnets;
                txtPolicyScopeName.Text = scope;
                await AddPolicyAsync();
            });

            var btnRemove = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранную политику", _toolTip,
                async (s, e) => await RemovePolicyAsync());

            lstPolicies.SelectedIndexChanged += (s, e) => ShowPolicyDetails();

            var column = Column(
                Row(new Label { Text = "Зона:", AutoSize = true, Margin = new Padding(4, 8, 4, 2) }, cmbPolicyZoneName, btnLoadPolicyZoneNames, btnRefresh,
                    new Label { Text = "  ", AutoSize = true }, btnAdd, btnRemove)
            );

            return WrapTabTwoLists("Политики", column,
                "Список политик", lstPolicies,
                "Подробности выбранной политики", rtbPolicyDetails,
                "PoliciesSplitter");
        }

        private async Task RefreshPoliciesAsync()
        {
            var zoneName = Val(cmbPolicyZoneName);
            if (string.IsNullOrEmpty(zoneName)) { AppendLog("Укажи имя зоны."); return; }

            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName };
            AppendLog($"Загружаю политики зоны '{zoneName}'...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerQueryResolutionPolicy", parameters));
            AppendLog(log);

            // Политика хранит только ИМЕНА подсетей (net_100, Old_DNS_redirect13...) -
            // сами по себе они ничего не говорят про реальный диапазон IP. Подтягиваем
            // список подсетей отдельно и резолвим имя -> CIDR, чтобы показать сразу обе вещи.
            var subnetMap = await Task.Run(() => LoadSubnetMap());

            lstPolicies.Items.Clear();
            _lastPolicies.Clear();
            rtbPolicyDetails.Clear();

            foreach (var p in results)
            {
                var name = p.Properties["Name"]?.Value?.ToString() ?? "";
                // Реальные имена свойств у Get-DnsServerQueryResolutionPolicy - "Criteria" (подсеть)
                // и "Content" (scope), а не "ClientSubnet"/"ZoneScope" (это имена параметров у
                // Add-DnsServerQueryResolutionPolicy, но в возвращаемом объекте они называются иначе).
                var subnetRaw = DnsHelper.FlattenPropertyValue(p.Properties["Criteria"]?.Value);
                var subnetDisplay = ResolveSubnetNames(subnetRaw, subnetMap);
                var scope = DnsHelper.FlattenPropertyValue(p.Properties["Content"]?.Value);

                lstPolicies.Items.Add(name);
                _lastPolicies.Add(new PolicyInfo { Name = name, SubnetDisplay = subnetDisplay, Scope = scope });

                if (string.IsNullOrEmpty(subnetRaw) || string.IsNullOrEmpty(scope))
                {
                    // Не удалось достать подсеть/scope обычным способом - выводим ВСЕ реальные
                    // свойства объекта в лог, чтобы увидеть точные имена и поправить код без гаданий.
                    var dump = string.Join("  |  ", p.Properties.Select(pr => $"{pr.Name}={DnsHelper.FlattenPropertyValue(pr.Value)}"));
                    AppendLog($"(диагностика) все поля политики '{name}': {dump}");
                }
            }

            if (lstPolicies.Items.Count > 0) lstPolicies.SelectedIndex = 0;
        }

        /// <summary>Показывает подсети (зелёным) и scope (синим) выбранной политики в правой панели.</summary>
        private void ShowPolicyDetails()
        {
            rtbPolicyDetails.Clear();
            var idx = lstPolicies.SelectedIndex;
            if (idx < 0 || idx >= _lastPolicies.Count) return;
            var info = _lastPolicies[idx];

            void Add(string text, Color color, bool bold = false)
            {
                rtbPolicyDetails.SelectionStart = rtbPolicyDetails.TextLength;
                rtbPolicyDetails.SelectionLength = 0;
                rtbPolicyDetails.SelectionColor = color;
                rtbPolicyDetails.SelectionFont = new Font(rtbPolicyDetails.Font, bold ? FontStyle.Bold : FontStyle.Regular);
                rtbPolicyDetails.AppendText(text);
            }

            Add(info.Name + "\n\n", Color.Black, bold: true);
            Add("Подсети:\n", Color.DimGray);
            Add("  " + (string.IsNullOrEmpty(info.SubnetDisplay) ? "(не задано)" : info.SubnetDisplay) + "\n\n", Color.SeaGreen);
            Add("Scope:\n", Color.DimGray);
            Add("  " + (string.IsNullOrEmpty(info.Scope) ? "(не задано)" : info.Scope), Color.RoyalBlue);
        }

        /// <summary>Имя подсети -> её реальный CIDR (10.0.1.0/24 и т.п.), берётся из Get-DnsServerClientSubnet.</summary>
        private static Dictionary<string, string> LoadSubnetMap()
        {
            var (results, _) = DnsHelper.Invoke("Get-DnsServerClientSubnet");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in results)
            {
                var subnetName = s.Properties["Name"]?.Value?.ToString();
                if (string.IsNullOrEmpty(subnetName)) continue;
                var cidr = DnsHelper.FlattenPropertyValue(s.Properties["IPv4Subnet"]?.Value);
                if (string.IsNullOrEmpty(cidr)) cidr = DnsHelper.FlattenPropertyValue(s.Properties["IPv6Subnet"]?.Value);
                map[subnetName] = cidr ?? "";
            }
            return map;
        }

        /// <summary>
        /// ClientSubnet у политики выглядит как "EQ,net_100,Old_DNS_redirect13" - отсекаем
        /// оператор (EQ/NE) и подставляем реальный CIDR рядом с каждым именем подсети.
        /// </summary>
        private static string ResolveSubnetNames(string rawClientSubnet, Dictionary<string, string> subnetMap)
        {
            if (string.IsNullOrEmpty(rawClientSubnet)) return "";

            var tokens = rawClientSubnet.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0 &&
                            !t.Equals("EQ", StringComparison.OrdinalIgnoreCase) &&
                            !t.Equals("NE", StringComparison.OrdinalIgnoreCase));

            var parts = tokens.Select(n =>
                subnetMap.TryGetValue(n, out var cidr) && !string.IsNullOrEmpty(cidr)
                    ? $"{n} ({cidr})"
                    : n);

            return string.Join(", ", parts);
        }

        private async Task AddPolicyAsync()
        {
            var zoneName = Val(cmbPolicyZoneName);
            var policyName = Val(txtPolicyName);
            var subnetInput = Val(txtPolicySubnetName);
            var scopeName = Val(txtPolicyScopeName);

            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(policyName) ||
                string.IsNullOrEmpty(subnetInput) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Заполни зону, имя политики, подсеть(и) и scope.");
                return;
            }

            // Можно перечислить несколько подсетей через запятую - это "ИЛИ": политика
            // срабатывает, если клиент попадает в любую из них. Пример:
            //   -ClientSubnet "EQ,net_100,Old_DNS_redirect13,Old_DNS_redirect6"
            var subnetNames = subnetInput
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            var clientSubnetValue = "EQ," + string.Join(",", subnetNames);

            // Синтаксис ZoneScope - "<scope>,<вес>". Здесь - базовый вариант "всё в один scope"
            // с весом 1; если нужно раскидывать трафик по нескольким scope - добавь ещё пары
            // через запятую, например "ScopeA,1,ScopeB,1" (50/50).
            var parameters = new Dictionary<string, object>
            {
                ["Name"] = policyName,
                ["Action"] = "ALLOW",
                ["ZoneName"] = zoneName,
                ["ClientSubnet"] = clientSubnetValue,
                ["ZoneScope"] = $"{scopeName},1"
            };

            AppendLog($"Создаю политику '{policyName}': подсети [{string.Join(", ", subnetNames)}] -> scope '{scopeName}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Add-DnsServerQueryResolutionPolicy", parameters));
            AppendLog(log);
            FileLogger.LogChange("POLICY ADD", zoneName,
                $"Policy={policyName} Subnets=[{string.Join(",", subnetNames)}] -> Scope={scopeName}", WasSuccess(log), log);
            await RefreshPoliciesAsync();
        }

        private async Task RemovePolicyAsync()
        {
            var zoneName = Val(cmbPolicyZoneName);
            if (lstPolicies.SelectedItem == null || string.IsNullOrEmpty(zoneName))
            {
                AppendLog("Укажи зону и выбери политику в списке.");
                return;
            }

            var policyName = lstPolicies.SelectedItem.ToString();

            if (!DangerConfirmDialog.Show(
                    "Удаление политики",
                    $"   Удалить политику \"{policyName}\" из зоны \"{zoneName}\"?",
                    "Клиенты, которых обслуживала эта политика, начнут получать ответы по " +
                    "обычной (не переопределённой) логике зоны. Это действие нельзя отменить."))
                return;

            var parameters = new Dictionary<string, object> { ["Name"] = policyName, ["ZoneName"] = zoneName, ["Force"] = true };
            AppendLog($"Удаляю политику '{policyName}'...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerQueryResolutionPolicy", parameters));
            AppendLog(log);
            FileLogger.LogChange("POLICY DELETE", zoneName, $"Policy={policyName}", WasSuccess(log), log);
            await RefreshPoliciesAsync();
        }

        // ============================================================
        //  Общие хелперы
        // ============================================================

        private void OpenChangeLog()
        {
            try
            {
                var dir = Path.GetDirectoryName(FileLogger.CurrentLogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(FileLogger.CurrentLogPath))
                    File.WriteAllText(FileLogger.CurrentLogPath, "");

                Process.Start(new ProcessStartInfo(FileLogger.CurrentLogPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("ОШИБКА: не удалось открыть файл лога - " + ex.Message);
            }
        }

        /// <summary>
        /// DnsHelper.Invoke возвращает текстовый лог вида "OK: ..." или "ОШИБКА: ..." /
        /// "ИСКЛЮЧЕНИЕ ...". Этот хелпер определяет по нему успех операции, чтобы
        /// одинаково решать, как писать в файл лога изменений.
        /// </summary>
        private static bool WasSuccess(string log) =>
            !string.IsNullOrEmpty(log) &&
            !log.Contains("ОШИБКА:") &&
            !log.Contains("ИСКЛЮЧЕНИЕ");

        /// <summary>
        /// Экспортирует список строк (то, что сейчас отображено в списке - с учётом
        /// применённых фильтра и сортировки) в текстовый файл. Путь выбирается диалогом
        /// сохранения - явно, как и просили, а не в жёстко зашитое место.
        /// </summary>
        private void ExportListToFile(IEnumerable<string> lines, string suggestedFileName)
        {
            var linesList = lines.ToList();
            if (linesList.Count == 0)
            {
                AppendLog("Список пуст - нечего экспортировать.");
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
                FileName = suggestedFileName,
                Title = "Сохранить список в файл"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                File.WriteAllLines(dlg.FileName, linesList);
                AppendLog($"OK: список ({linesList.Count} строк) сохранён в файл: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                AppendLog($"ОШИБКА: не удалось сохранить файл - {ex.Message}");
            }
        }

        private bool _adminPromptShown; // не спамим диалогом при каждой повторно провалившейся локальной операции

        private void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Лог может содержать сразу несколько строк (например вывод FormatObjects
            // или несколько ОШИБКА:/OK: подряд) - красим каждую отдельно по её содержимому.
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var rawLine in lines)
            {
                if (rawLine.Length == 0) continue;
                AppendColoredLine($"[{DateTime.Now:HH:mm:ss}] {rawLine}", ColorForLine(rawLine));

                // Централизованная точка: DnsHelper сам решает, что причина ошибки - именно
                // нехватка прав администратора для локальной работы (не любая другая ошибка),
                // и просто кладёт понятную фразу в лог. Тут её перехватываем и предлагаем
                // реальное действие - перезапуск с UAC - а не просто показываем текст.
                if (!_adminPromptShown && rawLine.Contains("нужны права администратора для локальной работы"))
                {
                    _adminPromptShown = true;
                    OfferAdminRelaunch();
                }
            }
        }

        /// <summary>
        /// Предлагает перезапустить приложение с правами администратора (UAC) - вызывается,
        /// когда локальная операция реально упёрлась в нехватку прав. Манифест теперь asInvoker
        /// (см. app.manifest), элевация запрашивается точечно, а не всегда при запуске -
        /// удалённый режим (управление другим сервером) прав администратора вообще не требует.
        /// </summary>
        private void OfferAdminRelaunch()
        {
            var result = MessageBox.Show(
                "Для локальной работы с DNS Server на этой машине нужны права администратора.\n\n" +
                "Перезапустить приложение с запросом повышенных прав?",
                "Требуются права администратора",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (result != DialogResult.OK) return; // отказался - остаёмся как есть, без прав

            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas" // запрашивает UAC у самой ОС - не наш код решает, показывать ли запрос
                };
                Process.Start(psi);
                Application.Exit(); // новый (повышенный) процесс уже стартует - этот больше не нужен
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Пользователь нажал "Нет" в самом системном UAC-диалоге (не в нашем окне выше) -
                // это отдельный, более поздний отказ. Просто остаёмся работать без прав, ничего
                // не ломаем и не показываем повторную ошибку - человек уже увидел UAC и отказался.
            }
        }

        private static Color ColorForLine(string line)
        {
            if (line.StartsWith("ОШИБКА:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ИСКЛЮЧЕНИЕ", StringComparison.OrdinalIgnoreCase))
                return Color.Firebrick;

            if (line.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
                return Color.SeaGreen;

            // Служебные сообщения о ходе выполнения ("Загружаю...", "Создаю...", "Удаляю...")
            return Color.DimGray;
        }

        private void AppendColoredLine(string line, Color color)
        {
            txtOutput.SelectionStart = txtOutput.TextLength;
            txtOutput.SelectionLength = 0;
            txtOutput.SelectionColor = color;
            txtOutput.AppendText(line + Environment.NewLine);
            txtOutput.SelectionColor = txtOutput.ForeColor; // сброс, чтобы курсор/следующий текст не наследовал цвет
            txtOutput.ScrollToCaret();
        }
    }
}
