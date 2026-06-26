using System.Collections.Generic;

namespace AgentBridge
{
    /// <summary>本地扫描出的一条已装扩展。对应 extension-manager roadmap 4.5。</summary>
    public sealed class InstalledExtension
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public List<string> Commands { get; set; } = new List<string>();    // manifest 声明的全部命令(供命令→扩展归属)
    }
}
