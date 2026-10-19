using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class PrintMigrationTests
{
    [Fact]
    public void MigrationAndEveryDynamicBatchParseWithoutOpeningSql()
    {
        var script=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"migrations","052_label_print_phase1.sql"));
        Parse(script);
        var batches=Regex.Matches(script,@"EXEC sys\.sp_executesql N'((?:[^']|'')*)';",RegexOptions.Singleline);
        Assert.Equal(4,batches.Count);
        foreach(Match batch in batches)Parse(batch.Groups[1].Value.Replace("''","'"));
    }
    private static void Parse(string script)
    {
        var parser=new TSql160Parser(true);
        parser.Parse(new StringReader(script),out var errors);
        Assert.True(errors.Count==0,string.Join(Environment.NewLine,errors.Select(e=>$"{e.Line}:{e.Column} {e.Message}")));
    }
}
