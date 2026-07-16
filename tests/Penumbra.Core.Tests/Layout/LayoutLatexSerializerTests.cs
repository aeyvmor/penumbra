using Penumbra.Cas.Latex;
using Penumbra.Core.Layout;
using static Penumbra.Core.Tests.Layout.LayoutTestFactory;

namespace Penumbra.Core.Tests.Layout;

/// <summary>
/// Serializer fixtures for every mandatory notation shape. Each case is checked two ways:
/// the emitted LaTeX is exact (string discipline), and — crucially — feeding that LaTeX to the REAL
/// <see cref="LatexToAngouriMath"/> yields the expected AngouriMath syntax, proving the serialization never
/// silently changes mathematical value. This is why Core.Tests references Cas (Core itself does not).
/// </summary>
public sealed class LayoutLatexSerializerTests
{
    private static readonly IReadOnlyDictionary<string, Fixture> Fixtures = BuildFixtures();

    private sealed record Fixture(LayoutNode Tree, string Latex, string Angouri);

    private static Dictionary<string, Fixture> BuildFixtures()
    {
        var bar = Tok(@"\bar");
        var rad1 = Tok(@"\sqrt");
        var rad2 = Tok(@"\sqrt");

        LayoutNode Xplus1() => Paren(Seq(Leaf("x"), Leaf("+"), Leaf("1")));
        LayoutNode Xminus1() => Paren(Seq(Leaf("x"), Leaf("-"), Leaf("1")));

        return new Dictionary<string, Fixture>
        {
            // y = x^2
            ["y=x^2"] = new(
                Eq(Leaf("y"), Sup(Leaf("x"), Leaf("2"))),
                "y=x^{2}", "y=x^(2)"),

            // 2x + 5 = 13  (digits 1,3 must glue into the number 13)
            ["2x+5=13"] = new(
                Eq(Seq(Product(Leaf("2"), Leaf("x")), Leaf("+"), Leaf("5")), Seq(Leaf("1"), Leaf("3"))),
                "2x+5=13", "2*x+5=13"),

            // x^2 - 5x + 6 = 0
            ["x^2-5x+6=0"] = new(
                Eq(
                    Seq(Sup(Leaf("x"), Leaf("2")), Leaf("-"), Product(Leaf("5"), Leaf("x")), Leaf("+"), Leaf("6")),
                    Leaf("0")),
                "x^{2}-5x+6=0", "x^(2)-5*x+6=0"),

            // (x+1)(x-1) =   (trailing-relation query: null right)
            ["(x+1)(x-1)="] = new(
                new RelationNode(Product(Xplus1(), Xminus1()), Tok("="), null),
                @"\left(x+1\right)\left(x-1\right)=", "(x+1)*(x-1)="),

            // (x+1)^2 =
            ["(x+1)^2="] = new(
                new RelationNode(Sup(Xplus1(), Leaf("2")), Tok("="), null),
                @"\left(x+1\right)^{2}=", "(x+1)^(2)="),

            // stacked (x+1)/2 as a real fraction
            ["(x+1)/2"] = new(
                new FractionNode(Xplus1(), Leaf("2"), bar),
                @"\frac{\left(x+1\right)}{2}", "(((x+1))/(2))"),

            // sqrt(x^2 + 1) =   (power nested inside a radical)
            ["sqrt(x^2+1)="] = new(
                new RelationNode(
                    new RadicalNode(Seq(Sup(Leaf("x"), Leaf("2")), Leaf("+"), Leaf("1")), null, rad1),
                    Tok("="), null),
                @"\sqrt{x^{2}+1}=", "sqrt(x^(2)+1)="),

            // y = sin(x)
            ["y=sin(x)"] = new(
                Eq(Leaf("y"), new FunctionCallNode("sin", new[] { Tok("s"), Tok("i"), Tok("n") }, Leaf("x"))),
                @"y=\sin(x)", "y=sin(x)"),

            // multi-line dependency page rows (each a separate tree)
            ["a=2"] = new(Eq(Leaf("a"), Leaf("2")), "a=2", "a=2"),
            ["b=1"] = new(Eq(Leaf("b"), Leaf("1")), "b=1", "b=1"),
            ["y=ax+b"] = new(
                Eq(Leaf("y"), Seq(Product(Leaf("a"), Leaf("x")), Leaf("+"), Leaf("b"))),
                "y=ax+b", "y=a*x+b"),

            // digit-against-digit product must NOT collapse to the number 23
            ["2*3"] = new(Product(Leaf("2"), Leaf("3")), @"2\times 3", "2*3"),

            // cube root  sqrt[3]{8}
            ["cbrt8"] = new(
                new RadicalNode(Leaf("8"), Leaf("3"), rad2),
                @"\sqrt[3]{8}", "((8)^(1/(3)))"),

            // e^x  (power, base is the constant e leaf)
            ["e^x"] = new(Sup(Leaf("e"), Leaf("x")), "e^{x}", "e^(x)"),

            // functions from the ink vocabulary
            ["ln(x)"] = new(
                new FunctionCallNode("ln", new[] { Tok("l"), Tok("n") }, Leaf("x")),
                @"\ln(x)", "ln(x)"),
            ["log(x)"] = new(
                new FunctionCallNode("log", new[] { Tok("l"), Tok("o"), Tok("g") }, Leaf("x")),
                @"\log(x)", "log(10,x)"),
            ["cos(2x)"] = new(
                new FunctionCallNode("cos", new[] { Tok("c"), Tok("o"), Tok("s") }, Product(Leaf("2"), Leaf("x"))),
                @"\cos(2x)", "cos(2*x)"),
            ["tan(x)"] = new(
                new FunctionCallNode("tan", new[] { Tok("t"), Tok("a"), Tok("n") }, Leaf("x")),
                @"\tan(x)", "tan(x)"),

            // relations across the supported closure
            // Slice 0 maps \neq to AngouriMath's actual not-equal spelling (<>), never raw != —
            // AngouriMath 1.4.0 parses 2!=3 as factorial-plus-equality (see the ledger's finding 9).
            ["x!=5"] = new(
                new RelationNode(Leaf("x"), Tok(@"\neq"), Leaf("5")),
                @"x\neq 5", "x<>5"),
            ["x<=3"] = new(
                new RelationNode(Leaf("x"), Tok(@"\leq"), Leaf("3")),
                @"x\leq 3", "x<=3"),
            ["x>=3"] = new(
                new RelationNode(Leaf("x"), Tok(@"\geq"), Leaf("3")),
                @"x\geq 3", "x>=3"),
            // \lt has no translator case — must degrade to bare '<', never the value name "lt"
            ["2<3"] = new(
                new RelationNode(Leaf("2"), Tok(@"\lt"), Leaf("3")),
                "2<3", "2<3"),

            // implicit product safety: control word meeting a letter needs a space
            ["pi*x"] = new(Product(Leaf(@"\pi"), Leaf("x")), @"\pi x", "pi*x"),
            // 2(x+1)
            ["2(x+1)"] = new(Product(Leaf("2"), Xplus1()), @"2\left(x+1\right)", "2*(x+1)"),
        };
    }

    public static IEnumerable<object[]> Keys => Fixtures.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Keys))]
    public void SerializesToExactLatex(string key)
    {
        var fixture = Fixtures[key];
        Assert.Equal(fixture.Latex, LayoutLatexSerializer.Serialize(fixture.Tree));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void RoundTripsThroughRealTranslatorWithoutChangingValue(string key)
    {
        var fixture = Fixtures[key];
        var latex = LayoutLatexSerializer.Serialize(fixture.Tree);
        Assert.Equal(fixture.Angouri, LatexToAngouriMath.Translate(latex));
    }

    [Fact]
    public void DigitProduct_NeverCollapsesToAMultiDigitNumber()
    {
        var latex = LayoutLatexSerializer.Serialize(Product(Leaf("2"), Leaf("3")));

        Assert.NotEqual("23", latex);
        Assert.Equal("2*3", LatexToAngouriMath.Translate(latex)); // value 6, not 23
    }

    [Fact]
    public void LessThan_NeverBecomesThePhantomValueName_lt()
    {
        var tree = new RelationNode(Leaf("2"), Tok(@"\lt"), Leaf("3"));
        var latex = LayoutLatexSerializer.Serialize(tree);

        Assert.DoesNotContain(@"\lt", latex);
        Assert.DoesNotContain("lt", LatexToAngouriMath.Translate(latex));
    }

    [Fact]
    public void Serialize_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => LayoutLatexSerializer.Serialize(null!));
}
