// Release signing for Detour's updater (ServiceLib/Common/ReleaseSignature.cs). Run with the .NET 10 SDK:
//   dotnet run --file tools/detour-sign.cs -- keygen [keyfile]
//       new ECDSA P-256 key, saved as a password-encrypted PEM (default %USERPROFILE%\.detour-signing\release-key.pem);
//       prints the public key for ReleaseSignature.PublicKey. Keep a backup of the file and the password offline:
//       without them no future release can be signed, and anyone with both can sign updates.
//   dotnet run --file tools/detour-sign.cs -- sign <archive>... [--key keyfile]
//       writes <archive>.sig next to each archive.
//   dotnet run --file tools/detour-sign.cs -- verify <archive> <publicKeyBase64>
// The key never goes to GitHub: tools/sign-release.ps1 downloads the release archives, signs them here and
// uploads only the .sig files.
using System.Security.Cryptography;

var argv = args.ToList();
if (argv.Count == 0)
{
    return Usage();
}
var keyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".detour-signing", "release-key.pem");
var k = argv.IndexOf("--key");
if (k >= 0 && k + 1 < argv.Count)
{
    keyFile = argv[k + 1];
    argv.RemoveRange(k, 2);
}

switch (argv[0])
{
    case "keygen":
    {
        if (argv.Count > 1)
        {
            keyFile = argv[1];
        }
        if (File.Exists(keyFile))
        {
            Console.Error.WriteLine($"{keyFile} already exists; refusing to overwrite a release key.");
            return 1;
        }
        var password = ReadPassword("New key password: ");
        if (password.Length < 12)
        {
            Console.Error.WriteLine("Use at least 12 characters.");
            return 1;
        }
        if (ReadPassword("Repeat: ") != password)
        {
            Console.Error.WriteLine("Passwords differ.");
            return 1;
        }
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile))!);
        File.WriteAllText(keyFile, pem);
        Console.WriteLine($"Key saved: {keyFile}");
        Console.WriteLine("Public key (ReleaseSignature.PublicKey):");
        Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case "sign" when argv.Count > 1:
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromEncryptedPem(File.ReadAllText(keyFile), ReadPassword($"Password for {keyFile}: "));
        Console.WriteLine($"Public key: {Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo())}");
        foreach (var file in argv.Skip(1))
        {
            using var fs = File.OpenRead(file);
            var sig = ecdsa.SignData(fs, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            File.WriteAllText(file + ".sig", Convert.ToBase64String(sig) + "\n");
            Console.WriteLine($"Signed {Path.GetFileName(file)}");
        }
        return 0;
    }
    case "verify" when argv.Count > 2:
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(argv[2]), out _);
        var sig = Convert.FromBase64String(File.ReadAllText(argv[1] + ".sig").Trim());
        using var fs = File.OpenRead(argv[1]);
        var ok = ecdsa.VerifyData(fs, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine(ok ? "OK" : "BAD SIGNATURE");
        return ok ? 0 : 2;
    }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: keygen [keyfile] | sign <archive>... [--key keyfile] | verify <archive> <publicKeyBase64>");
    return 1;
}

static string ReadPassword(string prompt)
{
    // For scripted tests only; a real key's password belongs in the person's head or password manager.
    if (Environment.GetEnvironmentVariable("DETOUR_SIGN_PASSWORD") is { Length: > 0 } fromEnv)
    {
        return fromEnv;
    }
    Console.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return (Console.ReadLine() ?? "").Trim('﻿', '\r', ' ');
    }
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0)
            {
                chars.RemoveAt(chars.Count - 1);
                Console.Write("\b \b");
            }
            continue;
        }
        if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
        {
            continue;
        }
        chars.Add(key.KeyChar);
        // A star per character: nothing visible looks like the input is broken.
        Console.Write('*');
    }
    Console.WriteLine();
    return new string([.. chars]);
}
