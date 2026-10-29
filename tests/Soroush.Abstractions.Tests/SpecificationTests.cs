using System.Linq.Expressions;
using Soroush.Persistence.Abstractions;

namespace Soroush.Abstractions.Tests;

public class SpecificationTests
{
    [Fact]
    public void ComposedPredicatesUseOneParameterAndContainNoInvocation()
    {
        var positive = new Specification<int>(left => left > 0);
        var small = new Specification<int>(right => right < 10);
        var and = positive.And(small);
        var or = positive.Or(small.Not());

        Assert.Equal(new[] { 1, 9 }, new[] { -1, 0, 1, 9, 10 }.Where(and.Criteria.Compile()));
        Assert.Equal(new[] { 1, 9, 10 }, new[] { -1, 0, 1, 9, 10 }.Where(or.Criteria.Compile()));
        var inspector = new InvocationInspector();
        inspector.Visit(and.Criteria);
        inspector.Visit(or.Criteria);
        Assert.False(inspector.Found);
    }

    [Fact]
    public void CompositionPreservesNestedLambdaParameters()
    {
        var nonempty = new Specification<int[]>(values => values.Length > 0);
        var containsPositive = new Specification<int[]>(items => items.Any(item => item > 0));
        var predicate = nonempty.And(containsPositive).Criteria.Compile();

        Assert.True(predicate(new[] { -1, 2 }));
        Assert.False(predicate(new[] { -1 }));
        Assert.False(predicate(Array.Empty<int>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionDoesNotCaptureTheLeftParameterInsideARightHandLambda(bool useOr)
    {
        var leftParameter = Expression.Parameter(typeof(int), "value");
        var left = new Specification<int>(Expression.Lambda<Func<int, bool>>(
            Expression.GreaterThan(leftParameter, Expression.Constant(useOr ? 10 : 0)), leftParameter));
        var rightParameter = Expression.Parameter(typeof(int), "other");
        // Dynamic query builders can reuse a parameter as a nested lambda's local binding.
        var nested = Expression.Lambda<Func<int, bool>>(
            Expression.GreaterThan(leftParameter, rightParameter), leftParameter);
        var right = new Specification<int>(Expression.Lambda<Func<int, bool>>(
            Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), new[] { typeof(int) },
                Expression.Constant(new[] { 2 }), nested), rightParameter));

        var predicate = (useOr ? left.Or(right) : left.And(right)).Criteria.Compile();

        Assert.True(predicate(1));
        Assert.False(predicate(3));
    }

    [Fact]
    public void CompositionShortCircuits()
    {
        var present = new Specification<string?>(value => value != null);
        var nonempty = new Specification<string?>(value => value!.Length > 0);

        Assert.False(present.And(nonempty).Criteria.Compile()(null));
        Assert.True(present.Not().Or(nonempty).Criteria.Compile()(null));
    }

    [Fact]
    public void OrderingIsCopiedAndPreservedByComposition()
    {
        var primary = SortExpression<string>.Create(value => value.Length, descending: true);
        var secondary = SortExpression<string>.Create(value => value);
        var orderings = new[] { primary, secondary };
        var specification = new Specification<string>(value => value.Length > 0, orderings);
        orderings[0] = secondary;

        Assert.Same(primary, specification.Orderings[0]);
        Assert.Equal(typeof(int), primary.KeySelector.ReturnType);
        Assert.True(primary.Descending);
        Assert.False(secondary.Descending);
        Assert.Equal(specification.Orderings, specification.And(Specification<string>.All).Orderings);
        Assert.Equal(specification.Orderings, specification.Or(Specification<string>.All).Orderings);
        Assert.Equal(specification.Orderings, specification.Not().Orderings);
        Assert.Throws<NotSupportedException>(() => ((IList<SortExpression<string>>)specification.Orderings)[0] = secondary);
    }

    [Fact]
    public void CompositionUsesOnlyTheLeftHandOrdering()
    {
        var ascending = SortExpression<int>.Create(value => value);
        var descending = SortExpression<int>.Create(value => value, descending: true);
        var left = new Specification<int>(value => value > 0, ascending);
        var right = new Specification<int>(value => value < 10, descending);

        Assert.Same(ascending, Assert.Single(left.And(right).Orderings));
        Assert.Same(ascending, Assert.Single(left.Or(right).Orderings));
    }

    [Fact]
    public void AllMatchesAnyValueAndNegationReversesIt()
    {
        Assert.True(Specification<string?>.All.Criteria.Compile()(null));
        Assert.False(Specification<int>.All.Not().Criteria.Compile()(42));
    }

    [Fact]
    public void NullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new Specification<int>(null!));
        Assert.Throws<ArgumentNullException>(() => new Specification<int>(_ => true, null!));
        Assert.Throws<ArgumentException>(() => new Specification<int>(_ => true, new SortExpression<int>[] { null! }));
        Assert.Throws<ArgumentNullException>(() => SortExpression<int>.Create<int>(null!));
        Assert.Throws<ArgumentNullException>(() => Specification<int>.All.And(null!));
        Assert.Throws<ArgumentNullException>(() => Specification<int>.All.Or(null!));
    }

    private sealed class InvocationInspector : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Found = true;
            return base.VisitInvocation(node);
        }
    }
}
