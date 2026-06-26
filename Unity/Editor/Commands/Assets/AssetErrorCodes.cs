namespace AgentBridge
{
    /// <summary>资源操作 handler 自有错误码(file-bridge 4.1 允许)。域内私有,仅 Commands/Assets/ 使用。</summary>
    public static class AssetErrorCodes
    {
        public const string InvalidAssetPath = "INVALID_ASSET_PATH";       // 写路径不在 Assets/ 下 / 非法
        public const string AssetSourceNotFound = "ASSET_SOURCE_NOT_FOUND"; // import 的外部源文件不存在
        public const string AssetNotFound = "ASSET_NOT_FOUND";            // 目标资产不存在
        public const string AssetCreateFailed = "ASSET_CREATE_FAILED";    // 创建失败(父目录缺 / API 返回失败)
        public const string AssetMoveFailed = "ASSET_MOVE_FAILED";        // 移动 / 改名失败(AssetDatabase 返回错误串)
        public const string UnknownAssetType = "UNKNOWN_ASSET_TYPE";      // create SO 时类型名无法解析为 ScriptableObject 子类
    }
}
