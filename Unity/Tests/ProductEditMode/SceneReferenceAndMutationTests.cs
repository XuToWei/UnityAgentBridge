using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class SceneReferenceAndMutationTests
    {
        private List<GameObject> m_Objects;

        [SetUp]
        public void SetUp()
        {
            m_Objects = new List<GameObject>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in m_Objects)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        [Test]
        public void DescribedObject_RoundTripsThroughResolver()
        {
            var go = CreateObject("RoundTripRoot/With~Escapes");
            var description = JObject.FromObject(SceneObjectResolver.Describe(go));
            var reference = description.ToObject<ObjectRef>();

            Assert.That(description.Property("scenePath"), Is.Not.Null);
            Assert.That(reference.Path, Does.Contain("~1").And.Contain("~0"));
            Assert.That(SceneObjectResolver.ResolveObject(reference), Is.SameAs(go));
        }

        [Test]
        public void ResolvedInstanceId_WithMismatchedPath_IsRejectedAsStale()
        {
            var expected = CreateObject("Expected");
            var other = CreateObject("Other");
            var reference = new ObjectRef
            {
                InstanceId = expected.GetInstanceID(),
                Path = SceneObjectResolver.GetPath(other.transform),
                ScenePath = expected.scene.path
            };

            var error = Assert.Throws<CommandException>(
                () => SceneObjectResolver.ResolveObject(reference));

            Assert.That(error.Code, Is.EqualTo(RefErrorCodes.ObjectRefStale));
        }

        [Test]
        public void LegacyRawTildePath_StillResolvesWhenCanonicalDecodeHasNoMatch()
        {
            var go = CreateObject("Legacy~0Literal");

            var resolved = SceneObjectResolver.ResolveObject(new ObjectRef
            {
                InstanceId = go.GetInstanceID(),
                Path = "Legacy~0Literal",
                ScenePath = go.scene.path
            });

            Assert.That(resolved, Is.SameAs(go));
        }

        [Test]
        public void CanonicalAndLegacyAlias_IsRejectedAsAmbiguous()
        {
            CreateObject("Alias~");
            CreateObject("Alias~0");

            var error = Assert.Throws<CommandException>(() =>
                SceneObjectResolver.ResolveObject(new ObjectRef { Path = "Alias~0" }));

            Assert.That(error.Code, Is.EqualTo(RefErrorCodes.ObjectRefAmbiguous));
        }

        [Test]
        public void LegacyBaseComponentType_FallsBackToAssignableComponents()
        {
            var go = CreateObject("ColliderOwner");
            var collider = go.AddComponent<BoxCollider>();

            var resolved = SceneObjectResolver.ResolveComponent(new ComponentRef
            {
                Object = new ObjectRef
                {
                    InstanceId = go.GetInstanceID(),
                    Path = SceneObjectResolver.GetPath(go.transform),
                    ScenePath = go.scene.path
                },
                Type = typeof(Collider).FullName,
                Index = 0
            });

            Assert.That(resolved, Is.SameAs(collider));
        }

        [Test]
        public void ExactTypeMarker_DistinguishesCanonicalAndLegacyBaseIndices()
        {
            var go = CreateObject("BaseComponentOwner");
            var derived = go.AddComponent<DerivedTestComponent>();
            var exactBase = go.AddComponent<BaseTestComponent>();
            var objectRef = new ObjectRef { InstanceId = go.GetInstanceID() };

            var legacy = SceneObjectResolver.ResolveComponent(new ComponentRef
            {
                Object = objectRef,
                Type = typeof(BaseTestComponent).FullName,
                Index = 0
            });
            var canonical = SceneObjectResolver.ResolveComponent(new ComponentRef
            {
                Object = objectRef,
                Type = typeof(BaseTestComponent).FullName,
                Index = 0,
                ExactType = true
            });

            Assert.That(legacy, Is.SameAs(derived));
            Assert.That(canonical, Is.SameAs(exactBase));
        }

        [Test]
        public void UncommittedUndoTransaction_RestoresRecordedObject()
        {
            var go = CreateObject("Before");

            using (var mutation = ObjectMutationSupport.BeginUndo("test_rollback", true))
            {
                mutation.Record(go);
                go.name = "After";
            }

            Assert.That(go.name, Is.EqualTo("Before"));
        }

        [Test]
        public void ReferenceSchemas_AreFreshAndComponentSchemaUsesSameObjectContract()
        {
            var first = SceneObjectResolver.CreateObjectRefSchema();
            first["consumerMutation"] = true;
            var second = SceneObjectResolver.CreateObjectRefSchema();
            var component = SceneObjectResolver.CreateComponentRefSchema();

            Assert.That(second["consumerMutation"], Is.Null);
            Assert.That(component["properties"]?["object"], Is.EqualTo(second));
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            m_Objects.Add(go);
            return go;
        }
    }

    public class BaseTestComponent : MonoBehaviour
    {
    }

    public sealed class DerivedTestComponent : BaseTestComponent
    {
    }

    public sealed class SceneUnsavedWorkflowTests
    {
        private Scene m_Scene;
        private string m_ScenePath;

        [SetUp]
        public void SetUp()
        {
            m_Scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Additive);
            var gameObject = new GameObject("UnsavedWorkflowProbe");
            if (gameObject.scene.handle != m_Scene.handle)
            {
                SceneManager.MoveGameObjectToScene(gameObject, m_Scene);
            }
            EditorSceneManager.MarkSceneDirty(m_Scene);
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Scene.IsValid() && m_Scene.isLoaded)
            {
                EditorSceneManager.CloseScene(m_Scene, true);
            }
            if (!string.IsNullOrEmpty(m_ScenePath))
            {
                AssetDatabase.DeleteAsset(m_ScenePath);
            }
        }

        [Test]
        public void SceneSwitch_ErrorPolicyRejectsDirtyScene()
        {
            var error = Assert.Throws<CommandException>(() =>
                SceneCommandSupport.HandleUnsavedScenes(
                    "error",
                    SceneUnsavedOperation.OpenSingle,
                    new[] { m_Scene }));

            Assert.That(error.Code, Is.EqualTo(SceneCommandErrorCodes.UnsavedScenes));
            Assert.That(error.Message, Does.Contain(SceneCommandSupport.Label(m_Scene)));
        }

        [Test]
        public void SceneSwitch_DiscardPolicyReportsDiscardWithoutClosingScene()
        {
            var result = SceneCommandSupport.HandleUnsavedScenes(
                "discard",
                SceneUnsavedOperation.OpenSingle,
                new[] { m_Scene });

            CollectionAssert.AreEqual(result.DirtyScenes, result.DiscardedScenes);
            CollectionAssert.IsEmpty(result.SavedScenes);
            Assert.That(m_Scene.isLoaded, Is.True);
            Assert.That(m_Scene.isDirty, Is.True);
        }

        [Test]
        public void RunTests_SavePolicySavesNamedScene()
        {
            m_ScenePath = "Assets/__AgentBridgeSceneWorkflow_" +
                          System.Guid.NewGuid().ToString("N") + ".unity";
            Assert.That(EditorSceneManager.SaveScene(m_Scene, m_ScenePath), Is.True);
            EditorSceneManager.MarkSceneDirty(m_Scene);

            var result = SceneCommandSupport.HandleUnsavedScenes(
                "save",
                SceneUnsavedOperation.RunTests,
                new[] { m_Scene });

            CollectionAssert.AreEqual(new[] { m_ScenePath }, result.SavedScenes);
            CollectionAssert.IsEmpty(result.DiscardedScenes);
            Assert.That(m_Scene.isDirty, Is.False);
        }

        [Test]
        public void PlayAlreadyOpen_ReportsDirtySceneWithoutApplyingErrorPolicy()
        {
            var result = SceneCommandSupport.HandleUnsavedScenes(
                "error",
                SceneUnsavedOperation.PlaySceneAlreadyOpen,
                new[] { m_Scene });

            CollectionAssert.AreEqual(
                new[] { m_Scene.name },
                result.DirtyScenes);
            CollectionAssert.IsEmpty(result.SavedScenes);
            CollectionAssert.IsEmpty(result.DiscardedScenes);
        }

        [Test]
        public void RunTests_RejectsDiscardPolicy()
        {
            var error = Assert.Throws<CommandException>(() =>
                SceneCommandSupport.HandleUnsavedScenes(
                    "discard",
                    SceneUnsavedOperation.RunTests,
                    new[] { m_Scene }));

            Assert.That(error.Code, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(error.Message, Does.Contain("error / save"));
        }

        [Test]
        public void RunTests_SaveRejectsUnnamedSceneWithoutSuggestingDiscard()
        {
            var error = Assert.Throws<CommandException>(() =>
                SceneCommandSupport.HandleUnsavedScenes(
                    "save",
                    SceneUnsavedOperation.RunTests,
                    new[] { m_Scene }));

            Assert.That(error.Code, Is.EqualTo(SceneCommandErrorCodes.SceneSaveFailed));
            Assert.That(error.Message, Does.Contain("未命名场景"));
            Assert.That(error.Message, Does.Not.Contain("discard"));
        }
    }
}
