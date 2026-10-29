using System.Globalization;
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;

namespace Soroush.Abstractions.Tests;

public class QueryFieldMapTests
{
    private static readonly QueryFieldMap<Row> Fields = new QueryFieldMap<Row>()
        .Map("price", row => row.Price)
        .Map("name", row => row.Name)
        .Map("quantity", row => row.Quantity)
        .Map("state", row => row.State)
        .Map("id", row => row.Id)
        .Map("created", row => row.Created)
        .Map("timestamp", row => row.Timestamp)
        .Map("duration", row => row.Duration)
        .Map("active", row => row.Active);

    [Theory]
    [InlineData(FilterOperator.Equal, "100", true)]
    [InlineData(FilterOperator.Equal, "101", false)]
    [InlineData(FilterOperator.NotEqual, "101", true)]
    [InlineData(FilterOperator.NotEqual, "100", false)]
    [InlineData(FilterOperator.GreaterThan, "99", true)]
    [InlineData(FilterOperator.GreaterThan, "100", false)]
    [InlineData(FilterOperator.GreaterThanOrEqual, "100", true)]
    [InlineData(FilterOperator.GreaterThanOrEqual, "101", false)]
    [InlineData(FilterOperator.LessThan, "101", true)]
    [InlineData(FilterOperator.LessThan, "100", false)]
    [InlineData(FilterOperator.LessThanOrEqual, "100", true)]
    [InlineData(FilterOperator.LessThanOrEqual, "99", false)]
    public void ScalarComparisonsAcceptInvariantValues(FilterOperator @operator, string value, bool expected)
        => Assert.Equal(expected, Fields.FilterBy("PRICE", @operator, value).Compile()(new Row { Price = 100m }));

    [Theory]
    [InlineData(FilterOperator.Contains, "pha", true)]
    [InlineData(FilterOperator.Contains, "xyz", false)]
    [InlineData(FilterOperator.StartsWith, "Al", true)]
    [InlineData(FilterOperator.StartsWith, "pha", false)]
    [InlineData(FilterOperator.EndsWith, "ha", true)]
    [InlineData(FilterOperator.EndsWith, "Al", false)]
    public void StringOperatorsGuardNullFields(FilterOperator @operator, string value, bool expected)
    {
        var predicate = Fields.FilterBy("name", @operator, value).Compile();
        Assert.Equal(expected, predicate(new Row { Name = "Alpha" }));
        Assert.False(predicate(new Row { Name = null }));
    }

    [Fact]
    public void NullableValuesAndNullChecksWorkWithoutBoxingTheSelector()
    {
        var equal = Fields.FilterBy("quantity", FilterOperator.Equal, "10").Compile();
        Assert.True(equal(new Row { Quantity = 10 }));
        Assert.False(equal(new Row { Quantity = null }));
        var greater = Fields.FilterBy("quantity", FilterOperator.GreaterThan, 5).Compile();
        Assert.True(greater(new Row { Quantity = 10 }));
        Assert.False(greater(new Row { Quantity = null }));
        Assert.True(Fields.FilterBy("quantity", FilterOperator.Equal).Compile()(new Row()));
        Assert.True(Fields.FilterBy("quantity", FilterOperator.IsNull).Compile()(new Row()));
        Assert.False(Fields.FilterBy("quantity", FilterOperator.IsNotNull).Compile()(new Row()));
        Assert.True(Fields.FilterBy("name", FilterOperator.IsNull).Compile()(new Row()));
        Assert.True(Fields.FilterBy("name", FilterOperator.NotEqual).Compile()(new Row { Name = "Alpha" }));
        Assert.Equal(typeof(int?), Fields.OrderBy("quantity").KeySelector.ReturnType);
    }

    [Fact]
    public void SetFiltersCopyInputAndSupportEmptyCollectionsAndNullMembers()
    {
        var values = new object?[] { "1", 2, null };
        var predicate = Fields.FilterBy("quantity", FilterOperator.In, values).Compile();
        values[0] = 99;

        Assert.True(predicate(new Row { Quantity = 1 }));
        Assert.True(predicate(new Row { Quantity = 2 }));
        Assert.True(predicate(new Row { Quantity = null }));
        Assert.False(predicate(new Row { Quantity = 99 }));
        Assert.False(Fields.FilterBy("quantity", FilterOperator.In, Array.Empty<int>()).Compile()(new Row()));
        Assert.True(Fields.FilterBy("quantity", FilterOperator.NotIn, Array.Empty<int>()).Compile()(new Row()));
        Assert.False(Fields.FilterBy("quantity", FilterOperator.NotIn, new[] { 1, 2 }).Compile()(new Row { Quantity = 1 }));
        Assert.True(Fields.FilterBy("quantity", FilterOperator.NotIn, new[] { 1, 2 }).Compile()(new Row { Quantity = 3 }));
    }

    [Fact]
    public void CommonBackendTypesAcceptTypedValuesAndStrings()
    {
        var id = Guid.NewGuid();
        var created = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var timestamp = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(3.5));
        var row = new Row { Id = id, State = State.Ready, Created = created, Timestamp = timestamp, Duration = TimeSpan.FromMinutes(2), Active = true };

        Assert.True(Fields.FilterBy("id", FilterOperator.Equal, id.ToString()).Compile()(row));
        Assert.True(Fields.FilterBy("id", FilterOperator.Equal, id).Compile()(row));
        Assert.True(Fields.FilterBy("state", FilterOperator.Equal, "ready").Compile()(row));
        Assert.True(Fields.FilterBy("state", FilterOperator.Equal, State.Ready).Compile()(row));
        Assert.True(Fields.FilterBy("created", FilterOperator.Equal, created.ToString("O")).Compile()(row));
        Assert.True(Fields.FilterBy("timestamp", FilterOperator.Equal, timestamp.ToString("O")).Compile()(row));
        Assert.True(Fields.FilterBy("duration", FilterOperator.Equal, "00:02:00").Compile()(row));
        Assert.True(Fields.FilterBy("active", FilterOperator.Equal, "true").Compile()(row));
        Assert.True(Fields.FilterBy("name", FilterOperator.Equal, "Alpha").Compile()(new Row { Name = "Alpha" }));
    }

    [Fact]
    public void ConversionIsIndependentOfTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(Fields.FilterBy("price", FilterOperator.Equal, "12.5").Compile()(new Row { Price = 12.5m }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void MappedFieldsSupportComputedSelectorsAndMultiColumnQueries()
    {
        var fields = Fields.Map("length", row => row.Name == null ? 0 : row.Name.Length);
        var query = new AdvancedQuery<Row>()
            .FilterBy(fields.FilterBy("price", FilterOperator.GreaterThanOrEqual, "10"))
            .FilterByAny(fields.FilterBy("name", FilterOperator.StartsWith, "A"),
                fields.FilterBy("quantity", FilterOperator.IsNull))
            .OrderBy(fields.OrderBy("price", descending: true))
            .ThenBy(fields.OrderBy("length"));
        var rows = new[]
        {
            new Row { Price = 20m, Name = "Alpha", Quantity = 1 },
            new Row { Price = 5m, Name = "A", Quantity = null },
            new Row { Price = 20m, Name = "A", Quantity = 1 },
            new Row { Price = 50m, Name = "B", Quantity = 1 }
        };

        Assert.Equal(new[] { "A", "Alpha" }, rows.AsQueryable().ApplyQuery(query).Select(row => row.Name));
        Assert.Equal(typeof(decimal), query.Orderings[0].KeySelector.ReturnType);
        Assert.Throws<ArgumentException>(() => Fields.OrderBy("length"));
    }

    [Fact]
    public void AllowlistRejectsUnknownAndRestrictedFieldsAndIsImmutable()
    {
        var fields = new QueryFieldMap<Row>().Map("price", row => row.Price, allowSorting: false)
            .Map("name", row => row.Name, allowFiltering: false);
        var extended = fields.Map("id", row => row.Id);

        Assert.Throws<ArgumentException>(() => fields.FilterBy("id", FilterOperator.Equal, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => fields.FilterBy("Name", FilterOperator.Equal, "x"));
        Assert.Throws<ArgumentException>(() => fields.OrderBy("price"));
        Assert.Throws<ArgumentException>(() => fields.FilterBy("Name.Length", FilterOperator.Equal, 1));
        Assert.Throws<ArgumentException>(() => fields.OrderBy("Price desc; drop table Orders"));
        Assert.Throws<ArgumentException>(() => fields.Map("PRICE", row => row.Price));
        Assert.Equal(typeof(Guid), extended.OrderBy("id").KeySelector.ReturnType);
    }

    [Theory]
    [InlineData("price", FilterOperator.Equal, null)]
    [InlineData("price", FilterOperator.Equal, "abc")]
    [InlineData("price", FilterOperator.Equal, 1)]
    [InlineData("price", FilterOperator.GreaterThan, null)]
    [InlineData("price", FilterOperator.IsNull, null)]
    [InlineData("quantity", FilterOperator.IsNotNull, 1)]
    [InlineData("quantity", FilterOperator.Equal, "99999999999999")]
    [InlineData("name", FilterOperator.GreaterThan, "x")]
    [InlineData("name", FilterOperator.Contains, null)]
    [InlineData("name", FilterOperator.StartsWith, 1)]
    [InlineData("price", FilterOperator.Contains, "1")]
    [InlineData("quantity", FilterOperator.In, "123")]
    [InlineData("quantity", FilterOperator.In, null)]
    [InlineData("quantity", FilterOperator.NotIn, 1)]
    [InlineData("id", FilterOperator.Equal, "invalid")]
    [InlineData("state", FilterOperator.Equal, "invalid")]
    [InlineData("created", FilterOperator.Equal, "invalid")]
    [InlineData("timestamp", FilterOperator.Equal, "invalid")]
    [InlineData("duration", FilterOperator.Equal, "invalid")]
    [InlineData("active", FilterOperator.Equal, "invalid")]
    public void InvalidValuesAndIncompatibleOperatorsAreRejectedBeforeExecution(string field, FilterOperator @operator, object? value)
        => Assert.Throws<ArgumentException>(() => Fields.FilterBy(field, @operator, value));

    [Fact]
    public void InvalidNamesSelectorsOperatorsAndSetItemsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Fields.Map(" ", row => row.Price));
        Assert.Throws<ArgumentNullException>(() => Fields.Map<int>("test", null!));
        Assert.Throws<ArgumentException>(() => Fields.FilterBy(null!, FilterOperator.Equal));
        Assert.Throws<ArgumentException>(() => Fields.FilterBy("", FilterOperator.Equal));
        Assert.Throws<ArgumentException>(() => Fields.OrderBy(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fields.FilterBy("price", (FilterOperator)999, 1m));
        Assert.Throws<ArgumentException>(() => Fields.FilterBy("quantity", FilterOperator.In, new[] { "1", "bad" }));
        Assert.Throws<ArgumentException>(() => Fields.FilterBy("price", FilterOperator.In, new object?[] { null }));
    }

    [Fact]
    public void AutomaticMapsIncludePublicInstanceOutputMembersAndExcludeOtherMembers()
    {
        var fields = QueryFieldMap<PublicOutput>.FromPublicMembers();
        var row = new PublicOutput { Id = 1, Amount = 10m, PrivateRead = 5 };

        Assert.True(fields.FilterBy("amount", FilterOperator.Equal, "10").Compile()(row));
        Assert.True(fields.FilterBy("id", FilterOperator.Equal, "1").Compile()(row));
        Assert.Equal(typeof(decimal), fields.OrderBy("Amount").KeySelector.ReturnType);
        foreach (var name in new[] { "Item", "StaticValue", "PrivateRead", "WriteOnly" })
            Assert.Throws<ArgumentException>(() => fields.OrderBy(name));
    }

    [Fact]
    public void AutomaticMapsRejectAmbiguousCaseInsensitiveOutputNames()
        => Assert.Throws<ArgumentException>(() => QueryFieldMap<AmbiguousOutput>.FromPublicMembers());

    private sealed class PublicOutput
    {
        public int Id { get; init; }
        public decimal Amount;
        public static int StaticValue => 10;
        public int this[int index] => index;
        public int PrivateRead { private get; set; }
        public int WriteOnly { set { } }
    }

    private sealed class AmbiguousOutput
    {
        public int Id { get; init; }
        public int ID { get; init; }
    }

    private enum State { Draft, Ready }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public decimal Price { get; init; }
        public string? Name { get; init; }
        public int? Quantity { get; init; }
        public State State { get; init; }
        public DateTime Created { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public TimeSpan Duration { get; init; }
        public bool Active { get; init; }
    }
}
