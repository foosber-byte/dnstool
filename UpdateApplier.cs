using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DnsToolWinForms
{
    /// <summary>
    /// Применение уже скачанного и проверенного по подписи обновления. Старый экземпляр
    /// приложения запускает НОВЫЙ exe (распакованный во временную папку) с аргументами
    /// --apply-update "&lt;папка приложения&gt;" &lt;pid старого процесса&gt; и завершается.
    /// Новый экземпляр в этом режиме не показывает интерфейс: ждёт, пока старый процесс
    /// освободит exe, копирует файлы поверх установки (кроме локальных данных), пишет
    /// update.log, перезапускает приложение из папки установки и подчищает временную папку.
    /// Никакого cmd/bat - вся логика здесь, на C#, и покрыта тестами.
    /// </summary>
    public static class UpdateApplier
    {
        public const string ArgPrefix = "--apply-update";
        private const int WaitParentSeconds = 30;

        /// <summary>Разбирает аргументы запуска; true, если это режим применения обновления.</summary>
        public static bool TryParseApplyArgs(string[] args, out string appDir, out int parentProcessId)
        {
            appDir = null;
            parentProcessId = 0;
            if (args == null || args.Length < 3 || !string.Equals(args[0], ArgPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(args[1]))
                return false;
            appDir = args[1];
            return int.TryParse(args[2], out parentProcessId);
        }

        /// <summary>Входная точка режима применения обновления (вызывается из Program.Main до UI).</summary>
        public static void Run(string appDir, int parentProcessId)
        {
            var sourceDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var exeName = Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var logPath = Path.Combine(sourceDir, "update.log");
            try
            {
                Log(logPath, "apply-update started, source=" + sourceDir + ", target=" + appDir);
                WaitForParentProcess(parentProcessId);
                var copied = CopyUpdateFiles(sourceDir, appDir);
                Log(logPath, "copied " + copied + " files, relaunching app");
                Process.Start(new ProcessStartInfo(Path.Combine(appDir, exeName)) { UseShellExecute = true });
                try
                {
                    File.Copy(logPath, Path.Combine(appDir, "update.log"), true);
                }
                catch
                {
                    // папка установки может быть недоступна на запись - лог останется во временной папке
                }
                TryDeleteStaging(sourceDir, Path.Combine(sourceDir, exeName));
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log(logPath, "UPDATE FAILED: " + ex.Message);
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(appDir, exeName)) { UseShellExecute = true });
                }
                catch
                {
                    // старый exe мог остаться рабочим - но и его запустить не вышло, скажем через лог
                    Log(logPath, "could not relaunch old app either");
                }
                TryDeleteStaging(sourceDir, Path.Combine(sourceDir, exeName));
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Копирует файлы обновления поверх папки установки, сохраняя структуру подкаталогов.
        /// Локальные данные (settings.ini, changes.log и его бэкапы, .dns-файлы зон, update.log)
        /// не трогаются. Возвращает число скопированных файлов.
        /// </summary>
        public static int CopyUpdateFiles(string sourceDir, string targetDir)
        {
            sourceDir = sourceDir.TrimEnd('\\');
            targetDir = targetDir.TrimEnd('\\');
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            var copied = 0;
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (IsLocalDataFile(Path.GetFileName(file)))
                    continue;
                var relative = file.Substring(sourceDir.Length + 1);
                var target = Path.Combine(targetDir, relative);
                var targetSubDir = Path.GetDirectoryName(target);
                if (!Directory.Exists(targetSubDir))
                    Directory.CreateDirectory(targetSubDir);
                File.Copy(file, target, true);
                copied++;
            }
            return copied;
        }

        /// <summary>Локальные данные пользователя, которые обновление обязано не трогать.</summary>
        public static bool IsLocalDataFile(string name)
        {
            return name.Equals("settings.ini", StringComparison.OrdinalIgnoreCase)
                || name.Equals("changes.log", StringComparison.OrdinalIgnoreCase)
                || name.Equals("update.log", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("changes.log.bak_", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dns", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".dns.bak_");
        }

        private static void WaitForParentProcess(int parentProcessId)
        {
            var ownName = Process.GetCurrentProcess().ProcessName;
            for (var i = 0; i < WaitParentSeconds; i++)
            {
                Process parent = null;
                try
                {
                    parent = Process.GetProcessById(parentProcessId);
                }
                catch (ArgumentException)
                {
                    return;
                }
                if (parent.HasExited || !string.Equals(parent.ProcessName, ownName, StringComparison.OrdinalIgnoreCase))
                    return;
                Thread.Sleep(1000);
            }
        }

        private static void TryDeleteStaging(string sourceDir, string ownExePath)
        {
            try
            {
                // sourceDir = <temp>\DnsToolWinForms_update_x\extracted; в корне staging лежит и update.zip.
                // Свой exe залочен, пока процесс жив - его не удалить, всё остальное убираем,
                // чтобы в %TEMP% не копились папки на ~2 МБ с каждой попыткой обновления.
                var stagingRoot = Path.GetDirectoryName(sourceDir);
                foreach (var file in Directory.GetFiles(stagingRoot, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(file, ownExePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(file); } catch { }
                }
                foreach (var dir in Directory.GetDirectories(stagingRoot, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, false); } catch { }
                }
                try { Directory.Delete(stagingRoot, false); } catch { }
            }
            catch
            {
                // зачистка - дело косметическое, при любой проблеме просто оставляем папку в %TEMP%
            }
        }

        private static void Log(string logPath, string message)
        {
            try
            {
                File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + "\r\n");
            }
            catch
            {
                // лог - дело диагностическое, падать из-за него нельзя
            }
        }
    }
}
