using System.Threading.Tasks;
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class SetImporterPropertyHandler : ICommandHandler
    {
        public string Command => "set_importer_property";
        public string Description => "修改 Assets/ 文件的 AssetImporter SerializedProperty;默认 SaveAndReimport";
        public string Group => "Assets";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new CommandException("ASSET_IMPORT_BUSY",
                    "Unity 正在编译或刷新 AssetDatabase,请稍后重试");
            }
            var path = AssetSupport.RequireFilePath(@params?["path"]?.Value<string>());
            if (!AssetSupport.Exists(path))
            {
                throw new CommandException(AssetErrorCodes.AssetNotFound, $"资产不存在:'{path}'");
            }
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                throw new CommandException("ASSET_IMPORTER_NOT_FOUND",
                    $"资产没有可编辑 Importer:'{path}'");
            }
            var propertyPath = @params?["propertyPath"]?.Value<string>();
            if (string.IsNullOrEmpty(propertyPath) || propertyPath == "m_Script")
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "propertyPath 不能为空且不能是 m_Script");
            }
            var importerType = importer.GetType().FullName;
            bool changed;
            using (var serialized = new SerializedObject(importer))
            {
                var property = serialized.FindProperty(propertyPath);
                if (property == null)
                {
                    throw new CommandException(MutationErrorCodes.PropertyNotFound,
                        $"'{importerType}' 上无属性 '{propertyPath}'");
                }
                PropertyDeserializer.Apply(property, @params?["value"]);
                changed = serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            var reimport = SceneCommandSupport.ReadBool(@params, "reimport", true);
            var reimported = false;
            try
            {
                if (changed)
                {
                    EditorUtility.SetDirty(importer);
                    if (reimport)
                    {
                        importer.SaveAndReimport();
                        reimported = true;
                    }
                    else
                    {
                        AssetDatabase.WriteImportSettingsIfDirty(path);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new CommandException("ASSET_IMPORT_FAILED",
                    $"Importer 属性已修改但保存/重导入失败:'{path}':{ex.Message}");
            }
            return Task.FromResult<object>(new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                importerType,
                propertyPath,
                changed,
                reimported
            });
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"" },
    ""propertyPath"": { ""type"": ""string"", ""minLength"": 1 },
    ""value"": {},
    ""reimport"": { ""type"": ""boolean"", ""default"": true }
  },
  ""required"": [""path"", ""propertyPath"", ""value""]
}");
    }
}
