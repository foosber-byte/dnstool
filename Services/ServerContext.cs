using System;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// Текущий целевой DNS-сервер приложения (пусто = локальный компьютер). Единственная
    /// точка записи глобального DnsHelper.ComputerName: и верхняя панель "Целевой сервер",
    /// и навигация по дереву переводят контекст только через этот класс.
    /// </summary>
    public sealed class ServerContext
    {
        private readonly IDnsCommandRunner _runner;

        /// <summary>Срабатывает, когда целевой сервер реально сменился (не при повторной установке того же значения).</summary>
        public event EventHandler CurrentServerChanged;

        public ServerContext(IDnsCommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>Имя текущего целевого сервера; пустая строка = локальный компьютер.</summary>
        public string CurrentServer
        {
            get
            {
                var value = _runner.ComputerName;
                return value == null ? "" : value.Trim();
            }
        }

        public bool IsLocal => string.IsNullOrWhiteSpace(CurrentServer);

        /// <summary>Имя сервера для заголовков/файлов выгрузки: локальный показываем как имя этой машины.</summary>
        public string DisplayLabel => IsLocal ? Environment.MachineName : CurrentServer;

        /// <summary>
        /// Переключает целевой сервер. Тот же сценарий, что у DnsHelper.ComputerName напрямую,
        /// но с событием для подписчиков и защитой от повторной установки того же значения.
        /// </summary>
        public void Set(string serverName)
        {
            var newValue = (serverName ?? "").Trim();
            var changed = !string.Equals(_runner.ComputerName, newValue, StringComparison.Ordinal);
            _runner.ComputerName = newValue;
            if (changed) CurrentServerChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Временно переключает целевой сервер на время одного запроса и гарантирует возврат
        /// прежнего (замена ручного паттерна "запомнить - подменить - вернуть"). Restore
        /// выполняется в Dispose, поэтому оборачивайте и сам await запроса.
        /// </summary>
        public TemporaryServerSwitch BeginTemporaryServer(string serverName)
        {
            return new TemporaryServerSwitch(this, serverName);
        }

        public sealed class TemporaryServerSwitch : IDisposable
        {
            private readonly ServerContext _context;
            private readonly string _previous;

            internal TemporaryServerSwitch(ServerContext context, string serverName)
            {
                _context = context;
                _previous = context._runner.ComputerName;
                context._runner.ComputerName = (serverName ?? "").Trim();
            }

            public void Dispose()
            {
                _context._runner.ComputerName = _previous;
            }
        }
    }
}
