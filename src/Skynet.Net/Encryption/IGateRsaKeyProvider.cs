using System.Security.Cryptography;

namespace Skynet.Net.Encryption;

/// <summary>
/// Supplies the RSA keypair used by the gate encryption handshake. The gate side calls
/// <see cref="CreatePrivateKey"/> to decrypt ConfirmEncryptKey payloads; the client side calls
/// <see cref="CreatePublicKey"/> to encrypt them. Implementations must return fresh <see cref="RSA"/>
/// instances (callers dispose them) so handshakes never share key objects.
/// </summary>
public interface IGateRsaKeyProvider
{
	/// <summary>Returns a fresh RSA holding the gate private key. Callers dispose.</summary>
	RSA CreatePrivateKey();

	/// <summary>Returns a fresh RSA holding the gate public key. Callers dispose.</summary>
	RSA CreatePublicKey();
}

/// <summary>
/// In-memory keypair provider that clones its RSA parameters on every <c>CreateXxxKey</c> call so each
/// handshake owns an independent instance. Intended for tests and self-contained deployments; production
/// deployments should provide an implementation that loads a persisted keypair so it survives restarts.
/// </summary>
public sealed class InMemoryGateRsaKeyProvider : IGateRsaKeyProvider, IDisposable
{
	private readonly RSAParameters _privateParameters;
	private readonly RSAParameters _publicParameters;

	public InMemoryGateRsaKeyProvider(RSA rsa)
	{
		ArgumentNullException.ThrowIfNull(rsa);
		_privateParameters = rsa.ExportParameters(includePrivateParameters: true);
		_publicParameters = rsa.ExportParameters(includePrivateParameters: false);
	}

	/// <summary>Convenience factory that generates a fresh <paramref name="keySize"/>-bit RSA keypair.</summary>
	public static InMemoryGateRsaKeyProvider Generate(int keySize = 2048)
	{
		using var rsa = RSA.Create(keySize);
		return new InMemoryGateRsaKeyProvider(rsa);
	}

	public RSA CreatePrivateKey()
	{
		var rsa = RSA.Create();
		rsa.ImportParameters(_privateParameters);
		return rsa;
	}

	public RSA CreatePublicKey()
	{
		var rsa = RSA.Create();
		rsa.ImportParameters(_publicParameters);
		return rsa;
	}

	public void Dispose()
	{
		// Nothing to dispose: we hold only value-type RSAParameters copies. Private key material is cleared
		// when the caller-owned RSA instances are disposed.
	}
}
