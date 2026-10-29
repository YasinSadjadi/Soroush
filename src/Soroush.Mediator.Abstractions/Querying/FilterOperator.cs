namespace Soroush.Mediator.Abstractions;

/// <summary>Supported operators for explicitly mapped query fields.</summary>
public enum FilterOperator
{
    /// <summary>Equal to a value, including null for nullable fields.</summary>
    Equal,
    /// <summary>Not equal to a value.</summary>
    NotEqual,
    /// <summary>Greater than a value.</summary>
    GreaterThan,
    /// <summary>Greater than or equal to a value.</summary>
    GreaterThanOrEqual,
    /// <summary>Less than a value.</summary>
    LessThan,
    /// <summary>Less than or equal to a value.</summary>
    LessThanOrEqual,
    /// <summary>A non-null string contains a value.</summary>
    Contains,
    /// <summary>A non-null string starts with a value.</summary>
    StartsWith,
    /// <summary>A non-null string ends with a value.</summary>
    EndsWith,
    /// <summary>A field equals at least one value in a supplied collection.</summary>
    In,
    /// <summary>A field equals none of the values in a supplied collection.</summary>
    NotIn,
    /// <summary>A nullable field is null. No value is accepted.</summary>
    IsNull,
    /// <summary>A nullable field is not null. No value is accepted.</summary>
    IsNotNull
}
