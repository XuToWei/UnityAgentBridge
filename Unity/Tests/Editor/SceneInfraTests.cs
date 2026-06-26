using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Tests
{
    /// <summary>共享对象层:SceneObjectResolver / PropertySerializer / PropertyDeserializer。</summary>
    public sealed class SceneInfraTests : BridgeTestBase
    {
        [Test]
        public void Resolver_FindByPath_Nested()
        {
            var a = NewGo("A");
            var b = NewGo("B", a);
            Assert.AreSame(b, SceneObjectResolver.FindByPath("A/B"));
        }

        [Test]
        public void Resolver_ResolveByInstanceId()
        {
            var a = NewGo("A");
            var go = SceneObjectResolver.ResolveObject(new ObjectRef { InstanceId = a.GetInstanceID() });
            Assert.AreSame(a, go);
        }

        [Test]
        public void Resolver_InvalidRef_Throws()
        {
            var ex = Assert.Throws<CommandException>(() => SceneObjectResolver.ResolveObject(new ObjectRef()));
            Assert.AreEqual(RefErrorCodes.InvalidObjectRef, ex.Code);
        }

        [Test]
        public void Resolver_NotFound_Throws()
        {
            var ex = Assert.Throws<CommandException>(
                () => SceneObjectResolver.ResolveObject(new ObjectRef { Path = "__nope__" }));
            Assert.AreEqual(RefErrorCodes.ObjectNotFound, ex.Code);
        }

        [Test]
        public void Resolver_FindType_Transform()
        {
            Assert.AreEqual(typeof(Transform), SceneObjectResolver.FindType("Transform"));
        }

        [Test]
        public void PropertySerializer_TopLevel_HasPosition()
        {
            var go = NewGo("A");
            var json = PropertySerializer.SerializeTopLevel(go.transform);
            Assert.IsNotNull(json["m_LocalPosition"], "应序列化出 m_LocalPosition");
        }

        [Test]
        public void PropertyDeserializer_WritesNested()
        {
            var go = NewGo("A");
            using (var so = new SerializedObject(go.transform))
            {
                var p = so.FindProperty("m_LocalPosition.x");
                Assert.IsNotNull(p);
                PropertyDeserializer.Apply(p, (JToken)7f);
                so.ApplyModifiedProperties();
            }
            Assert.AreEqual(7f, go.transform.localPosition.x, 0.0001f);
        }

        [Test]
        public void PropertyDeserializer_TypeMismatch_Throws()
        {
            var go = NewGo("A");
            using (var so = new SerializedObject(go.transform))
            {
                var p = so.FindProperty("m_LocalPosition.x");
                var ex = Assert.Throws<CommandException>(() => PropertyDeserializer.Apply(p, (JToken)"nope"));
                Assert.AreEqual(MutationErrorCodes.PropertyTypeMismatch, ex.Code);
            }
        }
    }
}
