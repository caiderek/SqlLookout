using SqlLookout.Alerts;

namespace SqlLookout.Tests;

public sealed class DatabaseFilterTests
{
    [Theory]
    [InlineData("Sales", "Sales", true)]
    [InlineData("sales", "SALES", true)]                       // case does not matter
    [InlineData("Sales", "Sales2", false)]                     // no wildcard: exact name only
    [InlineData("SqlLookoutAge_*", "SqlLookoutAge_27b19938", true)]
    [InlineData("SqlLookoutAge_*", "SqlLookoutAge_", true)]    // * can be empty
    [InlineData("SqlLookoutAge_*", "XSqlLookoutAge_1", false)]
    [InlineData("*Test*", "McpTest", true)]
    [InlineData("*Test*", "GuardProTest2", true)]
    [InlineData("*Test*", "Prod", false)]
    [InlineData("Db?", "Db1", true)]
    [InlineData("Db?", "Db", false)]                           // ? is exactly one character
    [InlineData("Db?", "Db12", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "anything", true)]
    [InlineData("*b", "ab", true)]                             // the match starts right after the first character
    [InlineData("*Test*", "XTest", true)]
    [InlineData("*ab", "aab", true)]
    public void Wildcards_work(string pattern, string name, bool expected) =>
        Assert.Equal(expected, DatabaseFilter.IsIgnored([pattern], name));

    [Fact]
    public void No_list_ignores_nothing()
    {
        Assert.False(DatabaseFilter.IsIgnored(null, "master"));
        Assert.False(DatabaseFilter.IsIgnored([], "master"));
    }

    [Fact]
    public void Parse_accepts_lines_commas_and_semicolons_and_drops_blanks_and_repeats()
    {
        var list = DatabaseFilter.Parse("  Alpha \r\n,Beta;; alpha \n\n  Gamma*  ");
        Assert.Equal(["Alpha", "Beta", "Gamma*"], list);
        Assert.Empty(DatabaseFilter.Parse(null));
        Assert.Empty(DatabaseFilter.Parse("   "));
    }

    [Fact]
    public void Parse_refuses_control_characters_and_oversized_lists()
    {
        Assert.Throws<InvalidDataException>(() => DatabaseFilter.Parse("bad\t name"));
        Assert.Throws<InvalidDataException>(() => DatabaseFilter.Parse(new string('x', 129)));
        Assert.Throws<InvalidDataException>(() => DatabaseFilter.Parse(string.Join('\n', Enumerable.Range(0, 201).Select(i => "db" + i))));
        Assert.Equal(200, DatabaseFilter.Parse(string.Join('\n', Enumerable.Range(0, 200).Select(i => "db" + i))).Count);
    }
}
