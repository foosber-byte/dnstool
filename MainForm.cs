using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DnsToolWinForms.Controls;
using DnsToolWinForms.Services;

namespace DnsToolWinForms
{
    public partial class MainForm : Form
    {
        // ---- целевой сервер (глобально, для всех вкладок) ----
        private CheckBox chkLocalServer;
        private readonly ToolTip _toolTip = new ToolTip();
        private ComboBox cmbTargetServer;
        private readonly ServerContext _serverContext = new ServerContext(DnsCommandRunner.Instance);
        private AppLog _log; // создаётся в конструкторе после InitializeComponent - txtOutput уже существует

        // ---- общий блок вывода ----
        private RichTextBox txtOutput;
        private TabControl tabs; // нужен, чтобы программно переключать вкладку (напр. двойной клик по зоне -> вкладка Scopes)


        // ---- вкладка "Scopes и записи": UI и логика - в Controls/ScopesTabControl.cs,
        //      чистая логика записей/файлов зоны - в Services/DnsRecordService.cs ----
        private ScopesTabControl _scopesTab;
        private readonly DnsZoneService _zoneService = new DnsZoneService(DnsCommandRunner.Instance);

        // ---- вкладка "Подсети": целиком в Controls/SubnetsTabControl.cs ----

        // ---- вкладка "Политики": целиком в Controls/PoliciesTabControl.cs ----
        private PoliciesTabControl _policiesTab;

        // Удалённые серверы, к которым в ТЕКУЩЕЙ сессии приложения было успешное подключение
        // (успешно загрузились зоны либо прошла явная авторизация). Источник для выпадашки
        // "Сервер" на вкладке "Политики".
        private readonly HashSet<string> _connectedRemoteServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Разовая (за запуск) фоновая проверка новой версии - чтобы не дёргать GitHub повторно,
        // если Shown сработает ещё раз (форма прячется/показывается).
        private bool _startupUpdateCheckDone;

        public MainForm()
        {
            // Лог создаётся ДО InitializeComponent: вкладки (SubnetsTabControl и др.) строятся
            // внутри него и должны получить готовый AppLog. Сам RichTextBox подключается после.
            _log = new AppLog(OfferAdminRelaunch);
            InitializeComponent();
            _log.SetOutput(txtOutput);

            // Дерево вкладки "Scopes и записи" переводит целевой сервер через ServerContext -
            // подписываемся, чтобы верхняя панель всегда отражала актуальный сервер.
            _serverContext.CurrentServerChanged += (s, e) => SyncTargetPanelFromContext();

            Shown += async (s, e) =>
            {
                // Вкладка "Scopes и записи" (со встроенным теперь управлением зонами) открыта
                // по умолчанию при старте - SelectedIndexChanged для неё не сработает
                // (переключения не было), поэтому инициализируем дерево явно здесь же.
                _scopesTab.InitializeTree();
                await RefreshAllZoneCombosAsync(); // держим внутренний список зон наполненным - используется в подсказках диалогов создания
                _policiesTab.RefreshServerCombo();

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
                    case 0 when _scopesTab.TreeIsEmpty:
                        _scopesTab.InitializeTree();
                        await RefreshAllZoneCombosAsync();
                        break;
                    case 2:
                        await _policiesTab.OnTabActivatedAsync(); // обновить список серверов; зоны грузим только при первом заходе
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
            _serverContext.Set(chkLocalServer.Checked ? "" : cmbTargetServer.Text.Trim());
        }

        /// <summary>
        /// Синхронизирует верхнюю панель "Целевой DNS-сервер" с текущим контекстом (смена
        /// сервера из дерева вкладки "Scopes и записи"). Переиспользует существующие обработчики
        /// CheckedChanged/TextChanged - они сами обновят контекст по цепочке.
        /// </summary>
        private void SyncTargetPanelFromContext()
        {
            var server = _serverContext.CurrentServer;
            if (string.IsNullOrEmpty(server))
            {
                chkLocalServer.Checked = true;
            }
            else
            {
                chkLocalServer.Checked = false;
                cmbTargetServer.Text = server;
            }
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
                _scopesTab.EnsureServerNode(""); // на случай, если это первое обращение к дереву вообще - гарантируем, что "Локальный" тоже на месте
                _scopesTab.EnsureServerNode(target); // появляется в дереве слева на вкладке "Scopes и записи", тем же принципом, что и локальный
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
                        _scopesTab.EnsureServerNode("");
                        _scopesTab.EnsureServerNode(target);
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
                _scopesTab.EnsureServerNode(""); // та же подстраховка - гарантируем "Локальный" на месте
                _scopesTab.EnsureServerNode(target); // тот же принцип - сервер появляется в дереве после успешной авторизации
                MarkRemoteConnected(target);
            }
        }

        /// <summary>Запоминает удалённый сервер как успешно подключённый в этой сессии и обновляет выпадашку "Сервер" на вкладке "Политики".</summary>
        private void MarkRemoteConnected(string server)
        {
            if (!string.IsNullOrWhiteSpace(server) && _connectedRemoteServers.Add(server.Trim()))
                _policiesTab?.RefreshServerCombo();
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

        /// <summary>Один запрос Get-DnsServerZone - заполняет выпадашки зон сразу на двух вкладках (Scopes и Политики).</summary>
        private async Task RefreshAllZoneCombosAsync()
        {
            // Только зоны, к которым применимы Zone Scopes - условная пересылка/stub и
            // служебные авто-зоны в подсказки Scopes/Политик не нужны.
            var (names, log) = await _zoneService.GetScopeCapableZoneNamesAsync();
            AppendLog(log);

            // Каждая вкладка заполняет свою выпадашку с той же семантикой "не затирать введённое".
            _scopesTab?.SetZoneNames(names);
            _policiesTab?.SetZoneNames(names);
        }

        private TabPage BuildScopesTab()
        {
            // Вся вкладка (дерево, записи, зоны, scopes, файловый режим, импорт) - в
            // Controls/ScopesTabControl.cs; чистая логика записей - в Services/DnsRecordService.cs.
            // Общий список зон (один запрос на обе вкладки) и список подключённых серверов
            // остаются в MainForm и передаются контролу колбэками.
            var page = new TabPage("Scopes и записи") { Padding = new Padding(10) };
            _scopesTab = new ScopesTabControl(_log, _serverContext, DnsCommandRunner.Instance, _toolTip,
                RefreshAllZoneCombosAsync, MarkRemoteConnected)
            {
                Dock = DockStyle.Fill
            };
            page.Controls.Add(_scopesTab);
            return page;
        }


        // ============================================================
        //  Вкладка "Подсети" (Client Subnets)
        // ============================================================

        private TabPage BuildSubnetsTab()
        {
            // Вся вкладка (список + кнопки + операции) - в Controls/SubnetsTabControl.cs,
            // работа с сервером - в Services/DnsSubnetService.cs.
            var page = new TabPage("Подсети") { Padding = new Padding(10) };
            var control = new SubnetsTabControl(_log, DnsCommandRunner.Instance, _toolTip) { Dock = DockStyle.Fill };
            page.Controls.Add(control);
            return page;
        }

        // ============================================================
        //  Вкладка "Политики" (Query Resolution Policies)
        // ============================================================

        private TabPage BuildPoliciesTab()
        {
            // Вся вкладка (список + подробности + сервер/зона + операции) - в
            // Controls/PoliciesTabControl.cs, выборки имён - в Services/DnsZoneService.cs.
            // Список успешно подключённых удалённых серверов остаётся в MainForm и передаётся
            // контролу делегатом - чтобы контрол не лез в чужое состояние.
            var page = new TabPage("Политики") { Padding = new Padding(10) };
            _policiesTab = new PoliciesTabControl(_log, _serverContext, DnsCommandRunner.Instance, _toolTip, ConnectedRemoteServers)
            {
                Dock = DockStyle.Fill
            };
            page.Controls.Add(_policiesTab);
            return page;
        }

        /// <summary>Список серверов для выпадашки на вкладке "Политики": локальный + все удалённые с успешным подключением в этой сессии.</summary>
        private IEnumerable<string> ConnectedRemoteServers() =>
            _connectedRemoteServers
                .Concat(DnsHelper.ActiveRemoteServers)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

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
        /// одинаково решать, как писать в файл лога изменений. Реализация - Services/DnsOutcome.cs.
        /// </summary>
        private static bool WasSuccess(string log) => DnsOutcome.WasSuccess(log);

        private void AppendLog(string text)
        {
            // Реализация (разбивка на строки, покраска, перехват подсказки про права
            // администратора) - в Services/AppLog.cs, обёртка осталась, чтобы не менять
            // сотни точек вызова и передавать AppendLog как колбэк (RemoteConnectDiagnostics).
            _log?.AppendLog(text);
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
    }
}
