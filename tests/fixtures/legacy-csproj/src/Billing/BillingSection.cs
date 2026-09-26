using System;
using System.Configuration;

namespace Contoso.Billing
{
    /// <summary>The &lt;billing&gt; section of App.config.</summary>
    public class BillingSection : ConfigurationSection
    {
        [ConfigurationProperty("currency", DefaultValue = "USD")]
        public string Currency
        {
            get { return (string)this["currency"]; }
            set { this["currency"] = value; }
        }

        [ConfigurationProperty("retries", DefaultValue = 1)]
        public int Retries
        {
            get { return (int)this["retries"]; }
            set { this["retries"] = value; }
        }

        [ConfigurationProperty("timeout", DefaultValue = "00:00:10")]
        public TimeSpan Timeout
        {
            get { return (TimeSpan)this["timeout"]; }
            set { this["timeout"] = value; }
        }

        [ConfigurationProperty("audit", IsRequired = false)]
        public AuditElement Audit
        {
            get { return (AuditElement)this["audit"]; }
        }

        public static BillingSection Current
        {
            get { return (BillingSection)ConfigurationManager.GetSection("billing"); }
        }
    }

    public class AuditElement : ConfigurationElement
    {
        [ConfigurationProperty("enabled", DefaultValue = false)]
        public bool Enabled
        {
            get { return (bool)this["enabled"]; }
            set { this["enabled"] = value; }
        }

        [ConfigurationProperty("path")]
        public string Path
        {
            get { return (string)this["path"]; }
            set { this["path"] = value; }
        }
    }
}
