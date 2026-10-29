using System.Linq.Expressions;
using System.Reflection;

namespace Soroush.Mediator.Abstractions;

/// <summary>Registers explicitly permitted DTO members for one query operation.</summary>
/// <remarks>Used independently by the handler's filterable and orderable configuration methods.
/// Registration does not grant permission to the other operation.</remarks>
public sealed class QueryFieldRegistry<TResult>
{
    private QueryFieldMap<TResult> _fields = new QueryFieldMap<TResult>();

    /// <summary>Registers a public readable DTO member using its CLR name.</summary>
    public QueryFieldRegistry<TResult> Add<TValue>(Expression<Func<TResult, TValue>> selector)
    {
        var member = GetMember(selector);
        return Add(member.Name, selector);
    }

    /// <summary>Registers a public readable DTO member using a case-insensitive external alias.</summary>
    public QueryFieldRegistry<TResult> Add<TValue>(string name, Expression<Func<TResult, TValue>> selector)
    {
        GetMember(selector);
        _fields = _fields.Map(name, selector);
        return this;
    }

    internal QueryFieldMap<TResult> Build() => _fields;

    private static MemberInfo GetMember<TValue>(Expression<Func<TResult, TValue>> selector)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        if (selector.Body is MemberExpression member && member.Expression == selector.Parameters[0])
        {
            if (member.Member is PropertyInfo property && property.GetMethod?.IsPublic == true)
                return property;
            if (member.Member is FieldInfo field && field.IsPublic)
                return field;
        }
        throw new ArgumentException("Select a public readable property or field directly from the output DTO.", nameof(selector));
    }
}
