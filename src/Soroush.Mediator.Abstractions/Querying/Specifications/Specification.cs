using System.Linq.Expressions;

namespace Soroush.Mediator.Abstractions;

/// <summary>An immutable specification with composable predicates and sort keys.</summary>
public sealed class Specification<TEntity> : ISpecification<TEntity>
{
    /// <summary>Creates a specification with an optional sequence of sort keys.</summary>
    public Specification(Expression<Func<TEntity, bool>> criteria, params SortExpression<TEntity>[] orderings)
    {
        Criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        if (orderings is null) throw new ArgumentNullException(nameof(orderings));
        if (orderings.Any(ordering => ordering is null))
            throw new ArgumentException("Sort keys cannot contain null.", nameof(orderings));
        Orderings = Array.AsReadOnly((SortExpression<TEntity>[])orderings.Clone());
    }

    /// <summary>Gets a specification matching every entity.</summary>
    public static Specification<TEntity> All { get; } = new Specification<TEntity>(_ => true);

    /// <inheritdoc />
    public Expression<Func<TEntity, bool>> Criteria { get; }

    /// <inheritdoc />
    public IReadOnlyList<SortExpression<TEntity>> Orderings { get; }

    /// <summary>Combines predicates using AND, preserving this specification's ordering.</summary>
    public Specification<TEntity> And(ISpecification<TEntity> other) => Combine(other, Expression.AndAlso);

    /// <summary>Combines predicates using OR, preserving this specification's ordering.</summary>
    public Specification<TEntity> Or(ISpecification<TEntity> other) => Combine(other, Expression.OrElse);

    /// <summary>Negates the predicate while preserving ordering.</summary>
    public Specification<TEntity> Not()
        => new Specification<TEntity>(Expression.Lambda<Func<TEntity, bool>>(Expression.Not(Criteria.Body), Criteria.Parameters), Orderings.ToArray());

    private Specification<TEntity> Combine(ISpecification<TEntity> other, Func<Expression, Expression, BinaryExpression> combine)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        var predicate = other.Criteria;
        // A fresh parameter cannot be captured by a nested binding in either predicate.
        var parameter = Expression.Parameter(typeof(TEntity), Criteria.Parameters[0].Name);
        var left = new ParameterReplacer(Criteria.Parameters[0], parameter).Visit(Criteria.Body)!;
        var right = new ParameterReplacer(predicate.Parameters[0], parameter).Visit(predicate.Body)!;
        return new Specification<TEntity>(Expression.Lambda<Func<TEntity, bool>>(combine(left, right), parameter), Orderings.ToArray());
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _source;
        private readonly ParameterExpression _target;

        public ParameterReplacer(ParameterExpression source, ParameterExpression target)
        {
            _source = source;
            _target = target;
        }

        protected override Expression VisitParameter(ParameterExpression node) => node == _source ? _target : node;
    }
}
