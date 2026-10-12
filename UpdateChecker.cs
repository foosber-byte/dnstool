using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public class UpdateInfo
    {
        public string Version;
        public string DownloadUrl;
        public string SignatureUrl;
    }

    /// <summary>Подготовленное обновление: где лежит bat-обновлятор и параметры его запуска.</summary>
    public class UpdateStaging
    {
        public string BatPath;
        public string SourceDir;
        public string AppDir;
        public string ExeName;
        public string TempDir;
    }

    /// <summary>
    /// Проверка и установка обновлений через GitHub Releases (последний релиз репозитория,
    /// .zip-ассет + файл подписи .zip.sig к нему). Требует исходящий доступ с этого сервера
    /// на github.com/api.github.com - если сервер в закрытом сегменте сети (частый случай
    /// для DNS-серверов в защищённой инфраструктуре), проверка честно вернёт ошибку, а не
    /// зависнет молча.
    ///
    /// Перед распаковкой архив проверяется по RSA-подписи: даже при полной утечке доступа
    /// к GitHub-аккаунту подделать релиз нельзя - приватный ключ подписи хранится только
    /// у владельца, вне репозитория. Digest из API для этой цели не годится: GitHub
    /// пересчитывает его сам, тем же (скомпрометированным) аккаунтом.
    ///
    /// Самообновление устроено так: нельзя перезаписать СВОЙ ЖЕ запущенный exe напрямую
    /// (Windows держит файл заблокированным, пока процесс жив). Поэтому: скачиваем архив
    /// во временную папку, проверяем подпись, готовим маленький .bat-скрипт, который
    /// подождёт закрытия текущего процесса, скопирует новые файлы (кроме settings.ini,
    /// changes.log и .dns-файлов зон - это локальные данные, не часть релиза), перезапустит
    /// приложение и уберёт за собой временную папку. Bat содержит только ASCII - все пути
    /// (в том числе с кириллицей) передаются через переменные окружения DSTOOL_UPD_*,
    /// поэтому кодовая страница bat-файла не может испортить пути. Каждый шаг пишется в
    /// update.log; при ошибке копирования временная папка НЕ удаляется - лог и скачанный
    /// архив остаются для диагностики.
    /// </summary>
    public static class UpdateChecker
    {
        private const string ApiUrl = "https://api.github.com/repos/foosber-byte/dnstool/releases/latest";

        // Публичный ключ проверки подписи релизных zip (парный приватный - вне репозитория).
        private const string PublicKeyXml = "<RSAKeyValue><Modulus>ko+zvLv76eL8WqD3zhTYFUtkfjoa50l1d+Qi9VnQXGIGKFe1fxZ5vq3PerPzx7lA/eYWsK7TPnQnbPpDymsRDaR3/nw3JuNgi57xR7z8XpRtCAuByy1w+kkUv0O8Jhgh3pzFKG3UWpNkLXqOT0qfOQmVk0S8fCBgFrrhZqYb07wkrRhPcPSw9XoJr1uGI2Mz0gEk6MLrKNMbm1Kg+OJ1KBtltYkcD4CBcgVQ6t8fO1Lfdgd4l1sE1cRgmO1vG9jHo11/Dxlwlo4KYXly+BIJN5uvndtbygmf1x7d3PVgUYGfCq7eruIMZYzVhZZJR22y2KqFUW/lGbKhOiafzlsMiQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public static async Task<(bool Success, string Error, UpdateInfo Info)> CheckLatestAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DnsToolWinForms-UpdateChecker");
                var json = await client.GetStringAsync(ApiUrl);

                var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                var urlMatch = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                var sigMatch = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip\\.sig)\"");

                if (!tagMatch.Success || !urlMatch.Success)
                    return (false, "Не удалось разобрать ответ GitHub (в последнем релизе нет тега или .zip-файла).", null);

                return (true, null, new UpdateInfo
                {
                    Version = tagMatch.Groups[1].Value,
                    DownloadUrl = urlMatch.Groups[1].Value,
                    SignatureUrl = sigMatch.Success ? sigMatch.Groups[1].Value : null
                });
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
        /// Скачивает архив и подпись, проверяет подпись, распаковывает и готовит bat-обновлятор.
        /// Возвращает подготовленное обновление - его нужно запустить через LaunchUpdaterAndExit().
        /// </summary>
        public static async Task<UpdateStaging> DownloadAndPrepareUpdateAsync(UpdateInfo info)
        {
            if (string.IsNullOrEmpty(info.SignatureUrl))
                throw new InvalidOperationException(
                    "В релизе v" + info.Version + " нет файла подписи (.zip.sig). " +
                    "Ради защиты от подделанных релизов обновление без подписи не устанавливается.");

            var tempDir = Path.Combine(Path.GetTempPath(), "DnsToolWinForms_update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, "update.zip");
            var extractDir = Path.Combine(tempDir, "extracted");

            byte[] zipBytes;
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DnsToolWinForms-UpdateChecker");
                zipBytes = await client.GetByteArrayAsync(info.DownloadUrl);
                var sigText = await client.GetStringAsync(info.SignatureUrl);
                var sigBytes = Convert.FromBase64String(sigText.Trim());

                using (var rsa = RSA.Create())
                {
                    rsa.FromXmlString(PublicKeyXml);
                    if (!VerifySignature(zipBytes, sigBytes, rsa))
                        throw new InvalidOperationException(
                            "Проверка подписи обновления НЕ ПРОШЛА: архив v" + info.Version +
                            " не совпадает с подписью релиза. Возможно, архив повреждён или подделан - " +
                            "обновление отменено.");
                }
            }

            File.WriteAllBytes(zipPath, zipBytes);
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
            // Ожидание закрытия процесса - опрос tasklist (до 30 циклов по ~1 с), пауза через
            // ping (работает в любом скрытом cmd, в отличие от timeout). Коды robocopy >= 8 -
            // ошибка копирования: логируем, врем папку не трогаем, но приложение всё равно
            // запускаем (старым exe), чтобы пользователь не остался с закрытым окном.
            var scriptLines = new[]
            {
                "@echo off",
                "setlocal enableextensions",
                "if \"%DSTOOL_UPD_TMP%\"==\"\" exit /b 1",
                "set \"SRC=%DSTOOL_UPD_SRC%\"",
                "set \"APPDIR=%DSTOOL_UPD_APP%\"",
                "set \"EXE=%DSTOOL_UPD_EXE%\"",
                "set \"TMPDIR=%DSTOOL_UPD_TMP%\"",
                "set \"LOG=%TMPDIR%\\update.log\"",
                ">>\"%LOG%\" echo [%date% %time%] updater started",
                "set RETRIES=0",
                ":waitloop",
                "tasklist /FI \"IMAGENAME eq %EXE%\" 2>NUL | find /I \"%EXE%\" >NUL",
                "if errorlevel 1 goto docopy",
                "set /a RETRIES+=1",
                "if %RETRIES% GEQ 30 goto docopy",
                "ping -n 2 127.0.0.1 >NUL",
                "goto waitloop",
                ":docopy",
                ">>\"%LOG%\" echo [%date% %time%] app process closed after %RETRIES% wait cycles",
                "robocopy \"%SRC%\" \"%APPDIR%\" /E /XF settings.ini changes.log changes.log.bak_* *.dns *.dns.bak_* /NFL /NDL /NJH /NJS /R:10 /W:1",
                "set RC=%ERRORLEVEL%",
                "rem редирект перед echo: иначе \"code 3>>\" парсится как дескриптор 3>>, и строка теряется",
                ">>\"%LOG%\" echo [%date% %time%] robocopy exit code %RC%",
                "if %RC% LSS 8 goto launch",
                ">>\"%LOG%\" echo [%date% %time%] UPDATE FAILED: robocopy %RC%, temp folder kept for diagnostics",
                "start \"\" \"%APPDIR%\\%EXE%\"",
                "exit /b %RC%",
                ":launch",
                ">>\"%LOG%\" echo [%date% %time%] update OK, relaunching",
                "start \"\" \"%APPDIR%\\%EXE%\"",
                "copy /Y \"%LOG%\" \"%APPDIR%\\update.log\" >NUL 2>&1",
                "rmdir /S /Q \"%TMPDIR%\"",
                "exit /b 0"
            };
            File.WriteAllLines(updaterScript, scriptLines, Encoding.ASCII);

            return new UpdateStaging
            {
                BatPath = updaterScript,
                SourceDir = sourceDir,
                AppDir = appDir,
                ExeName = exeName,
                TempDir = tempDir
            };
        }

        /// <summary>Проверка RSA-подписи (SHA-256, PKCS#1 v1.5) скачанных данных.</summary>
        public static bool VerifySignature(byte[] data, byte[] signature, RSA publicKey)
        {
            if (data == null || signature == null || publicKey == null) return false;
            try
            {
                return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Запускает bat-обновлятор и завершает текущий процесс - файл exe освобождается.
        /// Application.Exit() может не убить процесс (открытая модальная форма, зависший
        /// обработчик закрытия): тогда процесс живёт дальше, держит exe заблокированным, и
        /// robocopy не может его заменить. Поэтому через 5 секунд процесс завершается
        /// принудительно - это страховка, в норме приложение закрывается сразу.
        /// </summary>
        public static void LaunchUpdaterAndExit(UpdateStaging staging)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"\"" + staging.BatPath + "\"\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["DSTOOL_UPD_SRC"] = staging.SourceDir;
            psi.EnvironmentVariables["DSTOOL_UPD_APP"] = staging.AppDir;
            psi.EnvironmentVariables["DSTOOL_UPD_EXE"] = staging.ExeName;
            psi.EnvironmentVariables["DSTOOL_UPD_TMP"] = staging.TempDir;
            Process.Start(psi);
            Application.Exit();
            Task.Run(() =>
            {
                Thread.Sleep(5000);
                Environment.Exit(0);
            });
        }
    }
}
