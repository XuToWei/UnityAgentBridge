using System;
using NUnit.Framework;
using UnityEngine;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class TypeFinderTests
    {
        [Test]
        public void Find_ResolvesBaseTypeFullNameAndShortName()
        {
            Assert.That(TypeFinder.Find(typeof(Component).FullName, typeof(Component)),
                Is.EqualTo(typeof(Component)));
            Assert.That(TypeFinder.Find(nameof(Component), typeof(Component)),
                Is.EqualTo(typeof(Component)));
        }

        [Test]
        public void Find_ResolvesDerivedTypeFromTypeCache()
        {
            Assert.That(TypeFinder.Find(typeof(Transform).FullName, typeof(Component)),
                Is.EqualTo(typeof(Transform)));
            Assert.That(TypeFinder.Find(nameof(Transform), typeof(Component)),
                Is.EqualTo(typeof(Transform)));
        }

        [Test]
        public void Find_ResolvesAssemblyQualifiedNameAndChecksBaseType()
        {
            Assert.That(TypeFinder.Find(typeof(Transform).AssemblyQualifiedName, typeof(Component)),
                Is.EqualTo(typeof(Transform)));
            Assert.That(TypeFinder.Find(typeof(string).AssemblyQualifiedName, typeof(Component)),
                Is.Null);
        }

        [Test]
        public void Find_RejectsAmbiguousShortNameButAcceptsFullName()
        {
            var result = TypeFinder.Find(nameof(TypeFinderFixtureA.DuplicateType),
                typeof(ScriptableObject), out var ambiguous);

            Assert.That(result, Is.Null);
            Assert.That(ambiguous, Is.True);

            result = TypeFinder.Find(typeof(TypeFinderFixtureA.DuplicateType).FullName,
                typeof(ScriptableObject), out ambiguous);
            Assert.That(result, Is.EqualTo(typeof(TypeFinderFixtureA.DuplicateType)));
            Assert.That(ambiguous, Is.False);
        }

        [Test]
        public void Find_HandlesMissingNamesAndNullBaseType()
        {
            Assert.That(TypeFinder.Find("AgentBridge.Does.Not.Exist", typeof(Component),
                out var ambiguous), Is.Null);
            Assert.That(ambiguous, Is.False);
            Assert.That(TypeFinder.Find(null, typeof(Component)), Is.Null);
            Assert.Throws<ArgumentNullException>(() => TypeFinder.Find("Transform", null));
        }
    }
}

namespace AgentBridge.Tests.ProductEditMode.TypeFinderFixtureA
{
    internal sealed class DuplicateType : ScriptableObject
    {
    }
}

namespace AgentBridge.Tests.ProductEditMode.TypeFinderFixtureB
{
    internal sealed class DuplicateType : ScriptableObject
    {
    }
}
