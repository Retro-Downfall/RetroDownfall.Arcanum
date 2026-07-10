namespace RetroDownfall.Arcanum.Secrets.Security;

public enum OsCredentialStoreStatus
{

    Ok = 0,

    NotFound = 1,

    Unavailable = 2,

    Failed = 3,

}

public readonly record struct OsCredentialStoreResult(OsCredentialStoreStatus Status, string? Value, string? Message)
{

    public static OsCredentialStoreResult Ok(string value) => new(OsCredentialStoreStatus.Ok, value, null);

    public static OsCredentialStoreResult NotFound() => new(OsCredentialStoreStatus.NotFound, null, null);

    public static OsCredentialStoreResult Unavailable(string message) =>
        new(OsCredentialStoreStatus.Unavailable, null, message);

    public static OsCredentialStoreResult Failed(string message) =>
        new(OsCredentialStoreStatus.Failed, null, message);

}
