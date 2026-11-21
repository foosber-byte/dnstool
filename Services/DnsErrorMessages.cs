using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// Короткие русские сообщения для известных WIN32-кодов ошибок DNS Server - вместо полной
    /// "простыни" CimException, у которой верхнее Message - общая фраза ("Не удалось создать
    /// запись ресурса..."), а настоящая суть спрятана в error_Code/ErrorData. Известный код ->
    /// одна строка сути; неизвестный -> null, и вызывающий код показывает текст как раньше.
    /// Код извлекается из текста DescribeException (там всегда есть "MessageId=WIN32 NNNN"
    /// и/или "error_Code=NNNN") - на язык ОС не завязан.
    /// </summary>
    public static class DnsErrorMessages
    {
        /// <summary>Код -> суть про запись (подставляется "Запись {имя}... в зоне {зона}"). Пополняется по мере появления новых кодов в проекте.</summary>
        private static readonly Dictionary<string, string> RecordEssence = new Dictionary<string, string>
        {
            // 9711 - самый частый на практике: попытались создать запись, которая уже есть
            ["9711"] = "уже существует. Отредактируй существующую запись или удали её и создай заново",
            ["9709"] = "не создана: для этого имени уже существует CNAME-запись, а CNAME не может соседствовать с другими записями того же имени",
            ["9708"] = "не создана: это имя уже занято CNAME-записью, а у CNAME не может быть других записей",
            ["9701"] = "не найдена - возможно, она уже удалена",
            ["9706"] = "не принадлежит этой зоне"
        };

        /// <summary>Код -> суть про зону (подставляется "Зона {зона}...").</summary>
        private static readonly Dictionary<string, string> ZoneEssence = new Dictionary<string, string>
        {
            ["9601"] = "не существует на этом сервере",
            ["9609"] = "уже существует на этом сервере"
        };

        /// <summary>
        /// Вытаскивает WIN32-код DNS-ошибки из текста DescribeException ("WIN32 9711",
        /// "[WIN32 9711,Add-DnsServerResourceRecordA]", "error_Code=9711").
        /// null - если узнаваемого кода в тексте нет.
        /// </summary>
        public static string ExtractWin32Code(string errorText)
        {
            if (string.IsNullOrEmpty(errorText)) return null;
            var m = Regex.Match(errorText, @"(?:WIN32\s+|error_Code=)(\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Короткое сообщение "ОШИБКА: ..." для известного WIN32-кода DNS; null - если код
        /// неизвестен (тогда вызывающий показывает полный текст исключения, как раньше).
        /// Имя записи/зоны/области берётся из параметров командлета, чтобы сообщение говорило
        /// конкретно про запись пользователя, а не абстрактную "запись ресурса".
        /// </summary>
        public static string BuildShortError(string errorText, Dictionary<string, object> parameters)
        {
            var code = ExtractWin32Code(errorText);
            if (code == null) return null;

            var zoneName = GetParam(parameters, "ZoneName");
            var scopeName = GetParam(parameters, "ZoneScope");
            var recordName = GetParam(parameters, "Name");

            if (RecordEssence.TryGetValue(code, out var essence))
            {
                var subject = string.IsNullOrEmpty(recordName) ? "Запись" : $"Запись \"{recordName}\"";
                var where = "";
                if (!string.IsNullOrEmpty(zoneName))
                {
                    where = $" в зоне \"{zoneName}\"";
                    // Область упоминаем только когда она есть и не дублирует имя зоны
                    // (на серверах встречается "в области sasco.ru зоны sasco.ru" - дубль ни о чём).
                    if (!string.IsNullOrEmpty(scopeName) &&
                        !string.Equals(scopeName, zoneName, StringComparison.OrdinalIgnoreCase))
                        where += $" (область \"{scopeName}\")";
                }
                return $"ОШИБКА: {subject}{where} - {essence} (WIN32 {code}).";
            }

            if (ZoneEssence.TryGetValue(code, out var zoneEssence))
            {
                var subject = string.IsNullOrEmpty(zoneName) ? "Зона" : $"Зона \"{zoneName}\"";
                return $"ОШИБКА: {subject} {zoneEssence} (WIN32 {code}).";
            }

            return null;
        }

        private static string GetParam(Dictionary<string, object> parameters, string key) =>
            parameters != null && parameters.TryGetValue(key, out var v) ? v?.ToString() : null;
    }
}
