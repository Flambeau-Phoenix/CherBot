using CherBot.Core.Platform;
using CherBot.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace CherBot.Host.Security;

/// <summary>Secrets encrypted with ASP.NET Core Data Protection and stored in the database.</summary>
public sealed class DataProtectionSecretStore(IDataProtectionProvider provider, CherBotDb db) : ISecretStore
{
    private readonly IDataProtector _protector = provider.CreateProtector("CherBot.Secrets.v1");

    public string? Get(string name)
    {
        var blob = db.GetSecret(name);
        if (blob is null) return null;
        try { return _protector.Unprotect(blob); }
        catch { return null; }
    }

    public void Set(string name, string value) => db.SetSecret(name, _protector.Protect(value));

    public void Delete(string name) => db.DeleteSecret(name);
}
