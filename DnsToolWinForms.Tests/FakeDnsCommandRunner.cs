using System.Collections.Generic;
using System.Management.Automation;
using DnsToolWinForms.Services;

namespace DnsToolWinForms.Tests
{
    /// <summary>
    /// Фейковый раннер для тестов сервисов: помнит все вызовы Invoke и выдаёт заранее
    /// заготовленные ответы по очереди. Никакого PowerShell - только запись параметров.
    /// </summary>
    public sealed class FakeDnsCommandRunner : IDnsCommandRunner
    {
        public string ComputerName { get; set; } = "";

        public List<(string Cmdlet, Dictionary<string, object> Parameters)> Calls { get; } =
            new List<(string, Dictionary<string, object>)>();

        private readonly Queue<(List<PSObject> Results, string Log)> _responses =
            new Queue<(List<PSObject>, string)>();

        public void Enqueue(List<PSObject> results, string log = "OK: выполнено")
        {
            _responses.Enqueue((results, log));
        }

        public (List<PSObject> Results, string Log) Invoke(string cmdlet,
            Dictionary<string, object> parameters = null, bool applyGlobalComputerName = true)
        {
            Calls.Add((cmdlet, parameters));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : (new List<PSObject>(), "OK: (нет заготовленного ответа)");
        }
    }

    /// <summary>Хелпер сборки PSObject с заданными свойствами - как их возвращает PowerShell.</summary>
    public static class TestObjects
    {
        public static PSObject Obj(params (string Name, object Value)[] properties)
        {
            var o = new PSObject();
            foreach (var (name, value) in properties)
                o.Properties.Add(new PSNoteProperty(name, value));
            return o;
        }

        /// <summary>Зона: только поля, по которым DnsZoneService классифицирует применимость Zone Scopes.</summary>
        public static PSObject Zone(string name, string type, bool isAutoCreated = false, bool? isReverse = null)
        {
            return Obj(
                ("ZoneName", name),
                ("ZoneType", type),
                ("IsAutoCreated", isAutoCreated),
                ("IsReverseLookupZone", (object)isReverse)
            );
        }
    }
}
