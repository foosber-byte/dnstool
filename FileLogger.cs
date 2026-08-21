using System;
using System.IO;

namespace DnsToolWinForms
{
    /// <summary>
    /// Логирует в отдельный файл только реальные изменения (создание/удаление/добавление
    /// зон, scopes, подсетей, политик, записей) - без "шума" вроде обновлений списков,
    /// чтобы лог оставался коротким и читаемым при разборе "что и когда поменяли".
    /// </summary>
    public static class FileLogger
    {
        // Лог лежит прямо рядом с exe - так его проще найти, не нужно лезть в ProgramData.
        private static readonly string LogDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string LogPath = Path.Combine(LogDir, "changes.log");

        private static readonly object Lock = new object();

        public static string CurrentLogPath => LogPath;

        /// <summary>
        /// Вызывается один раз при старте приложения. Если файла лога ещё нет - создаёт его
        /// с заголовком-разделителем сессии. Если файл уже существует - ничего не трогает
        /// (существующие записи не теряются, размер файла не ограничиваем).
        /// </summary>
        public static void EnsureInitialized()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                if (!File.Exists(LogPath))
                {
                    File.WriteAllText(LogPath,
                        $"# Лог изменений DnsToolWinForms{Environment.NewLine}" +
                        $"# Файл создан: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch
            {
                // Не удалось создать (нет прав в папке с exe и т.п.) - не роняем приложение,
                // просто дальнейшие попытки записи в LogChange так же тихо промолчат.
            }
        }

        /// <summary>
        /// Записывает одну строку об изменении. success/errorText берутся из результата
        /// вызова DnsHelper.Invoke - см. MainForm.WasSuccess().
        /// </summary>
        public static void LogChange(string action, string target, string details, bool success, string errorText = null)
        {
            try
            {
                Directory.CreateDirectory(LogDir);

                var status = success ? "OK" : "ОШИБКА";
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {status,-6} | {action,-13} | зона/объект: {target} | {details} | пользователь: {Environment.UserName}";
                if (!success && !string.IsNullOrEmpty(errorText))
                    line += $" | причина: {errorText.Trim()}";

                lock (Lock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Логирование не должно ронять приложение, если файл недоступен
                // (нет прав в папке с exe, диск занят и т.п.) - молча пропускаем.
            }
        }
    }
}
