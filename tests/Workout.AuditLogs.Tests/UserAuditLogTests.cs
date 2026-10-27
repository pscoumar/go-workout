using System.Text.Json;
using MySqlConnector;
using Workout.AuditLogs;

namespace Workout.AuditLogs.Tests;

public sealed class UserAuditLogTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable("AUDIT_DB_CONNECTION")
        ?? throw new InvalidOperationException("Set AUDIT_DB_CONNECTION to a disposable MySQL 8+ test database.");

    private static async Task<UserAuditLogRepository> CreateRepositoryAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "user_audit_log.sql"));
        await command.ExecuteNonQueryAsync();
        // Exercise the override ensuring unchanged updates count as matching rows.
        return new UserAuditLogRepository(new MySqlConnectionStringBuilder(ConnectionString)
        { UseAffectedRows = true }.ConnectionString);
    }

    [Theory]
    [InlineData(AuditActionType.ADD)]
    [InlineData(AuditActionType.RENEW)]
    [InlineData(AuditActionType.UPDATE)]
    [InlineData(AuditActionType.SUSPEND)]
    [InlineData(AuditActionType.DELETE)]
    public async Task Crud_round_trips_dates_json_nulls_and_all_actions(AuditActionType action)
    {
        var repository = await CreateRepositoryAsync();
        var data = new UserAuditLogData
        {
            UserId = $"user-'😀-{Guid.NewGuid():N}",
            ActionType = action,
            OldExpiry = new DateOnly(2026, 10, 27),
            NewExpiry = new DateOnly(2027, 10, 27),
            OldData = JsonSerializer.SerializeToElement(new { plan = "old" }),
            NewData = JsonSerializer.SerializeToElement(new { plan = "premium 😀", active = true }),
            PerformedBy = "admin'--"
        };
        long id = await repository.CreateAsync(data);
        try
        {
            Assert.True(id > 0);
            var saved = Assert.IsType<UserAuditLog>(await repository.GetByIdAsync(id));
            Assert.Equal(data.UserId, saved.UserId);
            Assert.Equal(action, saved.ActionType);
            Assert.Equal(data.OldExpiry, saved.OldExpiry);
            Assert.Equal(data.NewExpiry, saved.NewExpiry);
            Assert.Equal("old", saved.OldData!.Value.GetProperty("plan").GetString());
            Assert.Equal("premium 😀", saved.NewData!.Value.GetProperty("plan").GetString());
            Assert.Equal(data.PerformedBy, saved.PerformedBy);
            Assert.Equal(DateTimeKind.Unspecified, saved.CreatedAt.Kind);
            Assert.True(saved.CreatedAt.Year >= 2026);
            Assert.Equal(id, Assert.Single(await repository.ListAsync(data.UserId)).Id);
            Assert.Empty(await repository.ListAsync("missing-" + Guid.NewGuid()));
            Assert.True(await repository.UpdateAsync(id, data)); // unchanged row

            var update = data with
            {
                ActionType = AuditActionType.UPDATE, OldExpiry = null, NewExpiry = null,
                OldData = null, NewData = JsonSerializer.SerializeToElement<object?>(null), PerformedBy = null
            };
            Assert.True(await repository.UpdateAsync(id, update));
            var updated = Assert.IsType<UserAuditLog>(await repository.GetByIdAsync(id));
            Assert.Equal(AuditActionType.UPDATE, updated.ActionType);
            Assert.Null(updated.OldExpiry);
            Assert.Null(updated.NewExpiry);
            Assert.Null(updated.OldData); // SQL NULL
            Assert.Equal(JsonValueKind.Null, updated.NewData!.Value.ValueKind); // JSON null
            Assert.Null(updated.PerformedBy);
            Assert.Equal(saved.CreatedAt, updated.CreatedAt);

            Assert.True(await repository.DeleteAsync(id));
            Assert.Null(await repository.GetByIdAsync(id));
            Assert.False(await repository.UpdateAsync(id, data));
            Assert.False(await repository.DeleteAsync(id));
        }
        finally { await repository.DeleteAsync(id); }
    }

    [Fact]
    public async Task Pagination_is_filtered_and_ordered_newest_first()
    {
        var repository = await CreateRepositoryAsync();
        var data = new UserAuditLogData { UserId = Guid.NewGuid().ToString(), ActionType = AuditActionType.ADD };
        var ids = new List<long>();
        try
        {
            ids.Add(await repository.CreateAsync(data));
            ids.Add(await repository.CreateAsync(data));
            Assert.Equal(ids[1], Assert.Single(await repository.ListAsync(data.UserId, 1)).Id);
            Assert.Equal(ids[0], Assert.Single(await repository.ListAsync(data.UserId, 1, 1)).Id);
            Assert.Empty(await repository.ListAsync(data.UserId, 1, 2));
        }
        finally { foreach (long id in ids) await repository.DeleteAsync(id); }
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_opening_a_connection()
    {
        var repository = new UserAuditLogRepository("Server=127.0.0.1;Port=1;User ID=unused");
        var data = new UserAuditLogData { UserId = "valid", ActionType = AuditActionType.ADD };
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(data with { UserId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(data with { UserId = new string('a', 76) }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(data with { PerformedBy = new string('a', 51) }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(data with { NewData = default(JsonElement) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.CreateAsync(data with { ActionType = (AuditActionType)99 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.CreateAsync(data with { NewExpiry = new DateOnly(999, 1, 1) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetByIdAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.DeleteAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpdateAsync(0, data));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAsync(limit: 1001));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAsync(offset: -1));
    }
}
