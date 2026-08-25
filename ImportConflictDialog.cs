using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public enum ImportConflictChoice { Overwrite, OverwriteAll, Skip, SkipAll }

    /// <summary>
    /// Что делать, если импортируемая запись уже существует в scope - конфликт по имени+типу.
    /// "Всё" варианты запоминаются вызывающей стороной и применяются ко всем дальнейшим
    /// конфликтам без повторных вопросов (пока импорт не закончится).
    /// </summary>
    public static class ImportConflictDialog
    {
        public static ImportConflictChoice Show(string recordName, string recordType)
        {
            using var dlg = new Form
            {
                Text = "Конфликт при импорте",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(400, 160),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lbl = new Label
            {
                Text = $"Запись \"{recordName}\" ({recordType}) уже существует в этом scope.\nЧто сделать?",
                Location = new Point(16, 16),
                Size = new Size(368, 40)
            };

            var btnOverwrite = new Button { Text = "Перезаписать", Location = new Point(16, 68), Size = new Size(170, 34) };
            var btnOverwriteAll = new Button { Text = "Перезаписать всё", Location = new Point(196, 68), Size = new Size(188, 34) };
            var btnSkip = new Button { Text = "Пропустить", Location = new Point(16, 110), Size = new Size(170, 34) };
            var btnSkipAll = new Button { Text = "Пропустить всё", Location = new Point(196, 110), Size = new Size(188, 34) };

            var result = ImportConflictChoice.SkipAll; // безопасный дефолт, если закрыть окно крестиком - не перезаписывает молча остальное

            btnOverwrite.Click += (s, e) => { result = ImportConflictChoice.Overwrite; dlg.Close(); };
            btnOverwriteAll.Click += (s, e) => { result = ImportConflictChoice.OverwriteAll; dlg.Close(); };
            btnSkip.Click += (s, e) => { result = ImportConflictChoice.Skip; dlg.Close(); };
            btnSkipAll.Click += (s, e) => { result = ImportConflictChoice.SkipAll; dlg.Close(); };

            dlg.Controls.AddRange(new Control[] { lbl, btnOverwrite, btnOverwriteAll, btnSkip, btnSkipAll });
            dlg.CancelButton = btnSkipAll; // Esc = безопасный выбор

            dlg.ShowDialog();
            return result;
        }
    }
}
