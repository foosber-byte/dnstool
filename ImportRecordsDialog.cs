using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public class ImportFolderChoice
    {
        public string Name;
        public bool Create;
        public string WildcardIp;
    }

    public class ImportOptionsResult
    {
        public List<ImportFolderChoice> Folders = new List<ImportFolderChoice>();
        public bool ExcludeApex;
    }

    /// <summary>
    /// Диалог настроек перед самим импортом - показывает, что нашлось в файле (сколько
    /// записей, какие субдомены-папки), даёт выбрать, какие из обнаруженных папок создать
    /// (через wildcard-запись, с указанием IP для каждой), и опцию исключить @-записи.
    /// </summary>
    public static class ImportRecordsDialog
    {
        public static ImportOptionsResult Show(List<string> detectedFolders, int recordCount, string targetHint)
        {
            var folderRowHeight = 28;
            var foldersAreaHeight = detectedFolders.Count > 0 ? Math.Min(140, detectedFolders.Count * folderRowHeight + 8) : 0;
            var extraForFolders = detectedFolders.Count > 0 ? foldersAreaHeight + 44 : 0;

            using var dlg = new Form
            {
                Text = "Импорт записей",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(460, 168 + extraForFolders),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblInfo = new Label
            {
                Text = $"В файле найдено записей: {recordCount}" +
                       (detectedFolders.Count > 0 ? $"\nОбнаружены субдомены (папки): {detectedFolders.Count}" : "\nСубдомены (папки) не обнаружены.") +
                       $"\nИмпорт в: {targetHint}",
                Location = new Point(16, 12),
                Size = new Size(428, 56)
            };

            var y = 72;
            var folderRows = new List<(CheckBox Chk, TextBox Ip)>();

            if (detectedFolders.Count > 0)
            {
                var lblFolders = new Label
                {
                    Text = "Создать обнаруженные субдомены (потребуется IP для wildcard-записи):",
                    Location = new Point(16, y),
                    AutoSize = true
                };
                dlg.Controls.Add(lblFolders);
                y += 20;

                var scroll = new Panel
                {
                    Location = new Point(16, y),
                    Size = new Size(428, foldersAreaHeight),
                    AutoScroll = true,
                    BorderStyle = BorderStyle.FixedSingle
                };

                var rowY = 4;
                foreach (var folder in detectedFolders)
                {
                    var chk = new CheckBox { Text = folder, Location = new Point(6, rowY + 3), AutoSize = true };
                    var lblIp = new Label { Text = "IP:", Location = new Point(200, rowY + 5), AutoSize = true };
                    var ip = new TextBox { Location = new Point(224, rowY + 2), Width = 150, Enabled = false };
                    chk.CheckedChanged += (s, e) => ip.Enabled = chk.Checked;
                    scroll.Controls.Add(chk);
                    scroll.Controls.Add(lblIp);
                    scroll.Controls.Add(ip);
                    folderRows.Add((chk, ip));
                    rowY += folderRowHeight;
                }

                dlg.Controls.Add(scroll);
                y += foldersAreaHeight + 12;
            }

            var chkExcludeApex = new CheckBox { Text = "Исключить @-записи (корень зоны)", Location = new Point(16, y), AutoSize = true };
            y += 32;

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(280, y), Size = new Size(80, 32) };
            var btnImport = new Button { Text = "Импортировать", DialogResult = DialogResult.OK, Location = new Point(364, y), Size = new Size(110, 32) };

            dlg.Controls.Add(lblInfo);
            dlg.Controls.Add(chkExcludeApex);
            dlg.Controls.Add(btnCancel);
            dlg.Controls.Add(btnImport);
            dlg.AcceptButton = btnImport;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;

            var result = new ImportOptionsResult { ExcludeApex = chkExcludeApex.Checked };
            for (var i = 0; i < detectedFolders.Count; i++)
            {
                var (chk, ip) = folderRows[i];
                result.Folders.Add(new ImportFolderChoice
                {
                    Name = detectedFolders[i],
                    Create = chk.Checked,
                    WildcardIp = ip.Text.Trim()
                });
            }
            return result;
        }
    }
}
