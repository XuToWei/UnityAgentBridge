using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 文件通讯模块：负责目录布局、请求认领、协议身份绑定及响应事务提交。
    /// 不依赖 Unity API，可用临时目录直接验证文件协议。
    /// </summary>
    public sealed class FileChannel
    {
        public const string RequestSuffix = ".request.json";
        public const string ResponseSuffix = ".response.json";
        private const string RequestTempSuffix = RequestSuffix + ".tmp";
        private const string ResponseTempSuffix = ResponseSuffix + ".tmp";

        /// <summary>
        /// FileChannel 创建的认领凭据。调用方只能读取规范身份和解析结果，不能接触或伪造物理路径。
        /// </summary>
        public sealed class ClaimedRequest
        {
            internal ClaimedRequest(
                FileChannel owner,
                string id,
                Request request,
                string errorCode,
                string errorMessage)
            {
                Owner = owner;
                Id = id;
                Request = request;
                ErrorCode = errorCode;
                ErrorMessage = errorMessage;
            }

            public string Id { get; }
            public Request Request { get; }
            public string ErrorCode { get; }
            public string ErrorMessage { get; }
            public bool CanDispatch => Request != null && string.IsNullOrEmpty(ErrorCode);

            internal FileChannel Owner { get; }
            internal bool IsCommitted { get; set; }
        }

        public string RootDir { get; }

        private string RequestsDir { get; }
        private string ProcessingDir { get; }
        private string ResponsesDir { get; }

        public FileChannel(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                throw new ArgumentException("bridge root directory must not be empty", nameof(rootDir));
            }

            RootDir = Path.GetFullPath(rootDir);
            RequestsDir = Path.Combine(RootDir, "requests");
            ProcessingDir = Path.Combine(RootDir, "processing");
            ResponsesDir = Path.Combine(RootDir, "responses");
            EnsureDirectories();
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(RequestsDir);
            Directory.CreateDirectory(ProcessingDir);
            Directory.CreateDirectory(ResponsesDir);
            EnsureGitIgnore();
        }

        /// <summary>
        /// 在根目录写 .gitignore(内容为 "*"),让运行时请求、响应和临时文件不进入版本库。
        /// 已存在则不改，尊重用户内容。
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
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                /* 写不成不影响桥接功能 */
            }
        }

        /// <summary>
        /// 优先收回 processing 中断 claim；没有中断 claim 时才认领 requests 中的最新请求。
        /// 调用方不需要了解 orphan-first 的事务顺序。
        /// </summary>
        public bool TryTakeNext(out ClaimedRequest claimed)
        {
            if (!TryTakeOrphan(out claimed) && !TryClaimLatest(out claimed))
            {
                return false;
            }

            // 单请求串行协议：claim 产生后、响应发布前，Agent 不会合法地写下一条请求。
            DeleteFiles(RequestsDir, RequestTempSuffix);
            return true;
        }

        /// <summary>
        /// 认领最新最终请求。旧最终请求会先被丢弃；processing 非空时不再认领新请求。
        /// 请求信封缺字段、身份不一致或版本不支持时返回不可分发的 claim。
        /// </summary>
        private bool TryClaimLatest(out ClaimedRequest claimed)
        {
            claimed = null;
            if (GetFiles(ProcessingDir, RequestSuffix).Count > 0)
            {
                return false;
            }

            var files = GetFiles(RequestsDir, RequestSuffix);
            if (files.Count == 0)
            {
                return false;
            }

            files.Sort(CompareByWriteTimeThenName);
            var latest = files[files.Count - 1];
            for (var i = 0; i < files.Count - 1; i++)
            {
                if (!TryDeleteFile(files[i]))
                {
                    return false;
                }
            }

            var latestName = Path.GetFileName(latest);
            var processingPath = SafeChildPath(ProcessingDir, latestName);
            try
            {
                File.Move(latest, processingPath); // 同卷 rename 原子认领
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }

            var canonicalId = StripSuffix(latestName, RequestSuffix);

            // id 不得复用。已有最终响应说明该 claim 已提交，只补 processing 清理，不重复执行。
            if (FinishPublishedClaim(canonicalId, true))
            {
                return false;
            }

            claimed = ParseClaim(canonicalId);
            return true;
        }

        /// <summary>
        /// 取上次执行中断留下的 processing claim。若存在多个，只保留最新一个；
        /// 若对应最终响应已存在，则仅补释放，不生成 INTERRUPTED 覆盖成功响应。
        /// </summary>
        private bool TryTakeOrphan(out ClaimedRequest claimed)
        {
            claimed = null;
            var files = GetFiles(ProcessingDir, RequestSuffix);
            if (files.Count == 0)
            {
                return false;
            }

            files.Sort(CompareByWriteTimeThenName);
            var latest = files[files.Count - 1];
            for (var i = 0; i < files.Count - 1; i++)
            {
                if (!TryDeleteFile(files[i]))
                {
                    throw new IOException("older orphan claim could not be discarded: " + files[i]);
                }
            }

            var latestName = Path.GetFileName(latest);
            var canonicalId = StripSuffix(latestName, RequestSuffix);
            if (FinishPublishedClaim(canonicalId, true))
            {
                return false;
            }

            claimed = new ClaimedRequest(
                this,
                canonicalId,
                null,
                ErrorCodes.Interrupted,
                "request was interrupted before its response was committed");
            return true;
        }

        /// <summary>
        /// 原子提交响应。最终文件发布是 commit point；在此之前失败会保留 claim 和旧响应。
        /// 发布后才清理其它响应并 best-effort 释放 processing claim。
        /// </summary>
        public void Commit(ClaimedRequest claim, Response response, string commandsVersion)
        {
            ValidateClaim(claim);
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            if (claim.IsCommitted)
            {
                return;
            }

            // Unity 是响应目录唯一写入者，提交前可直接清理上次失败留下的临时响应。
            DeleteFiles(ResponsesDir, ResponseTempSuffix);

            // 幂等恢复：final 已存在表示之前已经越过 commit point，绝不覆盖。
            if (FinishPublishedClaim(claim.Id, false))
            {
                claim.IsCommitted = true;
                return;
            }

            // final 已被后续事务清理时，旧 claim 不能再次发布并覆盖更新响应。
            if (!File.Exists(ProcessingPathForId(claim.Id)))
            {
                throw new InvalidOperationException("claim is no longer active: " + claim.Id);
            }

            PrepareResponseForPublish(response, claim.Id, commandsVersion);
            var json = JsonConvert.SerializeObject(response, Formatting.Indented);
            var responsePath = ResponsePathForId(claim.Id);
            var tempPath = responsePath + ".tmp";

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, responsePath); // 同目录原子发布
                claim.IsCommitted = true;
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            // final 已对 Agent 可见。之后的清理失败不能把已提交事务改回失败。
            FinishPublishedClaim(claim.Id, false);
        }

        private static void PrepareResponseForPublish(
            Response response,
            string canonicalId,
            string commandsVersion)
        {
            if (canonicalId == null)
            {
                throw new InvalidOperationException("response id must not be null");
            }

            response.V = 1;
            response.Id = canonicalId;
            response.CommandsVersion = commandsVersion ?? "";
            if (string.IsNullOrEmpty(response.Timestamp))
            {
                response.Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }

            if (response.Status == "ok")
            {
                if (response.Error != null)
                {
                    throw new InvalidOperationException("ok response must not contain error");
                }
                if (response.Result == null)
                {
                    response.Result = JValue.CreateNull();
                }
                return;
            }

            if (response.Status == "error")
            {
                if (response.Error == null)
                {
                    throw new InvalidOperationException("error response must contain error");
                }
                response.Result = null;
                return;
            }

            throw new InvalidOperationException("response status must be 'ok' or 'error'");
        }

        private ClaimedRequest ParseClaim(string canonicalId)
        {
            var processingPath = ProcessingPathForId(canonicalId);
            Request request;
            try
            {
                request = JsonConvert.DeserializeObject<Request>(File.ReadAllText(processingPath));
            }
            catch (JsonException)
            {
                return new ClaimedRequest(
                    this, canonicalId, null, ErrorCodes.InvalidRequest, "failed to parse request json");
            }

            var error = ValidateRequest(request, canonicalId);
            if (error != null)
            {
                return new ClaimedRequest(
                    this, canonicalId, null, ErrorCodes.InvalidRequest, error);
            }

            return new ClaimedRequest(this, canonicalId, request, null, null);
        }

        private static string ValidateRequest(Request request, string canonicalId)
        {
            if (request == null)
            {
                return "request json must be an object";
            }
            if (string.IsNullOrEmpty(canonicalId))
            {
                return "request filename id must not be empty";
            }
            if (request.V != 1)
            {
                return $"unsupported request version '{request.V}'; expected 1";
            }
            if (!string.Equals(request.Id, canonicalId, StringComparison.Ordinal))
            {
                return "request body id must exactly match filename id";
            }
            return string.IsNullOrEmpty(request.Command)
                ? "request command must not be empty"
                : null;
        }

        private void ValidateClaim(ClaimedRequest claim)
        {
            if (claim == null)
            {
                throw new ArgumentNullException(nameof(claim));
            }
            if (!ReferenceEquals(claim.Owner, this))
            {
                throw new ArgumentException("claim belongs to a different FileChannel", nameof(claim));
            }
        }

        private static List<string> GetFiles(string directory, string suffix)
        {
            var result = new List<string>();
            foreach (var file in Directory.GetFiles(directory, "*" + suffix))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    result.Add(file);
                }
            }
            return result;
        }

        private static void DeleteFiles(string directory, string suffix)
        {
            try
            {
                foreach (var file in GetFiles(directory, suffix))
                {
                    TryDeleteFile(file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                /* 临时文件清理失败不影响已认领请求或响应提交 */
            }
        }

        private static int CompareByWriteTimeThenName(string left, string right)
        {
            var byTime = File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
            return byTime != 0
                ? byTime
                : string.Compare(Path.GetFileName(left), Path.GetFileName(right), StringComparison.Ordinal);
        }

        private string ResponsePathForId(string canonicalId)
        {
            return SafeChildPath(ResponsesDir, canonicalId + ResponseSuffix);
        }

        private string ProcessingPathForId(string canonicalId)
        {
            return SafeChildPath(ProcessingDir, canonicalId + RequestSuffix);
        }

        /// <summary>
        /// final 存在即代表事务已提交。strictRelease 用于认领前阻止未释放 claim 与新请求并行。
        /// </summary>
        private bool FinishPublishedClaim(string canonicalId, bool strictRelease)
        {
            var responsePath = ResponsePathForId(canonicalId);
            if (!File.Exists(responsePath))
            {
                return false;
            }

            var released = TryDeleteFile(ProcessingPathForId(canonicalId));
            PruneResponses(responsePath);
            if (strictRelease && !released)
            {
                throw new IOException("published processing claim could not be released: " + canonicalId);
            }
            return true;
        }

        private static string SafeChildPath(string directory, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || Path.IsPathRooted(fileName) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            {
                throw new InvalidDataException("path must be a direct child filename: " + fileName);
            }

            var root = EnsureTrailingSeparator(Path.GetFullPath(directory));
            var fullPath = Path.GetFullPath(Path.Combine(root, fileName));
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(root, comparison))
            {
                throw new InvalidDataException("path escapes channel directory: " + fileName);
            }
            return fullPath;
        }

        private void PruneResponses(string keepPath)
        {
            try
            {
                foreach (var file in GetFiles(ResponsesDir, ResponseSuffix))
                {
                    if (!PathEquals(file, keepPath))
                    {
                        TryDeleteFile(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                /* final 已发布，清理失败不回滚事务 */
            }
        }

        private static bool PathEquals(string left, string right)
        {
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static bool TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
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
