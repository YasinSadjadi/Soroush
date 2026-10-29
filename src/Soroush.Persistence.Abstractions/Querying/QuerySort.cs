namespace Soroush.Persistence.Abstractions;

/// <summary>A sort key addressed by the selected output field's name.</summary>
public sealed class QuerySort
{
    /// <summary>Creates a sort key suitable for model binding.</summary>
    public QuerySort() { }

    /// <summary>Creates a named ascending or descending sort key.</summary>
    public QuerySort(string field, bool descending = false)
    {
        Field = field;
        Descending = descending;
    }

    /// <summary>Gets or sets the case-insensitive output field name.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this key is descending.</summary>
    public bool Descending { get; set; }
}
