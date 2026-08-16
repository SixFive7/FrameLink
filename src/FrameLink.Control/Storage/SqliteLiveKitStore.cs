using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FrameLink.Control.Storage;

/// <summary>
/// The LiveKit credential, in the <c>control_meta</c> table the schema already has.
/// </summary>
/// <remarks>
/// <para>
/// Three rows rather than a table, because that is honestly what this is: one key, one secret,
/// one date, for the whole fleet forever. A table with a primary key would be inventing a
/// cardinality the specification does not have — §3.7 gives the Fleet Manager <i>the</i> API
/// secret, singular, and a schema that could hold two would be a schema somebody eventually
/// puts two in.
/// </para>
/// <para>
/// <b>No schema version bump, and none is needed.</b> <c>control_meta</c> is
/// <c>CREATE TABLE IF NOT EXISTS</c> with a free key space and has existed since schema 1, so an
/// existing volume gains this by starting the new container and nothing else — which is exactly
/// the additive property <c>SqliteDatabase</c> claims for the whole schema.
/// </para>
/// </remarks>
public sealed class SqliteLiveKitStore(SqliteDatabase database, TimeProvider clock) : ILiveKitStore
{
    /// <summary>Row holding the API key.</summary>
    private const string KeyRow = "livekit_api_key";

    /// <summary>Row holding the signing secret.</summary>
    private const string SecretRow = "livekit_api_secret";

    /// <summary>Row holding when the secret was last generated.</summary>
    private const string IssuedRow = "livekit_secret_issued_utc";

    /// <summary>
    /// Characters an API key is drawn from.
    /// </summary>
    /// <remarks>
    /// Matches the shape <c>livekit-server generate-keys</c> produces — an <c>API</c> prefix and
    /// twelve mixed-case alphanumerics — so an operator who has ever seen a LiveKit key
    /// recognises this one. It is an identifier rather than a secret: it travels in the clear in
    /// every token's <c>iss</c> claim, and matching upstream's shape costs nothing.
    /// </remarks>
    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Prefix upstream's own generator gives every key.</summary>
    public const string KeyPrefix = "API";

    /// <summary>Random characters after the prefix.</summary>
    public const int KeyRandomLength = 12;

    /// <summary>
    /// Bytes of entropy behind the signing secret.
    /// </summary>
    /// <remarks>
    /// 32, rendered as 43 URL-safe base64 characters. LiveKit refuses a secret shorter than 32
    /// <i>characters</i> and guide 7 fed it 44 from <c>openssl rand -base64 32</c>; this is the
    /// same 256 bits without the padding character, which keeps the value safe to paste into a
    /// YAML scalar, a URL and a shell without quoting rules mattering anywhere.
    /// </remarks>
    public const int SecretBytes = 32;

    /// <inheritdoc/>
    public async Task<LiveKitCredential?> FindAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        return await ReadAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<LiveKitCredential> EnsureAsync(CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // INSERT OR IGNORE, so a second caller inside the same transaction window keeps the first
        // one's values rather than replacing a secret that may already have signed a token.
        await WriteAsync(
            scope.Connection,
            KeyRow,
            NewKey(),
            replace: false,
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(
            scope.Connection,
            SecretRow,
            NewSecret(),
            replace: false,
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(
            scope.Connection,
            IssuedRow,
            SqliteDatabase.FormatTimestamp(clock.GetUtcNow()),
            replace: false,
            cancellationToken).ConfigureAwait(false);

        var credential = await ReadAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return credential ?? throw new InvalidOperationException(
            "The LiveKit credential was written and could not be read back.");
    }

    /// <inheritdoc/>
    public async Task<LiveKitCredential> RotateSecretAsync(CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // The key is left alone. Rotating it as well would change nothing about who can join —
        // the secret is what signs — while making every diagnostic an operator has ever written
        // down wrong, and LiveKit's own key/secret pairing means a token names its key anyway.
        await WriteAsync(scope.Connection, KeyRow, NewKey(), replace: false, cancellationToken)
            .ConfigureAwait(false);

        await WriteAsync(scope.Connection, SecretRow, NewSecret(), replace: true, cancellationToken)
            .ConfigureAwait(false);

        await WriteAsync(
            scope.Connection,
            IssuedRow,
            SqliteDatabase.FormatTimestamp(clock.GetUtcNow()),
            replace: true,
            cancellationToken).ConfigureAwait(false);

        var credential = await ReadAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return credential ?? throw new InvalidOperationException(
            "The rotated LiveKit credential could not be read back.");
    }

    /// <summary>A fresh API key, in the shape upstream's own generator produces.</summary>
    public static string NewKey() =>
        KeyPrefix + RandomNumberGenerator.GetString(KeyAlphabet, KeyRandomLength);

    /// <summary>A fresh signing secret: 256 bits, URL-safe base64, no padding.</summary>
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task<LiveKitCredential?> ReadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT key, value FROM control_meta WHERE key IN ($key, $secret, $issued);";
        command.Parameters.AddWithValue("$key", KeyRow);
        command.Parameters.AddWithValue("$secret", SecretRow);
        command.Parameters.AddWithValue("$issued", IssuedRow);

        string? key = null;
        string? secret = null;
        string? issued = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (reader.GetString(0))
            {
                case KeyRow:
                    key = reader.GetString(1);
                    break;
                case SecretRow:
                    secret = reader.GetString(1);
                    break;
                case IssuedRow:
                    issued = reader.GetString(1);
                    break;
                default:
                    break;
            }
        }

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        return new LiveKitCredential(
            key,
            secret,
            issued is null ? DateTimeOffset.MinValue : SqliteDatabase.ParseTimestamp(issued));
    }

    private static async Task WriteAsync(
        SqliteConnection connection,
        string row,
        string value,
        bool replace,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = replace
            ? """
              INSERT INTO control_meta (key, value) VALUES ($key, $value)
              ON CONFLICT (key) DO UPDATE SET value = $value;
              """
            : "INSERT OR IGNORE INTO control_meta (key, value) VALUES ($key, $value);";

        command.Parameters.AddWithValue("$key", row);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
