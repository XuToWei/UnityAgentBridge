namespace AgentBridge
{
    /// <summary>Profiler capture 查询相关错误码。</summary>
    public static class ProfilerErrorCodes
    {
        public const string FrameNotFound = "PROFILER_FRAME_NOT_FOUND";
        public const string ThreadNotFound = "PROFILER_THREAD_NOT_FOUND";
        public const string ThreadAmbiguous = "PROFILER_THREAD_AMBIGUOUS";
        public const string CaptureChanged = "PROFILER_CAPTURE_CHANGED";
        public const string CaptureNotFound = "PROFILER_CAPTURE_NOT_FOUND";
        public const string CaptureLoadFailed = "PROFILER_CAPTURE_LOAD_FAILED";
        public const string CaptureRestoreFailed = "PROFILER_CAPTURE_RESTORE_FAILED";
        public const string RecordingActive = "PROFILER_RECORDING_ACTIVE";
        public const string QueryTooLarge = "PROFILER_QUERY_TOO_LARGE";
        public const string QueryTimeout = "PROFILER_QUERY_TIMEOUT";
        public const string Unavailable = "PROFILER_UNAVAILABLE";
    }
}
