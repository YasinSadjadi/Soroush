using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Soroush.Persistence.Abstractions;

/// <summary>An immutable allowlist translating external field names into typed query expressions.</summary>
/// <remarks>Names are case-insensitive. Values must have the field's CLR type or be invariant-culture
/// strings. String matching and null semantics ultimately depend on the query provider.</remarks>
public sealed class QueryFieldMap<TEntity>
{
    private readonly Dictionary<string, Field> _fields;

    /// <summary>Creates an empty field map.</summary>
    public QueryFieldMap() => _fields = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);

    private QueryFieldMap(Dictionary<string, Field> fields) => _fields = fields;

    /// <summary>Maps public readable instance properties and public instance fields by their CLR names.</summary>
    /// <remarks>Indexers and static members are excluded. Use on a selected DTO to address its output fields.</remarks>
    public static QueryFieldMap<TEntity> FromPublicMembers()
    {
        var fields = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
        var parameter = Expression.Parameter(typeof(TEntity), "row");
        var properties = typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>();
        var members = properties.Concat(typeof(TEntity).GetFields(BindingFlags.Public | BindingFlags.Instance));
        foreach (var member in members)
        {
            if (fields.ContainsKey(member.Name))
                throw new ArgumentException($"Output field '{member.Name}' is ambiguous on '{typeof(TEntity)}'.");
            var selector = Expression.Lambda(Expression.MakeMemberAccess(parameter, member), parameter);
            fields.Add(member.Name, new Field(selector,
                descending => SortExpression<TEntity>.FromSelector(selector, descending), true, true));
        }
        return new QueryFieldMap<TEntity>(fields);
    }

    internal bool HasField(string name) => _fields.ContainsKey(name);

    /// <summary>Maps a unique external name to a selector, optionally restricting filtering or sorting.</summary>
    public QueryFieldMap<TEntity> Map<TValue>(string name, Expression<Func<TEntity, TValue>> selector,
        bool allowFiltering = true, bool allowSorting = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A field name is required.", nameof(name));
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        if (_fields.ContainsKey(name)) throw new ArgumentException($"Field '{name}' is already mapped.", nameof(name));

        var fields = new Dictionary<string, Field>(_fields, StringComparer.OrdinalIgnoreCase)
        {
            [name] = new Field(selector, descending => SortExpression<TEntity>.Create(selector, descending),
                allowFiltering, allowSorting)
        };
        return new QueryFieldMap<TEntity>(fields);
    }

    /// <summary>Creates a predicate for a mapped field. Invalid fields, operators, and values are rejected.</summary>
    /// <remarks>In and NotIn accept a non-string IEnumerable and copy its values immediately.
    /// IsNull and IsNotNull require a nullable field and a null value.</remarks>
    public Expression<Func<TEntity, bool>> FilterBy(string name, FilterOperator @operator, object? value = null)
    {
        var field = GetField(name);
        if (!field.AllowFiltering) throw new ArgumentException($"Filtering by '{name}' is not allowed.", nameof(name));

        var member = field.Selector.Body;
        Expression body;
        switch (@operator)
        {
            case FilterOperator.IsNull:
            case FilterOperator.IsNotNull:
                if (value is not null || !CanBeNull(member.Type))
                    throw new ArgumentException("Null checks require a nullable field and no value.", nameof(value));
                body = @operator == FilterOperator.IsNull
                    ? Expression.Equal(member, Expression.Constant(null, member.Type))
                    : Expression.NotEqual(member, Expression.Constant(null, member.Type));
                break;

            case FilterOperator.Contains:
            case FilterOperator.StartsWith:
            case FilterOperator.EndsWith:
                if (member.Type != typeof(string) || value is not string text)
                    throw new ArgumentException("String operators require a string field and a non-null string value.", nameof(value));
                var method = typeof(string).GetMethod(@operator.ToString(), new[] { typeof(string) })!;
                body = Expression.AndAlso(
                    Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(member, method, Expression.Constant(text)));
                break;

            case FilterOperator.In:
            case FilterOperator.NotIn:
                if (value is string || value is not IEnumerable values)
                    throw new ArgumentException("Set operators require a non-string collection of values.", nameof(value));
                var items = new List<Expression>();
                foreach (var item in values) items.Add(CreateValue(item, member.Type));
                body = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), new[] { member.Type },
                    Expression.NewArrayInit(member.Type, items), member);
                if (@operator == FilterOperator.NotIn) body = Expression.Not(body);
                break;

            case FilterOperator.Equal:
            case FilterOperator.NotEqual:
            case FilterOperator.GreaterThan:
            case FilterOperator.GreaterThanOrEqual:
            case FilterOperator.LessThan:
            case FilterOperator.LessThanOrEqual:
                if (value is null && @operator != FilterOperator.Equal && @operator != FilterOperator.NotEqual)
                    throw new ArgumentException("Relational operators require a non-null value.", nameof(value));
                var operand = CreateValue(value, member.Type);
                try
                {
                    body = Expression.MakeBinary(ToExpressionType(@operator), member, operand);
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArgumentException($"Operator '{@operator}' is not supported for '{member.Type}'.", nameof(@operator), exception);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unknown filter operator.");
        }

        return Expression.Lambda<Func<TEntity, bool>>(body, field.Selector.Parameters);
    }

    /// <summary>Creates an unboxed sort key for a mapped field.</summary>
    public SortExpression<TEntity> OrderBy(string name, bool descending = false)
    {
        var field = GetField(name);
        if (!field.AllowSorting) throw new ArgumentException($"Sorting by '{name}' is not allowed.", nameof(name));
        return field.CreateOrdering(descending);
    }

    private Field GetField(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A field name is required.", nameof(name));
        if (!_fields.TryGetValue(name, out var field))
            throw new ArgumentException($"Field '{name}' is not mapped.", nameof(name));
        return field;
    }

    private static Expression CreateValue(object? value, Type fieldType)
    {
        if (value is null)
        {
            if (!CanBeNull(fieldType)) throw new ArgumentException($"Null is not valid for '{fieldType}'.", nameof(value));
            return Expression.Constant(null, fieldType);
        }

        var actualType = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
        object converted = value;
        if (!actualType.IsInstanceOfType(value))
        {
            if (value is not string text)
                throw new ArgumentException($"Provide a value of type '{actualType}' or an invariant-culture string.", nameof(value));
            try
            {
                if (actualType.IsEnum) converted = Enum.Parse(actualType, text, ignoreCase: true);
                else if (actualType == typeof(Guid)) converted = Guid.Parse(text);
                else if (actualType == typeof(DateTime)) converted = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                else if (actualType == typeof(DateTimeOffset)) converted = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
                else if (actualType == typeof(TimeSpan)) converted = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
                else converted = Convert.ChangeType(text, actualType, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException
                || exception is InvalidCastException || exception is ArgumentException)
            {
                throw new ArgumentException($"Value is not valid for '{fieldType}'.", nameof(value), exception);
            }
        }

        var constant = Expression.Constant(converted, actualType);
        return actualType == fieldType ? (Expression)constant : Expression.Convert(constant, fieldType);
    }

    private static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static ExpressionType ToExpressionType(FilterOperator @operator)
    {
        switch (@operator)
        {
            case FilterOperator.Equal: return ExpressionType.Equal;
            case FilterOperator.NotEqual: return ExpressionType.NotEqual;
            case FilterOperator.GreaterThan: return ExpressionType.GreaterThan;
            case FilterOperator.GreaterThanOrEqual: return ExpressionType.GreaterThanOrEqual;
            case FilterOperator.LessThan: return ExpressionType.LessThan;
            default: return ExpressionType.LessThanOrEqual;
        }
    }

    private sealed class Field
    {
        public Field(LambdaExpression selector, Func<bool, SortExpression<TEntity>> createOrdering,
            bool allowFiltering, bool allowSorting)
        {
            Selector = selector;
            CreateOrdering = createOrdering;
            AllowFiltering = allowFiltering;
            AllowSorting = allowSorting;
        }

        public LambdaExpression Selector { get; }
        public Func<bool, SortExpression<TEntity>> CreateOrdering { get; }
        public bool AllowFiltering { get; }
        public bool AllowSorting { get; }
    }
}
