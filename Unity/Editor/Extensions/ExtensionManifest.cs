using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>仓库根 `extension.json` 的模型。对应 extension-manager roadmap 4.1。</summary>
    public sealed class ExtensionManifest
    {
        [JsonProperty("id")] public string Id { get; set; }                 // 唯一,小写字母/数字/连字符,= 安装目录名
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("version")] public string Version { get; set; }       // semver
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("author")] public string Author { get; set; }
        [JsonProperty("repo")] public string Repo { get; set; }            // git URL,溯源用
        [JsonProperty("unityMin")] public string UnityMin { get; set; }
        [JsonProperty("commands")] public List<string> Commands { get; set; } = new List<string>(); // 提供的命令名
        [JsonProperty("sourceDir")] public string SourceDir { get; set; } = "."; // 仓库内源码文件夹相对路径
    }
}
