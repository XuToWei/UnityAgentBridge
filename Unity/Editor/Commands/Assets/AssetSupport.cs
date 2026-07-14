using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>cmd-assets 内部支撑:写路径守卫 + ScriptableObject 类型名解析。</summary>
    internal static class AssetSupport
    {
        /// <summary>
        /// 写路径守卫:必须工程相对、落在 Assets/ 下,且每个路径段跨平台安全。
        /// 同时拒绝 Assets 内已有的符号链接/junction,避免规范路径仍落到工程外。
        /// 越界 / 缺失 → INVALID_ASSET_PATH。
        /// </summary>
        public static string RequireProjectPath(string path, string field = "path")
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"缺 {field}");
            }

            var p = path.Replace('\\', '/');
            if (!string.Equals(p, p.Trim(), StringComparison.Ordinal))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"{field} 不能以空白开头或结尾:'{path}'");
            }
            while (p.EndsWith("/", StringComparison.Ordinal) && p.Length > "Assets".Length)
            {
                p = p.Substring(0, p.Length - 1);
            }

            if (Path.IsPathRooted(p) || p.Contains(":") ||
                (p != "Assets" && !p.StartsWith("Assets/", StringComparison.Ordinal)))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"{field} 必须在 Assets/ 下:'{path}'");
            }

            var segments = p.Split('/');
            foreach (var segment in segments)
            {
                if (!IsPortablePathSegment(segment))
                {
                    throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                        $"{field} 含非法路径段 '{segment}':'{path}'");
                }
            }

            if (p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"{field} 不允许直接写 Unity .meta 文件:'{path}'");
            }

            EnsurePhysicalContainment(p, field);
            return p;
        }

        private static bool IsPortablePathSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." ||
                segment.Length > 255 || segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal))
            {
                return false;
            }
            foreach (var c in segment)
            {
                if (c < 32 || c == '<' || c == '>' || c == ':' || c == '"' ||
                    c == '/' || c == '\\' || c == '|' || c == '?' || c == '*')
                {
                    return false;
                }
            }

            var deviceName = Path.GetFileNameWithoutExtension(segment);
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (deviceName.Length == 4 &&
                (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                deviceName[3] >= '1' && deviceName[3] <= '9')
            {
                return false;
            }
            return true;
        }

        public static string RequireFilePath(string path, string field = "path")
        {
            var p = RequireProjectPath(path, field);
            if (p == "Assets" || Directory.Exists(ToAbsolutePath(p)))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"{field} 必须是 Assets/ 下的文件路径:'{path}'");
            }
            return p;
        }

        public static string RequireAssetChildPath(string path, string field = "path")
        {
            var p = RequireProjectPath(path, field);
            if (p == "Assets")
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"{field} 不能是工程 Assets 根目录");
            }
            return p;
        }

        public static string ToAbsolutePath(string projectPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, "无法定位 Unity 工程根目录");
            }
            return Path.GetFullPath(Path.Combine(projectRoot,
                projectPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        public static bool Exists(string projectPath)
        {
            var fullPath = ToAbsolutePath(projectPath);
            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }

        public static void EnsureDestinationAvailable(string projectPath, bool overwrite)
        {
            if (Exists(projectPath) && !overwrite)
            {
                throw new CommandException(AssetErrorCodes.AssetAlreadyExists,
                    $"目标已存在:'{projectPath}'。如需覆盖请传 overwrite=true。元文件和文件夹始终不能覆盖。");
            }
        }

        public static PublishedAsset PublishTextAsset(
            string projectPath,
            string content,
            bool overwrite)
        {
            return PublishAndImport(projectPath, overwrite,
                () => WriteAllTextAtomic(projectPath, content, overwrite));
        }

        public static PublishedAsset PublishExternalAsset(
            string source,
            string projectPath,
            bool overwrite)
        {
            var sourceFullPath = Path.GetFullPath(source);
            if (PathEquals(sourceFullPath, ToAbsolutePath(projectPath)))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "source 与 destination 指向同一文件,无需导入");
            }
            return PublishAndImport(projectPath, overwrite,
                () => CopyFileAtomic(sourceFullPath, projectPath, overwrite));
        }

        public static PublishedAsset PublishBytesAsset(
            string projectPath,
            byte[] content,
            bool overwrite,
            Action<PublishedAsset> validatePublication = null)
        {
            return PublishAndImport(projectPath, overwrite,
                () => WriteAllBytesAtomic(projectPath, content, overwrite),
                validatePublication);
        }

        private static PublishedAsset PublishAndImport(
            string projectPath,
            bool overwrite,
            Action publishPayload,
            Action<PublishedAsset> validatePublication = null)
        {
            EnsureDestinationAvailable(projectPath, overwrite);
            var destination = ToAbsolutePath(projectPath);
            FileSnapshot payload = null;
            FileSnapshot metadata = null;
            try
            {
                payload = FileSnapshot.Capture(destination);
                metadata = FileSnapshot.Capture(destination + ".meta");
                var originalGuid = AssetDatabase.AssetPathToGUID(projectPath);
                publishPayload();
                AssetDatabase.ImportAsset(projectPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                var guid = AssetDatabase.AssetPathToGUID(projectPath);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                        $"资产发布后未获得 GUID:'{projectPath}'");
                }
                if (!string.IsNullOrEmpty(originalGuid) &&
                    !string.Equals(guid, originalGuid, StringComparison.Ordinal))
                {
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                        $"覆盖资产后 GUID 发生变化:'{originalGuid}' → '{guid}'");
                }

                var published = new PublishedAsset(
                    projectPath, guid, AssetDatabase.GetMainAssetTypeAtPath(projectPath));
                validatePublication?.Invoke(published);
                return published;
            }
            catch (Exception operationError)
            {
                try
                {
                    payload?.Restore();
                    metadata?.Restore();
                    AssetDatabase.Refresh(
                        ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    if (payload != null && payload.Existed)
                    {
                        AssetDatabase.ImportAsset(projectPath,
                            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                }
                catch (Exception rollbackError)
                {
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                        $"资产发布失败且回滚失败:'{projectPath}':{operationError.Message}; rollback:{rollbackError.Message}");
                }

                if (operationError is CommandException)
                {
                    throw;
                }
                throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                    $"资产发布失败:'{projectPath}':{operationError.Message}");
            }
            finally
            {
                payload?.Dispose();
                metadata?.Dispose();
            }
        }

        public static void WriteAllTextAtomic(string projectPath, string content, bool overwrite)
        {
            var destination = ToAbsolutePath(projectPath);
            PublishAssetFile(destination, overwrite,
                temp => File.WriteAllText(temp, content ?? ""));
        }

        public static void WriteAllBytesAtomic(string projectPath, byte[] content, bool overwrite)
        {
            var destination = ToAbsolutePath(projectPath);
            PublishAssetFile(destination, overwrite,
                temp => File.WriteAllBytes(temp, content ?? Array.Empty<byte>()));
        }

        public static void CopyFileAtomic(string source, string projectPath, bool overwrite)
        {
            var sourceFullPath = Path.GetFullPath(source);
            var destination = ToAbsolutePath(projectPath);
            if (PathEquals(sourceFullPath, destination))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "source 与 destination 指向同一文件,无需导入");
            }

            PublishAssetFile(destination, overwrite,
                temp => File.Copy(sourceFullPath, temp, false));
        }

        private static void PublishAssetFile(
            string destination,
            bool overwrite,
            Action<string> stageTempFile)
        {
            try
            {
                AtomicFilePublisher.Publish(destination, overwrite, stageTempFile);
            }
            catch (AtomicFileDestinationExistsException)
            {
                throw new CommandException(AssetErrorCodes.AssetAlreadyExists,
                    $"目标已存在:'{destination}'。如需覆盖请传 overwrite=true。");
            }
        }

        private static void EnsurePhysicalContainment(string projectPath, string field)
        {
            var assetsRoot = Path.GetFullPath(Application.dataPath);
            var fullPath = ToAbsolutePath(projectPath);
            var comparison = Application.platform == RuntimePlatform.WindowsEditor ||
                             Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var rootWithSeparator = EnsureTrailingSeparator(assetsRoot);
            if (!string.Equals(fullPath, assetsRoot, comparison) &&
                !fullPath.StartsWith(rootWithSeparator, comparison))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"{field} 解析后越出 Assets:'{projectPath}'");
            }

            var cursor = File.Exists(fullPath) || Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(cursor) &&
                   (string.Equals(cursor, assetsRoot, comparison) || cursor.StartsWith(rootWithSeparator, comparison)))
            {
                if (File.Exists(cursor) || Directory.Exists(cursor))
                {
                    try
                    {
                        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                                $"{field} 穿过符号链接或 junction:'{projectPath}'");
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // The path changed between Exists and GetAttributes; the subsequent
                        // asset operation will return its own deterministic failure.
                    }
                }
                if (string.Equals(cursor, assetsRoot, comparison))
                {
                    break;
                }
                cursor = Path.GetDirectoryName(cursor);
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                   path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static bool PathEquals(string left, string right)
        {
            var comparison = Application.platform == RuntimePlatform.WindowsEditor ||
                             Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }

        /// <summary>通过 TypeCache 索引按完整名/短名解析 ScriptableObject 子类。</summary>
        public static Type ResolveScriptableObjectType(string typeName)
        {
            return TypeFinder.Find(typeName, typeof(ScriptableObject));
        }

        public static Type ResolveScriptableObjectType(string typeName, out bool ambiguous)
        {
            return TypeFinder.Find(typeName, typeof(ScriptableObject), out ambiguous);
        }

        internal readonly struct PublishedAsset
        {
            public PublishedAsset(string path, string guid, Type type)
            {
                Path = path;
                Guid = guid;
                Type = type;
            }

            public string Path { get; }
            public string Guid { get; }
            public Type Type { get; }
        }

        private sealed class FileSnapshot : IDisposable
        {
            private readonly string m_Path;
            private readonly string m_BackupPath;

            private FileSnapshot(string path, bool existed, string backupPath)
            {
                m_Path = path;
                Existed = existed;
                m_BackupPath = backupPath;
            }

            public bool Existed { get; }

            public static FileSnapshot Capture(string path)
            {
                if (!File.Exists(path))
                {
                    return new FileSnapshot(path, false, null);
                }

                var backup = Path.Combine(Path.GetTempPath(),
                    "AgentBridge-" + Guid.NewGuid().ToString("N") + ".rollback");
                File.Copy(path, backup, false);
                return new FileSnapshot(path, true, backup);
            }

            public void Restore()
            {
                if (!Existed)
                {
                    if (File.Exists(m_Path))
                    {
                        File.Delete(m_Path);
                    }
                    return;
                }

                AtomicFilePublisher.Publish(m_Path, true,
                    temp => File.Copy(m_BackupPath, temp, false));
            }

            public void Dispose()
            {
                AtomicFilePublisher.DeleteBestEffort(m_BackupPath);
            }
        }
    }
}
