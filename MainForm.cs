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

        // ---- управление зонами (теперь часть вкладки "Scopes и записи", отдельной вкладки "Зоны" больше нет) ----
        private TextBox txtNewZoneName;
        private ComboBox cmbZoneType;
        private RichTextBox lblZoneSource;

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

        // ---- дерево записей (верхний уровень - серверы: локальный + любой, к которому успешно
        //      подключались; внутри каждого сервера - зоны; внутри зоны - scope'ы; внутри scope -
        //      папки по точкам в имени, как в dnsmgmt.msc. Каждый уровень подгружается лениво,
        //      при первом выборе/раскрытии - ничего не тянется впустую) ----
        private TreeView treeRecordFolders;
        private RecordTreeNode _currentFolderNode; // какая "папка" сейчас показана в правом списке
        private Label lblCurrentFolderPath; // "Добавление в: ..." - видимая подсказка, куда попадёт новая запись
        private Dictionary<RecordTreeNode, TreeNode> _folderToTreeNode = new Dictionary<RecordTreeNode, TreeNode>();
        private Dictionary<RecordTreeNode, string> _folderRootToScopeName = new Dictionary<RecordTreeNode, string>(); // корень поддерева -> имя scope, которому он принадлежит
        private HashSet<TreeNode> _loadedScopeTreeNodes = new HashSet<TreeNode>(); // какие узлы scope уже реально подгружены (не просто заглушка)
        private HashSet<TreeNode> _loadedZoneTreeNodes = new HashSet<TreeNode>();  // то же самое для узлов зоны (подгружены её scope'ы или ещё нет)
        private HashSet<TreeNode> _loadedServerTreeNodes = new HashSet<TreeNode>(); // то же самое для узлов сервера (подгружены его зоны или ещё нет)

        /// <summary>Маркер узла-сервера в дереве (верхний уровень). Пустая ServerName = локальный компьютер.</summary>
        private class ServerNodeMarker { public string ServerName; }

        /// <summary>Маркер узла-зоны в дереве (второй уровень, внутри узла сервера). ScopesUnavailable = зона условной пересылки/stub: Zone Scopes она не поддерживает (WIN32 9603), это лист без догрузки.</summary>
        private class ZoneNodeMarker { public string ServerName; public string ZoneName; public bool ScopesUnavailable; }

        /// <summary>Верхние контейнеры зон в дереве, как в dnsmgmt.msc: прямого/обратного просмотра, зоны-заглушки (Stub) и серверы условной пересылки (Forwarder).</summary>
        private enum ZoneCategoryKind { Forward, Reverse, Stub, Forwarder }

        /// <summary>Маркер узла-категории ("Зоны прямого/обратного просмотра" / "Зоны-заглушки" / "Серверы условной пересылки") - чисто визуальная группировка, без обращения к серверу.</summary>
        private class ZoneCategoryMarker { public string ServerName; public ZoneCategoryKind Kind; }

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
        private ComboBox cmbPolicyServer;   // на каком сервере смотрим/создаём политики (локальный + успешно подключённые удалённые)
        private ComboBox cmbPolicyZoneName; // выпадающий список зон, как на вкладке Scopes
        private ListBox lstPolicies;
        private RichTextBox rtbPolicyDetails; // подробности выбранной политики (подсети/scope), чтобы не уезжало за экран одной строкой
        private TextBox txtPolicyName;
        private TextBox txtPolicySubnetName;
        private TextBox txtPolicyScopeName;
        private List<PolicyInfo> _lastPolicies = new List<PolicyInfo>(); // 1:1 с элементами lstPolicies

        // Удалённые серверы, к которым в ТЕКУЩЕЙ сессии приложения было успешное подключение
        // (успешно загрузились зоны либо прошла явная авторизация). Источник для выпадашки
        // "Сервер" на вкладке "Политики".
        private readonly HashSet<string> _connectedRemoteServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private class PolicyInfo
        {
            public string Name;
            public string SubnetDisplay; // "net_100 (10.0.100.0/24), Old_DNS_redirect13 (...)"
            public string Scope;
        }

        /// <summary>Элемент выпадашки "Сервер" на вкладке "Политики": Server = "" означает локальный компьютер.</summary>
        private sealed class PolicyServerItem
        {
            public readonly string Server;
            private readonly string _label;
            public PolicyServerItem(string server, string label) { Server = server; _label = label; }
            public override string ToString() => _label;
        }

        // Пока false - обработчики UI, которые дёргают сервер (например смена сервера на вкладке
        // "Политики"), не должны срабатывать: часть контролов ещё строится, txtOutput может не
        // существовать. Выставляется в Shown, после первичной инициализации.
        private bool _uiReady;

        // true, пока RefreshPolicyServerCombo() программно пересобирает выпадашку "Сервер" на
        // вкладке "Политики". Нужно, чтобы её SelectedIndexChanged НЕ трогал глобальный
        // DnsHelper.ComputerName во время пересборки (иначе перезатирал контекст, выбранный
        // деревом/верхней панелью - именно из-за этого "авторизация ОК, а список по кругу").
        private bool _rebuildingPolicyServerCombo;

        // Разовая (за запуск) фоновая проверка новой версии - чтобы не дёргать GitHub повторно,
        // если Shown сработает ещё раз (форма прячется/показывается).
        private bool _startupUpdateCheckDone;

        public MainForm()
        {
            InitializeComponent();
            Shown += async (s, e) =>
            {
                // Вкладка "Scopes и записи" (со встроенным теперь управлением зонами) открыта
                // по умолчанию при старте - SelectedIndexChanged для неё не сработает
                // (переключения не было), поэтому инициализируем дерево явно здесь же.
                InitializeServerTree();
                await RefreshAllZoneCombosAsync(); // держим внутренний список зон наполненным - используется в подсказках диалогов создания
                _uiReady = true;
                RefreshPolicyServerCombo();

                // Не ждём: проверка идёт в фоне, старт и работа с формой не блокируются.
                _ = CheckForUpdatesInBackgroundAsync();
            };
            FormClosing += (s, e) => DnsHelper.DisposeAllCimSessions();
        }

        /// <summary>
        /// Разовая фоновая проверка новой версии при запуске: тихо спрашивает GitHub и, только
        /// если релиз реально новее текущей версии, предлагает обновиться (тот же путь, что и
        /// кнопка "Проверить обновления" в окне "О программе"). Ошибки (нет доступа в интернет -
        /// обычное дело для DNS-серверов в закрытом сегменте сети) наружу не показываются:
        /// фоновая проверка не должна мешать работе.
        /// </summary>
        private async Task CheckForUpdatesInBackgroundAsync()
        {
            if (_startupUpdateCheckDone) return;
            _startupUpdateCheckDone = true;

            try
            {
                var (success, _, info) = await UpdateChecker.CheckLatestAsync();
                if (!success || info == null) return;
                if (!UpdateChecker.IsNewer(info.Version, AppVersion.Current)) return;
                if (IsDisposed || Disposing) return;

                var confirm = MessageBox.Show(this,
                    $"Доступна новая версия: v{info.Version} (у тебя v{AppVersion.Current}).\n\n" +
                    "Скачать и установить сейчас? Приложение закроется и перезапустится само.\n" +
                    "changes.log, settings.ini и .dns-файлы зон не трогаются.",
                    "Доступно обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (confirm != DialogResult.Yes) return;

                var updaterScript = await UpdateChecker.DownloadAndPrepareUpdateAsync(info.DownloadUrl);
                FileLogger.LogChange("UPDATE", "GitHub", $"Скачано обновление до v{info.Version}, перезапуск...", true);
                UpdateChecker.LaunchUpdaterAndExit(updaterScript);
            }
            catch (Exception ex)
            {
                // Уже после согласия пользователя что-то сорвалось при скачивании/подготовке -
                // пишем в лог, но без модалки поверх всего (это всё-таки фоновая проверка).
                FileLogger.LogChange("UPDATE", "GitHub", "Фоновая проверка обновления при запуске", false, ex.Message);
            }
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

            // Отдельной вкладки "Зоны" больше нет - управление зонами (создать/удалить/
            // перезагрузить) переехало в верхний блок вкладки "Scopes и записи", а сам список
            // зон теперь один из уровней общего дерева (Сервер -> прямые/обратные -> Зона -> Scope).
            tabs.TabPages.Add(BuildScopesTab());
            tabs.TabPages.Add(BuildSubnetsTab());
            tabs.TabPages.Add(BuildPoliciesTab());

            // Автоподгрузка при переходе на вкладку - только если там ещё пусто (первый заход).
            // Если человек уже сам нажимал "Обновить"/"↻" - повторно не дёргаем сервер на каждый клик по вкладке.
            tabs.SelectedIndexChanged += async (s, e) =>
            {
                switch (tabs.SelectedIndex)
                {
                    case 0 when treeRecordFolders.Nodes.Count == 0:
                        InitializeServerTree();
                        await RefreshAllZoneCombosAsync();
                        break;
                    case 2:
                        RefreshPolicyServerCombo(); // вдруг с прошлого захода подключились новые серверы
                        if (cmbPolicyZoneName.Items.Count == 0)
                            await RefreshPolicyZoneComboAsync();
                        break;
                }
            };

            var targetServerPanel = BuildTargetServerPanel();

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
        }

        /// <summary>
        /// Панель сверху окна: куда физически идут все команды. Пусто = локальный компьютер
        /// (как было раньше). Если указать имя сервера - ВСЕ операции на всех вкладках начинают
        /// выполняться на нём через -ComputerName (WinRM), без переезда приложения на другой сервер.
        /// </summary>
        private Control BuildTargetServerPanel()
        {
            // Высота увеличена (была 40) - специально под баннер справа, чтобы он занимал
            // читаемый размер на всю высоту этого блока, а не был ужат до пары строчек.
            const int panelHeight = 56;
            var panel = new Panel { Dock = DockStyle.Top, Height = panelHeight, BackColor = Color.FromArgb(245, 247, 249) };

            // Баннер - на всю высоту панели (без отступов сверху/снизу от panel.Padding, у самой
            // panel его нет специально - иначе баннер ужался бы теми же отступами, что и строка
            // элементов слева). Добавляем ПЕРВЫМ - Dock=Right должен зарезервировать место
            // раньше, чем rowContainer (Dock=Fill) займёт всё оставшееся.
            var bannerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "banner.png");
            if (File.Exists(bannerPath))
            {
                try
                {
                    using var original = Image.FromFile(bannerPath);
                    var displayHeight = panelHeight - 8; // лёгкий отступ, не совсем впритык к краям
                    var displayWidth = (int)(original.Width * (displayHeight / (float)original.Height));
                    var bannerPic = new PictureBox
                    {
                        Image = new Bitmap(original, new Size(displayWidth, displayHeight)),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Dock = DockStyle.Right,
                        Width = displayWidth + 16, // запас по горизонтали, чтобы баннер не липнул к правому краю окна
                        Padding = new Padding(0, 4, 8, 4),
                        Cursor = Cursors.Hand
                    };
                    bannerPic.Click += (s, e) => AboutDialog.Show();
                    _toolTip.SetToolTip(bannerPic, "О программе");
                    panel.Controls.Add(bannerPic);
                }
                catch { /* повреждённый файл баннера - не критично, просто пропускаем */ }
            }

            // Строка с элементами управления сервером - в оставшемся месте слева, вертикально
            // центрируется за счёт padding именно ЭТОГО вложенного контейнера (не общего
            // panel.Padding - тот, если бы был, ужал бы по высоте и баннер справа тоже).
            var rowContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 14, 6, 14) };
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            rowContainer.Controls.Add(row);
            panel.Controls.Add(rowContainer);

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

            return panel;
        }

        private void UpdateTargetComputerName()
        {
            // Раньше здесь была инвалидация закешированной сессии при смене сервера - но раз
            // теперь сессии кешируются ПО СЕРВЕРАМ (см. DnsHelper._cimSessions), а не одна на
            // всё приложение, переключение целевого сервера больше не должно их разрушать -
            // подключение к каждому серверу живёт весь срок работы приложения независимо.
            DnsHelper.ComputerName = chkLocalServer.Checked ? "" : cmbTargetServer.Text.Trim();
        }

        private async Task TestTargetServerConnectionAsync()
        {
            var target = chkLocalServer.Checked ? "" : cmbTargetServer.Text.Trim();
            UpdateTargetComputerName(); // жёстко синхронизируем контекст с верхней панелью - вкладка "Политики" могла его перевести на другой сервер
            AppendLog(chkLocalServer.Checked
                ? "Проверяю подключение к локальному DNS-серверу..."
                : $"Проверяю подключение к '{target}'...");

            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(log);

            if (WasSuccess(log))
            {
                AppendLog($"OK: подключение работает, зон видно: {results.Count}");
                AddServerRootIfMissing(""); // на случай, если это первое обращение к дереву вообще - гарантируем, что "Локальный" тоже на месте
                AddServerRootIfMissing(target); // появляется в дереве слева на вкладке "Scopes и записи", тем же принципом, что и локальный
                MarkRemoteConnected(target);
                return;
            }

            if (chkLocalServer.Checked || string.IsNullOrEmpty(target)) return; // локально тут диагностировать нечего

            // Ошибка похожа на проблему транспорта (WinRM не запущен / сеть / TrustedHosts), а не
            // на нехватку прав? Тогда сначала чиним транспорт - иначе и окно ввода логина упрётся
            // ровно в то же самое. Все проверки RemoteConnectDiagnostics делает в фоне.
            if (LooksLikeTransportProblem(log))
            {
                var changed = await RemoteConnectDiagnostics.RunAsync(this, target, AppendLog);
                if (changed)
                {
                    AppendLog("Повторно проверяю подключение после изменений...");
                    var (r2, l2) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
                    AppendLog(l2);
                    if (WasSuccess(l2))
                    {
                        AppendLog($"OK: подключение работает, зон видно: {r2.Count}");
                        AddServerRootIfMissing("");
                        AddServerRootIfMissing(target);
                        MarkRemoteConnected(target);
                        return;
                    }
                }
            }

            // Обычная проверка не удалась - для удалённого сервера предлагаем ввести другие
            // учётные данные (текущая Windows-учётка может просто не иметь прав на этом сервере).
            AppendLog("Подключение не удалось текущей учётной записью - предлагаю ввести другие данные...");
            var authOk = ServerAuthDialog.Show(target);
            if (!authOk)
            {
                // Логин упал на том же транспорте - ещё раз предложим починить его и повторить вход.
                if (LooksLikeTransportProblem(ServerAuthDialog.LastError) &&
                    await RemoteConnectDiagnostics.RunAsync(this, target, AppendLog))
                {
                    authOk = ServerAuthDialog.Show(target);
                }
                if (!authOk)
                {
                    AppendLog("Аутентификация отменена или не удалась - работаем без доступа к этому серверу.");
                    return;
                }
            }

            AppendLog("Повторно проверяю подключение с новыми учётными данными...");
            var (retryResults, retryLog) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(retryLog);
            if (WasSuccess(retryLog))
            {
                AppendLog($"OK: подключение работает, зон видно: {retryResults.Count}");
                AddServerRootIfMissing(""); // та же подстраховка - гарантируем "Локальный" на месте
                AddServerRootIfMissing(target); // тот же принцип - сервер появляется в дереве после успешной авторизации
                MarkRemoteConnected(target);
            }
        }

        /// <summary>Запоминает удалённый сервер как успешно подключённый в этой сессии и обновляет выпадашку "Сервер" на вкладке "Политики".</summary>
        private void MarkRemoteConnected(string server)
        {
            if (!string.IsNullOrWhiteSpace(server) && _connectedRemoteServers.Add(server.Trim()))
                RefreshPolicyServerCombo();
        }

        /// <summary>
        /// Похоже ли сообщение об ошибке на проблему транспорта (WinRM не запущен, узел
        /// недоступен, нужен TrustedHosts), а не на отказ по правам/паролю. По этому признаку
        /// решаем, звать ли RemoteConnectDiagnostics до окна ввода логина.
        /// </summary>
        private static bool LooksLikeTransportProblem(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var marker in new[]
            {
                "WinRM", "TrustedHosts", "WS-Management", "Test-WSMan",
                "не удается обработать запрос", "cannot process the request",
                "RPC", "1722", "не прослушивает", "not listening",
                "не удалось подключиться к", "cannot connect to", "actively refused", "недоступен"
            })
            {
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private Control BuildOutputPanel()
        {
            const int expandedHeight = 220;
            const int collapsedHeight = 34; // только строка с кнопками, без самого текста

            var panel = new Panel { Dock = DockStyle.Bottom, Height = expandedHeight, Padding = new Padding(6) };

            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            header.Controls.Add(new Label { Text = "Вывод:", AutoSize = true, Margin = new Padding(0, 6, 8, 0), Font = new Font(Font, FontStyle.Bold) });

            var btnToggle = new Button { Text = "▼ Свернуть", AutoSize = true };
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
                btnToggle.Text = collapsed ? "▲ Показать" : "▼ Свернуть";
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
            "MX" => "почтовый сервер (FQDN), напр. mail.example.com - приоритет в поле Priority/Preference",
            "TXT" => "текст записи, напр. v=spf1 include:_spf.example.com ~all",
            "SRV" => "целевой хост (Target), напр. sipserver.example.com",
            _ => "IPv4, напр. 10.0.1.10"
        };

        // ============================================================
        //  Управление зонами (теперь часть вкладки "Scopes и записи")
        // ============================================================

        /// <summary>Идёт вверх от текущего выбранного узла дерева, пока не найдёт узел-зону (или её потомка).</summary>
        private (string ServerName, string ZoneName) GetSelectedZoneContext()
        {
            var node = treeRecordFolders.SelectedNode;
            while (node != null)
            {
                if (node.Tag is ZoneNodeMarker zm) return (zm.ServerName, zm.ZoneName);
                node = node.Parent;
            }
            return (null, null);
        }

        /// <summary>Добавляет фрагмент текста в конец RichTextBox с нужным начертанием - для составных строк вроде "Источник зоны", где разные слова должны выглядеть по-разному.</summary>
        private static void AppendStyled(RichTextBox rtb, string text, bool bold = false, bool underline = false)
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            var style = FontStyle.Regular;
            if (bold) style |= FontStyle.Bold;
            if (underline) style |= FontStyle.Underline;
            rtb.SelectionFont = new Font(rtb.Font, style);
            rtb.AppendText(text);
        }

        /// <summary>Показывает источник выбранной зоны (AD/файл для Primary, мастер-серверы для Secondary/Stub) - вызывается при выборе узла-зоны в дереве.</summary>
        private async Task ShowSelectedZoneSourceAsync(string serverName, string zoneName)
        {
            if (lblZoneSource == null) return;

            var previousComputerName = DnsHelper.ComputerName;
            DnsHelper.ComputerName = serverName ?? ""; // временно - именно для ЭТОГО запроса, чей бы узел ни был выбран

            var (results, _) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone", new Dictionary<string, object> { ["Name"] = zoneName }));

            DnsHelper.ComputerName = previousComputerName; // возвращаем как было - сам факт запроса источника не должен молча менять текущий сервер

            lblZoneSource.Clear();
            var z = results.FirstOrDefault();
            if (z == null)
            {
                AppendStyled(lblZoneSource, "Источник", underline: true);
                lblZoneSource.AppendText(": -");
                return;
            }

            var zoneType = z.Properties["ZoneType"]?.Value?.ToString() ?? "?";

            if (zoneType == "Primary")
            {
                var isDsIntegrated = z.Properties["IsDsIntegrated"]?.Value;
                AppendStyled(lblZoneSource, "Источник", underline: true);
                lblZoneSource.AppendText(": ");
                AppendStyled(lblZoneSource, "Primary", bold: true);
                if (isDsIntegrated is bool b && b)
                {
                    lblZoneSource.AppendText(", хранится в Active Directory (реплицируется между DC домена).");
                }
                else
                {
                    lblZoneSource.AppendText(", файловая зона - ");
                    AppendStyled(lblZoneSource, z.Properties["ZoneFile"]?.Value?.ToString() ?? "", bold: true);
                }
            }
            else
            {
                var masters = DnsHelper.FlattenPropertyValue(z.Properties["MasterServers"]?.Value);
                AppendStyled(lblZoneSource, "Источник", underline: true);
                lblZoneSource.AppendText(": ");
                AppendStyled(lblZoneSource, zoneType, bold: true);
                lblZoneSource.AppendText(" (только чтение здесь)");
                if (string.IsNullOrEmpty(masters))
                {
                    lblZoneSource.AppendText(", ");
                    AppendStyled(lblZoneSource, "мастер-серверы", underline: true);
                    lblZoneSource.AppendText(" не указаны.");
                }
                else
                {
                    lblZoneSource.AppendText(" - ");
                    AppendStyled(lblZoneSource, "мастер-серверы", underline: true);
                    lblZoneSource.AppendText(": ");
                    AppendStyled(lblZoneSource, masters, bold: true);
                }
            }
        }

        /// <summary>Экспортирует имена зон ТЕКУЩЕГО (уже развёрнутого) сервера в файл - обе категории, прямые и обратные вместе.</summary>
        private void ExportCurrentServerZones()
        {
            var serverName = DnsHelper.ComputerName;
            var serverNode = AddServerRootIfMissing(serverName); // вернёт уже существующий узел, если он есть
            if (!_loadedServerTreeNodes.Contains(serverNode))
            {
                AppendLog("Сначала разверни сервер в дереве слева, чтобы загрузить список его зон.");
                return;
            }

            var lines = new List<string>();
            foreach (TreeNode categoryNode in serverNode.Nodes)
            {
                // Зоны-заглушки и серверы условной пересылки не выгружаем - импорт создаёт зоны как Primary.
                if (categoryNode.Tag is ZoneCategoryMarker cm &&
                    (cm.Kind == ZoneCategoryKind.Stub || cm.Kind == ZoneCategoryKind.Forwarder))
                    continue;
                foreach (TreeNode zoneNode in categoryNode.Nodes)
                    if (zoneNode.Tag is ZoneNodeMarker zm)
                        lines.Add(zm.ZoneName);
            }

            ExportListToFile(lines, $"zones_{SanitizeForFileName(CurrentServerLabel())}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                $"Экспортировано {DateTime.Now:yyyy-MM-dd HH:mm:ss} с сервера: {CurrentServerLabel()}");
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

            // Обновляем список зон именно ТЕКУЩЕГО сервера (того, что сейчас в панели подключения) - не всё дерево.
            var serverName = DnsHelper.ComputerName;
            var serverNode = AddServerRootIfMissing(serverName);
            await LoadServerZonesIntoTreeAsync(serverNode, serverName);
            serverNode.Expand();
        }

        private async Task RemoveZoneAsync()
        {
            var (serverName, zoneName) = GetSelectedZoneContext();
            if (zoneName == null)
            {
                AppendLog("Выбери зону в дереве слева.");
                return;
            }

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

            var serverNode = AddServerRootIfMissing(serverName);
            await LoadServerZonesIntoTreeAsync(serverNode, serverName);
            serverNode.Expand();
        }

        /// <summary>
        /// Перезагружает выбранную зону с диска (dnscmd /ZoneReload) - тот же механизм, что уже
        /// используется в файловом режиме для Secondary-зон, но теперь доступен напрямую для
        /// любой зоны: полезно, если запись поправили в обход приложения (руками в файле) и
        /// нужно, чтобы DNS Server перечитал её без перезапуска всей службы.
        /// </summary>
        private async Task ReloadSelectedZoneAsync()
        {
            var (_, zoneName) = GetSelectedZoneContext();
            if (zoneName == null)
            {
                AppendLog("Выбери зону в дереве слева.");
                return;
            }

            AppendLog($"Перезагружаю зону '{zoneName}' (dnscmd /ZoneReload)...");
            var result = await Task.Run(() => RunDnscmdZoneReload(zoneName));
            AppendLog(result);

            var success = result.StartsWith("OK");
            FileLogger.LogChange("ZONE RELOAD", zoneName, "dnscmd /ZoneReload", success, success ? null : result);
            // Список зон не меняется от перезагрузки содержимого - обновлять дерево не нужно.
        }

        // ============================================================
        //  Вкладка "Scopes и записи"
        // ============================================================

        private TabPage BuildScopesTab()
        {
            lstRecords = new ListBox { SelectionMode = SelectionMode.MultiExtended };

            // Дерево записей: верхний уровень - scope'ы зоны, внутри каждого - папки записей
            // (группировка по составным именам, как в dnsmgmt.msc). Scope подгружается ЛЕНИВО -
            // при первом выборе/раскрытии его узла, а не все разом (некоторые scope содержат
            // сотни записей - незачем тянуть их все, если человек смотрит только один).
            treeRecordFolders = new TreeView { HideSelection = false, DrawMode = TreeViewDrawMode.OwnerDrawText };
            treeRecordFolders.DrawNode += TreeServerNode_DrawNode;
            treeRecordFolders.AfterSelect += async (s, e) =>
            {
                var node = e.Node;
                if (node?.Tag is RecordTreeNode rtn)
                {
                    // Восстанавливаем целевой сервер + зону по положению узла в дереве - иначе
                    // при нескольких подключённых серверах правка записи ушла бы на тот сервер,
                    // чью зону/scope выбирали последним, а не на владельца этой папки.
                    SyncContextToTreeNode(node);
                    _currentFolderNode = rtn;
                    var root = rtn;
                    while (root.Parent != null) root = root.Parent;
                    if (_folderRootToScopeName.TryGetValue(root, out var ownerScope))
                        txtRecordScopeName.Text = ownerScope;
                    UpdateCurrentFolderPathLabel();
                    RenderRecordsList();
                }
                else if (node?.Tag is string scopeNameUnloaded && !_loadedScopeTreeNodes.Contains(node))
                {
                    SyncContextToTreeNode(node);
                    await LoadScopeIntoTreeAsync(node, scopeNameUnloaded);
                    node.Expand();
                }
                else if (node?.Tag is ZoneNodeMarker zoneMarker)
                {
                    SetCurrentServerContext(zoneMarker.ServerName);
                    cmbScopeZoneName.Text = zoneMarker.ZoneName;
                    await ShowSelectedZoneSourceAsync(zoneMarker.ServerName, zoneMarker.ZoneName);
                    if (!zoneMarker.ScopesUnavailable && !_loadedZoneTreeNodes.Contains(node))
                    {
                        await LoadZoneScopesIntoTreeAsync(node, zoneMarker.ServerName, zoneMarker.ZoneName);
                        node.Expand();
                    }
                }
                else if (node?.Tag is ZoneCategoryMarker)
                {
                    // "Зоны прямого/обратного просмотра" - чисто визуальная группировка, уже
                    // полностью построена при загрузке зон сервера (LoadServerZonesIntoTreeAsync) -
                    // ничего дополнительно подгружать не нужно, просто обычное разворачивание узла.
                }
                else if (node?.Tag is ServerNodeMarker serverMarker)
                {
                    if (!_loadedServerTreeNodes.Contains(node))
                    {
                        await LoadServerZonesIntoTreeAsync(node, serverMarker.ServerName);
                        node.Expand();
                    }
                    else
                    {
                        SetCurrentServerContext(serverMarker.ServerName);
                    }
                }
            };
            // Подстраховка: если раскрыть стрелкой узел (сервер/зону/scope), который ещё не
            // выбирали кликом - тот же ленивый догруз, чтобы не остаться с одной заглушкой "..." внутри.
            treeRecordFolders.BeforeExpand += async (s, e) =>
            {
                if (e.Node?.Tag is string scopeNameUnloaded && !_loadedScopeTreeNodes.Contains(e.Node))
                {
                    SyncContextToTreeNode(e.Node);
                    await LoadScopeIntoTreeAsync(e.Node, scopeNameUnloaded);
                }
                else if (e.Node?.Tag is ZoneNodeMarker zoneMarker && !zoneMarker.ScopesUnavailable && !_loadedZoneTreeNodes.Contains(e.Node))
                    await LoadZoneScopesIntoTreeAsync(e.Node, zoneMarker.ServerName, zoneMarker.ZoneName);
                else if (e.Node?.Tag is ServerNodeMarker serverMarker && !_loadedServerTreeNodes.Contains(e.Node))
                    await LoadServerZonesIntoTreeAsync(e.Node, serverMarker.ServerName);
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
                    // Клик ВНУТРИ уже выделенной группы - сохраняем всё выделение (как в
                    // проводнике), иначе множественный выбор было бы бессмысленно заводить -
                    // правый клик тут же сбрасывал бы его до одной строки под курсором.
                    if (idx >= 0 && !lstRecords.SelectedIndices.Contains(idx))
                        lstRecords.SelectedIndex = idx;
                }
            };
            lstRecords.ContextMenuStrip = recordsContextMenu;

            cmbScopeZoneName = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            var btnLoadZoneNames = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список зон (для подсказок в диалогах)", _toolTip,
                async (s, e) => await RefreshAllZoneCombosAsync());
            var btnLoadScopes = IconFactory.CreateButton(IconFactory.Folder(), "Обновить дерево серверов/зон/scope'ов с нуля", _toolTip,
                (s, e) => InitializeServerTree());

            // Управление зонами - раньше жило на отдельной вкладке "Зоны", теперь здесь же,
            // рядом со scope-кнопками (см. GroupBox-разделение ниже в разметке column).
            txtNewZoneName = Tb(180, "имя зоны, напр. corp.local");
            cmbZoneType = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbZoneType.Items.AddRange(new object[]
            {
                "AD-интегрированная (реплика: домен)",
                "AD-интегрированная (реплика: лес)",
                "Файловая (.dns на диске)"
            });
            cmbZoneType.SelectedIndex = 0;

            var btnAddZone = IconFactory.CreateButton(IconFactory.Add(), "Создать зону...", _toolTip, async (s, e) =>
            {
                var (name, type) = AddZoneDialog.Show();
                if (name == null) return; // отмена
                txtNewZoneName.Text = name;
                cmbZoneType.Text = type;
                await AddZoneAsync();
            });
            var btnRemoveZone = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранную зону (в дереве слева)", _toolTip,
                async (s, e) => await RemoveZoneAsync());
            var btnReloadZone = IconFactory.CreateButton(IconFactory.RefreshZone(), "Перезагрузить выбранную зону (dnscmd /ZoneReload, только локально)", _toolTip,
                async (s, e) => await ReloadSelectedZoneAsync());
            var btnExportZones = IconFactory.CreateButton(IconFactory.Export(), "Экспортировать зоны текущего сервера в файл...", _toolTip,
                (s, e) => ExportCurrentServerZones());

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
                (s, e) => ExportListToFile(lstRecords.Items.Cast<string>(),
                    $"records_{Val(txtRecordScopeName)}_{SanitizeForFileName(CurrentServerLabel())}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    $"Экспортировано {DateTime.Now:yyyy-MM-dd HH:mm:ss} с сервера: {CurrentServerLabel()} | Зона: {Val(cmbScopeZoneName)} | Scope: {Val(txtRecordScopeName)}"));

            var btnImportRecords = IconFactory.CreateButton(IconFactory.Import(), "Импорт записей из файла...", _toolTip,
                async (s, e) => await ImportRecordsAsync());

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
            recordsFilterRow.Controls.Add(btnImportRecords);

            lstRecords.Dock = DockStyle.Fill;
            lstRecords.Font = new Font("Consolas", 9F);
            lstRecords.HorizontalScrollbar = true;
            var recordsWrapper = new Panel { Dock = DockStyle.Fill };
            recordsWrapper.Controls.Add(lstRecords);
            recordsWrapper.Controls.Add(recordsFilterRow);

            /*var hint = HelpIcon.Create(_toolTip,
                "Запись добавляется в scope/папку, которая сейчас выбрана в дереве слева.\n" +
                "Для записи в корне зоны (SOA/NS/SPF и т.п.) укажи имя \"@\" в диалоге добавления.\n" +
                "Слева - дерево: сверху серверы (Локальный + любой, к которому успешно\n" +
                "подключались), внутри каждого - зоны прямого/обратного просмотра, внутри них -\n" +
                "сами зоны, внутри зоны - scope'ы, внутри scope - записи, сгруппированные по\n" +
                "составным именам (как в dnsmgmt.msc). Двойной клик по записи справа - изменить;\n" +
                "правая кнопка мыши - меню (проверить/изменить/удалить).");*/
            

            lblCurrentFolderPath = new Label
            {
                Text = "Добавление в: корень scope",
                AutoSize = true,
                ForeColor = Color.SteelBlue,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(4, 6, 4, 2)
            };

            lblZoneSource = new RichTextBox
            {
                Text = "Источник: - (выбери зону в дереве слева)",
                Height = 22,
                Width = 900,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.None,
                BackColor = SystemColors.Control, // сливается с фоном формы, не выглядит как поле ввода
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(4, 2, 4, 2),
                TabStop = false
            };

            // Два отдельных блока с рамкой и подписью - чтобы не путать, какая кнопка относится
            // к зонам, а какая к scope/записям, раз теперь всё это на одной вкладке с деревом.
            // AutoSize вместо жёсткого размера - ширина/высота подстраиваются под содержимое,
            // отступ справа/снизу задаём Padding у самого GroupBox, слева/сверху - через
            // Location внутренней панели (её НЕ докаем Fill'ом - иначе авторазмер невозможен,
            // получилась бы циклическая зависимость "размер по содержимому, а содержимое
            // растянуто на весь размер"). Оба блока стоят РЯДОМ (Row), не друг под другом -
            // экономит вертикальное место для дерева/списка записей ниже.
            var zoneManagementRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(8, 20),
                Margin = new Padding(0)
            };
            zoneManagementRow.Controls.AddRange(new Control[]
            {
                btnAddZone, btnRemoveZone, btnReloadZone, btnExportZones,
                new Label { Text = "  ", AutoSize = true },
                btnLoadZoneNames, btnLoadScopes
            });

            var zoneManagementGroup = new GroupBox
            {
                Text = "Зоны",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 8, 8) // правый/нижний отступ - симметрично Location(8,20) внутренней панели
            };
            zoneManagementGroup.Controls.Add(zoneManagementRow);

            var scopeManagementRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(8, 20),
                Margin = new Padding(0)
            };
            scopeManagementRow.Controls.AddRange(new Control[] { btnAddScope, btnRemoveScope, btnLoadRecords, });

            var scopeManagementGroup = new GroupBox
            {
                Text = "Scope",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 8, 8),
                Margin = new Padding(12, 0, 0, 0) // небольшой зазор между двумя блоками
            };
            scopeManagementGroup.Controls.Add(scopeManagementRow);

            var column = Column(
                Row(zoneManagementGroup, scopeManagementGroup),
                Row(lblZoneSource),
                Row(lblCurrentFolderPath)
            );

            return WrapTabTwoLists("Scopes и записи", column,
                "Серверы / зоны / scope'ы", treeRecordFolders,
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

            // Только зоны, к которым применимы Zone Scopes - условная пересылка/stub и
            // служебные авто-зоны в подсказки Scopes/Политик не нужны.
            var names = results
                .Where(IsScopeCapableZone)
                .Select(o => o.Properties["ZoneName"]?.Value?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            foreach (var combo in new[] { cmbScopeZoneName, cmbPolicyZoneName })
            {
                var current = combo.Text;
                combo.Items.Clear();
                foreach (var name in names) combo.Items.Add(name);
                combo.Text = current; // не затираем то, что человек уже успел ввести/выбрать вручную
            }
        }

        /// <summary>
        /// Зона, к которой применимы Zone Scopes (в дереве это "прямые"/"обратные", но не
        /// "прочие"): не служебная авто-зона (TrustAnchors, корневые подсказки и т.п.) и
        /// не условная пересылка / stub.
        /// </summary>
        private static bool IsScopeCapableZone(PSObject z)
        {
            if (z == null) return false;
            var zoneName = z.Properties["ZoneName"]?.Value?.ToString();
            if (IsServiceAutoZone(z, zoneName)) return false;
            var t = z.Properties["ZoneType"]?.Value?.ToString() ?? "";
            return !t.Equals("Forwarder", StringComparison.OrdinalIgnoreCase)
                && !t.Equals("Stub", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Служебная зона, которую стандартная оснастка dnsmgmt.msc в обычном (не "расширенном")
        /// виде не показывает: помеченные IsAutoCreated, а также TrustAnchors (DNSSEC) и корневые
        /// подсказки ".", у которых этот флаг на контроллере домена бывает не выставлен - поэтому
        /// дополнительно ловим их по имени. Редактировать/смотреть scope в них всё равно нельзя
        /// (WIN32 9611/9603), а сырой дамп ошибки в выводе только путает.
        /// </summary>
        private static bool IsServiceAutoZone(PSObject z, string zoneName)
        {
            if (z != null && DnsHelper.GetBool(z, "IsAutoCreated")) return true;

            var zoneType = z?.Properties["ZoneType"]?.Value?.ToString() ?? "";
            if (zoneType.Equals("Cache", StringComparison.OrdinalIgnoreCase)) return true; // псевдо-зона кэша / корневых подсказок

            var n = (zoneName ?? "").Trim().TrimEnd('.');
            return n.Length == 0 // корневые подсказки "."
                || n.Equals("TrustAnchors", StringComparison.OrdinalIgnoreCase)
                || n.Equals("0.in-addr.arpa", StringComparison.OrdinalIgnoreCase)
                || n.Equals("127.in-addr.arpa", StringComparison.OrdinalIgnoreCase)
                || n.Equals("255.in-addr.arpa", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Полностью пересобирает дерево с нуля: только локальный сервер сверху, заглушкой
        /// (ничего не подгружает сам по себе - ленивая подгрузка сработает при первом клике).
        /// </summary>
        private void InitializeServerTree()
        {
            treeRecordFolders.Nodes.Clear();
            _loadedServerTreeNodes.Clear();
            _loadedZoneTreeNodes.Clear();
            _loadedScopeTreeNodes.Clear();
            _folderToTreeNode.Clear();
            _folderRootToScopeName.Clear();
            _currentFolderNode = null;
            lstRecords.Items.Clear();
            _displayedRecords.Clear();
            _displayedFolders.Clear();

            AddServerRootIfMissing(""); // локальный сервер всегда первым
        }

        /// <summary>
        /// Узлы-серверы (верхний уровень) рисуем сами: сплошная заливка на всю ширину дерева
        /// (штатный BackColor у TreeNode тянется только под текст - у коротких имён выглядит
        /// обрезанным) плюс линия-разделитель по верхней кромке между соседними серверами.
        /// Все остальные узлы отдаём системной отрисовке (e.DrawDefault).
        /// </summary>
        private void TreeServerNode_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (!(e.Node.Tag is ServerNodeMarker) || e.Bounds.Height <= 0)
            {
                e.DrawDefault = true;
                return;
            }

            var g = e.Graphics;
            int right = treeRecordFolders.ClientRectangle.Right;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;

            using (var bg = new SolidBrush(selected ? Color.MediumBlue : Color.RoyalBlue))
                g.FillRectangle(bg, new Rectangle(e.Bounds.Left, e.Bounds.Top, right - e.Bounds.Left, e.Bounds.Height));

            // Разделитель между серверами - линия по верхней кромке узла (у самого первого не нужна).
            if (treeRecordFolders.Nodes.IndexOf(e.Node) > 0)
                using (var pen = new Pen(Color.Silver))
                    g.DrawLine(pen, 0, e.Bounds.Top, right, e.Bounds.Top);

            TextRenderer.DrawText(g, e.Node.Text, e.Node.NodeFont ?? treeRecordFolders.Font,
                new Rectangle(e.Bounds.Left, e.Bounds.Top, right - e.Bounds.Left, e.Bounds.Height),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>Имя ЭТОГО компьютера в верхнем регистре - подпись узла локального сервера в дереве (раньше было просто "Локальный").</summary>
        private static string LocalServerNodeLabel() => Environment.MachineName.ToUpperInvariant();

        /// <summary>Находит узел-сервер по имени, а если его ещё нет в дереве - создаёт (с заглушкой внутри, ленивая подгрузка).</summary>
        private TreeNode AddServerRootIfMissing(string serverName)
        {
            var normalized = (serverName ?? "").Trim();
            foreach (TreeNode existing in treeRecordFolders.Nodes)
            {
                if (existing.Tag is ServerNodeMarker m && string.Equals(m.ServerName ?? "", normalized, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }

            var label = string.IsNullOrEmpty(normalized) ? LocalServerNodeLabel() : normalized;
            var node = new TreeNode(label)
            {
                Tag = new ServerNodeMarker { ServerName = normalized },
                BackColor = Color.RoyalBlue,
                ForeColor = Color.White,
                NodeFont = new Font(treeRecordFolders.Font, FontStyle.Bold)
            };
            node.Nodes.Add(new TreeNode("...")); // заглушка для стрелки разворачивания
            treeRecordFolders.Nodes.Add(node);
            return node;
        }

        /// <summary>Идёт вверх по дереву от любого узла (scope / папка записей) до узла-зоны и возвращает его маркер.</summary>
        private static ZoneNodeMarker OwningZoneMarker(TreeNode node)
        {
            for (var n = node; n != null; n = n.Parent)
                if (n.Tag is ZoneNodeMarker zm) return zm;
            return null;
        }

        /// <summary>
        /// Восстанавливает глобальный контекст (целевой сервер + имя зоны) по положению узла
        /// в дереве. Нужно перед любой операцией с scope/записями: при нескольких подключённых
        /// серверах DnsHelper.ComputerName - один на всё приложение, и без этой синхронизации
        /// правка ушла бы на сервер, чью ветку дерева трогали последней, а не на владельца
        /// выбранного узла.
        /// </summary>
        private void SyncContextToTreeNode(TreeNode node)
        {
            var zm = OwningZoneMarker(node);
            if (zm == null) return;
            SetCurrentServerContext(zm.ServerName);
            cmbScopeZoneName.Text = zm.ZoneName;
        }

        /// <summary>
        /// Переключает панель "Целевой DNS-сервер" сверху на указанный сервер (пусто = локальный) -
        /// используется навигацией по дереву, чтобы верхняя панель всегда отражала, с каким
        /// сервером сейчас реально работает дерево. Переиспользует уже существующие обработчики
        /// CheckedChanged/TextChanged - они сами обновят DnsHelper.ComputerName по цепочке.
        /// </summary>
        private void SetCurrentServerContext(string serverName)
        {
            if (string.IsNullOrEmpty(serverName))
            {
                chkLocalServer.Checked = true;
            }
            else
            {
                chkLocalServer.Checked = false;
                cmbTargetServer.Text = serverName;
            }
        }

        /// <summary>Ленивая подгрузка: зоны конкретного узла-сервера (вызывается при первом выборе/раскрытии).</summary>
        private async Task LoadServerZonesIntoTreeAsync(TreeNode serverNode, string serverName)
        {
            SetCurrentServerContext(serverName);

            var label = string.IsNullOrEmpty(serverName) ? "локальный" : serverName;
            AppendLog($"Загружаю зоны сервера '{label}'...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(log);

            serverNode.Nodes.Clear();
            _loadedServerTreeNodes.Add(serverNode);
            if (!WasSuccess(log)) return; // причина уже понятно объяснена в логе (не DNS-сервер / нет прав / WinRM и т.п.)

            if (!string.IsNullOrEmpty(serverName) && _connectedRemoteServers.Add(serverName))
                RefreshPolicyServerCombo(); // новый удалённый сервер стал доступен - показать его в выпадашке "Политики"

            // Контейнеры, как в dnsmgmt.msc: прямого просмотра, обратного просмотра, зоны-заглушки
            // (Stub) и серверы условной пересылки (Forwarder). Классификация - по ZoneType и
            // IsReverseLookupZone из самого объекта, а не по суффиксу имени.
            var forwardCategory = new TreeNode("Зоны прямого просмотра") { Tag = new ZoneCategoryMarker { ServerName = serverName, Kind = ZoneCategoryKind.Forward } };
            var reverseCategory = new TreeNode("Зоны обратного просмотра") { Tag = new ZoneCategoryMarker { ServerName = serverName, Kind = ZoneCategoryKind.Reverse } };
            var stubCategory = new TreeNode("Зоны-заглушки") { Tag = new ZoneCategoryMarker { ServerName = serverName, Kind = ZoneCategoryKind.Stub } };
            var forwarderCategory = new TreeNode("Серверы условной пересылки") { Tag = new ZoneCategoryMarker { ServerName = serverName, Kind = ZoneCategoryKind.Forwarder } };
            serverNode.Nodes.Add(forwardCategory);
            serverNode.Nodes.Add(reverseCategory);
            serverNode.Nodes.Add(stubCategory);
            serverNode.Nodes.Add(forwarderCategory);

            foreach (var z in results
                         .Where(o => o != null)
                         .OrderBy(o => o.Properties["ZoneName"]?.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var zoneName = z.Properties["ZoneName"]?.Value?.ToString();
                if (string.IsNullOrEmpty(zoneName)) continue;

                // Служебные авто-зоны (TrustAnchors, корневые подсказки ".", 0/127/255.in-addr.arpa)
                // в обычном (не "расширенном") виде оснастки скрыты - прячем и здесь.
                if (IsServiceAutoZone(z, zoneName)) continue;

                var zoneType = z.Properties["ZoneType"]?.Value?.ToString() ?? "";
                var isStub = zoneType.Equals("Stub", StringComparison.OrdinalIgnoreCase);
                var isForwarder = zoneType.Equals("Forwarder", StringComparison.OrdinalIgnoreCase);
                var isForwarderOrStub = isStub || isForwarder;

                // IsReverseLookupZone - родной признак PowerShell, надёжнее суффикса имени
                // (ловит и нестандартно названные обратные зоны).
                var isReverse = DnsHelper.GetBool(z, "IsReverseLookupZone")
                                || zoneName.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase)
                                || zoneName.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase);

                var category = isStub ? stubCategory
                             : isForwarder ? forwarderCategory
                             : isReverse ? reverseCategory
                             : forwardCategory;

                var zoneNode = new TreeNode(zoneName)
                {
                    Tag = new ZoneNodeMarker { ServerName = serverName, ZoneName = zoneName, ScopesUnavailable = isForwarderOrStub }
                };
                // Условная пересылка / stub не поддерживают Zone Scopes (WIN32 9603) - без
                // заглушки "...", это листья: по клику покажем только источник зоны.
                if (!isForwarderOrStub) zoneNode.Nodes.Add(new TreeNode("..."));
                category.Nodes.Add(zoneNode);
            }

            // Заглушки и условная пересылка у большинства серверов пустые - не мозолим глаза.
            // Прямые/обратные оставляем всегда, даже пустыми (привычное место).
            if (stubCategory.Nodes.Count == 0) stubCategory.Remove();
            if (forwarderCategory.Nodes.Count == 0) forwarderCategory.Remove();
        }

        /// <summary>Ленивая подгрузка: scope'ы конкретного узла-зоны (вызывается при первом выборе/раскрытии).</summary>
        private async Task LoadZoneScopesIntoTreeAsync(TreeNode zoneNode, string serverName, string zoneName)
        {
            SetCurrentServerContext(serverName);
            cmbScopeZoneName.Text = zoneName; // держим скрытое поле в синхроне - его читают AddScopeAsync/AddZoneDialog/AddScopeDialog и т.п.

            AppendLog($"Загружаю scopes зоны '{zoneName}'...");
            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName };
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZoneScope", parameters));

            zoneNode.Nodes.Clear();
            _loadedZoneTreeNodes.Add(zoneNode);

            if (!WasSuccess(log))
            {
                // Не вываливаем сырой CIM-дамп (WIN32 9603/9611 и т.п.) - для зон такого типа
                // Zone Scopes просто неприменимы. Короткое сообщение с выделенными именами.
                AppendLogStyled(
                    ("Зона ", false, false),
                    (zoneName, true, true),
                    (" на сервере ", false, false),
                    (CurrentServerLabel(), true, true),
                    (" не поддерживает Zone Scopes - просматривать и править области в ней нельзя.", false, false));
                return;
            }
            AppendLog(log);

            var scopeNames = DnsHelper.GetStringProperty(results, "ZoneScope");
            if (scopeNames.Count == 0) scopeNames = DnsHelper.GetStringProperty(results, "Name");

            // Scope не подгружается сразу целиком - только когда реально понадобится (клик/раскрытие).
            // Некоторые scope содержат сотни записей, незачем тянуть все разом, если смотрят один.
            foreach (var name in scopeNames)
            {
                var scopeNode = new TreeNode(name) { Tag = name };
                scopeNode.Nodes.Add(new TreeNode("...")); // заглушка - только чтобы была стрелка разворачивания
                zoneNode.Nodes.Add(scopeNode);
            }
        }

        /// <summary>Ищет узел-зону конкретного сервера в уже построенном дереве (через категорию прямых/обратных - без обращения к серверу).</summary>
        private TreeNode FindZoneNode(string serverName, string zoneName)
        {
            var normalizedServer = (serverName ?? "").Trim();
            foreach (TreeNode serverNode in treeRecordFolders.Nodes)
            {
                if (!(serverNode.Tag is ServerNodeMarker sm) || !string.Equals(sm.ServerName ?? "", normalizedServer, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (TreeNode categoryNode in serverNode.Nodes)
                {
                    foreach (TreeNode zoneNode in categoryNode.Nodes)
                    {
                        if (zoneNode.Tag is ZoneNodeMarker zm && string.Equals(zm.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                            return zoneNode;
                    }
                }
            }
            return null;
        }

        /// <summary>Ищет узел-scope конкретной зоны конкретного сервера в уже построенном дереве (без обращения к серверу).</summary>
        private TreeNode FindScopeNode(string serverName, string zoneName, string scopeName)
        {
            var zoneNode = FindZoneNode(serverName, zoneName);
            if (zoneNode == null) return null;
            foreach (TreeNode scopeNode in zoneNode.Nodes)
            {
                if (string.Equals(scopeNode.Text, scopeName, StringComparison.OrdinalIgnoreCase))
                    return scopeNode;
            }
            return null;
        }

        /// <summary>Перезагружает scope'ы ТЕКУЩЕЙ (уже открытой в дереве) зоны - после создания/удаления scope.</summary>
        private async Task RefreshCurrentZoneScopesAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            if (string.IsNullOrEmpty(zoneName)) return;

            var serverName = DnsHelper.ComputerName;
            var zoneNode = FindZoneNode(serverName, zoneName);
            if (zoneNode == null) return; // зона ещё не открывалась в дереве в этой сессии - обновлять нечего

            await LoadZoneScopesIntoTreeAsync(zoneNode, serverName, zoneName);
            zoneNode.Expand();
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
            await RefreshCurrentZoneScopesAsync();
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
            await RefreshCurrentZoneScopesAsync();
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
            if (WasSuccess(log))
                AppendLog($"OK: запись \"{recordName}\" ({type}) {value} добавлена в зону \"{zoneName}\", scope \"{scopeName}\".");
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

            var (folderName, ip, asWildcard) = CreateSubfolderDialog.Show(parentHint);
            if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(ip)) return; // отмена

            // Буквально как в оснастке ("Новый домен" с пустым именем записи = "(как папка
            // верхнего уровня)") - запись называется РОВНО как сама папка, без "*". У нас это
            // НЕ покажется папкой в дереве, пока внутри неё нет ещё одной вложенной записи -
            // предупреждение об этом уже показано в самом диалоге при выборе такого варианта.
            var recordName = asWildcard
                ? (string.IsNullOrEmpty(parentSuffix) ? $"*.{folderName}" : $"*.{folderName}.{parentSuffix}")
                : (string.IsNullOrEmpty(parentSuffix) ? folderName : $"{folderName}.{parentSuffix}");

            var (cmdlet, parameters) = BuildAddRecordCommand(zoneName, scopeName, "A", recordName, ip, "", "", "");
            AppendLog($"Создаю папку '{folderName}' - добавляю запись '{recordName}' -> {ip}...");
            var (_, log) = await Task.Run(() => DnsHelper.Invoke(cmdlet, parameters));
            AppendLog(log);
            if (WasSuccess(log))
                AppendLog($"OK: запись \"{recordName}\" (A) {ip} добавлена в зону \"{zoneName}\", scope \"{scopeName}\" (создание папки \"{folderName}\").");
            FileLogger.LogChange("RECORD ADD", zoneName,
                $"Scope={scopeName} A {recordName} -> {ip} (создание папки '{folderName}')", WasSuccess(log), log);

            await RefreshRecordsAsync();
        }

        /// <summary>
        /// Импорт записей из файла, ранее сохранённого через "Экспорт в файл...". Строки-папки
        /// (вида "[FLDR] имя  N запис.") распознаются и НЕ импортируются как записи - вместо
        /// этого пользователю предлагается создать соответствующий субдомен через wildcard
        /// (см. CreateSubfolderAsync выше - та же идея, здесь просто автоматизирован массовый
        /// разбор файла). Строки-заголовки экспорта (начинаются с "#") пропускаются как метаданные.
        /// Для SRV/MX значение в файле составное (см. DnsHelper.DescribeRecordData) - разбирается
        /// обратно регуляркой; если формат не совпал (например, файл от старой версии без
        /// preference у MX) - запись всё равно импортируется с разумными значениями по умолчанию,
        /// кроме SRV, где без порта/приоритета/веса создать запись нельзя - такие пропускаются
        /// с явным сообщением, добавить придётся вручную.
        /// </summary>
        private async Task ImportRecordsAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Сначала выбери зону и scope (в дереве слева).");
                return;
            }

            using var ofd = new OpenFileDialog
            {
                Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
                Title = "Выбери файл с выгрузкой записей"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            string[] rawLines;
            try { rawLines = File.ReadAllLines(ofd.FileName); }
            catch (Exception ex) { AppendLog($"ОШИБКА: не удалось прочитать файл - {ex.Message}"); return; }

            var detectedFolders = new List<string>();
            var detectedRecords = new List<(string Name, string Type, string Value)>();

            foreach (var raw in rawLines)
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("#")) continue; // строка-заголовок экспорта, не запись

                if (line.TrimStart().StartsWith("[FLDR]", StringComparison.OrdinalIgnoreCase))
                {
                    // "[FLDR] pro32connect              4 запис." - имя лежит между маркером
                    // и хвостом "N запис.", вырезаем тем же разделителем "2+ пробела", что и
                    // у обычных записей ниже - обычный ASCII, никаких суррогатных пар.
                    var afterMarker = line.Substring(line.IndexOf("[FLDR]", StringComparison.OrdinalIgnoreCase) + "[FLDR]".Length).Trim();
                    var folderParts = System.Text.RegularExpressions.Regex.Split(afterMarker, @"\s{2,}");
                    var folderName = folderParts.Length > 0 ? folderParts[0].Trim() : "";
                    if (!string.IsNullOrEmpty(folderName) && !detectedFolders.Contains(folderName))
                        detectedFolders.Add(folderName);
                    continue; // папка - не настоящая запись, при импорте самих записей игнорируем
                }

                // Формат строки записи - "{name,-28} {type,-6} {value}" (см. RenderRecordsList).
                // Парсим по разделителю "2+ пробела подряд", а не по точным позициям колонок -
                // устойчивее, если формат чуть поменяется в будущей версии.
                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
                if (parts.Length < 3) continue; // не похоже на строку записи - пропускаем молча

                var name = parts[0].Trim();
                var type = parts[1].Trim();
                var value = string.Join(" ", parts.Skip(2)).Trim();
                detectedRecords.Add((name, type, value));
            }

            if (detectedRecords.Count == 0 && detectedFolders.Count == 0)
            {
                AppendLog("В файле не найдено ни записей, ни папок - нечего импортировать.");
                return;
            }

            var currentPathSuffix = GetFolderPathSuffix(_currentFolderNode);
            var targetHint = string.IsNullOrEmpty(currentPathSuffix)
                ? $"{zoneName} / {scopeName} (корень scope)"
                : $"{zoneName} / {scopeName} / {currentPathSuffix}";

            var options = ImportRecordsDialog.Show(detectedFolders, detectedRecords.Count, targetHint);
            if (options == null) return; // отмена

            AppendLog($"Импорт: начинаю ({detectedRecords.Count} записей в файле, папок к созданию: {options.Folders.Count(f => f.Create)})...");

            // Сначала создаём выбранные папки (wildcard-записи) - структура раньше содержимого,
            // хотя для самого DNS Server порядок не принципиален.
            foreach (var folder in options.Folders.Where(f => f.Create))
            {
                if (string.IsNullOrEmpty(folder.WildcardIp))
                {
                    AppendLog($"Пропускаю создание папки '{folder.Name}' - не указан IP для wildcard-записи.");
                    continue;
                }

                var wildcardName = string.IsNullOrEmpty(currentPathSuffix) ? $"*.{folder.Name}" : $"*.{folder.Name}.{currentPathSuffix}";
                var (folderCmdlet, folderParams) = BuildAddRecordCommand(zoneName, scopeName, "A", wildcardName, folder.WildcardIp, "", "", "");
                AppendLog($"Создаю папку '{folder.Name}' - wildcard-запись '{wildcardName}' -> {folder.WildcardIp}...");
                var (_, folderLog) = await Task.Run(() => DnsHelper.Invoke(folderCmdlet, folderParams));
                AppendLog(folderLog);
                FileLogger.LogChange("RECORD ADD", zoneName,
                    $"Scope={scopeName} A {wildcardName} -> {folder.WildcardIp} (импорт, создание папки '{folder.Name}')", WasSuccess(folderLog), folderLog);
            }

            // Актуальный список записей ЭТОГО scope - нужен для проверки конфликтов (имя+тип)
            // и для получения "сырого" объекта существующей записи при перезаписи. Складываем
            // в словарь по ключу "имя|тип" - и обновляем ПО ХОДУ ИМПОРТА (см. ниже), а не
            // только один раз в начале: если в самом файле есть повторяющаяся запись, второй
            // дубль должен распознаться как конфликт с тем, что мы только что сами добавили
            // в этом же прогоне, а не улететь в DNS Server и получить WIN32 9709/9603 напрямую.
            var existingParams = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["ZoneScope"] = scopeName };
            var (existingResults, existingLog) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerResourceRecord", existingParams));
            if (!WasSuccess(existingLog))
            {
                AppendLog("ОШИБКА: не удалось получить текущие записи scope для проверки конфликтов - импорт остановлен.");
                AppendLog(existingLog);
                return;
            }

            var knownRecords = new Dictionary<string, PSObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in existingResults)
            {
                var key = $"{r.Properties["HostName"]?.Value}|{r.Properties["RecordType"]?.Value}";
                knownRecords[key] = r; // при дублях в самой зоне (round-robin A и т.п.) остаётся последний - для проверки "есть ли вообще конфликт" этого достаточно
            }

            var bulkModeActive = false;
            var bulkChoice = ImportConflictChoice.Skip;
            int added = 0, overwritten = 0, skipped = 0, failed = 0;

            foreach (var rec in detectedRecords)
            {
                if (options.ExcludeApex && (rec.Name == "@" || string.IsNullOrEmpty(rec.Name)))
                {
                    skipped++;
                    continue;
                }

                // SRV/MX - значение в файле составное (см. DescribeRecordData), разбираем обратно
                // СРАЗУ, до проверки конфликта - и чтобы было что показать в сравнении, и чтобы
                // заведомо нераспарсенный SRV не тратил диалог конфликта впустую.
                var value = rec.Value;
                string priority = "10", weight = "10", port = "443";

                if (rec.Type.Equals("SRV", StringComparison.OrdinalIgnoreCase))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(rec.Value,
                        @"^(?<target>.+):(?<port>\d+)\s*\(priority=(?<priority>\d+),\s*weight=(?<weight>\d+)\)$");
                    if (m.Success)
                    {
                        value = m.Groups["target"].Value.Trim();
                        port = m.Groups["port"].Value;
                        priority = m.Groups["priority"].Value;
                        weight = m.Groups["weight"].Value;
                    }
                    else
                    {
                        AppendLog($"Не удалось разобрать составное значение SRV-записи '{rec.Name}' ('{rec.Value}') - пропускаю, добавь вручную.");
                        skipped++;
                        continue;
                    }
                }
                else if (rec.Type.Equals("MX", StringComparison.OrdinalIgnoreCase))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(rec.Value, @"^(?<exchange>.+?)\s*\(preference=(?<preference>\d+)\)$");
                    if (m.Success)
                    {
                        value = m.Groups["exchange"].Value.Trim();
                        priority = m.Groups["preference"].Value;
                    }
                    // Не распарсилось (например файл от версии, где MX ещё не показывал preference) -
                    // используем значение как есть, приоритет по умолчанию (10).
                }

                var recordKey = $"{rec.Name}|{rec.Type}";
                var hasExisting = knownRecords.TryGetValue(recordKey, out var existingMatch);

                if (hasExisting)
                {
                    // Реальное значение существующей записи - чтобы в диалоге конфликта было
                    // видно, это полный дубль или отличается IP/имя/что угодно другое.
                    var existingValueText = DnsHelper.DescribeRecordData(existingMatch.Properties["RecordData"]?.Value, rec.Type);

                    ImportConflictChoice choice;
                    if (bulkModeActive)
                    {
                        choice = bulkChoice;
                    }
                    else
                    {
                        choice = ImportConflictDialog.Show(rec.Name, rec.Type, existingValueText, value);
                        if (choice == ImportConflictChoice.OverwriteAll || choice == ImportConflictChoice.SkipAll)
                        {
                            bulkModeActive = true;
                            bulkChoice = choice == ImportConflictChoice.OverwriteAll ? ImportConflictChoice.Overwrite : ImportConflictChoice.Skip;
                            choice = bulkChoice;
                        }
                    }

                    if (choice == ImportConflictChoice.Skip)
                    {
                        skipped++;
                        continue;
                    }

                    // Перезапись - сначала удаляем старую запись целиком через -InputObject
                    // (тот же паттерн, что и в RemoveRecordAsync), потом добавляем новую ниже.
                    var delParams = new Dictionary<string, object>
                    {
                        ["ZoneName"] = zoneName,
                        ["ZoneScope"] = scopeName,
                        ["InputObject"] = existingMatch,
                        ["Force"] = true
                    };
                    var (_, delLog) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerResourceRecord", delParams));
                    if (!WasSuccess(delLog))
                    {
                        AppendLog($"ОШИБКА при удалении старой записи '{rec.Name}' перед перезаписью: {delLog}");
                        failed++;
                        continue;
                    }
                    knownRecords.Remove(recordKey); // старой больше нет - если добавление ниже не удастся, конфликта на неё уже не будет
                }

                var (addCmdlet, addParams) = BuildAddRecordCommand(zoneName, scopeName, rec.Type, rec.Name, value, priority, weight, port);
                var (_, addLog) = await Task.Run(() => DnsHelper.Invoke(addCmdlet, addParams));

                if (WasSuccess(addLog))
                {
                    if (hasExisting) overwritten++; else added++;
                    AppendLog($"OK: запись \"{rec.Name}\" ({rec.Type}) {value} добавлена в зону \"{zoneName}\", scope \"{scopeName}\"" +
                              (hasExisting ? " (перезапись)." : " (импорт)."));
                    FileLogger.LogChange("RECORD ADD", zoneName,
                        $"Scope={scopeName} {rec.Type} {rec.Name} -> {value} (импорт{(hasExisting ? ", перезапись" : "")})", true, null);

                    // Точечный довыгруз реального объекта только что добавленной записи -
                    // если в файле есть ЕЩЁ ОДНА строка с тем же именем+типом (дубль внутри
                    // самого файла), она должна распознаться как конфликт с этой, а не улететь
                    // в DNS Server напрямую и вернуть WIN32 9709/аналогичный "уже существует".
                    var refetchParams = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["ZoneScope"] = scopeName, ["Name"] = rec.Name, ["RRType"] = rec.Type };
                    var (refetched, _) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerResourceRecord", refetchParams));
                    if (refetched.Count > 0) knownRecords[recordKey] = refetched[0];
                }
                else
                {
                    failed++;
                    AppendLog($"ОШИБКА при импорте записи '{rec.Name}' ({rec.Type}): {addLog}");
                    FileLogger.LogChange("RECORD ADD", zoneName,
                        $"Scope={scopeName} {rec.Type} {rec.Name} -> {value} (импорт)", false, addLog);
                }
            }

            AppendLog($"Импорт завершён: добавлено {added}, перезаписано {overwritten}, пропущено {skipped}, ошибок {failed}.");
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
                if (success)
                    AppendLog($"OK: запись \"{recordName}\" ({type}) {value} добавлена в зону \"{zoneName}\", scope \"{scopeName}\" (файловый режим).");
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

        /// <summary>
        /// Обходной путь для удаления записи из scope файловой (Secondary / read-only) зоны,
        /// когда Remove-DnsServerResourceRecord отказывает с WIN32 9611. Зеркало
        /// AddRecordToScopeFileAsync: правим .dns-файл scope напрямую и просим DNS Server
        /// перечитать зону. ВСЕГДА локально (файл лежит на этой машине).
        ///
        /// Из файла удаляется ТОЛЬКО строка(и), однозначно совпадающая с выбранной записью
        /// по имени + типу + значению. Если совпадение не одно (не найдено или найдено
        /// несколько) - файл не трогается вообще, чтобы случайно не снести чужую запись.
        /// Возвращает число реально удалённых записей.
        /// </summary>
        private async Task<int> DeleteRecordsFromScopeFileAsync(string zoneName, string scopeName, List<PSObject> records)
        {
            var filePath = Path.Combine(@"C:\Windows\System32\dns", zoneName, scopeName + ".dns");
            if (!File.Exists(filePath))
            {
                AppendLog($"ОШИБКА: файл scope не найден: {filePath} - проверь имя зоны/scope (регистр важен для пути на диске).");
                return 0;
            }

            List<string> lines;
            try { lines = File.ReadAllLines(filePath).ToList(); }
            catch (Exception ex) { AppendLog($"ОШИБКА чтения {filePath}: {ex.Message}"); return 0; }

            var toRemove = new SortedSet<int>();
            var unresolved = new List<string>();
            foreach (var rec in records)
            {
                var label = $"{rec.Properties["HostName"]?.Value} ({rec.Properties["RecordType"]?.Value})";
                var hits = FindScopeFileRecordLines(lines, zoneName, rec);
                if (hits == null)
                    unresolved.Add($"{label}: тип записи не поддерживается файловым удалением");
                else if (hits.Count == 0)
                    unresolved.Add($"{label}: подходящая строка в файле не найдена");
                else if (hits.Count > 1)
                    unresolved.Add($"{label}: под условие подходит строк: {hits.Count} - удали вручную");
                else
                    toRemove.Add(hits[0]);
            }

            if (unresolved.Count > 0)
            {
                AppendLog("Файловое удаление отменено, файл не изменён:" + Environment.NewLine +
                          "  " + string.Join(Environment.NewLine + "  ", unresolved) + Environment.NewLine +
                          $"Файл: {filePath}");
                return 0;
            }

            string backupPath;
            try
            {
                backupPath = filePath + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(filePath, backupPath, overwrite: false);

                var kept = lines.Where((_, i) => !toRemove.Contains(i)).ToList();
                File.WriteAllLines(filePath, kept, new UTF8Encoding(false));
                AppendLog($"OK: из файла {filePath} удалено строк: {toRemove.Count} (бэкап: {backupPath}).");

                AppendLog($"Перезагружаю зону '{zoneName}' (dnscmd /ZoneReload)...");
                var reload = RunDnscmdZoneReload(zoneName);
                AppendLog(reload);
                var ok = reload.StartsWith("OK");

                foreach (var rec in records)
                    FileLogger.LogChange("RECORD DELETE (файл)", zoneName,
                        $"Scope={scopeName} {rec.Properties["RecordType"]?.Value} {rec.Properties["HostName"]?.Value} | файл={filePath}",
                        ok, ok ? null : reload);

                if (!ok)
                {
                    AppendLog($"Зона не перезагрузилась - файл уже изменён, откат из бэкапа: копируй {backupPath} обратно в {filePath} и перезагрузи зону вручную.");
                    return 0;
                }
                return records.Count;
            }
            catch (Exception ex)
            {
                AppendLog($"ОШИБКА при правке файла/перезагрузке зоны: {ex.Message}");
                FileLogger.LogChange("RECORD DELETE (файл)", zoneName, $"Scope={scopeName} | файл={filePath}", false, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Ищет в строках .dns-файла scope все строки, соответствующие записи rec
        /// (имя + тип + значение). Возвращает индексы строк; null - если тип записи
        /// файловым удалением не поддерживается.
        /// </summary>
        private static List<int> FindScopeFileRecordLines(List<string> lines, string zoneName, PSObject rec)
        {
            var wantOwner = NormalizeZoneOwner(rec.Properties["HostName"]?.Value?.ToString(), zoneName);
            var wantType = (rec.Properties["RecordType"]?.Value?.ToString() ?? "").ToUpperInvariant();
            var wantData = ZoneRdataFromRecord(rec, wantType);
            if (wantData == null) return null; // тип не поддерживается

            var result = new List<int>();
            string lastOwner = null;
            for (int i = 0; i < lines.Count; i++)
            {
                var raw = StripZoneFileComment(lines[i]);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.TrimStart().StartsWith("$")) continue; // $ORIGIN / $TTL / $GENERATE

                var ownerInline = !char.IsWhiteSpace(raw[0]);
                var tok = TokenizeZoneFileLine(raw);
                if (tok.Count == 0) continue;

                int idx = 0;
                string owner;
                if (ownerInline) { owner = tok[0]; idx = 1; lastOwner = owner; }
                else owner = lastOwner;
                if (owner == null) continue;

                // необязательные TTL и CLASS в любом порядке перед типом
                for (int guard = 0; guard < 2 && idx < tok.Count; guard++)
                {
                    if (IsZoneFileTtl(tok[idx]) || IsZoneFileClass(tok[idx])) { idx++; continue; }
                    break;
                }
                if (idx >= tok.Count) continue;

                var type = tok[idx].ToUpperInvariant();
                idx++;
                if (type != wantType) continue;
                if (NormalizeZoneOwner(owner, zoneName) != wantOwner) continue;

                var rdata = string.Join(" ", tok.Skip(idx));
                if (ZoneRdataFromFile(rdata, type) == wantData) result.Add(i);
            }
            return result;
        }

        private static string StripZoneFileComment(string line)
        {
            var inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (c == ';' && !inQuotes) return line.Substring(0, i);
            }
            return line;
        }

        private static List<string> TokenizeZoneFileLine(string line)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            foreach (var c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; sb.Append(c); continue; }
                if (!inQuotes && (c == ' ' || c == '\t' || c == '(' || c == ')'))
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        private static bool IsZoneFileClass(string t) =>
            t.Equals("IN", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("CH", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("HS", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("CS", StringComparison.OrdinalIgnoreCase);

        private static bool IsZoneFileTtl(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            int i = 0;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i == 0) return false;
            if (i == t.Length) return true;                       // чистое число секунд
            return i == t.Length - 1 && "smhdwSMHDW".IndexOf(t[t.Length - 1]) >= 0; // 1h / 30m / 2w
        }

        /// <summary>Имя владельца записи -> сравнимая форма: "@" для вершины зоны, иначе относительное имя в нижнем регистре без хвостовой точки.</summary>
        private static string NormalizeZoneOwner(string name, string zone)
        {
            if (name == null) return null;
            name = name.Trim().TrimEnd('.');
            var z = (zone ?? "").Trim().TrimEnd('.');
            if (name.Length == 0 || name == "@") return "@";
            if (z.Length > 0 && name.Equals(z, StringComparison.OrdinalIgnoreCase)) return "@";
            if (z.Length > 0 && name.EndsWith("." + z, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - z.Length - 1);
            return name.ToLowerInvariant();
        }

        private static string NormalizeZoneName(string s) => (s ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        private static string NormalizeZoneIp(string s)
        {
            s = (s ?? "").Trim();
            return System.Net.IPAddress.TryParse(s, out var ip) ? ip.ToString() : s.ToLowerInvariant();
        }

        /// <summary>Значение записи из объекта Get-DnsServerResourceRecord в нормализованную строку. null - тип не поддерживается файловым удалением.</summary>
        private static string ZoneRdataFromRecord(PSObject rec, string type)
        {
            var rd = PSObject.AsPSObject(rec.Properties["RecordData"]?.Value);
            if (rd == null) return null;
            string P(string n) => rd.Properties[n]?.Value?.ToString();
            switch (type)
            {
                case "A": return "A " + NormalizeZoneIp(P("IPv4Address"));
                case "AAAA": return "AAAA " + NormalizeZoneIp(P("IPv6Address"));
                case "CNAME": return "CNAME " + NormalizeZoneName(P("HostNameAlias"));
                case "NS": return "NS " + NormalizeZoneName(P("NameServer"));
                case "PTR": return "PTR " + NormalizeZoneName(P("PtrDomainName"));
                case "MX": return $"MX {ParseIntOrDefault(P("Preference"), 0)} {NormalizeZoneName(P("MailExchange"))}";
                case "SRV": return $"SRV {ParseIntOrDefault(P("Priority"), 0)} {ParseIntOrDefault(P("Weight"), 0)} {ParseIntOrDefault(P("Port"), 0)} {NormalizeZoneName(P("DomainName"))}";
                case "TXT": return "TXT " + NormalizeZoneTxt(rd.Properties["DescriptiveText"]?.Value);
                default: return null;
            }
        }

        /// <summary>То же значение, но разобранное из rdata-части строки .dns-файла.</summary>
        private static string ZoneRdataFromFile(string rdata, string type)
        {
            var p = (rdata ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            switch (type)
            {
                case "A": return p.Length >= 1 ? "A " + NormalizeZoneIp(p[0]) : "A ";
                case "AAAA": return p.Length >= 1 ? "AAAA " + NormalizeZoneIp(p[0]) : "AAAA ";
                case "CNAME": return p.Length >= 1 ? "CNAME " + NormalizeZoneName(p[0]) : "CNAME ";
                case "NS": return p.Length >= 1 ? "NS " + NormalizeZoneName(p[0]) : "NS ";
                case "PTR": return p.Length >= 1 ? "PTR " + NormalizeZoneName(p[0]) : "PTR ";
                case "MX": return p.Length >= 2 ? $"MX {ParseIntOrDefault(p[0], 0)} {NormalizeZoneName(p[1])}" : null;
                case "SRV": return p.Length >= 4 ? $"SRV {ParseIntOrDefault(p[0], 0)} {ParseIntOrDefault(p[1], 0)} {ParseIntOrDefault(p[2], 0)} {NormalizeZoneName(p[3])}" : null;
                case "TXT": return "TXT " + NormalizeZoneTxt(rdata);
                default: return null;
            }
        }

        /// <summary>TXT: и объект записи, и строка файла приводятся к склейке содержимого всех кавычечных сегментов без самих кавычек.</summary>
        private static string NormalizeZoneTxt(object value)
        {
            if (value == null) return "";
            IEnumerable<string> parts;
            if (value is string s)
            {
                var segs = new List<string>();
                var sb = new StringBuilder();
                var inQ = false;
                foreach (var c in s)
                {
                    if (c == '"') { if (inQ) { segs.Add(sb.ToString()); sb.Clear(); } inQ = !inQ; }
                    else if (inQ) sb.Append(c);
                }
                if (segs.Count == 0) segs.Add(s.Trim()); // строка без кавычек - как есть
                parts = segs;
            }
            else if (value is System.Collections.IEnumerable en)
            {
                var segs = new List<string>();
                foreach (var o in en) if (o != null) segs.Add(o.ToString());
                parts = segs;
            }
            else parts = new[] { value.ToString() };
            return string.Concat(parts);
        }

        private async Task RefreshRecordsAsync()
        {
            var zoneName = Val(cmbScopeZoneName);
            var scopeName = Val(txtRecordScopeName);
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Перейди к нужному scope в дереве слева (сервер -> зона -> scope).");
                return;
            }

            // Ищем узел этого scope в дереве (сервер -> зона -> scope) - обновляем именно его
            // ветку, остальные узлы (если уже подгружены) не трогаем.
            var scopeNode = FindScopeNode(DnsHelper.ComputerName, zoneName, scopeName);

            if (scopeNode == null)
            {
                AppendLog("Не нашёл этот scope в уже построенном дереве - перейди к нему заново через дерево слева.");
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
            SyncContextToTreeNode(scopeNode); // сервер+зона строго по владельцу этого узла (важно при нескольких серверах)
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
            {
                treeRecordFolders.SelectedNode = tn; // синхронизируем дерево, если навигация пришла не из него
                SyncContextToTreeNode(tn);           // и целевой сервер/зону - тоже по этому узлу
            }
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
                    // [FLDR] вместо эмодзи-папки: обычный ASCII-текст, не суррогатная пара -
                    // не ломает char-литералы/посимвольный разбор при парсинге на импорте,
                    // и не зависит от кодировки, в которой файл потом откроют в блокноте.
                    var count = CountRecordsRecursive(child);
                    var display = $"[FLDR] {child.Label,-26} {count} запис.";
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

            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(scopeName))
            {
                AppendLog("Укажи зону и scope.");
                return;
            }

            // Собираем ВСЕ выделенные строки, которые реально являются записями (не папками) -
            // папки среди выделенного просто пропускаем молча, а не срываем всю операцию.
            var indices = lstRecords.SelectedIndices.Cast<int>()
                .Where(i => i >= 0 && i < _displayedRecords.Count && _displayedRecords[i] != null)
                .ToList();

            if (indices.Count == 0)
            {
                AppendLog("Выбери одну или несколько записей в правом списке (папки при удалении игнорируются).");
                return;
            }

            var records = indices.Select(i => _displayedRecords[i]).ToList();

            var confirmText = records.Count == 1
                ? $"Удалить запись '{records[0].Properties["HostName"]?.Value}' " +
                  $"({records[0].Properties["RecordType"]?.Value}) из scope '{scopeName}'?"
                : $"Удалить {records.Count} записей из scope '{scopeName}'?\n\n" +
                  string.Join("\n", records.Take(10).Select(r => $"  {r.Properties["HostName"]?.Value} ({r.Properties["RecordType"]?.Value})")) +
                  (records.Count > 10 ? $"\n  ...и ещё {records.Count - 10}" : "");

            if (MessageBox.Show(confirmText, "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            int deleted = 0, failed = 0;

            // Записи, которые обычный API удалить отказался именно из-за типа зоны (WIN32 9611,
            // read-only / файловая зона) - их добьём правкой .dns-файла scope напрямую (см.
            // DeleteRecordsFromScopeFileAsync, тот же обходной путь, что и при добавлении).
            var fileFallback = new List<PSObject>();

            // Берём "сырой" объект записи целиком из последнего Get-DnsServerResourceRecord
            // и передаём его в -InputObject - это официальный паттерн удаления конкретной
            // записи (эквивалент "Get-DnsServerResourceRecord ... | Remove-DnsServerResourceRecord").
            // Передавать Name/RRType/RecordData по отдельности ненадёжно: RecordData в объекте
            // записи - это вложенная структура, а не то, что ожидает параметр -RecordData.
            foreach (var record in records)
            {
                var hostName = record.Properties["HostName"]?.Value?.ToString();
                var recordType = record.Properties["RecordType"]?.Value?.ToString();
                var value = DnsHelper.DescribeRecordData(record.Properties["RecordData"]?.Value, recordType);

                var parameters = new Dictionary<string, object>
                {
                    ["ZoneName"] = zoneName,
                    ["ZoneScope"] = scopeName,
                    ["InputObject"] = record,
                    ["Force"] = true
                };

                var (_, delLog) = await Task.Run(() => DnsHelper.Invoke("Remove-DnsServerResourceRecord", parameters));

                if (WasSuccess(delLog))
                {
                    deleted++;
                    AppendLog($"OK: запись \"{hostName}\" ({recordType}) {value} удалена из зоны \"{zoneName}\", scope \"{scopeName}\".");
                    FileLogger.LogChange("RECORD DELETE", zoneName, $"Scope={scopeName} {recordType} {hostName}", true, delLog);
                }
                else if (delLog != null && delLog.IndexOf("9611", StringComparison.Ordinal) >= 0)
                {
                    fileFallback.Add(record);
                    AppendLog($"API отказал в удалении \"{hostName}\" ({recordType}) - WIN32 9611 (файловая/read-only зона), попробуем через файл scope.");
                }
                else
                {
                    failed++;
                    AppendLog($"ОШИБКА при удалении \"{hostName}\" ({recordType}): {delLog}");
                    FileLogger.LogChange("RECORD DELETE", zoneName, $"Scope={scopeName} {recordType} {hostName}", false, delLog);
                }
            }

            if (fileFallback.Count > 0)
            {
                var filePath = Path.Combine(@"C:\Windows\System32\dns", zoneName, scopeName + ".dns");
                var what = fileFallback.Count == 1 ? "запись" : $"записи ({fileFallback.Count})";
                var confirm = MessageBox.Show(
                    $"DNS Server отказал в удалении через API (WIN32 9611: зона файловая / только чтение)." +
                    $"{Environment.NewLine}{Environment.NewLine}Удалить {what} напрямую из файла scope:{Environment.NewLine}{filePath}{Environment.NewLine}" +
                    $"на ЭТОЙ машине (локально, независимо от настройки \"Целевой сервер\" сверху), после чего зона будет перезагружена командой dnscmd /ZoneReload." +
                    $"{Environment.NewLine}{Environment.NewLine}Удалится только строка, точно совпадающая с выбранной записью по имени, типу и значению. " +
                    $"Перед правкой создаётся резервная копия файла. Если однозначного совпадения нет - файл не трогается." +
                    $"{Environment.NewLine}{Environment.NewLine}Продолжить?",
                    "Файловый режим удаления записи", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirm == DialogResult.Yes)
                {
                    var doneViaFile = await DeleteRecordsFromScopeFileAsync(zoneName, scopeName, fileFallback);
                    deleted += doneViaFile;
                    failed += fileFallback.Count - doneViaFile;
                }
                else
                {
                    failed += fileFallback.Count;
                    AppendLog("Файловое удаление отменено пользователем.");
                }
            }

            if (records.Count > 1) AppendLog($"Удаление завершено: успешно {deleted}, ошибок {failed}.");
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

            cmbPolicyServer = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbPolicyServer.SelectedIndexChanged += async (s, e) =>
            {
                if (!_uiReady || _rebuildingPolicyServerCombo) return; // пересборка списка не должна менять контекст
                ApplyPolicyServerContext();
                lstPolicies.Items.Clear();
                _lastPolicies.Clear();
                rtbPolicyDetails.Clear();
                await RefreshPolicyZoneComboAsync();
            };

            cmbPolicyZoneName = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            var btnLoadPolicyZoneNames = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список зон", _toolTip,
                async (s, e) => await RefreshPolicyZoneComboAsync());

            var btnRefresh = IconFactory.CreateButton(IconFactory.Folder(), "Показать политики зоны", _toolTip,
                async (s, e) => await RefreshPoliciesAsync());

            txtPolicyName = Tb(140, "имя политики");
            txtPolicySubnetName = Tb(200, "подсеть(и) через запятую");
            txtPolicyScopeName = Tb(140, "имя scope");
            var btnAdd = IconFactory.CreateButton(IconFactory.Add(), "Создать политику (привязать подсеть к scope)...", _toolTip, async (s, e) =>
            {
                ApplyPolicyServerContext();
                var zoneHint = string.IsNullOrEmpty(Val(cmbPolicyZoneName)) ? "(зона не выбрана)" : Val(cmbPolicyZoneName);
                var subnetNames = await FetchClientSubnetNamesAsync();
                var (name, subnets, scope) = AddPolicyDialog.Show(zoneHint, subnetNames);
                if (name == null) return;
                txtPolicyName.Text = name;
                txtPolicySubnetName.Text = subnets;
                txtPolicyScopeName.Text = scope;
                await AddPolicyAsync();
            });

            var btnRemove = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранную политику", _toolTip,
                async (s, e) => await RemovePolicyAsync());

            var btnDuplicate = IconFactory.CreateButton(IconFactory.Duplicate(), "Дублировать политику на другие scope'ы / зоны...", _toolTip,
                async (s, e) => await DuplicatePolicyAsync());

            lstPolicies.SelectedIndexChanged += (s, e) => ShowPolicyDetails();

            RefreshPolicyServerCombo(); // локальный + уже подключённые удалённые (пополняется по мере подключений)

            var column = Column(
                Row(new Label { Text = "Сервер:", AutoSize = true, Margin = new Padding(4, 8, 4, 2) }, cmbPolicyServer,
                    new Label { Text = "Зона:", AutoSize = true, Margin = new Padding(12, 8, 4, 2) }, cmbPolicyZoneName, btnLoadPolicyZoneNames, btnRefresh,
                    new Label { Text = "  ", AutoSize = true }, btnAdd, btnRemove, btnDuplicate)
            );

            return WrapTabTwoLists("Политики", column,
                "Список политик", lstPolicies,
                "Подробности выбранной политики", rtbPolicyDetails,
                "PoliciesSplitter");
        }

        /// <summary>Список серверов для выпадашки на вкладке "Политики": локальный + все удалённые с успешным подключением в этой сессии.</summary>
        private IEnumerable<string> ConnectedRemoteServers() =>
            _connectedRemoteServers
                .Concat(DnsHelper.ActiveRemoteServers)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Пересобирает выпадашку "Сервер" на вкладке "Политики". НЕ трогает глобальный
        /// DnsHelper.ComputerName (флаг _rebuildingPolicyServerCombo глушит SelectedIndexChanged).
        /// Выбор: если пользователь уже выбрал здесь конкретный удалённый сервер - оставляем его;
        /// иначе следуем за текущим рабочим сервером (тем, что выбран деревом/верхней панелью).
        /// </summary>
        private void RefreshPolicyServerCombo()
        {
            if (cmbPolicyServer == null) return;

            _rebuildingPolicyServerCombo = true;
            try
            {
                var current = (cmbPolicyServer.SelectedItem as PolicyServerItem)?.Server ?? "";
                var want = string.IsNullOrEmpty(current) ? (DnsHelper.ComputerName ?? "").Trim() : current;

                cmbPolicyServer.BeginUpdate();
                cmbPolicyServer.Items.Clear();
                cmbPolicyServer.Items.Add(new PolicyServerItem("", $"(локальный) {LocalServerNodeLabel()}"));
                foreach (var srv in ConnectedRemoteServers())
                    cmbPolicyServer.Items.Add(new PolicyServerItem(srv, srv));
                cmbPolicyServer.EndUpdate();

                var restore = 0;
                for (int i = 0; i < cmbPolicyServer.Items.Count; i++)
                    if (((PolicyServerItem)cmbPolicyServer.Items[i]).Server.Equals(want, StringComparison.OrdinalIgnoreCase)) { restore = i; break; }
                cmbPolicyServer.SelectedIndex = restore;
            }
            finally
            {
                _rebuildingPolicyServerCombo = false;
            }
        }

        /// <summary>Ставит DnsHelper.ComputerName по выбранному на вкладке "Политики" серверу (пусто = локальный). Вызывать только из действий этой вкладки, не из пересборки списка.</summary>
        private void ApplyPolicyServerContext()
        {
            DnsHelper.ComputerName = (cmbPolicyServer?.SelectedItem as PolicyServerItem)?.Server ?? "";
        }

        /// <summary>Заполняет только выпадашку зон вкладки "Политики" - по выбранному там серверу (не трогает список зон вкладки Scopes).</summary>
        private async Task RefreshPolicyZoneComboAsync()
        {
            ApplyPolicyServerContext();
            var names = await FetchScopeCapableZoneNamesAsync();

            var cur = cmbPolicyZoneName.Text;
            cmbPolicyZoneName.Items.Clear();
            foreach (var n in names) cmbPolicyZoneName.Items.Add(n);
            cmbPolicyZoneName.Text = cur;
        }

        /// <summary>Имена клиентских подсетей текущего (для вкладки "Политики") сервера - для пикера в диалоге создания политики.</summary>
        private async Task<List<string>> FetchClientSubnetNamesAsync()
        {
            ApplyPolicyServerContext();
            var (results, _) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerClientSubnet"));
            return results
                .Select(r => r.Properties["Name"]?.Value?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Имена зон (только те, что поддерживают Zone Scopes) текущего для вкладки "Политики" сервера.</summary>
        private async Task<List<string>> FetchScopeCapableZoneNamesAsync()
        {
            ApplyPolicyServerContext();
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZone"));
            AppendLog(log);
            return results
                .Where(IsScopeCapableZone)
                .Select(o => o.Properties["ZoneName"]?.Value?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Имена scope'ов зоны на текущем для вкладки "Политики" сервере.</summary>
        private async Task<List<string>> FetchZoneScopeNamesAsync(string zoneName)
        {
            ApplyPolicyServerContext();
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerZoneScope",
                new Dictionary<string, object> { ["ZoneName"] = zoneName }));
            AppendLog(log);
            var names = DnsHelper.GetStringProperty(results, "ZoneScope");
            if (names.Count == 0) names = DnsHelper.GetStringProperty(results, "Name");
            return names;
        }

        private async Task RefreshPoliciesAsync()
        {
            ApplyPolicyServerContext();
            var zoneName = Val(cmbPolicyZoneName);
            if (string.IsNullOrEmpty(zoneName)) { AppendLog("Укажи имя зоны."); return; }

            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName };
            AppendLog($"Загружаю политики зоны '{zoneName}'...");
            var (results, log) = await Task.Run(() => DnsHelper.Invoke("Get-DnsServerQueryResolutionPolicy", parameters));
            AppendLog(log);

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
                var subnetDisplay = ResolveSubnetNames(subnetRaw);
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

        /// <summary>
        /// ClientSubnet у политики выглядит как "EQ,net_100,Old_DNS_redirect13" - отсекаем
        /// оператор (EQ/NE), оставляем ЧИСТЫЕ имена подсетей без CIDR в скобках. Раньше здесь
        /// рядом с именем подставлялся реальный CIDR - выглядело удобно для чтения, но именно
        /// это "(10.0.1.0/24)" в скобках НЕ часть имени подсети, и при копипасте в поле
        /// "Подсети" диалога создания политики ловилась ошибка ("такой подсети не существует").
        /// CIDR теперь показывается только на вкладке "Подсети", где он и должен быть виден.
        /// </summary>
        private static string ResolveSubnetNames(string rawClientSubnet)
        {
            if (string.IsNullOrEmpty(rawClientSubnet)) return "";

            var tokens = rawClientSubnet.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0 &&
                            !t.Equals("EQ", StringComparison.OrdinalIgnoreCase) &&
                            !t.Equals("NE", StringComparison.OrdinalIgnoreCase));

            return string.Join(", ", tokens);
        }

        private async Task AddPolicyAsync()
        {
            ApplyPolicyServerContext();
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
            ApplyPolicyServerContext();
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

        /// <summary>
        /// Дублирует выбранную политику на другие scope'ы других зон (того же сервера). Диалог
        /// позволяет отметить сразу несколько scope'ов в нескольких зонах; для каждой пары
        /// (зона, scope) создаётся отдельная политика с тем же критерием подсети.
        /// </summary>
        private async Task DuplicatePolicyAsync()
        {
            ApplyPolicyServerContext();
            var srcZone = Val(cmbPolicyZoneName);
            var idx = lstPolicies.SelectedIndex;
            if (string.IsNullOrEmpty(srcZone) || idx < 0 || idx >= _lastPolicies.Count)
            {
                AppendLog("Выбери зону и политику в списке - её и будем дублировать.");
                return;
            }

            var src = _lastPolicies[idx];
            var srcSubnets = (src.SubnetDisplay ?? "")
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            var zones = await FetchScopeCapableZoneNamesAsync();
            var subnets = await FetchClientSubnetNamesAsync();

            var plan = DuplicatePolicyDialog.Show(src.Name, srcZone, srcSubnets, zones, subnets,
                zone => FetchZoneScopeNamesAsync(zone));
            if (plan == null || plan.Targets.Count == 0) return;

            if (plan.Subnets.Count == 0)
            {
                AppendLog("Для политики нужен хотя бы один критерий-подсеть - дублирование отменено.");
                return;
            }

            var clientSubnetValue = "EQ," + string.Join(",", plan.Subnets);
            int ok = 0, fail = 0;

            foreach (var t in plan.Targets)
            {
                // Имя политики уникально В ПРЕДЕЛАХ ЗОНЫ. Если целимся в несколько scope'ов одной
                // зоны (или в ту же зону, что и оригинал) - к базовому имени добавляем "_<scope>",
                // иначе в одной зоне было бы две политики с одинаковым именем.
                var sameZoneCount = plan.Targets.Count(x => x.Zone.Equals(t.Zone, StringComparison.OrdinalIgnoreCase));
                var needSuffix = !plan.KeepExactName
                                 || sameZoneCount > 1
                                 || t.Zone.Equals(srcZone, StringComparison.OrdinalIgnoreCase);
                var newName = needSuffix ? $"{plan.BaseName}_{t.Scope}" : plan.BaseName;

                var parameters = new Dictionary<string, object>
                {
                    ["Name"] = newName,
                    ["Action"] = "ALLOW",
                    ["ZoneName"] = t.Zone,
                    ["ClientSubnet"] = clientSubnetValue,
                    ["ZoneScope"] = $"{t.Scope},1"
                };

                AppendLog($"Дублирую '{src.Name}' -> зона '{t.Zone}', scope '{t.Scope}' как политику '{newName}'...");
                var (_, log) = await Task.Run(() => DnsHelper.Invoke("Add-DnsServerQueryResolutionPolicy", parameters));
                AppendLog(log);
                var success = WasSuccess(log);
                FileLogger.LogChange("POLICY DUPLICATE", t.Zone,
                    $"Policy={newName} (from {srcZone}/{src.Name}) Subnets=[{string.Join(",", plan.Subnets)}] -> Scope={t.Scope}",
                    success, success ? null : log);
                if (success) ok++; else fail++;
            }

            AppendLog($"Дублирование завершено: создано {ok}, ошибок {fail}.");
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

        /// <summary>Имя сервера, с которого реально сделан запрос - для заголовка экспорта. Пусто в DnsHelper.ComputerName = локальная машина.</summary>
        private static string CurrentServerLabel() =>
            string.IsNullOrWhiteSpace(DnsHelper.ComputerName) ? Environment.MachineName : DnsHelper.ComputerName;

        /// <summary>Убирает из строки символы, недопустимые в имени файла (сервер может быть указан как угодно, но в имя файла попадёт как есть).</summary>
        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }

        /// <summary>
        /// Экспортирует список строк (то, что сейчас отображено в списке - с учётом
        /// применённых фильтра и сортировки) в текстовый файл. Путь выбирается диалогом
        /// сохранения - явно, как и просили, а не в жёстко зашитое место.
        /// </summary>
        /// <param name="headerLine">
        /// Необязательная строка-заголовок (дата + сервер, откуда выгрузка) - пишется первой
        /// строкой файла с префиксом "#", чтобы при последующем импорте её можно было
        /// однозначно отличить от настоящих строк с записями и просто пропустить.
        /// </param>
        private void ExportListToFile(IEnumerable<string> lines, string suggestedFileName, string headerLine = null)
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
                var output = new List<string>();
                if (!string.IsNullOrEmpty(headerLine)) output.Add("# " + headerLine);
                output.AddRange(linesList);
                File.WriteAllLines(dlg.FileName, output);
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
            if (string.IsNullOrEmpty(text) || txtOutput == null) return;

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

        /// <summary>
        /// Пишет в "Вывод" одну строку, собранную из фрагментов с разным начертанием
        /// (имена зон/серверов - жирным и подчёркнутым) - для понятных пояснений вместо
        /// сырого текста ошибки CIM/WinRM. Тайминг-префикс - как у обычного AppendLog.
        /// </summary>
        private void AppendLogStyled(params (string Text, bool Bold, bool Underline)[] parts)
        {
            txtOutput.SelectionStart = txtOutput.TextLength;
            txtOutput.SelectionLength = 0;
            txtOutput.SelectionColor = Color.DimGray;
            txtOutput.SelectionFont = new Font(txtOutput.Font, FontStyle.Regular);
            txtOutput.AppendText($"[{DateTime.Now:HH:mm:ss}] ");

            foreach (var (text, bold, underline) in parts)
            {
                var style = FontStyle.Regular;
                if (bold) style |= FontStyle.Bold;
                if (underline) style |= FontStyle.Underline;
                txtOutput.SelectionFont = new Font(txtOutput.Font, style);
                txtOutput.SelectionColor = Color.DimGray;
                txtOutput.AppendText(text);
            }

            txtOutput.SelectionFont = new Font(txtOutput.Font, FontStyle.Regular);
            txtOutput.SelectionColor = txtOutput.ForeColor;
            txtOutput.AppendText(Environment.NewLine);
            txtOutput.ScrollToCaret();
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
