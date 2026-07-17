using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    /// <summary>refresh(资源写):保存打开场景和资产后触发 AssetDatabase.Refresh()。</summary>
    public sealed class RefreshHandler : ICommandHandler
    {
        public string Command => "refresh";
        public string Description => "保存所有打开场景和资产后执行 AssetDatabase.Refresh()";
        public string Group => "Assets";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException("REFRESH_NOT_ALLOWED_IN_PLAY_MODE",
                    "refresh 会保存场景,PlayMode 或切换期间禁止执行");
            }
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty && string.IsNullOrEmpty(scene.path))
                {
                    throw new CommandException(PlayModeErrorCodes.SceneSaveFailed,
                        $"未命名场景 '{scene.name}' 无法非交互保存;refresh 已中止");
                }
            }
            if (!EditorSceneManager.SaveOpenScenes())
            {
                throw new CommandException(PlayModeErrorCodes.SceneSaveFailed,
                    "未能保存全部打开场景;refresh 已中止,未执行 AssetDatabase.Refresh");
            }
            try
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (System.Exception ex)
            {
                throw new CommandException("ASSET_REFRESH_FAILED", $"AssetDatabase.Refresh 失败:{ex.Message}");
            }
            return Task.FromResult<object>(new { saved = true, refreshed = true });
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{""type"":""object"",""properties"":{}}");
    }
}
