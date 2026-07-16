using System;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class UndoHandler : ICommandHandler
    {
        public string Command => "undo";
        public string Description => "执行 Unity 全局 Undo(可能撤销用户手工操作);空栈时 ok 且 eventObserved=false";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            var observed = false;
            Undo.UndoRedoCallback callback = () => observed = true;
            Undo.undoRedoPerformed += callback;
            try
            {
                Undo.PerformUndo();
            }
            catch (Exception ex)
            {
                throw new CommandException("UNDO_FAILED", ex.Message);
            }
            finally
            {
                Undo.undoRedoPerformed -= callback;
            }
            return new { requested = true, eventObserved = observed };
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
