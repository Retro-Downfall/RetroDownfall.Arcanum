using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantCompilerTests
{
    private const string ResponseStyleAuthoredHash = "B5C835F676515711F21CA61CF53A9FDEABD16057BA2938B9E96930B0A984BB26";

    private const string ResponseStyleFragmentHash = "E645D901DB511E428E00E1EC2E2F90F218B522FDBB1E3AEECF49BDCC43ED47BC";

    private const string NamesExampleAuthoredHash = "A5B210DB411E4FE76BEC7C365E5E04F9738682ED5FC62DF64A80F5E9B56777AE";

    private const string NamesExampleFragmentHash = "23F6A8DB2CAB8CC51372551105FCED1FCF3B6937A6F0F80B365E23537A90F7CE";

    private const string FormatExampleAuthoredHash = "18C4323CE6C127A15608B0C64CB86D7B8997A56A21D9A239A243913E84A16B8A";

    private const string FormatExampleFragmentHash = "0217680F78F5B6B2C2ECD2022A2D3D80149A12794E2EEF33B2A473E1CDE5DA2A";

    private readonly ICovenantCompiler _compiler = new CovenantCompiler();

    [Theory]
    [InlineData("")]
    [InlineData(".leading")]
    [InlineData("-leading")]
    [InlineData("_leading")]
    [InlineData("UPPER")]
    [InlineData("mixedCase")]
    [InlineData("space key")]
    [InlineData("caf\u00e9")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public void Compile_rejects_keys_outside_the_closed_ascii_grammar(string key)
    {
        Assert.Throws<ArgumentException>(() => _compiler.Compile(key, "content"));
    }

    [Fact]
    public void Compile_accepts_the_key_length_boundaries_and_rejects_129_characters()
    {
        CovenantCompiledContent shortest = _compiler.Compile("0", "content");
        CovenantCompiledContent longest = _compiler.Compile("a" + new string('z', 127), "content");

        Assert.Equal("0", shortest.NormalizedKey);
        Assert.Equal(128, longest.NormalizedKey.Length);
        Assert.Throws<ArgumentException>(() => _compiler.Compile(new string('a', 129), "content"));
    }

    [Fact]
    public void Compile_enforces_the_exact_authored_utf8_byte_limit()
    {
        CovenantCompiledContent ascii = _compiler.Compile("limit.ascii", new string('a', 2_048));
        CovenantCompiledContent multibyte = _compiler.Compile("limit.multibyte", new string('\u00e9', 1_024));

        Assert.Equal(2_048, ascii.AuthoredUtf8ByteCount);
        Assert.Equal(2_048, multibyte.AuthoredUtf8ByteCount);
        Assert.Throws<ArgumentException>(() => _compiler.Compile("limit.ascii", new string('a', 2_049)));
        Assert.Throws<ArgumentException>(() => _compiler.Compile("limit.multibyte", new string('\u00e9', 1_025)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n\u00a0\u1680\u2000\u200a\u2028\u2029\u202f\u205f\u3000 ")]
    public void Compile_rejects_empty_or_policy_whitespace_only_content(string authored)
    {
        Assert.Throws<ArgumentException>(() => _compiler.Compile("empty.example", authored));
    }

    [Fact]
    public void Compile_rejects_malformed_utf16_before_hashing()
    {
        string[] malformed =
        [
            new('\ud800', 1),
            new('\udfff', 1),
            string.Concat("before", new string('\ud800', 1), "after"),
            string.Concat("before", new string('\udfff', 1), "after")
        ];

        foreach (string authored in malformed)
        {
            Assert.ThrowsAny<ArgumentException>(() => _compiler.Compile("unicode.invalid", authored));
        }
    }

    [Fact]
    public void Compile_rejects_every_prohibited_c0_del_and_c1_control()
    {
        for (int scalar = 0; scalar <= 0x1f; scalar++)
        {
            if (scalar is 0x09 or 0x0a or 0x0d)
            {
                continue;
            }

            string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");

            Assert.Throws<ArgumentException>(() => _compiler.Compile("control.example", authored));
        }

        for (int scalar = 0x7f; scalar <= 0x9f; scalar++)
        {
            string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");

            Assert.Throws<ArgumentException>(() => _compiler.Compile("control.example", authored));
        }
    }

    [Fact]
    public void Compile_collapses_only_the_closed_whitespace_table()
    {
        int[] whitespaceScalars =
        [
            0x0009, 0x000a, 0x000d, 0x0020, 0x00a0, 0x1680,
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005,
            0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028,
            0x2029, 0x202f, 0x205f, 0x3000
        ];

        StringBuilder authored = new("first");

        foreach (int scalar in whitespaceScalars)
        {
            authored.Append(char.ConvertFromUtf32(scalar));
        }

        authored.Append("second");

        CovenantCompiledContent compiled = _compiler.Compile("whitespace.example", authored.ToString());

        Assert.Equal("- whitespace.example: \"first second\"\n", compiled.Fragment);
    }

    [Fact]
    public void Compile_preserves_authored_text_independently_of_nfc_rendering()
    {
        const string authored = "Cafe\u0301";

        CovenantCompiledContent compiled = _compiler.Compile("names.example", authored);

        Assert.Equal(authored, compiled.AuthoredContent);
        Assert.Equal(Encoding.UTF8.GetBytes(authored), compiled.AuthoredUtf8);
        Assert.Equal("- names.example: \"Caf\u00e9\"\n", compiled.Fragment);
        Assert.NotEqual(compiled.AuthoredUtf8, compiled.FragmentUtf8);
    }

    [Fact]
    public void Returned_utf8_arrays_are_defensive_copies()
    {
        CovenantCompiledContent compiled = _compiler.Compile("immutable.example", "content");
        byte[] authored = compiled.AuthoredUtf8;
        byte[] fragment = compiled.FragmentUtf8;

        authored[0] ^= 0xff;
        fragment[0] ^= 0xff;

        Assert.Equal((byte)'c', compiled.AuthoredUtf8[0]);
        Assert.Equal((byte)'-', compiled.FragmentUtf8[0]);
    }

    [Fact]
    public void Compile_escapes_quotes_and_backslashes_and_uses_lf_only()
    {
        const string authored = "Use \"A\"\r\nthen \\path";

        CovenantCompiledContent compiled = _compiler.Compile("format.example", authored);

        Assert.Equal("- format.example: \"Use \\\"A\\\" then \\\\path\"\n", compiled.Fragment);
        Assert.DoesNotContain('\r', compiled.Fragment);
        Assert.EndsWith("\n", compiled.Fragment, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(compiled.Fragment), compiled.FragmentUtf8ByteCount);
    }

    [Theory]
    [InlineData("no ticks", 3)]
    [InlineData("one ` tick", 3)]
    [InlineData("two `` ticks", 3)]
    [InlineData("three ``` ticks", 4)]
    [InlineData("seven ``````` ticks", 8)]
    public void Compile_selects_an_adaptive_fence_above_the_longest_backtick_run(string authored, int expected)
    {
        CovenantCompiledContent compiled = _compiler.Compile("fence.example", authored);

        Assert.Equal(expected, compiled.RequiredFenceLength);
    }

    [Fact]
    public void RenderProposedSection_uses_the_longest_required_matching_text_fence_and_final_lf()
    {
        CovenantCompiledContent shortFence = _compiler.Compile("alpha", "one ` tick");
        CovenantCompiledContent longFence = _compiler.Compile("beta", "four ```` ticks");

        string section = _compiler.RenderProposedSection([shortFence, longFence]);

        Assert.Equal("`````text\n- alpha: \"one ` tick\"\n- beta: \"four ```` ticks\"\n`````\n", section);
    }

    [Fact]
    public void RenderProposedSection_rejects_an_empty_section()
    {
        Assert.Throws<ArgumentException>(() => _compiler.RenderProposedSection([]));
    }

    [Fact]
    public void Published_compiler_and_hash_vectors_match_exactly()
    {
        AssertVector(
            "response.style",
            "  concise\r\nand\tclear  ",
            "- response.style: \"concise and clear\"\n",
            3,
            22,
            38,
            ResponseStyleAuthoredHash,
            ResponseStyleFragmentHash);
        AssertVector(
            "names.example",
            "Cafe\u0301",
            "- names.example: \"Caf\u00e9\"\n",
            3,
            6,
            25,
            NamesExampleAuthoredHash,
            NamesExampleFragmentHash);
        AssertVector(
            "format.example",
            "Use \"A\"\nthen \\path and ``` marker",
            "- format.example: \"Use \\\"A\\\" then \\\\path and ``` marker\"\n",
            4,
            33,
            57,
            FormatExampleAuthoredHash,
            FormatExampleFragmentHash);
    }

    [Fact]
    public void Published_vectors_are_identical_across_installed_cultures()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                CovenantCompiledContent compiled = _compiler.Compile("response.style", "  concise\r\nand\tclear  ");

                Assert.Equal(ResponseStyleAuthoredHash, compiled.AuthoredHash.ToString());
                Assert.Equal(ResponseStyleFragmentHash, compiled.FragmentHash.ToString());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Corpus_runner_exposes_only_the_complete_span_entry_point()
    {
        System.Reflection.MethodInfo method = Assert.Single(
            typeof(CovenantCompilerCorpus)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            candidate => candidate.Name == nameof(CovenantCompilerCorpus.Run));
        System.Reflection.ParameterInfo parameter = Assert.Single(method.GetParameters());

        Assert.Equal(typeof(ReadOnlySpan<byte>), parameter.ParameterType);
    }

    [Fact]
    public void Shared_corpus_runner_executes_the_complete_contract_and_binds_format_range_identities()
    {
        byte[] corpus = ReadNormalizationCorpus();

        CovenantCompilerCorpusResult result = CovenantCompilerCorpus.Run(corpus);

        Assert.True(result.Succeeded);
        Assert.Equal(20_034, result.NormalizationCaseCount);
        Assert.Equal(100_170, result.NormalizationAssertionCount);
        Assert.Equal(170, result.FormatScalarCount);
        Assert.Equal(42, result.FormatAdjacentScalarCount);
        Assert.Equal(62, result.ProhibitedControlCount);
        Assert.Equal(4, result.MalformedUtf16CaseCount);
        Assert.Equal(76, result.AcceptedKeyCaseCount);
        Assert.Equal(185, result.RejectedKeyCaseCount);
        Assert.Equal(8, result.Utf8BoundaryCaseCount);
        Assert.Equal(22, result.WhitespaceScalarCount);
        Assert.Equal(7, result.RendererCaseCount);
        Assert.Equal(3, result.VectorCount);
        Assert.Equal("F2144934FAC2DD5B8A7263F679C717FC41ADBB30FC702F2F04975AF0C985825A", result.AggregateHash.ToString());
    }

    [Fact]
    public void Shared_corpus_runner_rejects_truncated_or_corrupted_bytes()
    {
        byte[] corpus = ReadNormalizationCorpus();
        byte[] appended = [.. corpus, 0];
        byte[] badMagic = corpus.ToArray();
        byte[] badSourceHash = corpus.ToArray();
        byte[] badCount = corpus.ToArray();
        byte[] badPayload = corpus.ToArray();

        badMagic[0] ^= 0xff;
        badSourceHash[8] ^= 0xff;
        badCount[40] = 0;
        badCount[41] = 0;
        badCount[42] = 0;
        badCount[43] = 0;
        badPayload[^1] ^= 0x01;

        byte[][] invalidCorpora =
        [
            [],
            corpus[..^1],
            appended,
            badMagic,
            badSourceHash,
            badCount,
            badPayload
        ];

        foreach (byte[] invalidCorpus in invalidCorpora)
        {
            Assert.Throws<ArgumentException>(() => CovenantCompilerCorpus.Run(invalidCorpus));
        }
    }

    private void AssertVector(
        string key,
        string authored,
        string fragment,
        int fence,
        int authoredBytes,
        int fragmentBytes,
        string authoredHash,
        string fragmentHash)
    {
        CovenantCompiledContent compiled = _compiler.Compile(key, authored);

        Assert.Equal(key, compiled.NormalizedKey);
        Assert.Equal(authored, compiled.AuthoredContent);
        Assert.Equal(fragment, compiled.Fragment);
        Assert.Equal(fence, compiled.RequiredFenceLength);
        Assert.Equal(authoredBytes, compiled.AuthoredUtf8ByteCount);
        Assert.Equal(fragmentBytes, compiled.FragmentUtf8ByteCount);
        Assert.Equal(1, compiled.CompilerPolicyVersion);
        Assert.Equal(1, compiled.RendererPolicyVersion);
        Assert.Equal(authoredHash, compiled.AuthoredHash.ToString());
        Assert.Equal(fragmentHash, compiled.FragmentHash.ToString());
    }

    private static byte[] ReadNormalizationCorpus()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Covenant",
            "Unicode17",
            "NormalizationTest.nfc.bin");

        return File.ReadAllBytes(path);
    }
}
