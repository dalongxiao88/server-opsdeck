using System;
using System.Collections.Generic;

namespace RDPManager
{
    public sealed class DetectedServicePort
    {
        public string ServiceType { get; set; }
        public string DisplayName { get; set; }
        public string ServiceName { get; set; }
        public string ConfigPath { get; set; }
        public string Protocol { get; set; }
        public int Port { get; set; }
        public bool IsSupported { get; set; }
        public string TargetKey { get; set; }
        public string ServiceStatus { get; set; }

        public override string ToString()
        {
            return string.Format("{0}  ·  {1}:{2}", DisplayName, Protocol, Port);
        }
    }

    public sealed class PortChangeRequest
    {
        public DetectedServicePort Target { get; set; }
        public int NewPort { get; set; }
        public bool ConfigureFirewall { get; set; }
        public bool VerifyAfterChange { get; set; }
        public bool KeepOldFirewallRule { get; set; }
        public bool ConfirmWebConfiguration { get; set; }
    }

    public sealed class PortChangeSession
    {
        public DetectedServicePort Target { get; set; }
        public int OldPort { get; set; }
        public int NewPort { get; set; }
        public string BackupPath { get; set; }
        public string FirewallRuleName { get; set; }
        public bool FirewallRuleCreated { get; set; }
        public string FirewallBackend { get; set; }
        public string FirewallPortSpec { get; set; }
        public string FirewallSourceIp { get; set; }
        public bool SelinuxRuleCreated { get; set; }
        public bool ServiceRestarted { get; set; }
        public bool VerifiedWithNewConnection { get; set; }
    }

    public sealed class FirewallPreparation
    {
        public string SourceIp { get; set; }
        public string RuleName { get; set; }
        public bool AllowedBefore { get; set; }
        public bool RuleCreated { get; set; }
    }

    public sealed class PortInspectionResult
    {
        public List<DetectedServicePort> Services { get; set; } = new List<DetectedServicePort>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Transport { get; set; }
        public string HostName { get; set; }
    }
}
