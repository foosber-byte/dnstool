using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
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

    /// <summary>Подготовленное обновление: откуда запускать новый exe и куда его ставить.</summary>
    public class UpdateStaging
    {
        public string SourceDir;
        public string AppDir;
        public string ExeName;
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
    /// во временную папку, проверяем подпись, распаковываем и запускаем НОВЫЙ exe с ключом
    /// --apply-update - он сам (UpdateApplier, чистый C#, без cmd/bat) дождётся закрытия
    /// старого процесса, скопирует файлы поверх установки (кроме settings.ini, changes.log
    /// и .dns-файлов зон - это локальные данные, не часть релиза), запишет update.log,
    /// перезапустит приложение и удалит временную папку.
    /// </summary>
    public static class UpdateChecker
    {
        private const string ApiUrl = "https://api.github.com/repos/foosber-byte/dnstool/releases/latest";

        // Публичный ключ проверки подписи релизных zip (парный приватный - вне репозитория).
        private const string PublicKeyXml = "<RSAKeyValue><Modulus>zTcdunizXgC73eNP6W1pofGoUr0LHxGjg8pXjoakclCaleIfnc3GNi50cClrTx1nOQRnBgxnIKXWn6EINJ2nc1lDbg2STxkGncXWm3ChGx11OyB1yP0MaIp34s2n5wLyHPY9XGnJjhJyOJE0Vewg+XEtPXybXGuwj7oAHisBOvlhkZmy3uD8ZMF30VVvs1LthogcwhWcaGiaQXHKRm2W8umusPCU/g9YmWAGkxwDL341p24hFix0nU29DbZU1jTmrOIx1G27NExpB9XUaitjNcD1QmqoW52QkSdcri94OWETxiKjtj0pEzAfQzCeaO7kriUxQqiggCFeg4MDwHxJZQGdmOZJpCO/yg0vml56yeCqP9X5wfECe7vsopeU1atQB2jqGf+p9bNaJKWQ61rhcmHdel4QF75d9xN1J2O87wO5v0Go2BxrkYAYbyygi6MEr+Z01tOAs39dfIpwHlsXJV3vOsYBHIRLx2ZJIXVHtBm2hixxh58gWWP6C1CE5O3jYUL0EKY6JpCbM9Q5Uca67S9bXivYRKW3Vr9ja1uTC3QJya4UMqx02Dfkxx7tIcltWe0mdxVuuXkeJOODxOxlGTW/PywqROF3XC1etInMJYSklZgU7s6bSdDpgTIeg7lk3r4/hx88L+Rlswq7CXnp+FNykr3bb7PZCQkomvjs/9k=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

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
        /// Скачивает архив и подпись, проверяет подпись, распаковывает во временную папку.
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

            return new UpdateStaging
            {
                SourceDir = sourceDir,
                AppDir = appDir,
                ExeName = exeName
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
        /// Запускает НОВЫЙ exe в режиме применения обновления (UpdateApplier.ApplyArgs:
        /// "--apply-update &lt;папка&gt; &lt;pid&gt;") и завершает текущий процесс - файл exe освобождается.
        /// Application.Exit() может не убить процесс (открытая модальная форма, зависший
        /// обработчик закрытия): тогда процесс живёт дальше, держит exe заблокированным, и
        /// копирование не сможет его заменить. Поэтому через 5 секунд процесс завершается
        /// принудительно - это страховка, в норме приложение закрывается сразу.
        /// </summary>
        public static void LaunchUpdaterAndExit(UpdateStaging staging)
        {
            var exePath = Path.Combine(staging.SourceDir, staging.ExeName);
            var psi = new ProcessStartInfo(exePath)
            {
                Arguments = UpdateApplier.ArgPrefix + " \"" + staging.AppDir + "\" " + Process.GetCurrentProcess().Id,
                UseShellExecute = true,
                WorkingDirectory = staging.SourceDir
            };
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
