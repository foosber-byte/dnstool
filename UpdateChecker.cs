using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public class UpdateInfo
    {
        public string Version;
        public string DownloadUrl;
    }

    /// <summary>
    /// Проверка и установка обновлений через GitHub Releases (последний релиз репозитория,
    /// один .zip-ассет на релиз). Требует исходящий доступ с этого сервера на github.com/
    /// api.github.com - если сервер в закрытом сегменте сети (частый случай для DNS-серверов
    /// в защищённой инфраструктуре), проверка честно вернёт ошибку, а не зависнет молча.
    ///
    /// Самообновление устроено так: нельзя перезаписать СВОЙ ЖЕ запущенный exe напрямую
    /// (Windows держит файл заблокированным, пока процесс жив). Поэтому: скачиваем и
    /// распаковываем архив во временную папку, готовим маленький .bat-скрипт, который
    /// подождёт закрытия текущего процесса, скопирует новые файлы (кроме settings.ini,
    /// changes.log и .dns-файлов зон - это локальные данные, не часть релиза), перезапустит
    /// приложение и уберёт за собой временную папку. Сам .bat запускается, и только потом
    /// текущий процесс завершается - в этот момент файл перестаёт быть занят.
    /// </summary>
    public static class UpdateChecker
    {
        private const string ApiUrl = "https://api.github.com/repos/foosber-byte/dnstool/releases/latest";

        public static async Task<(bool Success, string Error, UpdateInfo Info)> CheckLatestAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DnsToolWinForms-UpdateChecker");
                var json = await client.GetStringAsync(ApiUrl);

                var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                var urlMatch = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");

                if (!tagMatch.Success || !urlMatch.Success)
                    return (false, "Не удалось разобрать ответ GitHub (в последнем релизе нет тега или .zip-файла).", null);

                return (true, null, new UpdateInfo { Version = tagMatch.Groups[1].Value, DownloadUrl = urlMatch.Groups[1].Value });
            }
            catch (Exception ex)
            {
                return (false,
                    "Не удалось связаться с GitHub. Проверь, есть ли у этого сервера доступ в интернет " +
                    "(частая причина - закрытый сегмент сети). Подробности: " + ex.Message, null);
            }
        }

        /// <summary>Простое сравнение версий вида "2.0.2" - true, если latest реально новее current.</summary>
        public static bool IsNewer(string latest, string current)
        {
            return Version.TryParse(latest, out var lv) && Version.TryParse(current, out var cv) && lv > cv;
        }

        /// <summary>
        /// Скачивает и распаковывает обновление, готовит .bat-обновлятор.
        /// Возвращает путь к готовому .bat - его ещё нужно запустить через LaunchUpdaterAndExit().
        /// </summary>
        public static async Task<string> DownloadAndPrepareUpdateAsync(string downloadUrl)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DnsToolWinForms_update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, "update.zip");
            var extractDir = Path.Combine(tempDir, "extracted");

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DnsToolWinForms-UpdateChecker");
                var bytes = await client.GetByteArrayAsync(downloadUrl);
                File.WriteAllBytes(zipPath, bytes);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // Если внутри архива всё лежит в одной вложенной папке (обычное дело для архивов
            // репозитория) - спускаемся туда, иначе скопировали бы файлы на уровень глубже, чем нужно.
            var sourceDir = extractDir;
            var subDirs = Directory.GetDirectories(extractDir);
            var topFiles = Directory.GetFiles(extractDir);
            if (subDirs.Length == 1 && topFiles.Length == 0)
                sourceDir = subDirs[0];

            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var exeName = Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var updaterScript = Path.Combine(tempDir, "update.bat");

            // /XF исключает локальные файлы - именно они НЕ должны быть тронуты обновлением.
            //
            // Раньше здесь была фиксированная пауза "timeout /t 2" перед копированием - этого
            // иногда не хватало, чтобы процесс приложения реально завершился и отпустил файл
            // .exe (Application.Exit() не гарантирует мгновенное закрытие: закрытие CimSession,
            // финализаторы и т.п. могут занять больше времени). Если .exe оставался залоченным,
            // robocopy молча не мог его перезаписать - а скрипт всё равно шёл дальше и запускал
            // СТАРЫЙ .exe, как будто обновление прошло успешно. Теперь вместо фиксированной паузы -
            // реальный опрос через tasklist, жив ли ещё процесс, с разумным пределом ожидания (30 сек).
            var scriptLines = new[]
            {
                "@echo off",
                "setlocal",
                "set RETRIES=0",
                ":waitloop",
                $"tasklist /FI \"IMAGENAME eq {exeName}\" 2>NUL | find /I \"{exeName}\" >NUL",
                "if \"%ERRORLEVEL%\"==\"0\" (",
                "    set /a RETRIES+=1",
                "    if %RETRIES% GEQ 30 goto docopy",
                "    timeout /t 1 /nobreak >nul",
                "    goto waitloop",
                ")",
                ":docopy",
                // /R:10 /W:1 - на случай остаточной блокировки файла уже после завершения процесса
                // (антивирус, индексатор и т.п.) - раньше было /R:3, теперь больше запас на всякий случай.
                $"robocopy \"{sourceDir}\" \"{appDir}\" /E /XF settings.ini changes.log changes.log.bak_* *.dns *.dns.bak_* /NFL /NDL /NJH /NJS /R:10 /W:1",
                $"start \"\" \"{Path.Combine(appDir, exeName)}\"",
                $"rmdir /S /Q \"{tempDir}\""
            };
            File.WriteAllLines(updaterScript, scriptLines);

            return updaterScript;
        }

        /// <summary>Запускает .bat-обновлятор и сразу завершает текущий процесс - файл exe освобождается.</summary>
        public static void LaunchUpdaterAndExit(string updaterScriptPath)
        {
            Process.Start(new ProcessStartInfo(updaterScriptPath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
            Application.Exit();
        }
    }
}
