using System.Threading.Tasks;
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class RedoHandler : ICommandHandler
    {
        public string Command => "redo";
        public string Description => "执行 Unity 全局 Redo(可能重做用户手工操作);空栈时 ok 且 eventObserved=false";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            var observed = false;
            Undo.UndoRedoCallback callback = () => observed = true;
            Undo.undoRedoPerformed += callback;
            try
            {
                Undo.PerformRedo();
            }
            catch (Exception ex)
            {
                throw new CommandException("REDO_FAILED", ex.Message);
            }
            finally
            {
                Undo.undoRedoPerformed -= callback;
            }
            return Task.FromResult<object>(new { requested = true, eventObserved = observed });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
