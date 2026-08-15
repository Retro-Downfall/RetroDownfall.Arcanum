using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

[Collection(CovenantCanonicalCultureCollection.Name)]
public sealed class CovenantCanonicalJsonTests
{
    public static TheoryData<string, string> OfficialCyberphoneVectors =>
        new()
        {
            {
                """
                [
                  56,
                  {
                    "d": true,
                    "10": null,
                    "1": [ ]
                  }
                ]
                """,
                "[56,{\"1\":[],\"10\":null,\"d\":true}]"
            },
            {
                """
                {
                  "peach": "This sorting order",
                  "péché": "is wrong according to French",
                  "pêche": "but canonicalization MUST",
                  "sin":   "ignore locale"
                }
                """,
                "{\"peach\":\"This sorting order\",\"péché\":\"is wrong according to French\",\"pêche\":\"but canonicalization MUST\",\"sin\":\"ignore locale\"}"
            },
            {
                """
                {
                  "1": {"f": {"f": "hi","F": 5} ,"\n": 56.0},
                  "10": { },
                  "": "empty",
                  "a": { },
                  "111": [ {"e": "yes","E": "no" } ],
                  "A": { }
                }
                """,
                "{\"\":\"empty\",\"1\":{\"\\n\":56,\"f\":{\"F\":5,\"f\":\"hi\"}},\"10\":{},\"111\":[{\"E\":\"no\",\"e\":\"yes\"}],\"A\":{},\"a\":{}}"
            },
            {
                """
                {
                  "Unnormalized Unicode":"A\u030a"
                }
                """,
                "{\"Unnormalized Unicode\":\"Å\"}"
            },
            {
                """
                {
                  "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
                  "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
                  "literals": [null, true, false]
                }
                """,
                "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}"
            },
            {
                """
                {
                  "\u20ac": "Euro Sign",
                  "\r": "Carriage Return",
                  "\u000a": "Newline",
                  "1": "One",
                  "\u0080": "Control\u007f",
                  "\ud83d\ude02": "Smiley",
                  "\u00f6": "Latin Small Letter O With Diaeresis",
                  "\ufb33": "Hebrew Letter Dalet With Dagesh",
                  "</script>": "Browser Challenge"
                }
                """,
                "{\"\\n\":\"Newline\",\"\\r\":\"Carriage Return\",\"1\":\"One\",\"</script>\":\"Browser Challenge\",\"\":\"Control\",\"ö\":\"Latin Small Letter O With Diaeresis\",\"€\":\"Euro Sign\",\"😂\":\"Smiley\",\"דּ\":\"Hebrew Letter Dalet With Dagesh\"}"
            }
        };

    // Reproduce the V8 JSON.stringify corpus with scripts/generate-covenant-v8-oracle.mjs.
    public static TheoryData<ulong, string> V8Binary64OracleCorpus =>
        new()
        {
            { 0x6135b24b61e77b28UL, "1.9064553815437672e+160" },
            { 0x766db5bdcbe71dc6UL, "2.923531852809169e+262" },
            { 0xf287ecabf7574336UL, "-5.1049249472653646e+243" },
            { 0xab48d4d2e3409cceUL, "-3.54773897912765e-100" },
            { 0x768b652b7c4c4135UL, "1.0783025313309435e+263" },
            { 0xd13672b7722766fbUL, "-1.7034862584356338e+83" },
            { 0xa754c7ea3507b2bdUL, "-3.219040518708433e-119" },
            { 0x3d1646472f0dc1aaUL, "1.978375124047165e-14" },
            { 0xbe27570b3fe36619UL, "-2.71713540478583e-9" },
            { 0xe77399a840e8cf91UL, "-2.1832240294537994e+190" },
            { 0xdd5c43b217982755UL, "-5.385404223494719e+141" },
            { 0xa68f104759da9ea1UL, "-5.873857546923265e-123" },
            { 0x9ab0612d867acaa4UL, "-3.947352561816503e-180" },
            { 0x75d0d7b506ff32c5UL, "3.23702138995155e+259" },
            { 0x3a4ff4304aba42aeUL, "8.066288769535162e-28" },
            { 0x9d394b1429e23ca6UL, "-6.702047761761329e-168" },
            { 0xbc2cc7dba237380bUL, "-7.801023036534744e-19" },
            { 0xd2c1a807b05479ddUL, "-4.495831930686897e+90" },
            { 0x7bd623e5e5efd0efUL, "3.3713040882883484e+288" },
            { 0x85e3f08985e6ce0cUL, "-2.7461891900886896e-280" },
            { 0x130ff9dc3958cf36UL, "7.246653495463661e-217" },
            { 0x2543312d5dfa4ee6UL, "3.460942090887031e-129" },
            { 0x469e0fc3a5b7ff97UL, "1.5243031511646184e+32" },
            { 0x6df9c62b9d7a07c0UL, "5.8229141348388566e+221" },
            { 0x8456de369b8e79efUL, "-9.38626327268729e-288" },
            { 0x32b8bfc631ca1eb9UL, "2.3500692621724592e-64" },
            { 0x33240ad8f5228520UL, "2.436015496941644e-62" },
            { 0x16b9117922061e22UL, "3.274967359118604e-199" },
            { 0x0078926cf88ee5daUL, "2.186978392511788e-306" },
            { 0xf823d5c6b02d935dUL, "-5.239377651507905e+270" },
            { 0x120c624b8f4b8304UL, "9.815360924301168e-222" },
            { 0xfbf868b8f388ff4cUL, "-1.486712954144551e+289" },
            { 0x08aa1b613a378452UL, "6.3254296615580025e-267" },
            { 0xa804588cfc857fc2UL, "-6.454589260160463e-116" },
            { 0x4620884d23c46313UL, "6.549169088787852e+29" },
            { 0x5d61b0a1875ea28eUL, "6.74116476593861e+141" },
            { 0x1d1960d3dc6c19c4UL, "1.6811397336503183e-168" },
            { 0x0268a37723a11312UL, "4.7092223768292604e-297" },
            { 0x21b92534018962bcUL, "3.1464393563882815e-146" },
            { 0xf2ab6cc92f68af4fUL, "-2.340735078784193e+244" },
            { 0x996b196d9186f6c2UL, "-3.11409040434367e-186" },
            { 0x03f5565306aea03bUL, "1.3684271969596238e-289" },
            { 0x356c6d25fe3b448dUL, "2.374280451871113e-51" },
            { 0xa291ac9ff82b98c2UL, "-3.62346999414019e-142" },
            { 0x270c196e9f8234e1UL, "1.3602161656925517e-120" },
            { 0x3511814f0db93249UL, "4.5690475555772265e-53" },
            { 0x5d1375be081ee560UL, "2.317390864893467e+140" },
            { 0xe455d7b0aab359d4UL, "-2.1609296226636728e+175" },
            { 0xad881472b95af24cUL, "-2.364207194152526e-89" },
            { 0xe2dbfa48282fb68cUL, "-1.6497914201465194e+168" },
            { 0x27fdbbaab5c4d2a7UL, "4.7163007116640926e-116" },
            { 0xe1bd8832cd9f6ff1UL, "-6.643101776647725e+162" },
            { 0x4629961a09f6de7cUL, "1.0135791467281746e+30" },
            { 0x81253fd4a7f1bcd1UL, "-3.873288018477721e-303" },
            { 0x40aed166cbe19c6fUL, "3944.7007742408236" },
            { 0xe65c8163fcd2e263UL, "-1.2112254323814189e+185" },
            { 0x9753a9efa0ac9836UL, "-2.630574247053591e-196" },
            { 0x43f59fab7a49a495UL, "24930440604853817000" },
            { 0x494034214ad53a00UL, "7.227061816661144e+44" },
            { 0x6dd1b402f5babdd7UL, "9.998799931414848e+220" },
            { 0xc967067d5dbe2791UL, "-4.10785978546562e+45" },
            { 0x460cd6f6215741c9UL, "2.8561448696256194e+29" },
            { 0xbe3e8f1fe3a52c1eUL, "-7.115090345305503e-9" },
            { 0x91c997995d998a18UL, "-5.531215381374108e-223" },
            { 0x4765aa736ccef722UL, "8.999631086010251e+35" },
            { 0x7787e23fdd393d41UL, "6.160973656957472e+267" },
            { 0xd82c4f41057f2237UL, "-5.5772723950316386e+116" },
            { 0x4c730313c816dfdbUL, "1.90944619934525e+60" },
            { 0xb4fe7fe342f9b27dUL, "-1.990189248441668e-53" },
            { 0x762f53e68f235b7dUL, "1.9267052442275402e+261" },
            { 0x93eb92c6217f7aceUL, "-1.023817109843482e-212" },
            { 0x7f2c92a04c8498a6UL, "3.9188424484512864e+304" },
            { 0x744db92de19cd037UL, "1.702488692887197e+252" },
            { 0x3357d57ccd140568UL, "2.3174835178586526e-61" },
            { 0x9aab7ae4cc9797f2UL, "-3.3112363651114097e-180" },
            { 0x555fc0bfe195ed7cUL, "1.7779613314007304e+103" },
            { 0x72866ce9b8c79816UL, "4.785061322537633e+243" },
            { 0x802f643d327f4071UL, "-8.73106710684452e-308" },
            { 0x059a73505828043fUL, "1.1384056574195604e-281" },
            { 0x42915ed3b44d0467UL, "4774744101697.101" },
            { 0xd6ea9837302c617eUL, "-4.9966825623613576e+110" },
            { 0x3cc8f0cbc6b5648aUL, "6.92240969494638e-16" },
            { 0xbf7b7280904dd5abUL, "-0.006700994684128696" },
            { 0xa97bbe307c09a318UL, "-7.383016896996165e-109" },
            { 0xabc493544b75e99aUL, "-7.525613685874796e-98" },
            { 0x90acc414d685cf9fUL, "-2.3716571919118932e-228" },
            { 0xaac79941cb384273UL, "-1.317039601641439e-102" },
            { 0x1a99972dfe4f0fabUL, "1.5417791022047245e-180" },
            { 0x713d690c17f7e026UL, "2.9923794071078166e+237" },
            { 0x492fcf628ea85332UL, "3.546944408193253e+44" },
            { 0x7b166d67b28165aeUL, "8.337467201008657e+284" },
            { 0xba00a3510d00ea26UL, "-2.6250065252983563e-29" },
            { 0x76fc68b321fc1dacUL, "1.4313040293069662e+265" },
            { 0x4e98d13eaf9bb97bUL, "4.282078569488888e+70" },
            { 0x4bb8cb5cbc779c99UL, "6.079565215989697e+56" },
            { 0x55465449444026eaUL, "6.251475564461964e+102" },
            { 0x770bd940f6aa9825UL, "2.8061462748869636e+265" },
            { 0x609cc85be685c43eUL, "2.4698385511608514e+157" },
            { 0x4e4db17c4b0e969cUL, "1.6010597204518116e+69" },
            { 0xc66baa4d473040d7UL, "-1.7534930104168098e+31" },
            { 0x5842a2d8117a9965UL, "1.4686001992837237e+117" },
            { 0x95e6818793a2a612UL, "-3.5891430440437846e-203" },
            { 0xcddb1a9fb77d0870UL, "-1.1417537238392343e+67" },
            { 0x40ede53c85fbd8ddUL, "61225.89135544163" },
            { 0x06bcd7aba39edd79UL, "3.2541407220171943e-276" },
            { 0xea237ee2325426a6UL, "-1.9101372091973395e+203" },
            { 0xa7c4fafc5aadfe39UL, "-4.1599272047529114e-117" },
            { 0x6478b642d8e1d5bfUL, "9.77922939433213e+175" },
            { 0xc2aca0d698c92c66UL, "-15738560341142.2" },
            { 0xca8e7b9957e38555UL, "-1.4256215889582079e+51" },
            { 0x61c164289877ec9aUL, "7.824170951264676e+162" },
            { 0x13ffcb59d04663c0UL, "2.3610918008659602e-212" },
            { 0x06627963817c1b3aUL, "6.513569543950087e-278" },
            { 0x940896711127f976UL, "-3.6518279936622987e-212" },
            { 0xb85c778f66bb8fd5UL, "-3.3462835159829375e-37" },
            { 0xf3ea70fe0cc1646bUL, "-2.366412627646276e+250" },
            { 0xe1fcbb61e918fb12UL, "-1.0341009883467957e+164" },
            { 0x2d2e77ad0a97231aUL, "4.6739914797933317e-91" },
            { 0x7211169f4a9ee4b8UL, "2.848637127328916e+241" },
            { 0x9f81cf3a56f34c2bUL, "-6.485800756315865e-157" },
            { 0x56fa7359fd94372eUL, "9.939254625439653e+110" },
            { 0x39b7d4a436b30092UL, "1.1749408067098583e-30" },
            { 0xa24a08bd74068bbeUL, "-1.6679204239022029e-143" },
            { 0xa53b69c4d985ed2aUL, "-2.4717296831437736e-129" },
            { 0x1af841f267e47b77UL, "9.353421493862062e-179" },
            { 0x6bc0b5134aa0c5d6UL, "1.0985350410900978e+211" },
            { 0xfd6168d39873764bUL, "-8.895114071051517e+295" },
            { 0x39a4b8cf44cbe1cbUL, "5.1083458708453855e-31" }
        };

    public static TheoryData<byte[]> MalformedRawUtf8Vectors =>
        new()
        {
            { new byte[] { (byte)'"', 0xc0, 0xaf, (byte)'"' } },
            { new byte[] { (byte)'"', 0x80, (byte)'"' } },
            { new byte[] { (byte)'"', 0xe2, 0x82 } },
            { new byte[] { (byte)'"', 0xed, 0xa0, 0x80, (byte)'"' } },
            { new byte[] { (byte)'"', 0xf4, 0x90, 0x80, 0x80, (byte)'"' } }
        };

    [Theory]
    [MemberData(nameof(OfficialCyberphoneVectors))]
    public void Official_cyberphone_structural_vectors_are_byte_exact(string input, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(input)));
    }

    [Fact]
    public void Rfc_8785_sample_uses_minimal_strings_and_ecmascript_numbers()
    {
        const string input = "{\"numbers\":[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\",\"literals\":[null,true,false]}";
        const string expected = "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";

        byte[] actual = ArcanumCanonicalJsonV1.Canonicalize(Encoding.UTF8.GetBytes(input));

        Assert.Equal(expected, Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void Property_names_sort_by_raw_utf16_units_recursively()
    {
        const string input = "{\"\\u20ac\":\"Euro Sign\",\"\\r\":\"Carriage Return\",\"\\ufb33\":\"Hebrew Letter Dalet With Dagesh\",\"1\":\"One\",\"\\ud83d\\ude00\":\"Emoji: Grinning Face\",\"\\u0080\":\"Control\",\"\\u00f6\":\"Latin Small Letter O With Diaeresis\",\"nested\":[{\"z\":0,\"a\":1}]}";
        const string expected = "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"nested\":[{\"a\":1,\"z\":0}],\"\":\"Control\",\"ö\":\"Latin Small Letter O With Diaeresis\",\"€\":\"Euro Sign\",\"😀\":\"Emoji: Grinning Face\",\"דּ\":\"Hebrew Letter Dalet With Dagesh\"}";

        byte[] actual = ArcanumCanonicalJsonV1.Canonicalize(input);

        Assert.Equal(expected, Encoding.UTF8.GetString(actual));
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("\"é\"", "\"é\"")]
    [InlineData("-0.0", "0")]
    [InlineData("[3,2,1]", "[3,2,1]")]
    [InlineData("{\"b\":2,\"a\":1}", "{\"a\":1,\"b\":2}")]
    public void Every_json_root_kind_is_canonicalized(string input, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(input)));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("-0e10", "0")]
    [InlineData("5e-324", "5e-324")]
    [InlineData("-5e-324", "-5e-324")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
    [InlineData("-1.7976931348623157e308", "-1.7976931348623157e+308")]
    [InlineData("9007199254740992", "9007199254740992")]
    [InlineData("-9007199254740992", "-9007199254740992")]
    [InlineData("295147905179352825856", "295147905179352830000")]
    [InlineData("9.999999999999997e22", "9.999999999999997e+22")]
    [InlineData("1e23", "1e+23")]
    [InlineData("1.0000000000000001e23", "1.0000000000000001e+23")]
    [InlineData("999999999999999700000", "999999999999999700000")]
    [InlineData("999999999999999900000", "999999999999999900000")]
    [InlineData("100000000000000000000", "100000000000000000000")]
    [InlineData("1000000000000000000000", "1e+21")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("9.999999999999997e-7", "9.999999999999997e-7")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("333333333.3333332", "333333333.3333332")]
    [InlineData("333333333.33333325", "333333333.33333325")]
    [InlineData("333333333.3333333", "333333333.3333333")]
    [InlineData("333333333.3333334", "333333333.3333334")]
    [InlineData("333333333.33333343", "333333333.33333343")]
    [InlineData("-0.0000033333333333333333", "-0.0000033333333333333333")]
    [InlineData("1424953923781206.25", "1424953923781206.2")]
    public void Rfc_8785_appendix_b_number_vectors(string input, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(input)));
    }

    [Theory]
    [InlineData("0e-4000")]
    [InlineData("-0.000e9")]
    public void Lexically_zero_numbers_canonicalize_to_positive_zero(string input)
    {
        Assert.Equal("0", Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(input)));
    }

    [Theory]
    [InlineData("1e-4000")]
    [InlineData("-1e-4000")]
    public void Lexically_nonzero_binary64_underflow_is_rejected(string input)
    {
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(input));
    }

    [Theory]
    [MemberData(nameof(V8Binary64OracleCorpus))]
    public void Fixed_seed_v8_binary64_oracle_corpus_is_byte_exact(ulong bits, string expected)
    {
        double value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
        string input = value.ToString("R", CultureInfo.InvariantCulture);

        Assert.Equal(expected, Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(input)));
    }

    [Fact]
    public void Tool_arguments_and_json_schemas_sort_at_every_object_level()
    {
        const string arguments = "{\"z\":null,\"arguments\":{\"b\":[2,1],\"a\":true}}";
        const string schema = "{\"type\":\"object\",\"properties\":{\"z\":{\"type\":\"number\"},\"a\":{\"type\":\"string\"}},\"required\":[\"a\"]}";

        Assert.Equal(
            "{\"arguments\":{\"a\":true,\"b\":[2,1]},\"z\":null}",
            Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(arguments)));
        Assert.Equal(
            "{\"properties\":{\"a\":{\"type\":\"string\"},\"z\":{\"type\":\"number\"}},\"required\":[\"a\"],\"type\":\"object\"}",
            Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(schema)));
    }

    [Fact]
    public void Decoded_duplicate_property_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("{\"a\":1,\"\\u0061\":2}"));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("{\"😀\":1,\"\\ud83d\\ude00\":2}"));

        Assert.Equal(
            "{\"é\":1,\"é\":2}",
            Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize("{\"é\":2,\"e\\u0301\":1}")));
    }

    [Fact]
    public void Invalid_utf8_utf16_surrogates_and_noncharacters_are_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize(new byte[] { (byte)'\"', 0xc3, 0x28, (byte)'\"' }));
        Assert.Throws<EncoderFallbackException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("\"\ud800\""));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("\"\\ud800\""));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("\"\\udfff\""));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("\"\\ufffe\""));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("\"\\udbff\\udfff\""));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("\"\ufdd0\""));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("{\"\\uffff\":0}"));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize("{\"\\ud800\":0}"));
        Assert.Throws<ArgumentException>(
            () => ArcanumCanonicalJsonV1.Canonicalize(new byte[] { 0xef, 0xbb, 0xbf, (byte)'0' }));
    }

    [Theory]
    [MemberData(nameof(MalformedRawUtf8Vectors))]
    public void Malformed_raw_utf8_forms_are_rejected(byte[] input)
    {
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(input));
    }

    [Theory]
    [InlineData("{\"valid\":[0,1,2],\"late\":1,\"\\u006cate\":2}")]
    [InlineData("[0,1,2,{\"valid\":true},\"\\ud800\"]")]
    [InlineData("[0,1,2,{\"valid\":true},1e400]")]
    public void Invalid_values_after_valid_prefixes_are_refused_atomically(string input)
    {
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(input));
    }

    [Fact]
    public void Excessive_depth_after_a_valid_prefix_is_refused_atomically()
    {
        string input = "{\"valid\":[0,1,2],\"late\":"
            + new string('[', ArcanumCanonicalJsonV1.MaxDepth)
            + "0"
            + new string(']', ArcanumCanonicalJsonV1.MaxDepth)
            + "}";

        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(input));
    }

    [Fact]
    public void Nonfinite_or_non_binary64_numbers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize("1e400"));
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize("-1e400"));
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize("NaN"));
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize("Infinity"));
    }

    [Fact]
    public void Input_byte_and_depth_boundaries_are_exact()
    {
        string exactBytes = "\"" + new string('a', ArcanumCanonicalJsonV1.MaxInputUtf8Bytes - 2) + "\"";
        string tooManyBytes = "\"" + new string('a', ArcanumCanonicalJsonV1.MaxInputUtf8Bytes - 1) + "\"";
        string exactDepth = new string('[', ArcanumCanonicalJsonV1.MaxDepth) + "0" + new string(']', ArcanumCanonicalJsonV1.MaxDepth);
        string tooDeep = "[" + exactDepth + "]";

        Assert.Equal(ArcanumCanonicalJsonV1.MaxInputUtf8Bytes, ArcanumCanonicalJsonV1.Canonicalize(exactBytes).Length);
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(tooManyBytes));
        Assert.Equal(exactDepth, Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize(exactDepth)));
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(tooDeep));
    }

    [Fact]
    public void Oversized_canonical_output_fails_before_any_output_is_returned()
    {
        string input = "[" + string.Join(',', Enumerable.Repeat("1e20", 4_000)) + "]";

        Assert.True(Encoding.UTF8.GetByteCount(input) < ArcanumCanonicalJsonV1.MaxInputUtf8Bytes);
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(input));
    }

    [Fact]
    public void Large_byte_payload_allocates_one_exact_output_buffer_without_a_full_copy()
    {
        byte[] input = Encoding.UTF8.GetBytes("\"" + new string('a', 60_000) + "\"");

        _ = ArcanumCanonicalJsonV1.Canonicalize(input);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] output = ArcanumCanonicalJsonV1.Canonicalize(input);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // One UTF-8 output plus two UTF-16 decodes consumes 5x payload bytes. Fixed metadata gets 16 KiB.
        long maximumExpectedBytes = checked((output.LongLength * 5) + 16_384);

        Assert.Equal(input.Length, output.Length);
        Assert.True(
            allocatedBytes <= maximumExpectedBytes,
            $"Expected at most {maximumExpectedBytes} allocated bytes, but observed {allocatedBytes}.");
    }

    [Fact]
    public void Malformed_trailing_data_releases_the_parsed_document_before_failing()
    {
        byte[] input = Encoding.UTF8.GetBytes("\"" + new string('a', 60_000) + "\"x");

        for (int iteration = 0; iteration < 8; iteration++)
        {
            RejectTrailingData(input);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int iteration = 0; iteration < 64; iteration++)
        {
            RejectTrailingData(input);
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        // Exceptions and parser metadata remain bounded below half the reproduced 4.3 MB leak.
        const long maximumExpectedBytes = 2_000_000;

        Assert.True(
            allocatedBytes <= maximumExpectedBytes,
            $"Expected at most {maximumExpectedBytes} allocated bytes, but observed {allocatedBytes}.");
    }

    [Fact]
    public void Canonical_json_is_identical_under_every_installed_culture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        const string expected = "{\"a\":333333333.3333333,\"é\":1e+30}";

        try
        {
            foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                Assert.Equal(
                    expected,
                    Encoding.UTF8.GetString(ArcanumCanonicalJsonV1.Canonicalize("{\"é\":1E30,\"a\":333333333.33333329}")));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void RejectTrailingData(byte[] input)
    {
        Assert.Throws<ArgumentException>(() => ArcanumCanonicalJsonV1.Canonicalize(input));
    }
}
