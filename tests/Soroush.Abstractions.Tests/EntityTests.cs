using System.Reflection;
using Soroush.Domain.Abstractions;

namespace Soroush.Abstractions.Tests;

public class EntityTests
{
    [Fact]
    public void TouchSetsUtcModificationTime()
    {
        var entity = new TestEntity();
        var before = DateTime.UtcNow;

        entity.Touch();

        Assert.NotNull(entity.ModifiedAt);
        Assert.InRange(entity.ModifiedAt.Value, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, entity.ModifiedAt.Value.Kind);
        Assert.Equal(default, entity.CreatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppressionPreservesModificationTimeUntilEnabledAgain(bool previouslyModified)
    {
        DateTime? original = previouslyModified ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;
        var entity = new TestEntity { ModifiedAt = original };

        entity.SuppressModifiedAtUpdate();
        entity.Touch();
        entity.Touch();

        Assert.True(entity.IsModifiedAtUpdateSuppressed);
        Assert.Equal(original, entity.ModifiedAt);
        entity.SuppressModifiedAtUpdate(false);
        entity.Touch();
        Assert.False(entity.IsModifiedAtUpdateSuppressed);
        Assert.NotNull(entity.ModifiedAt);
        Assert.NotEqual(original, entity.ModifiedAt);
    }

    [Fact]
    public void SuppressionFlagUsesAnAnnotationRecognizedByStandardMapping()
    {
        var property = typeof(TestEntity).GetProperty(nameof(Entity.IsModifiedAtUpdateSuppressed))!;

        Assert.NotNull(property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>());
        Assert.Null(typeof(TestEntity).GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>());
    }

    [Fact]
    public void DerivedEntitiesCanOverrideBothOperations()
    {
        Entity entity = new CustomEntity();
        entity.SuppressModifiedAtUpdate();
        entity.Touch();

        var custom = Assert.IsType<CustomEntity>(entity);
        Assert.True(custom.SuppressCalled);
        Assert.True(custom.TouchCalled);
        Assert.Null(custom.ModifiedAt);
    }

    private sealed class TestEntity : Entity { }

    private sealed class CustomEntity : Entity
    {
        public bool SuppressCalled { get; private set; }
        public bool TouchCalled { get; private set; }

        public override void SuppressModifiedAtUpdate(bool suppress = true)
        {
            SuppressCalled = true;
            base.SuppressModifiedAtUpdate(suppress);
        }

        public override void Touch()
        {
            TouchCalled = true;
            base.Touch();
        }
    }
}
