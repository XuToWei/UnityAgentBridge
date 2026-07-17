using System;
using System.Threading.Tasks;
using UnityEditor;

namespace AgentBridge
{
    public static class TaskExtension
    {
        /// <summary>等待指定毫秒数，并在 EditorApplication.update 中完成。</summary>
        public static Task Delay(int milliseconds)
        {
            if (milliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            }

            var completion = new TaskCompletionSource<bool>();
            var dueTime = EditorApplication.timeSinceStartup + milliseconds / 1000.0;

            void Tick()
            {
                if (EditorApplication.timeSinceStartup < dueTime)
                {
                    return;
                }

                EditorApplication.update -= Tick;
                completion.SetResult(true);
            }

            EditorApplication.update += Tick;
            return completion.Task;
        }
    }
}
