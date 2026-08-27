using System;
using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public enum ImportConflictChoice { Overwrite, OverwriteAll, Skip, SkipAll }

    /// <summary>
    /// Что делать, если импортируемая запись уже существует в scope - конфликт по имени+типу.
    /// Показывает и существующее, и новое значение рядом - чтобы сразу видеть, это точный
    /// дубль (значения совпадают) или реально отличается IP/целевое имя/что угодно другое.
    /// "Всё" варианты запоминаются вызывающей стороной и применяются ко всем дальнейшим
    /// конфликтам без повторных вопросов (пока импорт не закончится).
    /// </summary>
    public static class ImportConflictDialog
    {
        public static ImportConflictChoice Show(string recordName, string recordType, string existingValue, string newValue)
        {
            var isSameValue = string.Equals((existingValue ?? "").Trim(), (newValue ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

            using var dlg = new Form
            {
                Text = "Конфликт при импорте",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(420, 236),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lbl = new Label
            {
                Text = $"Запись \"{recordName}\" ({recordType}) уже существует в этом scope.",
                Location = new Point(16, 16),
                Size = new Size(388, 20)
            };

            var lblExisting = new Label
            {
                Text = $"Существующее значение: {existingValue}",
                Location = new Point(16, 42),
                Size = new Size(388, 20),
                AutoEllipsis = true
            };

            var lblNew = new Label
            {
                Text = $"Новое значение: {newValue}",
                Location = new Point(16, 62),
                Size = new Size(388, 20),
                AutoEllipsis = true
            };

            var lblCompare = new Label
            {
                Text = isSameValue ? "Значения совпадают - это точный дубль." : "Значения ОТЛИЧАЮТСЯ.",
                ForeColor = isSameValue ? Color.SeaGreen : Color.DarkOrange,
                Font = new Font(dlg.Font, FontStyle.Bold),
                Location = new Point(16, 86),
                Size = new Size(388, 20)
            };

            var lblQuestion = new Label { Text = "Что сделать?", Location = new Point(16, 110), AutoSize = true };

            var btnOverwrite = new Button { Text = "Перезаписать", Location = new Point(16, 138), Size = new Size(180, 34) };
            var btnOverwriteAll = new Button { Text = "Перезаписать всё", Location = new Point(204, 138), Size = new Size(200, 34) };
            var btnSkip = new Button { Text = "Пропустить", Location = new Point(16, 180), Size = new Size(180, 34) };
            var btnSkipAll = new Button { Text = "Пропустить всё", Location = new Point(204, 180), Size = new Size(200, 34) };

            var result = ImportConflictChoice.SkipAll; // безопасный дефолт, если закрыть окно крестиком - не перезаписывает молча остальное

            btnOverwrite.Click += (s, e) => { result = ImportConflictChoice.Overwrite; dlg.Close(); };
            btnOverwriteAll.Click += (s, e) => { result = ImportConflictChoice.OverwriteAll; dlg.Close(); };
            btnSkip.Click += (s, e) => { result = ImportConflictChoice.Skip; dlg.Close(); };
            btnSkipAll.Click += (s, e) => { result = ImportConflictChoice.SkipAll; dlg.Close(); };

            dlg.Controls.AddRange(new Control[]
            {
                lbl, lblExisting, lblNew, lblCompare, lblQuestion,
                btnOverwrite, btnOverwriteAll, btnSkip, btnSkipAll
            });
            dlg.CancelButton = btnSkipAll; // Esc = безопасный выбор

            dlg.ShowDialog();
            return result;
        }
    }
}
