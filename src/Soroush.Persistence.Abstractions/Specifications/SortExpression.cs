using System.Linq.Expressions;

namespace Soroush.Persistence.Abstractions;

/// <summary>A typed sort key, preserved without boxing its return type.</summary>
public sealed class SortExpression<TEntity>
{
    private SortExpression(LambdaExpression keySelector, bool descending)
    {
        KeySelector = keySelector;
        Descending = descending;
    }

    /// <summary>Gets the expression selecting the sort key; its parameter is <typeparamref name="TEntity"/>.</summary>
    public LambdaExpression KeySelector { get; }

    /// <summary>Gets whether the key is sorted in descending order.</summary>
    public bool Descending { get; }

    /// <summary>Creates a sort key while preserving its actual result type for query providers.</summary>
    public static SortExpression<TEntity> Create<TKey>(Expression<Func<TEntity, TKey>> keySelector, bool descending = false)
        => new SortExpression<TEntity>(keySelector ?? throw new ArgumentNullException(nameof(keySelector)), descending);

    internal static SortExpression<TEntity> FromSelector(LambdaExpression keySelector, bool descending)
        => new SortExpression<TEntity>(keySelector, descending);
}
