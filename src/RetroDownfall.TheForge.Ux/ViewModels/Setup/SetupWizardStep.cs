namespace RetroDownfall.TheForge.Ux.ViewModels.Setup;

/// <summary>Ordered steps in The Forge first-run / connection setup wizard.</summary>
public enum SetupWizardStep
{

    BaseUrl = 0,

    ApiKey = 1,

    TestConnection = 2,

    ProvidersAndModels = 3,

    DefaultModel = 4,

    Embeddings = 5,

    Complete = 6,

}
