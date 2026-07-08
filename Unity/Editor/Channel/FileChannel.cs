using System;
using System.IO;
using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>
    /// 文件通讯物理层(M2)。负责目录布局、原子写、请求认领、响应产出。平台无关。
    /// 对应 file-bridge roadmap 4.2。
    /// </summary>
    public sealed class FileChannel
    {
        public const string RequestSuffix = ".request.json";
        public const string ResponseSuffix = ".response.json";

        public string RootDir { get; }
        public string RequestsDir { get; }
        public string ProcessingDir { get; }
        public string ResponsesDir { get; }

        public FileChannel(string rootDir)
        {
            RootDir = rootDir;
            RequestsDir = Path.Combine(rootDir, "requests");
            ProcessingDir = Path.Combine(rootDir, "processing");
            ResponsesDir = Path.Combine(rootDir, "responses");
            EnsureDirectories();
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(RequestsDir);
            Directory.CreateDirectory(ProcessingDir);
            Directory.CreateDirectory(ResponsesDir);
            EnsureGitIgnore();
        }

        /// <summary>
        /// 在根目录写 .gitignore(内容为 "*"),让整个通讯目录连同请求/响应临时文件都被 git 忽略——
        /// 这些是运行时产物,不应进版本库。已存在则不动(尊重用户手改)。
        /// </summary>
        private void EnsureGitIgnore()
        {
            var path = Path.Combine(RootDir, ".gitignore");
            if (File.Exists(path))
            {
                return;
            }
            try
            {
                File.WriteAllText(path, "*" + Environment.NewLine);
            }
            catch (IOException)
            {
                /* 写不成不影响桥接功能,忽略 */
            }
        }

        /// <summary>
        /// 认领最新请求:删除 requests/ 中除最新最终请求外的其它请求文件,再把最新请求原子 rename 到 processing/ 解析。
        /// 最新按 LastWriteTimeUtc 判定;并发写入中的 .tmp 不会被触碰。rename 失败则本轮不回退处理旧请求。
        /// </summary>
        /// <param name="claimedPath">认领后在 processing/ 的路径(用于处理完释放)。</param>
        /// <param name="request">解析后的请求;解析失败为 null(调用方写 INTERNAL_ERROR)。</param>
        /// <param name="rawId">从文件名提取的 id(解析失败时作响应 id 回退)。</param>
        public bool TryClaimLatest(out string claimedPath, out Request request, out string rawId)
        {
            claimedPath = null;
            request = null;
            rawId = null;

            var files = Directory.GetFiles(RequestsDir, "*" + RequestSuffix);
            string latest = null;
            string latestName = null;
            var latestTime = DateTime.MinValue;

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                // GetFiles 通配符在部分平台会误匹配,显式校验后缀;临时文件(.tmp)不会进来。
                if (!name.EndsWith(RequestSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var time = File.GetLastWriteTimeUtc(file);
                if (latest == null || time > latestTime ||
                    (time == latestTime && string.Compare(name, latestName, StringComparison.Ordinal) > 0))
                {
                    latest = file;
                    latestName = name;
                    latestTime = time;
                }
            }

            if (latest == null)
            {
                return false;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(RequestSuffix, StringComparison.Ordinal) &&
                    !string.Equals(file, latest, StringComparison.Ordinal))
                {
                    TryDeleteFile(file);
                }
            }

            var dst = Path.Combine(ProcessingDir, latestName);
            try
            {
                File.Move(latest, dst); // 同卷 rename 原子;认领独占
            }
            catch (IOException)
            {
                return false; // 已被认领 / 正被写入;不回退处理旧请求
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            claimedPath = dst;
            rawId = StripSuffix(latestName, RequestSuffix);
            try
            {
                var json = File.ReadAllText(dst);
                request = JsonConvert.DeserializeObject<Request>(json);
            }
            catch
            {
                request = null; // 解析失败 → caller 写 INTERNAL_ERROR
            }
            return true;
        }

        /// <summary>原子写响应:先写 {id}.response.json.tmp,再 rename 成最终名。读方只认最终名。</summary>
        public void WriteResponse(Response response)
        {
            var finalPath = Path.Combine(ResponsesDir, response.Id + ResponseSuffix);
            var tmpPath = finalPath + ".tmp";
            var json = JsonConvert.SerializeObject(response, Formatting.Indented);
            File.WriteAllText(tmpPath, json);
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(tmpPath, finalPath); // 原子发布
        }

        /// <summary>处理完毕删除 processing/ 中的请求文件。</summary>
        public void ReleaseProcessed(string claimedPath)
        {
            TryDeleteFile(claimedPath);
        }

        /// <summary>清空 responses/ 中的全部文件(由 host 在每次写响应前调用)。</summary>
        public void ClearResponses()
        {
            foreach (var file in Directory.GetFiles(ResponsesDir))
            {
                TryDeleteFile(file);
            }
        }

        /// <summary>列举 processing/ 中的孤儿请求(上次会话被 domain reload 打断遗留)。</summary>
        public string[] ListOrphans()
        {
            return Directory.GetFiles(ProcessingDir, "*" + RequestSuffix);
        }

        /// <summary>从 processing/ 路径提取请求 id。</summary>
        public string IdFromProcessingPath(string path)
        {
            return StripSuffix(Path.GetFileName(path), RequestSuffix);
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                /* 被占用则跳过 */
            }
            catch (UnauthorizedAccessException)
            {
                /* 无权限时跳过 */
            }
        }

        private static string StripSuffix(string name, string suffix)
        {
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }
    }
}
