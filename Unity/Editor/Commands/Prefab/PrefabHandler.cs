using System.Threading.Tasks;
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class PrefabHandler : ICommandHandler
    {
        public string Command => "prefab";
        public string Description => "Prefab 工作流:action=status/create/apply/revert/unpack;instantiate 请用 create_object(kind=prefab)";
        public string Group => "Prefab";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var action = @params?["action"]?.Value<string>();
            if (string.IsNullOrEmpty(action))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "缺 action");
            }
            if (action == "status")
            {
                var statusObject = ResolveObject(@params);
                return Task.FromResult<object>(new { action, status = Describe(statusObject) });
            }

            SceneCommandSupport.RequireEditMode($"{Command}.{action}");
            switch (action)
            {
                case "create": return Task.FromResult<object>(Create(@params));
                case "apply": return Task.FromResult<object>(Apply(@params));
                case "revert": return Task.FromResult<object>(Revert(@params));
                case "unpack": return Task.FromResult<object>(Unpack(@params));
                default:
                    throw new CommandException(ErrorCodes.InvalidParams,
                        "action 只能是 status / create / apply / revert / unpack");
            }
        }

        private static object Create(JObject @params)
        {
            var go = ResolveObject(@params);
            var path = RequirePrefabPath(@params?["assetPath"]?.Value<string>());
            var overwrite = SceneCommandSupport.ReadBool(@params, "overwrite", false);
            var connect = SceneCommandSupport.ReadBool(@params, "connect", false);
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && !(existing is GameObject))
            {
                throw new CommandException(AssetErrorCodes.AssetAlreadyExists,
                    $"目标已被非 Prefab 资产占用:'{path}'");
            }
            if (existing != null && !overwrite)
            {
                throw new CommandException(AssetErrorCodes.AssetAlreadyExists,
                    $"Prefab 已存在:'{path}';如需覆盖请传 overwrite=true");
            }

            var existingRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (existingRoot != null && existingRoot != go)
            {
                throw new CommandException("PREFAB_SOURCE_NOT_ROOT",
                    "Prefab instance 子节点不能直接作为 create 源;请传 outermost instance root");
            }

            bool success;
            GameObject asset;
            try
            {
                asset = connect
                    ? PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.AutomatedAction, out success)
                    : PrefabUtility.SaveAsPrefabAsset(go, path, out success);
            }
            catch (Exception ex)
            {
                throw new CommandException("PREFAB_CREATE_FAILED", ex.Message);
            }
            if (!success || asset == null)
            {
                throw new CommandException("PREFAB_CREATE_FAILED",
                    $"创建 Prefab 失败:'{path}'");
            }
            // Save only the prefab produced by this command; SaveAssets would also flush
            // unrelated dirty user assets in the editor.
            AssetDatabase.SaveAssetIfDirty(asset);
            if (connect)
            {
                ObjectMutationSupport.MarkSceneDirty(go, true);
            }
            return new
            {
                action = "create",
                created = existing == null,
                overwritten = existing != null,
                connected = connect,
                undoable = false,
                asset = DescribeAsset(asset),
                status = Describe(go)
            };
        }

        private static object Apply(JObject @params)
        {
            var go = ResolveObject(@params);
            var root = RequireInstanceRoot(go);
            EnsureApplicable(root, "apply");
            try
            {
                PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
            }
            catch (Exception ex)
            {
                throw new CommandException("PREFAB_APPLY_FAILED", ex.Message);
            }
            return new
            {
                action = "apply",
                applied = true,
                undoable = false,
                root = SceneObjectResolver.Describe(root),
                status = Describe(root)
            };
        }

        private static object Revert(JObject @params)
        {
            var go = ResolveObject(@params);
            var root = RequireInstanceRoot(go);
            EnsureApplicable(root, "revert");
            try
            {
                PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);
            }
            catch (Exception ex)
            {
                throw new CommandException("PREFAB_REVERT_FAILED", ex.Message);
            }
            ObjectMutationSupport.MarkSceneDirty(root, true);
            return new
            {
                action = "revert",
                reverted = true,
                undoable = false,
                root = SceneObjectResolver.Describe(root),
                status = Describe(root)
            };
        }

        private static object Unpack(JObject @params)
        {
            var go = ResolveObject(@params);
            var root = RequireInstanceRoot(go);
            var modeName = @params?["unpackMode"]?.Value<string>() ?? "outermost";
            var mode = modeName == "complete"
                ? PrefabUnpackMode.Completely
                : PrefabUnpackMode.OutermostRoot;
            if (modeName != "outermost" && modeName != "complete")
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "unpackMode 只能是 outermost / complete");
            }
            try
            {
                PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction);
            }
            catch (Exception ex)
            {
                throw new CommandException("PREFAB_UNPACK_FAILED", ex.Message);
            }
            ObjectMutationSupport.MarkSceneDirty(root, true);
            return new
            {
                action = "unpack",
                unpacked = true,
                unpackMode = modeName,
                undoable = false,
                @object = SceneObjectResolver.Describe(root),
                status = Describe(root)
            };
        }

        private static GameObject ResolveObject(JObject @params)
        {
            return SceneObjectResolver.ResolveObject(@params?["object"]?.ToObject<ObjectRef>());
        }

        private static GameObject RequireInstanceRoot(GameObject go)
        {
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null || !PrefabUtility.IsPartOfPrefabInstance(root))
            {
                throw new CommandException("NOT_PREFAB_INSTANCE",
                    $"'{go.name}' 不是 Prefab instance 的一部分");
            }
            return root;
        }

        private static void EnsureApplicable(GameObject root, string action)
        {
            var assetType = PrefabUtility.GetPrefabAssetType(root);
            var status = PrefabUtility.GetPrefabInstanceStatus(root);
            if (assetType == PrefabAssetType.Model || assetType == PrefabAssetType.MissingAsset ||
                status == PrefabInstanceStatus.MissingAsset)
            {
                throw new CommandException("PREFAB_ASSET_NOT_APPLICABLE",
                    $"{action} 不支持 assetType={assetType}, instanceStatus={status}");
            }
        }

        private static string RequirePrefabPath(string path)
        {
            var normalized = AssetSupport.RequireProjectPath(path, "assetPath");
            if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    "assetPath 必须是 Assets/ 下的 .prefab 文件");
            }
            var folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"Prefab 目标目录不存在:'{folder}'");
            }
            return normalized;
        }

        private static object Describe(GameObject go)
        {
            var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var isInstance = PrefabUtility.IsPartOfPrefabInstance(go);
            return new
            {
                isPartOfAnyPrefab = PrefabUtility.IsPartOfAnyPrefab(go),
                isInstance,
                instanceStatus = PrefabUtility.GetPrefabInstanceStatus(go).ToString(),
                assetType = PrefabUtility.GetPrefabAssetType(go).ToString(),
                assetPath = isInstance ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) : null,
                nearestRoot = nearest == null ? null : SceneObjectResolver.Describe(nearest),
                outermostRoot = outermost == null ? null : SceneObjectResolver.Describe(outermost),
                isOutermostRoot = outermost != null && outermost == go,
                hasOverrides = outermost != null && PrefabUtility.HasPrefabInstanceAnyOverrides(outermost, false)
            };
        }

        private static object DescribeAsset(GameObject asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return new
            {
                name = asset.name,
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                type = asset.GetType().FullName
            };
        }

        public JObject ParamsSchema { get; } = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["action"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("status", "create", "apply", "revert", "unpack")
                },
                ["object"] = SceneObjectResolver.CreateObjectRefSchema(),
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["overwrite"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["connect"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["unpackMode"] = new JObject
                {
                    ["type"] = "string", ["enum"] = new JArray("outermost", "complete"),
                    ["default"] = "outermost"
                }
            },
            ["required"] = new JArray("action", "object")
        };
    }
}
