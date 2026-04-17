using System.Data;
using System.Data.Common;
using System.Text.Json;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

public sealed class AdoNetGrainStorage : IGrainStorage
{
    private readonly Func<DbConnection> _connectionFactory;
    private bool _initialized;

    public AdoNetGrainStorage(Func<DbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async ValueTask ReadStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PayloadJson, ETag FROM OrleansGrainState WHERE GrainType = @grainType AND GrainKey = @grainKey AND StateName = @stateName";
        AddParameter(command, "@grainType", grainId.GrainType);
        AddParameter(command, "@grainKey", grainId.Key);
        AddParameter(command, "@stateName", stateName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(0);
            var eTag = reader.GetString(1);
            grainState.State = JsonSerializer.Deserialize<TState>(json)!;
            grainState.ETag = eTag;
            grainState.RecordExists = true;
        }
        else
        {
            grainState.RecordExists = false;
            grainState.ETag = null;
        }
    }

    public async ValueTask WriteStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var newETag = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(grainState.State);

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);

        int rowsAffected;

        if (grainState.ETag is null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO OrleansGrainState (GrainType, GrainKey, StateName, PayloadJson, ETag) VALUES (@grainType, @grainKey, @stateName, @payload, @newETag)";
            AddParameter(command, "@grainType", grainId.GrainType);
            AddParameter(command, "@grainKey", grainId.Key);
            AddParameter(command, "@stateName", stateName);
            AddParameter(command, "@payload", json);
            AddParameter(command, "@newETag", newETag);

            try
            {
                rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (DbException)
            {
                throw new InconsistentStateException(grainId, stateName, null, "existing");
            }
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE OrleansGrainState SET PayloadJson = @payload, ETag = @newETag WHERE GrainType = @grainType AND GrainKey = @grainKey AND StateName = @stateName AND ETag = @expectedETag";
            AddParameter(command, "@grainType", grainId.GrainType);
            AddParameter(command, "@grainKey", grainId.Key);
            AddParameter(command, "@stateName", stateName);
            AddParameter(command, "@payload", json);
            AddParameter(command, "@newETag", newETag);
            AddParameter(command, "@expectedETag", grainState.ETag);

            rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (rowsAffected == 0)
        {
            throw new InconsistentStateException(grainId, stateName, grainState.ETag, null);
        }

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async ValueTask ClearStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM OrleansGrainState WHERE GrainType = @grainType AND GrainKey = @grainKey AND StateName = @stateName";
        AddParameter(command, "@grainType", grainId.GrainType);
        AddParameter(command, "@grainKey", grainId.Key);
        AddParameter(command, "@stateName", stateName);

        await command.ExecuteNonQueryAsync(cancellationToken);
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS OrleansGrainState (
                GrainType TEXT NOT NULL,
                GrainKey TEXT NOT NULL,
                StateName TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                ETag TEXT NOT NULL,
                PRIMARY KEY (GrainType, GrainKey, StateName)
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _initialized = true;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        parameter.DbType = DbType.String;
        command.Parameters.Add(parameter);
    }
}
