using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 固定单槽文件通讯：处理一个完整 Exchange，并负责 Claim、恢复、校验、响应发布与清理。
    /// 不依赖 Unity API，可用临时目录直接验证文件协议。
    /// </summary>
    internal sealed class FileChannel
    {
        internal const string RequestFileName = "request.json";
        internal const string ProcessingFileName = "processing.json";
        internal const string ResponseFileName = "response.json";

        /// <summary>单个请求或响应文件固定为最多 1 MiB。</summary>
        internal const long MaxFileBytes = 1024L * 1024;

        private readonly string m_RequestPath;
        private readonly string m_ProcessingPath;
        private readonly string m_ResponsePath;

        internal string RootDir { get; }

        internal FileChannel(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                throw new ArgumentException("bridge root directory must not be empty", nameof(rootDir));
            }

            RootDir = Path.GetFullPath(rootDir);
            m_RequestPath = Path.Combine(RootDir, RequestFileName);
            m_ProcessingPath = Path.Combine(RootDir, ProcessingFileName);
            m_ResponsePath = Path.Combine(RootDir, ResponseFileName);
        }

        /// <summary>仅打开已经存在的 Bridge root，绝不创建目录。</summary>
        internal static bool TryOpenExisting(string rootDir, out FileChannel channel)
        {
            if (Directory.Exists(rootDir))
            {
                channel = new FileChannel(rootDir);
                return true;
            }

            channel = null;
            return false;
        }

        /// <summary>
        /// 处理至多一个 Exchange。Claim 后直接 await 命令完成并发布响应；调用方必须
        /// 避免并发调用。await 期间
        /// processing.json 保留，domain reload 后由新实例按 INTERRUPTED 恢复。
        /// </summary>
        internal async CommandTask<bool> TryProcessOneAsync(
            Func<Request, CommandTask<Response>> dispatch,
            Func<string> getCommandsVersion)
        {
            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }
            if (getCommandsVersion == null)
            {
                throw new ArgumentNullException(nameof(getCommandsVersion));
            }

            if (File.Exists(m_ProcessingPath))
            {
                if (File.Exists(m_ResponsePath))
                {
                    // 响应已经发布；domain reload 只留下了待补做的 Claim 清理。
                    AtomicFilePublisher.DeleteBestEffort(m_ProcessingPath);
                    return false;
                }

                // Claim 后没有终态响应，命令可能已经产生副作用，绝不重新执行。
                PublishResponse(
                    Error(
                        ErrorCodes.Interrupted,
                        "request was interrupted before its response was committed"),
                    ReadResponseId(m_ProcessingPath),
                    getCommandsVersion());
                return true;
            }

            // Agent 读完并删除当前响应，才算确认上一 Exchange。
            if (File.Exists(m_ResponsePath) || !File.Exists(m_RequestPath))
            {
                return false;
            }

            if (!TryClaim())
            {
                return false;
            }

            var request = ParseClaim(out var responseId, out var validationError);
            var response = request == null
                ? Error(ErrorCodes.InvalidRequest, validationError)
                : await dispatch(request);
            PublishResponse(response, responseId, getCommandsVersion());
            return true;
        }

        private bool TryClaim()
        {
            try
            {
                File.Move(m_RequestPath, m_ProcessingPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private Request ParseClaim(out string responseId, out string error)
        {
            responseId = "";
            JObject envelope;
            try
            {
                if (new FileInfo(m_ProcessingPath).Length > MaxFileBytes)
                {
                    error = $"request exceeds {MaxFileBytes} bytes";
                    return null;
                }

                var token = JToken.Parse(
                    File.ReadAllText(m_ProcessingPath),
                    new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                if (token.Type != JTokenType.Object)
                {
                    error = "request json must be an object";
                    return null;
                }
                envelope = (JObject)token;
            }
            catch (JsonException)
            {
                error = "failed to parse request json";
                return null;
            }

            responseId = ResponseIdFrom(envelope["id"]);
            error = ValidateRequest(envelope);
            if (error != null)
            {
                return null;
            }

            return new Request
            {
                V = 1,
                Id = responseId,
                Command = envelope["command"].Value<string>(),
                Params = (JObject)envelope["params"]
            };
        }

        private static string ValidateRequest(JObject request)
        {
            var version = request["v"];
            if (version == null || version.Type != JTokenType.Integer)
            {
                return "request v must be integer 1";
            }

            long versionNumber;
            try
            {
                versionNumber = version.Value<long>();
            }
            catch (Exception ex) when (
                ex is OverflowException ||
                ex is FormatException ||
                ex is InvalidCastException)
            {
                return "request v must be integer 1";
            }
            if (versionNumber != 1)
            {
                return $"unsupported request version '{version}'; expected 1";
            }

            var id = request["id"];
            if (id == null || id.Type != JTokenType.String)
            {
                return "request id must be a string";
            }
            var idValue = id.Value<string>();
            if (string.IsNullOrEmpty(idValue) || idValue.Length > 64)
            {
                return "request id must contain 1 to 64 characters";
            }

            var command = request["command"];
            if (command == null || command.Type != JTokenType.String ||
                string.IsNullOrEmpty(command.Value<string>()))
            {
                return "request command must be a non-empty string";
            }

            var @params = request["params"];
            return @params == null || @params.Type != JTokenType.Object
                ? "request params must be an object"
                : null;
        }

        private static string ReadResponseId(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    return string.Empty;
                }

                var token = JToken.Parse(
                    File.ReadAllText(path),
                    new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                return token is JObject envelope
                    ? ResponseIdFrom(envelope["id"])
                    : "";
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is JsonException)
            {
                return "";
            }
        }

        private static string ResponseIdFrom(JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                return "";
            }

            var id = token.Value<string>();
            return !string.IsNullOrEmpty(id) && id.Length <= 64 ? id : "";
        }

        private void PublishResponse(Response response, string id, string commandsVersion)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var bytes = BuildResponseBytes(response, id, commandsVersion);
            AtomicFilePublisher.PublishRecoverableNew(
                m_ResponsePath,
                temp => File.WriteAllBytes(temp, bytes));

            // response.json 已经是提交点，Claim 清理失败由下次轮询补做。
            AtomicFilePublisher.DeleteBestEffort(m_ProcessingPath);
        }

        private static byte[] BuildResponseBytes(
            Response response,
            string id,
            string commandsVersion)
        {
            PrepareResponseForPublish(response, id, commandsVersion);
            var bytes = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(response, Formatting.None));
            if (bytes.LongLength <= MaxFileBytes)
            {
                return bytes;
            }

            var compactError = Error(
                ErrorCodes.ResponseTooLarge,
                $"response exceeded {MaxFileBytes} bytes (actual {bytes.LongLength})");
            PrepareResponseForPublish(compactError, id, commandsVersion);
            var fallbackBytes = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(compactError, Formatting.None));
            if (fallbackBytes.LongLength > MaxFileBytes)
            {
                throw new InvalidOperationException(
                    $"response byte limit {MaxFileBytes} is too small for the structured error envelope");
            }
            return fallbackBytes;
        }

        private static void PrepareResponseForPublish(
            Response response,
            string id,
            string commandsVersion)
        {
            response.V = 1;
            response.Id = id ?? "";
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

        private static Response Error(string code, string message)
        {
            return new Response
            {
                Status = "error",
                Error = new ErrorInfo
                {
                    Code = code,
                    Message = message
                }
            };
        }

    }
}
