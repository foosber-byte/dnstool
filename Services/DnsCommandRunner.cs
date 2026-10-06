using System.Collections.Generic;
using System.Management.Automation;

namespace DnsToolWinForms.Services
{
    public sealed class DnsCommandRunner : IDnsCommandRunner
    {
        public static readonly DnsCommandRunner Instance = new DnsCommandRunner();

        public string ComputerName
        {
            get { return DnsHelper.ComputerName; }
            set { DnsHelper.ComputerName = value; }
        }

        public (List<PSObject> Results, string Log) Invoke(string cmdlet, Dictionary<string, object> parameters = null, bool applyGlobalComputerName = true)
        {
            return DnsHelper.Invoke(cmdlet, parameters, applyGlobalComputerName);
        }
    }
}
