using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DnsToolWinForms.Services;

namespace DnsToolWinForms.Controls
{
    /// <summary>
    /// Вкладка "Политики" (Query Resolution Policies): список политик зоны + подробности,
    /// создание/удаление/дублирование политик. Перенесено из MainForm (BuildPoliciesTab и
    /// методы RefreshPolicyServerCombo..DuplicatePolicyAsync); выборки имён зон/scope'ов/
    /// подсетей - в DnsZoneService, операции с политиками - в DnsPolicyService.
    /// </summary>
    public sealed class PoliciesTabControl : UserControl
    {
        /// <summary>Элемент выпадашки "Сервер": Server = "" означает локальный компьютер.</summary>
        private sealed class PolicyServerItem
        {
            public readonly string Server;
            private readonly string _label;
            public PolicyServerItem(string server, string label) { Server = server; _label = label; }
            public override string ToString() => _label;
        }

        private readonly AppLog _log;
        private readonly ServerContext _serverContext;
        private readonly ToolTip _toolTip;
        private readonly DnsZoneService _zoneService;
        private readonly DnsPolicyService _policyService;
        private readonly Func<IEnumerable<string>> _connectedServersProvider;

        private readonly ComboBox cmbPolicyServer;
        private readonly ComboBox cmbPolicyZoneName;
        private readonly ListBox lstPolicies = new ListBox();
        private readonly RichTextBox rtbPolicyDetails;
        private readonly TextBox txtPolicyName;
        private readonly TextBox txtPolicySubnetName;
        private readonly TextBox txtPolicyScopeName;
        private List<DnsPolicy> _lastPolicies = new List<DnsPolicy>(); // 1:1 с элементами lstPolicies

        // true, пока RefreshServerCombo() программно пересобирает выпадашку "Сервер". Нужно,
        // чтобы её SelectedIndexChanged НЕ трогал глобальный контекст сервера во время
        // пересборки (иначе перезатирал контекст, выбранный деревом/верхней панелью - именно
        // из-за этого "авторизация ОК, а список по кругу").
        private bool _rebuildingServerCombo;

        // Пока false - обработчики UI, которые дёргают сервер, не должны срабатывать:
        // часть контролов ещё строится, лог может быть не подключён. Выставляется в Load.
        private bool _uiReady;

        public PoliciesTabControl(AppLog log, ServerContext serverContext, IDnsCommandRunner runner,
            ToolTip toolTip, Func<IEnumerable<string>> connectedServersProvider)
        {
            _log = log;
            _serverContext = serverContext;
            _zoneService = new DnsZoneService(runner);
            _policyService = new DnsPolicyService(runner);
            _toolTip = toolTip;
            _connectedServersProvider = connectedServersProvider;

            rtbPolicyDetails = new RichTextBox
            {
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White
            };

            cmbPolicyServer = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbPolicyServer.SelectedIndexChanged += async (s, e) =>
            {
                if (!_uiReady || _rebuildingServerCombo) return; // пересборка списка не должна менять контекст
                ApplyServerContext();
                lstPolicies.Items.Clear();
                _lastPolicies.Clear();
                rtbPolicyDetails.Clear();
                await RefreshZoneComboAsync();
            };

            cmbPolicyZoneName = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            txtPolicyName = Ui.Tb(140, "имя политики");
            txtPolicySubnetName = Ui.Tb(200, "подсеть(и) через запятую");
            txtPolicyScopeName = Ui.Tb(140, "имя scope");

            lstPolicies.SelectedIndexChanged += (s, e) => ShowPolicyDetails();

            BuildUi();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _uiReady = true;
        }

        private void BuildUi()
        {
            var btnLoadPolicyZoneNames = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список зон", _toolTip,
                async (s, e) => await RefreshZoneComboAsync());

            var btnRefresh = IconFactory.CreateButton(IconFactory.Folder(), "Показать политики зоны", _toolTip,
                async (s, e) => await RefreshPoliciesAsync());

            var btnAdd = IconFactory.CreateButton(IconFactory.Add(), "Создать политику (привязать подсеть к scope)...", _toolTip, async (s, e) =>
            {
                ApplyServerContext();
                var zoneHint = string.IsNullOrEmpty(Ui.Val(cmbPolicyZoneName)) ? "(зона не выбрана)" : Ui.Val(cmbPolicyZoneName);
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

            RefreshServerCombo(); // локальный + уже подключённые удалённые (пополняется по мере подключений)

            var column = Ui.Column(
                Ui.Row(new Label { Text = "Сервер:", AutoSize = true, Margin = new Padding(4, 8, 4, 2) }, cmbPolicyServer,
                    new Label { Text = "Зона:", AutoSize = true, Margin = new Padding(12, 8, 4, 2) }, cmbPolicyZoneName, btnLoadPolicyZoneNames, btnRefresh,
                    new Label { Text = "  ", AutoSize = true }, btnAdd, btnRemove, btnDuplicate)
            );

            Controls.Add(Ui.TwoListsLayout(column,
                "Список политик", lstPolicies,
                "Подробности выбранной политики", rtbPolicyDetails,
                "PoliciesSplitter"));
        }

        /// <summary>
        /// Пересобирает выпадашку "Сервер". НЕ трогает глобальный контекст сервера (флаг
        /// _rebuildingServerCombo глушит SelectedIndexChanged). Выбор: если пользователь уже
        /// выбрал здесь конкретный удалённый сервер - оставляем его; иначе следуем за текущим
        /// рабочим сервером (тем, что выбран деревом/верхней панелью).
        /// </summary>
        public void RefreshServerCombo()
        {
            _rebuildingServerCombo = true;
            try
            {
                var current = (cmbPolicyServer.SelectedItem as PolicyServerItem)?.Server ?? "";
                var want = string.IsNullOrEmpty(current) ? _serverContext.CurrentServer : current;

                cmbPolicyServer.BeginUpdate();
                cmbPolicyServer.Items.Clear();
                cmbPolicyServer.Items.Add(new PolicyServerItem("", $"(локальный) {Ui.LocalServerLabel()}"));
                foreach (var srv in _connectedServersProvider())
                    cmbPolicyServer.Items.Add(new PolicyServerItem(srv, srv));
                cmbPolicyServer.EndUpdate();

                var restore = 0;
                for (int i = 0; i < cmbPolicyServer.Items.Count; i++)
                    if (((PolicyServerItem)cmbPolicyServer.Items[i]).Server.Equals(want, StringComparison.OrdinalIgnoreCase)) { restore = i; break; }
                cmbPolicyServer.SelectedIndex = restore;
            }
            finally
            {
                _rebuildingServerCombo = false;
            }
        }

        /// <summary>
        /// Заполняет выпадашку зон уже готовым списком имён (один общий запрос Get-DnsServerZone
        /// из MainForm обслуживает и вкладку Scopes, и эту вкладку - чтобы не дёргать сервер дважды).
        /// </summary>
        public void SetZoneNames(IReadOnlyList<string> names)
        {
            var current = cmbPolicyZoneName.Text;
            cmbPolicyZoneName.Items.Clear();
            foreach (var name in names) cmbPolicyZoneName.Items.Add(name);
            cmbPolicyZoneName.Text = current; // не затираем то, что человек уже успел ввести/выбрать вручную
        }

        /// <summary>Реакция на активацию вкладки: обновить список серверов, а если зоны ещё не грузились - загрузить.</summary>
        public async Task OnTabActivatedAsync()
        {
            RefreshServerCombo(); // вдруг с прошлого захода подключились новые серверы
            if (cmbPolicyZoneName.Items.Count == 0)
                await RefreshZoneComboAsync();
        }

        /// <summary>Ставит контекст сервера по выбранному в выпадашке (пусто = локальный). Вызывать только из действий этой вкладки, не из пересборки списка.</summary>
        private void ApplyServerContext()
        {
            _serverContext.Set((cmbPolicyServer?.SelectedItem as PolicyServerItem)?.Server ?? "");
        }

        /// <summary>Заполняет только выпадашку зон - по выбранному здесь серверу (не трогает список зон вкладки Scopes).</summary>
        private async Task RefreshZoneComboAsync()
        {
            var names = await FetchScopeCapableZoneNamesAsync();

            var cur = cmbPolicyZoneName.Text;
            cmbPolicyZoneName.Items.Clear();
            foreach (var n in names) cmbPolicyZoneName.Items.Add(n);
            cmbPolicyZoneName.Text = cur;
        }

        /// <summary>Имена клиентских подсетей текущего (для этой вкладки) сервера - для пикера в диалоге создания политики.</summary>
        private async Task<List<string>> FetchClientSubnetNamesAsync()
        {
            ApplyServerContext();
            var (names, _) = await _zoneService.GetClientSubnetNamesAsync();
            return names;
        }

        /// <summary>Имена зон (только те, что поддерживают Zone Scopes) текущего для этой вкладки сервера.</summary>
        private async Task<List<string>> FetchScopeCapableZoneNamesAsync()
        {
            ApplyServerContext();
            var (names, log) = await _zoneService.GetScopeCapableZoneNamesAsync();
            _log.AppendLog(log);
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Имена scope'ов зоны на текущем для этой вкладки сервере.</summary>
        private async Task<List<string>> FetchZoneScopeNamesAsync(string zoneName)
        {
            ApplyServerContext();
            var (names, log) = await _zoneService.GetZoneScopeNamesAsync(zoneName);
            _log.AppendLog(log);
            return names;
        }

        private async Task RefreshPoliciesAsync()
        {
            ApplyServerContext();
            var zoneName = Ui.Val(cmbPolicyZoneName);
            if (string.IsNullOrEmpty(zoneName)) { _log.AppendLog("Укажи имя зоны."); return; }

            _log.AppendLog($"Загружаю политики зоны '{zoneName}'...");
            var (policies, diagnostics, log) = await _policyService.GetZonePoliciesAsync(zoneName);
            _log.AppendLog(log);
            foreach (var d in diagnostics) _log.AppendLog(d); // не достали подсеть/scope обычным способом - показываем все поля

            lstPolicies.Items.Clear();
            _lastPolicies.Clear();
            rtbPolicyDetails.Clear();

            foreach (var p in policies)
            {
                lstPolicies.Items.Add(p.Name);
                _lastPolicies.Add(p);
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

        private async Task AddPolicyAsync()
        {
            ApplyServerContext();
            var zoneName = Ui.Val(cmbPolicyZoneName);
            var policyName = Ui.Val(txtPolicyName);
            var subnetInput = Ui.Val(txtPolicySubnetName);
            var scopeName = Ui.Val(txtPolicyScopeName);

            var validationError = DnsPolicyService.ValidateAdd(zoneName, policyName, subnetInput, scopeName);
            if (validationError != null)
            {
                _log.AppendLog(validationError);
                return;
            }

            // Можно перечислить несколько подсетей через запятую - это "ИЛИ": политика
            // срабатывает, если клиент попадает в любую из них. Пример:
            //   -ClientSubnet "EQ,net_100,Old_DNS_redirect13,Old_DNS_redirect6"
            var subnetNames = DnsPolicyService.ParseSubnetNames(subnetInput);

            _log.AppendLog($"Создаю политику '{policyName}': подсети [{string.Join(", ", subnetNames)}] -> scope '{scopeName}'...");
            var (success, log) = await _policyService.AddAsync(zoneName, policyName, subnetNames, scopeName);
            _log.AppendLog(log);
            FileLogger.LogChange("POLICY ADD", zoneName,
                $"Policy={policyName} Subnets=[{string.Join(",", subnetNames)}] -> Scope={scopeName}", success, log);
            await RefreshPoliciesAsync();
        }

        private async Task RemovePolicyAsync()
        {
            ApplyServerContext();
            var zoneName = Ui.Val(cmbPolicyZoneName);
            if (lstPolicies.SelectedItem == null || string.IsNullOrEmpty(zoneName))
            {
                _log.AppendLog("Укажи зону и выбери политику в списке.");
                return;
            }

            var policyName = lstPolicies.SelectedItem.ToString();

            if (!DangerConfirmDialog.Show(
                    "Удаление политики",
                    $"   Удалить политику \"{policyName}\" из зоны \"{zoneName}\"?",
                    "Клиенты, которых обслуживала эта политика, начнут получать ответы по " +
                    "обычной (не переопределённой) логике зоны. Это действие нельзя отменить."))
                return;

            _log.AppendLog($"Удаляю политику '{policyName}'...");
            var (success, log) = await _policyService.RemoveAsync(zoneName, policyName);
            _log.AppendLog(log);
            FileLogger.LogChange("POLICY DELETE", zoneName, $"Policy={policyName}", success, log);
            await RefreshPoliciesAsync();
        }

        /// <summary>
        /// Дублирует выбранную политику на другие scope'ы других зон (того же сервера). Диалог
        /// позволяет отметить сразу несколько scope'ов в нескольких зонах; для каждой пары
        /// (зона, scope) создаётся отдельная политика с тем же критерием подсети.
        /// </summary>
        private async Task DuplicatePolicyAsync()
        {
            ApplyServerContext();
            var srcZone = Ui.Val(cmbPolicyZoneName);
            var idx = lstPolicies.SelectedIndex;
            if (string.IsNullOrEmpty(srcZone) || idx < 0 || idx >= _lastPolicies.Count)
            {
                _log.AppendLog("Выбери зону и политику в списке - её и будем дублировать.");
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
                _log.AppendLog("Для политики нужен хотя бы один критерий-подсеть - дублирование отменено.");
                return;
            }

            int ok = 0, fail = 0;
            foreach (var t in plan.Targets)
            {
                var newName = DnsPolicyService.DuplicateName(plan.BaseName, plan.KeepExactName, plan.Targets, t.Zone, t.Scope, srcZone);

                _log.AppendLog($"Дублирую '{src.Name}' -> зона '{t.Zone}', scope '{t.Scope}' как политику '{newName}'...");
                var (success, log) = await _policyService.AddAsync(t.Zone, newName, plan.Subnets.ToArray(), t.Scope);
                _log.AppendLog(log);
                FileLogger.LogChange("POLICY DUPLICATE", t.Zone,
                    $"Policy={newName} (from {srcZone}/{src.Name}) Subnets=[{string.Join(",", plan.Subnets)}] -> Scope={t.Scope}",
                    success, success ? null : log);
                if (success) ok++; else fail++;
            }

            _log.AppendLog($"Дублирование завершено: создано {ok}, ошибок {fail}.");
            await RefreshPoliciesAsync();
        }
    }
}
