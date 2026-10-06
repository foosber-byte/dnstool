using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DnsToolWinForms.Services;

namespace DnsToolWinForms.Controls
{
    /// <summary>
    /// Экспорт списка строк (то, что сейчас отображено в списке - с учётом применённых
    /// фильтра и сортировки) в текстовый файл. Путь выбирается диалогом сохранения -
    /// явно, а не в жёстко зашитое место. Общий для всех вкладок (подсети, записи, зоны).
    /// </summary>
    public static class ListExporter
    {
        /// <param name="headerLine">
        /// Необязательная строка-заголовок (дата + сервер, откуда выгрузка) - пишется первой
        /// строкой файла с префиксом "#", чтобы при последующем импорте её можно было
        /// однозначно отличить от настоящих строк с записями и просто пропустить.
        /// </param>
        public static void Export(AppLog log, IEnumerable<string> lines, string suggestedFileName, string headerLine = null)
        {
            var linesList = lines.ToList();
            if (linesList.Count == 0)
            {
                log.AppendLog("Список пуст - нечего экспортировать.");
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
                FileName = suggestedFileName,
                Title = "Сохранить список в файл"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    var output = new List<string>();
                    if (!string.IsNullOrEmpty(headerLine)) output.Add("# " + headerLine);
                    output.AddRange(linesList);
                    File.WriteAllLines(dlg.FileName, output);
                    log.AppendLog($"OK: список ({linesList.Count} строк) сохранён в файл: {dlg.FileName}");
                }
                catch (System.Exception ex)
                {
                    log.AppendLog($"ОШИБКА: не удалось сохранить файл - {ex.Message}");
                }
            }
        }
    }
}
