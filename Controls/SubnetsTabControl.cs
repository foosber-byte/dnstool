using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
                (s, e) => ListExporter.Export(_log, lstSubnets.Items.Cast<string>(), $"subnets_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    $"выгрузка подсетей, {DateTime.Now:dd.MM.yyyy HH:mm:ss}"));

            var btnImport = IconFactory.CreateButton(IconFactory.Import(), "Импорт подсетей из файла...", _toolTip,
                async (s, e) => await ImportSubnetsAsync());

            var column = Ui.Column(Ui.Row(btnRefresh, btnAdd, btnRemove, btnExport, btnImport));
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

        /// <summary>
        /// Импорт подсетей из файла, ранее сохранённого через "Экспорт в файл...". Перед выбором
        /// файла пользователь выбирает режим (ImportSubnetsDialog): "заменить текущие сети"
        /// (по умолчанию - все существующие подсети удаляются, затем создаются подсети из
        /// файла) или "добавить к существующим" (дубликаты по имени пропускаются). Строки-
        /// заголовки экспорта (начинаются с "#") пропускаются как метаданные. Формат строки
        /// подсети - DnsSubnet.Display ("{Name,-25} {Cidr}"): имя и CIDR разделяются 2+
        /// пробелами; строка без CIDR (подсеть без префикса) распознаётся как имя без CIDR.
        /// Каждая строка валидируется через DnsSubnetService.ValidateAdd; строки с ошибками
        /// валидации пропускаются с сообщением, создание/удаление идёт по одной.
        /// </summary>
        private async Task ImportSubnetsAsync()
        {
            var mode = ImportSubnetsDialog.Show();
            if (mode == null) return;
            var replaceExisting = mode == SubnetImportMode.Replace;

            using var ofd = new OpenFileDialog
            {
                Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
                Title = "Выбери файл с выгрузкой подсетей"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            string[] rawLines;
            try { rawLines = File.ReadAllLines(ofd.FileName); }
            catch (Exception ex) { _log.AppendLog($"ОШИБКА: не удалось прочитать файл - {ex.Message}"); return; }

            var parsed = new List<(string Name, string Cidr)>();
            foreach (var raw in rawLines)
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("#")) continue; // строка-заголовок экспорта, не подсеть

                // Формат строки - "{name,-25} {cidr}" (см. DnsSubnet.Display). Парсим по
                // разделителю "2+ пробела подряд" - устойчивее, чем точные позиции колонок.
                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
                var name = parts[0].Trim();
                var cidr = parts.Length > 1 ? string.Join(" ", parts.Skip(1)).Trim() : "";
                if (!string.IsNullOrEmpty(name)) parsed.Add((name, cidr));
            }

            if (parsed.Count == 0)
            {
                _log.AppendLog("В файле не найдено строк с подсетями - импортировать нечего.");
                return;
            }

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var (items, log) = await _service.GetAllAsync();
                _log.AppendLog(log);
                foreach (var subnet in items) existing.Add(subnet.Name);
            }
            catch (Exception ex)
            {
                _log.AppendLog($"ОШИБКА: не удалось получить текущий список подсетей - {ex.Message}");
                return;
            }

            int removed = 0, removeFailed = 0;
            if (replaceExisting)
            {
                if (!DangerConfirmDialog.Show(
                        "Замена подсетей",
                        $"   Удалить все текущие подсети ({existing.Count} шт.) и заменить их подсетями из файла?",
                        "Все подсети, которых нет в файле, будут удалены. Политики, ссылающиеся на " +
                        "удалённые подсети по имени, перестанут корректно работать. Это действие нельзя отменить."))
                    return;

                foreach (var name in existing.ToList())
                {
                    _log.AppendLog($"Удаляю подсеть '{name}' (режим замены)...");
                    var (success, log) = await _service.RemoveAsync(name);
                    _log.AppendLog(log);
                    FileLogger.LogChange("SUBNET DELETE (import replace)", name, "-", success, log);
                    if (success) removed++; else removeFailed++;
                }
                existing.Clear();
            }

            var created = 0;
            var skippedExisting = 0;
            var skippedInvalid = 0;

            foreach (var (name, cidr) in parsed)
            {
                if (existing.Contains(name))
                {
                    skippedExisting++;
                    _log.AppendLog($"Пропускаю '{name}': подсеть с таким именем уже существует.");
                    continue;
                }

                var validationError = DnsSubnetService.ValidateAdd(name, cidr);
                if (validationError != null)
                {
                    skippedInvalid++;
                    _log.AppendLog($"Пропускаю '{name}': {validationError}");
                    continue;
                }

                _log.AppendLog($"Создаю подсеть '{name}' ({cidr})...");
                var (success, log) = await _service.AddAsync(name, cidr);
                _log.AppendLog(log);
                FileLogger.LogChange("SUBNET ADD (import)", name, $"CIDR={cidr}", success, log);
                if (success)
                {
                    created++;
                    existing.Add(name);
                }
            }

            var summary = replaceExisting
                ? $"Импорт завершён (замена): удалено {removed} (не удалено {removeFailed}), создано {created}, пропущено (ошибки) {skippedInvalid}."
                : $"Импорт завершён (добавление): создано {created}, пропущено (уже есть) {skippedExisting}, пропущено (ошибки) {skippedInvalid}.";
            _log.AppendLog(summary);
            if (created > 0) await RefreshAsync();
        }
    }
}
