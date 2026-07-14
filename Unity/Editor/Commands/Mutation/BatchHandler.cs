using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class BatchHandler : ICommandHandler
    {
        public string Command => "batch";
        public string Description => "顺序执行 1..50 个已预校验且显式允许批处理的子命令;非原子";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            var steps = (JArray)@params["steps"];
            var stopOnError = SceneCommandSupport.ReadBool(@params, "stopOnError", true);
            var collapseUndo = SceneCommandSupport.ReadBool(@params, "collapseUndo", true);
            var validated = new List<PreparedCommand>(steps.Count);

            for (var i = 0; i < steps.Count; i++)
            {
                var stepObject = (JObject)steps[i];
                var command = stepObject["command"].Value<string>();
                var stepParams = stepObject["params"] as JObject ?? new JObject();
                if (!CommandDispatcher.TryPrepare(
                        command,
                        stepParams,
                        CommandInvocationPolicy.BatchStep,
                        out var prepared,
                        out var error))
                {
                    var errorInfo = error.Error;
                    throw new CommandException(
                        errorInfo.Code,
                        $"steps[{i}] {command}: {errorInfo.Message}");
                }
                validated.Add(prepared);
            }

            var canCollapse = collapseUndo && !EditorApplication.isPlaying &&
                              validated.TrueForAll(step => step.SupportsUndoCollapse);
            var undoGroup = -1;
            if (canCollapse)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("AgentBridge batch");
            }

            var results = new JArray();
            var failedIndex = -1;
            try
            {
                for (var i = 0; i < validated.Count; i++)
                {
                    var step = validated[i];
                    var response = CommandDispatcher.Dispatch(step);
                    var result = new JObject
                    {
                        ["index"] = i,
                        ["command"] = step.Command,
                        ["status"] = response.Status
                    };
                    if (response.Status == "ok")
                    {
                        result["result"] = response.Result ?? JValue.CreateNull();
                        result["error"] = JValue.CreateNull();
                    }
                    else
                    {
                        result["result"] = JValue.CreateNull();
                        result["error"] = response.Error == null
                            ? JValue.CreateNull()
                            : JObject.FromObject(response.Error);
                        if (failedIndex < 0)
                        {
                            failedIndex = i;
                        }
                    }
                    results.Add(result);
                    if (response.Status != "ok" && stopOnError)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (canCollapse)
                {
                    Undo.CollapseUndoOperations(undoGroup);
                }
            }

            return new
            {
                success = failedIndex < 0,
                executed = results.Count,
                requested = validated.Count,
                failedIndex = failedIndex < 0 ? (int?)null : failedIndex,
                stopped = failedIndex >= 0 && stopOnError && results.Count < validated.Count,
                collapsedUndo = canCollapse,
                results
            };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""steps"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""command"": { ""type"": ""string"", ""minLength"": 1 },
          ""params"": { ""type"": ""object"" }
        },
        ""required"": [""command""],
        ""additionalProperties"": false
      }
    },
    ""stopOnError"": { ""type"": ""boolean"", ""default"": true },
    ""collapseUndo"": { ""type"": ""boolean"", ""default"": true }
  },
  ""required"": [""steps""],
  ""additionalProperties"": false
}");

    }
}
