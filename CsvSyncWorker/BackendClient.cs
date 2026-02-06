using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CsvSyncWorker;

public class UserDepartmentRecord
{
    public string MicrosoftUsername { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
}

public class UserDeptDto
{
    public int Id { get; set; }
    public string microsoft_username { get; set; } = string.Empty;
    public string department { get; set; } = string.Empty;
}

public class BackendClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BackendClient> _logger;
    private readonly IConfiguration _configuration;

    public BackendClient(
        HttpClient httpClient,
        ILogger<BackendClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task SyncUserDepartmentMappingsAsync(
        List<UserDepartmentRecord> csvRecords,
        CancellationToken cancellationToken)
    {
        // 1. Get all current mappings from backend
        var allMappings = await _httpClient.GetFromJsonAsync<List<UserDeptDto>>(
            "/user-department-mappings/",
            cancellationToken
        ) ?? new List<UserDeptDto>();

        var csvByUser = csvRecords
            .Where(r => !string.IsNullOrWhiteSpace(r.MicrosoftUsername) &&
                        !string.IsNullOrWhiteSpace(r.Department))
            .GroupBy(r => r.MicrosoftUsername.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Department.Trim(), StringComparer.OrdinalIgnoreCase);

        var backendByUser = allMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.microsoft_username))
            .ToDictionary(m => m.microsoft_username, m => m, StringComparer.OrdinalIgnoreCase);

        var toCreate = csvByUser
            .Where(kvp => !backendByUser.ContainsKey(kvp.Key))
            .Select(kvp => (microsoftUsername: kvp.Key, department: kvp.Value))
            .ToList();

        var toUpdate = csvByUser
            .Where(kvp => backendByUser.TryGetValue(kvp.Key, out var existing) &&
                          !string.Equals(existing.department, kvp.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => (mapping: backendByUser[kvp.Key], department: kvp.Value))
            .ToList();

        var toDelete = backendByUser
            .Where(kvp => !csvByUser.ContainsKey(kvp.Key))
            .Select(kvp => kvp.Value)
            .ToList();

        _logger.LogInformation("Sync summary: {Create} to create, {Update} to update, {Delete} to delete",
            toCreate.Count, toUpdate.Count, toDelete.Count);

        // 2. Create missing records
        foreach (var (microsoftUsername, department) in toCreate)
        {
            var payload = new
            {
                microsoft_username = microsoftUsername,
                department = department
            };

            var resp = await _httpClient.PostAsJsonAsync(
                "/user-department-mappings/",
                payload,
                cancellationToken
            );

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to create mapping {User}/{Dept}: {Status} {Body}",
                    microsoftUsername, department, resp.StatusCode, body);
            }
        }

        // 3. Update records where department differs from CSV
        foreach (var (mapping, department) in toUpdate)
        {
            var payload = new
            {
                microsoft_username = mapping.microsoft_username,
                department = department
            };

            var resp = await _httpClient.PutAsJsonAsync(
                $"/user-department-mappings/{mapping.Id}",
                payload,
                cancellationToken
            );

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to update mapping {User}/{Dept}: {Status} {Body}",
                    mapping.microsoft_username, department, resp.StatusCode, body);
            }
        }

        // 4. Delete records not present in CSV
        foreach (var mapping in toDelete)
        {
            var resp = await _httpClient.DeleteAsync(
                $"/user-department-mappings/{mapping.Id}",
                cancellationToken
            );

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete mapping {Id}: {Status} {Body}",
                    mapping.Id, resp.StatusCode, body);
            }
        }
    }
}

