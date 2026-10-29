namespace Soroush.Mediator.Abstractions;

/// <summary>How request filters are combined within the selected query.</summary>
public enum FilterLogic
{
    /// <summary>Every filter must match.</summary>
    And,
    /// <summary>At least one filter must match. Existing source predicates remain in effect.</summary>
    Or
}
