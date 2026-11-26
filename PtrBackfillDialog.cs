using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DnsToolWinForms.Services;

namespace DnsToolWinForms
{
    /// <summary>
    /// Прогон «добавить недостающие PTR записи» по выбранному scope. Модальное окно с
    /// прогресс-баром; по завершении - итоговая таблица (имя, IP, целевая reverse-зона,
    /// статус) с сортировкой кликом по заголовку колонки. Отдельно подсвечиваются записи,
    /// для которых не нашлось обратной зоны. UI трогается только из UI-потока
    /// (прогресс - через IProgress), закрытие формы = отмена прогона.
    /// </summary>
    public static class PtrBackfillDialog
    {
        public static void Run(IWin32Window owner, ReverseDnsService service,
            string zoneName, string scopeName, string serverLabel)
        {            using var dlg = new Form
            {
                Text = $"Недостающие PTR записи - {zoneName} / {scopeName} ({serverLabel})",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(760, 420),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblStatus = new Label
            {
                Text = "Подготовка...",
                Location = new Point(16, 12),
                Size = new Size(728, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var progress = new ProgressBar
            {
                Location = new Point(16, 38),
                Size = new Size(640, 22),
                Minimum = 0,
                Maximum = 100
            };

            var btnCancelRun = new Button { Text = "Прервать", Location = new Point(664, 36), Size = new Size(80, 26) };

            var lst = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
                Location = new Point(16, 70),
                Size = new Size(728, 260),
                Visible = false,
                Font = new Font("Consolas", 9F)
            };
            lst.Columns.Add("Имя записи", 170);
            lst.Columns.Add("IP", 130);
            lst.Columns.Add("Обратная зона", 190);
            lst.Columns.Add("Статус", 210);

            // Сортировка кликом по заголовку; повторный клик по той же колонке меняет направление.
            var sortState = (Col: 0, Asc: true);
            lst.ColumnClick += (s, e) =>
            {
                if (sortState.Col == e.Column) sortState.Asc = !sortState.Asc;
                else sortState = (e.Column, true);
                lst.ListViewItemSorter = new RowComparer(sortState.Col, sortState.Asc);
            };

            var lblSummary = new Label
            {
                Text = "",
                Location = new Point(16, 338),
                Size = new Size(728, 20),
                ForeColor = Color.DimGray,
                Visible = false
            };

            var logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 338),
                Size = new Size(728, 66),
                Visible = false,
                Font = new Font("Consolas", 8.5F)
            };

            var btnClose = new Button { Text = "Закрыть", DialogResult = DialogResult.Cancel, Location = new Point(664, 378), Size = new Size(80, 30), Enabled = false };
            dlg.CancelButton = btnClose;

            // Форма не закрывается во время прогона - "Прервать" кооперативно останавливает цикл
            // (между записями), и только потом окно закрывается само.
            bool cancelled = false;
            var uiProgress = new Progress<int>(pct =>
            {
                progress.Value = Math.Min(pct, 100);
            });
            var uiStatus = new Progress<string>(text =>
            {
                lblStatus.Text = text;
            });

            btnCancelRun.Click += (s, e) => cancelled = true;
            dlg.FormClosing += (s, e) =>
            {
                if (!btnClose.Enabled && !cancelled) // прогон ещё идёт - первое нажатие = "Прервать"
                {
                    cancelled = true;
                    e.Cancel = true;
                }
            };

            dlg.Shown += async (s, e) =>
            {
                List<PtrBackfillResultRow> rows = null;
                Exception error = null;
                try
                {
                    rows = await service.BackfillPtrsAsync(zoneName, scopeName,
                        pct => ((IProgress<int>)uiProgress).Report(pct),
                        statusText => ((IProgress<string>)uiStatus).Report(statusText),
                        () => cancelled,
                        (reverseZone, ptrName, targetFqdn) => FileLogger.LogChange(
                            "PTR BACKFILL", reverseZone, $"PTR {ptrName} -> {targetFqdn} (прогон по scope \"{scopeName}\")", true));
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (dlg.IsDisposed) return;

                progress.Visible = false;
                btnCancelRun.Visible = false;

                if (error != null)
                {
                    lblStatus.Text = "Ошибка прогона:";
                    logBox.Visible = true;
                    logBox.Text = error.Message;
                    btnClose.Enabled = true;
                    btnClose.Focus();
                    return;
                }

                lblStatus.Text = $"Готово. Обработано A/AAAA-записей: {rows.Count}";

                var ok = rows.Count(r => r.Status == "OK");
                var already = rows.Count(r => r.Status == "Уже есть");
                var noZone = rows.Count(r => r.Status == "Нет reverse-зоны");
                var failed = rows.Count(r => r.Status != "OK" && r.Status != "Уже есть" && r.Status != "Нет reverse-зоны");

                lblSummary.Text = $"Создано: {ok}   Уже было: {already}   Нет reverse-зоны: {noZone}" +
                                  (failed > 0 ? $"   Ошибок: {failed}" : "");
                lblSummary.ForeColor = noZone > 0 ? Color.Firebrick : Color.DimGray;
                lblSummary.Visible = true;

                foreach (var r in rows)
                {
                    var item = new ListViewItem(new[] { r.RecordName ?? "", r.Ip ?? "", r.RequiredZone ?? "", r.Status ?? "" });
                    if (r.Status == "Нет reverse-зоны") item.ForeColor = Color.Firebrick;
                    else if (r.Status == "OK") item.ForeColor = Color.FromArgb(30, 130, 76);
                    lst.Items.Add(item);
                }

                // Служебная сортировка по умолчанию: "Нет reverse-зоны" сверху, внутри - по IP.
                lst.ListViewItemSorter = new RowComparer(-1, true);
                lst.Visible = true;
                btnClose.Enabled = true;
                btnClose.Focus();
            };

            dlg.Controls.AddRange(new Control[] { lblStatus, progress, btnCancelRun, lst, lblSummary, logBox, btnClose });
            dlg.ShowDialog(owner);
        }

        /// <summary>Сортировщик строк итога: колонка 0..3 кликом; -1 - служебный порядок (проблемы сверху, по IP).</summary>
        private sealed class RowComparer : System.Collections.IComparer
        {
            private readonly int _col;
            private readonly bool _asc;

            public RowComparer(int col, bool asc) { _col = col; _asc = asc; }

            public int Compare(object x, object y)
            {
                var a = (ListViewItem)x;
                var b = (ListViewItem)y;

                if (_col < 0)
                {
                    var aProb = a.SubItems[3].Text == "Нет reverse-зоны" ? 0 : 1;
                    var bProb = b.SubItems[3].Text == "Нет reverse-зоны" ? 0 : 1;
                    if (aProb != bProb) return aProb - bProb;
                    return _asc ? CompareValues(a, b, 1) : -CompareValues(a, b, 1);
                }

                var cmp = CompareValues(a, b, _col);
                return _asc ? cmp : -cmp;
            }

            private static int CompareValues(ListViewItem a, ListViewItem b, int col)
            {
                var ta = a.SubItems[col].Text;
                var tb = b.SubItems[col].Text;
                // IP-колонка сортируется по октетам, а не строково ("10.2.0.1" < "10.10.0.1").
                if (col == 1 && System.Net.IPAddress.TryParse(ta, out var ia) && System.Net.IPAddress.TryParse(tb, out var ib))
                {
                    var ba = ia.GetAddressBytes();
                    var bb = ib.GetAddressBytes();
                    if (ba.Length != bb.Length) return ba.Length.CompareTo(bb.Length);
                    for (var i = 0; i < ba.Length; i++)
                        if (ba[i] != bb[i]) return ba[i].CompareTo(bb[i]);
                    return 0;
                }
                return string.Compare(ta, tb, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
