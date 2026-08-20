using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DnsToolWinForms
{
    /// <summary>
    /// Простое хранилище настроек приложения в текстовом файле рядом с exe (формат
    /// "ключ=значение", по одной паре на строку). Сейчас используется только для
    /// запоминания позиции разделителей между списками (Scopes/записи, Политики/детали),
    /// чтобы при следующем запуске не пришлось растягивать их заново.
    /// </summary>
    public static class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (var line in File.ReadAllLines(SettingsPath))
                    {
                        var idx = line.IndexOf('=');
                        if (idx <= 0) continue;
                        Values[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                    }
                }
            }
            catch
            {
                // Повреждённый/недоступный файл настроек - просто работаем со значениями по умолчанию.
            }
        }

        public static int GetInt(string key, int defaultValue)
        {
            EnsureLoaded();
            return Values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : defaultValue;
        }

        public static void SetInt(string key, int value)
        {
            EnsureLoaded();
            Values[key] = value.ToString();
            Save();
        }

        /// <summary>Список строк (например истории серверов), хранится в одном значении через ';'.</summary>
        public static List<string> GetList(string key)
        {
            EnsureLoaded();
            if (!Values.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return new List<string>();
            return v.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        public static void SetList(string key, IEnumerable<string> items)
        {
            EnsureLoaded();
            Values[key] = string.Join(";", items.Where(s => !string.IsNullOrWhiteSpace(s)));
            Save();
        }

        private static void Save()
        {
            try
            {
                File.WriteAllLines(SettingsPath, Values.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch
            {
                // Нет прав на запись рядом с exe и т.п. - молча пропускаем, это не критично.
            }
        }
    }
}

