using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args is not [var sourceMap, var destination, var repo])
{
    Console.Error.WriteLine("Usage: UtInventory source-map.csv functions.csv solution-root");
    return 2;
}

using var output = new StreamWriter(destination, append: false, new UTF8Encoding(true));
output.WriteLine("source,line,kind,signature,test_file,review_status");
int count = 0;
foreach (var line in File.ReadLines(sourceMap).Skip(1))
{
    // The source-map paths are all known repository file paths; still handle CSV escaped quotes.
    var fields = ParseCsv(line);
    if (fields.Count != 3)
        throw new FormatException("Invalid source-map row: " + line);
    string fullPath = Path.Combine(repo, fields[0]);
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(fullPath), new CSharpParseOptions(LanguageVersion.Preview), fullPath);
    foreach (var node in tree.GetRoot().DescendantNodes())
    {
        if (Describe(node) is not { } entry)
            continue;
        int number = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var signature = QualifiedName(node, entry.Signature);
        output.WriteLine(string.Join(',', new[]{
            fields[0], number.ToString(CultureInfo.InvariantCulture), entry.Kind, signature, fields[1],
            fields[2] == "mapped" ? "needs_function_review" : "missing_test_file"
        }.Select(Csv)));
        count++;
    }
}
if (count == 0)
    throw new InvalidOperationException("No executable source declarations were found.");
Console.WriteLine($"UT inventory: {count} executable declarations; see functions.csv. Test-file existence is not method coverage.");
return 0;

static (string Kind, string Signature)? Describe(SyntaxNode node) => node switch
{
    MethodDeclarationSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("method", n.Identifier.Text + n.TypeParameterList + n.ParameterList),
    ConstructorDeclarationSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("constructor", n.Identifier.Text + n.ParameterList),
    DestructorDeclarationSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("destructor", "~" + n.Identifier.Text + n.ParameterList),
    OperatorDeclarationSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("operator", n.OperatorToken.Text + n.ParameterList),
    ConversionOperatorDeclarationSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("conversion", n.ImplicitOrExplicitKeyword.Text + " " + n.Type + n.ParameterList),
    LocalFunctionStatementSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("local", n.Identifier.Text + n.TypeParameterList + n.ParameterList),
    AccessorDeclarationSyntax n when n.Body is not null || n.ExpressionBody is not null =>
        ("accessor", MemberName(n.Parent?.Parent) + "." + n.Keyword.Text),
    PropertyDeclarationSyntax n when n.ExpressionBody is not null => ("accessor", n.Identifier.Text + ".get"),
    IndexerDeclarationSyntax n when n.ExpressionBody is not null => ("accessor", "this" + n.ParameterList + ".get"),
    _ => null,
};

static string MemberName(SyntaxNode? node) => node switch
{
    PropertyDeclarationSyntax n => n.Identifier.Text,
    EventDeclarationSyntax n => n.Identifier.Text,
    IndexerDeclarationSyntax n => "this" + n.ParameterList,
    _ => "?",
};

static string QualifiedName(SyntaxNode node, string name)
{
    // Include the owning type/method, so identical local names in two methods remain distinguishable.
    var owners = node.Ancestors().Reverse().Select(ancestor => ancestor switch
    {
        BaseNamespaceDeclarationSyntax n => n.Name.ToString(),
        TypeDeclarationSyntax n => n.Identifier.Text + n.TypeParameterList,
        MethodDeclarationSyntax n => n.Identifier.Text + n.TypeParameterList + n.ParameterList,
        LocalFunctionStatementSyntax n => n.Identifier.Text + n.TypeParameterList + n.ParameterList,
        _ => null,
    }).OfType<string>();
    return string.Join('.', owners.Append(name)).ReplaceLineEndings(" ");
}

static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

static List<string> ParseCsv(string line)
{
    var fields = new List<string>();
    var field = new StringBuilder();
    bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        char current = line[i];
        if (current == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                field.Append('"');
                i++;
            }
            else
            {
                quoted = !quoted;
            }
        }
        else if (current == ',' && !quoted)
        {
            fields.Add(field.ToString());
            field.Clear();
        }
        else
        {
            field.Append(current);
        }
    }
    if (quoted)
        throw new FormatException("Unterminated CSV field.");
    fields.Add(field.ToString());
    return fields;
}
