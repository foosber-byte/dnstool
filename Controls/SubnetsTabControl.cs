using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DnsToolWinForms.Services;

namespace DnsToolWinForms.Controls
{
    /// <summary>
    /// Вкладка "Подсети" (Client Subnets): список подсетей + создать/удалить/экспорт.
    /// Логика работы с DNS-сервером - в DnsSubnetService, сюда перенесена только логика
    /// UI из MainForm (BuildSubnetsTab/RefreshSubnetsAsync/AddSubnetAsync/RemoveSubnetAsync).
    /// </summary>
    public sealed class SubnetsTabControl : UserControl
    {
        private readonly AppLog _log;
        private readonly DnsSubnetService _service;
        private readonly ToolTip _toolTip;

        private readonly ListBox lstSubnets = new ListBox();
        private readonly TextBox txtSubnetName;
        private readonly TextBox txtSubnetCidr;

        public SubnetsTabControl(AppLog log, IDnsCommandRunner runner, ToolTip toolTip)
        {
            _log = log;
            _service = new DnsSubnetService(runner);
            _toolTip = toolTip;
            txtSubnetName = Ui.Tb(180, "имя подсети");
            txtSubnetCidr = Ui.Tb(160, "10.0.1.0/24");
            BuildUi();
        }

        private void BuildUi()
        {
            var btnRefresh = IconFactory.CreateButton(IconFactory.Refresh(), "Обновить список подсетей", _toolTip,
                async (s, e) => await RefreshAsync());

            var btnAdd = IconFactory.CreateButton(IconFactory.Add(), "Добавить подсеть...", _toolTip, async (s, e) =>
            {
                var (name, cidr) = AddSubnetDialog.Show();
                if (name == null) return;
                txtSubnetName.Text = name;
                txtSubnetCidr.Text = cidr;
                await AddAsync();
            });

            var btnRemove = IconFactory.CreateButton(IconFactory.Delete(), "Удалить выбранную подсеть", _toolTip,
                async (s, e) => await RemoveAsync());

            var btnExport = IconFactory.CreateButton(IconFactory.Export(), "Экспорт в файл...", _toolTip,
                (s, e) => ListExporter.Export(_log, lstSubnets.Items.Cast<string>(), $"subnets_{DateTime.Now:yyyyMMdd_HHmmss}.txt"));

            var column = Ui.Column(Ui.Row(btnRefresh, btnAdd, btnRemove, btnExport));
            column.Dock = DockStyle.Top;

            lstSubnets.Dock = DockStyle.Fill;
            lstSubnets.Font = new Font("Consolas", 9F);
            lstSubnets.HorizontalScrollbar = true;

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.Controls.Add(column, 0, 0);
            table.Controls.Add(lstSubnets, 0, 1);

            Controls.Add(table);
        }

        private async Task RefreshAsync()
        {
            _log.AppendLog("Загружаю клиентские подсети...");
            var (items, log) = await _service.GetAllAsync();
            _log.AppendLog(log);

            lstSubnets.Items.Clear();
            foreach (var subnet in items)
                lstSubnets.Items.Add(subnet.Display);
        }

        private async Task AddAsync()
        {
            var name = Ui.Val(txtSubnetName);
            var cidr = Ui.Val(txtSubnetCidr);

            var validationError = DnsSubnetService.ValidateAdd(name, cidr);
            if (validationError != null)
            {
                _log.AppendLog(validationError);
                return;
            }

            _log.AppendLog($"Создаю подсеть '{name}' ({cidr})...");
            var (success, log) = await _service.AddAsync(name, cidr);
            _log.AppendLog(log);
            FileLogger.LogChange("SUBNET ADD", name, $"CIDR={cidr}", success, log);
            await RefreshAsync();
        }

        private async Task RemoveAsync()
        {
            if (lstSubnets.SelectedItem == null) { _log.AppendLog("Выбери подсеть в списке."); return; }
            var name = lstSubnets.SelectedItem.ToString().Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            if (!DangerConfirmDialog.Show(
                    "Удаление подсети",
                    $"   Удалить подсеть \"{name}\"?",
                    "Все политики, ссылающиеся на эту подсеть по имени, перестанут корректно " +
                    "работать (перестанут находить клиентов). Это действие нельзя отменить."))
                return;

            _log.AppendLog($"Удаляю подсеть '{name}'...");
            var (success, log) = await _service.RemoveAsync(name);
            _log.AppendLog(log);
            FileLogger.LogChange("SUBNET DELETE", name, "-", success, log);
            await RefreshAsync();
        }
    }
}
