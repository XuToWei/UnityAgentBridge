using System;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 将已解析的 Request 分发给对应命令处理器,并把所有异常统一转换为 error 响应。
    /// 每个被认领的请求都必须产生一份响应。
    /// </summary>
    public static class CommandDispatcher
    {
        public static Response Dispatch(Request request)
        {
            if (request == null)
            {
                return Response.MakeError(null, ErrorCodes.InternalError, "request was null or failed to parse");
            }

            if (string.IsNullOrEmpty(request.Command))
            {
                return Response.MakeError(request.Id, ErrorCodes.InvalidParams, "missing 'command'");
            }

            if (!CommandRegistry.TryGet(request.Command, out var handler))
            {
                return Response.MakeError(request.Id, ErrorCodes.UnknownCommand, $"no handler for '{request.Command}'");
            }

            if (CommandRegistry.IsDisabled(request.Command))
            {
                return Response.MakeError(request.Id, ErrorCodes.CommandDisabled, $"command '{request.Command}' is disabled");
            }

            try
            {
                var result = handler.Execute(request.Params ?? new JObject());
                return Response.MakeOk(request.Id, result);
            }
            catch (CommandException ce)
            {
                return Response.MakeError(request.Id, ce.Code, ce.Message);
            }
            catch (Exception ex)
            {
                return Response.MakeError(request.Id, ErrorCodes.HandlerException, Summarize(ex));
            }
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
