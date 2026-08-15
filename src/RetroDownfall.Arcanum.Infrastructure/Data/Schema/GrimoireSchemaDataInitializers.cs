namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The exactly-one-per-tier set of data initializers, validated before any transaction opens.
/// </summary>
/// <remarks>
/// Checking the set up front rather than looking one up mid-install matters: a missing initializer
/// discovered inside a live transaction would roll back DDL that was otherwise fine, and a duplicate
/// would make seeding order depend on registration order. Both become a startup-time composition
/// error here instead.
/// </remarks>
internal sealed class GrimoireSchemaDataInitializers
{

    private readonly Dictionary<GrimoireSchemaTransactionTier, IGrimoireSchemaDataInitializer> _byTier;

    public GrimoireSchemaDataInitializers(IEnumerable<IGrimoireSchemaDataInitializer> initializers)
    {

        ArgumentNullException.ThrowIfNull(initializers);

        _byTier = [];

        foreach (IGrimoireSchemaDataInitializer initializer in initializers)
        {

            if (!_byTier.TryAdd(initializer.TransactionTier, initializer))
            {

                throw new InvalidOperationException(
                    $"More than one Grimoire schema data initializer is registered for the {initializer.TransactionTier} tier.");

            }

        }

        foreach (GrimoireSchemaTransactionTier tier in Enum.GetValues<GrimoireSchemaTransactionTier>())
        {

            if (!_byTier.ContainsKey(tier))
            {

                throw new InvalidOperationException(
                    $"No Grimoire schema data initializer is registered for the {tier} tier.");

            }

        }

    }

    public IGrimoireSchemaDataInitializer For(GrimoireSchemaTransactionTier tier) => _byTier[tier];

}
