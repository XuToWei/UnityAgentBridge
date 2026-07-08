using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>
    /// 扫描本地已安装扩展:读取 Assets/AgentBridgeExtensions/*/extension.json。
    /// 结果用于判断命令归属和卸载目标;启停状态由 CommandToggle 统一管理。
    /// </summary>
    public static class LocalRegistry
    {
        public static List<InstalledExtension> Scan()
        {
            var result = new List<InstalledExtension>();
            var root = ExtensionInstaller.InstallRoot;
            if (!Directory.Exists(root))
            {
                return result;
            }

            foreach (var dir in Directory.GetDirectories(root))
            {
                var manifest = TryRead<ExtensionManifest>(Path.Combine(dir, "extension.json"));
                if (manifest == null)
                {
                    continue;
                }

                result.Add(new InstalledExtension
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Commands = manifest.Commands ?? new List<string>()
                });
            }
            return result;
        }

        private static T TryRead<T>(string path) where T : class
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }
    }
}
