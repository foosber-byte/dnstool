using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DnsToolWinForms.Services;
using static DnsToolWinForms.Services.DnsRecordService;

namespace DnsToolWinForms.Controls
{
    /// <summary>
    /// Вкладка "Scopes и записи": дерево серверы->зоны->scopes->папки, список записей с
    /// фильтром/сортировкой, управление зонами и scopes, добавление/редактирование/удаление
    /// записей (включая файловый обходной путь для Secondary-зон) и импорт из файла.
    /// Перенесено из MainForm; чистая логика - в Services (DnsZoneService, DnsScopeService,
    /// DnsRecordService).
    /// </summary>
    public sealed class ScopesTabControl : UserControl
    {
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

        private readonly AppLog _log;
        private readonly ServerContext _serverContext;
        private readonly IDnsCommandRunner _runner;
        private readonly DnsZoneService _zoneService;
        private readonly DnsScopeService _scopeService;
        private readonly ToolTip _toolTip;

        // MainForm оркестрирует общий список зон (один запрос на вкладки Scopes и Политики)
        // и хранит список успешно подключённых серверов - контрол получает это колбэками.
        private readonly Func<Task> _refreshZoneNamesAsync;
        private readonly Action<string> _serverConnected;

        public ScopesTabControl(AppLog log, ServerContext serverContext, IDnsCommandRunner runner,
            ToolTip toolTip, Func<Task> refreshZoneNamesAsync, Action<string> serverConnected)
        {
            _log = log;
            _serverContext = serverContext;
            _runner = runner;
            _zoneService = new DnsZoneService(runner);
            _scopeService = new DnsScopeService(runner);
            _toolTip = toolTip;
            _refreshZoneNamesAsync = refreshZoneNamesAsync;
            _serverConnected = serverConnected;
            BuildUi();
        }

        /// <summary>Пустое ли дерево (условие ленивой инициализации при активации вкладки).</summary>
        public bool TreeIsEmpty => treeRecordFolders.Nodes.Count == 0;

        /// <summary>Полностью пересобирает дерево с нуля (бывший InitializeServerTree из MainForm).</summary>
        public void InitializeTree() => InitializeServerTree();

        /// <summary>Гарантирует наличие узла-сервера в дереве (бывший AddServerRootIfMissing из MainForm).</summary>
        public TreeNode EnsureServerNode(string serverName) => AddServerRootIfMissing(serverName);

        /// <summary>Заполняет выпадашку зон готовым списком имён (общий запрос делает MainForm).</summary>
        public void SetZoneNames(IReadOnlyList<string> names)
        {
            var current = cmbScopeZoneName.Text;
            cmbScopeZoneName.Items.Clear();
            foreach (var name in names) cmbScopeZoneName.Items.Add(name);
            cmbScopeZoneName.Text = current; // не затираем то, что человек уже успел ввести/выбрать вручную
        }

        // ---- маленькие хелперы (те же имена, что были в MainForm, - перенесённый код ниже не менялся) ----

        private static FlowLayoutPanel Row(params Control[] controls) => Ui.Row(controls);

        private static FlowLayoutPanel Column(params Control[] rows) => Ui.Column(rows);

        private static TextBox Tb(int width = 200, string placeholderText = null) => Ui.Tb(width, placeholderText);

        private static string Val(TextBox t) => Ui.Val(t);

        private static string Val(ComboBox c) => Ui.Val(c);

        private static void SetPlaceholder(TextBox t, string newPlaceholder) => Ui.SetPlaceholder(t, newPlaceholder);

        private static bool WasSuccess(string log) => DnsOutcome.WasSuccess(log);

        private string CurrentServerLabel() => _serverContext.DisplayLabel;

        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }

        private void ExportListToFile(IEnumerable<string> lines, string suggestedFileName, string headerLine = null)
            => ListExporter.Export(_log, lines, suggestedFileName, headerLine);

        private void AppendLog(string text) => _log?.AppendLog(text);

        private void AppendLogStyled(params (string Text, bool Bold, bool Underline)[] parts)
            => _log?.AppendLogStyled(parts);

        private void AppendColoredLine(string line, Color color) => _log?.AppendColoredLine(line, color);
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
        //  Построение вкладки (бывший BuildScopesTab из MainForm)
        // ============================================================

        private void BuildUi()
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
                async (s, e) => await _refreshZoneNamesAsync());
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

            // Отдельный, явно подписанный "аварийный" путь для Secondary/read-only зон, где
            // обычный API пишет отказ (WIN32 9611) - правит .dns-файл scope напрямую на ЭТОЙ
            // машине и перезагружает зону через dnscmd. См. AddRecordToScopeFileAsync().
            //
            // ВАЖНО, проверено практически: у Primary AD-интегрированной зоны named Zone Scope
            // ВСЁ ЖЕ реплицируется через AD (записи внутри scope появлялись на другом DC после
            // добавления через обычный API) - ранний комментарий здесь ошибочно утверждал
            // обратное. Файл "<scope>.<zone>.dns" в System32\dns у такой зоны - локальный
            // кэш/бэкап, не источник истины: правка этого файла в обход API у AD-интегрированной
            // зоны либо не подхватится, либо создаст рассинхрон между этим сервером и AD.
            // Файловый режим имеет смысл только для настоящих Secondary/read-only зон, где
            // .dns-файл - единственное, что реально есть на этой машине.
            var btnAddRecordFile = IconFactory.CreateButton(IconFactory.Notepad(), "Добавить запись в файл (обходной путь для Secondary-зон)...", _toolTip, async (s, e) =>
            {
                var confirmInfo = MessageBox.Show(
                    "Это обходной путь для Secondary/read-only зон: строка будет дописана НАПРЯМУЮ " +
                    "в .dns-файл scope на этой машине, в обход обычного API. Используй, только если " +
                    "обычная \"Добавить запись\" отказывает с ошибкой \"Недопустимый тип зоны DNS\" " +
                    "(WIN32 9611) И зона реально Secondary/read-only.\n\n" +
                    "НЕ используй для AD-интегрированной зоны: там named Zone Scope реплицируется " +
                    "через AD, а .dns-файл на диске - лишь локальный кэш, правка которого напрямую " +
                    "не попадёт в AD и может рассинхронизироваться с другими контроллерами.\n\nПродолжить?",
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

            Controls.Add(Ui.TwoListsLayout(column,
                "Серверы / зоны / scope'ы", treeRecordFolders,
                "Записи выбранной папки", recordsWrapper,
                "ScopesRecordsSplitter"));
        }

        // ============================================================
        //  Управление зонами (часть этой вкладки)
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

            List<PSObject> results;
            using (_serverContext.BeginTemporaryServer(serverName)) // именно для ЭТОГО запроса, чей бы узел ни был выбран
            {
                var (r, _) = await _scopeService.GetZoneAsync(zoneName);
                results = r;
            } // возвращаем как было - сам факт запроса источника не должен молча менять текущий сервер

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
            var serverName = _serverContext.CurrentServer;
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

            string replicationScope = null;
            string zoneFile = null;
            string kindLabel;

            switch (cmbZoneType.SelectedIndex)
            {
                case 1: // AD, реплика на весь лес
                    replicationScope = "Forest";
                    kindLabel = "AD (Forest)";
                    break;
                case 2: // Файловая - AD ничего не знает про неё, всё хранится в .dns на диске
                    zoneFile = zoneName + ".dns";
                    kindLabel = "файловая";
                    break;
                default: // AD, реплика на домен (значение по умолчанию, как раньше)
                    replicationScope = "Domain";
                    kindLabel = "AD (Domain)";
                    break;
            }

            AppendLog($"Создаю первичную зону '{zoneName}' ({kindLabel})...");
            var (success, log) = await _scopeService.AddPrimaryZoneAsync(zoneName, replicationScope, zoneFile);
            AppendLog(log);
            FileLogger.LogChange("ZONE ADD", zoneName, $"Тип={kindLabel}", success, log);

            // Обновляем список зон именно ТЕКУЩЕГО сервера (того, что сейчас в панели подключения) - не всё дерево.
            var serverName = _serverContext.CurrentServer;
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

            AppendLog($"Удаляю зону '{zoneName}'...");
            var (zoneRemoved, log) = await _scopeService.RemoveZoneAsync(zoneName);
            AppendLog(log);
            FileLogger.LogChange("ZONE DELETE", zoneName, "-", zoneRemoved, log);

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
        /// серверах _serverContext.CurrentServer - один на всё приложение, и без этой синхронизации
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
        /// Переводит целевой сервер на указанный (пусто = локальный) - используется навигацией
        /// по дереву. Верхняя панель "Целевой DNS-сервер" в MainForm подписана на
        /// ServerContext.CurrentServerChanged и синхронизируется сама (галочка/поле сервера).
        /// </summary>
        private void SetCurrentServerContext(string serverName)
        {
            _serverContext.Set(serverName);
        }

        /// <summary>Служебная авто-зона (TrustAnchors, корневые подсказки и т.п.) - реализация в DnsZoneService.</summary>
        private static bool IsServiceAutoZone(PSObject z, string zoneName) => DnsZoneService.IsServiceAutoZone(z, zoneName);

        /// <summary>Ленивая подгрузка: зоны конкретного узла-сервера (вызывается при первом выборе/раскрытии).</summary>
        private async Task LoadServerZonesIntoTreeAsync(TreeNode serverNode, string serverName)
        {
            SetCurrentServerContext(serverName);

            var label = string.IsNullOrEmpty(serverName) ? "локальный" : serverName;
            AppendLog($"Загружаю зоны сервера '{label}'...");
            var (results, log) = await _scopeService.GetAllZonesAsync();
            AppendLog(log);

            serverNode.Nodes.Clear();
            _loadedServerTreeNodes.Add(serverNode);
            if (!WasSuccess(log)) return; // причина уже понятно объяснена в логе (не DNS-сервер / нет прав / WinRM и т.п.)

            if (!string.IsNullOrEmpty(serverName))
                _serverConnected?.Invoke(serverName); // новый удалённый сервер стал доступен - сообщаем MainForm (история + выпадашка "Политики")

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
            var (scopeNames, log) = await _zoneService.GetZoneScopeNamesAsync(zoneName);

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

            var serverName = _serverContext.CurrentServer;
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

            AppendLog($"Создаю scope '{scopeName}' в зоне '{zoneName}'...");
            var (scopeAdded, log) = await _scopeService.AddScopeAsync(zoneName, scopeName);
            AppendLog(log);
            FileLogger.LogChange("SCOPE ADD", zoneName, $"Scope={scopeName}", scopeAdded, log);
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

            AppendLog($"Удаляю scope '{scopeName}'...");
            var (scopeRemoved, log) = await _scopeService.RemoveScopeAsync(zoneName, scopeName);
            AppendLog(log);
            FileLogger.LogChange("SCOPE DELETE", zoneName, $"Scope={scopeName}", scopeRemoved, log);
            await RefreshCurrentZoneScopesAsync();
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
        ///
        /// Абсолютный FQDN (хвостовая точка, см. NormalizeRecordName) - самостоятельное имя,
        /// не привязанное к текущей зоне, и тем более не к текущей папке внутри неё: приклеивать
        /// к нему суффикс папки через точку нельзя (даёт синтаксически невалидное имя вида
        /// "ssheiee0j1.a.trbcdn.net..world" - WIN32 123, "синтаксическая ошибка в имени файла").
        /// </summary>
        private string ApplyFolderPrefix(string enteredName)
        {
            if (!string.IsNullOrEmpty(enteredName) && enteredName.EndsWith(".", StringComparison.Ordinal))
                return enteredName; // абсолютный FQDN - папка тут ни при чём

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
            var (_, log) = await Task.Run(() => _runner.Invoke(cmdlet, parameters));
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
            var (_, log) = await Task.Run(() => _runner.Invoke(cmdlet, parameters));
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
                var (_, folderLog) = await Task.Run(() => _runner.Invoke(folderCmdlet, folderParams));
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
            var (existingResults, existingLog) = await Task.Run(() => _runner.Invoke("Get-DnsServerResourceRecord", existingParams));
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
                    var (_, delLog) = await Task.Run(() => _runner.Invoke("Remove-DnsServerResourceRecord", delParams));
                    if (!WasSuccess(delLog))
                    {
                        AppendLog($"ОШИБКА при удалении старой записи '{rec.Name}' перед перезаписью: {delLog}");
                        failed++;
                        continue;
                    }
                    knownRecords.Remove(recordKey); // старой больше нет - если добавление ниже не удастся, конфликта на неё уже не будет
                }

                var (addCmdlet, addParams) = BuildAddRecordCommand(zoneName, scopeName, rec.Type, rec.Name, value, priority, weight, port);
                var (_, addLog) = await Task.Run(() => _runner.Invoke(addCmdlet, addParams));

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
                    var (refetched, _) = await Task.Run(() => _runner.Invoke("Get-DnsServerResourceRecord", refetchParams));
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
        /// Обходной путь для случаев, когда обычный API (Add-DnsServerResourceRecord*)
        /// отказывает с WIN32 9611 "Недопустимый тип зоны DNS" - у настоящих Secondary/read-only
        /// зон, где .dns-файл на диске - единственное, что реально есть на этой машине.
        ///
        /// ВАЖНО, проверено практически: у Primary AD-интегрированной зоны named Zone Scope
        /// ВСЁ ЖЕ реплицируется через AD (запись, добавленная через обычный API, появилась на
        /// другом контроллере домена) - файл "<scope>.<zone>.dns" в System32\dns у такой зоны
        /// (лежит ПЛОСКО, без подпапки по имени зоны, создаётся самим DNS Server'ом автоматически
        /// при создании scope, не приложением) - локальный кэш/бэкап, не источник истины. Правка
        /// этого файла в обход API у AD-интегрированной зоны либо не подхватится, либо создаст
        /// рассинхрон между этим сервером и AD - НЕ используй этот путь для такой зоны, только
        /// для настоящих Secondary/read-only. Reload зоны просто перечитывает файл с диска и
        /// проверку API не делает - поэтому у Secondary-зон правим файл напрямую и просим DNS
        /// Server перечитать зону.
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

            // Файл scope лежит ПЛОСКО прямо в System32\dns, без подпапки по имени зоны -
            // имя файла "<scope>.<zone>.dns" (например "nat.delo-group.ru.dns"), не
            // "<zone>\<scope>.dns" (проверено на реальном сервере: подпапки с именем зоны
            // для scope-файлов не существует, туда попадают отдельные .dns файлы САМИХ зон).
            var filePath = Path.Combine(@"C:\Windows\System32\dns", $"{scopeName}.{zoneName}.dns");

            var confirm = MessageBox.Show(
                "Это обходной путь для Secondary/read-only зон: строка допишется НАПРЯМУЮ в файл" +
                $"{Environment.NewLine}{filePath}{Environment.NewLine}" +
                "на ЭТОЙ машине (локально, независимо от настройки \"Целевой сервер\" сверху), " +
                "после чего зона будет перезагружена командой dnscmd /ZoneReload." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "Это в обход обычных проверок DNS Server API - используй, только если точно " +
                "понимаешь, что делаешь, и зона реально Secondary/read-only (см. README)." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "НЕ используй для AD-интегрированной зоны: там named Zone Scope реплицируется через " +
                "AD, а .dns-файл на диске - лишь локальный кэш, правка которого напрямую не попадёт " +
                "в AD и может рассинхронизироваться с другими контроллерами." +
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

        /// <summary>
        /// Обходной путь для удаления записи из scope-файла, когда Remove-DnsServerResourceRecord
        /// отказывает с WIN32 9611 - только для настоящих Secondary/read-only зон, где .dns-файл
        /// на диске - единственное, что реально есть на этой машине. НЕ используй для
        /// AD-интегрированной зоны: там named Zone Scope реплицируется через AD, а файл на диске -
        /// локальный кэш/бэкап, не источник истины (см. комментарий у AddRecordToScopeFileAsync).
        /// Зеркало AddRecordToScopeFileAsync: правим .dns-файл scope напрямую и просим DNS Server
        /// перечитать зону. ВСЕГДА локально (файл лежит на этой машине).
        ///
        /// Из файла удаляется ТОЛЬКО строка(и), однозначно совпадающая с выбранной записью
        /// по имени + типу + значению. Если совпадение не одно (не найдено или найдено
        /// несколько) - файл не трогается вообще, чтобы случайно не снести чужую запись.
        /// Возвращает число реально удалённых записей.
        /// </summary>
        private async Task<int> DeleteRecordsFromScopeFileAsync(string zoneName, string scopeName, List<PSObject> records)
        {
            // См. комментарий у AddRecordToScopeFileAsync - файл scope лежит ПЛОСКО прямо в
            // System32\dns, имя "<scope>.<zone>.dns", без подпапки по имени зоны.
            var filePath = Path.Combine(@"C:\Windows\System32\dns", $"{scopeName}.{zoneName}.dns");
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
            var scopeNode = FindScopeNode(_serverContext.CurrentServer, zoneName, scopeName);

            if (scopeNode == null)
            {
                AppendLog("Не нашёл этот scope в уже построенном дереве - перейди к нему заново через дерево слева.");
                return;
            }

            await LoadScopeIntoTreeAsync(scopeNode, scopeName);
            scopeNode.Expand();
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
            var (results, log) = await Task.Run(() => _runner.Invoke("Get-DnsServerResourceRecord", parameters));
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

                var (_, delLog) = await Task.Run(() => _runner.Invoke("Remove-DnsServerResourceRecord", parameters));

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
                // См. комментарий у AddRecordToScopeFileAsync - файл scope лежит ПЛОСКО прямо в
                // System32\dns, имя "<scope>.<zone>.dns", без подпапки по имени зоны.
                var filePath = Path.Combine(@"C:\Windows\System32\dns", $"{scopeName}.{zoneName}.dns");
                var what = fileFallback.Count == 1 ? "запись" : $"записи ({fileFallback.Count})";
                var confirm = MessageBox.Show(
                    $"DNS Server отказал в удалении через API (WIN32 9611: зона файловая / только чтение)." +
                    $"{Environment.NewLine}{Environment.NewLine}Удалить {what} напрямую из файла scope:{Environment.NewLine}{filePath}{Environment.NewLine}" +
                    $"на ЭТОЙ машине (локально, независимо от настройки \"Целевой сервер\" сверху), после чего зона будет перезагружена командой dnscmd /ZoneReload." +
                    $"{Environment.NewLine}{Environment.NewLine}Удалится только строка, точно совпадающая с выбранной записью по имени, типу и значению. " +
                    $"Перед правкой создаётся резервная копия файла. Если однозначного совпадения нет - файл не трогается." +
                    $"{Environment.NewLine}{Environment.NewLine}НЕ используй для AD-интегрированной зоны: там named Zone Scope реплицируется через AD, " +
                    $"а .dns-файл на диске - лишь локальный кэш, правка которого напрямую не попадёт в AD и может рассинхронизироваться с другими контроллерами." +
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
            var (_, addLog) = await Task.Run(() => _runner.Invoke(addCmdlet, addParameters));
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
            var (_, removeLog) = await Task.Run(() => _runner.Invoke("Remove-DnsServerResourceRecord", removeParameters));
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
            var (_, removeLog) = await Task.Run(() => _runner.Invoke("Remove-DnsServerResourceRecord", removeParameters));
            AppendLog(removeLog);

            if (!WasSuccess(removeLog))
            {
                AppendLog("Не удалось удалить старую запись - изменения не сохранены, старая запись осталась как была.");
                FileLogger.LogChange("RECORD EDIT", zoneName,
                    $"Scope={scopeName} {oldType} {oldName} -> {edited.Type} {edited.Name}={edited.Value} (ОТМЕНЕНО: ошибка удаления старой)", false, removeLog);
                return;
            }

            AppendLog($"Старая запись удалена, добавляю новую ({edited.Type}, {edited.Value})...");
            var (_, addLog) = await Task.Run(() => _runner.Invoke(addCmdlet, addParameters));
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
            var (_, rollbackLog) = await Task.Run(() => _runner.Invoke(rollbackCmdlet, rollbackParameters));
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

            RecordCheckDialog.Show(fqdn, _serverContext.CurrentServer);
        }
    }
}
