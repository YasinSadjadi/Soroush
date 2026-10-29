namespace Soroush.Mediator.Abstractions;

/// <summary>A filter addressed by the selected output field's name.</summary>
public sealed class QueryFilter
{
    /// <summary>Creates a filter suitable for model binding.</summary>
    public QueryFilter() { }

    /// <summary>Creates a scalar or null filter. Values use invariant-culture text.</summary>
    public QueryFilter(string field, FilterOperator @operator, string? value = null)
    {
        Field = field;
        Operator = @operator;
        Value = value;
    }

    /// <summary>Gets or sets the case-insensitive output field name.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the comparison operator.</summary>
    public FilterOperator Operator { get; set; }

    /// <summary>Gets or sets a scalar value. Null checks and set operators require null here.</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets values for In and NotIn. Other operators require an empty collection.</summary>
    public IReadOnlyList<string?> Values { get; set; } = Array.Empty<string?>();

    internal object? GetValue()
    {
        if (Values is null) throw new ArgumentException("Filter values cannot be null.", nameof(Values));
        if (Operator == FilterOperator.In || Operator == FilterOperator.NotIn)
        {
            if (Value is not null) throw new ArgumentException("Use Values for set operators.", nameof(Value));
            return Values;
        }
        if (Values.Count > 0) throw new ArgumentException("Use Value for scalar operators.", nameof(Values));
        return Value;
    }
}
