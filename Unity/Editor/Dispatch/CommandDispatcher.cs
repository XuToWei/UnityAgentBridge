using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 将 FileChannel 已校验的 Request 分发给对应命令处理器,并把异常统一转换为 error 响应。
    /// 每个被认领的请求都必须产生一份响应。
    /// </summary>
    internal static class CommandDispatcher
    {
        private const string BatchCommandNotAllowed = "BATCH_COMMAND_NOT_ALLOWED";

        internal static async Task<Response> DispatchAsync(Request request)
        {
            if (request == null)
            {
                return Error(
                    ErrorCodes.InternalError,
                    "ArgumentNullException: request is required");
            }

            if (!TryPrepare(
                    request.Command,
                    request.Params,
                    CommandInvocationPolicy.Single,
                    out var prepared,
                    out var error))
            {
                return error;
            }

            return await DispatchAsync(prepared);
        }

        /// <summary>
        /// 一次完成 resolve、batch 策略、禁用状态与 schema 校验。
        /// 返回的调用已准备完毕,执行时不再读取注册表或禁用状态。
        /// </summary>
        internal static bool TryPrepare(
            string command,
            JObject @params,
            CommandInvocationPolicy policy,
            out PreparedCommand prepared,
            out Response error)
        {
            prepared = null;
            error = null;
            try
            {
                if (!CommandRegistry.TryGetRegistered(command, out var registration))
                {
                    error = Error(ErrorCodes.UnknownCommand, $"no handler for '{command}'");
                    return false;
                }

                if (policy == CommandInvocationPolicy.BatchStep && !registration.BatchAllowed)
                {
                    error = Error(BatchCommandNotAllowed,
                        $"command '{command}' cannot be used in batch");
                    return false;
                }

                if (CommandRegistry.IsDisabled(command))
                {
                    error = Error(ErrorCodes.CommandDisabled, $"command '{command}' is disabled");
                    return false;
                }

                var normalizedParams = @params ?? new JObject();
                if (!JsonParamsValidator.TryValidate(
                        normalizedParams, registration.ParamsSchema, out var validationError))
                {
                    error = Error(ErrorCodes.InvalidParams, validationError);
                    return false;
                }

                prepared = new PreparedCommand(registration, normalizedParams);
                return true;
            }
            catch (Exception ex)
            {
                error = Error(ErrorCodes.InternalError, Summarize(ex));
                return false;
            }
        }

        /// <summary>仅执行已准备调用并统一转换 handler 错误,不重复预检。</summary>
        internal static async Task<Response> DispatchAsync(PreparedCommand prepared)
        {
            try
            {
                var result = await prepared.ExecuteAsync();
                return Ok(result);
            }
            catch (CommandException ce)
            {
                return Error(ce.Code, ce.Message);
            }
            catch (Exception ex)
            {
                return Error(ErrorCodes.HandlerException, Summarize(ex));
            }
        }

        private static Response Ok(object result)
        {
            return new Response
            {
                Status = "ok",
                Result = result == null ? JValue.CreateNull() : JToken.FromObject(result)
            };
        }

        internal static Response Error(string code, string message)
        {
            return new Response
            {
                Status = "error",
                Error = new ErrorInfo { Code = code, Message = message }
            };
        }

        private static string Summarize(Exception ex)
        {
            var msg = $"{ex.GetType().Name}: {ex.Message}";
            var trace = ex.StackTrace;
            if (!string.IsNullOrEmpty(trace))
            {
                var firstLine = trace.Split('\n')[0].Trim();
                if (firstLine.Length > 0)
                {
                    msg = $"{msg} | {firstLine}";
                }
            }
            return msg;
        }
    }

    internal enum CommandInvocationPolicy
    {
        Single,
        BatchStep
    }

    /// <summary>不可变的已准备调用;batch 可先收集全部实例再顺序执行。</summary>
    internal sealed class PreparedCommand
    {
        internal PreparedCommand(CommandRegistry.RegisteredCommand registration, JObject @params)
        {
            Command = registration.Command;
            m_Handler = registration.Handler;
            m_Params = (JObject)@params.DeepClone();
            SupportsUndoCollapse = registration.SupportsUndoCollapse;
        }

        private readonly ICommandHandler m_Handler;
        private readonly JObject m_Params;

        internal string Command { get; }
        internal bool SupportsUndoCollapse { get; }

        internal Task<object> ExecuteAsync()
        {
            return m_Handler.ExecuteAsync(m_Params);
        }
    }
}
