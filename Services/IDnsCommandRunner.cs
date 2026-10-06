using System.Collections.Generic;
using System.Management.Automation;

namespace DnsToolWinForms.Services
{
    public interface IDnsCommandRunner
    {
        string ComputerName { get; set; }

        (List<PSObject> Results, string Log) Invoke(string cmdlet, Dictionary<string, object> parameters = null, bool applyGlobalComputerName = true);
    }
}
