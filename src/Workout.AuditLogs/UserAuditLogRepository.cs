using System.Text.Json;
using MySqlConnector;

namespace Workout.AuditLogs;

/// <summary>Async CRUD for an existing user_audit_log table. Each call owns its connection.</summary>
public sealed class UserAuditLogRepository
{
    private const string Columns = "id, user_id, action_type, old_expiry, new_expiry, old_data, new_data, performed_by, created_at";
    private readonly string _connectionString;

    public UserAuditLogRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // Matched rows make an unchanged UPDATE return true for an existing row.
        _connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            UseAffectedRows = false,
            DateTimeKind = MySqlDateTimeKind.Unspecified
        }.ConnectionString;
    }

    public async Task<long> CreateAsync(UserAuditLogData data, CancellationToken cancellationToken = default)
    {
        Validate(data);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_audit_log
                (user_id, action_type, old_expiry, new_expiry, old_data, new_data, performed_by)
            VALUES (@user_id, @action_type, @old_expiry, @new_expiry, @old_data, @new_data, @performed_by)
            """;
        AddData(command, data);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return command.LastInsertedId;
    }

    public async Task<UserAuditLog?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM user_audit_log WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    /// <summary>Newest first, optionally filtered by user; limit is 1–1000.</summary>
    public async Task<IReadOnlyList<UserAuditLog>> ListAsync(string? userId = null, int limit = 100,
        int offset = 0, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (userId is not null) ValidateText(userId, 75, nameof(userId), required: true);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM user_audit_log " +
            (userId is null ? "" : "WHERE user_id = @user_id ") +
            "ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset";
        if (userId is not null) command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        var results = new List<UserAuditLog>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Map(reader));
        return results;
    }

    /// <summary>Replaces all editable fields; preserves id and created_at. False means missing id.</summary>
    public async Task<bool> UpdateAsync(long id, UserAuditLogData data, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        Validate(data);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE user_audit_log SET user_id = @user_id, action_type = @action_type,
                old_expiry = @old_expiry, new_expiry = @new_expiry, old_data = @old_data,
                new_data = @new_data, performed_by = @performed_by
            WHERE id = @id
            """;
        AddData(command, data);
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Permanently removes the audit record. False means missing id.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_audit_log WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static void AddData(MySqlCommand command, UserAuditLogData data)
    {
        command.Parameters.AddWithValue("@user_id", data.UserId);
        command.Parameters.AddWithValue("@action_type", data.ActionType.ToString());
        command.Parameters.Add("@old_expiry", MySqlDbType.Date).Value =
            data.OldExpiry is { } oldDate ? oldDate.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.Add("@new_expiry", MySqlDbType.Date).Value =
            data.NewExpiry is { } newDate ? newDate.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.Add("@old_data", MySqlDbType.JSON).Value = (object?)data.OldData?.GetRawText() ?? DBNull.Value;
        command.Parameters.Add("@new_data", MySqlDbType.JSON).Value = (object?)data.NewData?.GetRawText() ?? DBNull.Value;
        command.Parameters.AddWithValue("@performed_by", (object?)data.PerformedBy ?? DBNull.Value);
    }

    private static UserAuditLog Map(MySqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        UserId = reader.GetString(1),
        ActionType = Enum.Parse<AuditActionType>(reader.GetString(2)),
        OldExpiry = reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
        NewExpiry = reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
        OldData = ReadJson(reader, 5),
        NewData = ReadJson(reader, 6),
        PerformedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt = reader.GetDateTime(8)
    };

    private static JsonElement? ReadJson(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        using var document = JsonDocument.Parse(reader.GetString(ordinal));
        return document.RootElement.Clone();
    }

    private static void Validate(UserAuditLogData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateText(data.UserId, 75, nameof(data.UserId), required: true);
        ValidateText(data.PerformedBy, 50, nameof(data.PerformedBy), required: false);
        if (!Enum.IsDefined(data.ActionType)) throw new ArgumentOutOfRangeException(nameof(data.ActionType));
        if (data.OldExpiry is { Year: < 1000 } || data.NewExpiry is { Year: < 1000 })
            throw new ArgumentOutOfRangeException(nameof(data), "MySQL dates must be in the range 1000-01-01 through 9999-12-31.");
        if (data.OldData?.ValueKind == JsonValueKind.Undefined || data.NewData?.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("JSON must be valid or null.", nameof(data));
    }

    private static void ValidateText(string? value, int maxLength, string name, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name);
        if (value is not null && value.EnumerateRunes().Count() > maxLength)
            throw new ArgumentException($"Maximum length is {maxLength} characters.", name);
    }
}
