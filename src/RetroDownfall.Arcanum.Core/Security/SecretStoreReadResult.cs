namespace RetroDownfall.Arcanum.Core.Security;

public sealed record SecretStoreReadResult(SecretStoreReadStatus Status, string? Value, string? Message)
{

    public static SecretStoreReadResult Ok(string value) => new(SecretStoreReadStatus.Ok, value, null);

    public static SecretStoreReadResult Missing() => new(SecretStoreReadStatus.Missing, null, null);

    public static SecretStoreReadResult Corrupted(string message) => new(SecretStoreReadStatus.Corrupted, null, message);

}
