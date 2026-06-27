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
        }

        /// <summary>
        /// 认领下一个请求:把 requests/ 中最早的请求文件原子 rename 到 processing/ 再解析。
        /// rename 失败(已被认领 / 锁定)则跳过下一个。即使轮询重入也保证单次处理。
        /// </summary>
        /// <param name="claimedPath">认领后在 processing/ 的路径(用于处理完释放)。</param>
        /// <param name="request">解析后的请求;解析失败为 null(调用方写 INTERNAL_ERROR)。</param>
        /// <param name="rawId">从文件名提取的 id(解析失败时作响应 id 回退)。</param>
        public bool TryClaimNext(out string claimedPath, out Request request, out string rawId)
        {
            claimedPath = null;
            request = null;
            rawId = null;

            var files = Directory.GetFiles(RequestsDir, "*" + RequestSuffix);
            Array.Sort(files, StringComparer.Ordinal); // 文件名近似 FIFO

            foreach (var src in files)
            {
                var name = Path.GetFileName(src);
                // GetFiles 通配符在部分平台会误匹配,显式校验后缀。
                if (!name.EndsWith(RequestSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var dst = Path.Combine(ProcessingDir, name);
                try
                {
                    File.Move(src, dst); // 同卷 rename 原子;认领独占
                }
                catch (IOException)
                {
                    continue; // 已被认领 / 正被写入,跳过
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                claimedPath = dst;
                rawId = StripSuffix(name, RequestSuffix);
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

            return false;
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
            try
            {
                if (!string.IsNullOrEmpty(claimedPath) && File.Exists(claimedPath))
                {
                    File.Delete(claimedPath);
                }
            }
            catch (IOException)
            {
                /* 下次启动 ReclaimOrphans 会兜底 */
            }
        }

        /// <summary>清空 responses/ 中的全部文件(会话级残留清理,由 host 每会话调一次)。</summary>
        public void ClearResponses()
        {
            foreach (var file in Directory.GetFiles(ResponsesDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    /* 个别被占用则跳过,无害 */
                }
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

        private static string StripSuffix(string name, string suffix)
        {
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }
    }
}
