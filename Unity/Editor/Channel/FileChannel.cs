using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 文件通讯模块：维持单请求、单响应，并负责认领、校验与清理通讯文件。
    /// 不依赖 Unity API，可用临时目录直接验证文件协议。
    /// </summary>
    public sealed class FileChannel
    {
        public const string RequestSuffix = ".request.json";
        public const string ResponseSuffix = ".response.json";
        private const string RequestTempSuffix = RequestSuffix + ".tmp";
        private const string ResponseTempSuffix = ResponseSuffix + ".tmp";

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
                // .gitignore 写入失败不影响通讯。
            }
        }

        /// <summary>
        /// 每次只取一个请求。若上次在 processing 中中断，先返回 INTERRUPTED；
        /// 否则认领 requests 中最新的最终请求，并清掉其余通讯文件。
        /// </summary>
        public bool TryTakeNext(out ClaimedRequest claimed)
        {
            if (TryRecoverProcessing(out claimed))
            {
                return true;
            }

            return TryClaimLatest(out claimed);
        }

        /// <summary>
        /// 恢复上次中断的单个请求。响应已经发布时只补清理 processing；
        /// 尚未发布时丢弃所有抢发请求并返回 INTERRUPTED，避免执行结果不确定时继续下一条。
        /// </summary>
        private bool TryRecoverProcessing(out ClaimedRequest claimed)
        {
            claimed = null;
            if (!TryKeepLatest(ProcessingDir, RequestSuffix, out var activePath))
            {
                return false;
            }

            var id = StripSuffix(Path.GetFileName(activePath), RequestSuffix);
            var responsePath = ResponsePathForId(id);
            if (File.Exists(responsePath))
            {
                // 响应是提交点。只保留它，释放可能因 reload 遗留的 processing 文件。
                if (!DeleteFiles(ResponsesDir, ResponseTempSuffix) ||
                    !DeleteFilesExcept(ResponsesDir, ResponseSuffix, responsePath) ||
                    !TryDeleteFile(activePath))
                {
                    return false;
                }

                return false;
            }

            // 没有响应时无法判断命令是否执行过。清空其它轮次，只返回一次 INTERRUPTED。
            if (!ClearRequests() || !ClearResponses())
            {
                return false;
            }

            claimed = new ClaimedRequest(
                this,
                id,
                null,
                ErrorCodes.Interrupted,
                "request was interrupted before its response was committed");
            return true;
        }

        /// <summary>
        /// 只认领最新最终请求。新请求是上一响应已被 Agent 读取的隐式确认，
        /// 因此认领前清掉旧响应、旧请求和临时文件；清理失败则本轮不执行。
        /// </summary>
        private bool TryClaimLatest(out ClaimedRequest claimed)
        {
            claimed = null;
            if (GetFiles(ProcessingDir, RequestSuffix).Count > 0)
            {
                return false;
            }

            if (!TryKeepLatest(RequestsDir, RequestSuffix, out var latest))
            {
                return false;
            }

            // 单通讯协议下，最终请求出现后不应再有写入中的请求。
            if (!DeleteFiles(RequestsDir, RequestTempSuffix) || !ClearResponses())
            {
                return false;
            }

            var latestName = Path.GetFileName(latest);
            var processingPath = Path.Combine(ProcessingDir, latestName);
            try
            {
                File.Move(latest, processingPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }

            var id = StripSuffix(latestName, RequestSuffix);
            claimed = ParseClaim(id);
            return true;
        }

        /// <summary>
        /// 原子发布当前响应。成功后删除 processing 请求；正常空闲时只留下这一份响应，
        /// 它会在下一条请求被认领前由 Unity 清理。
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

            var responsePath = ResponsePathForId(claim.Id);
            var processingPath = ProcessingPathForId(claim.Id);

            // domain reload 可能发生在响应发布后、processing 删除前。
            if (File.Exists(responsePath))
            {
                claim.IsCommitted = true;
                TryDeleteFile(processingPath);
                return;
            }
            if (!File.Exists(processingPath))
            {
                throw new InvalidOperationException("claim is no longer active: " + claim.Id);
            }

            PrepareResponseForPublish(response, claim.Id, commandsVersion);
            if (!ClearResponses())
            {
                throw new IOException("previous response files could not be cleared");
            }

            var tempPath = responsePath + ".tmp";
            var json = JsonConvert.SerializeObject(response, Formatting.Indented);
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, responsePath);
                claim.IsCommitted = true;
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            // 响应已可见，processing 清理失败不影响结果；下次轮询会补清理。
            TryDeleteFile(processingPath);
        }

        private static void PrepareResponseForPublish(
            Response response,
            string id,
            string commandsVersion)
        {
            if (id == null)
            {
                throw new InvalidOperationException("response id must not be null");
            }

            response.V = 1;
            response.Id = id;
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

        private ClaimedRequest ParseClaim(string id)
        {
            Request request;
            try
            {
                request = JsonConvert.DeserializeObject<Request>(
                    File.ReadAllText(ProcessingPathForId(id)));
            }
            catch (JsonException)
            {
                return new ClaimedRequest(
                    this, id, null, ErrorCodes.InvalidRequest, "failed to parse request json");
            }

            var error = ValidateRequest(request, id);
            return error == null
                ? new ClaimedRequest(this, id, request, null, null)
                : new ClaimedRequest(this, id, null, ErrorCodes.InvalidRequest, error);
        }

        private static string ValidateRequest(Request request, string id)
        {
            if (request == null)
            {
                return "request json must be an object";
            }
            if (string.IsNullOrEmpty(id))
            {
                return "request filename id must not be empty";
            }
            if (request.V != 1)
            {
                return $"unsupported request version '{request.V}'; expected 1";
            }
            if (!string.Equals(request.Id, id, StringComparison.Ordinal))
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

        private bool ClearRequests()
        {
            var finalsCleared = DeleteFiles(RequestsDir, RequestSuffix);
            var tempsCleared = DeleteFiles(RequestsDir, RequestTempSuffix);
            return finalsCleared && tempsCleared;
        }

        private bool ClearResponses()
        {
            var finalsCleared = DeleteFiles(ResponsesDir, ResponseSuffix);
            var tempsCleared = DeleteFiles(ResponsesDir, ResponseTempSuffix);
            return finalsCleared && tempsCleared;
        }

        private static List<string> GetFiles(string directory, string suffix)
        {
            var result = new List<string>();
            foreach (var file in Directory.GetFiles(directory, "*" + suffix))
            {
                if (Path.GetFileName(file).EndsWith(suffix, StringComparison.Ordinal))
                {
                    result.Add(file);
                }
            }
            return result;
        }

        private static bool TryKeepLatest(string directory, string suffix, out string latest)
        {
            latest = null;
            var files = GetFiles(directory, suffix);
            if (files.Count == 0)
            {
                return false;
            }

            files.Sort(CompareByWriteTimeThenName);
            latest = files[files.Count - 1];
            for (var i = 0; i < files.Count - 1; i++)
            {
                if (!TryDeleteFile(files[i]))
                {
                    latest = null;
                    return false;
                }
            }
            return true;
        }

        private static bool DeleteFiles(string directory, string suffix)
        {
            var success = true;
            foreach (var file in GetFiles(directory, suffix))
            {
                if (!TryDeleteFile(file))
                {
                    success = false;
                }
            }
            return success;
        }

        private static bool DeleteFilesExcept(string directory, string suffix, string keepPath)
        {
            var success = true;
            foreach (var file in GetFiles(directory, suffix))
            {
                if (!PathEquals(file, keepPath) && !TryDeleteFile(file))
                {
                    success = false;
                }
            }
            return success;
        }

        private static int CompareByWriteTimeThenName(string left, string right)
        {
            var byTime = File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
            return byTime != 0
                ? byTime
                : string.Compare(Path.GetFileName(left), Path.GetFileName(right), StringComparison.Ordinal);
        }

        private string ResponsePathForId(string id)
        {
            return Path.Combine(ResponsesDir, id + ResponseSuffix);
        }

        private string ProcessingPathForId(string id)
        {
            return Path.Combine(ProcessingDir, id + RequestSuffix);
        }

        private static bool PathEquals(string left, string right)
        {
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
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
