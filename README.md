## CsvSyncService - Windows Service for User-Department Mapping

cd /Users/chenziwen/PycharmProjects/Falcon/CsvSyncService/CsvSyncWorker
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish

This project is intended to be a **standalone Windows Service** that:

- Watches a configured folder on the Windows host for incoming CSV files.
- Parses each CSV into `(microsoft_username, department)` records.
- Synchronizes these records with the backend **User-Department Mapping** APIs:
  - Creates mappings that exist in CSV but not in the backend.
  - Deletes mappings that exist in the backend but not in the CSV.

The service only talks to the existing FastAPI backend via HTTP; it does **not** share runtime or dependencies with the Python/React app.

---

### Recommended Technology

Use **C# + .NET (e.g., .NET 8)** with a **Worker Service** hosted as a Windows Service.

Reasons:
- Native Windows Service support (start/stop via `services.msc`, auto-start on boot).
- Fully independent from Python/Node environments (no runtime conflicts).
- Strong ecosystem for file watching and HTTP client.

---

### Project Structure (suggested)

Inside this `CsvSyncService` folder, you can create a standard .NET Worker Service project, for example:

- `CsvSyncService.sln`
- `CsvSyncService/`
  - `CsvSyncWorker.csproj`
  - `Program.cs`
  - `CsvWorker.cs`
  - `appsettings.json`
  - `Models/`
  - `Services/BackendClient.cs`

Below is example code you can use as a starting point.

---

### Example `Program.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CsvSyncService;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "CsvSyncService";
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddHostedService<CsvWorker>();

                services.AddHttpClient<BackendClient>(client =>
                {
                    var config = hostContext.Configuration;
                    var baseUrl = config["Backend:BaseUrl"] ?? "http://localhost:8089";
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
            });
}
```

---

### Example `CsvWorker.cs`

```csharp
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsvSyncService;

public class CsvWorker : BackgroundService
{
    private readonly ILogger<CsvWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly BackendClient _backendClient;

    public CsvWorker(
        ILogger<CsvWorker> logger,
        IConfiguration configuration,
        BackendClient backendClient)
    {
        _logger = logger;
        _configuration = configuration;
        _backendClient = backendClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var folderPath = _configuration["Watcher:FolderPath"];
        var intervalSeconds = int.TryParse(_configuration["Watcher:IntervalSeconds"], out var v) ? v : 60;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _logger.LogError("Watcher:FolderPath is not configured.");
            return;
        }

        _logger.LogInformation("CsvWorker started. Watching folder: {Folder} (every {Seconds} seconds)", folderPath, intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessFolderAsync(folderPath, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing folder.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("Folder does not exist: {Folder}", folderPath);
            return;
        }

        // You can refine the pattern if needed
        var csvFiles = Directory.GetFiles(folderPath, "*.csv");
        foreach (var file in csvFiles)
        {
            _logger.LogInformation("Processing CSV file: {File}", file);
            try
            {
                var records = await LoadCsvAsync(file, cancellationToken);
                await _backendClient.SyncUserDepartmentMappingsAsync(records, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process CSV file: {File}", file);
            }
        }
    }

    private async Task<List<UserDepartmentRecord>> LoadCsvAsync(string filePath, CancellationToken cancellationToken)
    {
        var result = new List<UserDepartmentRecord>();
        var config = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            Encoding = Encoding.UTF8
        };

        await using var reader = new StreamReader(filePath, Encoding.UTF8);
        await using var csv = new CsvReader(reader, config);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = new UserDepartmentRecord
            {
                MicrosoftUsername = csv.GetField("microsoft_username"),
                Department = csv.GetField("department")
            };
            if (!string.IsNullOrWhiteSpace(record.MicrosoftUsername) &&
                !string.IsNullOrWhiteSpace(record.Department))
            {
                result.Add(record);
            }
        }

        return result;
    }
}
```

---

### Example `BackendClient.cs`

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CsvSyncService;

public record UserDepartmentRecord(string MicrosoftUsername, string Department);

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
            "/api/user-department-mappings/",
            cancellationToken
        ) ?? new List<UserDeptDto>();

        // Build sets for comparison
        var csvSet = csvRecords
            .Select(r => (r.MicrosoftUsername, r.Department))
            .ToHashSet();

        var backendSet = allMappings
            .Select(m => (m.MicrosoftUsername, m.Department))
            .ToHashSet();

        // Need to create: in CSV but not in backend
        var toCreate = csvSet.Except(backendSet).ToList();
        // Need to delete: in backend but not in CSV
        var toDelete = allMappings
            .Where(m => !csvSet.Contains((m.MicrosoftUsername, m.Department)))
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
                "/api/user-department-mappings/",
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
                $"/api/user-department-mappings/{mapping.Id}",
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

public class UserDeptDto
{
    public int Id { get; set; }
    public string MicrosoftUsername { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
}
```

> 注意：上面示例中的 URL 前缀 `/api` 以及字段名 `id` / `microsoft_username` / `department` 需要与你实际后端接口保持一致，请根据实际情况调整。

---

### Example `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "Backend": {
    "BaseUrl": "http://localhost:8089"
  },
  "Watcher": {
    "FolderPath": "C:\\CsvDropFolder",
    "IntervalSeconds": 60
  }
}
```

---

### How to Run as a Windows Service (Outline)

1. On a Windows machine with .NET SDK installed, create a Worker Service project and integrate the above code.
2. Publish the project:
   ```powershell
   dotnet publish -c Release -o C:\Services\CsvSyncService
   ```
3. Register it as a Windows Service (PowerShell as Administrator):
   ```powershell
   New-Service -Name "CsvSyncService" -BinaryPathName "C:\Services\CsvSyncService\CsvSyncService.exe" -StartupType Automatic
   ```
4. Start the service:
   ```powershell
   Start-Service CsvSyncService
   ```

You can then drop CSV files into the configured folder, and the service will periodically sync the User-Department Mapping table via your existing backend APIs.

