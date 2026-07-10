using System;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 将 FileChannel 已校验的 Request 分发给对应命令处理器,并把异常统一转换为 error 响应。
    /// 每个被认领的请求都必须产生一份响应。
    /// </summary>
    internal static class CommandDispatcher
    {
        internal static Response Dispatch(Request request)
        {
            ICommandHandler handler;
            try
            {
                if (!CommandRegistry.TryGet(request.Command, out handler))
                {
                    return Error(ErrorCodes.UnknownCommand, $"no handler for '{request.Command}'");
                }

                if (CommandRegistry.IsDisabled(request.Command))
                {
                    return Error(ErrorCodes.CommandDisabled, $"command '{request.Command}' is disabled");
                }
            }
            catch (Exception ex)
            {
                return Error(ErrorCodes.InternalError, Summarize(ex));
            }

            try
            {
                var result = handler.Execute(request.Params ?? new JObject());
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
            var msg = ex.GetType().Name + ": " + ex.Message;
            var trace = ex.StackTrace;
            if (!string.IsNullOrEmpty(trace))
            {
                var firstLine = trace.Split('\n')[0].Trim();
                if (firstLine.Length > 0)
                {
                    msg += " | " + firstLine;
                }
            }
            return msg;
        }
    }
}
