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
    [InlineData("quota", false, 0, "94C0365A424325892FD673C13307565E9C2C6E98057A3C3A9CCB1B565DA21C86")]
    [InlineData("section", false, 0, "1A4835064680A31A897AAD729FCA7D382B9853295D8DAC3D8832B1ED4E1B984A")]
    [InlineData("quota", false, 1, "2D78CD10D20671534B46AEADC573DBD946AE22A109DCA23AA69DE08E48683715")]
    [InlineData("section", false, 1, "06A9736A7323A69ED70A8CA9E360BE797BED92235B42496E8610A9D560BE7624")]
    [InlineData("quota", false, 2, "CB9708116E8F4B2ABDBAB74999B1703F46963DA5134D30077CF7087210D03C4F")]
    [InlineData("section", false, 2, "15A4D8ECFC808BABDFAFC52A3E76058DD5B6900B20ED5532C6A95C3D0DC42A78")]
    [InlineData("quota", false, 3, "3622B49E5E88AF715E378F8EB21C8AEC72A3A7849298335F60F28BB65D04012A")]
    [InlineData("section", false, 3, "65AF2DD960DCCF0B8C8E19BA9BFAA6AFB166B6A254220ECEF004F48CC69034A3")]
    [InlineData("quota", false, 4, "BEDB70E7F09753E6E58080B70CD9CB28C78FDF5D811ACE9E5153DBF543DBBF89")]
    [InlineData("section", false, 4, "6D518AACD3F95B74FF6B07904340488C471D877A92F20FC86DEADD4966809661")]
    [InlineData("quota", false, 5, "FD21245CD912510CDF38F4E8E48A513559924651ACEA628032D6E68BAED3D96D")]
    [InlineData("section", false, 5, "99C097EE82958E8322288DC5C9E9A88D86FD39BA94C3FFD5D4D3C53884648635")]
    [InlineData("detail", false, 0, "6F949806F0A5C560A8BA280E5CDC180B3A510CDC8DDECF67F7D0072C3B262A76")]
    [InlineData("curation", false, 0, "289BE53096F83C0CF20844143DD19C16D9D9C947032E908EDB5CCFA082D4BF58")]
    [InlineData("dependent", false, 0, "C1D0906C15A85C8BD23A4E1C245D516651F0842AFD42B960B0FC4778529EF030")]
    [InlineData("local", false, 0, "F25F7762D31C5466BF741F5EC97430191AECB8777E35FD91BDC20DE96B7122DE")]
    [InlineData("quota", true, 0, "B59A992E1B2C0EC2304BAD2A4D8AFD0F399F62E2B0982FA093BE2552F76BE9E8")]
    [InlineData("section", true, 0, "0935B3D17C27C7DF6ABF1448D8D24C03AA2AEADF2A6546B279302F65F151B26A")]
    [InlineData("quota", true, 1, "C2C85AC092EE66B04BC8360421A1B8196A1EE60D2E1B99F8B63A715CC154DBAD")]
    [InlineData("section", true, 1, "B3AE65E712E28B9AFC2BD711A61EFE95FE8ADEAB17380D653CE496E7BE2F394A")]
    [InlineData("quota", true, 2, "7A540F486DA996A1EC237B59B9A0BA4A308608D83CA97ED6FDB34C435718CCB3")]
    [InlineData("section", true, 2, "80DC8BDD3DA71513B192D82C77F88EDC7F79D1B5AB11FF4A0E0066A8C7299B08")]
    [InlineData("quota", true, 3, "55425E1AF0E5DF41D815A680A5089DC8F6B652F5A2E5A4FEA54AE151A2626256")]
    [InlineData("section", true, 3, "B9BDCAC65BC89AE122786CA8383D1A87E415CF7B598E361AB6E2BB55D4CE0883")]
    [InlineData("quota", true, 4, "3663597565830BDBF28D6003DF864A44A7FD9E5D4E52AD5F10C73F6EC6D29F38")]
    [InlineData("section", true, 4, "CC2B31BEA73DECEF2345B1E7A51770EFCAC0FE6994AB9162A22993587229C803")]
    [InlineData("quota", true, 5, "BC501BD94C982063417C2190AA3D93A6E580FEDA98B1D5BD476F15CE193921A8")]
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
