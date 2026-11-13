using System.Security.Cryptography;
using System.Text;

namespace Vestigo.Playwright.Setup;

/// <summary>Stable Windows Installer identity for this plugin. It is not Vestigo's product.</summary>
public static class ProductIdentity
{
    public static readonly Guid UpgradeCode = new("6f3c8e21-4b7a-4d19-8c52-9e1a0d6b7f24");

    /// <summary>UUID v5 namespace for versioned product codes. The name is <c>Vestigo.Playwright:major.minor.build</c>.</summary>
    public static readonly Guid ProductCodeNamespace = new("2c91e7a4-8d05-4f6b-b3a1-7e5c9d2f8140");

    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text, out Version? parsed))
            throw new SetupException(ExitCodes.BadArguments, "The product version must be a numeric major.minor.build version.");
        int build = parsed.Build < 0 ? 0 : parsed.Build;
        if (parsed.Revision > 0)
            throw new SetupException(ExitCodes.BadArguments, "The product version uses three numeric parts. Windows Installer ignores a fourth part.");
        if (parsed.Major is < 0 or > 255 || parsed.Minor is < 0 or > 255 || build > 255)
            throw new SetupException(ExitCodes.BadArguments, "Each product version part must be from 0 through 255.");
        return new Version(parsed.Major, parsed.Minor, build);
    }

    public static Guid ProductCode(Version version)
    {
        Version parsed = ParseVersion(version.ToString());
        return Uuid5.Create(ProductCodeNamespace, $"Vestigo.Playwright:{parsed.Major}.{parsed.Minor}.{parsed.Build}");
    }

    public static Guid ProductCode(string version) => ProductCode(ParseVersion(version));
}

public static class Uuid5
{
    public static Guid Create(Guid namespaceId, string name)
    {
        byte[] namespaceBytes = namespaceId.ToByteArray();
        SwapGuidLayout(namespaceBytes);
        byte[] hash = SHA1.HashData(namespaceBytes.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        var guid = new byte[16];
        Array.Copy(hash, guid, 16);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        SwapGuidLayout(guid);
        return new Guid(guid);
    }

    private static void SwapGuidLayout(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
