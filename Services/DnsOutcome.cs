using System;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// DnsHelper.Invoke возвращает текстовый лог вида "OK: ..." или "ОШИБКА: ..." /
    /// "ИСКЛЮЧЕНИЕ ...". Этот хелпер определяет по нему успех операции - единая точка
    /// для MainForm, UserControls и сервисов.
    /// </summary>
    public static class DnsOutcome
    {
        public static bool WasSuccess(string log) =>
            !string.IsNullOrEmpty(log) &&
            !log.Contains("ОШИБКА:") &&
            !log.Contains("ИСКЛЮЧЕНИЕ");
    }
}
