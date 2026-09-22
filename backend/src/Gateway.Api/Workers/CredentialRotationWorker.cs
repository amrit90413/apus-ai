using Gateway.Api.Providers;
using Gateway.Api.Security;

namespace Gateway.Api.Workers;

/// <summary>
/// Re-seals stored provider secrets under the current data key.
///
/// Key rotation is therefore an operational change, not an outage: add the new key,
/// point Encryption:CurrentKeyVersion at it, and this worker walks the rows in the
/// background while old rows keep decrypting under the key that wrote them. The old
/// key can be retired once nothing reports the old version.
/// </summary>
public sealed class CredentialRotationWorker : PeriodicWorker
{
    private readonly IProviderConnectionService _connections;
    private readonly ICredentialEncryption _crypto;

    public CredentialRotationWorker(
        IProviderConnectionService connections, ICredentialEncryption crypto,
        WorkerOptions options, ILogger<CredentialRotationWorker> logger)
        : base(options, logger)
    {
        _connections = connections; _crypto = crypto;
    }

    protected override string Name => nameof(CredentialRotationWorker);
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Options.RotationSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var rotated = await _connections.RotateEncryptionAsync(ct);
        if (rotated > 0)
            Logger.LogInformation("Rotated {Count} provider connection(s) to key version {Version}.",
                rotated, _crypto.CurrentKeyVersion);
    }
}
