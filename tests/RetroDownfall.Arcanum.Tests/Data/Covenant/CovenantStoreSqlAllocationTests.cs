using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class CovenantStoreSqlAllocationTests
{
    [Fact]
    public void Cold_mutation_template_table_has_a_fixed_small_allocation_bound()
    {
        // Warm the containing type separately so this measures one complete fresh table, independent
        // of test order. The table has no process-wide registration or lazy entries.
        _ = CovenantStoreSql.QuotaSnapshot(false, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();

        CovenantStoreSql.MutationReadTemplates templates = new();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(templates);

        Assert.InRange(allocated, 1, 128 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_mutation_read_templates_allocate_no_sql_text(bool campaignScoped)
    {
        for (int count = 0; count <= CovenantLimits.MaxStagedMutationsPerTurn; count++)
        {
            _ = ReadTemplates(campaignScoped, count);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        int totalCharacters = 0;

        for (int iteration = 0; iteration < 64; iteration++)
        {
            for (int count = 0; count <= CovenantLimits.MaxStagedMutationsPerTurn; count++)
            {
                totalCharacters += ReadTemplates(campaignScoped, count);
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(totalCharacters > 0);

        Assert.Equal(0, allocated);
    }

    private static int ReadTemplates(bool campaignScoped, int count) =>
        CovenantStoreSql.QuotaSnapshot(campaignScoped, count).Length
        + CovenantStoreSql.SectionOccupancy(campaignScoped, count).Length
        + CovenantStoreSql.Detail(campaignScoped).Length
        + CovenantStoreSql.DetailCuration(campaignScoped).Length
        + CovenantStoreSql.DependentHeadScan(campaignScoped).Length
        + CovenantStoreSql.LocalHeadFactsSql(campaignScoped).Length;

    [Theory]
    [InlineData("quota", false, 0, "BA78A197513276157DD248C3B424F833BEC5EDDC880CB93CA3D5023E0D8951CC")]
    [InlineData("section", false, 0, "1A4835064680A31A897AAD729FCA7D382B9853295D8DAC3D8832B1ED4E1B984A")]
    [InlineData("quota", false, 1, "534EE3B6B93C3F4410E7212A88A8470EC021D77FE3FD1522E517C0FBA0116815")]
    [InlineData("section", false, 1, "06A9736A7323A69ED70A8CA9E360BE797BED92235B42496E8610A9D560BE7624")]
    [InlineData("quota", false, 2, "1650329B65B141CFB77CD50A4559E5050F8933BF32B8E5328850E9223561B04F")]
    [InlineData("section", false, 2, "15A4D8ECFC808BABDFAFC52A3E76058DD5B6900B20ED5532C6A95C3D0DC42A78")]
    [InlineData("quota", false, 3, "065CCA561B2C4CF4BB51B1775C92E65C62B4F59449831F354355FEDB09618D69")]
    [InlineData("section", false, 3, "65AF2DD960DCCF0B8C8E19BA9BFAA6AFB166B6A254220ECEF004F48CC69034A3")]
    [InlineData("quota", false, 4, "156DDE3732A1077F64E73A814F974E15E1DDDB103B163D5C2FDC2D62653749D6")]
    [InlineData("section", false, 4, "6D518AACD3F95B74FF6B07904340488C471D877A92F20FC86DEADD4966809661")]
    [InlineData("quota", false, 5, "8E4BA15075C626A9128A56995A972E0422EE1F20DCECC7EF5D7BC6453FBDA44C")]
    [InlineData("section", false, 5, "99C097EE82958E8322288DC5C9E9A88D86FD39BA94C3FFD5D4D3C53884648635")]
    [InlineData("detail", false, 0, "6F949806F0A5C560A8BA280E5CDC180B3A510CDC8DDECF67F7D0072C3B262A76")]
    [InlineData("curation", false, 0, "289BE53096F83C0CF20844143DD19C16D9D9C947032E908EDB5CCFA082D4BF58")]
    [InlineData("dependent", false, 0, "C1D0906C15A85C8BD23A4E1C245D516651F0842AFD42B960B0FC4778529EF030")]
    [InlineData("local", false, 0, "F25F7762D31C5466BF741F5EC97430191AECB8777E35FD91BDC20DE96B7122DE")]
    [InlineData("quota", true, 0, "9A78F70D1CA8D7C207A292C962D48E48294E1E84766E5C0B4BBBA20899DF18E6")]
    [InlineData("section", true, 0, "0935B3D17C27C7DF6ABF1448D8D24C03AA2AEADF2A6546B279302F65F151B26A")]
    [InlineData("quota", true, 1, "9AE58D2506B82D7D8BD31E190B28157E10625A651F8F17F25FCB8D1F3AD38317")]
    [InlineData("section", true, 1, "B3AE65E712E28B9AFC2BD711A61EFE95FE8ADEAB17380D653CE496E7BE2F394A")]
    [InlineData("quota", true, 2, "DF413D587430A9CD24F022B630DBB8EABF057AB8FB4B542DDE20C4C0BAFF12C6")]
    [InlineData("section", true, 2, "80DC8BDD3DA71513B192D82C77F88EDC7F79D1B5AB11FF4A0E0066A8C7299B08")]
    [InlineData("quota", true, 3, "FDC87F433B7DA1B67391622844D7319603D85900179CA89E730A2BD5FD38ABA0")]
    [InlineData("section", true, 3, "B9BDCAC65BC89AE122786CA8383D1A87E415CF7B598E361AB6E2BB55D4CE0883")]
    [InlineData("quota", true, 4, "CE4E9B190F5DCF806F19C2C9ADD911C04DE029166E0F959C35CC749C61E5A331")]
    [InlineData("section", true, 4, "CC2B31BEA73DECEF2345B1E7A51770EFCAC0FE6994AB9162A22993587229C803")]
    [InlineData("quota", true, 5, "5805E66220B6B1F14F37FB4046D6931B013FC2A7828A7CFE2DC14EBAA4E6407A")]
    [InlineData("section", true, 5, "BA5468EBDF179456F2D8285CF64019CACE6872CC6498D0879A0F1936D1E2F8D8")]
    [InlineData("detail", true, 0, "0C740CD02B0B3F6DB689F1F0B34C30DECEB1186E2D0FE47DA268A34F47262CA2")]
    [InlineData("curation", true, 0, "4BF6CEC676E6BF9762F4DED770C1C4E619F17D511D8F9A7F0D49F5B64308A480")]
    [InlineData("dependent", true, 0, "491E03D5475EB824EB6A98E4D2545F0A09737966D3C4508CF74B68E3FACEC0C5")]
    [InlineData("local", true, 0, "C117A24C3AB42F68BF0D4909393F4E6D7F61450685579ABD5558695145516A68")]
    public void Mutation_sql_templates_remain_byte_identical(string kind, bool campaignScoped, int count, string expected)
    {
        string sql = kind switch
        {
            "quota" => CovenantStoreSql.QuotaSnapshot(campaignScoped, count),
            "section" => CovenantStoreSql.SectionOccupancy(campaignScoped, count),
            "detail" => CovenantStoreSql.Detail(campaignScoped),
            "curation" => CovenantStoreSql.DetailCuration(campaignScoped),
            "dependent" => CovenantStoreSql.DependentHeadScan(campaignScoped),
            "local" => CovenantStoreSql.LocalHeadFactsSql(campaignScoped),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Excluded_key_placeholders_and_outside_bound_behavior_remain_exact(bool campaignScoped)
    {
        for (int count = 0; count <= CovenantLimits.MaxStagedMutationsPerTurn + 1; count++)
        {
            string quota = CovenantStoreSql.QuotaSnapshot(campaignScoped, count);

            string section = CovenantStoreSql.SectionOccupancy(campaignScoped, count);

            for (int index = 0; index < count; index++)
            {
                Assert.Contains($"$xkey{index}", quota, StringComparison.Ordinal);

                Assert.Contains($"$key{index}", section, StringComparison.Ordinal);
            }

            Assert.DoesNotContain($"$xkey{count}", quota, StringComparison.Ordinal);

            Assert.DoesNotContain($"$key{count}", section, StringComparison.Ordinal);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantStoreSql.QuotaSnapshot(campaignScoped, -1));

        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantStoreSql.SectionOccupancy(campaignScoped, -1));
    }
}
