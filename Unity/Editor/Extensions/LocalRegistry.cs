using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>
    /// 本地已装扫描(命令管理器 EM3)。扫 Assets/AgentBridgeExtensions/* 读 manifest,
    /// 供命令→扩展归属(CommandCatalog)与卸载用。启停态不在此(归全局禁用名单 CommandToggle)。
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
