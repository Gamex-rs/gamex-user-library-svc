using MySql.Data.MySqlClient;

namespace Gamex.UserLibrary.Infrastructure.Jobs;

/// <summary>
/// Cluster-wide lock via MySQL <c>GET_LOCK</c> so only one service instance runs a job.
/// The lock lives as long as its connection; disposing releases it (and MySQL releases it if the instance dies).
/// </summary>
internal sealed class MySqlJobLock(string connectionString)
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT GET_LOCK(@name, 0)", connection);
            command.Parameters.AddWithValue("@name", name);

            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                return new Handle(connection, name);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class Handle(MySqlConnection connection, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new MySqlCommand("SELECT RELEASE_LOCK(@name)", connection);
                command.Parameters.AddWithValue("@name", name);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
