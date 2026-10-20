using System.Text.Json;
using PinBot.Contracts;

// Release signing for cabinet and server auto-update, run by publish.ps1 and the release workflow:
//   PinBot.Sign key  DIR [--existing]          print DIR's public key; create a key pair first if missing (unless
//                                              --existing: then a missing key is an error, never a silent new key)
//   PinBot.Sign sign DIR EXE VERSION OUT.json  sign EXE as VERSION with DIR's private key (cabinet agent)
//   PinBot.Sign server DIR VERSION SERVER_IMAGE UPDATER_IMAGE NOTES.txt OUT.json
//                                              sign a server release (images by digest; NOTES.txt one note per line)
// The private key is never committed (keys/ is git-ignored); the release workflow writes it from a GitHub secret. Without
// it no further update can be signed, and every cabinet needs one manual reinstall with a build carrying a new key.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
switch (args)
{
    case ["key", var dir, ..var rest]:
    {
        Directory.CreateDirectory(dir);
        var (priv, pub) = (Path.Combine(dir, "update-signing.pem"), Path.Combine(dir, "update-signing.pub"));
        if (!File.Exists(priv))
        {
            if (rest.Contains("--existing"))
            {
                Console.Error.WriteLine($"no release signing key in {dir} (update-signing.pem): refusing to create a new one");
                return 2;
            }
            var (pem, publicKey) = UpdateSigning.NewKey();
            File.WriteAllText(priv, pem);
            File.WriteAllText(pub, publicKey);
            Console.Error.WriteLine($"new release signing key created in {dir}: back up update-signing.pem, keep it private");
        }
        if (!File.Exists(pub)) File.WriteAllText(pub, UpdateSigning.PublicKeyOf(File.ReadAllText(priv)));
        Console.Write(File.ReadAllText(pub).Trim());
        return 0;
    }
    case ["sign", var dir, var exe, var version, var output]:
    {
        var manifest = UpdateSigning.Sign(File.ReadAllText(Path.Combine(dir, "update-signing.pem")), long.Parse(version), File.ReadAllBytes(exe));
        if (!UpdateSigning.Verify(manifest, File.ReadAllText(Path.Combine(dir, "update-signing.pub")).Trim()))
            throw new InvalidOperationException("signature does not verify with the public key");
        File.WriteAllText(output, JsonSerializer.Serialize(manifest, json));
        return 0;
    }
    case ["server", var dir, var version, var serverImage, var updaterImage, var notesFile, var output]:
    {
        var notes = File.ReadAllLines(notesFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var release = UpdateSigning.SignServer(File.ReadAllText(Path.Combine(dir, "update-signing.pem")), long.Parse(version), serverImage, updaterImage, notes);
        if (!UpdateSigning.VerifyServer(release, File.ReadAllText(Path.Combine(dir, "update-signing.pub")).Trim()))
            throw new InvalidOperationException("signature does not verify with the public key");
        File.WriteAllText(output, JsonSerializer.Serialize(release, json));
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: PinBot.Sign key DIR [--existing] | sign DIR EXE VERSION OUT.json | server DIR VERSION SERVER_IMAGE UPDATER_IMAGE NOTES.txt OUT.json");
        return 1;
}
