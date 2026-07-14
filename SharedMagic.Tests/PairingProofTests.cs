using SharedMagic.Contracts;

namespace SharedMagic.Tests;

public sealed class PairingProofTests
{
    [Fact]
    public void ProofBindsEveryIdentityField()
    {
        var challenge = Guid.NewGuid();
        var node = Guid.NewGuid();
        var gateway = Guid.NewGuid();
        var cluster = Guid.NewGuid();
        var nonce = Enumerable.Range(0, 32).Select(static x => (byte)x).ToArray();

        var first = PairingProof.Compute("secret", challenge, nonce, node, gateway, cluster, "MagicAiGateway");
        var same = PairingProof.Compute("secret", challenge, nonce, node, gateway, cluster, "MagicAiGateway");
        var changed = PairingProof.Compute("secret", challenge, nonce, Guid.NewGuid(), gateway, cluster, "MagicAiGateway");

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
    }
}
