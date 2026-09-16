using Windows.Security.Credentials;

namespace PalmierPro.WinUI.AI;

public sealed class WindowsCredentialStore
{
    private const string ResourcePrefix = "PalmierPro/";
    private readonly PasswordVault _vault = new();

    public void Save(string providerId, string secret)
    {
        Remove(providerId);
        _vault.Add(new PasswordCredential(ResourcePrefix + providerId, providerId, secret));
    }

    public string? Read(string providerId)
    {
        try
        {
            var credential = _vault.Retrieve(ResourcePrefix + providerId, providerId);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception) { return null; }
    }

    public void Remove(string providerId)
    {
        try
        {
            var credential = _vault.Retrieve(ResourcePrefix + providerId, providerId);
            _vault.Remove(credential);
        }
        catch (Exception) { }
    }
}
