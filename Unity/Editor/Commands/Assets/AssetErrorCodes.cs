namespace AgentBridge
{
    /// <summary>资源操作相关错误码,仅 Commands/Assets/ 使用。</summary>
    public static class AssetErrorCodes
    {
        public const string InvalidAssetPath = "INVALID_ASSET_PATH";       // 写路径不在 Assets/ 下 / 非法
        public const string AssetSourceNotFound = "ASSET_SOURCE_NOT_FOUND"; // import 的外部源文件不存在
        public const string AssetNotFound = "ASSET_NOT_FOUND";            // 目标资产不存在
        public const string AssetAlreadyExists = "ASSET_ALREADY_EXISTS";  // 目标已存在且未显式允许覆盖
        public const string AssetCreateFailed = "ASSET_CREATE_FAILED";    // 创建失败(父目录缺 / API 返回失败)
        public const string AssetMoveFailed = "ASSET_MOVE_FAILED";        // 移动 / 改名失败(AssetDatabase 返回错误串)
        public const string AssetDeleteFailed = "ASSET_DELETE_FAILED";    // 目标存在但因占用/权限/VCS 等删除失败
        public const string AssetDirectoryDeleteRequiresPermanent =
            "ASSET_DIRECTORY_DELETE_REQUIRES_PERMANENT"; // 文件夹禁止同步送系统回收站
        public const string UnknownAssetType = "UNKNOWN_ASSET_TYPE";      // create SO 时类型名无法解析为 ScriptableObject 子类
        public const string AmbiguousAssetType = "AMBIGUOUS_ASSET_TYPE";  // SO 短类型名命中多个类型
    }
}
