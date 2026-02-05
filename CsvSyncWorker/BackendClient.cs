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

        // Build sets for comparison
        var csvSet = csvRecords
            .Select(r => (r.MicrosoftUsername, r.Department))
            .ToHashSet();

        var backendSet = allMappings
            .Select(m => (m.microsoft_username, m.department))
            .ToHashSet();

        // Need to create: in CSV but not in backend
        var toCreate = csvSet.Except(backendSet).ToList();
        // Need to delete: in backend but not in CSV
        var toDelete = allMappings
            .Where(m => !csvSet.Contains((m.microsoft_username, m.department)))
            .ToList();

        _logger.LogInformation("Sync summary: {Create} to create, {Delete} to delete",
            toCreate.Count, toDelete.Count);

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

        // 3. Delete records not present in CSV
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

