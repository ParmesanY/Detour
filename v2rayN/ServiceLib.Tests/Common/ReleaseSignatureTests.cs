using System.Security.Cryptography;

namespace ServiceLib.Tests.Common;

public class ReleaseSignatureTests
{
    private static (string PublicKey, Func<byte[], string> Sign) NewKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            data => Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");
    }

    [Test]
    public async Task BuiltInKey_IsAP256PublicKey()
    {
        await Assert.That(ReleaseSignature.HasKey).IsTrue();
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ReleaseSignature.PublicKey), out var read);
        await Assert.That(read).IsEqualTo(Convert.FromBase64String(ReleaseSignature.PublicKey).Length);
        await Assert.That(ecdsa.KeySize).IsEqualTo(256);
    }

    [Test]
    public async Task Update_InstallsOnlySignedArchives()
    {
        var (pub, sign) = NewKey();
        var (otherPub, otherSign) = NewKey();
        var file = Path.Combine(Path.GetTempPath(), $"detour-sig-{Guid.NewGuid():N}.zip");
        var data = RandomNumberGenerator.GetBytes(100_000);
        File.WriteAllBytes(file, data);
        try
        {
            var sig = sign(data);
            await Assert.That(AppUpdateManager.CheckSignature(file, sig, pub)).IsNull();
            await Assert.That(AppUpdateManager.CheckSignature(file, "# comment\n" + sig, pub)).IsNull();

            // Missing, garbage or HTML (a 404 page) instead of a signature.
            await Assert.That(AppUpdateManager.CheckSignature(file, null, pub)).IsEqualTo(AppText.UpdateNotSigned);
            await Assert.That(AppUpdateManager.CheckSignature(file, "<html>Not Found</html>", pub)).IsEqualTo(AppText.UpdateNotSigned);
            await Assert.That(AppUpdateManager.CheckSignature(file, "QUJD", pub)).IsEqualTo(AppText.UpdateNotSigned);

            // Signed by another key, or the archive changed after signing.
            await Assert.That(AppUpdateManager.CheckSignature(file, otherSign(data), pub)).IsEqualTo(AppText.UpdateBadSignature);
            await Assert.That(AppUpdateManager.CheckSignature(file, sig, otherPub)).IsEqualTo(AppText.UpdateBadSignature);
            data[500] ^= 1;
            File.WriteAllBytes(file, data);
            await Assert.That(AppUpdateManager.CheckSignature(file, sig, pub)).IsEqualTo(AppText.UpdateBadSignature);

            // No key built in: never install.
            await Assert.That(AppUpdateManager.CheckSignature(file, sig, "")).IsEqualTo(AppText.UpdateNoKey);
            await Assert.That(ReleaseSignature.Verify(new MemoryStream(data), new byte[64], "not base64!")).IsFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
