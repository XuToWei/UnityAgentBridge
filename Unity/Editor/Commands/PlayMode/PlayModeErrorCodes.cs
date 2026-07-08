namespace AgentBridge
{
    /// <summary>Play Mode / 场景运行相关错误码,仅 Commands/PlayMode/ 使用。</summary>
    public static class PlayModeErrorCodes
    {
        public const string InvalidScenePath = "INVALID_SCENE_PATH";                 // 场景路径越界 / 非 .unity
        public const string SceneNotFound = "SCENE_NOT_FOUND";                       // 场景资产不存在 / 不是 SceneAsset
        public const string SceneNotInBuildSettings = "SCENE_NOT_IN_BUILD_SETTINGS"; // Build Settings 无对应已启用场景
        public const string UnsavedScenes = "UNSAVED_SCENES";                        // 当前场景有未保存修改
        public const string SceneSaveFailed = "SCENE_SAVE_FAILED";                  // 自动保存当前场景失败
        public const string SceneOpenFailed = "SCENE_OPEN_FAILED";                  // 打开目标场景失败
        public const string PlayModeAlreadyActive = "PLAY_MODE_ALREADY_ACTIVE";      // 已在 Play Mode
        public const string PlayModeTransition = "PLAY_MODE_TRANSITION";             // 正在进入/退出 Play Mode
        public const string EnterPlayModeFailed = "ENTER_PLAY_MODE_FAILED";          // 请求进入 Play Mode 失败
    }
}
